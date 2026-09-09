// The complete production formatter and overlay are source-linked. DTO/Unity
// stubs are inert; no game, disk saves, RPC, HTTP or actual GUI can run here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using ServerManager.Events;
using YamlDotNet.Serialization;

internal static class EventMessageSmoke
{
    private static int _checks;
    private static readonly Dictionary<string, int> WebhookLabels = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { "sm_event_server_ready", 0 }, { "sm_event_server_shutdown", 0 }, { "sm_event_world_saved", 0 },
        { "sm_event_player_joined", 1 }, { "sm_event_player_first_join", 1 }, { "sm_event_player_left", 1 },
        { "sm_event_raid_started", 0 }, { "sm_event_raid_ended", 0 }, { "sm_event_announcement", 0 }
    };

    private static int Main(string[] args)
    {
        try
        {
            ServerManager.PlayerLocalizer.Load(args[0], args[1]);
            VariantsAndProjection();
            FallbackAndNames();
            LocalizationTokens();
            DisplayPackets();
            OverlayLabels();
            OverlayToggle(args[2]);
            Console.WriteLine("PASS: shared event phrasing and overlay (" + _checks + " assertions; no game or network).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void VariantsAndProjection()
    {
        foreach (string bucket in new[] { "creature", "enemyhit", "boss", "poisoned", "burning", "drowning", "fall", "tree",
            "smoke", "freezing", "environment", "UNRECOGNIZED_SECRET", "pvp", "playerhit", "combat.pvp_kill", "boss.killed" })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < 180; ++index)
            {
                var value = Make(bucket, "sample-" + index);
                string before = Fingerprint(value);
                string named = Format(value, "NamedPlayer", "NamedOther");
                string alias = Format(value, "Guest 1", "Guest 2");
                Check(named == Format(value, "NamedPlayer", "NamedOther"), "One event deterministically repeats the same wording.");
                Check(named.Replace("NamedPlayer", "{player}").Replace("NamedOther", "{other}") ==
                    alias.Replace("Guest 1", "{player}").Replace("Guest 2", "{other}"),
                    "Named and anonymous destinations keep the same variant, changing only supplied labels.");
                Check(named.Contains("NamedPlayer") && !named.Contains("SECRET") && !alias.Contains("SECRET"),
                    "Format uses projected names, not raw actor, field, cause or diagnostic identity.");
                Check(!named.StartsWith("[") && named.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0 && named.Length <= 256,
                    "Templates are concise single-line content without event-type tags.");
                value.Reliability = "DIFFERENT_PRIVATE_RELIABILITY";
                value.Fields["message"] = "CHANGED_PRIVATE_AUDIT_FACT";
                value.Fields["damage_tags"] = "frost,poison,PRIVATE";
                Check(named == Format(value, "NamedPlayer", "NamedOther"),
                    "Route/evidence/audit data does not select a different phrase or override its cause bucket.");
                value.Reliability = "client_reported";
                value.Fields["message"] = "SECRET_AUDIT_FACT";
                value.Fields["damage_tags"] = "SECRET_DAMAGE_TAGS";
                Check(before == Fingerprint(value), "Formatting does not mutate the event, actor, target, reliability or audit fields.");
                seen.Add(named);
            }
            Check(seen.Count >= 3, "Every supported cause/fallback/PvP/boss-victory bucket offers at least three actual variants: " + bucket);
        }
        for (int index = 0; index < 30; ++index)
        {
            string id = "alias-" + index;
            Check(Format(Make("creature", id), "P", "O") == Format(Make("enemyhit", id), "P", "O"), "Creature aliases share one template pool.");
            Check(Format(Make("pvp", id), "P", "O") == Format(Make("playerhit", id), "P", "O"), "PvP cause aliases share one template pool.");
            Check(Format(Make("environment", id), "P", "O") == Format(Make("UNKNOWN_SECRET", id), "P", "O"), "Unknown causes stay generic instead of becoming untrusted prose.");
        }
        var unknownKillerVariants = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? unavailableKiller in new string?[] { null, "", "Unknown", "\r\n\t" })
        for (int index = 0; index < 30; ++index)
        {
            var value = Make("boss.killed", "unknown-killer-" + index);
            value.Actor = unavailableKiller == null ? null : new ServerManagerActor("", unavailableKiller, "player");
            value.Fields["player"] = unavailableKiller ?? "";
            value.Fields["player_name"] = "SECRET_REPORTER_NOT_KILLER";
            string text = Format(value, "WronglyNamedKiller", "Eikthyr");
            Check(text == Format(value, "Guest 1", "Eikthyr") && !text.Contains("WronglyNamedKiller") && !text.Contains("SECRET") &&
                text.IndexOf("blow", StringComparison.OrdinalIgnoreCase) < 0,
                "An unknown boss killer uses a victory-only variant, never a fabricated killer or reporter attribution.");
            unknownKillerVariants.Add(text);
        }
        Check(unknownKillerVariants.Count >= 3, "Unknown-killer boss victories also retain at least three identity-independent variants.");
    }

    private static void FallbackAndNames()
    {
        foreach (string kind in new[] { "chat.shout", "player.login", "server.announcement", "server.ready", "security.response", "" })
        {
            ServerManagerEvent value = Make("environment", "unsupported"); value.Kind = kind;
            Check(!EventMessageText.TryFormat(value, "P", "O", "English", out string message) && message == "",
                "The shared helper does not change unrelated event wording: " + kind);
        }
        foreach (string bucket in new[] { "creature", "boss", "smoke", "freezing", "combat.pvp_kill", "boss.killed" })
        {
            var value = Make(bucket, "empty-labels");
            string text = Format(value, "", "");
            Check(text.Length > 0 && !text.Contains("SECRET"), "Empty projected labels use safe fallbacks without recovering private event names.");
        }
        var death = Make("creature", "names");
        death.Actor = new ServerManagerActor("a", "ActorName", "player");
        death.Target = new ServerManagerActor("t", "TargetName", "cause");
        death.Fields["player_name"] = "FieldPlayer"; death.Fields["attacker"] = "FieldAttacker";
        Check(EventMessageText.PlayerName(death) == "ActorName" && EventMessageText.OtherName(death) == "TargetName",
            "Named projection prefers the producer's actor and target roles.");
        death.Actor = null; death.Target = null;
        Check(EventMessageText.PlayerName(death) == "FieldPlayer" && EventMessageText.OtherName(death) == "FieldAttacker",
            "Named death projection retains the documented field fallbacks.");
        var boss = Make("boss.killed", "boss-names"); boss.Actor = null; boss.Target = null;
        boss.Fields["player"] = "FinalKiller"; boss.Fields["player_name"] = "ReporterMustNotBecomeKiller"; boss.Fields["boss"] = "Eikthyr";
        Check(EventMessageText.PlayerName(boss) == "FinalKiller" && EventMessageText.OtherName(boss) == "Eikthyr",
            "Boss victory falls back to the killer, not the reporting player's name.");
        foreach (string cause in new[] { "smoke", "freezing", "environment", "Unknown" })
        {
            death.Fields["cause"] = cause; death.Target = new ServerManagerActor("", cause, "cause"); death.Fields["attacker"] = cause;
            Check(EventMessageText.OtherName(death) == "", "A cause token is not displayed as an enemy name; its empty value lets rendering localize a fixed fallback.");
        }
        death.Actor = new ServerManagerActor("id", "Name\r\n\t" + new string('a', 200), "player");
        string bounded = EventMessageText.PlayerName(death);
        Check(bounded.Length <= 80 && !bounded.Any(char.IsControl), "Name projection remains bounded and single-line.");
    }

    private static void LocalizationTokens()
    {
        var english = ServerManager.PlayerLocalizer.Table("English");
        var korean = ServerManager.PlayerLocalizer.Table("Korean");
        string[] eventKeys = english.Keys.Where(key => key.StartsWith("sm_event_", StringComparison.Ordinal)).OrderBy(key => key).ToArray();
        Check(eventKeys.Length == 52 && eventKeys.SequenceEqual(korean.Keys.Where(key => key.StartsWith("sm_event_", StringComparison.Ordinal)).OrderBy(key => key)),
            "Both real translation resources contain 39 story variants, four auxiliary labels and nine webhook-only labels.");
        string[] kinds = { "player.death", "combat.pvp_kill", "boss.killed" };
        int validStories = 0, auxiliaryLabels = 0, webhookLabels = 0;
        foreach (string key in eventKeys)
        {
            string[] englishSlots = Regex.Matches(english[key], @"\{([0-9]+)\}").Cast<Match>().Select(match => match.Groups[1].Value).Distinct().OrderBy(value => value).ToArray();
            string[] koreanSlots = Regex.Matches(korean[key], @"\{([0-9]+)\}").Cast<Match>().Select(match => match.Groups[1].Value).Distinct().OrderBy(value => value).ToArray();
            Check(englishSlots.SequenceEqual(koreanSlots), "English/Korean argument identity and arity agree: " + key);
            int count = englishSlots.Length == 0 ? 0 : englishSlots.Select(int.Parse).Max() + 1;
            string[] arguments = Enumerable.Range(0, count).Select(index => "NAME" + index).ToArray();
            bool isStory = kinds.Any(kind => EventMessageText.IsValidMessage(kind, key, count));
            if (isStory)
            {
                ++validStories;
                string en = EventMessageText.RenderForLanguage(key, arguments, 0, "English");
                string ko = EventMessageText.RenderForLanguage(key, arguments, 0, "Korean");
                Check(en == ServerManager.PlayerLocalizer.TextForLanguage("English", key, arguments) &&
                    ko == ServerManager.PlayerLocalizer.TextForLanguage("Korean", key, arguments) && en != ko && ko.Any(IsHangul),
                    "Every story renders the actual Korean resource without falling back to its English sentence.");
                Check(!en.StartsWith("sm_event_", StringComparison.Ordinal) && !ko.StartsWith("sm_event_", StringComparison.Ordinal) &&
                    arguments.All(argument => en.Contains(argument) && ko.Contains(argument)), "Story rendering resolves only the template while preserving each literal name.");
                foreach (string kind in kinds)
                {
                    bool expectedKind = key.StartsWith("sm_event_boss_victory", StringComparison.Ordinal) ? kind == "boss.killed" :
                        key.StartsWith("sm_event_pvp_", StringComparison.Ordinal) ? kind == "player.death" || kind == "combat.pvp_kill" : kind == "player.death";
                    Check(EventMessageText.IsValidMessage(kind, key, count) == expectedKind &&
                        !EventMessageText.IsValidMessage(kind, key, count + 1) && !EventMessageText.IsValidMessage(kind, key, count - 1),
                        "Only the exact story kind/key/arity tuple is admitted.");
                }
            }
            else
            {
                if (WebhookLabels.TryGetValue(key, out int expectedCount))
                {
                    ++webhookLabels;
                    Check(count == expectedCount, "Webhook headings have only their documented player-name argument: " + key);
                    string en = ServerManager.PlayerLocalizer.TextForLanguage("English", key, arguments);
                    string ko = ServerManager.PlayerLocalizer.TextForLanguage("Korean", key, arguments);
                    Check(en != ko && ko.Any(IsHangul) && arguments.All(argument => en.Contains(argument) && ko.Contains(argument)) &&
                        !Regex.IsMatch(en + ko, @"\{[0-9]+\}"), "Webhook-only labels render actual English/Korean resources without losing literal names.");
                    Check(!EventMessageText.IsValidLabels(new[] { key }, 1), "Webhook headings cannot masquerade as masked combat role labels.");
                }
                else
                {
                    ++auxiliaryLabels;
                    Check(count == 0 && new[] { "sm_event_unknown_player", "sm_event_unknown_creature", "sm_event_unknown_boss", "sm_event_client_report" }.Contains(key),
                        "Auxiliary event labels are zero-argument resources, never independent display messages.");
                }
                Check(korean[key].Any(IsHangul), "Every fixed event label is also translated to Korean.");
            }
        }
        Check(validStories == 39 && auxiliaryLabels == 4 && webhookLabels == 9,
            "Adding nine webhook labels leaves the display allowlist at exactly 39 stories and the auxiliary label set at four.");
        foreach (string key in new[] { "sm_event_death_smoke_0", "sm_event_death_smoke_4", "sm_event_death_smoke_01",
            "sm_event_death_smoke_1_EXTRA", "sm_event_death_SMOKE_1", "sm_event_unknown_player", "sm_event_client_report",
            "sm_connection_failed", "sm_menu_guide_title", "", "sm_event_untrusted_1" }.Concat(WebhookLabels.Keys))
            foreach (string kind in kinds.Concat(new[] { "server.announcement", "chat.shout" }))
                for (int arity = 0; arity <= 3; ++arity)
                    Check(!EventMessageText.IsValidMessage(kind, key, arity), "Unknown, malformed, label-only and rejection/menu keys cannot become event display instructions.");

        var creature = Make("creature", "masked-labels");
        Check(EventMessageText.TryCreate(creature, "", "", out string fallbackKey, out string[] fallbackArgs, out byte fallbackMask) &&
            fallbackArgs.SequenceEqual(new[] { "sm_event_unknown_player", "sm_event_unknown_creature" }) && fallbackMask == 3,
            "Absent creature roles are represented explicitly as two masked fixed labels, not pre-rendered English.");
        string[] before = (string[])fallbackArgs.Clone();
        string localized = EventMessageText.RenderForLanguage(fallbackKey, fallbackArgs, fallbackMask, "Korean");
        Check(localized.Contains(korean["sm_event_unknown_player"]) && localized.Contains(korean["sm_event_unknown_creature"]) &&
            !localized.Contains("sm_event_") && before.SequenceEqual(fallbackArgs), "Masked fallback labels localize without mutating caller-owned arguments.");
        ServerManager.PlayerLocalizer.LocalLanguage = "Korean";
        Check(EventMessageText.Render(fallbackKey, fallbackArgs, fallbackMask) == localized, "Game-thread render uses its current local language.");
        ServerManager.PlayerLocalizer.LocalLanguage = "English";
        Check(EventMessageText.Render(fallbackKey, fallbackArgs, fallbackMask) != localized, "The same token packet can render differently for another client's language.");

        string literal = "sm_event_unknown_player";
        Check(EventMessageText.TryCreate(creature, literal, "sm_event_unknown_creature", out string literalKey, out string[] literalArgs, out byte literalMask) &&
            literalMask == 0 && literalArgs[0] == literal && literalArgs[1] == "sm_event_unknown_creature",
            "Player/creature names identical to known translation tokens remain unmasked literal strings.");
        string literalResult = EventMessageText.RenderForLanguage(literalKey, literalArgs, literalMask, "Korean");
        Check(literalResult.Contains(literal) && literalResult.Contains("sm_event_unknown_creature"), "Known token-shaped names are never recursively translated.");
        foreach (string rawName in new[] { "$sm_connection_failed", "{0}{1}", "<b>literal</b>", "한글😀" })
        {
            Check(EventMessageText.TryCreate(creature, rawName, "enemy", out string key, out string[] arguments, out byte mask) && mask == 0,
                "Formatting-like names remain literal, unmasked arguments.");
            Check(EventMessageText.RenderForLanguage(key, arguments, mask, "Korean").Contains(rawName), "Arguments are substituted once, never parsed as templates or markup.");
        }
        var unknownBoss = Make("boss.killed", "unknown-boss"); unknownBoss.Actor = null;
        Check(EventMessageText.TryCreate(unknownBoss, "IGNORED", "", out string bossKey, out string[] bossArgs, out byte bossMask) &&
            bossKey.StartsWith("sm_event_boss_victory_unknown_", StringComparison.Ordinal) && bossArgs.SequenceEqual(new[] { "sm_event_unknown_boss" }) && bossMask == 1,
            "Unknown-killer victory keeps only the boss at argument zero and remaps its fallback mask correctly.");
        Check(EventMessageText.RenderForLanguage(bossKey, bossArgs, bossMask, "Korean").Contains(korean["sm_event_unknown_boss"]),
            "Unknown-killer victory never exposes an English fallback role to a Korean client.");

        foreach (string token in new[] { "sm_event_unknown_player", "sm_event_unknown_creature", "sm_event_unknown_boss" })
        {
            Check(EventMessageText.IsValidLabels(new[] { token }, 1) && EventMessageText.IsValidLabels(new[] { token }, 0),
                "Only an explicit mask distinguishes a fixed label from a literal token-shaped name.");
        }
        Check(!EventMessageText.IsValidLabels(new[] { "sm_event_client_report" }, 1) &&
            !EventMessageText.IsValidLabels(new[] { "sm_connection_failed" }, 1) &&
            !EventMessageText.IsValidLabels(new[] { "ordinary name" }, 1) &&
            !EventMessageText.IsValidLabels(new[] { "sm_event_unknown_player" }, 2) &&
            !EventMessageText.IsValidLabels(new[] { "sm_event_unknown_player", "sm_event_unknown_boss" }, 4),
            "Masks cannot reference arbitrary locale keys or arguments outside the actual array.");
    }

    private static bool IsHangul(char value) => value >= '\uac00' && value <= '\ud7a3';

    private static void DisplayPackets()
    {
        foreach (string key in ServerManager.PlayerLocalizer.Table("English").Keys.Where(key => key.StartsWith("sm_event_", StringComparison.Ordinal)))
        foreach (string kind in new[] { "player.death", "combat.pvp_kill", "boss.killed" })
        for (int count = 1; count <= 2; ++count)
        {
            if (!EventMessageText.IsValidMessage(kind, key, count)) continue;
            var value = Packet(kind, key, Enumerable.Range(0, count).Select(index => "이름😀" + index).ToArray());
            EventDisplayPacket decoded = RoundTrip(value);
            Check(EventMessageText.RenderForLanguage(decoded.MessageKey, decoded.Arguments, decoded.LabelMask, "Korean").Any(IsHangul),
                "Every allowed packet kind/key variant renders from Korean resources after decoding.");
        }
        var fallback = Packet("player.death", "sm_event_death_creature_1", new[] { "sm_event_unknown_player", "sm_event_unknown_creature" }, 3);
        EventDisplayPacket fallbackCopy = RoundTrip(fallback);
        Check(EventMessageText.RenderForLanguage(fallbackCopy.MessageKey, fallbackCopy.Arguments, fallbackCopy.LabelMask, "Korean") ==
            EventMessageText.RenderForLanguage(fallback.MessageKey, fallback.Arguments, fallback.LabelMask, "Korean"), "Packet preserves the explicit label mask independently from token-shaped names.");
        RoundTrip(Packet("player.death", "sm_event_death_creature_1", new[] { "sm_event_unknown_player", "sm_event_unknown_creature" }, 0));
        var announcement = Packet("server.announcement", "", Array.Empty<string>());
        announcement.Message = "Literal $sm_event_unknown_player <b>notice</b>\nsecond line";
        Check(RoundTrip(announcement).Message == announcement.Message, "Server announcements retain literal free text without template interpretation.");
        announcement.Message = new string('x', 2000); RoundTrip(announcement);
        RoundTrip(Packet("player.death", "sm_event_death_smoke_1", new[] { new string('가', 106) + "aa" }));

        foreach (var label in WebhookLabels)
        foreach (string kind in new[] { "player.death", "combat.pvp_kill", "boss.killed", "server.announcement" })
            RejectDisplay(Packet(kind, label.Key, Enumerable.Range(0, label.Value).Select(index => "NAME" + index).ToArray()));

        foreach (EventDisplayPacket invalid in new[]
        {
            Packet("player.death", "sm_connection_failed", Array.Empty<string>()),
            Packet("player.death", "sm_menu_guide_title", Array.Empty<string>()),
            Packet("player.death", "sm_event_unknown_player", Array.Empty<string>()),
            Packet("player.death", "sm_event_death_smoke_4", new[] { "player" }),
            Packet("player.death", "sm_event_boss_victory_1", new[] { "player", "boss" }),
            Packet("boss.killed", "sm_event_death_smoke_1", new[] { "player" }),
            Packet("combat.pvp_kill", "sm_event_death_smoke_1", new[] { "player" }),
            Packet("player.death", "sm_event_death_creature_1", new[] { "one" }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "one", "extra" }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "one", "two", "three" }),
            Packet("chat.shout", "sm_event_death_smoke_1", new[] { "player" }),
            Packet("", "sm_event_death_smoke_1", new[] { "player" }),
            Packet(new string('x', 65), "sm_event_death_smoke_1", new[] { "player" }),
            Packet("player.death", new string('k', 97), new[] { "player" }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "sm_connection_failed" }, 1),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "sm_event_client_report" }, 1),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "ordinary name" }, 1),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "sm_event_unknown_player" }, 2),
            Packet("player.death", "sm_event_death_creature_1", new[] { "sm_event_unknown_player", "sm_event_unknown_boss" }, 4),
            Packet("player.death", "sm_event_death_smoke_1", new[] { new string('x', 321) }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { new string('가', 107) }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "line\nname" }),
            Packet("player.death", "sm_event_death_smoke_1", new[] { "tab\tname" }),
            Packet("server.announcement", "sm_event_death_smoke_1", new[] { "player" }),
            Packet("server.announcement", "", new[] { "argument" }),
            Packet("server.announcement", "", Array.Empty<string>(), 1),
            Packet("server.announcement", "", Array.Empty<string>())
        }) RejectDisplay(invalid);

        var mixedStory = Packet("player.death", "sm_event_death_smoke_1", new[] { "player" }); mixedStory.Message = "SERVER_ENGLISH_ONLY"; RejectDisplay(mixedStory);
        foreach (string invalidText in new[] { "\t", "line\tbad", "\0", new string('x', 2001) })
        { var invalid = Packet("server.announcement", "", Array.Empty<string>()); invalid.Message = invalidText; RejectDisplay(invalid); }
        var noSequence = Packet("player.death", "sm_event_death_smoke_1", new[] { "player" }); noSequence.Sequence = 0; RejectDisplay(noSequence);

        var valid = Packet("player.death", "sm_event_death_smoke_1", new[] { "player" });
        byte[] complete = EventDisplayCodec.Encode(valid).GetArray();
        Check(BitConverter.ToUInt16(complete, 4) == 2, "Localized event packets require wire version two.");
        for (int length = 0; length < complete.Length; ++length)
            Check(!EventDisplayCodec.TryDecode(new ZPackage(complete.Take(length).ToArray()), out _), "Truncated event headers/arguments always fail closed.");
        byte[] invalidUtf8 = (byte[])complete.Clone(); invalidUtf8[invalidUtf8.Length - 1] = 0xff;
        Check(!EventDisplayCodec.TryDecode(new ZPackage(invalidUtf8), out _), "Malformed UTF-8 argument bytes are rejected before local rendering.");
        Check(!EventDisplayCodec.TryDecode(new ZPackage(complete.Concat(new byte[] { 0 }).ToArray()), out _) &&
            !EventDisplayCodec.TryDecode(new ZPackage(new byte[EventDisplayCodec.MaximumPacketBytes + 1]), out _), "Trailing payload bytes and oversized display packets are rejected.");
        byte[] oldVersion = (byte[])complete.Clone(); oldVersion[4] = 1;
        Check(!EventDisplayCodec.TryDecode(new ZPackage(oldVersion), out _) && !EventDisplayCodec.TryDecode(new ZPackage(LegacyDisplay()), out _),
            "Both version-byte downgrade and genuine literal-message v1 frames are rejected without legacy fallback.");
        var wrongMagic = (byte[])complete.Clone(); wrongMagic[0] ^= 0xff;
        Check(!EventDisplayCodec.TryDecode(new ZPackage(wrongMagic), out _), "A foreign display frame cannot enter the localization path.");
    }

    private static EventDisplayPacket Packet(string kind, string key, string[] arguments, byte mask = 0) => new EventDisplayPacket
    {
        SessionId = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray(),
        Nonce = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray(), Sequence = 3,
        Kind = kind, Message = "", MessageKey = key, Arguments = arguments, LabelMask = mask
    };

    private static EventDisplayPacket RoundTrip(EventDisplayPacket value)
    {
        Check(EventDisplayCodec.TryDecode(EventDisplayCodec.Encode(value), out EventDisplayPacket copy), "Actual localized display codec accepts and decodes a valid frame.");
        Check(copy.Kind == value.Kind && copy.Message == value.Message && copy.MessageKey == value.MessageKey && copy.LabelMask == value.LabelMask &&
            copy.Arguments.SequenceEqual(value.Arguments) && copy.Sequence == value.Sequence && copy.SessionId.SequenceEqual(value.SessionId) && copy.Nonce.SequenceEqual(value.Nonce),
            "Event tokens/labels/literals round-trip independently of the existing authenticated envelope.");
        return copy;
    }

    private static void RejectDisplay(EventDisplayPacket value)
    {
        bool rejected = false;
        try { EventDisplayCodec.Encode(value); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Sender validates kind/key/arity/label-mask/size rather than creating an ambiguous display frame.");
        Check(!EventDisplayCodec.TryDecode(new ZPackage(RawDisplay(value)), out _), "Receiver independently rejects malformed display fields without trusting the sender encoder.");
    }

    private static byte[] RawDisplay(EventDisplayPacket value)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
        {
            byte[] kind = Encoding.UTF8.GetBytes(value.Kind), message = Encoding.UTF8.GetBytes(value.Message), key = Encoding.UTF8.GetBytes(value.MessageKey);
            writer.Write(0x44454D53); writer.Write((ushort)2); writer.Write(value.SessionId); writer.Write(value.Nonce); writer.Write(value.Sequence);
            writer.Write((byte)kind.Length); writer.Write((ushort)message.Length); writer.Write((byte)key.Length); writer.Write((byte)value.Arguments.Length); writer.Write(value.LabelMask);
            writer.Write(kind); writer.Write(message); writer.Write(key);
            foreach (string argument in value.Arguments) { byte[] bytes = Encoding.UTF8.GetBytes(argument); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
            writer.Flush(); return stream.ToArray();
        }
    }

    private static byte[] LegacyDisplay()
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            EventDisplayPacket value = Packet("player.death", "", Array.Empty<string>());
            byte[] kind = Encoding.UTF8.GetBytes(value.Kind), message = Encoding.UTF8.GetBytes("Old English death message");
            writer.Write(0x44454D53); writer.Write((ushort)1); writer.Write(value.SessionId); writer.Write(value.Nonce); writer.Write(value.Sequence);
            writer.Write((byte)kind.Length); writer.Write((ushort)message.Length); writer.Write(kind); writer.Write(message); writer.Flush(); return stream.ToArray();
        }
    }

    private static void OverlayLabels()
    {
        var overlay = new ServerEventOverlay();
        MethodInfo add = typeof(ServerEventOverlay).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo draw = typeof(ServerEventOverlay).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo update = typeof(ServerEventOverlay).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        UnityEngine.Time.unscaledTime = 1f;
        foreach (string kind in new[] { "player.death", "combat.pvp_kill", "boss.killed", "server.announcement", "other.event" })
            add.Invoke(overlay, new object[] { kind, "content " + kind });
        UnityEngine.GUILayout.Labels.Clear(); draw.Invoke(overlay, null);
        Check(UnityEngine.GUILayout.Labels.SequenceEqual(new[] { "content player.death", "content combat.pvp_kill", "content boss.killed",
            "[SERVER] content server.announcement", "content other.event" }),
            "Only actual server announcements receive [SERVER]; death/PvP/boss content has no redundant tag.");
        Check(UnityEngine.GUILayout.LastStyle != null && !UnityEngine.GUILayout.LastStyle.richText,
            "Overlay labels render names as literal text, not user-controlled rich-text markup.");
        UnityEngine.Time.unscaledTime = 10f; update.Invoke(overlay, null);
        UnityEngine.GUILayout.Labels.Clear(); draw.Invoke(overlay, null);
        Check(UnityEngine.GUILayout.Labels.Count == 0, "Existing eight-second overlay expiry remains functional.");
        for (int index = 0; index < 7; ++index) add.Invoke(overlay, new object[] { "player.death", "entry " + index });
        draw.Invoke(overlay, null);
        Check(UnityEngine.GUILayout.Labels.SequenceEqual(Enumerable.Range(2, 5).Select(index => "entry " + index)),
            "Prefix simplification preserves the five-entry bounded overlay queue.");
    }

    private static void OverlayToggle(string configurationPath)
    {
        MethodInfo draw = typeof(ServerEventOverlay).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo update = typeof(ServerEventOverlay).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo instance = typeof(ServerEventOverlay).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        string[] kinds = { "player.death", "combat.pvp_kill", "boss.killed", "server.announcement" };
        Action drawCurrent = () =>
        {
            UnityEngine.GUILayout.Labels.Clear();
            if (instance.GetValue(null) is ServerEventOverlay overlay) draw.Invoke(overlay, null);
        };
        ServerEventOverlay.Shutdown();
        ServerManager.ServerManagerPlugin.ShowEventNotifications = null;
        UnityEngine.Time.unscaledTime = 20f;
        UnityEngine.Application.isBatchMode = true;
        int owners = UnityEngine.GameObject.CreatedCount;
        ServerEventOverlay.Show("server.announcement", "dedicated server");
        Check(instance.GetValue(null) == null && UnityEngine.GameObject.CreatedCount == owners,
            "Dedicated/batch mode never creates a display, regardless of notification defaults.");
        UnityEngine.Application.isBatchMode = false;
        ServerEventOverlay.Show("player.death", "not yet bound");
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.SequenceEqual(new[] { "not yet bound" }),
            "An as-yet unbound notification entry preserves the default-on display behavior.");
        ServerEventOverlay.Shutdown();

        var configuration = new ConfigFile(configurationPath, false) { SaveOnConfigSet = false };
        ConfigEntry<bool> enabled = configuration.Bind("1 - Client", "Show Event Notifications", true);
        ServerManager.ServerManagerPlugin.ShowEventNotifications = enabled;
        Check(enabled.Value, "The actual BepInEx ConfigEntry defaults to notifications enabled.");
        enabled.Value = false;
        owners = UnityEngine.GameObject.CreatedCount;
        foreach (string kind in kinds) ServerEventOverlay.Show(kind, "off " + kind);
        Check(instance.GetValue(null) == null && UnityEngine.GameObject.CreatedCount == owners,
            "A disabled entry drops death, PvP, boss and announcement displays without creating a Unity owner.");
        enabled.Value = true;
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.Count == 0, "Enabling notifications does not replay events received while disabled.");
        foreach (string kind in kinds) ServerEventOverlay.Show(kind, "on " + kind);
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.SequenceEqual(new[] { "on player.death", "on combat.pvp_kill", "on boss.killed", "[SERVER] on server.announcement" }),
            "Changing a real ConfigEntry.Value back to true permits all four new event kinds without restart.");
        var current = (ServerEventOverlay)instance.GetValue(null)!;

        enabled.Value = false;
        update.Invoke(current, null);
        enabled.Value = true;
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.Count == 0, "Update immediately clears visible entries on disable; enabling cannot resurrect them before expiry.");

        ServerEventOverlay.Show("boss.killed", "clear at GUI");
        enabled.Value = false;
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.Count == 0, "OnGUI observes the current entry value and does not draw a stale notification even before Update.");
        enabled.Value = true;
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.Count == 0, "OnGUI clears disabled notifications permanently, not only hides them for one frame.");

        ServerEventOverlay.Show("player.death", "clear at Show");
        enabled.Value = false;
        foreach (string kind in kinds) ServerEventOverlay.Show(kind, "do not queue " + kind);
        enabled.Value = true;
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.Count == 0, "The disabled Show path drops incoming events and clears any older queue without replay.");
        ServerEventOverlay.Show("server.announcement", "fresh notice");
        drawCurrent();
        Check(UnityEngine.GUILayout.Labels.SequenceEqual(new[] { "[SERVER] fresh notice" }),
            "After disable/enable transitions only a freshly received notification is rendered.");

        enabled.Value = false;
        configuration.Save();
        ServerEventOverlay.Shutdown();
        var reopened = new ConfigFile(configurationPath, false) { SaveOnConfigSet = false };
        ServerManager.ServerManagerPlugin.ShowEventNotifications = reopened.Bind("1 - Client", "Show Event Notifications", true);
        Check(!ServerManager.ServerManagerPlugin.ShowEventNotifications.Value, "A persisted false loads into the actual display entry despite the true default.");
        owners = UnityEngine.GameObject.CreatedCount;
        ServerEventOverlay.Show("server.announcement", "persisted off");
        Check(instance.GetValue(null) == null && UnityEngine.GameObject.CreatedCount == owners,
            "A persisted disabled value blocks display creation on the next client session.");
        ServerManager.ServerManagerPlugin.ShowEventNotifications = null;
    }

    private static ServerManagerEvent Make(string bucket, string id) => new ServerManagerEvent
    {
        EventId = id,
        Kind = bucket == "combat.pvp_kill" || bucket == "boss.killed" ? bucket : "player.death",
        Reliability = "client_reported",
        Actor = new ServerManagerActor("SECRET_ACTOR_ID", "SECRET_ACTOR_NAME", "player"),
        Target = new ServerManagerActor("SECRET_TARGET_ID", "SECRET_TARGET_NAME", "cause"),
        Fields = new Dictionary<string, string> { ["cause"] = bucket, ["message"] = "SECRET_AUDIT_FACT", ["damage_tags"] = "SECRET_DAMAGE_TAGS" }
    };

    private static string Format(ServerManagerEvent value, string player, string other, string language = "English")
    {
        Check(EventMessageText.TryFormat(value, player, other, language, out string text), "Supported event has a presentation.");
        return text;
    }
    private static string Fingerprint(ServerManagerEvent value) => value.EventId + "|" + value.Kind + "|" + value.Reliability + "|" +
        value.Actor?.Id + "|" + value.Actor?.Name + "|" + value.Target?.Id + "|" + value.Target?.Name + "|" +
        string.Join("|", value.Fields.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
    private static void Check(bool passed, string message) { ++_checks; if (!passed) throw new InvalidOperationException(message); }
}

