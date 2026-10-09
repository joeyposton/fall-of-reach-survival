using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Scripted test run (--autotest &lt;name&gt;): plays the game through its real controls and requests, logs
    /// "[AUTOTEST]" results and saves screenshots to %LOCALAPPDATA%/ReachSurvival/shots, then quits.
    /// Steps that need items a short test can't gather use <see cref="Server.TestGrant"/> and say so in the log.
    /// </summary>
    public sealed class AutoTest : MonoBehaviour
    {
        public static bool Driving;
        public static Vector2 Move;
        string shots;
        Player p;
        Client c;

        IEnumerator Start()
        {
            Driving = true;
            shots = Path.Combine(Path.GetDirectoryName(Paths.Cache), "shots");
            Directory.CreateDirectory(shots);
            p = Game.Instance.LocalPlayer;
            c = Client.Instance;
            var name = Paths.Arg("autotest");
            yield return new WaitForSeconds(2f);
            Result("spawned", $"pos={p.transform.position} water={World.WaterLevel:0.0} swimming={p.Swimming} ents={c.Ents.Count}");
            p.Look(p.Yaw, 5f);
            yield return Shot("01_spawn");

            if (name == "host") { yield return HostTest(); Application.Quit(); yield break; }
            if (name == "join") { yield return JoinTest(); Application.Quit(); yield break; }
            if (name is "smoke" or "all")
            {
                Move = new Vector2(0, 1);
                var start = p.transform.position;
                yield return new WaitForSeconds(3f);
                Move = Vector2.zero;
                Result("walk", $"moved {Vector3.Distance(start, p.transform.position):0.0} m in 3 s");
            }
            if (name is "gather" or "all") yield return Gather();
            if (name is "build" or "all") yield return Build();
            if (name is "raid" or "all") yield return Raid();
            if (name is "smelt" or "all") yield return Smelt();

            Result("done", name);
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // ---- steps ---------------------------------------------------------------------------------

        IEnumerator Gather()
        {
            foreach (var kind in new[] { "tree", "titanium" })
            {
                string item = kind == "tree" ? "wood" : "stone";
                var node = Nearest(EKind.Node, e => e.Sub == kind);
                if (node == null) { Result($"gather {kind}", "FAIL no node"); continue; }
                yield return StandFacing(node.Pos + Vector3.up * 1.2f, 1.9f);
                int before = c.Inv.Total(item);
                for (int i = 0; i < 4; i++) { p.Melee(); yield return new WaitForSeconds(0.9f); }
                int after = c.Inv.Total(item);
                Result($"gather {kind}", $"{ItemDb.Name(item)} {before} -> {after} in 4 hits (Rust total {(kind == "tree" ? Rules.TreeWood.ToString() : "design")} / 10 per hit) {(after > before ? "PASS" : "FAIL")}");
                yield return Shot($"10_gather_{kind}");
            }
        }

        IEnumerator Build()
        {
            if (c.Inv.Total("wood") < 700) TestGrant("wood", 700 - c.Inv.Total("wood"));
            // find flat dry ground ahead
            var spot = World.RandomSpot(new System.Random(7));
            p.Teleport(spot);
            yield return new WaitForSeconds(0.5f);
            int pieces0 = Count(EKind.Piece);

            p.SetForgePiece("foundation");
            var ground = p.transform.position + p.transform.forward * 4f;
            ground = World.Ground(ground.x, ground.z, 0.5f) ?? ground;
            yield return LookAt(ground);
            yield return Commit();
            var f = Newest(EKind.Piece);
            Result("place foundation", f != null && f.Sub == "foundation" ? $"PASS hp {f.Hp}/{f.MaxHp} (Rust twigs {Content.Rust.Grades["twigs"].Health} x Foundation {Content.Rust.Pieces["Foundation"].HealthMultiplier})" : "FAIL");
            if (f == null) yield break;

            // walls on three edges and a doorway on the fourth, aimed at each edge of the foundation's top
            string[] kinds = { "wall", "wall", "window", "doorway" };
            var dirs = new[] { Vector3.forward, Vector3.right, Vector3.left, Vector3.back };
            for (int i = 0; i < 4; i++)
            {
                p.SetForgePiece(kinds[i]);
                // stand on the foundation and look down at the edge, as a player would
                var edge = f.Pos + f.Rot * dirs[i] * 1.2f + Vector3.up * PieceShape.FoundationH;
                p.Teleport(f.Pos + f.Rot * -dirs[i] * 0.6f + Vector3.up * PieceShape.FoundationH);
                yield return new WaitForSeconds(0.2f);
                yield return LookAt(edge);
                yield return Commit();
                if (Newest(EKind.Piece)?.Sub != kinds[i]) Result($"place {kinds[i]}", "FAIL (aim missed the edge)");
            }
            Result("place walls", $"{Count(EKind.Piece) - pieces0} pieces placed (expected 5) {(Count(EKind.Piece) - pieces0 == 5 ? "PASS" : "FAIL")}");
            yield return Back(f, 9f);
            yield return Shot("20_base_frame");

            // upgrade one wall: twigs -> wood costs Rust's wood grade cost (200 wood x Wall multiplier)
            var wall = c.Ents.Values.Where(e => e.Kind == EKind.Piece && e.Sub == "wall").OrderByDescending(e => e.Id).FirstOrDefault();
            if (wall == null) { Result("upgrade wall", "FAIL no wall"); yield break; }
            p.Equip(Player.Tool.Upgrade);
            yield return StandFacing(wall.Pos + Vector3.up * 1.5f, 3f);
            int woodBefore = c.Inv.Total("wood");
            p.UpgradeLooked();
            yield return new WaitForSeconds(0.5f);
            Result("upgrade wall", $"grade {Rules.Grade(wall.Grade).HaloName} hp {wall.Hp}/{wall.MaxHp}, wood {woodBefore} -> {c.Inv.Total("wood")} {(wall.Grade == 1 && wall.MaxHp == Content.Rust.Grades["wood"].Health ? "PASS" : "FAIL")}");

            // door in the doorway (Rust's wooden door health)
            TestGrant("door_wood", 1);
            yield return new WaitForSeconds(0.3f);
            var way = c.Ents.Values.FirstOrDefault(e => e.Kind == EKind.Piece && e.Sub == "doorway");
            if (way == null) { Result("door", "FAIL no doorway"); yield break; }
            p.SetPlaceItem("door_wood");
            yield return StandFacing(way.Pos + Vector3.up * 1.2f, 3f);
            yield return Commit();
            var door = Newest(EKind.Door);
            Result("door", door != null ? $"PASS hp {door.Hp} (Rust door.hinged.wood {Content.Rust.Doors["door.hinged.wood"]})" : "FAIL");
            yield return Back(f, 7f);
            yield return Shot("21_base_upgraded");
        }

        IEnumerator Raid()
        {
            var f = c.Ents.Values.FirstOrDefault(e => e.Kind == EKind.Piece && e.Sub == "foundation");
            TestGrant("beacon", 1);
            TestGrant("ar_ammo", 200);
            yield return new WaitForSeconds(0.3f);
            p.SetPlaceItem("beacon");
            // inside the base: stand on the foundation and set the beacon down beside you
            Vector3 spot;
            if (f != null)
            {
                p.Teleport(f.Pos + f.Rot * Vector3.forward * 0.8f + Vector3.up * PieceShape.FoundationH);
                spot = f.Pos + f.Rot * Vector3.back * 0.7f + Vector3.up * PieceShape.FoundationH;
            }
            else spot = p.transform.position + p.transform.forward * 3f;
            yield return new WaitForSeconds(0.2f);
            yield return LookAt(spot);
            yield return Commit();
            var beacon = Newest(EKind.Beacon);
            Result("beacon", beacon != null ? $"PASS hp {beacon.Hp} (Rust cupboard {Content.Rust.CupboardHealth})" : "FAIL");
            if (beacon == null) yield break;

            Server.Instance.RaidNow();
            yield return new WaitForSeconds(1f);
            int squad = Count(EKind.Enemy);
            Result("raid spawn", $"{squad} Covenant ({string.Join(", ", c.Ents.Values.Where(e => e.Kind == EKind.Enemy).Select(e => e.Sub))}) {(squad > 0 ? "PASS" : "FAIL")}");
            p.Equip(Player.Tool.Rifle);
            p.Interact(0, "reload");
            // hold fire near the squad for a few seconds: they should shoot back with Reach's plasma weapons
            var first = Nearest(EKind.Enemy, _ => true);
            if (first != null)
            {
                var to = first.Pos - p.transform.position; to.y = 0;
                var near = first.Pos - to.normalized * 20f;
                var g0 = World.Ground(near.x, near.z, 0.3f);
                if (g0.HasValue) p.Teleport(g0.Value);
                yield return LookAt(c.Views[first.Id].transform.position + Vector3.up);
                float s0 = p.Vitals.Shield + p.Vitals.Body;
                yield return new WaitForSeconds(6f);
                yield return Shot("30_raid_incoming");
                Result("covenant fire", $"Spartan {s0:0} -> {p.Vitals.Shield + p.Vitals.Body:0} after 6 s holding fire {(p.Vitals.Shield + p.Vitals.Body < s0 ? "PASS" : "FAIL")}");
            }

            float until = Time.time + 60f;
            int kills = 0, shotsTaken = 0, shotN = 0;
            float nextShot = 0f;
            float shield0 = p.Vitals.Shield;
            while (Time.time < until && Count(EKind.Enemy) > 0)
            {
                var foe = Nearest(EKind.Enemy, _ => true);
                if (foe == null) break;
                var view = c.Views[foe.Id].transform.position + Vector3.up * (foe.Sub == "elite" ? 1.4f : 0.8f);
                yield return LookAt(view, 0f);
                if ((foe.Pos - p.transform.position).magnitude < 50f)
                {
                    p.Fire();
                    if (c.Mag == 0 && !c.Reloading) p.Interact(0, "reload");
                }
                else
                {
                    // close the distance like a player would, without walking through our own walls
                    var to = (foe.Pos - p.transform.position); to.y = 0;
                    var at = foe.Pos - to.normalized * 30f;
                    var g = World.Ground(at.x, at.z, 0.3f);
                    if (g.HasValue) p.Teleport(g.Value);
                }
                if (p.Vitals.Shield < shield0) shotsTaken++;
                if (c.Dead) { Result("raid", "Spartan died (respawning to carry on)"); yield return Respawn(); }
                if (Time.time > nextShot) { nextShot = Time.time + 6f; yield return Shot($"3{kills}_raid_fight_{shotN++}"); }
                int before = Count(EKind.Enemy);
                yield return null;
                Move = Vector2.zero;
                if (Count(EKind.Enemy) < before) { kills++; yield return Shot($"3{kills}_kill"); }
            }
            int drops = Count(EKind.Drop);
            kills = squad - Count(EKind.Enemy);
            Result("raid", $"killed {kills}/{squad}, drops on the ground {drops}, took fire {(shotsTaken > 0 ? "yes" : "no")} {(kills == squad ? "PASS" : "FAIL")}");

            if (c.Dead) yield return Respawn();
            // satchel on a wall (Rust: 6-12 s fuse, 20% dud)
            var wall = c.Ents.Values.Where(e => e.Kind == EKind.Piece && e.Sub == "wall").OrderBy(e => e.Grade).FirstOrDefault();
            if (wall != null)
            {
                TestGrant("satchel", 1);
                yield return new WaitForSeconds(0.3f);
                p.Equip(Player.Tool.Satchel);
                yield return StandFacing(wall.Pos + Vector3.up * 1.2f, 3f);
                float hp0 = wall.Hp;
                p.ThrowSatchel();
                yield return new WaitForSeconds(0.3f);
                Result("satchel thrown", Count(EKind.Satchel) > 0 ? "PASS" : "FAIL");
                var away = wall.Pos + (p.transform.position - wall.Pos).normalized * 14f;
                p.Teleport(World.Ground(away.x, away.z, 0.3f) ?? away);
                yield return LookAt(wall.Pos + Vector3.up);
                yield return Shot("40_satchel_armed");
                float t = Time.time + 14f;
                while (Time.time < t && Count(EKind.Satchel) > 0) yield return null;
                yield return Shot("41_satchel_after");
                bool gone = !c.Ents.ContainsKey(wall.Id);
                Result("satchel", gone ? "PASS wall destroyed" : c.Ents.Values.Any(e => e.Kind == EKind.Drop && e.Sub == "dud") ? "PASS (dud: Rust's 20% chance)" : $"wall hp {hp0} -> {wall.Hp} {(wall.Hp < hp0 ? "PASS" : "FAIL")}");
            }
        }

        IEnumerator Smelt()
        {
            if (c.Dead) yield return Respawn();
            TestGrant("smelter", 1); TestGrant("metal_ore", 30); TestGrant("wood", 40);
            yield return new WaitForSeconds(0.3f);
            p.SetPlaceItem("smelter");
            p.Teleport(World.RandomSpot(new System.Random(99)));
            yield return new WaitForSeconds(0.3f);
            var spot = p.transform.position + p.transform.forward * 3.5f;
            spot = World.Ground(spot.x, spot.z, 0.5f) ?? spot;
            yield return LookAt(spot);
            yield return Commit();
            var sm = Newest(EKind.Smelter);
            if (sm == null) { Result("smelter", "FAIL not placed"); yield break; }
            yield return StandFacing(sm.Pos + Vector3.up, 2.5f);
            p.Interact(sm.Id, "put", "metal_ore", 30);
            p.Interact(sm.Id, "put", "wood", 40);
            p.Interact(sm.Id, "light");
            yield return new WaitForSeconds(Content.Rust.Cooking["metal.ore"].Time * 2 + 1f);
            p.Interact(sm.Id, "open");
            yield return new WaitForSeconds(0.3f);
            int frags = c.Storage.Total("metal");
            Result("smelt", $"{frags} {ItemDb.Name("metal")} after {Content.Rust.Cooking["metal.ore"].Time * 2 + 1:0}s, charcoal {c.Storage.Total("charcoal")} (Rust: {Content.Rust.Cooking["metal.ore"].Time}s per ore, wood->charcoal {Content.Rust.Burning["wood"].ByproductChance:P0}) {(frags > 0 ? "PASS" : "FAIL")}");
            Hud.Instance.OpenStorage(sm.Id);
            yield return Shot("50_smelter");
            Hud.Instance.OpenStorage(0);
        }

        // ---- playing together (two copies: --host and --join <code>) --------------------------------

        IEnumerator HostTest()
        {
            float until = Time.time + 60f;
            while (OnlineNet.Instance?.Session == null && Time.time < until) yield return null;
            Result("host session", OnlineNet.Instance?.Session != null ? $"PASS code {OnlineNet.Instance.Session.Code}" : $"FAIL {OnlineNet.Status}");
            if (OnlineNet.Instance?.Session == null) yield break;
            until = Time.time + 180f;
            while (c.Names.Count < 2 && Time.time < until) yield return null;
            Result("friend joined", c.Names.Count >= 2 ? $"PASS {string.Join(", ", c.Names.Values)}" : "FAIL nobody joined");
            int pieces0 = Count(EKind.Piece);
            // watch the friend for a while: they build in front of themselves
            until = Time.time + 45f;
            while (Count(EKind.Piece) == pieces0 && Time.time < until) yield return null;
            var piece = Newest(EKind.Piece);
            Result("friend's building seen by host", piece != null && Count(EKind.Piece) > pieces0 ? $"PASS {piece.Sub} owner {piece.Owner}" : "FAIL");
            if (piece != null)
            {
                yield return StandFacing(piece.Pos + Vector3.up, 6f);
                yield return Shot("60_host_sees_friend");
            }
            yield return new WaitForSeconds(10f);
        }

        IEnumerator JoinTest()
        {
            Result("joined", $"PASS as {Net.LocalId}, world has {c.Ents.Count} things from the host");
            float until = Time.time + 10f;
            while (c.Names.Count < 2 && Time.time < until) yield return null;
            Result("host visible", c.Names.Count >= 2 ? $"PASS {string.Join(", ", c.Names.Values)}" : "FAIL");
            var tree = Nearest(EKind.Node, e => e.Sub == "tree");
            if (tree != null)
            {
                yield return StandFacing(tree.Pos + Vector3.up * 1.2f, 1.9f);
                for (int i = 0; i < 3 && c.Inv.Total("wood") < 60; i++) { p.Melee(); yield return new WaitForSeconds(1.0f); }
            }
            Result("joiner gathered", $"{c.Inv.Total("wood")} Carbon Fibre {(c.Inv.Total("wood") > 0 ? "PASS" : "FAIL")}");
            p.SetForgePiece("foundation");
            var spot = World.RandomSpot(new System.Random(Net.LocalId.GetHashCode() + 5));
            p.Teleport(spot);
            yield return new WaitForSeconds(0.5f);
            var ground = p.transform.position + p.transform.forward * 4f;
            ground = World.Ground(ground.x, ground.z, 0.5f) ?? ground;
            yield return LookAt(ground);
            yield return Commit();
            var f = Newest(EKind.Piece);
            Result("joiner built", f != null && f.Owner == Net.LocalId ? "PASS foundation placed through the host" : "FAIL");
            yield return Back(f ?? new Ent { Pos = ground }, 7f);
            yield return Shot("61_joiner_view");
            yield return new WaitForSeconds(25f);
        }

        // ---- helpers -------------------------------------------------------------------------------

        IEnumerator Respawn()
        {
            float until = Time.time + 15f;
            while (c.Dead && Time.time < until) yield return null; // Player sends the respawn request while driving
            yield return new WaitForSeconds(0.5f);
        }

        void TestGrant(string item, int n)
        {
            Server.Instance.TestGrant(Net.LocalId, item, n);
            Result("test grant", $"{n} {ItemDb.Name(item)} (test-only, skips gathering)");
        }

        Ent Nearest(EKind kind, System.Func<Ent, bool> ok) =>
            c.Ents.Values.Where(e => e.Kind == kind && ok(e)).OrderBy(e => (e.Pos - p.transform.position).sqrMagnitude).FirstOrDefault();

        Ent Newest(EKind kind) => c.Ents.Values.Where(e => e.Kind == kind).OrderByDescending(e => e.Id).FirstOrDefault();
        int Count(EKind kind) => c.Ents.Values.Count(e => e.Kind == kind);

        IEnumerator StandFacing(Vector3 target, float dist, Vector3? from = null)
        {
            var flat = (from ?? p.transform.position) - target; flat.y = 0;
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.back;
            var at = target + flat.normalized * dist;
            var g = World.Ground(at.x, at.z, 0f);
            // stand on whatever is there (a foundation, the terrain)
            if (Physics.Raycast(at + Vector3.up * 6f, Vector3.down, out var hit, 20f, ~0, QueryTriggerInteraction.Ignore)) p.Teleport(hit.point);
            else if (g.HasValue) p.Teleport(g.Value);
            yield return new WaitForSeconds(0.2f);
            yield return LookAt(target);
        }

        IEnumerator LookAt(Vector3 target, float settle = 0.15f)
        {
            var d = target - p.Cam.transform.position;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            p.Look(yaw, pitch);
            yield return null;
            if (settle > 0) yield return new WaitForSeconds(settle);
        }

        IEnumerator Commit()
        {
            yield return null;
            p.GetComponent<Ghost>().Commit();
            yield return new WaitForSeconds(0.4f);
        }

        IEnumerator Back(Ent around, float dist)
        {
            var dir = Quaternion.Euler(0, 35f, 0) * (around.Rot * Vector3.back);
            var at = around.Pos + dir * dist;
            p.Teleport(World.Ground(at.x, at.z, 0f) ?? at);
            yield return LookAt(around.Pos + Vector3.up * 1.5f);
        }

        public IEnumerator Shot(string name)
        {
            yield return null; // let this frame's Update apply any look/move change first
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(shots, name + ".png"));
            yield return null;
            yield return null;
        }

        public static void Result(string what, string detail) => Debug.Log($"[AUTOTEST] {what}: {detail}");
    }
}
