using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ItemType = ItemDrop.ItemData.ItemType;

namespace ServerManager
{
    public sealed partial class ValheimPlayerProfileCodec
    {
        /// <summary>
        /// Builds only the first authoritative profile. All work is detached
        /// data: no Player, ItemDrop, ZNetView, or world object is instantiated.
        /// START ITEMS is the complete initial inventory, including an empty
        /// materialized inventory when the configured list is explicitly empty.
        /// The resulting ordinary Player.Load inventory is the grant, so a
        /// reconnect/reload cannot replay a login-time AddItem operation.
        /// </summary>
        internal byte[] CreateInitialProfileBytes(
            string canonicalCharacterName, ServerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Player template = GetStarterPlayerTemplate();
            Inventory inventory = CreateStarterInventory(template, settings);
            PlayerProfile profile = CreateEmptyProfile(canonicalCharacterName);
            ProfilePrivateAccess.SetPlayerData(profile,
                SerializeStarterPlayerData(template, inventory));
            // The same parser used by save admission validates the entire
            // profile before the repository can persist or publish any bytes.
            return SerializeProfileToBytes(profile);
        }

        internal void ValidateStartItems(ServerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Player template = GetStarterPlayerTemplate();
            Inventory inventory = CreateStarterInventory(template, settings);
            _ = ParseAndValidateInnerPlayerData(
                SerializeStarterPlayerData(template, inventory));
        }

        internal static bool IsStartItemsCatalogReady(ServerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return IsStarterCatalogLoaded();
        }

        private static bool IsStarterCatalogLoaded()
        {
            ObjectDB catalog = ObjectDB.instance;
            if (catalog == null || catalog.m_items == null || catalog.m_items.Count == 0 ||
                Game.instance == null || Game.instance.m_playerPrefab == null)
                return false;
            // A constructed ObjectDB is not necessarily registered yet. Avoid
            // treating its empty lookup dictionary as invalid administrator data.
            foreach (GameObject prefab in catalog.m_items)
            {
                if (prefab != null)
                    return catalog.GetItemPrefab(prefab.name) == prefab;
            }
            return false;
        }

        private static Player GetStarterPlayerTemplate()
        {
            if (!IsStarterCatalogLoaded())
                throw new InvalidOperationException(
                    "START ITEMS validation requires the loaded server item database and player prefab.");
            Player template = Game.instance.m_playerPrefab.GetComponent<Player>();
            if (template == null)
                throw new InvalidDataException(
                    "START ITEMS requires a valid player prefab.");
            return template;
        }

        private Inventory CreateStarterInventory(Player template, ServerSettings settings)
        {
            // Only dimensions come from the prefab. Its inventory contents,
            // default items and randomized defaults are deliberately ignored:
            // a materialized Player.Load payload replaces vanilla first-spawn
            // equipment, even when the configured inventory is empty.
            Inventory source = template.GetInventory();
            if (source == null || source.GetWidth() < 1 || source.GetHeight() < 1 ||
                source.GetWidth() > 255 || source.GetHeight() > 255 ||
                (long)source.GetWidth() * source.GetHeight() > 256)
                throw new InvalidDataException(
                    "START ITEMS requires bounded player-prefab inventory dimensions.");

            Inventory staged = new Inventory("Server starter inventory", null,
                source.GetWidth(), source.GetHeight());
            CharacterSemanticPolicy policy = new CharacterSemanticPolicy(
                CharacterSemanticPolicyMode.Enforce, settings.ForbiddenItemPrefabs,
                settings.MaximumHealth, settings.MaximumStamina, settings.MaximumEitr,
                2f, 10f);
            Dictionary<string, long> expectedCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> entry in settings.StartItems.OrderBy(
                value => value.Key, StringComparer.Ordinal))
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(entry.Key);
                ItemDrop.ItemData definition = GetStarterItemDefinition(prefab, policy);
                if (!string.Equals(prefab.name, entry.Key, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "START ITEMS prefab names must match the item database exactly.");
                AddStarterItem(staged, prefab, definition, entry.Value, expectedCounts);
            }
            // Inventory's native stack merger uses shared item names. Reject
            // alias/collision cases instead of silently changing a prefab grant.
            Dictionary<string, long> actualCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            HashSet<ItemType> equippedSlots = new HashSet<ItemType>();
            foreach (ItemDrop.ItemData item in staged.GetAllItems())
            {
                string name = item.m_dropPrefab.name;
                actualCounts.TryGetValue(name, out long count);
                actualCounts[name] = checked(count + item.m_stack);
                // Persist the intent in the first profile, not a later login
                // grant. Player.Load performs the real equipment checks. Keep
                // hand items unequipped and one wearable per vanilla slot;
                // the existing ordinal prefab order makes conflicts stable.
                ItemType type = item.m_shared.m_itemType;
                if (type is ItemType.Helmet or ItemType.Chest or ItemType.Legs or
                    ItemType.Shoulder or ItemType.Utility or ItemType.Trinket)
                    item.m_equipped = equippedSlots.Add(type);
            }
            if (actualCounts.Count != expectedCounts.Count ||
                expectedCounts.Any(pair => !actualCounts.TryGetValue(pair.Key, out long count) || count != pair.Value))
                throw new InvalidDataException(
                    "START ITEMS contains conflicting item definitions that cannot preserve exact quantities.");
            _ = CaptureInventoryToBytes(staged);
            return staged;
        }

