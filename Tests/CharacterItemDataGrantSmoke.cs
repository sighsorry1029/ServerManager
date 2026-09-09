using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ServerManager.Events;

// Only the preset methods are source-linked by the runner. These boundaries are
// inert and intentionally cannot instantiate prefabs, contact peers, or save.
namespace UnityEngine
{
    public sealed class GameObject
    {
        public readonly string name;
        public GameObject(string value) { name = value; }
    }
}
public struct Vector2i
{
    public int x, y;
    public Vector2i(int x, int y) { this.x = x; this.y = y; }
}
public static class Game { public static int m_worldLevel; }
public sealed class ItemDrop
{
    public sealed class SharedData
    {
        public int m_maxStackSize = 10, m_maxQuality = 100;
        public string m_name = "FixtureItem";
    }
    public sealed class ItemData
    {
        public SharedData m_shared = new SharedData();
        public GameObject m_dropPrefab;
        public Dictionary<string, string> m_customData = new Dictionary<string, string>(StringComparer.Ordinal);
        public int m_stack = 1, m_quality = 1;
        public byte m_worldLevel;
        public float m_durability;
        public bool m_equipped;
        public Vector2i m_gridPos;
        public ItemData Clone()
        {
            PresetGrantHooks.Observe(this, "Clone");
            var clone = (ItemData)MemberwiseClone();
            clone.m_customData = new Dictionary<string, string>(m_customData, StringComparer.Ordinal);
            PresetGrantHooks.Clones.Add(clone);
            PresetGrantHooks.AfterClone?.Invoke(this, clone);
            return clone;
        }
        public float GetMaxDurability()
        {
            PresetGrantHooks.Observe(this, "GetMaxDurability");
            PresetGrantHooks.Durability?.Invoke(this);
            return 100f + m_quality;
        }
    }
}
public static class PresetGrantHooks
{
    public static Action<ItemDrop.ItemData, string> FirstAccess;
    public static Action<ItemDrop.ItemData, ItemDrop.ItemData> AfterClone;
    public static Action<ItemDrop.ItemData> Durability;
    public static readonly List<ItemDrop.ItemData> Clones = new List<ItemDrop.ItemData>();
    public static readonly List<string> AccessKinds = new List<string>();
    static readonly HashSet<ItemDrop.ItemData> Accessed = new HashSet<ItemDrop.ItemData>();
    public static void Observe(ItemDrop.ItemData item, string kind)
    {
        AccessKinds.Add(kind);
        if (Accessed.Add(item)) FirstAccess?.Invoke(item, kind);
    }
    public static void Reset()
    {
        FirstAccess = null; AfterClone = null; Durability = null;
        Clones.Clear(); AccessKinds.Clear(); Accessed.Clear(); Game.m_worldLevel = 3;
    }
}
public sealed class Inventory
{
    readonly string name;
    readonly object background;
    readonly int width, height;
    readonly List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>();
    public int AddCalls, ChangedCalls;
    public Func<Inventory, ItemDrop.ItemData, bool> BeforeAdd;
    public Action<Inventory, ItemDrop.ItemData> AfterPlacement;
    public Action<Inventory> OnChanged;
    public Inventory(string name, object background, int width, int height)
    { this.name = name; this.background = background; this.width = width; this.height = height; }
    public string GetName() { return name; }
    public object GetBkg() { return background; }
    public int GetWidth() { return width; }
    public int GetHeight() { return height; }
    public List<ItemDrop.ItemData> GetAllItems() { return items; }
    public ItemDrop.ItemData GetItemAt(int x, int y)
    { return items.FirstOrDefault(item => item.m_gridPos.x == x && item.m_gridPos.y == y); }
    private bool AddItem(ItemDrop.ItemData source, int amount, int x, int y)
    {
        ++AddCalls;
        if (BeforeAdd != null && !BeforeAdd(this, source)) return false;
        if (amount <= 0 || amount > source.m_stack || x < 0 || y < 0 || x >= width || y >= height) return false;
        ItemDrop.ItemData existing = GetItemAt(x, y);
        if (existing == null)
        {
            // Installed vanilla coordinate-placement semantics: clone into the
            // empty slot, decrement the source stack, then signal Changed.
            var added = source.Clone();
            added.m_gridPos = new Vector2i(x, y); added.m_stack = amount;
            items.Add(added); source.m_stack -= amount;
            AfterPlacement?.Invoke(this, added);
            Changed();
            return true;
        }
        // Vanilla may merge matching item/quality without comparing custom data.
        // Production must never reach this branch for a preset grant.
        if (existing.m_shared.m_name != source.m_shared.m_name || existing.m_quality != source.m_quality) return false;
        int accepted = Math.Min(amount, existing.m_shared.m_maxStackSize - existing.m_stack);
        existing.m_stack += accepted; source.m_stack -= accepted;
        Changed(); return accepted == amount;
    }
    void Changed() { ++ChangedCalls; OnChanged?.Invoke(this); }
}
namespace ServerManager.Events
{
    public sealed class ServerManagerCommandResult
    {
        public readonly bool Success;
        public readonly string Code, Message;
        public ServerManagerCommandResult(bool success, string code, string message)
        { Success = success; Code = code; Message = message; }
    }
}
namespace ServerManager
{
    internal static class CharacterAdminActions
    {
        // SOURCE_LINKED_PRESET_IMPLEMENTATION
        static ServerManagerCommandResult Result(bool success, string code, string message)
        { return new ServerManagerCommandResult(success, code, message); }
        internal static ServerManagerCommandResult Grant(Inventory inventory, ItemDrop.ItemData source,
            GameObject prefab, int amount, int quality, Dictionary<string, string> data)
        { return GivePresetItems(inventory, source, prefab, amount, quality, data); }
    }
}
public static class CharacterItemDataGrantSmoke
{
    static int assertions;
    static void Check(bool value, string message)
    { ++assertions; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidOperationException ||
            exception is TargetInvocationException invocation && invocation.InnerException is InvalidOperationException)
        { ++assertions; return; }
        throw new Exception(message);
    }
    sealed class Fixture
    {
        internal readonly GameObject Prefab = new GameObject("FixtureItem");
        internal readonly ItemDrop.ItemData Source;
        internal readonly Inventory Inventory;
        internal readonly Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["default"] = "override",
            ["empty"] = "",
            ["unicode"] = "검 ⚔️ e\u0301 Ω \"quoted\" \\ value",
            ["opaque"] = "{\"name\":\"희귀\",\"values\":[1,2]}"
        };
        internal Fixture(int width = 4, int height = 1, int stackLimit = 10)
        {
            PresetGrantHooks.Reset();
            Source = new ItemDrop.ItemData
            {
                m_dropPrefab = Prefab, m_stack = 7, m_quality = 1, m_durability = 12f,
                m_equipped = true, m_gridPos = new Vector2i(8, 9)
            };
            Source.m_shared.m_maxStackSize = stackLimit;
            Source.m_customData["default"] = "base"; Source.m_customData["retained"] = "prefab value";
            Inventory = new Inventory("fixture", new object(), width, height);
        }
        internal ServerManagerCommandResult Grant(int amount, int quality = 2, Dictionary<string, string> data = null)
        { return ServerManager.CharacterAdminActions.Grant(Inventory, Source, Prefab, amount, quality, data ?? Data); }
        internal ItemDrop.ItemData Existing(int x, int y, int stack = 4, string metadata = "old")
        {
            var item = new ItemDrop.ItemData
            {
                m_shared = Source.m_shared, m_dropPrefab = Prefab, m_stack = stack, m_quality = 2,
                m_equipped = true, m_gridPos = new Vector2i(x, y)
            };
            item.m_customData["default"] = metadata; Inventory.GetAllItems().Add(item); return item;
        }
        internal void SourceUnchanged()
        {
            Check(Source.m_stack == 7 && Source.m_quality == 1 && Source.m_durability == 12f && Source.m_equipped &&
                Source.m_worldLevel == 0 && Source.m_gridPos.x == 8 && Source.m_gridPos.y == 9 && ReferenceEquals(Source.m_dropPrefab, Prefab),
                "Grant must not mutate prefab item state");
            Check(Source.m_customData.Count == 2 && Source.m_customData["default"] == "base" && Source.m_customData["retained"] == "prefab value",
                "Grant must not mutate prefab default metadata");
        }
    }
    static void ExactData(ItemDrop.ItemData item, Dictionary<string, string> expected, string context)
    {
        foreach (var pair in expected)
            Check(item.m_customData.TryGetValue(pair.Key, out string value) && string.Equals(value, pair.Value, StringComparison.Ordinal),
                context + ": exact custom value " + pair.Key);
    }
    static void OriginalInventory(Inventory inventory, ItemDrop.ItemData[] originals, int[] stacks, string context)
    {
        Check(inventory.GetAllItems().Count == originals.Length, context + ": original count");
        for (int index = 0; index < originals.Length; ++index)
        {
            Check(ReferenceEquals(inventory.GetAllItems()[index], originals[index]), context + ": original reference/order");
            Check(originals[index].m_stack == stacks[index], context + ": original stack");
        }
    }
    static void MetadataBeforeCacheAndDetachedStacks()
    {
        var fixture = new Fixture();
        int firstAccesses = 0;
        PresetGrantHooks.FirstAccess = (item, kind) =>
        {
            ++firstAccesses;
            Check(!ReferenceEquals(item, fixture.Source), "No Clone/cache access may run on the shared prefab");
            ExactData(item, fixture.Data, "Metadata must precede the first " + kind);
            Check(item.m_customData["retained"] == "prefab value", "Default metadata precedes cache access");
            Check(item.m_quality == 2 && item.m_worldLevel == 3 && !item.m_equipped && ReferenceEquals(item.m_dropPrefab, fixture.Prefab),
                "Grant state is complete before any metadata consumer");
        };
        var result = fixture.Grant(25);
        Check(result.Success && result.Code == "applied", "Multi-stack preset grant applies");
        Check(firstAccesses >= 4 && PresetGrantHooks.AccessKinds[0] == "GetMaxDurability", "Metadata-aware durability is the first observable access");
        Check(fixture.Inventory.GetAllItems().Select(item => item.m_stack).SequenceEqual(new[] { 10, 10, 5 }), "Amount splits into bounded stacks");
        var granted = fixture.Inventory.GetAllItems();
        for (int index = 0; index < granted.Count; ++index)
        {
            ExactData(granted[index], fixture.Data, "Placed stack");
            Check(granted[index].m_gridPos.x == index && granted[index].m_gridPos.y == 0 && granted[index].m_durability == 102f,
                "Empty slots and requested durability are preserved");
        }
        var dictionaries = granted.Select(item => item.m_customData).Concat(new[] { fixture.Source.m_customData, fixture.Data }).ToArray();
        for (int first = 0; first < dictionaries.Length; ++first)
            for (int second = first + 1; second < dictionaries.Length; ++second)
                Check(!ReferenceEquals(dictionaries[first], dictionaries[second]), "Granted stacks, preset input, and prefab must have detached dictionaries");
        granted[0].m_customData["unicode"] = "changed after grant";
        Check(granted[1].m_customData["unicode"] == fixture.Data["unicode"] && granted[2].m_customData["unicode"] == fixture.Data["unicode"],
            "Changing one granted stack cannot affect siblings or input");
        fixture.SourceUnchanged();
    }
    static void DefaultsAndEmptyData()
    {
        var fixture = new Fixture();
        Check(fixture.Grant(1, 2, new Dictionary<string, string>()).Success, "Empty preset is a valid detached grant");
        ItemDrop.ItemData added = fixture.Inventory.GetAllItems().Single();
        Check(added.m_customData.Count == 2 && added.m_customData["default"] == "base" && added.m_customData["retained"] == "prefab value",
            "Empty preset retains all prefab custom-data defaults");
        Check(!ReferenceEquals(added.m_customData, fixture.Source.m_customData), "Empty preset still detaches source dictionary");
        fixture.SourceUnchanged();
    }
    static void NoMetadataMergeAndCapacity()
    {
        var fixture = new Fixture(3, 1);
        var original = fixture.Existing(0, 0, 3, "different metadata");
        Check(fixture.Grant(12).Success, "Separate empty slots permit a grant alongside matching prefab and quality");
        Check(fixture.Inventory.GetAllItems().Count == 3 && ReferenceEquals(fixture.Inventory.GetAllItems()[0], original) && original.m_stack == 3 &&
            original.m_customData["default"] == "different metadata", "Preset never merges with same-prefab, same-quality existing stack");
        Check(fixture.Inventory.GetAllItems().Skip(1).Select(item => item.m_stack).SequenceEqual(new[] { 10, 2 }), "Existing stack capacity is not consumed");
        fixture.SourceUnchanged();

        fixture = new Fixture(2, 1);
        original = fixture.Existing(0, 0, 1, "old");
        var result = fixture.Grant(11);
        Check(!result.Success && result.Code == "inventory_full", "Entire preset grant must fit empty slots, even when merge space exists");
        OriginalInventory(fixture.Inventory, new[] { original }, new[] { 1 }, "Insufficient whole-grant capacity");
        Check(fixture.Inventory.AddCalls == 0 && fixture.Inventory.ChangedCalls == 0, "Capacity failure cannot reach live placement or Changed");
        fixture.SourceUnchanged();

        fixture = new Fixture(2, 2);
        fixture.Existing(0, 0); fixture.Existing(1, 0); fixture.Existing(0, 1);
        Check(fixture.Grant(1).Success && fixture.Inventory.GetItemAt(1, 1).m_customData["default"] == "override", "Empty-slot search spans inventory rows");
    }
    static void CloneAndDurabilityMutationRejected()
    {
        var fixture = new Fixture();
        PresetGrantHooks.Durability = item => item.m_customData["unicode"] = "CALLBACK_SECRET";
        Reject(() => fixture.Grant(1), "Durability callback metadata corruption must be rejected");
        Check(fixture.Inventory.GetAllItems().Count == 0 && fixture.Inventory.AddCalls == 0, "Durability corruption cannot grant items");
        fixture.SourceUnchanged();

        fixture = new Fixture();
        PresetGrantHooks.AfterClone = (source, clone) => clone.m_customData.Remove("empty");
        Reject(() => fixture.Grant(1), "Clone callback metadata removal must be rejected before placement");
        Check(fixture.Inventory.GetAllItems().Count == 0 && fixture.Inventory.AddCalls == 0, "Template clone corruption cannot grant items");
        fixture.SourceUnchanged();

        fixture = new Fixture();
        int clones = 0;
        PresetGrantHooks.AfterClone = (source, clone) => { if (++clones == 2) clone.m_customData["opaque"] = "CHANGED"; };
        Reject(() => fixture.Grant(1), "Staged AddItem Clone callback corruption must be rejected");
        Check(clones == 2 && fixture.Inventory.AddCalls == 0 && fixture.Inventory.GetAllItems().Count == 0, "Staged corruption cannot reach live placement");
        fixture.SourceUnchanged();
    }
    static void LiveFailureRollsBackOriginalReferences()
    {
        var fixture = new Fixture(4, 1);
        var first = fixture.Existing(0, 0, 3); var second = fixture.Existing(1, 0, 7);
        fixture.Inventory.BeforeAdd = (inventory, item) =>
        {
            first.m_stack = 99; second.m_stack = 88;
            return inventory.AddCalls != 2;
        };
        Reject(() => fixture.Grant(11), "Injected second AddItem failure must reject the entire grant");
        OriginalInventory(fixture.Inventory, new[] { first, second }, new[] { 3, 7 }, "Second placement failure rollback");
        Check(fixture.Inventory.AddCalls == 2 && fixture.Inventory.ChangedCalls == 2, "One successful placement and rollback each signal Changed");
        Check(first.m_equipped && second.m_equipped, "Rollback retains equipped item identity/state");
        fixture.SourceUnchanged();
    }
    static void LiveMetadataMutationRollsBack()
    {
        var fixture = new Fixture(4, 1);
        var original = fixture.Existing(0, 0, 4);
        fixture.Inventory.AfterPlacement = (inventory, added) =>
        {
            if (inventory.AddCalls != 2) return;
            original.m_stack = 99; added.m_customData["unicode"] = "CALLBACK_SECRET";
        };
        Reject(() => fixture.Grant(11), "Placed data mutation must reject the whole grant");
        OriginalInventory(fixture.Inventory, new[] { original }, new[] { 4 }, "Metadata callback rollback");
        Check(fixture.Inventory.AddCalls == 2 && fixture.Inventory.ChangedCalls == 3, "Two placements plus rollback notify inventory changes");
        fixture.SourceUnchanged();

        fixture = new Fixture(3, 1);
        original = fixture.Existing(0, 0, 4);
        fixture.Inventory.OnChanged = inventory =>
        {
            if (inventory.AddCalls == 1 && inventory.GetAllItems().Count > 1)
            { original.m_stack = 77; inventory.GetAllItems().Last().m_customData.Remove("empty"); }
        };
        Reject(() => fixture.Grant(1), "Changed callback metadata deletion must reject the grant");
        OriginalInventory(fixture.Inventory, new[] { original }, new[] { 4 }, "Changed callback rollback");
        fixture.SourceUnchanged();

        fixture = new Fixture(3, 1);
        original = fixture.Existing(0, 0, 4);
        fixture.Inventory.BeforeAdd = (inventory, item) =>
        {
            PresetGrantHooks.AfterClone = (source, clone) => { original.m_stack = 55; clone.m_customData["opaque"] = "CHANGED"; };
            return true;
        };
        Reject(() => fixture.Grant(1), "Live placement Clone callback metadata mutation must reject the grant");
        OriginalInventory(fixture.Inventory, new[] { original }, new[] { 4 }, "Live Clone callback rollback");
        fixture.SourceUnchanged();
    }
    static void PlacementShapeMutationRollsBack()
    {
        foreach (string mutation in new[] { "stack", "remove", "throw" })
        {
            var fixture = new Fixture(3, 1);
            var original = fixture.Existing(0, 0, 4);
            fixture.Inventory.AfterPlacement = (inventory, added) =>
            {
                original.m_stack = 90;
                if (mutation == "stack") added.m_stack++;
                else if (mutation == "remove") inventory.GetAllItems().Remove(added);
                else throw new InvalidOperationException("CALLBACK_SECRET");
            };
            Reject(() => fixture.Grant(1), "Unexpected placement shape must reject: " + mutation);
            OriginalInventory(fixture.Inventory, new[] { original }, new[] { 4 }, "Placement " + mutation + " rollback");
            fixture.SourceUnchanged();
        }
    }
    public static void Run()
    {
        assertions = 0;
        MetadataBeforeCacheAndDetachedStacks(); DefaultsAndEmptyData(); NoMetadataMergeAndCapacity();
        CloneAndDurabilityMutationRejected(); LiveFailureRollsBackOriginalReferences();
        LiveMetadataMutationRollsBack(); PlacementShapeMutationRollsBack();
        PresetGrantHooks.Reset();
        Console.WriteLine("PASS: source-linked custom-data item grant (" + assertions + " assertions; inert inventory and metadata callbacks).");
    }
}
