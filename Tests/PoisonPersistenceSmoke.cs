#nullable enable annotations
#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

// Inert engine boundaries for the actual production adapter. Private poison
// fields deliberately have the installed game's visibility, so the real cached
// reflection code is exercised rather than a test replacement for that code.
public class StatusEffect
{
    protected float m_time;
    public float m_ttl;
    public float GetRemaningTime() => m_ttl - m_time;
    public float Elapsed => m_time;
    public void SetElapsed(float elapsed) => m_time = elapsed;
}

public sealed class SE_Poison : StatusEffect
{
    private float m_timer;
    private float m_damageLeft;
    private float m_damagePerHit;
    public SE_Poison() { }
    public SE_Poison(float ttl, float elapsed, float timer, float left, float hit)
    {
        m_ttl = ttl;
        m_time = elapsed;
        m_timer = timer;
        m_damageLeft = left;
        m_damagePerHit = hit;
    }
    public float Timer => m_timer;
    public float DamageLeft => m_damageLeft;
    public float DamagePerHit => m_damagePerHit;
}

public sealed class SEMan
{
    public StatusEffect Effect;
    public bool ReturnNull;
    public bool ReturnWrongType;
    public bool ThrowOnGet;
    public bool ThrowOnAdd;
    public bool ThrowAfterInsert;
    public int Added;
    public int Removed;
    public StatusEffect GetStatusEffect(int hash)
    {
        if (ThrowOnGet) throw new InvalidOperationException("fixture status lookup failure");
        return Effect;
    }
    public StatusEffect AddStatusEffect(int hash, bool resetTime)
    {
        ++Added;
        if (ThrowOnAdd) throw new InvalidOperationException("fixture status add failure");
        if (ReturnNull || Effect != null) return null;
        Effect = ReturnWrongType ? new StatusEffect() : new SE_Poison();
        if (ThrowAfterInsert) throw new InvalidOperationException("fixture Setup callback after list insertion failure");
        return Effect;
    }
    public bool RemoveStatusEffect(StatusEffect effect, bool quiet)
    {
        ++Removed;
        if (!ReferenceEquals(Effect, effect)) return false;
        Effect = null;
        return true;
    }
}

public sealed class Player
{
    public readonly Dictionary<string, string> m_customData = new Dictionary<string, string>(StringComparer.Ordinal);
    public readonly SEMan Effects = new SEMan();
    public bool Dead;
    public bool CaptureAllowed = true;
    public bool RestoreAllowed = true;
    public bool IsDead() => Dead;
    public SEMan GetSEMan() => Effects;
}

public static class PoisonFixtureStableHash
{
    public static int GetStableHashCode(this string value) => 173409;
}

namespace ServerManager
{
    // Only ownership/logging boundaries are inert; the complete production
    // value type and runtime capture/restore code are inserted unchanged.
    internal static class ServerManagerRuntime
    {
        internal static bool IsPlayerLoadInProgress { get; set; }
        internal static bool CanPersistCharacterPoison(Player player, bool restoring) =>
            player != null && player.CaptureAllowed && (!restoring || player.RestoreAllowed);
    }
    internal static class IntegrityCanonical
    {
        internal static bool IsFatal(Exception error) => error is OutOfMemoryException || error is StackOverflowException;
    }
    internal static class ServerManagerPlugin
    {
        internal static readonly PoisonFixtureLogger Log = new PoisonFixtureLogger();
    }
    internal sealed class PoisonFixtureLogger
    {
        internal readonly List<string> Warnings = new List<string>();
        public void LogWarning(string value) => Warnings.Add(value);
    }
    // SOURCE_LINKED_POISON_IMPLEMENTATION
}

public static class PoisonPersistenceSmoke
{
    private static int _assertions;

