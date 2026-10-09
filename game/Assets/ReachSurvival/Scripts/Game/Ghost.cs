using System.Collections.Generic;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Placement preview for the Forge Tool (base pieces) and placeable items. Snaps pieces Rust-style:
    /// foundations side by side, walls on foundation/floor edges and on top of walls, floors on top of walls.
    /// </summary>
    public sealed class Ghost : MonoBehaviour
    {
        readonly Dictionary<string, GameObject> previews = new();
        GameObject shown;
        string what;
        public Vector3 Pos { get; private set; }
        public float Yaw { get; private set; }
        public bool Valid { get; private set; }
        public int Target { get; private set; }

        public void Show(string id)
        {
            if (id != what)
            {
                if (shown) shown.SetActive(false);
                what = id;
                shown = id == null ? null : Preview(id);
            }
            if (id == null) return;
            Solve();
            shown.SetActive(Valid);
            if (Valid) shown.transform.SetPositionAndRotation(Pos, Quaternion.Euler(0, Yaw, 0));
        }

        GameObject Preview(string id)
        {
            if (previews.TryGetValue(id, out var go)) return go;
            if (IsPiece(id)) { go = new GameObject("ghost " + id); PieceShape.Model(id, go.transform); }
            else go = Content.Spawn(id switch { "beacon" => "beacon", "smelter" => "smelter", "armory1" => "armory", _ => "forge_door" }, null,
                id switch { "beacon" => 1.9f, "smelter" => 2.0f, "armory1" => 1.9f, _ => 2.3f }, "ghost " + id);
            Client.Tint(go, new Color(0.45f, 0.85f, 1f));
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return previews[id] = go;
        }

        static bool IsPiece(string id) => id is "foundation" or "wall" or "doorway" or "window" or "floor";

        void Solve()
        {
            Valid = false; Target = 0;
            var p = Game.Instance.LocalPlayer;
            if (what is "door_wood" or "door_metal")
            {
                // you look *through* a doorway's opening, so pick the doorway whose opening the aim passes through
                var ray = p.Aim;
                Ent best = null; float bestD = 1.0f;
                foreach (var e in Client.Instance.Ents.Values)
                {
                    if (e.Kind != EKind.Piece || e.Sub != "doorway") continue;
                    var mid = e.Pos + Vector3.up * 1.2f;
                    if ((mid - ray.origin).sqrMagnitude > 7f * 7f) continue;
                    float d = Vector3.Cross(ray.direction, mid - ray.origin).magnitude;
                    if (d < bestD) { bestD = d; best = e; }
                }
                if (best == null) return;
                Pos = best.Pos; Yaw = best.Yaw; Target = best.Id; Valid = true;
                return;
            }
            if (!Physics.Raycast(p.Aim, out var hit, 9f, ~0, QueryTriggerInteraction.Ignore)) return;
            var er = hit.collider.GetComponentInParent<EntityRef>();
            var on = Client.Instance.Find(er);

            if (!IsPiece(what))
            {
                if (hit.normal.y < 0.7f) return;
                Pos = hit.point; Yaw = p.Yaw + 180f; Valid = true;
                return;
            }

            if (what == "foundation")
            {
                if (on != null && on.Kind == EKind.Piece && on.Sub == "foundation")
                {
                    var d = Side(on, hit.point);
                    Pos = on.Pos + on.Rot * d * PieceShape.Grid; Yaw = on.Yaw; Valid = true;
                }
                else if (on == null && er == null && hit.normal.y > 0.6f)
                {
                    Pos = hit.point - Vector3.up * 0.15f; Yaw = Mathf.Round(p.Yaw / 15f) * 15f; Valid = true;
                }
                return;
            }

            if (on == null || on.Kind != EKind.Piece) return;

            if (PieceShape.IsWallLike(what))
            {
                if (on.Sub is "foundation" or "floor")
                {
                    var d = Side(on, hit.point);
                    Pos = on.Pos + on.Rot * d * (PieceShape.Grid / 2f) + Vector3.up * PieceShape.Height(on.Sub);
                    Yaw = on.Yaw + Quaternion.LookRotation(d).eulerAngles.y;
                    Valid = true;
                }
                else if (PieceShape.IsWallLike(on.Sub))
                {
                    Pos = on.Pos + Vector3.up * PieceShape.WallH; Yaw = on.Yaw; Valid = true;
                }
                return;
            }

            if (what == "floor")
            {
                if (PieceShape.IsWallLike(on.Sub))
                {
                    // inward from the wall: towards whichever side of it you're standing on
                    var fwd = on.Rot * Vector3.forward;
                    var inward = Vector3.Dot(p.transform.position - on.Pos, fwd) > 0 ? fwd : -fwd;
                    Pos = on.Pos + Vector3.up * PieceShape.WallH + inward * (PieceShape.Grid / 2f); Yaw = on.Yaw; Valid = true;
                }
                else if (on.Sub == "floor")
                {
                    var d = Side(on, hit.point);
                    Pos = on.Pos + on.Rot * d * PieceShape.Grid; Yaw = on.Yaw; Valid = true;
                }
            }
        }

        /// <summary>Which of the piece's four sides (local ±x/±z) the point is nearest to.</summary>
        static Vector3 Side(Ent piece, Vector3 point)
        {
            var local = Quaternion.Inverse(piece.Rot) * (point - piece.Pos);
            return Mathf.Abs(local.x) > Mathf.Abs(local.z) ? new Vector3(Mathf.Sign(local.x), 0, 0) : new Vector3(0, 0, Mathf.Sign(local.z));
        }

        public void Commit()
        {
            if (!Valid || what == null) return;
            if (IsPiece(what))
            {
                string piece = what; var pos = Pos; float yaw = Yaw;
                Player.Request(Msg.Place, w => { w.Write(piece); w.Write(pos); w.Write(yaw); });
            }
            else
            {
                string item = what; var pos = Pos; float yaw = Yaw; int target = Target;
                Player.Request(Msg.Deploy, w => { w.Write(item); w.Write(pos); w.Write(yaw); w.Write(target); });
            }
        }
    }
}
