using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ReachSurvival
{
    // Shapes of the converter's rust/rules.json (same names as converter/RustExport.cs).
    public sealed class RCost { public string Item; public float Amount; }
    public sealed class RGrade { public float Health; public List<RCost> Cost = new(); public float[] Protection; }
    public sealed class RPiece { public float CostMultiplier; public float HealthMultiplier; }
    public sealed class RExplosive { public float Radius, MinRadius, TimerMin, TimerMax, DudChance; public Dictionary<string, float> DamageByType = new(); }
    public sealed class RBlueprint { public List<RCost> Ingredients = new(); public float Time; public int Workbench; public int Amount; }
    public sealed class RItem { public int Stack; public int Category; }
    public sealed class RCook { public string Become; public float Time; public float Amount; }
    public sealed class RBurn { public float Fuel; public string Byproduct; public int ByproductAmount; public float ByproductChance; }
    public sealed class ROven { public float Health; public int Slots; }

    public sealed class RustRules
    {
        public Dictionary<string, RItem> Items = new();
        public Dictionary<string, RBlueprint> Blueprints = new();
        public Dictionary<string, RGrade> Grades = new();
        public Dictionary<string, RPiece> Pieces = new();
        public Dictionary<string, float> Doors = new();
        public Dictionary<string, RExplosive> Explosives = new();
        public Dictionary<string, float> TreeWood = new();
        public float CupboardHealth;
        public Dictionary<string, RCook> Cooking = new();
        public Dictionary<string, RBurn> Burning = new();
        public ROven Furnace;
        public Dictionary<string, float> Upkeep = new();
        public List<string> Missing = new();
    }

    /// <summary>Everything read from the player's own games, via the converter's cache.</summary>
    public static class Content
    {
        public static RustRules Rust;
        public static Dictionary<string, Dictionary<string, float>> HaloStats;
        static readonly Dictionary<string, Texture2D> textures = new();
        static readonly Dictionary<string, (Mesh mesh, Material[] mats)> meshes = new();
        static Material baseMaterial;

        public static void LoadRules()
        {
            Rust = JsonConvert.DeserializeObject<RustRules>(File.ReadAllText(Path.Combine(Paths.Cache, "rust", "rules.json")));
            HaloStats = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(Path.Combine(Paths.Cache, "halo", "stats.json")));
        }

        public static float Stat(string row, string key) => HaloStats[row][key];

        /// <summary>A halo_sources row's mesh, with Halo's own textures. Cached.</summary>
        public static (Mesh mesh, Material[] mats) Mesh(string rowId)
        {
            if (meshes.TryGetValue(rowId, out var hit)) return hit;
            if (baseMaterial == null) baseMaterial = Resources.Load<Material>("RS_Base");
            using var r = new BinaryReader(File.OpenRead(Path.Combine(Paths.Cache, "halo", rowId + ".rsm")));
            if (new string(r.ReadChars(4)) != "RSM1") throw new InvalidDataException(rowId + ": not an RSM1 file");
            int subCount = r.ReadInt32();
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            var tris = new List<int[]>(); var mats = new List<Material>();
            for (int s = 0; s < subCount; s++)
            {
                var tex = r.ReadString();
                int vc = r.ReadInt32(), baseV = pos.Count;
                for (int i = 0; i < vc; i++) pos.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                for (int i = 0; i < vc; i++) nrm.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                for (int i = 0; i < vc; i++) uv.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                int ic = r.ReadInt32();
                var idx = new int[ic];
                for (int i = 0; i < ic; i++) idx[i] = r.ReadInt32() + baseV;
                tris.Add(idx);
                var m = new Material(baseMaterial) { name = tex };
                if (tex != "") m.mainTexture = Texture(tex);
                mats.Add(m);
            }
            var mesh = new Mesh { name = rowId, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetUVs(0, uv);
            mesh.subMeshCount = tris.Count;
            for (int s = 0; s < tris.Count; s++) mesh.SetTriangles(tris[s], s, false);
            mesh.RecalculateBounds();
            return meshes[rowId] = (mesh, mats.ToArray());
        }

        static Texture2D Texture(string file)
        {
            if (textures.TryGetValue(file, out var t)) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = file, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var path = Path.Combine(Paths.Cache, "halo", "tex", file);
            if (File.Exists(path)) t.LoadImage(File.ReadAllBytes(path), true);
            return textures[file] = t;
        }

        /// <summary>Builds a GameObject showing a halo_sources row, scaled so its largest size is <paramref name="size"/> metres (0 = as is).</summary>
        public static GameObject Spawn(string rowId, Transform parent = null, float size = 0f, string name = null)
        {
            var (mesh, mats) = Mesh(rowId);
            var go = new GameObject(name ?? rowId);
            if (parent) go.transform.SetParent(parent, false);
            var model = new GameObject("model");
            model.transform.SetParent(go.transform, false);
            model.AddComponent<MeshFilter>().sharedMesh = mesh;
            model.AddComponent<MeshRenderer>().sharedMaterials = mats;
            if (size > 0f)
            {
                var b = mesh.bounds;
                float k = size / Mathf.Max(b.size.x, b.size.y, b.size.z);
                model.transform.localScale = Vector3.one * k;
                // sit the model on its own origin: centred in x/z, bottom at y = 0
                model.transform.localPosition = new Vector3(-b.center.x * k, -b.min.y * k, -b.center.z * k);
            }
            return go;
        }
    }
}
