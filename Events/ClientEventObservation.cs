using System;
using System.Reflection;
using UnityEngine;

namespace ServerManager.Events;

[Flags]
internal enum EventDamageTags : uint
{
    Generic = 1 << 0,
    Blunt = 1 << 1,
    Slash = 1 << 2,
    Pierce = 1 << 3,
    Chop = 1 << 4,
    Pickaxe = 1 << 5,
    Fire = 1 << 6,
    Frost = 1 << 7,
    Lightning = 1 << 8,
    Poison = 1 << 9,
    Spirit = 1 << 10
}

internal static class ClientEventObservation
{
    private static readonly FieldInfo? LastHitField =
        typeof(Character).GetField(
            "m_lastHit",
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance);

    internal static EventClientReport CreateDeath(Player player)
    {
        EventClientReport report = new()
        {
            Kind = EventClientReportKind.Death,
            Cause = "environment"
        };
        HitData? hit = GetLastHit(player);
        if (hit == null)
        {
            report.Cause = player != null && player.IsSwimming()
                ? "drowning"
                : "environment";
            return report;
        }

        report.HitType = SafeToken(hit.m_hitType.ToString(), 64);
        float finalDamage = hit.GetTotalDamage();
        report.FinalDamage = float.IsNaN(finalDamage) ||
                             float.IsInfinity(finalDamage)
            ? 0f
            : Math.Max(0f, finalDamage);
        report.DamageTags = (uint)GetDamageTags(hit.m_damage);

        // These are explicit final-damage sources, not elemental channels on
        // an enemy attack. Do not turn a final status tick into drowning merely
        // because the victim is swimming, or attribute the tick to an attacker.
        switch (hit.m_hitType)
        {
            case HitData.HitType.Burning:
                report.Cause = "burning";
                return report;
            case HitData.HitType.Poisoned:
                report.Cause = "poisoned";
                return report;
            case HitData.HitType.Drowning:
                report.Cause = "drowning";
                return report;
            case HitData.HitType.Fall:
                report.Cause = "fall";
                return report;
            case HitData.HitType.Tree:
                report.Cause = "tree";
                return report;
            case HitData.HitType.Smoke:
                report.Cause = "smoke";
                return report;
            case HitData.HitType.Freezing:
                report.Cause = "freezing";
                return report;
        }

        Character? attacker = hit.GetAttacker();
        if (attacker != null)
        {
            report.AttackerPrefab = CleanPrefab(attacker.gameObject.name);
            report.AttackerName = GetCharacterName(attacker);
            report.AttackerIsPlayer = attacker is Player;
            report.Cause = report.AttackerIsPlayer
                ? "pvp"
                : attacker.IsBoss() ? "boss" : "creature";
        }
        else if (player != null && player.IsSwimming())
        {
            report.Cause = "drowning";
        }
        else if (!string.IsNullOrEmpty(report.HitType) &&
                 !string.Equals(
                     report.HitType,
                     "Undefined",
                     StringComparison.OrdinalIgnoreCase))
        {
            report.Cause = SafeToken(
                report.HitType.ToLowerInvariant(),
                64);
        }

        return report;
    }

    internal static EventClientReport CreateBossKill(Character boss)
    {
        HitData? hit = GetLastHit(boss);
        Character? attacker = hit?.GetAttacker();
        return new EventClientReport
        {
            Kind = EventClientReportKind.BossKilled,
            BossName = boss == null ? "Unknown" : GetCharacterName(boss),
            BossPrefab = boss == null
                ? string.Empty
                : CleanPrefab(boss.gameObject.name),
            BossZdoId = boss == null ? string.Empty : GetZdoId(boss),
            FinalAttacker = attacker == null
                ? "Unknown"
                : GetCharacterName(attacker)
        };
    }

