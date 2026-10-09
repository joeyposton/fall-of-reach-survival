using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReachSurvival.Sheets;

namespace ReachSurvival
{
    /// <summary>
    /// The items in sheets/items.json. Each keeps its Halo name on screen; its stack size, recipe and
    /// smelting come from the player's Rust (by its Rust shortname).
    /// </summary>
    public static class ItemDb
    {
        static Dictionary<string, ItemsRow> byId;
        static Dictionary<string, ItemsRow> byRust;

        public static void Init()
        {
            byId = Items.All.ToDictionary(r => r.Id);
            byRust = Items.All.ToDictionary(r => r.RustShortname);
        }

        public static ItemsRow Get(string id) => byId.TryGetValue(id, out var r) ? r : null;
        public static ItemsRow FromRust(string shortname) => byRust.TryGetValue(shortname, out var r) ? r : null;
        public static string Name(string id) => Get(id)?.HaloName ?? id;
        public static bool InV01(string id) => Get(id)?.Version == "v0.1";

        public static int Stack(string id)
        {
            var row = Get(id);
            return row != null && Content.Rust.Items.TryGetValue(row.RustShortname, out var it) && it.Stack > 0 ? it.Stack : 1;
        }

        /// <summary>Rust's recipe for an item, with ingredients translated to our item ids.</summary>
        public static RBlueprint Recipe(string id) =>
            Get(id) is { } row && Content.Rust.Blueprints.TryGetValue(row.RustShortname, out var bp) ? bp : null;

        public static string IdOfRust(string shortname) => FromRust(shortname)?.Id;

        /// <summary>Items a player can craft in v0.1 (have a Rust recipe and are in scope).</summary>
        public static IEnumerable<ItemsRow> Craftable() => Items.All.Where(r => r.Version == "v0.1" && r.Source == "craft" && Recipe(r.Id) != null);
    }

    /// <summary>Slots of (item, count), stacking to Rust's stack sizes. Lives on the host; owners get copies.</summary>
    public sealed class Inventory
    {
        public const int SlotCount = 30;
        public readonly string[] Item = new string[SlotCount];
        public readonly int[] Count = new int[SlotCount];

        public int Total(string id)
        {
            int n = 0;
            for (int i = 0; i < SlotCount; i++) if (Item[i] == id) n += Count[i];
            return n;
        }

        /// <summary>Adds as much as fits; returns what didn't.</summary>
        public int Add(string id, int amount)
        {
            int stack = ItemDb.Stack(id);
            for (int i = 0; i < SlotCount && amount > 0; i++)
                if (Item[i] == id && Count[i] < stack) { int k = Math.Min(stack - Count[i], amount); Count[i] += k; amount -= k; }
            for (int i = 0; i < SlotCount && amount > 0; i++)
                if (Item[i] == null) { int k = Math.Min(stack, amount); Item[i] = id; Count[i] = k; amount -= k; }
            return amount;
        }

        public bool Take(string id, int amount)
        {
            if (Total(id) < amount) return false;
            for (int i = SlotCount - 1; i >= 0 && amount > 0; i--)
                if (Item[i] == id) { int k = Math.Min(Count[i], amount); Count[i] -= k; amount -= k; if (Count[i] == 0) Item[i] = null; }
            return true;
        }

        public bool Has(IEnumerable<(string id, int n)> costs) => costs.All(c => Total(c.id) >= c.n);
        public bool TakeAll(IEnumerable<(string id, int n)> costs)
        {
            var list = costs.ToList();
            if (!Has(list)) return false;
            foreach (var (id, n) in list) Take(id, n);
            return true;
        }

        public void Write(BinaryWriter w)
        {
            for (int i = 0; i < SlotCount; i++) { w.Write(Item[i] ?? ""); w.Write(Count[i]); }
        }

        public void Read(BinaryReader r)
        {
            for (int i = 0; i < SlotCount; i++) { var s = r.ReadString(); Item[i] = s == "" ? null : s; Count[i] = r.ReadInt32(); }
        }
    }
}
