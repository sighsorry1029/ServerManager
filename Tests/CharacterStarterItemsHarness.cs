using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Inert fixtures for the source-linked production starter builder. Keep Unity
// and gameplay APIs absent: accidentally spawning a world object cannot compile.
namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; }
    public sealed class GameObject
    {
        public string name;
        public object Component;
        public T GetComponent<T>() where T : class { return Component as T; }
        public GameObject(string name, object component) { this.name = name; Component = component; }
    }
}
public sealed class ItemDrop
{
    public ItemData m_itemData = new ItemData();
    public sealed class ItemData
    {
        // Values checked against installed vanilla metadata by the smoke script.
        public enum ItemType
        {
            None = 0, Material = 1, Consumable = 2, OneHandedWeapon = 3,
            Bow = 4, Shield = 5, Helmet = 6, Chest = 7, Ammo = 9,
            Customization = 10, Legs = 11, Hands = 12, Trophy = 13,
            TwoHandedWeapon = 14, Torch = 15, Misc = 16, Shoulder = 17,
            Utility = 18, Tool = 19, Attach_Atgeir = 20, Fish = 21,
            TwoHandedWeaponLeft = 22, AmmoNonEquipable = 23, Trinket = 24
        }
        public SharedData m_shared = new SharedData();
        public Dictionary<string, string> m_customData = new Dictionary<string, string>();
        public UnityEngine.GameObject m_dropPrefab;
        public int m_stack = 1, m_quality = 1, m_variant, m_worldLevel;
        public float m_durability;
        public bool m_equipped;
        public bool Weapon, Equipable;
        public ItemData Clone()
        {
            var clone = (ItemData)MemberwiseClone();
            clone.m_customData = new Dictionary<string, string>(m_customData);
            return clone;
        }
        public float GetMaxDurability(int quality) { return 100f; }
        public bool IsWeapon() { return Weapon; }
        public bool IsEquipable() { return Equipable; }
    }
    public sealed class SharedData
    {
        public ItemData.ItemType m_itemType = ItemData.ItemType.Material;
        public int m_maxStackSize = 50, m_maxQuality = 1;
        public object[] m_icons = new object[1];
        public string Name;
    }
}
public sealed class Inventory
{
    public static bool RequireUnequippedOnAdd;
    public static List<ItemDrop.ItemData> LastSavedItems = new List<ItemDrop.ItemData>();
    readonly int width, height;
    readonly List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
    public Inventory(string name, object icon, int width, int height) { this.width = width; this.height = height; }
    public int GetWidth() { return width; }
    public int GetHeight() { return height; }
    public List<ItemDrop.ItemData> GetAllItems() { return items; }
    public bool AddItem(ItemDrop.ItemData item)
    {
        if (RequireUnequippedOnAdd && item.m_equipped)
            throw new Exception("Starter clones must remain unequipped until all staging/count checks pass.");
        foreach (var existing in items)
        {
            if (existing.m_shared.Name != item.m_shared.Name) continue;
            int added = Math.Min(item.m_stack, existing.m_shared.m_maxStackSize - existing.m_stack);
            existing.m_stack += added; item.m_stack -= added;
            if (item.m_stack == 0) return true;
        }
        if (items.Count == width * height) return false;
        items.Add(item); return true;
    }
    public void Save(ZPackage package)
    {
        LastSavedItems = new List<ItemDrop.ItemData>(items);
        package.Write(106); package.Write(items.Count);
        foreach (var item in items)
        {
            package.Write(item.m_dropPrefab.name); package.Write(item.m_stack);
            package.Write(item.m_equipped);
            package.Write(item.m_customData.Count);
            foreach (var entry in item.m_customData.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                package.Write(entry.Key); package.Write(entry.Value);
            }
        }
    }
}
public sealed class Player
{
    public float m_baseHP = 25f, m_baseStamina = 75f;
    public float m_timeSinceDeath = 999999f, m_guardianPowerCooldown;
    public string m_guardianPower = "";
    public UnityEngine.Vector3 m_skinColor;
    public UnityEngine.GameObject[] m_defaultItems = new UnityEngine.GameObject[0];
    public object[] m_randomWeapon = new object[0], m_randomArmor = new object[0],
        m_randomShield = new object[0], m_randomSets = new object[0], m_randomItems = new object[0];
    public Inventory Inventory = new Inventory("fixture", null, 8, 4);
    public Inventory GetInventory() { return Inventory; }
    public string GetBeard() { return ""; }
    public string GetHair() { return ""; }
    public UnityEngine.Vector3 GetHairColor() { return new UnityEngine.Vector3(); }
    public int GetPlayerModel() { return 0; }
}
public sealed class Game
{
    public static Game instance;
    public static int m_worldLevel;
    public UnityEngine.GameObject m_playerPrefab;
}
public sealed class ObjectDB
{
    public static ObjectDB instance;
    public readonly List<UnityEngine.GameObject> m_items = new List<UnityEngine.GameObject>();
    public readonly Dictionary<string, UnityEngine.GameObject> Items = new Dictionary<string, UnityEngine.GameObject>();
    public UnityEngine.GameObject GetItemPrefab(string name) { return Items.TryGetValue(name, out var value) ? value : null; }
}
public sealed class PlayerProfile { public byte[] Data; }
public sealed class ZPackage
{
    readonly MemoryStream stream = new MemoryStream();
    readonly BinaryWriter writer;
    public ZPackage() { writer = new BinaryWriter(stream); }
    public void Write(int value) { writer.Write(value); }
    public void Write(float value) { writer.Write(value); }
    public void Write(bool value) { writer.Write(value); }
    public void Write(string value) { writer.Write(value); }
    public void Write(byte[] value) { writer.Write(value.Length); writer.Write(value); }
    public void Write(UnityEngine.Vector3 value) { Write(value.x); Write(value.y); Write(value.z); }
    public byte[] GetArray() { writer.Flush(); return stream.ToArray(); }
}
namespace ServerManager
{
    internal sealed class ServerSettings
    {
        public Dictionary<string, int> StartItems = new Dictionary<string, int>();
        public string ForbiddenItemPrefabs = "";
        public float MaximumHealth = 1000, MaximumStamina = 1000, MaximumEitr = 1000;
    }
    public enum CharacterSemanticPolicyMode { Enforce }
    public sealed class CharacterSemanticPolicy
    {
        readonly HashSet<string> forbidden;
        public CharacterSemanticPolicy(CharacterSemanticPolicyMode mode, string names, float health,
            float stamina, float eitr, float burst, float rate) { forbidden = new HashSet<string>(names.Split(',')); }
        public bool IsForbiddenItemPrefab(string name) { return forbidden.Contains(name); }
    }
    public sealed partial class ValheimPlayerProfileCodec
    {
        public const int SupportedPlayerDataVersion = 33;
        public byte[] CreateEmptyProfileBytes(string name) { throw new Exception("Unmaterialized first-spawn fallback is forbidden."); }
        static PlayerProfile CreateEmptyProfile(string name) { return new PlayerProfile(); }
        static class ProfilePrivateAccess { public static void SetPlayerData(PlayerProfile profile, byte[] data) { profile.Data = data; } }
        static byte[] SerializeProfileToBytes(PlayerProfile profile) { return profile.Data; }
        object ParseAndValidateInnerPlayerData(byte[] data) { CharacterStarterItemsHarness.ReadInventory(data); return null; }
        public void ValidateInventorySnapshot(byte[] data) { if (data.Length == 0) throw new Exception("empty inventory"); }
        public byte[] CaptureInventoryToBytes(Inventory inventory) { var p = new ZPackage(); inventory.Save(p); return p.GetArray(); }
    }
}
public static class CharacterStarterItemsHarness
{
    static int assertions;
    static void Check(bool condition, string message) { ++assertions; if (!condition) throw new Exception(message); }
    static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { ++assertions; return; }
        throw new Exception("Expected rejection " + typeof(T).Name);
    }
    static UnityEngine.GameObject Item(string name, int stackLimit = 50, int stack = 1,
        ItemDrop.ItemData.ItemType itemType = ItemDrop.ItemData.ItemType.Material)
    {
        var drop = new ItemDrop(); drop.m_itemData.m_shared.Name = name;
        drop.m_itemData.m_shared.m_itemType = itemType;
        drop.m_itemData.m_shared.m_maxStackSize = stackLimit; drop.m_itemData.m_stack = stack;
        var prefab = new UnityEngine.GameObject(name, drop); ObjectDB.instance.Items.Add(name, prefab);
        ObjectDB.instance.m_items.Add(prefab); return prefab;
    }
    public sealed class SavedItem
    {
        public string Name;
        public int Stack;
        public bool Equipped;
        public Dictionary<string, string> CustomData;
    }
    static bool IsWearable(ItemDrop.ItemData.ItemType type)
    {
        return type == ItemDrop.ItemData.ItemType.Helmet || type == ItemDrop.ItemData.ItemType.Chest ||
            type == ItemDrop.ItemData.ItemType.Legs || type == ItemDrop.ItemData.ItemType.Shoulder ||
            type == ItemDrop.ItemData.ItemType.Utility || type == ItemDrop.ItemData.ItemType.Trinket;
    }
    public static Dictionary<string, int> ReadInventory(byte[] data)
    {
        var result = new Dictionary<string, int>();
        foreach (var item in ReadInventoryItems(data))
        {
            result.TryGetValue(item.Name, out int previous); result[item.Name] = previous + item.Stack;
        }
        return result;
    }
    static List<SavedItem> ReadInventoryItems(byte[] data)
    {
        using (var reader = new BinaryReader(new MemoryStream(data)))
        {
            Check(reader.ReadInt32() == 33, "Player schema");
            Check(reader.ReadSingle() == 25 && reader.ReadSingle() == 25 && reader.ReadSingle() == 75, "new player stats");
            reader.ReadSingle(); reader.ReadString(); reader.ReadSingle();
            Check(reader.ReadInt32() == 106, "inventory schema");
            var result = new List<SavedItem>(); int count = reader.ReadInt32();
            var equippedSlots = new HashSet<ItemDrop.ItemData.ItemType>();
            for (int i = 0; i < count; ++i)
            {
                string name = reader.ReadString(); int stack = reader.ReadInt32();
                bool equipped = reader.ReadBoolean();
                var definition = ObjectDB.instance.GetItemPrefab(name).GetComponent<ItemDrop>().m_itemData;
                Check(!equipped || (IsWearable(definition.m_shared.m_itemType) &&
                    equippedSlots.Add(definition.m_shared.m_itemType)), "only one wearable may occupy each supported slot");
                Check(stack > 0 && stack <= definition.m_shared.m_maxStackSize, "stack split");
                var custom = new Dictionary<string, string>();
                int customCount = reader.ReadInt32();
                for (int entry = 0; entry < customCount; ++entry) custom.Add(reader.ReadString(), reader.ReadString());
                Check(custom.Count == definition.m_customData.Count && custom.All(pair =>
                    definition.m_customData.TryGetValue(pair.Key, out string value) && value == pair.Value),
                    "starter clones must preserve prefab custom data");
                result.Add(new SavedItem { Name = name, Stack = stack, Equipped = equipped, CustomData = custom });
            }
            for (int i = 0; i < 8; ++i) Check(reader.ReadInt32() == 0, "empty progression collections");
            reader.ReadString(); reader.ReadString();
            for (int i = 0; i < 6; ++i) reader.ReadSingle();
            Check(reader.ReadInt32() == 0 && reader.ReadInt32() == 0 && reader.ReadInt32() == 2 && reader.ReadInt32() == 0 && reader.ReadInt32() == 0, "no food, skills or custom data granted");
            Check(reader.ReadSingle() == 75 && reader.ReadSingle() == 0 && reader.ReadSingle() == 0 && reader.ReadInt32() == 0 && reader.BaseStream.Position == reader.BaseStream.Length, "complete bounded Player payload");
            return result;
        }
    }
    public static void Run()
    {
        var codec = new ServerManager.ValheimPlayerProfileCodec();
        var empty = new ServerManager.ServerSettings();
        Check(!ServerManager.ValheimPlayerProfileCodec.IsStartItemsCatalogReady(empty), "empty plan must also wait for catalog/player");
        Reject<InvalidOperationException>(() => codec.CreateInitialProfileBytes("New", empty));
        Reject<InvalidOperationException>(() => codec.ValidateStartItems(empty));
        var plan = new ServerManager.ServerSettings(); plan.StartItems.Add("Wood", 63);
        Check(!ServerManager.ValheimPlayerProfileCodec.IsStartItemsCatalogReady(plan), "defer absent catalog");
        Reject<InvalidOperationException>(() => codec.ValidateStartItems(plan));
        ObjectDB.instance = new ObjectDB(); var player = new Player();
        Game.instance = new Game { m_playerPrefab = new UnityEngine.GameObject("Player", player) };
        Check(!ServerManager.ValheimPlayerProfileCodec.IsStartItemsCatalogReady(plan), "defer empty item registrations");
        var wood = Item("Wood", 50, 2); var rags = Item("Rags", 1);
        var originalRags = rags.GetComponent<ItemDrop>().m_itemData;
        Check(player.Inventory.AddItem(originalRags), "populate a prefab inventory fixture");
        Inventory.RequireUnequippedOnAdd = true;
        player.m_defaultItems = new[] { rags, wood, null,
            new UnityEngine.GameObject("InvalidVanillaDefault", new object()) };
        player.m_randomWeapon = new object[1]; player.m_randomArmor = new object[1];
        player.m_randomShield = new object[1]; player.m_randomSets = new object[1];
        player.m_randomItems = new object[1];
        Check(ServerManager.ValheimPlayerProfileCodec.IsStartItemsCatalogReady(empty), "loaded catalog permits an empty configured list");
        var emptyInventory = ReadInventory(codec.CreateInitialProfileBytes("New", empty));
        Check(emptyInventory.Count == 0, "explicitly empty list must materialize an empty inventory without vanilla defaults");
        codec.ValidateStartItems(empty);
        var starterKit = new ServerManager.ServerSettings();
        foreach (var entry in new[] {
            ("HelmetMidsummerCrown", ItemDrop.ItemData.ItemType.Helmet),
            ("ArmorRagsChest", ItemDrop.ItemData.ItemType.Chest),
            ("ArmorRagsLegs", ItemDrop.ItemData.ItemType.Legs),
            ("Torch", ItemDrop.ItemData.ItemType.Torch) })
        {
            var prefab = Item(entry.Item1, 1, 1, entry.Item2);
            // Nonwearable prefab equipment flags must still be cleared.
            prefab.GetComponent<ItemDrop>().m_itemData.m_equipped = entry.Item2 == ItemDrop.ItemData.ItemType.Torch;
            starterKit.StartItems.Add(entry.Item1, 1);
        }
        codec.ValidateStartItems(starterKit);
        var starterInventory = ReadInventory(codec.CreateInitialProfileBytes("New", starterKit));
        Check(starterInventory.Count == 4 && starterKit.StartItems.All(item =>
            starterInventory.TryGetValue(item.Key, out int amount) && amount == 1),
            "four-item starter kit materializes exactly one each without vanilla/default inventory");
        var starterFlags = ReadInventoryItems(codec.CreateInitialProfileBytes("New", starterKit));
        Check(starterFlags.Where(item => item.Name != "Torch").All(item => item.Equipped) &&
            !starterFlags.Single(item => item.Name == "Torch").Equipped,
            "default crown, chest and legs auto-equip, but the granted torch stays unequipped");
        Check(!ObjectDB.instance.GetItemPrefab("HelmetMidsummerCrown").GetComponent<ItemDrop>().m_itemData.m_equipped &&
            !ObjectDB.instance.GetItemPrefab("ArmorRagsChest").GetComponent<ItemDrop>().m_itemData.m_equipped &&
            !ObjectDB.instance.GetItemPrefab("ArmorRagsLegs").GetComponent<ItemDrop>().m_itemData.m_equipped &&
            ObjectDB.instance.GetItemPrefab("Torch").GetComponent<ItemDrop>().m_itemData.m_equipped,
            "default equipment selection must not mutate any source prefab's flag");
        ExerciseWearableSelection(codec, player, originalRags);
        wood.GetComponent<ItemDrop>().m_itemData.m_equipped = true;
        var result = ReadInventory(codec.CreateInitialProfileBytes("New", plan));
        Check(result.Count == 1 && result["Wood"] == 63, "only configured items may appear, ignoring all vanilla/default/prefab inventory items");
        Check(player.Inventory.GetAllItems().Count == 1 && ReferenceEquals(player.Inventory.GetAllItems()[0], originalRags) &&
            wood.GetComponent<ItemDrop>().m_itemData.m_stack == 2 && wood.GetComponent<ItemDrop>().m_itemData.m_equipped,
            "only detached clones may change");
        Check(codec.CreateInitialProfileBytes("New", plan).SequenceEqual(codec.CreateInitialProfileBytes("New", plan)), "retry template must not accumulate inventory");
        plan.StartItems["Wood"] = 1000000;
        Reject<InvalidDataException>(() => codec.CreateInitialProfileBytes("New", plan));
        Check(player.Inventory.GetAllItems().Count == 1 && ReferenceEquals(player.Inventory.GetAllItems()[0], originalRags), "no-space leaves source untouched");
        plan.StartItems["Wood"] = 63; plan.ForbiddenItemPrefabs = "Wood";
        Reject<InvalidDataException>(() => codec.ValidateStartItems(plan));
        plan.ForbiddenItemPrefabs = "Rags";
        codec.ValidateStartItems(plan);
        Check(true, "forbidden vanilla defaults are irrelevant to the configured inventory override");
        plan.ForbiddenItemPrefabs = ""; plan.StartItems["Missing"] = 1;
        Reject<InvalidDataException>(() => codec.ValidateStartItems(plan)); plan.StartItems.Remove("Missing");
        plan.StartItems["Wood"] = 0;
        Reject<InvalidDataException>(() => codec.ValidateStartItems(plan)); plan.StartItems["Wood"] = 1;
        var alias = Item("WoodAlias"); alias.GetComponent<ItemDrop>().m_itemData.m_shared.Name = "Wood";
        plan.StartItems.Add("WoodAlias", 1);
        Reject<InvalidDataException>(() => codec.ValidateStartItems(plan)); plan.StartItems.Remove("WoodAlias");
        wood.GetComponent<ItemDrop>().m_itemData.m_shared.m_icons = new object[0];
        Reject<InvalidDataException>(() => codec.ValidateStartItems(plan));
        // Empty plans ignore invalid/unselected catalog definitions, but still
        // obey the live player prefab's bounded inventory dimensions.
        codec.ValidateStartItems(empty);
        player.Inventory = new Inventory("invalid dimensions", null, 0, 4);
        Reject<InvalidDataException>(() => codec.ValidateStartItems(empty));
        player.Inventory = new Inventory("bounded", null, 8, 4);
        Game.instance.m_playerPrefab = null;
        Check(!ServerManager.ValheimPlayerProfileCodec.IsStartItemsCatalogReady(empty), "empty plan must wait for player prefab");
        Reject<InvalidOperationException>(() => codec.ValidateStartItems(empty));
        Console.WriteLine("START ITEMS builder assertions: " + assertions);
    }

    static void ExerciseWearableSelection(ServerManager.ValheimPlayerProfileCodec codec, Player player,
        ItemDrop.ItemData originalInventoryItem)
    {
        var plan = new ServerManager.ServerSettings();
        var definitions = new List<ItemDrop.ItemData>();
        // Intentionally reverse/case-mix insertion order: ordinal prefab order,
        // not YAML/dictionary order or broad IsEquipable(), selects the winner.
        var wearables = new[] {
            ("aHelmet", ItemDrop.ItemData.ItemType.Helmet, 2),
            ("ZHelmet", ItemDrop.ItemData.ItemType.Helmet, 2),
            ("AHelmet", ItemDrop.ItemData.ItemType.Helmet, 3),
            ("TestChest", ItemDrop.ItemData.ItemType.Chest, 1),
            ("TestLegs", ItemDrop.ItemData.ItemType.Legs, 1),
            ("TestShoulder", ItemDrop.ItemData.ItemType.Shoulder, 1),
            ("TestUtility", ItemDrop.ItemData.ItemType.Utility, 1),
            ("TestTrinket", ItemDrop.ItemData.ItemType.Trinket, 1) };
        foreach (var entry in wearables)
        {
            var definition = Item(entry.Item1, 1, 1, entry.Item2).GetComponent<ItemDrop>().m_itemData;
            definitions.Add(definition); plan.StartItems.Add(entry.Item1, entry.Item3);
        }
        foreach (ItemDrop.ItemData.ItemType type in Enum.GetValues(typeof(ItemDrop.ItemData.ItemType)))
        {
            if (IsWearable(type)) continue;
            string name = "Excluded" + type;
            definitions.Add(Item(name, 1, 1, type).GetComponent<ItemDrop>().m_itemData);
            plan.StartItems.Add(name, 1);
        }
        definitions.Add(Item("ExcludedUnknown", 1, 1, (ItemDrop.ItemData.ItemType)999).GetComponent<ItemDrop>().m_itemData);
        plan.StartItems.Add("ExcludedUnknown", 1);
        foreach (var definition in definitions)
        {
            definition.m_equipped = true;
            definition.m_durability = 17.5f;
            definition.m_worldLevel = 7;
            definition.m_customData["fixture-owner"] = definition.m_shared.Name;
            definition.m_customData["fixture-value"] = "unchanged <wearable> metadata";
            // These broad flags must have no influence on wearable-slot policy.
            definition.Equipable = !IsWearable(definition.m_shared.m_itemType);
        }
        codec.ValidateStartItems(plan);
        byte[] first = codec.CreateInitialProfileBytes("New", plan);
        var entries = ReadInventoryItems(first);
        var counts = ReadInventory(first);
        Check(counts.Count == plan.StartItems.Count && plan.StartItems.All(pair => counts[pair.Key] == pair.Value),
            "auto-equipping must preserve every configured prefab and exact quantity");
        Check(entries.Count == 31 && entries.Count(item => item.Equipped) == 6,
            "all six wearable slots equip once while every other item type remains unequipped");
        foreach (var slot in wearables.Select(item => item.Item2).Distinct())
            Check(entries.Count(item => item.Equipped && ObjectDB.instance.GetItemPrefab(item.Name)
                .GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType == slot) == 1,
                "one equipped item for slot " + slot);
        Check(entries.Count(item => item.Name == "AHelmet") == 3 &&
            entries.Count(item => item.Name == "AHelmet" && item.Equipped) == 1 &&
            entries.Where(item => item.Name == "ZHelmet" || item.Name == "aHelmet").All(item => !item.Equipped),
            "only the first ordinal prefab and one of its multiple copies can occupy a duplicate slot");
        Check(entries.Where(item => item.Name.StartsWith("Excluded", StringComparison.Ordinal)).All(item => !item.Equipped),
            "weapons, shield, torch, material, unknown and every nonwearable type stay unequipped");
        foreach (var clone in Inventory.LastSavedItems)
        {
            var definition = clone.m_dropPrefab.GetComponent<ItemDrop>().m_itemData;
            Check(!ReferenceEquals(clone, definition) && ReferenceEquals(clone.m_shared, definition.m_shared) &&
                !ReferenceEquals(clone.m_customData, definition.m_customData),
                "equipment flags change only detached clones, preserving vanilla custom-data clone isolation");
        }
        Check(definitions.All(item => item.m_equipped && item.m_stack == 1 &&
            item.m_durability == 17.5f && item.m_worldLevel == 7 && item.m_dropPrefab == null &&
            item.m_customData.Count == 2 && item.m_customData["fixture-owner"] == item.m_shared.Name),
            "source equipment, stack, durability, world-level, prefab and custom data remain untouched");
        var clonedHelmet = Inventory.LastSavedItems.First(item => item.m_dropPrefab.name == "AHelmet");
        clonedHelmet.m_customData["fixture-owner"] = "clone-only change";
        Check(clonedHelmet.m_dropPrefab.GetComponent<ItemDrop>().m_itemData.m_customData["fixture-owner"] == "AHelmet",
            "mutating staged custom data cannot affect the item definition or a later creation");
        Check(first.SequenceEqual(codec.CreateInitialProfileBytes("New", plan)),
            "retries regenerate identical wearable flags, order, counts and custom data");
        var reversed = new ServerManager.ServerSettings();
        foreach (var pair in plan.StartItems.Reverse()) reversed.StartItems.Add(pair.Key, pair.Value);
        Check(first.SequenceEqual(codec.CreateInitialProfileBytes("New", reversed)),
            "equivalent reordered configuration must select identical slot winners and bytes");
        Check(player.Inventory.GetAllItems().Count == 1 &&
            ReferenceEquals(player.Inventory.GetAllItems()[0], originalInventoryItem),
            "wearable creation never modifies or re-equips the player prefab inventory");
    }
}
