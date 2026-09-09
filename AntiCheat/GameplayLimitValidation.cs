using System;
using System.Collections.Generic;
using UnityEngine;

namespace ServerManager;

internal enum RoutedDamageInspection
{
    NotDamage = 0,
    Damage = 1,
    MalformedDamage = 2,
    MalformedRoutedRpc = 3
}

internal enum RoutedDamageTargetKind
{
    Character = 0,
    Structure = 1,
    MineRock = 2,
    Destructible = 3,
    TreeLog = 4,
    TreeBase = 5
}

/// <summary>
/// Immutable result of decoding a server-observed routed RPC_Damage envelope.
/// Target metadata is resolved from the server's ZDO and prefab registries;
/// client-claimed sender and target-peer envelope fields are deliberately not
/// retained or exposed as attribution evidence.
/// </summary>
internal sealed class RoutedDamageObservation
{
    internal RoutedDamageObservation(
        HitData hit,
        ZDOID target,
        int targetPrefabHash,
        string targetPrefabName,
        RoutedDamageTargetKind targetKind)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));
        Target = target;
        TargetPrefabHash = targetPrefabHash;
        TargetPrefabName = targetPrefabName ?? string.Empty;
        TargetKind = targetKind;
    }

    internal HitData Hit { get; }

    internal ZDOID Target { get; }

    internal int TargetPrefabHash { get; }

    internal string TargetPrefabName { get; }

    internal RoutedDamageTargetKind TargetKind { get; }
}

internal readonly struct RoutedDamageTargetMetadata
{
    internal RoutedDamageTargetMetadata(
        int prefabHash,
        string prefabName,
        RoutedDamageTargetKind kind)
    {
        PrefabHash = prefabHash;
        PrefabName = prefabName ?? string.Empty;
        Kind = kind;
    }

    internal int PrefabHash { get; }

    internal string PrefabName { get; }

    internal RoutedDamageTargetKind Kind { get; }
}

/// <summary>
/// Pure gameplay-limit predicates, shared raw-damage measurement, and the
/// bounded server-side decoder for the vanilla routed RPC_Damage envelope.
/// The configured limits are server-owned; client observations remain defense
/// in depth rather than remote attestation.
/// </summary>
internal static class GameplayLimitValidation
{
    private const int MaximumRoutedDamagePacketBytes = 4096;
    private const int MaximumDamageParameterBytes = 2048;
    private static readonly Dictionary<int, RoutedDamageTargetMetadata>
        KnownDamagePrefabs = new();
    private static readonly HashSet<int> NonDamagePrefabs = new();
    private static int? _damageMethodHash;

    internal static bool ExceedsCarryWeight(
        float effectiveCarryWeight,
        float baseCarryWeight,
        float maximumCarryWeight)
    {
        ValidateLimit(maximumCarryWeight);
        return !IsFinite(effectiveCarryWeight) ||
               !IsFinite(baseCarryWeight) ||
               effectiveCarryWeight > maximumCarryWeight ||
               baseCarryWeight > maximumCarryWeight;
    }

    internal static bool ExceedsDamage(
        HitData? hit,
        float maximumDamage)
    {
        ValidateLimit(maximumDamage);
        if (hit == null)
        {
            return false;
        }

        return !IsFinite(hit.m_backstabBonus) ||
               hit.m_backstabBonus < 0f ||
               !TrySumDamageComponents(hit.m_damage, out double total) ||
               ExceedsPotentialDamage(
                   total,
                   hit.m_backstabBonus,
                   maximumDamage);
    }

    // Administrator exceptions affect only finite numerical caps. Invalid
    // components/multipliers remain invalid even when no cap is enforced.
    internal static bool IsValidDamage(HitData? hit) =>
        hit != null && IsFinite(hit.m_backstabBonus) &&
        hit.m_backstabBonus >= 0f &&
        TrySumDamageComponents(hit.m_damage, out _);

    internal static bool ShouldBlockDamage(HitData? hit, float maximumDamage,
        bool hasAdminBypass) =>
        ExceedsDamage(hit, maximumDamage) &&
        (!hasAdminBypass || !IsValidDamage(hit));

    internal static bool IsValidCarryWeight(float effectiveCarryWeight,
        float baseCarryWeight) =>
        IsFinite(effectiveCarryWeight) && IsFinite(baseCarryWeight);

