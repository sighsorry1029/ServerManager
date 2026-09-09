// Source-links actual gateway ingress and Tick; no Discord, network, Unity scene or game mutation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerManager;
using ServerManager.Commands;
using ServerManager.Discord;
using ServerManager.Events;

internal static class DiscordChatSmoke
{
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks, _id;
    internal static int GameThread;

    private static int Main()
    {
        GameThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            ActorAccountMetadata(); LiteralMainThread(); Validation(); MultiGuild(); AdminChannels(); NamesAndText(); RatesAndQueue(); Lifecycle(); Reload();
            System.Console.WriteLine("PASS: Discord chat ingress/Tick (" + _checks + " assertions).");
            return 0;
        }
        catch (Exception exception) { System.Console.Error.WriteLine(exception); return 1; }
    }

    private static void ActorAccountMetadata()
    {
        const string canonical = "steamworks:76561198775206205";
        var original = new ServerManagerActor("opaque-public-id", "Original Player 이름", "player");
        Check(original.PlayerAccountId == string.Empty &&
            new ServerManagerActor(canonical, canonical, "player").PlayerAccountId == string.Empty,
            "Actor construction never guesses account attribution from its public ID or name");
        ServerManagerActor tagged = original.WithPlayerAccount(canonical);
        Check(!ReferenceEquals(original, tagged) && original.PlayerAccountId == string.Empty &&
            tagged.PlayerAccountId == canonical, "Verified account metadata uses a separate clone and leaves the original untouched");
        Check(tagged.Id == original.Id && tagged.Name == original.Name && tagged.Kind == original.Kind,
            "Account tagging preserves public ID, name and kind exactly");
        foreach (string accepted in new[] { "steamworks:76561197960265729", canonical, "steamworks:76561202255233023" })
            Check(original.WithPlayerAccount(accepted).PlayerAccountId == accepted,
                "Canonical individual Steam accounts, including both valid endpoints, retain their exact principal");
        foreach (string? rejected in new string?[] { null, "", "76561198775206205", "Steam_76561198775206205",
            "Steamworks:76561198775206205", "steamworks:076561198775206205", canonical + " ", " " + canonical,
            canonical + "\n", "steamworks:7656119877520620", "steamworks:7656119877520620x",
            "steamworks:７6561198775206205", "steamworks:76561197960265728", "steamworks:76561202255233024",
            "steamworks:00000000000000000", "steamworks:99999999999999999", "discord:76561198775206205" })
        {
            ServerManagerActor invalid = tagged.WithPlayerAccount(rejected!);
            Check(!ReferenceEquals(invalid, tagged) && invalid.PlayerAccountId == string.Empty &&
                tagged.PlayerAccountId == canonical && invalid.Id == tagged.Id && invalid.Name == tagged.Name && invalid.Kind == tagged.Kind,
                "Malformed or noncanonical principal yields an untagged clone, without exceptions or mutation");
        }

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
        Check(typeof(ServerManagerActor).GetProperties(publicInstance).Select(property => property.Name)
            .OrderBy(name => name).SequenceEqual(new[] { "Id", "Kind", "Name" }) &&
            typeof(ServerManagerActor).GetMethod("WithPlayerAccount", publicInstance) == null &&
            typeof(ServerManagerActor).GetFields(publicInstance).Length == 0,
            "Verified account metadata adds no public actor property, field or method");
        JObject actorJson = JObject.FromObject(tagged);
        Check(actorJson.Properties().Select(property => property.Name).OrderBy(name => name)
            .SequenceEqual(new[] { "Id", "Kind", "Name" }) && JToken.DeepEquals(actorJson, JObject.FromObject(original)),
            "Default public JSON serialization is identical before and after account tagging");
        DateTime occurred = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var fields = new Dictionary<string, string> { ["message"] = "Original Player 이름: hello", ["text"] = "hello" };
        var before = new ServerManagerEvent("event", occurred, "server", ServerManagerEventKinds.ChatShout,
            ServerManagerEventReliability.Authoritative, original, original, fields);
        var after = new ServerManagerEvent("event", occurred, "server", ServerManagerEventKinds.ChatShout,
            ServerManagerEventReliability.Authoritative, tagged, tagged, fields);
        JObject eventJson = JObject.FromObject(after);
        Check(ServerManagerEvent.CurrentSchemaVersion == 1 && after.SchemaVersion == 1 &&
            eventJson.Properties().Select(property => property.Name).OrderBy(name => name).SequenceEqual(new[]
                { "Actor", "EventId", "Fields", "Kind", "OccurredAtUtc", "Reliability", "SchemaVersion", "ServerId", "Target" }) &&
            JToken.DeepEquals(JObject.FromObject(before), eventJson),
            "Public event schema, both participants and original fields serialize identically after internal tagging");
        Check(after.Actor.PlayerAccountId == canonical && after.Target.PlayerAccountId == canonical &&
            after.Fields["message"] == fields["message"] && after.Fields["text"] == fields["text"],
            "Adapters retain internal attribution without replacing original event messages or fields");
    }

    private static void LiteralMainThread()
    {
        using var fixture = new Fixture();
        Set(fixture.Commands, "_inFlight", 64);
        JObject message = Payload("sm ban PlayerName");
        message["attachments"] = new JArray(new JObject { ["url"] = "https://do-not-fetch.invalid/attachment" });
        Task.Run(() => fixture.Dispatch(message)).GetAwaiter().GetResult();
        Check(fixture.Queued == 1 && ServerManagerRuntime.Calls.Count == 0, "Worker ingress only queues bounded data");
        Set(fixture.Commands, "_inFlight", 0);
        Check(fixture.Http.Calls == 0 && ServerCommands.Calls == 0 && DiscordRconCapture.Calls == 0, "Chat has no HTTP, parser or console path");
        object pending = Peek(fixture.Commands, "_chatQueue");
        Check(pending.GetType().GetFields(Fields).All(field => field.FieldType == typeof(string) || field.FieldType == typeof(long)), "Queued chat retains only immutable bounded values, never JObject or attachments");
        double remaining = ((long)Get(pending, "Deadline") - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        Check(remaining > 9 && remaining <= 10, "Ordinary chat gets a ten-second queue lifetime");
        message["content"] = "mutated payload"; message["author"]!["username"] = "mutated author";
        fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Single().Text == "sm ban PlayerName" && ServerManagerRuntime.Calls[0].Name == "Name", "Payload mutation cannot change copied literal text or author");
        Check(fixture.Audits == 0 && ServerCommands.Calls == 0 && DiscordRconCapture.Calls == 0, "Ordinary text grants no management powers and creates no command audit");
        Check(fixture.Settings.CommandChannelIds.Count == 0 && fixture.Settings.AdminUserIds.Count == 0, "Chat works without management channels or admin users");
        fixture.Commands.HandleDispatchAsync("READY", JObject.Parse("{\"application\":{\"id\":\"777\"}}")).Wait();
        Check(fixture.Http.Calls == 0 && (int)Get(fixture.Commands, "_registering") == 0, "Chat-only READY skips unused slash registration HTTP");
        fixture.Dispatch(Payload("/rcon shutdown", "445")); fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Last().Text == "/rcon shutdown" && ServerCommands.Calls == 0, "Slash-looking text remains literal");
    }

    private static void Validation()
    {
        Action<JObject>[] invalid =
        {
            p => p.Remove("guild_id"), p => p["guild_id"] = "112", p => p["guild_id"] = 111,
            p => p["channel_id"] = "223", p => p["channel_id"] = 222, p => p["channel_id"] = "0222",
            p => { p["channel_id"] = "223"; p["parent_id"] = "222"; },
            p => p["author"] = new JArray(), p => p.Remove("author"), p => p["author"]!["id"] = 444,
            p => p["author"]!["id"] = "0", p => p["author"]!["id"] = "18446744073709551616",
            p => p["author"]!["id"] = " 444", p => p["author"]!["bot"] = true,
            p => p["author"]!["bot"] = "false", p => p["author"]!["system"] = true,
            p => p["system"] = true, p => p["webhook_id"] = "999", p => p["webhook_id"] = "",
            p => p.Remove("type"), p => p["type"] = 7, p => p["type"] = "0", p => p["type"] = 0.0,
            p => p["content"] = new JObject(), p => p["content"] = new JArray("text"), p => p["content"] = 42,
            p => p["content"] = "", p => p["content"] = " \n\t ", p => p["content"] = new string('x', 501),
            p => { p["content"] = ""; p["attachments"] = new JArray(new JObject { ["url"] = "https://invalid.invalid" }); },
            p => p["id"] = "1", p => p["id"] = "01", p => p["id"] = Snowflake(-31000),
            p => p["id"] = Snowflake(6000), p => p["id"] = 123
        };
        foreach (Action<JObject> mutate in invalid)
        {
            using var fixture = new Fixture(); JObject payload = Payload(); mutate(payload);
            fixture.Dispatch(payload); fixture.Commands.Tick();
            Check(fixture.Queued == 0 && ServerManagerRuntime.Calls.Count == 0, "Malformed, wrong-scope, automated, stale and unsupported messages fail closed");
            Check(fixture.Http.Calls == 0 && fixture.Audits == 0, "Rejected content creates no HTTP/command audit");
        }
        using (var fixture = new Fixture())
        {
            JObject reply = Payload(); reply["type"] = 19; reply["author"]!["bot"] = false;
            reply["webhook_id"] = JValue.CreateNull(); reply["id"] = Snowflake(3000);
            fixture.Dispatch(reply); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1, "Human reply and modest forward clock skew accepted");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.ChatChannelIds.Add("223"); JObject explicitThread = Payload(); explicitThread["channel_id"] = "223";
            fixture.Dispatch(explicitThread); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1, "An explicitly listed channel ID is in scope; parents never imply thread IDs");
        }
    }

    private static void MultiGuild()
    {
        using (var fixture = new Fixture())
        {
            fixture.Settings.GuildIds.Add("112"); fixture.Settings.ChatChannelIds.Add("223");
            fixture.Dispatch(Payload("first guild", "444", "111", "222"));
            fixture.Dispatch(Payload("second guild", "445", "112", "223"));
            Check(fixture.Queued == 2 && ServerManagerRuntime.Calls.Count == 0, "Both listed guilds queue chat for the same game thread");
            fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Select(call => call.Text).SequenceEqual(new[] { "first guild", "second guild" }),
                "Both guilds deliver to the single shared Valheim shout sink");
            fixture.Dispatch(Payload("unlisted guild", "446", "113", "222"));
            fixture.Dispatch(Payload("unlisted channel", "447", "112", "224"));
            fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 2 && fixture.Queued == 0, "A shared channel allowlist never permits an unlisted guild or channel");
            Check(fixture.Http.Calls == 0 && fixture.Audits == 0 && ServerCommands.Calls == 0 && DiscordRconCapture.Calls == 0,
                "Adding a guild grants no chat-to-command or extra network path");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.GuildIds.Add("112"); fixture.Settings.ChatChannelIds.Add("223");
            fixture.Dispatch(Payload("removed guild", "444", "112", "223"));
            fixture.Dispatch(Payload("retained guild", "445", "111", "222"));
            Check(fixture.Queued == 2, "Both guilds may have pending text before configuration revocation");
            fixture.Settings.GuildIds.Remove("112");
            fixture.Commands.Tick();
            Check(fixture.Queued == 0 && ServerManagerRuntime.Calls.Count == 1 &&
                ServerManagerRuntime.Calls[0].Text == "retained guild", "Removed-guild queued chat is dropped without cancelling the retained guild's text");
            fixture.Dispatch(Payload("late removed guild", "446", "112", "223")); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1 && fixture.Queued == 0, "Removed guild cannot admit new text even while its channel remains listed");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.GuildIds.Add("112"); fixture.Settings.ChatChannelIds.Add("223");
            fixture.Dispatch(Payload("first user burst", "444", "111", "222"));
            fixture.Dispatch(Payload("same user other guild", "444", "112", "223"));
            Check(fixture.Queued == 1 && History(fixture, "_chatSeen").Count == 2,
                "Changing guild does not reset the shared per-user chat rate");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.GuildIds.Add("112"); fixture.Settings.ChatChannelIds.Add("223");
            for (int index = 0; index < 11; ++index)
                fixture.Dispatch(Payload("shared burst", (900 + index).ToString(CultureInfo.InvariantCulture),
                    index % 2 == 0 ? "111" : "112", index % 2 == 0 ? "222" : "223"));
            Check(fixture.Queued == 10 && Count(fixture.Commands, "_chatGlobalRate") == 10,
                "All guilds share one ten-message-per-second chat admission budget");
        }
    }

    private static void AdminChannels()
    {
        using (var fixture = new Fixture())
        {
            fixture.Settings.ChatChannelIds.Clear(); fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            JObject admin = Payload("/rcon save", "444", channel: "333");
            admin["member"] = new JObject { ["nick"] = "Actual Operator" };
            Task.Run(() => fixture.Dispatch(admin)).GetAwaiter().GetResult();
            Check(fixture.Queued == 1 && ServerManagerRuntime.Calls.Count == 0,
                "Admin-channel plain text queues without requiring a public chat channel or worker game access");
            object pending = Peek(fixture.Commands, "_chatQueue");
            Check((string)Get(pending, "UserId") == "444" && (string)Get(pending, "UserName") == "Actual Operator",
                "Queued admin chat retains the original actor instead of replacing its identity with Admin");
            fixture.Commands.Tick();
            var call = ServerManagerRuntime.Calls.Single();
            Check(call.AdminChannel && call.Id == "444" && call.Name == "Actual Operator" && call.Text == "/rcon save",
                "Admin display classification and original audit identity reach the runtime as separate values");
            Check(ServerCommands.Calls == 0 && DiscordRconCapture.Calls == 0 && fixture.Http.Calls == 0 && fixture.Audits == 0,
                "Admin-channel text remains literal chat, never a management command or RCON request");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.CommandChannelIds.Add("222");
            fixture.Dispatch(Payload("private no admins", "444", channel: "333"));
            fixture.Dispatch(Payload("overlap no admins", "445"));
            fixture.Commands.Tick();
            Check(fixture.Queued == 0 && ServerManagerRuntime.Calls.Count == 0 && History(fixture, "_chatSeen").Count == 0,
                "No admin IDs means admin-only and overlapping channels fail closed at ingress");
            fixture.Settings.AdminUserIds.Add("444");
            fixture.Dispatch(Payload("registered admin", "444"));
            JObject spoof = Payload("not an admin", "445"); spoof["member"] = new JObject { ["nick"] = "Admin", ["roles"] = new JArray("444") };
            fixture.Dispatch(spoof); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1 && ServerManagerRuntime.Calls[0].AdminChannel,
                "Admin-channel precedence cannot be bypassed through public-channel overlap, names or role payloads");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            fixture.Dispatch(Payload("public operator", "444")); fixture.Commands.Tick();
            Check(!ServerManagerRuntime.Calls.Single().AdminChannel && ServerManagerRuntime.Calls[0].Name == "Name" &&
                ServerManagerRuntime.Calls[0].Id == "444", "An administrator in a public-only channel keeps the normal display name");
        }
        foreach (Action<Fixture> revoke in new Action<Fixture>[] {
            f => f.Settings.AdminUserIds.Clear(), f => f.Settings.GuildIds.Clear(), f => f.Settings.CommandChannelIds.Clear() })
        {
            using var fixture = new Fixture(); fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            fixture.Dispatch(Payload("queued before revocation", "444", channel: "333"));
            Check(fixture.Queued == 1, "Authorized admin-channel text waits for the game thread");
            revoke(fixture); fixture.Commands.Tick();
            Check(fixture.Queued == 0 && ServerManagerRuntime.Calls.Count == 0,
                "Queued admin chat rechecks user, guild and channel authorization before broadcasting");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("222"); fixture.Settings.AdminUserIds.Add("444");
            fixture.Dispatch(Payload("overlap queued admin")); fixture.Settings.AdminUserIds.Clear(); fixture.Commands.Tick();
            Check(fixture.Queued == 0 && ServerManagerRuntime.Calls.Count == 0,
                "Revoked queued administrator cannot fall back to an overlapping public channel");
        }
        using (var fixture = new Fixture())
        {
            fixture.Dispatch(Payload("public before restriction")); fixture.Settings.CommandChannelIds.Add("222"); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 0, "A newly restricted channel rechecks already queued public text");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.AdminUserIds.Add("444"); fixture.Dispatch(Payload("public becomes admin"));
            fixture.Settings.CommandChannelIds.Add("222"); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Single().AdminChannel, "Display mode follows current channel authorization at Tick");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            fixture.Dispatch(Payload("admin becomes public", channel: "333"));
            fixture.Settings.CommandChannelIds.Clear(); fixture.Settings.ChatChannelIds.Add("333"); fixture.Commands.Tick();
            Check(!ServerManagerRuntime.Calls.Single().AdminChannel,
                "Removing the admin designation does not preserve a stale privileged display mode on queued public text");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.UnionWith(new[] { "444", "445" });
            fixture.Dispatch(Payload("first admin", "444", channel: "333")); fixture.Dispatch(Payload("revoked next", "445", channel: "333"));
            ServerManagerRuntime.AfterBroadcast = () => fixture.Settings.AdminUserIds.Clear();
            fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1 && fixture.Queued == 0,
                "Authorization is rechecked for each message, including revocation during the same Tick");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            JObject first = Payload("admin first", channel: "333"); fixture.Dispatch(first);
            JObject duplicate = (JObject)first.DeepClone(); duplicate["channel_id"] = "222"; fixture.Dispatch(duplicate);
            fixture.Dispatch(Payload("same user public"));
            Check(fixture.Queued == 1 && History(fixture, "_chatSeen").Count == 2,
                "Admin and public channels share duplicate history and the same per-user chat cooldown");
            Check(History(fixture, "_seen").Count == 0 && History(fixture, "_userRate").Count == 0,
                "Admin plain chat still uses no command rate or interaction budget");
        }
        using (var fixture = new Fixture())
        {
            fixture.Settings.CommandChannelIds.Add("333");
            for (int index = 0; index < 11; index++)
            {
                string user = (900 + index).ToString(CultureInfo.InvariantCulture); fixture.Settings.AdminUserIds.Add(user);
                fixture.Dispatch(Payload("shared global budget", user, channel: index % 2 == 0 ? "333" : "222"));
            }
            Check(fixture.Queued == 10 && Count(fixture.Commands, "_chatGlobalRate") == 10,
                "Admin and public channels share one global ten-message chat budget");
        }
    }

    private static void NamesAndText()
    {
        using var fixture = new Fixture();
        JObject payload = Payload("A\r\nB\tC\u0000\u202e <b>tag</b> \ud83d\ude42\ud800\u2028D\u2029E");
        payload["member"] = new JObject { ["nick"] = "\u202e<Admin>\nNick" };
        payload["author"]!["global_name"] = "Global";
        fixture.Dispatch(payload); fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls[0].Name == "＜Admin＞Nick", "Nick has precedence; controls, bidi and rich text are neutralized");
        Check(ServerManagerRuntime.Calls[0].Text == "A  B C ＜b＞tag＜/b＞ \ud83d\ude42 D E", "Text is bounded, single-line, markup-neutral and valid UTF-16");
        JObject global = Payload("hello", "445"); global["member"] = new JObject { ["nick"] = "\n\u202e" };
        global["author"]!["global_name"] = "Global"; fixture.Dispatch(global); fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Last().Name == "Global", "Empty sanitized nickname falls back to global name");
        JObject fallback = Payload("hello", "446"); fallback["author"]!["username"] = JValue.CreateNull();
        fixture.Dispatch(fallback); fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Last().Name == "Discord user 446", "Missing display fields use bounded stable user identity");
        JObject capped = Payload(new string('x', 500), "447"); capped["member"] = new JObject { ["nick"] = new string('n', 79) + "\ud83d\ude42" };
        fixture.Dispatch(capped); fixture.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Last().Name.Length == 79 && ServerManagerRuntime.Calls.Last().Text.Length == 500, "Bounded display name never splits a surrogate pair; 500-char text accepted");
        Check(ServerManagerRuntime.Calls.All(call => !call.Text.Any(char.IsControl) && !call.Name.Any(char.IsControl)), "Sink receives no control characters");
    }

    private static void RatesAndQueue()
    {
        using (var fixture = new Fixture())
        {
            JObject message = Payload(); fixture.Dispatch(message); fixture.Dispatch(message);
            fixture.Dispatch(Payload());
            Check(fixture.Queued == 1 && History(fixture, "_chatSeen").Count == 2, "Duplicate ID and same-user two-second burst are dropped");
            History(fixture, "_chatUserRate")["444"] = Stopwatch.GetTimestamp() - 3L * Stopwatch.Frequency;
            fixture.Dispatch(Payload()); Check(fixture.Queued == 2, "Per-user rate admits after its two-second window");
            Check(History(fixture, "_seen").Count == 0 && History(fixture, "_userRate").Count == 0 && Count(fixture.Commands, "_responseRate") == 0, "Chat budgets never consume slash command history/ACK capacity");
        }
        using (var fixture = new Fixture())
        {
            for (int index = 0; index < 11; index++) fixture.Dispatch(Payload("burst", (500 + index).ToString()));
            Check(fixture.Queued == 10 && Count(fixture.Commands, "_chatGlobalRate") == 10, "Global chat ingress limited to ten per second");
            fixture.Commands.Tick(); Check(ServerManagerRuntime.Calls.Count == 4 && fixture.Queued == 6, "Tick sends at most four ordinary messages");
            fixture.Commands.Tick(); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 10 && fixture.Queued == 0, "Accepted chat drains without command execution");
        }
        using (var fixture = new Fixture())
        {
            for (int index = 0; index < 33; index++)
            { Clear(fixture.Commands, "_chatGlobalRate"); fixture.Dispatch(Payload("capacity", (600 + index).ToString())); }
            Check(fixture.Queued == 32, "Chat queue is hard bounded at 32 entries");
            Set(Peek(fixture.Commands, "_chatQueue"), "Deadline", Stopwatch.GetTimestamp() - 1);
            fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 3 && fixture.Queued == 28, "Expired pending text is dropped without retry; four items maximum per frame");
        }
        using (var fixture = new Fixture())
        {
            long now = Stopwatch.GetTimestamp(); var seen = History(fixture, "_chatSeen");
            for (int index = 0; index < 4096; index++) seen[index.ToString()] = now;
            fixture.Dispatch(Payload()); Check(seen.Count == 4096 && fixture.Queued == 0, "4096-ID hard cap fails closed");
            foreach (string key in seen.Keys.ToArray()) seen[key] = now - 901L * Stopwatch.Frequency;
            fixture.Dispatch(Payload()); Check(seen.Count == 1 && fixture.Queued == 1, "15-minute replay history is pruned");
        }
        using (var fixture = new Fixture())
        {
            var users = History(fixture, "_chatUserRate");
            for (int index = 0; index < 4096; index++) users[index.ToString()] = Stopwatch.GetTimestamp();
            fixture.Dispatch(Payload()); Check(users.Count == 4096 && fixture.Queued == 0, "Per-user history cannot grow without bound");
        }
        using (var fixture = new Fixture())
        {
            for (int index = 0; index < 10; index++) fixture.Dispatch(Payload("chat", (700 + index).ToString()));
            fixture.Settings.CommandChannelIds.Add("333"); fixture.Settings.AdminUserIds.Add("444");
            Set(fixture.Commands, "_applicationId", "777"); ((Dictionary<string, string>)Get(fixture.Commands, "_commandIds"))["111:status"] = "555";
            fixture.Commands.HandleDispatchAsync("INTERACTION_CREATE", JObject.Parse("{\"id\":\"999\",\"application_id\":\"777\",\"type\":2,\"token\":\"test\",\"guild_id\":\"111\",\"channel_id\":\"333\",\"member\":{\"user\":{\"id\":\"444\"},\"roles\":[]},\"data\":{\"id\":\"555\",\"type\":1,\"name\":\"status\",\"options\":[]}}")).Wait();
            Wait(() => Count(fixture.Commands, "_queue") == 1);
            fixture.Commands.Tick(); Wait(() => (int)Get(fixture.Commands, "_inFlight") == 0);
            Check(ServerCommands.Calls == 1 && ServerManagerRuntime.Calls.Count == 4, "Full chat rate budget does not block ordinary authorized slash commands");
        }
    }

    private static void Lifecycle()
    {
        using (var fixture = new Fixture(ready: false))
        {
            JObject payload = Payload(); fixture.Dispatch(payload);
            Check(fixture.Queued == 0, "Ingress before first ready Tick cannot queue game work");
            fixture.Commands.Tick(); fixture.Dispatch(payload); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 0, "Message dropped before readiness cannot replay after readiness");
        }
        Action<Fixture>[] revoke =
        {
            f => f.Settings.ChatChannelIds.Clear(), f => f.Settings.GuildIds.Clear(), f => f.Settings.BotEnabled = false,
            f => f.Stop.Cancel(), f => f.Commands.Dispose(), f => ZNet.World = new object(),
            f => ZNet.instance = new ZNet(), f => ZNet.instance = null, f => ZNet.instance!.Server = false,
            f => ServerEventRuntime.Initialized = false, f => ServerEventRuntime.Started = false,
            f => ServerEventRuntime.Ready = false, f => ServerEventRuntime.Server = false,
            f => ServerEventRuntime.Shutdown = true
        };
        foreach (Action<Fixture> change in revoke)
        {
            using var fixture = new Fixture(); fixture.Dispatch(Payload()); change(fixture); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 0 && fixture.Queued == 0, "Current cancellation/config/network/world/readiness rechecked before delivery");
        }
        using (var fixture = new Fixture())
        {
            for (int index = 0; index < 4; index++) fixture.Dispatch(Payload("world switch", (800 + index).ToString()));
            ServerManagerRuntime.AfterBroadcast = () => ZNet.World = new object();
            fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1 && fixture.Queued == 0, "World rollover during a Tick drops remaining old-world text");
            fixture.Dispatch(Payload("late old-world", "900")); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Calls.Count == 1 && fixture.Queued == 0, "Retired adapter cannot admit late old-world messages");
        }
        using (var fixture = new Fixture())
        {
            fixture.Dispatch(Payload()); fixture.Dispatch(Payload("more", "445")); ServerManagerRuntime.Accept = false;
            fixture.Commands.Tick(); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Attempts == 1 && fixture.Queued == 0, "Offline/failed delivery drops backlog without retry");
        }
        using (var fixture = new Fixture())
        {
            fixture.Dispatch(Payload("PRIVATE_BODY")); fixture.Dispatch(Payload("more", "445"));
            ServerManagerRuntime.Throw = true; fixture.Commands.Tick(); fixture.Commands.Tick();
            Check(ServerManagerRuntime.Attempts == 1 && fixture.Queued == 0 && fixture.Logs.Count == 1, "Sink exceptions do not escape the game Tick or retry work");
            Check(!fixture.Logs[0].Contains("PRIVATE_BODY") && !fixture.Logs[0].Contains("PRIVATE_EXCEPTION"), "Failure logs include only exception type, not message content or exception secrets");
        }
    }

    private static void Reload()
    {
        using var old = new Fixture(); JObject message = Payload(); old.Dispatch(message);
        old.Commands.Dispose();
        using var replacement = new Fixture(ready: false, preserveWorld: true); replacement.Commands.InheritRecentState(old.Commands);
        Check(replacement.Queued == 0 && old.Queued == 0, "Replacement and disposal never inherit pending text");
        Check(!(bool)Get(replacement.Commands, "_bound") && !(bool)Get(replacement.Commands, "_ready"), "History inheritance does not import old lifecycle readiness");
        foreach (string name in new[] { "_chatSeen", "_chatUserRate", "_chatGlobalRate" })
            Check(Count(replacement.Commands, name) == Count(old.Commands, name) && !ReferenceEquals(Get(replacement.Commands, name), Get(old.Commands, name)), "Reload copies independent bounded chat history: " + name);
        replacement.Commands.Tick(); replacement.Dispatch(message); replacement.Dispatch(Payload()); replacement.Commands.Tick();
        Check(ServerManagerRuntime.Calls.Count == 0, "Copied IDs and user rates survive same-world replacement/off-on");
        replacement.Commands.InheritRecentState(old.Commands);
        Check(Count(replacement.Commands, "_chatGlobalRate") == 1, "Two-phase recopy does not double the global budget");
        Check((string)Get(replacement.Commands, "_applicationId") == "" && Count(replacement.Commands, "_queue") == 0, "History transfer carries no registration, command work or grants");
    }

    private static string Snowflake(int offset = 0) =>
        (((ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offset - 1420070400000L) << 22) | (uint)Interlocked.Increment(ref _id)).ToString(CultureInfo.InvariantCulture);
    private static JObject Payload(string text = "hello", string user = "444", string guild = "111", string channel = "222") => new()
    {
        ["id"] = Snowflake(), ["guild_id"] = guild, ["channel_id"] = channel, ["type"] = 0,
        ["content"] = text, ["author"] = new JObject { ["id"] = user, ["username"] = "Name" }
    };
    private static object Get(object target, string name) => target.GetType().GetField(name, Fields)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
    private static int Count(object target, string name) => (int)Get(target, name).GetType().GetProperty("Count")!.GetValue(Get(target, name))!;
    private static void Clear(object target, string name) => Get(target, name).GetType().GetMethod("Clear")!.Invoke(Get(target, name), null);
    private static object Peek(object target, string name) => Get(target, name).GetType().GetMethod("Peek")!.Invoke(Get(target, name), null)!;
    private static Dictionary<string, long> History(Fixture fixture, string name) => (Dictionary<string, long>)Get(fixture.Commands, name);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); ++_checks; }
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > 5000) throw new Exception("Asynchronous test work timed out."); Thread.Sleep(5); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly DiscordSettings Settings = new();
        internal readonly DiscordHttp Http = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly DiscordCommands Commands;
        internal readonly List<string> Logs = new();
        internal int Audits;
        internal int Queued => Count(Commands, "_chatQueue");
        internal Fixture(bool ready = true, bool preserveWorld = false)
        {
            if (!preserveWorld) { ZNet.instance = new ZNet(); ZNet.World = new object(); }
            ServerEventRuntime.Reset(); ServerManagerRuntime.Reset();
            ServerCommands.Calls = 0; DiscordRconCapture.Calls = 0;
            Settings.BotEnabled = true; Settings.GuildIds.Add("111"); Settings.ChatChannelIds.Add("222");
            Commands = new DiscordCommands(Settings, Http, Logs.Add, _ => Interlocked.Increment(ref Audits), Stop.Token);
            if (ready) Commands.Tick();
        }
        internal void Dispatch(JObject payload)
        {
            Task dispatch = Commands.HandleDispatchAsync("MESSAGE_CREATE", payload);
            if (!dispatch.IsCompleted) throw new Exception("Gateway ingress must finish without waiting for game/HTTP.");
            dispatch.GetAwaiter().GetResult();
        }
        public void Dispose() { Commands.Dispose(); Stop.Dispose(); }
    }
}

