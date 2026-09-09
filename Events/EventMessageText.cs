using System;
using System.Globalization;
using System.Text;

namespace ServerManager.Events;

// Presentation only: event fields remain the neutral source of audit facts.
internal static class EventMessageText
{
    private const int MaximumNameLength = 80;
    private const string CreatureDeath = "sm_event_death_creature";
    private const string BossDeath = "sm_event_death_boss";
    private const string PoisonDeath = "sm_event_death_poisoned";
    private const string BurningDeath = "sm_event_death_burning";
    private const string DrowningDeath = "sm_event_death_drowning";
    private const string FallingDeath = "sm_event_death_fall";
    private const string TreeDeath = "sm_event_death_tree";
    private const string SmokeDeath = "sm_event_death_smoke";
    private const string FreezingDeath = "sm_event_death_freezing";
    private const string UnknownDeath = "sm_event_death_unknown";
    private const string PlayerBattle = "sm_event_pvp";
    private const string BossVictory = "sm_event_boss_victory";
    private const string UnknownKillerBossVictory = "sm_event_boss_victory_unknown";
    private const string UnknownPlayer = "sm_event_unknown_player";
    private const string UnknownCreature = "sm_event_unknown_creature";
    private const string UnknownBoss = "sm_event_unknown_boss";

    internal static bool TryCreate(
        ServerManagerEvent value,
        string player,
        string other,
        out string key,
        out string[] arguments,
        out byte labelMask)
    {
        key = string.Empty;
        arguments = Array.Empty<string>();
        labelMask = 0;
        if (value == null) return false;

        string family;
        switch (value.Kind)
        {
            case ServerManagerEventKinds.PlayerDeath:
                family = DeathFamily(Field(value, "cause"));
                break;
            case ServerManagerEventKinds.CombatPvpKill:
                family = PlayerBattle;
                break;
            case ServerManagerEventKinds.BossKilled:
                // Pool choice uses original attribution, never destination
                // aliases or player_name (which identifies the reporter).
                family = HasKnownBossKiller(value)
                    ? BossVictory
                    : UnknownKillerBossVictory;
                break;
            default:
                return false;
        }

        key = family + "_" + (Variant(value.EventId) + 1).ToString(CultureInfo.InvariantCulture);
        TryGetFamilySchema(family, out _, out int argumentCount);
        // Nonempty supplied names are always literal, including strings equal
        // to translation keys. Only explicitly marked fallback labels localize.
        string safePlayer = CleanText(player, MaximumNameLength);
        string safeOther = CleanText(other, MaximumNameLength);
        if (family == UnknownKillerBossVictory)
        {
            arguments = new[] { LabelOrName(safeOther, UnknownBoss, 0, ref labelMask) };
        }
        else if (argumentCount == 2)
        {
            arguments = new[]
            {
                LabelOrName(safePlayer, UnknownPlayer, 0, ref labelMask),
                LabelOrName(safeOther, OtherFallback(value), 1, ref labelMask)
            };
        }
        else
        {
            arguments = new[] { LabelOrName(safePlayer, UnknownPlayer, 0, ref labelMask) };
        }
        return true;
    }

    internal static bool TryFormat(
        ServerManagerEvent value,
        string player,
        string other,
        string language,
        out string message)
    {
        message = string.Empty;
        if (!TryCreate(value, player, other, out string key, out string[] arguments, out byte labelMask))
            return false;
        message = RenderForLanguage(key, arguments, labelMask, language);
        return true;
    }

    // Render is game-thread-only. The explicit-language path never consults
    // Unity or a game-language singleton and is also safe for webhook workers.
    internal static string Render(string key, string[] arguments, byte labelMask) =>
        RenderCore(key, arguments, labelMask, string.Empty, true);

    internal static string RenderForLanguage(string key, string[] arguments, byte labelMask, string language) =>
        RenderCore(key, arguments, labelMask, language, false);

    internal static bool IsValidMessage(string kind, string key, int argumentCount)
    {
        return TryGetKeySchema(key, out string expectedKind, out int expectedCount) &&
               argumentCount == expectedCount &&
               (kind == expectedKind ||
                kind == ServerManagerEventKinds.PlayerDeath && expectedKind == ServerManagerEventKinds.CombatPvpKill);
    }

    internal static bool IsValidLabels(string[] arguments, byte labelMask)
    {
        if (arguments == null || arguments.Length < 1 || arguments.Length > 2 ||
            (labelMask & ~((1 << arguments.Length) - 1)) != 0) return false;
        for (int index = 0; index < arguments.Length; ++index)
        {
            string argument = arguments[index];
            if (argument == null) return false;
            if ((labelMask & (1 << index)) != 0 &&
                argument != UnknownPlayer && argument != UnknownCreature && argument != UnknownBoss) return false;
        }
        return true;
    }

    internal static string PlayerName(ServerManagerEvent value)
    {
        if (value == null) return string.Empty;
        string actor = CleanName(value.Actor?.Name);
        if (actor.Length != 0) return actor;
        // For boss events player_name is the reporter, not necessarily killer.
        return CleanName(Field(value, value.Kind == ServerManagerEventKinds.BossKilled ? "player" : "player_name"));
    }

    internal static string OtherName(ServerManagerEvent value)
    {
        if (value == null) return string.Empty;
        string cause = Field(value, "cause");
        string target = CleanOtherName(value.Target?.Name, cause);
        if (target.Length != 0) return target;
        return CleanOtherName(Field(value, value.Kind == ServerManagerEventKinds.BossKilled ? "boss" : "attacker"), cause);
    }

