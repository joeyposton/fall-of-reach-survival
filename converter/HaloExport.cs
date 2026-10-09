using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Reclaimer.Blam.Common;
using Reclaimer.Blam.Utilities;
using Reclaimer.Geometry;
using ReachSurvival.Sheets;

namespace ReachSurvival.Convert
{
    /// <summary>Exports every halo_sources row: meshes (mode/sbsp) to .rsm + PNG textures, stats (weap/hlmt/char) to stats.json.</summary>
    static class HaloExport
    {
        const float MetresPerWorldUnit = 3.048f;

        public static int Run(string mccDir, string outDir)
        {
            Directory.CreateDirectory(Path.Combine(outDir, "tex"));
            var caches = new Dictionary<string, ICacheFile>();
            ICacheFile Cache(string map)
            {
                if (!caches.TryGetValue(map, out var c))
                {
                    var path = Path.Combine(mccDir, map.Replace('/', Path.DirectorySeparatorChar));
                    Log.Info($"Reading {Path.GetFileName(path)} from Halo: Reach");
                    caches[map] = c = CacheFactory.ReadCacheFile(path);
                }
                return c;
            }

            var stats = new Dictionary<string, Dictionary<string, float>>();
            var textures = new HashSet<string>();
            int errors = 0;
            foreach (var row in HaloSources.All)
            {
                try
                {
                    var cache = Cache(row.Map);
                    var tagPath = row.TagPath.Replace('/', '\\');
                    var tag = cache.TagIndex.FirstOrDefault(t => t.ClassCode == row.TagClass && t.TagName == tagPath)
                              ?? throw new InvalidDataException($"tag {row.TagClass} {row.TagPath} not in {row.Map}");
                    switch (row.TagClass)
                    {
                        case "mode":
                        case "sbsp":
                            ExportMesh(tag, row.TagClass == "sbsp", Path.Combine(outDir, row.Id + ".rsm"), Path.Combine(outDir, "tex"), textures);
                            break;
                        default:
                            stats[row.Id] = new RawTags(cache).Read(tag);
                            break;
                    }
                    Log.Info($"Halo: {row.Id} ok");
                }
                catch (Exception e)
                {
                    errors++;
                    Log.Error($"Halo: {row.Id}: {e.Message}");
                }
            }
            File.WriteAllText(Path.Combine(outDir, "stats.json"), JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
            return errors == 0 ? 0 : 1;
        }

        // ---- meshes --------------------------------------------------------------------------------------

        sealed class Sub
        {
            public string Texture = "";
            public Vector2 Tiling = Vector2.One;
            public readonly List<Vector3> Pos = new(), Nrm = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<int> Idx = new();
        }

        /// <summary>Halo (X forward, Y left, Z up, world units) to Unity (X right, Y up, Z forward, metres). A mirror, so winding flips.</summary>
        static Vector3 ToUnity(Vector3 h, float scale) => new Vector3(-h.Y, h.Z, h.X) * scale;

        static void ExportMesh(IIndexItem tag, bool allPermutations, string outPath, string texDir, HashSet<string> textures)
        {
            if (!ContentFactory.TryGetGeometryContent(tag, out var provider))
                throw new InvalidDataException("no geometry");
            var scene = provider.GetContent();
            var subs = new Dictionary<string, Sub>();

            void Walk(SceneGroup g, Matrix4x4 xf)
            {
                foreach (var o in g.ChildObjects) Obj(o, xf);
                foreach (var c in g.ChildGroups) Walk(c, xf);
            }

            void Obj(SceneObject o, Matrix4x4 xf)
            {
                if (o is ObjectPlacement p) { Obj(p.Object, p.Transform * xf); return; }
                if (o is not Model m) return;
                foreach (var r in m.Regions)
                foreach (var perm in allPermutations ? r.Permutations : r.Permutations.Take(1))
                foreach (var mi in perm.MeshIndices)
                {
                    var mesh = m.Meshes[mi];
                    if (mesh?.VertexBuffer == null || mesh.IndexBuffer == null || mesh.VertexBuffer.Count == 0) continue;
                    AddMesh(mesh, perm.Transform * xf, subs, texDir, textures);
                }
            }

            Walk(scene.RootNode, Matrix4x4.Identity);
            WriteRsm(outPath, subs.Values.Where(s => s.Idx.Count > 0).ToList());
        }

        static void AddMesh(Mesh mesh, Matrix4x4 xf, Dictionary<string, Sub> subs, string texDir, HashSet<string> textures)
        {
            var vb = mesh.VertexBuffer;
            var pos = vb.PositionChannels[0];
            var nrm = vb.HasNormals ? vb.NormalChannels[0] : null;
            var uv = vb.HasTextureCoordinates ? vb.TextureCoordinateChannels[0] : null;
            Matrix4x4.Invert(xf, out var inv);
            var nxf = Matrix4x4.Transpose(inv);
            var idx = mesh.IndexBuffer;

            foreach (var seg in mesh.Segments)
            {
                var (texName, tiling) = Diffuse(seg.Material, texDir, textures);
                if (!subs.TryGetValue(texName, out var sub)) subs[texName] = sub = new Sub { Texture = texName, Tiling = tiling };

                // Copy only the vertices this segment uses.
                var remap = new Dictionary<int, int>();
                int Vert(int i)
                {
                    if (remap.TryGetValue(i, out var n)) return n;
                    var v = pos[i];
                    var p = new Vector3(v.X, v.Y, v.Z);
                    if (mesh.IsCompressed) p = mesh.PositionBounds.Min + p * (mesh.PositionBounds.Max - mesh.PositionBounds.Min);
                    sub.Pos.Add(ToUnity(Vector3.Transform(p, xf), MetresPerWorldUnit));
                    if (nrm != null)
                    {
                        var nv = nrm[i];
                        var nn = Vector3.TransformNormal(new Vector3(nv.X, nv.Y, nv.Z), nxf);
                        sub.Nrm.Add(Vector3.Normalize(ToUnity(nn, 1f)));
                    }
                    else sub.Nrm.Add(Vector3.UnitY);
                    if (uv != null)
                    {
                        var t = uv[i];
                        var q = new Vector2(t.X, t.Y);
                        if (mesh.IsCompressed) q = mesh.TextureBounds.Min + q * (mesh.TextureBounds.Max - mesh.TextureBounds.Min);
                        sub.Uv.Add(new Vector2(q.X * tiling.X, 1f - q.Y * tiling.Y));
                    }
                    else sub.Uv.Add(Vector2.Zero);
                    return remap[i] = sub.Pos.Count - 1;
                }

                void Tri(int a, int b, int c)
                {
                    if (a == b || b == c || a == c) return;
                    // mirror in ToUnity flips handedness, so swap b and c
                    sub.Idx.Add(Vert(a)); sub.Idx.Add(Vert(c)); sub.Idx.Add(Vert(b));
                }

                int end = seg.IndexStart + seg.IndexLength;
                if (idx.Layout == IndexFormat.TriangleStrip)
                {
                    for (int i = seg.IndexStart; i < end - 2; i++)
                    {
                        if (((i - seg.IndexStart) & 1) == 0) Tri(idx[i], idx[i + 1], idx[i + 2]);
                        else Tri(idx[i], idx[i + 2], idx[i + 1]);
                    }
                }
                else
                {
                    for (int i = seg.IndexStart; i + 2 < end; i += 3) Tri(idx[i], idx[i + 1], idx[i + 2]);
                }
            }
        }

        /// <summary>First diffuse-like texture of a material, written once as PNG. Returns (file name, tiling).</summary>
        static (string, Vector2) Diffuse(Material mat, string texDir, HashSet<string> textures)
        {
            var map = mat?.TextureMappings?.FirstOrDefault(t => t.Usage != null && t.Usage.Contains("diffuse", StringComparison.OrdinalIgnoreCase))
                      ?? mat?.TextureMappings?.FirstOrDefault(t => t.Usage != null && t.Usage.Contains("blend", StringComparison.OrdinalIgnoreCase) && false);
            if (map?.Texture == null) return ("", Vector2.One);
            var name = Safe(map.Texture.Name) + ".png";
            var tiling = map.Tiling == Vector2.Zero ? Vector2.One : map.Tiling;
            if (textures.Add(name))
            {
                try
                {
                    dynamic bmp = map.Texture.ContentProvider.GetContent();
                    var dds = bmp.ToDds(0);
                    using var fs = File.Create(Path.Combine(texDir, name));
                    dds.WriteToStream(fs, ImageFormat.Png);
                }
                catch (Exception e)
                {
                    Log.Error($"texture {map.Texture.Name}: {e.Message}");
                    return ("", tiling);
                }
            }
            return (name, tiling);
        }

        static string Safe(string s) => string.Concat((s ?? "tex").Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

        /// <summary>Format the game reads: "RSM1", submesh count, then per submesh texture, verts (pos, normal, uv), indices.</summary>
        static void WriteRsm(string path, List<Sub> subs)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RSM1"u8.ToArray());
            w.Write(subs.Count);
            foreach (var s in subs)
            {
                w.Write(s.Texture);
                w.Write(s.Pos.Count);
                foreach (var p in s.Pos) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                foreach (var n in s.Nrm) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
                foreach (var u in s.Uv) { w.Write(u.X); w.Write(u.Y); }
                w.Write(s.Idx.Count);
                foreach (var i in s.Idx) w.Write(i);
            }
        }

        // ---- raw tag fields (layouts from Assembly's ReachMCC plugins; jpt!/char from its Reach plugins) ----

        sealed class RawTags
        {
            readonly ICacheFile cache;
            readonly IPointerExpander expander;
            readonly MethodInfo createReader;

            public RawTags(ICacheFile cache)
            {
                this.cache = cache;
                var ct = cache.GetType();
                expander = ct.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => typeof(IPointerExpander).IsAssignableFrom(p.PropertyType))
                    .Select(p => (IPointerExpander)p.GetValue(cache)).FirstOrDefault();
                createReader = ct.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "CreateReader" && m.GetParameters().Length == 1);
            }