    internal static bool TryMeasureRawDamage(
        HitData? hit,
        out double rawDamage)
    {
        rawDamage = 0d;
        return hit != null &&
               TrySumDamageComponents(hit.m_damage, out rawDamage) &&
               rawDamage > 0d;
    }

    private static bool TrySumDamageComponents(
        HitData.DamageTypes damage,
        out double total)
    {
        return TrySumDamageComponents(
            damage.m_damage,
            damage.m_blunt,
            damage.m_slash,
            damage.m_pierce,
            damage.m_chop,
            damage.m_pickaxe,
            damage.m_fire,
            damage.m_frost,
            damage.m_lightning,
            damage.m_poison,
            damage.m_spirit,
            out total);
    }

    internal static bool ExceedsDamageComponents(
        float maximumDamage,
        float backstabMultiplier,
        float generic,
        float blunt,
        float slash,
        float pierce,
        float chop,
        float pickaxe,
        float fire,
        float frost,
        float lightning,
        float poison,
        float spirit)
    {
        ValidateLimit(maximumDamage);
        if (!IsFinite(backstabMultiplier) || backstabMultiplier < 0f)
        {
            return true;
        }

        return !TrySumDamageComponents(
                   generic,
                   blunt,
                   slash,
                   pierce,
                   chop,
                   pickaxe,
                   fire,
                   frost,
                   lightning,
                   poison,
                   spirit,
                   out double total) ||
               ExceedsPotentialDamage(
                   total,
                   backstabMultiplier,
                   maximumDamage);
    }

    private static bool TrySumDamageComponents(
        float generic,
        float blunt,
        float slash,
        float pierce,
        float chop,
        float pickaxe,
        float fire,
        float frost,
        float lightning,
        float poison,
        float spirit,
        out double total)
    {
        total = 0d;
        return !InvalidComponent(generic, ref total) &&
               !InvalidComponent(blunt, ref total) &&
               !InvalidComponent(slash, ref total) &&
               !InvalidComponent(pierce, ref total) &&
               !InvalidComponent(chop, ref total) &&
               !InvalidComponent(pickaxe, ref total) &&
               !InvalidComponent(fire, ref total) &&
               !InvalidComponent(frost, ref total) &&
               !InvalidComponent(lightning, ref total) &&
               !InvalidComponent(poison, ref total) &&
               !InvalidComponent(spirit, ref total);
    }

    internal static RoutedDamageInspection InspectRoutedDamage(
        ZPackage? package,
        out RoutedDamageObservation? observation)
    {
        observation = null;
        if (package == null)
        {
            return RoutedDamageInspection.NotDamage;
        }

        int originalPosition;
        bool knownDamageCandidate = false;
        try
        {
            originalPosition = package.GetPos();
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return RoutedDamageInspection.NotDamage;
        }

        try
        {
            // RoutedRPCData: msg ID, claimed sender, target peer, target ZDO,
            // method hash, then a length-prefixed parameter package.
            package.ReadLong();
            package.ReadLong();
            package.ReadLong();
            ZDOID target = package.ReadZDOID();
            int methodHash = package.ReadInt();
            // Do not call ZPackage.ReadPackage here. Vanilla allocates the
            // declared inner length before checking it against the remaining
            // outer bytes, which would turn this security prefix into an
            // attacker-controlled allocation primitive.
            int parameterLength = package.ReadInt();
            int remainingOuterBytes = package.Size() - package.GetPos();
            if (parameterLength < 0 ||
                parameterLength != remainingOuterBytes)
            {
                return RoutedDamageInspection.MalformedRoutedRpc;
            }

            int damageMethodHash = _damageMethodHash ??=
                StringExtensionMethods.GetStableHashCode("RPC_Damage");
            if (methodHash != damageMethodHash ||
                target.IsNone() ||
                !TryResolveDamageTarget(
                    target,
                    out RoutedDamageTargetMetadata targetMetadata))
            {
                return RoutedDamageInspection.NotDamage;
            }

            knownDamageCandidate = true;
            if (package.Size() - originalPosition >
                    MaximumRoutedDamagePacketBytes ||
                parameterLength > MaximumDamageParameterBytes)
            {
                return RoutedDamageInspection.MalformedDamage;
            }

            byte[] parameterBytes = package.ReadByteArray(parameterLength);
            if (parameterBytes.Length != parameterLength ||
                package.GetPos() != package.Size())
            {
                return RoutedDamageInspection.MalformedDamage;
            }

            ZPackage parameters = new(parameterBytes);

            HitData decoded = new();
            decoded.Deserialize(ref parameters);
            int remaining = parameters.Size() - parameters.GetPos();
            // MineRock5 appends one hit-area index; the other five vanilla
            // RPC_Damage registrations contain only HitData.
            if (remaining != 0 && remaining != sizeof(int))
            {
                return RoutedDamageInspection.MalformedDamage;
            }

            observation = new RoutedDamageObservation(
                decoded,
                target,
                targetMetadata.PrefabHash,
                targetMetadata.PrefabName,
                targetMetadata.Kind);
            return RoutedDamageInspection.Damage;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return knownDamageCandidate
                ? RoutedDamageInspection.MalformedDamage
                : RoutedDamageInspection.MalformedRoutedRpc;
        }
        finally
        {
            try
            {
                package.SetPos(originalPosition);
            }
            catch (Exception exception)
                when (!IntegrityCanonical.IsFatal(exception))
            {
                // The original RPC handler will perform its own validation.
            }
        }
    }