namespace ServerManager.Events
{
    internal sealed class ServerManagerActor
    {
        public ServerManagerActor(string id, string name, string kind) { Id = id; Name = name; Kind = kind; }
        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }
    }
    internal sealed class ServerManagerEvent
    {
        public string EventId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Reliability { get; set; } = "";
        public ServerManagerActor? Actor { get; set; }
        public ServerManagerActor? Target { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }
    internal static class ServerManagerEventKinds
    {
        public const string PlayerDeath = "player.death", CombatPvpKill = "combat.pvp_kill", BossKilled = "boss.killed",
            ServerAnnouncement = "server.announcement";
    }
}

namespace ServerManager
{
    internal static class ServerManagerPlugin
    {
        internal static ConfigEntry<bool>? ShowEventNotifications { get; set; }
    }
    // Deliberately strict resource adapter: a missing Korean key throws rather
    // than hiding the omission behind English fallback. The actual bundled
    // PlayerLocalizer is covered separately by PlayerLocalizationSmoke.ps1.
    internal static class PlayerLocalizer
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        public static string LocalLanguage = "English";
        internal static void Load(string english, string korean)
        {
            var deserializer = new DeserializerBuilder().Build();
            Tables["English"] = deserializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(english));
            Tables["Korean"] = deserializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(korean));
        }
        internal static Dictionary<string, string> Table(string language) => Tables[language];
        internal static string Text(string key, params string[] args) => TextForLanguage(LocalLanguage, key, args);
        internal static string TextForLanguage(string language, string key, params string[] args) =>
            string.Format(CultureInfo.InvariantCulture, Tables[language][key], args.Cast<object>().ToArray());
    }
    internal static class ConnectionProtocolLimits { public const int SessionIdBytes = 16, NonceBytes = 32; }
    internal enum ProtocolRejectCode { MalformedPacket, ProtocolVersionMismatch }
    internal sealed class ProtocolRejection { public ProtocolRejection(ProtocolRejectCode code, string message) { } }
    internal static class ProtocolByteUtil
    {
        internal static byte[] ReadExact(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return bytes;
        }
    }
}

