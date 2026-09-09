using System;
using System.Globalization;
using System.Reflection;

namespace ServerManager;

/// <summary>Remaining poison state, not a new duration or damage calculation.</summary>
internal readonly struct CharacterPoisonState
{
    public CharacterPoisonState(float remainingTime, float nextTickDelay,
        float remainingDamage, float damagePerHit)
    {
        RemainingTime = remainingTime;
        NextTickDelay = nextTickDelay;
        RemainingDamage = remainingDamage;
        DamagePerHit = damagePerHit;
    }

    public float RemainingTime { get; }
    public float NextTickDelay { get; }
    public float RemainingDamage { get; }
    public float DamagePerHit { get; }

    // Vanilla ends poison by TTL, not by damageLeft: its last ticks can leave
    // zero or negative damageLeft. A nonpositive TTL would create an infinite SE.
    public bool IsValid => Finite(RemainingTime) && RemainingTime > 0 &&
        Finite(NextTickDelay) && NextTickDelay >= 0 && Finite(RemainingDamage) &&
        Finite(DamagePerHit) && DamagePerHit > 0;

    public string Encode()
    {
        if (!IsValid) throw new InvalidOperationException("Invalid remaining poison state.");
        return "1|" + RemainingTime.ToString("R", CultureInfo.InvariantCulture) + "|" +
            NextTickDelay.ToString("R", CultureInfo.InvariantCulture) + "|" +
            RemainingDamage.ToString("R", CultureInfo.InvariantCulture) + "|" +
            DamagePerHit.ToString("R", CultureInfo.InvariantCulture);
    }

    public static bool TryDecode(string? encoded, out CharacterPoisonState state)
    {
        state = default;
        if (string.IsNullOrEmpty(encoded) || encoded!.Length > 192) return false;
        string[] fields = encoded.Split('|');
        if (fields.Length != 5 || fields[0] != "1" ||
            !Parse(fields[1], out float ttl) || !Parse(fields[2], out float timer) ||
            !Parse(fields[3], out float left) || !Parse(fields[4], out float hit)) return false;
        CharacterPoisonState candidate = new(ttl, timer, left, hit);
        if (!candidate.IsValid) return false;
        state = candidate;
        return true;
    }

    private static bool Parse(string value, out float result)
    {
        result = 0;
        // TryParse accepts trailing NUL on some runtimes. Only the compact
        // numeric form written by this codec belongs in the persisted value.
        foreach (char character in value)
            if (char.IsControl(character) || char.IsWhiteSpace(character)) return false;
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>
/// Main-thread extension of vanilla Player.Save/Load for the currently bound
/// server character only. Uses the existing full-profile transport/checkpoint.
/// </summary>
internal static class CharacterPoisonPersistence
{
    internal const string CustomDataKey = "ServerManager.Poison";
    private static readonly int PoisonHash = "Poison".GetStableHashCode();
    private static readonly FieldInfo? Timer = FloatField(typeof(SE_Poison), "m_timer");
    private static readonly FieldInfo? DamageLeft = FloatField(typeof(SE_Poison), "m_damageLeft");
    private static readonly FieldInfo? DamagePerHit = FloatField(typeof(SE_Poison), "m_damagePerHit");
    private static readonly FieldInfo? ElapsedTime = FloatField(typeof(StatusEffect), "m_time");
    private static bool _warnedSchema;
    private static bool _warnedInvalid;
    private static bool _warnedFailure;

    internal static void Capture(Player player)
    {
        if (ServerManagerRuntime.IsPlayerLoadInProgress ||
            !ServerManagerRuntime.CanPersistCharacterPoison(player, restoring: false)) return;
        try
        {
            // Also clears the previous capture on death, cure or expiry. Vanilla
            // saves the dead player before spawning/loading the replacement.
            player.m_customData.Remove(CustomDataKey);
            if (player.IsDead() ||
                player.GetSEMan().GetStatusEffect(PoisonHash) is not SE_Poison poison) return;
            if (poison.m_ttl <= 0 || float.IsNaN(poison.m_ttl) || float.IsInfinity(poison.m_ttl)) return;
            float remaining = poison.GetRemaningTime();
            if (remaining < 0) return;
            // IsDone uses time > ttl and SEMan updates before removing an SE.
            // At exact equality one final due tick can still run. Preserve that
            // boundary without restoring ttl=0 (vanilla's infinite-duration SE).
            if (remaining == 0) remaining = 0.000001f;
            if (!HasSchema()) return;
            float timer = (float)Timer!.GetValue(poison);
            if (float.IsNaN(timer) || float.IsInfinity(timer))
            {
                WarnOnce(ref _warnedInvalid, "Skipped invalid remaining poison state.");
                return;
            }
            CharacterPoisonState state = new(remaining, Math.Max(0f, timer),
                (float)DamageLeft!.GetValue(poison), (float)DamagePerHit!.GetValue(poison));
            if (!state.IsValid)
            {
                WarnOnce(ref _warnedInvalid, "Skipped invalid remaining poison state.");
                return;
            }
            player.m_customData[CustomDataKey] = state.Encode();
        }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
        {
            WarnFailure(error);
        }
    }

    internal static void BeforeLoad(Player player)
    {
        if (!ServerManagerRuntime.CanPersistCharacterPoison(player, restoring: false)) return;
        // Player.Load overwrites present custom-data keys but does not clear
        // absent ones. Never apply a key left over from an earlier profile.
        player.m_customData.Remove(CustomDataKey);
    }

    internal static void AfterLoad(Player player, bool succeeded)
    {
        if (!ServerManagerRuntime.CanPersistCharacterPoison(player, restoring: false)) return;
        bool present = player.m_customData.TryGetValue(CustomDataKey, out string encoded);
        // Consume even a partially loaded or invalid value. Future saves capture
        // the live effect again; an ACK must never reapply an older poison state.
        player.m_customData.Remove(CustomDataKey);
        if (!succeeded || !present || player.IsDead() ||
            !ServerManagerRuntime.CanPersistCharacterPoison(player, restoring: true)) return;
        if (!CharacterPoisonState.TryDecode(encoded, out CharacterPoisonState state))
        {
            WarnOnce(ref _warnedInvalid, "Ignored invalid saved poison state.");
            return;
        }
        if (!HasSchema()) return;
        SE_Poison? created = null;
        SEMan? effects = null;
        bool attemptedAdd = false;
        try
        {
            effects = player.GetSEMan();
            StatusEffect existing = effects.GetStatusEffect(PoisonHash);
            SE_Poison? poison = existing as SE_Poison;
            if (existing == null)
            {
                attemptedAdd = true;
                poison = effects.AddStatusEffect(PoisonHash, false) as SE_Poison;
                created = poison;
            }
            if (poison == null)
            {
                WarnOnce(ref _warnedFailure, "Could not restore the saved Poison status effect.");
                return;
            }
            // Resume remaining duration and tick phase. Offline time causes no
            // damage and does not cure poison; no fresh full-duration restart.
            Timer!.SetValue(poison, state.NextTickDelay);
            DamageLeft!.SetValue(poison, state.RemainingDamage);
            DamagePerHit!.SetValue(poison, state.DamagePerHit);
            ElapsedTime!.SetValue(poison, 0f);
            poison.m_ttl = state.RemainingTime;
        }
        catch (Exception error) when (!IntegrityCanonical.IsFatal(error))
        {
            // A failed reflection/mod callback must not leave a newly created,
            // uninitialized effect or make the character itself unloadable.
            if (created != null || attemptedAdd)
            {
                try
                {
                    // Vanilla inserts before Setup; a mod callback can throw
                    // before AddStatusEffect returns the newly attached effect.
                    SE_Poison? attached = created ?? effects?.GetStatusEffect(PoisonHash) as SE_Poison;
                    if (attached != null) effects?.RemoveStatusEffect(attached, true);
                }
                catch (Exception cleanup) when (!IntegrityCanonical.IsFatal(cleanup)) { }
            }
            WarnFailure(error);
        }
    }

    private static FieldInfo? FloatField(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic);
        return field?.FieldType == typeof(float) ? field : null;
    }

    private static bool HasSchema()
    {
        if (Timer != null && DamageLeft != null && DamagePerHit != null && ElapsedTime != null)
            return true;
        WarnOnce(ref _warnedSchema, "Poison persistence is unavailable: the game's status-effect fields changed.");
        return false;
    }

    private static void WarnFailure(Exception error) => WarnOnce(ref _warnedFailure,
        "Could not capture/restore poison state (" + error.GetType().Name + ").");

    private static void WarnOnce(ref bool warned, string message)
    {
        if (warned) return;
        warned = true;
        ServerManagerPlugin.Log?.LogWarning(message);
    }
}
