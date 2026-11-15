using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ServerManager;

// Owner-local presentation data, not part of the command result or wire protocol.
internal sealed class CharacterAdminReceipt
{
    private readonly string _template;
    private readonly string _name;
    private readonly string _fallback;
    private readonly string _value;
    private readonly int _quality;

    private CharacterAdminReceipt(string template, string name, string fallback, string value, int quality = 0)
    {
        _template = template;
        _name = name;
        _fallback = fallback;
        _value = value;
        _quality = quality;
    }

    internal static CharacterAdminReceipt ForItem(string itemName, string prefab, int amount, int quality) =>
        new(quality > 1 ? "sm_receipt_item_quality" : "sm_receipt_item", itemName, prefab,
            amount.ToString(CultureInfo.InvariantCulture), quality);

    internal static CharacterAdminReceipt ForSkill(string skill, float level) =>
        string.Equals(skill, "all", StringComparison.OrdinalIgnoreCase)
            ? new("sm_receipt_skills_all", string.Empty, string.Empty, level.ToString("R", CultureInfo.InvariantCulture))
            : new("sm_receipt_skill", "$skill_" + skill.ToLowerInvariant(), skill, level.ToString("R", CultureInfo.InvariantCulture));

    internal void Show(Player player)
    {
        try
        {
            Chat chat = Chat.instance;
            if (Application.isBatchMode || player == null || player != Player.m_localPlayer ||
                chat == null) return;
            // Append on the owner only. Do not send player speech, take input focus,
            // or skip the history entry just because the HUD is currently hidden.
            chat.AddString(FormatMessage());
            ServerManagerRuntime.WakeChat(chat);
        }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
        {
            // A missing/modded chat must never change or retry a confirmed grant.
            ServerManagerPlugin.Log?.LogWarning("Could not display a personal command receipt (" + error.GetType().Name + ").");
        }
    }

    private string FormatMessage()
    {
        if (_template == "sm_receipt_skills_all") return PlayerLocalizer.Text(_template, _value);
        string label = _name;
        if (string.IsNullOrWhiteSpace(label)) label = _fallback;
        else if (label.StartsWith("$", StringComparison.Ordinal))
        {
            string? translated = Localization.instance?.Localize(label);
            label = string.IsNullOrEmpty(translated) || translated == label || translated == "[" + label.Substring(1) + "]"
                ? _fallback : translated!;
        }
        label = ChatLabel(label);
        return _quality > 1 ? PlayerLocalizer.Text(_template, label, _value, _quality.ToString(CultureInfo.InvariantCulture))
            : PlayerLocalizer.Text(_template, label, _value);
    }

    private static string ChatLabel(string value)
    {
        // TMP renders the chat buffer as rich text. Treat modded names as a
        // bounded, single-line literal, including backslash escape sequences.
        const int limit = 96;
        StringBuilder label = new(Math.Min(limit, value.Length));
        for (int i = 0; i < value.Length && label.Length < limit; ++i)
        {
            char ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (label.Length + 2 > limit) break;
                label.Append(ch).Append(value[++i]);
                continue;
            }
            if (char.IsControl(ch) || char.IsSurrogate(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format ||
                ch == '\u2028' || ch == '\u2029') ch = ' ';
            label.Append(ch switch { '<' => '‹', '>' => '›', '\\' => '＼', _ => ch });
        }
        return label.ToString();
    }
}