            dynamic Reader(IIndexItem t) => createReader.Invoke(cache, new object[] { t.GetAddressTranslator() });
            long Addr(IIndexItem t, int ptr) => t.GetAddressTranslator().GetAddress(expander != null ? expander.Expand(ptr) : ptr);
            IIndexItem ById(int raw) => cache.TagIndex.FirstOrDefault(t => (t.Id & 0xFFFF) == (raw & 0xFFFF));
            static float F(object o, long a) { dynamic r = o; r.Seek(a, SeekOrigin.Begin); return (float)r.ReadSingle(); }
            static int I(object o, long a) { dynamic r = o; r.Seek(a, SeekOrigin.Begin); return (int)r.ReadInt32(); }
            static short S(object o, long a) { dynamic r = o; r.Seek(a, SeekOrigin.Begin); return (short)r.ReadInt16(); }
            (int, long) Block(IIndexItem t, object r, long a) => (I(r, a), Addr(t, I(r, a + 4)));
            IIndexItem Ref(object r, long a) => ById(I(r, a + 12));

            public Dictionary<string, float> Read(IIndexItem t) => t.ClassCode switch
            {
                "weap" => Weapon(t),
                "hlmt" => Model(t),
                "char" => Character(t),
                _ => throw new NotSupportedException(t.ClassCode),
            };