public sealed class ZPackage
{
    private readonly byte[] _data;
    public ZPackage(byte[] data) => _data = data;
    public int Size() => _data.Length;
    public byte[] GetArray() => _data;
}

namespace UnityEngine
{
    public class MonoBehaviour
    {
        public GameObject gameObject = null!;
        protected static void DontDestroyOnLoad(object owner) { }
        protected static void Destroy(object owner) { }
    }
    public sealed class GameObject
    {
        public static int CreatedCount;
        public GameObject(string name) { ++CreatedCount; }
        public T AddComponent<T>() where T : MonoBehaviour, new() { var component = new T(); component.gameObject = this; return component; }
    }
    public static class Application { public static bool isBatchMode; }
    public static class Time { public static float unscaledTime; }
    public static class Screen { public static int width = 1920; }
    public enum FontStyle { Bold }
    public enum TextAnchor { MiddleCenter }
    public struct Color { public static readonly Color white = default; }
    public struct Rect { public Rect(float left, float top, float width, float height) { } }
    public sealed class GUIStyleState { public Color textColor; }
    public sealed class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle original) { }
        public int fontSize;
        public FontStyle fontStyle;
        public TextAnchor alignment;
        public bool wordWrap;
        public bool richText = true;
        public readonly GUIStyleState normal = new GUIStyleState();
    }
    public sealed class GUISkin { public readonly GUIStyle label = new GUIStyle(); }
    public static class GUI { public static readonly GUISkin skin = new GUISkin(); }
    public static class GUILayout
    {
        public static readonly List<string> Labels = new List<string>();
        public static GUIStyle? LastStyle;
        public static void BeginArea(Rect area) { }
        public static void Label(string message, GUIStyle style) { Labels.Add(message); LastStyle = style; }
        public static void EndArea() { }
    }
}