internal sealed class ZNet
{
    internal static ZNet? instance;
    internal static object? World;
    internal bool Server = true;
    internal bool IsServer()
    {
        if (Thread.CurrentThread.ManagedThreadId != DiscordChatSmoke.GameThread) throw new Exception("Worker accessed game state.");
        return Server;
    }
}

namespace ServerManager
{
    internal static class IntegrityCanonical
    {
        internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException || exception is StackOverflowException || exception is AccessViolationException;
    }

    internal static class ServerManagerRuntime
    {
        internal sealed class Call
        {
            internal Call(string id, string name, string text, bool adminChannel) { Id = id; Name = name; Text = text; AdminChannel = adminChannel; }
            internal readonly string Id, Name, Text;
            internal readonly bool AdminChannel;
        }
        internal static readonly List<Call> Calls = new();
        internal static bool Accept, Throw;
        internal static int Attempts;
        internal static Action? AfterBroadcast;
        internal static void Reset() { Calls.Clear(); Accept = true; Throw = false; Attempts = 0; AfterBroadcast = null; }
        internal static bool TryBroadcastDiscordShout(string userId, string userName, string message, bool adminChannel)
        {
            if (Thread.CurrentThread.ManagedThreadId != DiscordChatSmoke.GameThread) throw new Exception("Worker tried to broadcast.");
            ++Attempts; if (Throw) throw new InvalidOperationException("PRIVATE_EXCEPTION"); if (!Accept) return false;
            Calls.Add(new Call(userId, userName, message, adminChannel)); AfterBroadcast?.Invoke(); return true;
        }
    }
    internal static class ServerManagerPlugin { internal const string ModVersion = "test"; internal static readonly TestLog Log = new(); }
    internal sealed class TestLog { public void LogWarning(string value) { } public void LogDebug(string value) { } }
}