    private static bool TryResolveDamageTarget(
        ZDOID target,
        out RoutedDamageTargetMetadata metadata)
    {
        metadata = default;
        try
        {
            ZDOMan? manager = ZDOMan.instance;
            ZNetScene? scene = ZNetScene.instance;
            if (manager == null || scene == null)
            {
                return false;
            }

            ZDO? zdo = manager.GetZDO(target);
            if (zdo == null)
            {
                return false;
            }

            int prefabHash = zdo.GetPrefab();
            if (KnownDamagePrefabs.TryGetValue(
                    prefabHash,
                    out RoutedDamageTargetMetadata known))
            {
                metadata = known;
                return true;
            }

            if (NonDamagePrefabs.Contains(prefabHash))
            {
                return false;
            }

            GameObject? prefab = scene.GetPrefab(prefabHash);
            if (prefab == null)
            {
                return false;
            }

            if (!TryClassifyDamageTarget(
                    prefab,
                    out RoutedDamageTargetKind kind))
            {
                NonDamagePrefabs.Add(prefabHash);
                return false;
            }

            metadata = new RoutedDamageTargetMetadata(
                prefabHash,
                prefab.name,
                kind);
            KnownDamagePrefabs[prefabHash] = metadata;
            return true;
        }
        catch (Exception exception)
            when (!IntegrityCanonical.IsFatal(exception))
        {
            return false;
        }
    }

    private static bool TryClassifyDamageTarget(
        GameObject prefab,
        out RoutedDamageTargetKind kind)
    {
        if (prefab.GetComponent<Character>() != null)
        {
            kind = RoutedDamageTargetKind.Character;
            return true;
        }

        if (prefab.GetComponent<WearNTear>() != null)
        {
            kind = RoutedDamageTargetKind.Structure;
            return true;
        }

        if (prefab.GetComponent<MineRock5>() != null)
        {
            kind = RoutedDamageTargetKind.MineRock;
            return true;
        }

        if (prefab.GetComponent<Destructible>() != null)
        {
            kind = RoutedDamageTargetKind.Destructible;
            return true;
        }

        if (prefab.GetComponent<TreeLog>() != null)
        {
            kind = RoutedDamageTargetKind.TreeLog;
            return true;
        }

        if (prefab.GetComponent<TreeBase>() != null)
        {
            kind = RoutedDamageTargetKind.TreeBase;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool InvalidComponent(float value, ref double total)
    {
        // Negative components can cancel a large positive component in a raw
        // sum, so reject them rather than permitting a cancellation bypass.
        if (!IsFinite(value) || value < 0f)
        {
            return true;
        }

        total += value;
        return double.IsNaN(total) || double.IsInfinity(total);
    }

    private static bool ExceedsPotentialDamage(
        double rawDamage,
        float backstabMultiplier,
        float maximumDamage)
    {
        double potentialDamage =
            rawDamage * Math.Max(1d, backstabMultiplier);
        return double.IsNaN(potentialDamage) ||
               double.IsInfinity(potentialDamage) ||
               potentialDamage > maximumDamage;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void ValidateLimit(float value)
    {
        if (!IsFinite(value) || value < 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