    private static void Check(bool condition, string message)
    {
        ++_assertions;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RoundTrip(float ttl, float timer, float left, float hit)
    {
        var original = new ServerManager.CharacterPoisonState(ttl, timer, left, hit);
        Check(original.IsValid, "Valid poison state was rejected.");
        string encoded = original.Encode();
        Check(encoded.StartsWith("1|", StringComparison.Ordinal), "Poison record lost its version.");
        Check(encoded.Length <= 192, "Valid record exceeded its parser bound.");
        Check(ServerManager.CharacterPoisonState.TryDecode(encoded, out var decoded), "Valid encoded poison did not decode.");
        Check(decoded.IsValid, "Decoded state is invalid.");
        Check(decoded.RemainingTime.Equals(ttl) && decoded.NextTickDelay.Equals(timer) &&
              decoded.RemainingDamage.Equals(left) && decoded.DamagePerHit.Equals(hit),
            "Poison state changed during the invariant round trip.");
        Check(string.Equals(encoded, decoded.Encode(), StringComparison.Ordinal), "Poison record did not encode deterministically.");
    }

    private static void Invalid(string value)
    {
        Check(!ServerManager.CharacterPoisonState.TryDecode(value, out _), "Malformed poison record was accepted: " + value);
    }

    private static void InvalidState(float ttl, float timer, float left, float hit)
    {
        var state = new ServerManager.CharacterPoisonState(ttl, timer, left, hit);
        Check(!state.IsValid, "Invalid poison state was marked valid.");
    }

    private const string Key = "ServerManager.Poison";

    private static void RuntimeCases()
    {
        var source = new Player();
        source.m_customData["UnrelatedMod.Key"] = "keep";
        source.Effects.Effect = new SE_Poison(30, 17.5f, .75f, 80.25f, 10.5f);
        ServerManager.CharacterPoisonPersistence.Capture(source);
        Check(source.m_customData[Key] == "1|12.5|0.75|80.25|10.5", "Capture saved full TTL or lost pending tick/damage.");
        Check(source.m_customData["UnrelatedMod.Key"] == "keep", "Capture changed unrelated customData.");

        var destination = new Player();
        destination.m_customData[Key] = source.m_customData[Key];
        destination.m_customData["UnrelatedMod.Key"] = "keep";
        ServerManager.CharacterPoisonPersistence.AfterLoad(destination, true);
        var restored = destination.Effects.Effect as SE_Poison;
        Check(restored != null && restored.m_ttl == 12.5f && restored.Elapsed == 0f &&
              restored.Timer == .75f && restored.DamageLeft == 80.25f && restored.DamagePerHit == 10.5f,
            "Restore recalculated damage, restarted original TTL, or reset tick phase.");
        Check(destination.Effects.Added == 1 && !destination.m_customData.ContainsKey(Key), "Restore did not consume the captured record once.");
        Check(destination.m_customData["UnrelatedMod.Key"] == "keep", "Restore changed unrelated customData.");
        ServerManager.CharacterPoisonPersistence.AfterLoad(destination, true);
        Check(destination.Effects.Added == 1, "Repeated Load completion reapplied an already consumed record.");
        restored.SetElapsed(2f);
        ServerManager.CharacterPoisonPersistence.Capture(destination);
        Check(ServerManager.CharacterPoisonState.TryDecode(destination.m_customData[Key], out var recaptured) &&
            recaptured.RemainingTime == 10.5f, "Subsequent capture used stale saved TTL instead of live remaining time.");

        var reused = new Player();
        var existing = new SE_Poison(90, 60, .1f, 1, 1);
        reused.Effects.Effect = existing;
        reused.m_customData[Key] = "1|4|0|-2|2";
        ServerManager.CharacterPoisonPersistence.AfterLoad(reused, true);
        Check(ReferenceEquals(existing, reused.Effects.Effect) && reused.Effects.Added == 0,
            "Existing poison was duplicated instead of resumed.");
        Check(existing.m_ttl == 4 && existing.Elapsed == 0 && existing.Timer == 0 && existing.DamageLeft == -2,
            "Existing poison retained old duration/phase or rejected legitimate exhausted damage.");

        var stale = new Player();
        stale.m_customData[Key] = "previous-profile";
        stale.m_customData["ServerCharacters PoisonTTL"] = "99";
        ServerManager.CharacterPoisonPersistence.BeforeLoad(stale);
        Check(!stale.m_customData.ContainsKey(Key), "BeforeLoad retained an earlier profile's absent customData key.");
        Check(stale.m_customData["ServerCharacters PoisonTTL"] == "99", "Own persistence mutated legacy/other-mod data.");
        ServerManager.CharacterPoisonPersistence.AfterLoad(stale, true);
        Check(stale.Effects.Added == 0, "An absent own record or a legacy ServerCharacters key restored poison.");
        stale.m_customData[Key] = "1|9|0|10|1";
        ServerManager.CharacterPoisonPersistence.AfterLoad(stale, false);
        Check(stale.Effects.Added == 0 && !stale.m_customData.ContainsKey(Key), "Failed or skipped Load applied/retained a partially loaded record.");

        var dead = new Player { Dead = true };
        dead.m_customData[Key] = "old";
        dead.Effects.Effect = new SE_Poison(30, 1, .5f, 20, 2);
        ServerManager.CharacterPoisonPersistence.Capture(dead);
        Check(!dead.m_customData.ContainsKey(Key), "Saving the dead player retained poison for respawn.");
        dead.m_customData[Key] = "1|10|0|10|1";
        ServerManager.CharacterPoisonPersistence.AfterLoad(dead, true);
        Check(dead.Effects.Added == 0 && !dead.m_customData.ContainsKey(Key), "Dead player restored poison.");

        foreach (StatusEffect effect in new StatusEffect[] { null, new StatusEffect(), new SE_Poison(3, 4, .5f, 1, 1), new SE_Poison(0, 0, 0, 1, 1) })
        {
            var cured = new Player();
            cured.Effects.Effect = effect;
            cured.m_customData[Key] = "old";
            ServerManager.CharacterPoisonPersistence.Capture(cured);
            Check(!cured.m_customData.ContainsKey(Key), "Cured, expired, absent, or indefinite poison retained an old record.");
        }
        foreach (float originalTtl in new[] { 0f, -1f, float.PositiveInfinity, float.NaN })
        {
            var indefinite = new Player();
            indefinite.Effects.Effect = new SE_Poison(originalTtl, -2, 0, 1, 1);
            ServerManager.CharacterPoisonPersistence.Capture(indefinite);
            Check(!indefinite.m_customData.ContainsKey(Key), "An indefinite/invalid original TTL was converted into a saved finite poison.");
        }
        var exactBoundary = new Player();
        exactBoundary.Effects.Effect = new SE_Poison(5, 5, 0, -1, 1);
        ServerManager.CharacterPoisonPersistence.Capture(exactBoundary);
        Check(exactBoundary.m_customData.TryGetValue(Key, out string boundaryRecord) &&
              ServerManager.CharacterPoisonState.TryDecode(boundaryRecord, out var boundary) &&
              boundary.RemainingTime > 0 && boundary.RemainingTime <= .00001f && boundary.NextTickDelay == 0,
            "Exact TTL equality lost the final due tick or created a zero-TTL permanent effect.");
        exactBoundary.Effects.Effect = null;
        ServerManager.CharacterPoisonPersistence.AfterLoad(exactBoundary, true);
        Check(exactBoundary.Effects.Effect is SE_Poison lastTick && lastTick.m_ttl > 0 && lastTick.m_ttl <= .00001f,
            "Exact-boundary poison did not restore as a finite final-tick window.");

        var pendingTick = new Player();
        pendingTick.Effects.Effect = new SE_Poison(3, 1, -.05f, 2, 1);
        ServerManager.CharacterPoisonPersistence.Capture(pendingTick);
        Check(ServerManager.CharacterPoisonState.TryDecode(pendingTick.m_customData[Key], out var due) && due.NextTickDelay == 0,
            "An overdue tick was rejected instead of resuming immediately.");

        var backup = new Player { RestoreAllowed = false };
        backup.Effects.Effect = new SE_Poison(10, 2, .5f, 5, 1);
        ServerManager.CharacterPoisonPersistence.Capture(backup);
        Check(backup.m_customData.ContainsKey(Key), "Backup-only mode no longer captures live poison.");
        backup.Effects.Effect = null;
        ServerManager.CharacterPoisonPersistence.AfterLoad(backup, true);
        Check(backup.Effects.Added == 0 && !backup.m_customData.ContainsKey(Key), "Backup-only mode forced server poison into a local character.");

        var unmanaged = new Player { CaptureAllowed = false };
        unmanaged.m_customData[Key] = "do-not-touch";
        ServerManager.CharacterPoisonPersistence.Capture(unmanaged);
        ServerManager.CharacterPoisonPersistence.BeforeLoad(unmanaged);
        ServerManager.CharacterPoisonPersistence.AfterLoad(unmanaged, true);
        Check(unmanaged.m_customData[Key] == "do-not-touch" && unmanaged.Effects.Added == 0,
            "Unmanaged single-player/menu/remote-proxy data was changed.");
        ServerManager.CharacterPoisonPersistence.Capture(null);
        ServerManager.CharacterPoisonPersistence.BeforeLoad(null);
        ServerManager.CharacterPoisonPersistence.AfterLoad(null, true);

        var loading = new Player();
        loading.m_customData[Key] = "in-progress-profile";
        ServerManager.ServerManagerRuntime.IsPlayerLoadInProgress = true;
        try { ServerManager.CharacterPoisonPersistence.Capture(loading); }
        finally { ServerManager.ServerManagerRuntime.IsPlayerLoadInProgress = false; }
        Check(loading.m_customData[Key] == "in-progress-profile", "Reentrant save during Load cleared partial customData.");

        int warningStart = ServerManager.ServerManagerPlugin.Log.Warnings.Count;
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            var malformed = new Player();
            malformed.m_customData[Key] = attempt == 1 ? "1|Infinity|0|1|1" : "broken SECRET_RECORD";
            ServerManager.CharacterPoisonPersistence.AfterLoad(malformed, true);
            Check(malformed.Effects.Added == 0 && !malformed.m_customData.ContainsKey(Key), "Malformed customData created an effect or poisoned future loads.");
        }
        Check(ServerManager.ServerManagerPlugin.Log.Warnings.Count == warningStart + 1, "Malformed snapshots spam warnings instead of reporting once.");
        Check(!ServerManager.ServerManagerPlugin.Log.Warnings[warningStart].Contains("SECRET_RECORD"), "Warning echoed raw customData.");
        var invalidLive = new Player();
        invalidLive.m_customData[Key] = "previous";
        invalidLive.Effects.Effect = new SE_Poison(float.NaN, 0, 0, 1, 1);
        ServerManager.CharacterPoisonPersistence.Capture(invalidLive);
        Check(!invalidLive.m_customData.ContainsKey(Key), "Invalid live poison left a previously valid capture behind.");
        foreach (float badTimer in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            invalidLive.m_customData[Key] = "previous";
            invalidLive.Effects.Effect = new SE_Poison(10, 1, badTimer, 1, 1);
            ServerManager.CharacterPoisonPersistence.Capture(invalidLive);
            Check(!invalidLive.m_customData.ContainsKey(Key), "Nonfinite tick delay was clamped into a valid saved state.");
        }

        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var unavailable = new Player();
            unavailable.m_customData[Key] = "1|10|.5|3|1";
            unavailable.Effects.ReturnNull = kind == 0;
            unavailable.Effects.ThrowOnAdd = kind == 1;
            unavailable.Effects.ThrowOnGet = kind == 2;
            if (kind == 3) unavailable.Effects.Effect = new StatusEffect();
            ServerManager.CharacterPoisonPersistence.AfterLoad(unavailable, true);
            Check(!unavailable.m_customData.ContainsKey(Key), "Missing/modded effect failure retained a consumed snapshot.");
            Check(unavailable.Effects.Effect is not SE_Poison, "Failed effect creation left an uninitialized poison.");
        }
        var failingCapture = new Player();
        failingCapture.m_customData[Key] = "previous";
        failingCapture.Effects.ThrowOnGet = true;
        ServerManager.CharacterPoisonPersistence.Capture(failingCapture);
        Check(!failingCapture.m_customData.ContainsKey(Key), "Failed effect lookup retained stale poison capture.");