    internal static string FormatDamageTags(uint flags)
    {
        EventDamageTags tags = (EventDamageTags)flags;
        System.Collections.Generic.List<string> values = new(11);
        Add(values, tags, EventDamageTags.Generic, "generic");
        Add(values, tags, EventDamageTags.Blunt, "blunt");
        Add(values, tags, EventDamageTags.Slash, "slash");
        Add(values, tags, EventDamageTags.Pierce, "pierce");
        Add(values, tags, EventDamageTags.Chop, "chop");
        Add(values, tags, EventDamageTags.Pickaxe, "pickaxe");
        Add(values, tags, EventDamageTags.Fire, "fire");
        Add(values, tags, EventDamageTags.Frost, "frost");
        Add(values, tags, EventDamageTags.Lightning, "lightning");
        Add(values, tags, EventDamageTags.Poison, "poison");
        Add(values, tags, EventDamageTags.Spirit, "spirit");
        return string.Join(",", values);
    }

    private static EventDamageTags GetDamageTags(
        HitData.DamageTypes damage)
    {
        EventDamageTags tags = 0;
        Set(ref tags, EventDamageTags.Generic, damage.m_damage);
        Set(ref tags, EventDamageTags.Blunt, damage.m_blunt);
        Set(ref tags, EventDamageTags.Slash, damage.m_slash);
        Set(ref tags, EventDamageTags.Pierce, damage.m_pierce);
        Set(ref tags, EventDamageTags.Chop, damage.m_chop);
        Set(ref tags, EventDamageTags.Pickaxe, damage.m_pickaxe);
        Set(ref tags, EventDamageTags.Fire, damage.m_fire);
        Set(ref tags, EventDamageTags.Frost, damage.m_frost);
        Set(ref tags, EventDamageTags.Lightning, damage.m_lightning);
        Set(ref tags, EventDamageTags.Poison, damage.m_poison);
        Set(ref tags, EventDamageTags.Spirit, damage.m_spirit);
        return tags;
    }

    private static void Set(
        ref EventDamageTags tags,
        EventDamageTags flag,
        float damage)
    {
        if (damage > 0f && !float.IsNaN(damage))
        {
            tags |= flag;
        }
    }

    private static void Add(
        System.Collections.Generic.ICollection<string> values,
        EventDamageTags actual,
        EventDamageTags expected,
        string name)
    {
        if ((actual & expected) != 0)
        {
            values.Add(name);
        }
    }

    private static string GetCharacterName(Character character)
    {
        string? name;
        if (character is Player player)
        {
            name = player.GetPlayerName();
        }
        else
        {
            name = character.GetHoverName();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = character.m_name;
            }

        }

        return SafeText(name, 96, "Unknown");
    }

    private static string GetZdoId(Character character)
    {
        try
        {
            ZDOID id = character.GetZDOID();
            return id == ZDOID.None ? string.Empty : id.ToString();
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            return string.Empty;
        }
    }

    private static HitData? GetLastHit(Character? character)
    {
        if (character == null || LastHitField == null)
        {
            return null;
        }

        try
        {
            return LastHitField.GetValue(character) as HitData;
        }
        catch (Exception exception) when (
            !IntegrityCanonical.IsFatal(exception))
        {
            return null;
        }
    }

    private static string CleanPrefab(string? value)
    {
        return SafeToken(
            CleanPrefabInstanceName(value),
            128);
    }

    internal static string CleanPrefabInstanceName(string? value)
    {
        const string cloneSuffix = "(Clone)";
        string result = (value ?? string.Empty).Trim();
        return result.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? result.Substring(0, result.Length - cloneSuffix.Length)
            : result;
    }

    internal static string SafeText(
        string? value,
        int maximumCharacters,
        string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string source = value!;
        System.Text.StringBuilder sanitized = new(source.Length);
        for (int index = 0; index < source.Length; ++index)
        {
            char character = source[index];
            sanitized.Append(char.IsControl(character) ? ' ' : character);
        }

        string normalized = sanitized.ToString().Trim();
        if (normalized.Length > maximumCharacters)
        {
            normalized = normalized.Substring(0, maximumCharacters);
        }

        return normalized;
    }

    internal static string SafeToken(string? value, int maximumCharacters)
    {
        string safe = SafeText(value, maximumCharacters);
        System.Text.StringBuilder builder = new(safe.Length);
        for (int index = 0; index < safe.Length; ++index)
        {
            char character = safe[index];
            if (!char.IsControl(character) && !char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
