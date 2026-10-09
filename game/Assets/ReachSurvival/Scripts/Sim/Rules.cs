using System;
using System.Collections.Generic;
using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Rust's rules, as read from the player's Rust (rust/rules.json), in the shapes the game uses.</summary>
    public static class Rules
    {
        // Rust's DamageType numbers (the index into ProtectionProperties.amounts).
        public const int Generic = 0, Bullet = 9, Slash = 10, Blunt = 11, Stab = 15, Explosion = 16;

        public static GradesRow Grade(int g) => Grades.All[Mathf.Clamp(g, 0, Grades.All.Length - 1)];
        public static RGrade RustGrade(int g) => Content.Rust.Grades[Grade(g).RustGrade];
        public static RPiece RustPiece(PiecesRow p) => Content.Rust.Pieces[p.RustConstruction];

        /// <summary>Health of a building piece: Rust's grade health x Rust's construction health multiplier.</summary>
        public static float PieceHealth(PiecesRow piece, int grade) => RustGrade(grade).Health * RustPiece(piece).HealthMultiplier;

        /// <summary>Cost to build/upgrade: Rust's grade cost x construction cost multiplier, in our item ids.</summary>
        public static List<(string id, int n)> PieceCost(PiecesRow piece, int grade) =>
            RustGrade(grade).Cost.Select(c => (ItemDb.IdOfRust(c.Item) ?? c.Item, Mathf.CeilToInt(c.Amount * RustPiece(piece).CostMultiplier))).ToList();

        /// <summary>Damage after Rust's per-grade protection for that damage type.</summary>
        public static float AfterProtection(float amount, int damageType, float[] protection)
        {
            if (protection == null || damageType < 0 || damageType >= protection.Length) return amount;
            return amount * (1f - Mathf.Clamp01(protection[damageType]));
        }

        public static float PieceDamage(int grade, int damageType, float amount) =>
            AfterProtection(amount, damageType, RustGrade(grade).Protection);

        public static float DoorHealth(string rustDoor) => Content.Rust.Doors.TryGetValue(rustDoor, out var h) ? h : 200f;

        public static RExplosive Explosive(string rustPrefab) => Content.Rust.Explosives[rustPrefab];

        /// <summary>Recipe ingredients as (our id, amount).</summary>
        public static List<(string id, int n)> Ingredients(RBlueprint bp) =>
            bp.Ingredients.Select(i => (ItemDb.IdOfRust(i.Item) ?? i.Item, Mathf.RoundToInt(i.Amount))).ToList();

        /// <summary>Wood per tree: the median of Rust's tree yields (they range 250-500).</summary>
        public static int TreeWood
        {
            get
            {
                var v = Content.Rust.TreeWood.Values.OrderBy(x => x).ToList();
                return v.Count == 0 ? 300 : Mathf.RoundToInt(v[v.Count / 2]);
            }
        }

        public static string Design(string id) => Sheets.Design.All.First(r => r.Id == id).Value;
    }
}