        var callbackFailure = new Player();
        callbackFailure.m_customData[Key] = "1|10|.5|3|1";
        callbackFailure.Effects.ThrowAfterInsert = true;
        ServerManager.CharacterPoisonPersistence.AfterLoad(callbackFailure, true);
        Check(callbackFailure.Effects.Added == 1 && callbackFailure.Effects.Removed == 1 && callbackFailure.Effects.Effect == null,
            "A mod Setup callback throwing after insertion left an uninitialized poison behind.");
        Check(!callbackFailure.m_customData.ContainsKey(Key), "Post-insertion callback failure retained its consumed record.");
        var foreignCallbackFailure = new Player();
        foreignCallbackFailure.m_customData[Key] = "1|10|.5|3|1";
        foreignCallbackFailure.Effects.ReturnWrongType = true;
        foreignCallbackFailure.Effects.ThrowAfterInsert = true;
        ServerManager.CharacterPoisonPersistence.AfterLoad(foreignCallbackFailure, true);
        Check(foreignCallbackFailure.Effects.Effect is StatusEffect && foreignCallbackFailure.Effects.Effect is not SE_Poison &&
            foreignCallbackFailure.Effects.Removed == 0, "Poison callback cleanup removed a foreign replacement effect.");
    }

    public static void Run()
    {
        _assertions = 0;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en-US", "ko-KR", "de-DE", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var sample = new ServerManager.CharacterPoisonState(12.5f, .75f, 80.25f, 10.5f);
                Check(sample.Encode() == "1|12.5|0.75|80.25|10.5", "Encoding depends on the user's locale.");
                RoundTrip(12.5f, .75f, 80.25f, 10.5f);
                RoundTrip(1f, 0f, 0f, 1f); // New status: next tick may be immediate.
                RoundTrip(.1f, .9f, -5f, 5f); // Vanilla can exhaust damage before TTL expires.
                RoundTrip(5f, 20f, 1f, .125f); // Modded intervals can leave a longer pending timer.
            }

            RoundTrip(float.Epsilon, float.Epsilon, -float.Epsilon, float.Epsilon);
            RoundTrip(float.MaxValue, float.MaxValue, -float.MaxValue, float.MaxValue);
            var random = new Random(20260906);
            for (int index = 0; index < 64; ++index)
            {
                RoundTrip((float)(.001 + random.NextDouble() * 10000),
                    (float)(random.NextDouble() * 100),
                    (float)((random.NextDouble() - .5) * 10000),
                    (float)(.001 + random.NextDouble() * 500));
            }

            Check(!default(ServerManager.CharacterPoisonState).IsValid, "Default state became a permanent zero-TTL poison.");
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                InvalidState(invalid, 0, 1, 1);
                InvalidState(1, invalid, 1, 1);
                InvalidState(1, 0, invalid, 1);
                InvalidState(1, 0, 1, invalid);
            }
            InvalidState(0, 0, 1, 1);
            InvalidState(-1, 0, 1, 1);
            InvalidState(1, -1, 1, 1);
            InvalidState(1, 0, 1, 0);
            InvalidState(1, 0, 1, -1);

            Check(!ServerManager.CharacterPoisonState.TryDecode(null, out _), "Missing poison record was accepted.");
            foreach (string malformed in new[]
            {
                "", " ", "1", "1|", "1|1|0|1", "1|1|0|1|1|0", "1|1||1|1",
                "0|1|0|1|1", "2|1|0|1|1", "01|1|0|1|1", "1.0|1|0|1|1",
                "1|0|0|1|1", "1|-1|0|1|1", "1|1|-1|1|1", "1|1|0|1|0", "1|1|0|1|-1",
                "1|1,5|0|1|1", "1|1|0,5|1|1", "1|1|0|1,5|1", "1|1|0|1|1,5",
                "1|not-a-number|0|1|1", "1|1|not-a-number|1|1", "1|1|0|no|1", "1|1|0|1|no",
                "1|1E1000|0|1|1", "1|1|1E1000|1|1", "1|1|0|-1E1000|1", "1|1|0|1|1E1000",
                "1|1;0;1;1", "ServerCharacters PoisonDamage|1|0|1|1", "1|1\0|0|1|1"
            }) Invalid(malformed);
            foreach (string nonfinite in new[] { "NaN", "Infinity", "-Infinity", "+Infinity" })
            {
                Invalid("1|" + nonfinite + "|0|1|1");
                Invalid("1|1|" + nonfinite + "|1|1");
                Invalid("1|1|0|" + nonfinite + "|1");
                Invalid("1|1|0|1|" + nonfinite);
            }
            Invalid("1|" + new string('0', 184) + "1|0|1|1"); // Numerically valid, but >192 characters.
            Invalid(new string('x', 10000));

            Check(ServerManager.CharacterPoisonState.TryDecode("1|1.25E+1|7.5E-1|-0.5|1.05E+1", out var exponent),
                "Finite invariant exponent syntax was rejected.");
            Check(exponent.RemainingTime == 12.5f && exponent.NextTickDelay == .75f &&
                  exponent.RemainingDamage == -.5f && exponent.DamagePerHit == 10.5f,
                "Invariant exponent values were parsed incorrectly.");
            RuntimeCases();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
        Console.WriteLine("Poison persistence codec/runtime smoke passed (" + _assertions + " assertions; source-linked, no Unity or game files).");
    }
}