            Dictionary<string, float> Weapon(IIndexItem t)
            {
                var d = new Dictionary<string, float>();
                object r = Reader(t); long b = t.MetaPointer.Address;
                var (mc, ma) = Block(t, r, b + 0x3F0);
                var (bc, ba) = Block(t, r, b + 0x408);
                if (mc > 0) { d["roundsTotalInitial"] = S(r, ma + 6); d["roundsLoadedMax"] = S(r, ma + 0xA); }
                if (bc == 0) throw new InvalidDataException("weapon has no barrel");
                d["roundsPerSecondMin"] = F(r, ba + 4);
                d["roundsPerSecondMax"] = F(r, ba + 8);
                d["fireRecoveryTime"] = F(r, ba + 0x28);
                d["projectilesPerShot"] = S(r, ba + 0x6A);
                var proj = Ref(r, ba + 0x104) ?? throw new InvalidDataException("weapon has no projectile");
                object pr = Reader(proj); long pb = proj.MetaPointer.Address;
                d["initialVelocity"] = F(pr, pb + 0x2D0) * MetresPerWorldUnit;
                var jpt = Ref(pr, pb + 0x284) ?? Ref(pr, pb + 0x1D8) ?? throw new InvalidDataException("projectile has no damage");
                object jr = Reader(jpt); long jb = jpt.MetaPointer.Address;
                d["damageRadiusMax"] = F(jr, jb + 4) * MetresPerWorldUnit;
                d["damageLower"] = F(jr, jb + 0x20);
                d["damageUpperMin"] = F(jr, jb + 0x24);
                d["damageUpperMax"] = F(jr, jb + 0x28);
                return d;
            }

            Dictionary<string, float> Model(IIndexItem t)
            {
                object r = Reader(t); long b = t.MetaPointer.Address;
                var (c, a) = Block(t, r, b + 0xC0);
                if (c == 0) throw new InvalidDataException("model has no damage info");
                return new()
                {
                    ["body"] = F(r, a + 0x28),
                    ["shield"] = F(r, a + 0x7C),
                    ["shieldStunTime"] = F(r, a + 0x88),
                    ["shieldRechargeTime"] = F(r, a + 0x8C),
                };
            }

            Dictionary<string, float> Character(IIndexItem t)
            {
                object r = Reader(t); long b = t.MetaPointer.Address;
                var (c, a) = Block(t, r, b + 0x90);
                if (c == 0) throw new InvalidDataException("character has no vitality");
                return new() { ["body"] = F(r, a + 4), ["shield"] = F(r, a + 8) };
            }
        }
    }
}