namespace ServerManager.Events
{
    internal enum ServerEventCommandKind { Save, Announce, Kick, Ban, Unban }
    internal static class ServerEventRuntime
    {
        internal static bool Initialized, Server, Started, Ready, Shutdown;
        internal static void Reset() { Initialized = Server = Started = Ready = true; Shutdown = false; }
        internal static ServerManagerStatusSnapshot GetStatusSnapshot()
        {
            if (Thread.CurrentThread.ManagedThreadId != DiscordChatSmoke.GameThread) throw new Exception("Worker accessed status.");
            return new(Initialized, Server, true, Started, Ready, Shutdown, "Test", "World", Array.Empty<ServerManagerPlayerSnapshot>(), null!);
        }
        internal static Task<ServerManagerCommandResult> EnqueueCommand(ServerEventCommandKind kind, string first, string second) => throw new Exception("No direct integration management calls allowed.");
    }
}

namespace ServerManager.Commands
{
    internal sealed class CommandCaller
    {
        internal CommandCaller(string source, string id, string name, Func<bool> authorized, CancellationToken token) { }
    }
    internal static class ServerCommands
    {
        internal static int Calls;
        // The real catalog equality is checked against the bundled DLL by
        // DiscordCommandsSmoke; this fixture isolates literal chat delivery.
        internal static IReadOnlyList<string> FlatCommandNames { get; } = new[]
        {
            "status", "players", "announce", "chat", "adminlist", "adminadd", "adminremove",
            "accesslist", "accessadd", "accessremove", "banlist", "keylist", "keyadd", "keyremove",
            "eventstart", "eventstop", "characterlist", "characterinfo", "characterbackups", "characterrestore", "giveitem", "teleport",
            "skillget", "skillset", "heal", "damage", "modsstatus", "modsreload",
            "discordstatus", "discordtest", "cronstatus", "cronack", "help"
        };
        internal static bool IsRconManagedLine(string line) => throw new Exception("Ordinary chat must never inspect a console command.");
        internal static bool IsBackupId(string value) => throw new Exception("Ordinary chat must never validate a command backup ID.");
        internal static bool IsItemDataId(string value) => throw new Exception("Ordinary chat must never validate a command item-data ID.");
        internal static Task<ServerManagerCommandResult> ExecuteAsync(string line, CommandCaller caller)
        { ++Calls; return Task.FromResult(new ServerManagerCommandResult(true, "ok", "test", "", new Dictionary<string, string>())); }
    }
}

namespace ServerManager.Discord
{
    internal sealed class DiscordHttp
    {
        internal int Calls;
        internal Task<JToken?> SendAsync(HttpMethod method, string uri, JObject? body, bool bot, CancellationToken token, bool retry = true)
        { Interlocked.Increment(ref Calls); token.ThrowIfCancellationRequested(); return Task.FromResult<JToken?>(new JObject()); }
    }
    internal sealed class DiscordHttpException : Exception { internal int StatusCode { get; } }
    internal sealed class DiscordRconCapture : IDisposable
    {
        internal static int Calls;
        internal DiscordRconCapture(Action<string> log) { }
        internal bool IsAvailable => true;
        internal DiscordCommands.Result Execute(string line, int maximum) { ++Calls; return DiscordCommands.Result.Ok("test"); }
        public void Dispose() { }
    }
}
