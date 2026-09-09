using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ServerManager.Commands;
using ServerManager.Events;
using UnityEngine;

namespace ServerManager
{
    /// <summary>Bounded owner-local actions. Authentication and Unity dispatch belong to the runtime.</summary>
    internal static class CharacterAdminActions
    {
        internal const float MaximumMagnitude = 1000000f;
        private static readonly MethodInfo ShallowClone = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo AddItemAt = typeof(Inventory).GetMethod(
            "AddItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) }, null)!;

        internal static ServerManagerCommandResult Apply(Player player, string[] action)
        {
            if (player == null || action == null || action.Length == 0)
                return Result(false, "invalid_action", "An active player and action are required.");
            try
            {
                switch (action[0].ToLowerInvariant())
                {
                    case "item":
                        return GiveItem(player, action);
                    case "teleport":
                        if (action.Length != 4 || !TryNumber(action[1], -MaximumMagnitude, MaximumMagnitude, out float x) ||
                            !TryNumber(action[2], -MaximumMagnitude, MaximumMagnitude, out float y) ||
                            !TryNumber(action[3], -MaximumMagnitude, MaximumMagnitude, out float z))
                            return Result(false, "invalid_position", "Teleport requires three finite coordinates within +/-1000000.");
                        return player.TeleportTo(new Vector3(x, y, z), player.transform.rotation, true)
                            ? Result(true, "applied", "Teleport started; arrival is asynchronous.")
                            : Result(false, "teleport_busy", "Teleport was not started (ownership, cooldown, or teleport in progress).");
                    case "skill":
                        return ApplySkill(player.GetSkills(), action);
                    case "heal":
                    case "damage":
                        if (action.Length != 2 || !TryNumber(action[1], 0f, MaximumMagnitude, out float amount) || amount <= 0f)
                            return Result(false, "invalid_amount", "Amount must be finite, positive, and at most 1000000.");
                        bool healing = action[0].Equals("heal", StringComparison.OrdinalIgnoreCase);
                        if (player.IsDead() || player.IsTeleporting() || player.InCutscene())
                            return Result(false, "target_busy", "Health actions cannot run while the player is dead, teleporting, or in a cutscene.");
                        if (!healing && amount <= 0.1f)
                            return Result(false, "invalid_amount", "Raw damage must exceed 0.1; vanilla ignores smaller amounts.");
                        float beforeHealth = player.GetHealth();
                        if (float.IsNaN(beforeHealth) || float.IsInfinity(beforeHealth))
                            return Result(false, "invalid_health", "The player's current health is not finite.");
                        if (healing)
                            player.Heal(amount);
                        else
                        {
                            HitData hit = new HitData();
                            hit.m_damage.m_damage = amount;
                            hit.m_point = player.transform.position;
                            player.Damage(hit);
                        }
                        return HealthResult(healing, amount, beforeHealth, player.GetHealth());
                    default:
                        return Result(false, "invalid_action", "Unsupported character action.");
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                // Mod callbacks may include metadata in their exception text.
                if (string.Equals(action[0], "item", StringComparison.OrdinalIgnoreCase) && action.Length == 6)
                    return Result(false, "action_failed", "Custom-data item grant failed; inspect the target before retrying.");
                return Result(false, "action_failed", "Character action failed: " + exception.Message);
            }
        }

        private static ServerManagerCommandResult GiveItem(Player player, string[] action)
        {
            if ((action.Length != 5 && action.Length != 6) || !action[1].Equals("give", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(action[2]) || action[2].Length > 256 ||
                !int.TryParse(action[3], NumberStyles.None, CultureInfo.InvariantCulture, out int amount) || amount < 1 || amount > 1000 ||
                !int.TryParse(action[4], NumberStyles.None, CultureInfo.InvariantCulture, out int quality) || quality < 1 || quality > 100)
                return Result(false, "invalid_item", "Item give requires a prefab, amount 1..1000, and valid quality 1..100.");
            GameObject? prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(action[2]);
            ItemDrop? drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null ||
                quality > drop.m_itemData.m_shared.m_maxQuality || drop.m_itemData.m_shared.m_maxStackSize <= 0)
                return Result(false, "invalid_item", "Unknown item prefab or quality exceeds this item's maximum.");

            Inventory inventory = player.GetInventory();
            if (action.Length == 6)
            {
                Dictionary<string, string> customData = ItemDataPresets.DecodeData(action[5]);
                return GivePresetItems(inventory, drop.m_itemData, prefab!, amount, quality, customData);
            }
            ItemDrop.ItemData template = drop.m_itemData.Clone();
            template.m_dropPrefab = prefab;
            template.m_quality = quality;
            template.m_worldLevel = (byte)Game.m_worldLevel;
            template.m_durability = template.GetMaxDurability();
            template.m_equipped = false;
            // Vanilla CanAddItem ignores quality. Simulate the exact AddItem path on
            // detached clones instead; no prefabs are instantiated or dropped.
            Inventory staged = new Inventory(inventory.GetName(), inventory.GetBkg(), inventory.GetWidth(), inventory.GetHeight());
            List<ItemDrop.ItemData> originals = new List<ItemDrop.ItemData>(inventory.GetAllItems());
            int[] originalStacks = new int[originals.Count];
            for (int index = 0; index < originals.Count; ++index)
            {
                originalStacks[index] = originals[index].m_stack;
                staged.GetAllItems().Add(originals[index].Clone());
            }
            List<ItemDrop.ItemData> additions = new List<ItemDrop.ItemData>();
            int remaining = amount;
            while (remaining > 0)
            {
                ItemDrop.ItemData next = template.Clone();
                next.m_stack = Math.Min(remaining, template.m_shared.m_maxStackSize);
                remaining -= next.m_stack;
                additions.Add(next);
                if (!staged.AddItem(next.Clone()))
                    return Result(false, "inventory_full", "The entire grant does not fit; no items were added.");
            }
            try
            {
                foreach (ItemDrop.ItemData addition in additions)
                    if (!inventory.AddItem(addition))
                        throw new InvalidOperationException("Inventory changed after preflight.");
            }
            catch
            {
                // Preserve original ItemData object identity (including equipped items).
                inventory.GetAllItems().Clear();
                for (int index = 0; index < originals.Count; ++index)
                {
                    originals[index].m_stack = originalStacks[index];
                    inventory.GetAllItems().Add(originals[index]);
                }
                System.Reflection.MethodInfo changed = typeof(Inventory).GetMethod("Changed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                changed?.Invoke(inventory, null);
                throw;
            }
            return Result(true, "applied", "Added " + amount + " " + action[2] + " at quality " + quality + ".");
        }

        private static ItemDrop.ItemData CloneBeforeMetadataLoad(ItemDrop.ItemData source)
        {
            // Equivalent to vanilla's shallow ItemData clone + detached dictionary,
            // without invoking Clone patches before the preset data is present.
            // Other mod code first sees the new identity after its data is complete.
            ItemDrop.ItemData copy = (ItemDrop.ItemData)ShallowClone.Invoke(source, null);
            copy.m_customData = new Dictionary<string, string>(source.m_customData, StringComparer.Ordinal);
            return copy;
        }

        private static ServerManagerCommandResult GivePresetItems(Inventory inventory,
            ItemDrop.ItemData source, GameObject prefab, int amount, int quality,
            Dictionary<string, string> customData)
        {
            ItemDrop.ItemData template = CloneBeforeMetadataLoad(source);
            template.m_dropPrefab = prefab;
            template.m_quality = quality;
            template.m_worldLevel = (byte)Game.m_worldLevel;
            template.m_equipped = false;
            template.m_stack = 1;
            foreach (var pair in customData) template.m_customData[pair.Key] = pair.Value;
            // Effects may inspect/cache custom data during this call.
            template.m_durability = template.GetMaxDurability();
            VerifyPresetData(template, customData);

            List<ItemDrop.ItemData> originals = new(inventory.GetAllItems());
            int[] originalStacks = new int[originals.Count];
            Inventory staged = new(inventory.GetName(), inventory.GetBkg(), inventory.GetWidth(), inventory.GetHeight());
            for (int index = 0; index < originals.Count; ++index)
            {
                originalStacks[index] = originals[index].m_stack;
                staged.GetAllItems().Add(CloneBeforeMetadataLoad(originals[index]));
            }
            List<(ItemDrop.ItemData Item, Vector2i Slot)> additions = new();
            int remaining = amount;
            while (remaining > 0)
            {
                if (!TryFindPresetSlot(staged, out Vector2i slot))
                    return Result(false, "inventory_full", "Custom-data items require empty slots for the entire grant; no items were added.");
                ItemDrop.ItemData next = template.Clone();
                next.m_stack = Math.Min(remaining, template.m_shared.m_maxStackSize);
                remaining -= next.m_stack;
                VerifyPresetData(next, customData);
                AddPresetStackAt(staged, CloneBeforeMetadataLoad(next), slot, customData);
                additions.Add((next, slot));
            }
            try
            {
                foreach (var addition in additions)
                    AddPresetStackAt(inventory, addition.Item, addition.Slot, customData);
            }
            catch
            {
                inventory.GetAllItems().Clear();
                for (int index = 0; index < originals.Count; ++index)
                {
                    originals[index].m_stack = originalStacks[index];
                    inventory.GetAllItems().Add(originals[index]);
                }
                typeof(Inventory).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(inventory, null);
                throw;
            }
            return Result(true, "applied", "Added " + amount + " " + prefab.name + " at quality " + quality + " with custom data.");
        }

        private static bool TryFindPresetSlot(Inventory inventory, out Vector2i slot)
        {
            for (int y = 0; y < inventory.GetHeight(); ++y)
                for (int x = 0; x < inventory.GetWidth(); ++x)
                    if (inventory.GetItemAt(x, y) == null)
                    {
                        slot = new Vector2i(x, y);
                        return true;
                    }
            slot = default;
            return false;
        }

        private static void AddPresetStackAt(Inventory inventory, ItemDrop.ItemData item,
            Vector2i slot, Dictionary<string, string> expected)
        {
            int amount = item.m_stack;
            // The generic AddItem overload merges stacks without comparing data.
            // Use a verified empty coordinate and recheck the newly inserted clone.
            if (inventory.GetItemAt(slot.x, slot.y) != null ||
                AddItemAt == null || !(bool)AddItemAt.Invoke(inventory, new object[] { item, amount, slot.x, slot.y }))
                throw new InvalidOperationException("Preset inventory placement failed.");
            ItemDrop.ItemData added = inventory.GetItemAt(slot.x, slot.y);
            if (added == null || added.m_stack != amount || item.m_stack != 0)
                throw new InvalidOperationException("Preset inventory placement was not confirmed.");
            VerifyPresetData(added, expected);
        }

        private static void VerifyPresetData(ItemDrop.ItemData item, Dictionary<string, string> expected)
        {
            foreach (var pair in expected)
                if (!item.m_customData.TryGetValue(pair.Key, out string value) ||
                    !string.Equals(value, pair.Value, StringComparison.Ordinal))
                    throw new InvalidOperationException("An item callback changed the requested custom data.");
        }

        private static ServerManagerCommandResult ApplySkill(Skills skills, string[] action)
        {
            if (skills == null || action.Length < 2 || action.Length > 4)
                return Result(false, "invalid_skill", "Skill requires get or set.");
            string operation = action[1].ToLowerInvariant();
            bool get = operation == "get";
            if ((!get && operation != "set") || (get && action.Length > 3) ||
                (!get && action.Length != 4))
                return Result(false, "invalid_skill", "Invalid skill arguments.");
            string name = action.Length >= 3 ? action[2] : "all";
            float value = 0f;
            if (action.Length == 4 && !TryNumber(action[3], 0f, 100f, out value))
                return Result(false, "invalid_skill", "Skill levels must be finite and within 0..100.");
            List<CharacterSemanticSkillState> current = new List<CharacterSemanticSkillState>();
            foreach (Skills.Skill skill in skills.GetSkillList())
                current.Add(new CharacterSemanticSkillState((int)skill.m_info.m_skill, skill.m_level, skill.m_accumulator));
            List<CharacterSemanticSkillState> updated = EditSkills(current, operation, name, value);
            if (!get)
            {
                // Validate definitions before Load clears the live dictionary.
                foreach (CharacterSemanticSkillState skill in updated)
                    if (!skills.m_skills.Exists(definition => (int)definition.m_skill == skill.SkillType))
                        return Result(false, "invalid_skill", "The player has no definition for the requested skill.");
                skills.Load(new ZPackage(EncodeSkills(updated)));
            }
            return SkillResult(updated, get ? "skills" : "applied", name,
                get ? "Raw skill levels and XP accumulators." : "Skill levels updated; changed skills' XP accumulators reset to zero.");
        }

        internal static List<CharacterSemanticSkillState> EditSkills(IEnumerable<CharacterSemanticSkillState> source, string operation, string name, float value)
        {
            operation = (operation ?? string.Empty).ToLowerInvariant();
            if (operation != "get" && operation != "set")
                throw new CharacterProtocolException("Unsupported skill operation.");
            if (float.IsNaN(value) || float.IsInfinity(value) || value > 100f || value < 0f)
                throw new CharacterProtocolException("The skill value is outside its finite bounded range.");
            bool all = string.Equals(name, "all", StringComparison.OrdinalIgnoreCase);
            Skills.SkillType selected = Skills.SkillType.None;
            if (!all && (!Enum.TryParse(name, true, out selected) || !Enum.IsDefined(typeof(Skills.SkillType), selected) ||
                selected == Skills.SkillType.None || selected == Skills.SkillType.All || !selected.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new CharacterProtocolException("Unknown concrete skill name.");
            Dictionary<int, CharacterSemanticSkillState> map = new Dictionary<int, CharacterSemanticSkillState>();
            foreach (CharacterSemanticSkillState skill in source) map.Add(skill.SkillType, skill);
            if (operation == "set")
            {
                foreach (Skills.SkillType type in Enum.GetValues(typeof(Skills.SkillType)))
                {
                    if (type == Skills.SkillType.None || type == Skills.SkillType.All || (!all && type != selected)) continue;
                    map[(int)type] = new CharacterSemanticSkillState((int)type, value, 0f);
                }
            }
            List<CharacterSemanticSkillState> result = new List<CharacterSemanticSkillState>(map.Values);
            result.Sort((left, right) => left.SkillType.CompareTo(right.SkillType));
            return result;
        }

        internal static byte[] EncodeSkills(IEnumerable<CharacterSemanticSkillState> skills)
        {
            List<CharacterSemanticSkillState> values = new List<CharacterSemanticSkillState>(skills);
            ZPackage package = new ZPackage();
            package.Write(2); package.Write(values.Count);
            foreach (CharacterSemanticSkillState skill in values)
            { package.Write(skill.SkillType); package.Write(skill.Level); package.Write(skill.Accumulator); }
            return package.GetArray();
        }

        internal static ServerManagerCommandResult SkillResult(IEnumerable<CharacterSemanticSkillState> skills, string code, string name, string message)
        {
            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> lines = new List<string>();
            bool all = string.Equals(name, "all", StringComparison.OrdinalIgnoreCase);
            foreach (CharacterSemanticSkillState skill in skills)
            {
                string key = ((Skills.SkillType)skill.SkillType).ToString();
                if (!all && !key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                data[key] = Number(skill.Level);
                data[key + ".xp"] = Number(skill.Accumulator);
                lines.Add(key + " = " + Number(skill.Level) + " (XP " + Number(skill.Accumulator) + ")");
            }
            if (!all && data.Count == 0)
            {
                data[name] = "0"; data[name + ".xp"] = "0";
                lines.Add(name + " = 0 (XP 0)");
            }
            if (lines.Count == 0) lines.Add("No recorded skills; untrained skills are level 0 (XP 0).");
            // Terminal/RPC adapters carry Message, whereas direct API consumers
            // can use Data. Keep both useful without exceeding the wire bound.
            const int maximumMessageLength = 1500;
            const string truncated = "\n[more skills omitted]";
            if (message.Length > maximumMessageLength - truncated.Length)
                message = message.Substring(0, maximumMessageLength - truncated.Length);
            foreach (string line in lines)
            {
                if (message.Length + line.Length + 1 > maximumMessageLength - truncated.Length)
                { message += truncated; break; }
                message += "\n" + line;
            }
            return new ServerManagerCommandResult(true, code, message, string.Empty, data);
        }

        internal static ServerManagerCommandResult Result(bool success, string code, string message) =>
            new ServerManagerCommandResult(success, code, message, string.Empty, new Dictionary<string, string>());
        internal static ServerManagerCommandResult HealthResult(bool healing, float amount, float beforeHealth, float afterHealth)
        {
            bool changed = beforeHealth != afterHealth;
            return Result(true, changed ? "applied" : "no_health_change",
                (healing ? "Heal" : "Raw damage") + " requested: " + Number(amount) +
                "; health " + Number(beforeHealth) + " -> " + Number(afterHealth) +
                ". " + (changed ? "" : "No health change observed. ") +
                "Vanilla health caps and invulnerability rules apply.");
        }
        private static bool TryNumber(string text, float minimum, float maximum, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= minimum && value <= maximum;
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal sealed class CharacterAdminRecord
    {
        internal CharacterAdminRecord(CharacterIdentity identity, long playerId, long revision, long durableRevision, bool active, CharacterSemanticSnapshot snapshot)
        { AccountId = identity.AccountId; CharacterName = identity.CharacterName; PlayerId = playerId; Revision = revision; DurableRevision = durableRevision; IsOnline = active; Snapshot = snapshot; }
        internal string AccountId { get; }
        internal string CharacterName { get; }
        internal long Revision { get; }
        internal long DurableRevision { get; }
        internal long PlayerId { get; }
        internal bool IsOnline { get; }
        internal CharacterSemanticSnapshot Snapshot { get; }
    }
}