    private static string RenderCore(string key, string[] arguments, byte labelMask, string language, bool currentLanguage)
    {
        if (!TryGetKeySchema(key, out _, out int argumentCount) ||
            !IsValidLabels(arguments, labelMask) || arguments.Length != argumentCount) return string.Empty;
        string[] resolved = (string[])arguments.Clone();
        for (int index = 0; index < resolved.Length; ++index)
        {
            if ((labelMask & (1 << index)) == 0) continue;
            resolved[index] = currentLanguage
                ? PlayerLocalizer.Text(resolved[index])
                : PlayerLocalizer.TextForLanguage(language, resolved[index]);
        }
        string message = currentLanguage
            ? PlayerLocalizer.Text(key, resolved)
            : PlayerLocalizer.TextForLanguage(language, key, resolved);
        return CleanText(message, 500);
    }

    private static string LabelOrName(string name, string fallback, int index, ref byte labelMask)
    {
        if (name.Length != 0) return name;
        labelMask |= (byte)(1 << index);
        return fallback;
    }

    private static bool TryGetKeySchema(string key, out string kind, out int argumentCount)
    {
        kind = string.Empty;
        argumentCount = 0;
        if (key == null || key.Length < 3 || key.Length > 64 ||
            key[key.Length - 2] != '_' || key[key.Length - 1] < '1' || key[key.Length - 1] > '3') return false;
        return TryGetFamilySchema(key.Substring(0, key.Length - 2), out kind, out argumentCount);
    }

    private static bool TryGetFamilySchema(string family, out string kind, out int argumentCount)
    {
        kind = ServerManagerEventKinds.PlayerDeath;
        argumentCount = 1;
        switch (family)
        {
            case CreatureDeath:
            case BossDeath:
                argumentCount = 2;
                return true;
            case PoisonDeath:
            case BurningDeath:
            case DrowningDeath:
            case FallingDeath:
            case TreeDeath:
            case SmokeDeath:
            case FreezingDeath:
            case UnknownDeath:
                return true;
            case PlayerBattle:
                kind = ServerManagerEventKinds.CombatPvpKill;
                argumentCount = 2;
                return true;
            case BossVictory:
                kind = ServerManagerEventKinds.BossKilled;
                argumentCount = 2;
                return true;
            case UnknownKillerBossVictory:
                kind = ServerManagerEventKinds.BossKilled;
                return true;
            default:
                kind = string.Empty;
                argumentCount = 0;
                return false;
        }
    }

    private static string DeathFamily(string cause)
    {
        // Explicit causes only: damage tags and arbitrary hit-type strings do
        // not establish the primary death cause or identify an attacker.
        switch (cause)
        {
            case "creature":
            case "enemyhit": return CreatureDeath;
            case "boss": return BossDeath;
            case "pvp":
            case "playerhit": return PlayerBattle;
            case "poisoned": return PoisonDeath;
            case "burning": return BurningDeath;
            case "drowning": return DrowningDeath;
            case "fall": return FallingDeath;
            case "tree": return TreeDeath;
            case "smoke": return SmokeDeath;
            case "freezing": return FreezingDeath;
            default: return UnknownDeath;
        }
    }

    private static bool HasKnownBossKiller(ServerManagerEvent value) =>
        CleanName(value.Actor?.Name).Length != 0 || CleanName(Field(value, "player")).Length != 0;

    private static string OtherFallback(ServerManagerEvent value)
    {
        if (value.Kind == ServerManagerEventKinds.BossKilled) return UnknownBoss;
        string cause = Field(value, "cause");
        if (value.Kind == ServerManagerEventKinds.CombatPvpKill || cause == "pvp" || cause == "playerhit") return UnknownPlayer;
        return cause == "boss" ? UnknownBoss : UnknownCreature;
    }

    private static string Field(ServerManagerEvent value, string key) =>
        value.Fields.TryGetValue(key, out string? text) ? text ?? string.Empty : string.Empty;

    private static string CleanOtherName(string? name, string cause)
    {
        string cleaned = CleanName(name);
        // PublishDeath's cause-token fallback is not an attacker name.
        return string.Equals(cleaned, cause, StringComparison.OrdinalIgnoreCase) ? string.Empty : cleaned;
    }

    private static string CleanName(string? name)
    {
        string result = CleanText(name, MaximumNameLength);
        return result.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("Unknown player", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("Unknown creature", StringComparison.OrdinalIgnoreCase) ||
               result.Equals("Unknown boss", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : result;
    }

    private static string CleanText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        StringBuilder cleaned = new(maximumLength);
        bool pendingSpace = false;
        for (int index = 0; index < value!.Length && cleaned.Length < maximumLength; ++index)
        {
            char character = value[index];
            if (char.IsControl(character) || char.IsWhiteSpace(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                pendingSpace = cleaned.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                cleaned.Append(' ');
                pendingSpace = false;
                if (cleaned.Length == maximumLength) break;
            }
            cleaned.Append(character);
        }
        if (cleaned.Length != 0 && char.IsHighSurrogate(cleaned[cleaned.Length - 1])) --cleaned.Length;
        return cleaned.ToString().Trim();
    }

    private static int Variant(string eventId)
    {
        // Explicit FNV-1a over UTF-16 code units is stable across processes,
        // runtimes and languages; names and aliases never affect selection.
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char character in eventId ?? string.Empty) hash = (hash ^ character) * 16777619u;
            return (int)(hash % 3u);
        }
    }
}
