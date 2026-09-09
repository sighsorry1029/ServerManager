// Source-link the actual death observation and event codec with inert engine
// boundaries. No Unity methods, game state, saves or network calls can execute.
using System;
using System.IO;
using System.Linq;
using ServerManager.Events;

internal static class DeathCauseSmoke
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            EnvironmentalPrecedence();
            ExistingClassification();
            MissingHitAndMetadata();
            Console.WriteLine("PASS: death cause observation/packet round-trip (" + _checks + " assertions; no game or network).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void EnvironmentalPrecedence()
    {
        foreach (HitData.HitType type in new[] { HitData.HitType.Smoke, HitData.HitType.Freezing, HitData.HitType.Burning,
            HitData.HitType.Poisoned, HitData.HitType.Drowning, HitData.HitType.Fall, HitData.HitType.Tree })
        foreach (bool swimming in new[] { false, true })
        foreach (Character? attacker in new Character?[] { null, Enemy(), Enemy(true), new Player { m_name = "ATTACKER_SECRET" } })
        {
            HitData hit = Hit(type, attacker);
            var player = new Player { Swimming = swimming };
            player.SetLastHit(hit);
            EventClientReport report = ClientEventObservation.CreateDeath(player);
            string cause = type.ToString().ToLowerInvariant();
            Check(report.Kind == EventClientReportKind.Death && report.Cause == cause,
                "Typed environmental death takes precedence over swimming and attached attacker: " + type);
            Check(!report.AttackerIsPlayer && report.AttackerName == "" && report.AttackerPrefab == "",
                "Environmental classification cannot carry a stale attacker into PvP, logs or webhooks.");
            Check(hit.AttackerReads == 0 && player.SwimmingReads == 0,
                "The explicit typed cause is resolved before attacker lookup and swimming inference.");
            Check(report.HitType == type.ToString() && report.DamageTags == (uint)(EventDamageTags.Generic | EventDamageTags.Frost) &&
                report.FinalDamage == 15.5f, "Explicit cause preserves independently reported hit type, damage channels and total.");
            Check(ReferenceEquals(hit.Attacker, attacker) && hit.m_hitType == type && hit.m_damage.m_damage == 11.5f &&
                hit.m_damage.m_frost == 4f && ReferenceEquals(player.ReadLastHit(), hit),
                "Death observation does not mutate the original combat hit or replace m_lastHit.");
            RoundTrip(report);
        }
    }

    private static void ExistingClassification()
    {
        foreach (bool swimming in new[] { false, true })
        foreach (Character attacker in new Character[] { Enemy(), Enemy(true), new Player { m_name = "ATTACKER_SECRET" } })
        {
            HitData hit = Hit(HitData.HitType.EnemyHit, attacker);
            var player = new Player { Swimming = swimming }; player.SetLastHit(hit);
            EventClientReport report = ClientEventObservation.CreateDeath(player);
            string expected = attacker is Player ? "pvp" : attacker.Boss ? "boss" : "creature";
            Check(report.Cause == expected && report.AttackerIsPlayer == (attacker is Player),
                "Frost damage alone does not turn a creature/boss/PvP attack into a freezing death.");
            Check(report.AttackerName == "ATTACKER_SECRET" && report.AttackerPrefab == "AttackerPrefab",
                "Ordinary attacker identity and prefab cleanup remain unchanged.");
            Check(report.HitType == "EnemyHit" && hit.AttackerReads == 1 && player.SwimmingReads == 0,
                "Ordinary attacker attribution still precedes swimming fallback.");
            RoundTrip(report);
        }

        foreach (HitData.HitType type in new[] { HitData.HitType.Undefined, HitData.HitType.Impact,
            HitData.HitType.Cart, HitData.HitType.Boat, (HitData.HitType)12345 })
        foreach (bool swimming in new[] { false, true })
        {
            HitData hit = Hit(type, null);
            var player = new Player { Swimming = swimming }; player.SetLastHit(hit);
            EventClientReport report = ClientEventObservation.CreateDeath(player);
            string expected = swimming ? "drowning" : type == HitData.HitType.Undefined ? "environment" : type.ToString().ToLowerInvariant();
            Check(report.Cause == expected && !report.AttackerIsPlayer,
                "The narrow patch does not change existing other-hit or undefined-hit inference: " + type);
            Check(report.HitType == type.ToString(), "Unknown/modded numeric hit types remain metadata, without being reclassified as smoke/freezing.");
            RoundTrip(report);
        }
    }

    private static void MissingHitAndMetadata()
    {
        foreach (Player? player in new Player?[] { null, new Player(), new Player { Swimming = true } })
        {
            EventClientReport report = ClientEventObservation.CreateDeath(player!);
            Check(report.Cause == (player != null && player.Swimming ? "drowning" : "environment") &&
                report.HitType == "" && report.DamageTags == 0 && report.FinalDamage == 0 && !report.AttackerIsPlayer,
                "Missing player/last-hit fallback remains safe and does not invent smoke or freezing.");
            RoundTrip(report);
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -3f })
        {
            HitData hit = Hit(HitData.HitType.Smoke, null); hit.TotalOverride = invalid;
            var player = new Player(); player.SetLastHit(hit);
            EventClientReport report = ClientEventObservation.CreateDeath(player);
            Check(report.Cause == "smoke" && report.FinalDamage == 0, "Explicit cause still uses existing finite, nonnegative damage normalization.");
            RoundTrip(report);
        }
        Check(ClientEventObservation.FormatDamageTags((uint)(EventDamageTags.Generic | EventDamageTags.Frost)) == "generic,frost",
            "Damage channel text remains independent from the environmental cause.");
    }

    private static Character Enemy(bool boss = false) => new Character { m_name = "ATTACKER_SECRET", Boss = boss };

    private static HitData Hit(HitData.HitType type, Character? attacker) => new HitData
    {
        m_hitType = type, Attacker = attacker,
        m_damage = new HitData.DamageTypes { m_damage = 11.5f, m_frost = 4f }
    };

    private static void RoundTrip(EventClientReport report)
    {
        report.SessionId = Enumerable.Range(1, ServerManager.ConnectionProtocolLimits.SessionIdBytes).Select(value => (byte)value).ToArray();
        report.Nonce = Enumerable.Range(33, ServerManager.ConnectionProtocolLimits.NonceBytes).Select(value => (byte)value).ToArray();
        report.Sequence = 17;
        ZPackage packet = EventClientReportCodec.Encode(report);
        Check(EventClientReportCodec.TryDecode(packet, out EventClientReport copy, out _) &&
            copy.Kind == report.Kind && copy.Cause == report.Cause && copy.HitType == report.HitType &&
            copy.DamageTags == report.DamageTags && copy.FinalDamage == report.FinalDamage &&
            copy.AttackerIsPlayer == report.AttackerIsPlayer && copy.AttackerName == report.AttackerName && copy.AttackerPrefab == report.AttackerPrefab,
            "Actual death packet codec round-trips cause, attacker classification and independent damage metadata.");
        Check(copy.Sequence == report.Sequence && copy.SessionId.SequenceEqual(report.SessionId) && copy.Nonce.SequenceEqual(report.Nonce),
            "Cause improvements retain the existing session/nonce/sequence wire envelope.");
    }

    private static void Check(bool passed, string message)
    {
        ++_checks;
        if (!passed) throw new InvalidOperationException(message);
    }
}

