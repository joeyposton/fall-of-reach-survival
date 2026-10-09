using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Forge World from the player's Halo: Reach, with collision, sea, sun and sky.</summary>
    public static class World
    {
        public static GameObject Terrain;
        public static Bounds Bounds;
        /// <summary>Height of Reach's sea surface (the water_big_foam layer); below it Spartans swim.</summary>
        public static float WaterLevel = float.NegativeInfinity;

        // Layers in Forge World's mesh that should be seen but not stood on.
        static bool IsWater(string tex) => tex.Contains("water_");
        static bool NoCollision(string tex) => IsWater(tex) || tex.Contains("tree_branch") || tex.Contains("foliage");

        public static IEnumerator Build(Action<string> say)
        {
            Terrain = Content.Spawn("terrain_forge_world", name: "Forge World");
            Terrain.isStatic = true;
            var mf = Terrain.GetComponentInChildren<MeshFilter>();
            var mr = Terrain.GetComponentInChildren<MeshRenderer>();
            var mesh = mf.sharedMesh;
            Bounds = mesh.bounds;

            // Sea surface: tint it, and remember its height.
            var mats = mr.sharedMaterials;
            var solid = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tex = mats[s].name;
                if (IsWater(tex))
                {
                    if (tex.Contains("water_big")) WaterLevel = Mathf.Max(WaterLevel, mesh.GetSubMesh(s).bounds.max.y);
                    mats[s].color = new Color(0.32f, 0.52f, 0.60f);
                    mats[s].SetFloat("_Glossiness", 0.85f);
                }
                if (!NoCollision(tex)) solid.Add(s);
            }
            mr.sharedMaterials = mats;

            say("Building collision for Forge World");
            var collisionMesh = SubMeshes(mesh, solid);
            int id = collisionMesh.GetInstanceID();
            var bake = Task.Run(() => Physics.BakeMesh(id, false));
            while (!bake.IsCompleted) yield return null;
            mf.gameObject.AddComponent<MeshCollider>().sharedMesh = collisionMesh;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.70f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.52f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.22f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.70f, 0.78f, 0.86f);
            RenderSettings.fogStartDistance = 150f;
            RenderSettings.fogEndDistance = 1400f;
            QualitySettings.shadowDistance = 160f;
        }

        static Mesh SubMeshes(Mesh src, List<int> keep)
        {
            var m = new Mesh { name = src.name + " (collision)", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(src.vertices);
            var tris = new List<int>();
            foreach (var s in keep) tris.AddRange(src.GetTriangles(s));
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Dry ground under (x, z), or null over the sea, steep slopes or off the map.</summary>
        public static Vector3? Ground(float x, float z, float minNormalY = 0.75f)
        {
            if (Physics.Raycast(new Vector3(x, Bounds.max.y + 50f, z), Vector3.down, out var hit, Bounds.size.y + 100f)
                && hit.normal.y >= minNormalY && hit.point.y > WaterLevel + 1.0f)
                return hit.point;
            return null;
        }

        /// <summary>A random walkable spot on the island.</summary>
        public static Vector3 RandomSpot(System.Random rng)
        {
            for (int tries = 0; tries < 2000; tries++)
            {
                float x = Mathf.Lerp(Bounds.min.x, Bounds.max.x, 0.1f + 0.8f * (float)rng.NextDouble());
                float z = Mathf.Lerp(Bounds.min.z, Bounds.max.z, 0.1f + 0.8f * (float)rng.NextDouble());
                var g = Ground(x, z);
                if (g.HasValue && g.Value.y < WaterLevel + 60f) return g.Value; // low ground: not on top of the Forerunner structures
            }
            return Bounds.center;
        }
    }
}
