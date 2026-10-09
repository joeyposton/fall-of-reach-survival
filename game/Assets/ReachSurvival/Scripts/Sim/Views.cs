using System.Collections.Generic;
using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Piece sizes on Rust's 3 m grid. Forge blocks (6 m in Reach) are scaled to fit.</summary>
    public static class PieceShape
    {
        public const float Grid = 3f, FoundationH = 0.9f, WallH = 3f, FloorH = 0.25f, WallT = 0.3f;

        public static bool IsWallLike(string piece) => piece is "wall" or "doorway" or "window";

        public static float Height(string piece) => piece switch
        {
            "foundation" => FoundationH, "floor" => FloorH, _ => WallH,
        };

        /// <summary>Collision boxes (centre, size) in the piece's own space; walls face +z, doorways/windows keep a hole.</summary>
        public static IEnumerable<(Vector3 c, Vector3 s)> Boxes(string piece)
        {
            switch (piece)
            {
                case "foundation": yield return (new Vector3(0, FoundationH / 2, 0), new Vector3(Grid, FoundationH, Grid)); break;
                case "floor": yield return (new Vector3(0, FloorH / 2, 0), new Vector3(Grid, FloorH, Grid)); break;
                case "wall": yield return (new Vector3(0, WallH / 2, 0), new Vector3(Grid, WallH, WallT)); break;
                case "doorway":
                    yield return (new Vector3(-1.05f, WallH / 2, 0), new Vector3(0.9f, WallH, WallT));
                    yield return (new Vector3(1.05f, WallH / 2, 0), new Vector3(0.9f, WallH, WallT));
                    yield return (new Vector3(0, 2.65f, 0), new Vector3(1.2f, 0.7f, WallT));
                    break;
                case "window":
                    yield return (new Vector3(0, 0.6f, 0), new Vector3(Grid, 1.2f, WallT));
                    yield return (new Vector3(0, 2.6f, 0), new Vector3(Grid, 0.8f, WallT));
                    yield return (new Vector3(-1.05f, 1.6f, 0), new Vector3(0.9f, 1.0f, WallT));
                    yield return (new Vector3(1.05f, 1.6f, 0), new Vector3(0.9f, 1.0f, WallT));
                    break;
            }
        }

        public static string Look(string piece) => Pieces.All.First(p => p.Id == piece).Look.Replace("halo_sources.", "");

        /// <summary>A Forge block fitted to a piece's box: largest side to the grid, thin side along z, base at y = 0.</summary>
        public static GameObject Model(string piece, Transform parent)
        {
            var go = Content.Spawn(Look(piece), parent, 0f, "model");
            var model = go.transform.GetChild(0);
            var b = model.GetComponent<MeshFilter>().sharedMesh.bounds;
            // Reach's wall blocks are thin along x: turn them so the thin side faces +z like our walls
            bool turn = b.size.x < b.size.z;
            var size = turn ? new Vector3(b.size.z, b.size.y, b.size.x) : b.size;
            var target = new Vector3(Grid, Height(piece), IsWallLike(piece) ? WallT : Grid);
            var k = new Vector3(target.x / size.x, target.y / size.y, target.z / size.z);
            go.transform.localRotation = turn ? Quaternion.Euler(0, 90, 0) : Quaternion.identity;
            model.localScale = turn ? new Vector3(k.z, k.y, k.x) : k;
            model.localPosition = new Vector3(-b.center.x * model.localScale.x, -b.min.y * model.localScale.y, -b.center.z * model.localScale.z);
            return go;
        }

        public static Color Tint(int grade) => ColorUtility.TryParseHtmlString(Rules.Grade(grade).Tint, out var c) ? c : Color.white;
    }

    public sealed class PieceView : MonoBehaviour
    {
        GameObject model;

        public void Build(Ent e)
        {
            model = PieceShape.Model(e.Sub, transform);
            foreach (var (c, s) in PieceShape.Boxes(e.Sub))
            {
                var b = gameObject.AddComponent<BoxCollider>();
                b.center = c; b.size = s;
            }
            SetGrade(e.Grade);
        }

        public void SetGrade(int grade) => Client.Tint(model, PieceShape.Tint(grade));
    }

    public sealed class DoorView : MonoBehaviour
    {
        Transform hinge;

        public void Build(Ent e)
        {
            hinge = new GameObject("hinge").transform;
            hinge.SetParent(transform, false);
            hinge.localPosition = new Vector3(-0.6f, 0, 0);
            var leaf = Content.Spawn("forge_door", hinge, 0f, "leaf");
            var model = leaf.transform.GetChild(0);
            var b = model.GetComponent<MeshFilter>().sharedMesh.bounds;
            bool turn = b.size.x < b.size.z;
            var size = turn ? new Vector3(b.size.z, b.size.y, b.size.x) : b.size;
            var k = new Vector3(1.2f / size.x, 2.3f / size.y, 0.12f / size.z);
            leaf.transform.localRotation = turn ? Quaternion.Euler(0, 90, 0) : Quaternion.identity;
            model.localScale = turn ? new Vector3(k.z, k.y, k.x) : k;
            model.localPosition = new Vector3(-b.center.x * model.localScale.x, -b.min.y * model.localScale.y, -b.center.z * model.localScale.z);
            leaf.transform.localPosition = new Vector3(0.6f, 0, 0);
            var col = hinge.gameObject.AddComponent<BoxCollider>();
            col.center = new Vector3(0.6f, 1.15f, 0); col.size = new Vector3(1.2f, 2.3f, 0.12f);
            Client.Tint(leaf, e.Sub == "door_metal" ? PieceShape.Tint(3) : PieceShape.Tint(1));
            SetOpen(e.Open);
        }

        public void SetOpen(bool open) => hinge.localRotation = Quaternion.Euler(0, open ? -95f : 0f, 0);
    }

    /// <summary>Tracers, plasma bolts, explosions. Halo's own sounds come in v0.2 (Reach keeps them in FMOD banks).</summary>
    public static class Effects
    {
        static Material lineMat;

        public static void Play(byte kind, Vector3 a, Vector3 b)
        {
            switch (kind)
            {
                case Server.FxTracer: Line(a, b, new Color(1f, 0.85f, 0.45f), 0.03f, 0.06f); break;
                case Server.FxPlasma: Line(a, b, new Color(0.4f, 0.9f, 1f), 0.08f, 0.15f); break;
                case Server.FxExplosion: Burst(a, 4f, new Color(1f, 0.6f, 0.2f), 0.6f); break;
                case Server.FxDud: Burst(a, 0.8f, new Color(0.6f, 0.6f, 0.6f), 0.4f); break;
                case Server.FxHit: Burst(a + Vector3.up, 0.5f, new Color(0.9f, 0.9f, 0.8f), 0.15f); break;
            }
        }

        static void Line(Vector3 a, Vector3 b, Color c, float width, float life)
        {
            if (lineMat == null) lineMat = new Material(Shader.Find("Sprites/Default"));
            var go = new GameObject("fx");
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = lineMat;
            lr.startColor = lr.endColor = c;
            lr.startWidth = lr.endWidth = width;
            lr.SetPositions(new[] { a, b });
            Object.Destroy(go, life);
        }

        static void Burst(Vector3 at, float size, Color c, float life)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.position = at;
            var mr = go.GetComponent<MeshRenderer>();
            if (lineMat == null) lineMat = new Material(Shader.Find("Sprites/Default"));
            mr.sharedMaterial = lineMat;
            go.AddComponent<Grow>().Init(size, c, life);
        }

        sealed class Grow : MonoBehaviour
        {
            float size, life, t; Color c; MaterialPropertyBlock mpb;
            public void Init(float s, Color col, float l) { size = s; c = col; life = l; mpb = new MaterialPropertyBlock(); }
            void Update()
            {
                t += Time.deltaTime;
                float k = t / life;
                transform.localScale = Vector3.one * Mathf.Lerp(0.2f, size * 2f, k);
                mpb.SetColor("_Color", new Color(c.r, c.g, c.b, 1f - k));
                GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
                if (k >= 1f) Destroy(gameObject);
            }
        }
    }
}