namespace UnityEngine
{
    public sealed class GameObject { public string name = "AttackerPrefab(Clone)"; }
}

public class Character
{
    private HitData? m_lastHit;
    public string m_name = "victim";
    public bool Boss;
    public UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
    public void SetLastHit(HitData hit) => m_lastHit = hit;
    public HitData? ReadLastHit() => m_lastHit;
    public bool IsBoss() => Boss;
    public string GetHoverName() => m_name;
    public ZDOID GetZDOID() => default;
}

public sealed class Player : Character
{
    public bool Swimming;
    public int SwimmingReads;
    public bool IsSwimming() { ++SwimmingReads; return Swimming; }
    public string GetPlayerName() => m_name;
}

public struct ZDOID
{
    public static readonly ZDOID None = default;
    public static bool operator ==(ZDOID first, ZDOID second) => true;
    public static bool operator !=(ZDOID first, ZDOID second) => false;
    public override bool Equals(object? value) => value is ZDOID;
    public override int GetHashCode() => 0;
}

public sealed class HitData
{
    public enum HitType { Undefined = 0, EnemyHit = 1, PlayerHit = 2, Fall = 3, Drowning = 4, Burning = 5,
        Freezing = 6, Poisoned = 7, Water = 8, Smoke = 9, EdgeOfWorld = 10, Impact = 11, Cart = 12, Tree = 13, Boat = 17 }
    public struct DamageTypes
    {
        public float m_damage, m_blunt, m_slash, m_pierce, m_chop, m_pickaxe, m_fire, m_frost, m_lightning, m_poison, m_spirit;
    }
    public HitType m_hitType;
    public DamageTypes m_damage;
    public Character? Attacker;
    public int AttackerReads;
    public float? TotalOverride;
    public Character? GetAttacker() { ++AttackerReads; return Attacker; }
    public float GetTotalDamage() => TotalOverride ?? (m_damage.m_damage + m_damage.m_blunt + m_damage.m_slash + m_damage.m_pierce +
        m_damage.m_chop + m_damage.m_pickaxe + m_damage.m_fire + m_damage.m_frost + m_damage.m_lightning + m_damage.m_poison + m_damage.m_spirit);
}

public sealed class ZPackage
{
    private readonly byte[] _data;
    public ZPackage(byte[] data) => _data = data;
    public int Size() => _data.Length;
    public byte[] GetArray() => _data;
}

namespace ServerManager
{
    internal static class PlayerLocalizer
    {
        internal static string Text(string key, params string[] args) => throw new InvalidOperationException("Death observation must not localize on the event-report path.");
        internal static string TextForLanguage(string language, string key, params string[] args) => throw new InvalidOperationException("Death observation must not localize on the event-report path.");
    }
    internal static class IntegrityCanonical { internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException; }
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

namespace ServerManager.Events
{
    internal sealed class ServerManagerActor
    {
        public string Name { get; set; } = "";
    }
    internal sealed class ServerManagerEvent
    {
        public string EventId { get; set; } = "";
        public string Kind { get; set; } = "";
        public ServerManagerActor? Actor { get; set; }
        public ServerManagerActor? Target { get; set; }
        public System.Collections.Generic.Dictionary<string, string> Fields { get; set; } = new System.Collections.Generic.Dictionary<string, string>();
    }
    internal static class ServerManagerEventKinds
    {
        public const string PlayerDeath = "player.death", CombatPvpKill = "combat.pvp_kill", BossKilled = "boss.killed",
            ServerAnnouncement = "server.announcement";
    }
}