        private static ItemDrop.ItemData GetStarterItemDefinition(
            GameObject prefab, CharacterSemanticPolicy policy)
        {
            if (prefab == null)
                throw new InvalidDataException(
                    "START ITEMS references an unknown or invalid item prefab.");
            ItemDrop drop = prefab.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData?.m_shared == null ||
                ObjectDB.instance.GetItemPrefab(prefab.name) != prefab)
                throw new InvalidDataException(
                    "START ITEMS references an unknown or invalid item prefab.");
            if (policy.IsForbiddenItemPrefab(prefab.name))
                throw new InvalidDataException(
                    "START ITEMS conflicts with forbiddenItems: " + prefab.name + ".");
            ItemDrop.ItemData definition = drop.m_itemData;
            if (definition.m_shared.m_maxStackSize < 1 ||
                definition.m_shared.m_maxStackSize > 1000000 ||
                definition.m_shared.m_maxQuality < 1 ||
                definition.m_shared.m_icons == null || definition.m_shared.m_icons.Length == 0 ||
                definition.m_variant < 0 || definition.m_variant >= definition.m_shared.m_icons.Length ||
                definition.m_quality < 1 || definition.m_quality > definition.m_shared.m_maxQuality)
                throw new InvalidDataException(
                    "START ITEMS has invalid stack, quality or variant defaults: " + prefab.name + ".");
            float durability = definition.GetMaxDurability(definition.m_quality);
            if (float.IsNaN(durability) || float.IsInfinity(durability) || durability < 0)
                throw new InvalidDataException(
                    "START ITEMS has invalid durability defaults: " + prefab.name + ".");
            return definition;
        }

        private static void AddStarterItem(Inventory staged, GameObject prefab,
            ItemDrop.ItemData definition, int quantity,
            Dictionary<string, long> expectedCounts)
        {
            if (quantity < 1 || quantity > 1000000)
                throw new InvalidDataException("START ITEMS quantities must be positive and bounded.");
            expectedCounts.TryGetValue(prefab.name, out long previous);
            expectedCounts[prefab.name] = checked(previous + quantity);
            int remaining = quantity;
            while (remaining > 0)
            {
                ItemDrop.ItemData item = definition.Clone();
                item.m_dropPrefab = prefab;
                item.m_stack = Math.Min(remaining, definition.m_shared.m_maxStackSize);
                remaining -= item.m_stack;
                item.m_durability = item.GetMaxDurability(item.m_quality);
                item.m_worldLevel = (byte)Game.m_worldLevel;
                item.m_equipped = false;
                if (!staged.AddItem(item))
                    throw new InvalidDataException(
                        "START ITEMS exceeds the new player's inventory capacity.");
            }
        }

        // Exact current Player.Save schema (29); all progression starts empty.
        // Values come from the loaded player prefab rather than a fabricated
        // world player. The regular codec refuses any future schema change.
        private static byte[] SerializeStarterPlayerData(Player template, Inventory inventory)
        {
            ZPackage package = new ZPackage();
            package.Write(SupportedPlayerDataVersion);
            package.Write(template.m_baseHP);
            package.Write(template.m_baseHP);
            package.Write(template.m_baseStamina);
            package.Write(ReadStarterField<float>(template, "m_timeSinceDeath"));
            package.Write(ReadStarterField<string>(template, "m_guardianPower"));
            package.Write(ReadStarterField<float>(template, "m_guardianPowerCooldown"));
            inventory.Save(package);
            for (int index = 0; index < 8; ++index) package.Write(0);
            package.Write(template.GetBeard());
            package.Write(template.GetHair());
            package.Write(ReadStarterField<Vector3>(template, "m_skinColor"));
            package.Write(template.GetHairColor());
            package.Write(template.GetPlayerModel());
            package.Write(0); // active foods
            package.Write(2); // Skills.Save version
            package.Write(0); // no granted skills
            package.Write(0); // player custom data
            package.Write(template.m_baseStamina);
            package.Write(0f); // maximum eitr
            package.Write(0f); // current eitr
            return package.GetArray();
        }

        private static T ReadStarterField<T>(Player template, string name)
        {
            FieldInfo field = typeof(Player).GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(T) || !(field.GetValue(template) is T value))
                throw new NotSupportedException(
                    "Valheim changed its initial Player data schema: " + name + ".");
            return value;
        }
    }
}
