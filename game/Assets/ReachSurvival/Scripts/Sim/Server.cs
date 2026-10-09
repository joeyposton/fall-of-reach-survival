using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Runs on the host only and owns the truth: Spartans' health and inventories, nodes, bases, smelters,
    /// explosives and Covenant raids. Clients send requests; this validates them against Rust's rules (read from
    /// the player's Rust) and Halo's stats (read from the player's Reach), then tells everyone what happened.
    /// </summary>
    public sealed partial class Server : MonoBehaviour
    {
        public static Server Instance { get; private set; }

        sealed class Spartan
        {
            public ulong Id;
            public string Name = "Spartan";
            public Vector3 Pos;
            public float Yaw, Pitch;
            public VitalsState Vitals;
            public readonly Inventory Inv = new();
            public int Mag;
            public float NextFire, ReloadUntil, RespawnAt, NextMelee;
            public bool Spawned;
        }

        readonly Dictionary<ulong, Spartan> spartans = new();
        readonly Dictionary<int, Ent> ents = new();
        readonly Dictionary<int, int> nodeLeft = new();           // node id -> amount left
        readonly Dictionary<int, Inventory> storage = new();      // beacon / smelter / drop contents
        readonly Dictionary<int, HashSet<ulong>> authorized = new(); // beacon id -> players
        readonly Dictionary<int, float> fuse = new();              // satchel id -> explode time (or -1 = dud)
        readonly HashSet<string> occupied = new();
        int nextId = 1;
        float nextSnapshot;
        System.Random rng;

        // design.json values
        int hitsPerNode, meleeDamage; float reloadSeconds, respawnSeconds, privilegeRadius;

        void Awake() => Instance = this;

        public void Begin(int seed)
        {
            rng = new System.Random(seed);
            hitsPerNode = int.Parse(Rules.Design("hits_per_node"));
            meleeDamage = int.Parse(Rules.Design("melee_damage"));
            reloadSeconds = float.Parse(Rules.Design("reload_s"), System.Globalization.CultureInfo.InvariantCulture);
            respawnSeconds = float.Parse(Rules.Design("respawn_s"), System.Globalization.CultureInfo.InvariantCulture);
            privilegeRadius = float.Parse(Rules.Design("privilege_radius_m"), System.Globalization.CultureInfo.InvariantCulture);
            ScatterNodes();
            BeginRaids();
        }

        // ---- nodes ----------------------------------------------------------------------------------

        void ScatterNodes()
        {
            // "tree 140; titanium 70; alloy 50; plasma 40"
            foreach (var part in Rules.Design("node_count").Split(';'))
            {
                var bits = part.Trim().Split(' ');
                int n = int.Parse(bits[1]);
                for (int i = 0; i < n; i++)
                {
                    var at = World.RandomSpot(rng);
                    var e = Add(EKind.Node, bits[0], at, (float)rng.NextDouble() * 360f);
                    nodeLeft[e.Id] = NodeTotal(bits[0]);
                }
            }
        }

        static (string item, int amount) NodeYield(string node) => node switch
        {
            "tree" => ("wood", Rules.TreeWood),
            "titanium" => ("stone", OreYield("titanium")),
            "alloy" => ("metal_ore", OreYield("alloy")),
            _ => ("sulfur_ore", OreYield("plasma")),
        };

        static int OreYield(string node)
        {
            // "titanium node 1000 items.stone; alloy node 500 items.metal_ore; plasma node 500 items.sulfur_ore"
            foreach (var part in Rules.Design("ore_yield").Split(';'))
            {
                var b = part.Trim().Split(' ');
                if (b[0] == node) return int.Parse(b[2]);
            }
            return 500;
        }

        static int NodeTotal(string node) => NodeYield(node).amount;

        // ---- entities -------------------------------------------------------------------------------

        Ent Add(EKind kind, string sub, Vector3 pos, float yaw, float hp = 0f, ulong owner = 0, int parent = 0, int grade = 0)
        {
            var e = new Ent { Id = nextId++, Kind = kind, Sub = sub, Pos = pos, Yaw = yaw, Hp = hp, MaxHp = hp, Owner = owner, Parent = parent, Grade = grade };
            ents[e.Id] = e;
            var w = Net.Begin(Msg.Spawn, out var ms); e.Write(w); Net.ToAll(ms);
            return e;
        }

        void Remove(int id)
        {
            if (!ents.Remove(id, out var e)) return;
            if (e.Kind == EKind.Piece) occupied.Remove(SlotKey(e));
            storage.Remove(id); authorized.Remove(id); fuse.Remove(id); nodeLeft.Remove(id); enemies.Remove(id);
            var w = Net.Begin(Msg.Remove, out var ms); w.Write(id); Net.ToAll(ms);
            // doors fall with their doorway; satchels with what they stuck to
            foreach (var child in ents.Values.Where(x => x.Parent == id && x.Kind is EKind.Door or EKind.Satchel).Select(x => x.Id).ToList()) Remove(child);
        }

        void SendHealth(Ent e)
        {
            var w = Net.Begin(Msg.Health, out var ms);
            w.Write(e.Id); w.Write(e.Hp); w.Write(e.MaxHp); w.Write((byte)e.Grade); w.Write(e.Open);
            Net.ToAll(ms);
        }

        void Toast(ulong client, string text)
        {
            var w = Net.Begin(Msg.Toast, out var ms); w.Write(text); Net.ToClient(client, ms);
        }

        void SendInv(Spartan s)
        {
            var w = Net.Begin(Msg.Inv, out var ms);
            s.Inv.Write(w); w.Write(s.Mag); w.Write(s.ReloadUntil > Time.time);
            Net.ToClient(s.Id, ms);
        }

        void Fx(byte kind, Vector3 a, Vector3 b)
        {
            var w = Net.Begin(Msg.Fx, out var ms); w.Write(kind); w.Write(a); w.Write(b); Net.ToAll(ms, reliable: false);
        }

        public const byte FxTracer = 0, FxPlasma = 1, FxExplosion = 2, FxDud = 3, FxHit = 4;

        // ---- receive --------------------------------------------------------------------------------

        public void Receive(ulong from, byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var m = (Msg)r.ReadByte();
            if (m == Msg.Hello) { Join(from, r.ReadString()); return; }
            if (!spartans.TryGetValue(from, out var s)) return;
            switch (m)
            {
                case Msg.PlayerState:
                    if (s.Vitals.Dead) break;
                    s.Pos = r.ReadV3(); s.Yaw = r.ReadSingle(); s.Pitch = r.ReadSingle();
                    break;
                case Msg.Fire: Fire(s, r.ReadV3(), r.ReadV3()); break;
                case Msg.Melee: Melee(s, r.ReadV3(), r.ReadV3()); break;
                case Msg.Place: Place(s, r.ReadString(), r.ReadV3(), r.ReadSingle()); break;
                case Msg.Upgrade: Upgrade(s, r.ReadInt32()); break;
                case Msg.Deploy: Deploy(s, r.ReadString(), r.ReadV3(), r.ReadSingle(), r.ReadInt32()); break;
                case Msg.Craft: Craft(s, r.ReadString()); break;
                case Msg.Interact: Interact(s, r.ReadInt32(), r.ReadString(), r.ReadString(), r.ReadInt32()); break;
                case Msg.Respawn: Respawn(s, r.ReadBoolean()); break;
                case Msg.ThrowExplosive: Throw(s, r.ReadString(), r.ReadV3(), r.ReadInt32()); break;
            }
        }

        /// <summary>A friend's game disconnected: their Spartan leaves the island (their base stays, like Rust).</summary>
        public void Leave(ulong id)
        {
            if (spartans.Remove(id, out var s)) Debug.Log($"[server] {s.Name} left");
        }

        void Join(ulong id, string name)
        {
            if (!spartans.TryGetValue(id, out var s))
            {
                s = new Spartan { Id = id, Name = name, Vitals = VitalsState.Of("stats_spartan_vitality") };
                spartans[id] = s;
                s.Pos = World.RandomSpot(rng);
                // crash-landed: one MA37 magazine, a Forge Tool and an Upgrade Tool
                s.Mag = int.Parse(Rules.Design("crash_ammo"));
                s.Inv.Add("planner", 1);
                s.Inv.Add("hammer", 1);
                s.Spawned = true;
            }
            var w = Net.Begin(Msg.Welcome, out var ms);
            w.Write(id); w.Write(s.Pos);
            w.Write(ents.Count);
            foreach (var e in ents.Values) e.Write(w);
            Net.ToClient(id, ms);
            SendInv(s);
            Debug.Log($"[server] {name} joined as {id}");
        }

        /// <summary>Test runs only (AutoTest): puts items straight into a Spartan's inventory.</summary>
        public void TestGrant(ulong id, string item, int n)
        {
            if (Paths.Arg("autotest") == null || !spartans.TryGetValue(id, out var s)) return;
            s.Inv.Add(item, n);
            SendInv(s);
        }

        // ---- combat ---------------------------------------------------------------------------------

        void Fire(Spartan s, Vector3 origin, Vector3 dir)
        {
            if (s.Vitals.Dead || Time.time < s.NextFire || Time.time < s.ReloadUntil || s.Mag <= 0) return;
            if ((origin - (s.Pos + Vector3.up * 1.75f)).sqrMagnitude > 9f) origin = s.Pos + Vector3.up * 1.75f; // keep shots honest
            var st = Content.HaloStats["stats_assault_rifle"];
            s.NextFire = Time.time + 1f / Mathf.Max(1f, st["roundsPerSecondMax"]) * 0.9f;
            s.Mag--;
            float dmg = UnityEngine.Random.Range(st["damageLower"], st["damageUpperMax"]);
            var end = HitScan(origin, dir.normalized, 300f, s.Id, dmg, Rules.Bullet);
            Fx(FxTracer, origin + dir.normalized * 0.8f, end);
            if (s.Mag == 0) SendInv(s);
        }

        void Melee(Spartan s, Vector3 origin, Vector3 dir)
        {
            if (s.Vitals.Dead || Time.time < s.NextMelee) return;
            s.NextMelee = Time.time + 0.8f;
            if (!Physics.Raycast(origin, dir.normalized, out var hit, 3.0f, ~0, QueryTriggerInteraction.Ignore)) return;
            var er = hit.collider.GetComponentInParent<EntityRef>();
            if (er == null) return;
            if (!er.IsPlayer && ents.TryGetValue(er.EntId, out var e) && e.Kind == EKind.Node) { Gather(s, e); return; }
            ApplyDamage(er, meleeDamage, Rules.Blunt, s.Id, hit.point);
        }

        /// <summary>Raycast for the host; damages what it hits. Returns the end point for the tracer.</summary>
        Vector3 HitScan(Vector3 origin, Vector3 dir, float range, ulong shooter, float dmg, int damageType)
        {
            var hits = Physics.RaycastAll(origin, dir, range, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
            foreach (var h in hits)
            {
                var er = h.collider.GetComponentInParent<EntityRef>();
                if (er != null && er.IsPlayer && er.PlayerId == shooter) continue;
                if (er != null) ApplyDamage(er, dmg, damageType, shooter, h.point);
                return h.point;
            }
            return origin + dir * range;
        }

        void ApplyDamage(EntityRef er, float dmg, int damageType, ulong attacker, Vector3 at)
        {
            if (er.IsPlayer)
            {
                if (spartans.TryGetValue(er.PlayerId, out var victim)) HurtSpartan(victim, dmg, attacker);
                return;
            }
            if (ents.TryGetValue(er.EntId, out var e)) HurtEnt(e, dmg, damageType, attacker);
        }

        void HurtSpartan(Spartan v, float dmg, ulong attacker)
        {
            if (v.Vitals.Dead) return;
            v.Vitals.Damage(dmg, Time.time);
            if (v.Vitals.Dead)
            {
                v.RespawnAt = Time.time + respawnSeconds;
                // like Rust: you drop what you carried
                var bag = Add(EKind.Drop, "spartan", v.Pos + Vector3.up * 0.5f, 0f, 1f);
                var inv = new Inventory();
                for (int i = 0; i < Inventory.SlotCount; i++)
                    if (v.Inv.Item[i] != null && v.Inv.Item[i] != "planner" && v.Inv.Item[i] != "hammer") { inv.Add(v.Inv.Item[i], v.Inv.Count[i]); v.Inv.Item[i] = null; v.Inv.Count[i] = 0; }
                storage[bag.Id] = inv;
                SendInv(v);
                Toast(v.Id, attacker == v.Id ? "You died." : spartans.TryGetValue(attacker, out var k) ? $"Killed by {k.Name}" : "Killed by the Covenant");
                if (spartans.TryGetValue(attacker, out var killer) && killer != v) Toast(killer.Id, $"You killed {v.Name}");
            }
        }

        void HurtEnt(Ent e, float dmg, int damageType, ulong attacker)
        {
            switch (e.Kind)
            {
                case EKind.Piece:
                    dmg = Rules.PieceDamage(e.Grade, damageType, dmg);
                    break;
                case EKind.Door:
                    dmg = Rules.AfterProtection(dmg, damageType, Rules.RustGrade(e.Sub == "door_metal" ? 3 : 1).Protection);
                    break;
                case EKind.Beacon: case EKind.Smelter: case EKind.Armory:
                    dmg = Rules.AfterProtection(dmg, damageType, Rules.RustGrade(1).Protection);
                    break;
                case EKind.Enemy:
                    HurtEnemy(e, dmg, attacker);
                    return;
                default:
                    return;
            }
            if (dmg <= 0f) return;
            e.Hp -= dmg;
            if (e.Hp <= 0f) Remove(e.Id); else SendHealth(e);
        }

        void Respawn(Spartan s, bool atBeacon)
        {
            if (!s.Vitals.Dead || Time.time < s.RespawnAt) return;
            var spot = World.RandomSpot(rng);
            if (atBeacon)
            {
                var b = ents.Values.FirstOrDefault(x => x.Kind == EKind.Beacon && authorized.TryGetValue(x.Id, out var a) && a.Contains(s.Id));
                if (b != null) spot = b.Pos + b.Rot * Vector3.forward * 2f;
            }
            s.Vitals.Restore();
            s.Pos = spot;
            s.Mag = int.Parse(Rules.Design("crash_ammo"));
            SendInv(s);
            var w = Net.Begin(Msg.Welcome, out var ms);
            w.Write(s.Id); w.Write(spot); w.Write(0);
            Net.ToClient(s.Id, ms);
        }

        // ---- gathering & crafting -------------------------------------------------------------------

        void Gather(Spartan s, Ent node)
        {
            var (item, total) = NodeYield(node.Sub);
            int per = Mathf.CeilToInt(total / (float)hitsPerNode);
            int left = nodeLeft[node.Id];
            int got = Mathf.Min(per, left);
            nodeLeft[node.Id] = left - got;
            int spill = s.Inv.Add(item, got);
            if (spill > 0) Toast(s.Id, "Inventory full");
            Fx(FxHit, node.Pos, node.Pos);
            SendInv(s);
            if (nodeLeft[node.Id] <= 0)
            {
                Remove(node.Id);
                // Rust nodes respawn elsewhere on the map
                var e = Add(EKind.Node, node.Sub, World.RandomSpot(rng), (float)rng.NextDouble() * 360f);
                nodeLeft[e.Id] = NodeTotal(node.Sub);
            }
        }

        void Craft(Spartan s, string item)
        {
            var bp = ItemDb.Recipe(item);
            if (bp == null || !ItemDb.InV01(item)) return;
            if (bp.Workbench > 0 && !NearArmory(s.Pos, bp.Workbench)) { Toast(s.Id, $"Needs an Armory (Tier {bp.Workbench}) nearby"); return; }
            if (!s.Inv.TakeAll(Rules.Ingredients(bp))) { Toast(s.Id, "Not enough materials"); return; }
            // Rust's craft time runs on the host; the item arrives when it's done
            StartCoroutine(Deliver(s, item, Mathf.Max(1, bp.Amount), bp.Time));
            SendInv(s);
            Toast(s.Id, $"Crafting {ItemDb.Name(item)} ({bp.Time:0}s)");
        }

        System.Collections.IEnumerator Deliver(Spartan s, string item, int amount, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (s.Inv.Add(item, amount) > 0) Toast(s.Id, "Inventory full");
            Toast(s.Id, $"{ItemDb.Name(item)} x{amount} crafted");
            SendInv(s);
        }

        bool NearArmory(Vector3 p, int level) =>
            level <= 1 && ents.Values.Any(e => e.Kind == EKind.Armory && (e.Pos - p).sqrMagnitude < 8f * 8f);

        // ---- building -------------------------------------------------------------------------------

        /// <summary>Occupancy key: one piece per grid slot (rounded centre) and family.</summary>
        static string SlotKey(Ent e) => SlotKey(e.Sub, e.Pos);
        static string SlotKey(string piece, Vector3 p)
        {
            string family = piece is "foundation" ? "F" : piece is "floor" ? "C" : "W";
            return $"{family}:{Mathf.RoundToInt(p.x * 2)}:{Mathf.RoundToInt(p.y * 2)}:{Mathf.RoundToInt(p.z * 2)}";
        }

        /// <summary>Building blocked inside someone else's beacon zone (Rust's building privilege).</summary>
        bool Blocked(Spartan s, Vector3 p) => ents.Values.Any(b =>
            b.Kind == EKind.Beacon && (b.Pos - p).sqrMagnitude < privilegeRadius * privilegeRadius &&
            !(authorized.TryGetValue(b.Id, out var a) && a.Contains(s.Id)));

        void Place(Spartan s, string pieceId, Vector3 pos, float yaw)
        {
            var piece = Pieces.All.FirstOrDefault(p => p.Id == pieceId);
            if (piece == null || s.Vitals.Dead) return;
            if ((pos - s.Pos).sqrMagnitude > 12f * 12f) return;
            if (Blocked(s, pos)) { Toast(s.Id, "Building blocked by another base's beacon"); return; }
            var key = SlotKey(pieceId, pos);
            if (occupied.Contains(key)) return;
            if (!s.Inv.TakeAll(Rules.PieceCost(piece, 0))) { Toast(s.Id, "Not enough materials"); SendInv(s); return; }
            occupied.Add(key);
            Add(EKind.Piece, pieceId, pos, yaw, Rules.PieceHealth(piece, 0), s.Id);
            SendInv(s);
        }

        void Upgrade(Spartan s, int id)
        {
            if (!ents.TryGetValue(id, out var e) || e.Kind != EKind.Piece) return;
            var piece = Pieces.All.First(p => p.Id == e.Sub);
            int next = e.Grade + 1;
            if (next > 3) { Toast(s.Id, "Forerunner grade comes in v0.2"); return; }
            if (Blocked(s, e.Pos)) { Toast(s.Id, "Building blocked by another base's beacon"); return; }
            if ((e.Pos - s.Pos).sqrMagnitude > 8f * 8f) return;
            if (!s.Inv.TakeAll(Rules.PieceCost(piece, next))) { Toast(s.Id, $"Needs {Cost(Rules.PieceCost(piece, next))}"); return; }
            e.Grade = next;
            e.MaxHp = e.Hp = Rules.PieceHealth(piece, next);
            SendHealth(e);
            SendInv(s);
        }

        static string Cost(List<(string id, int n)> c) => string.Join(", ", c.Select(x => $"{x.n} {ItemDb.Name(x.id)}"));

        void Deploy(Spartan s, string item, Vector3 pos, float yaw, int target)
        {
            if (s.Vitals.Dead || s.Inv.Total(item) <= 0 || (pos - s.Pos).sqrMagnitude > 10f * 10f) return;
            switch (item)
            {
                case "beacon":
                    if (Blocked(s, pos)) { Toast(s.Id, "Too close to another base's beacon"); return; }
                    s.Inv.Take(item, 1);
                    var b = Add(EKind.Beacon, item, pos, yaw, Content.Rust.CupboardHealth, s.Id);
                    authorized[b.Id] = new HashSet<ulong> { s.Id };
                    storage[b.Id] = new Inventory();
                    Toast(s.Id, "Forward Base Beacon placed. Covenant patrols will notice it.");
                    raidAt[b.Id] = Time.time + raidInterval;
                    break;
                case "smelter":
                    s.Inv.Take(item, 1);
                    var f = Add(EKind.Smelter, item, pos, yaw, Content.Rust.Furnace?.Health ?? 500f, s.Id);
                    storage[f.Id] = new Inventory();
                    break;
                case "armory1":
                    s.Inv.Take(item, 1);
                    Add(EKind.Armory, item, pos, yaw, 500f, s.Id);
                    break;
                case "door_wood":
                case "door_metal":
                    if (!ents.TryGetValue(target, out var way) || way.Sub != "doorway" || ents.Values.Any(x => x.Kind == EKind.Door && x.Parent == target)) return;
                    if (Blocked(s, way.Pos)) { Toast(s.Id, "Building blocked by another base's beacon"); return; }
                    s.Inv.Take(item, 1);
                    Add(EKind.Door, item, way.Pos, way.Yaw, Rules.DoorHealth(ItemDb.Get(item).RustShortname), s.Id, way.Id);
                    break;
                default:
                    return;
            }
            SendInv(s);
        }

        void Interact(Spartan s, int id, string action, string item, int amount)
        {
            if (action == "reload")
            {
                int need = (int)Content.Stat("stats_assault_rifle", "roundsLoadedMax") - s.Mag;
                int have = s.Inv.Total("ar_ammo");
                int k = Mathf.Min(need, have);
                if (k <= 0 || Time.time < s.ReloadUntil) return;
                s.Inv.Take("ar_ammo", k);
                s.ReloadUntil = Time.time + reloadSeconds;
                StartCoroutine(FinishReload(s, k));
                SendInv(s);
                return;
            }
            if (!ents.TryGetValue(id, out var e) || (e.Pos - s.Pos).sqrMagnitude > 6f * 6f) return;
            switch (action)
            {
                case "door":
                    bool ok = e.Owner == s.Id || ents.Values.Any(b => b.Kind == EKind.Beacon && (b.Pos - e.Pos).sqrMagnitude < privilegeRadius * privilegeRadius && authorized[b.Id].Contains(s.Id));
                    if (!ok) { Toast(s.Id, "Locked: authorise on this base's beacon"); return; }
                    e.Open = !e.Open; SendHealth(e);
                    break;
                case "authorize":
                    if (e.Kind == EKind.Beacon) { authorized[e.Id].Add(s.Id); Toast(s.Id, "Authorised on this beacon"); }
                    break;
                case "put":
                    if (storage.TryGetValue(e.Id, out var into) && s.Inv.Take(item, amount)) { int spill = into.Add(item, amount); if (spill > 0) s.Inv.Add(item, spill); SendStorage(s, e); SendInv(s); }
                    break;
                case "take":
                    if (storage.TryGetValue(e.Id, out var from) && from.Take(item, amount)) { int spill = s.Inv.Add(item, amount); if (spill > 0) from.Add(item, spill); SendStorage(s, e); SendInv(s); }
                    if (e.Kind == EKind.Drop && from != null && Enumerable.Range(0, Inventory.SlotCount).All(i => from.Item[i] == null)) Remove(e.Id);
                    break;
                case "open":
                    SendStorage(s, e);
                    break;
                case "light":
                    if (e.Kind == EKind.Smelter) { e.Open = !e.Open; SendHealth(e); }
                    break;
            }
        }

        System.Collections.IEnumerator FinishReload(Spartan s, int rounds)
        {
            yield return new WaitForSeconds(reloadSeconds);
            s.Mag += rounds;
            SendInv(s);
        }

        void SendStorage(Spartan s, Ent e)
        {
            if (!storage.TryGetValue(e.Id, out var inv)) return;
            var w = Net.Begin(Msg.Oven, out var ms);
            w.Write(e.Id); inv.Write(w);
            Net.ToClient(s.Id, ms);
        }

        // ---- explosives (Rust's satchel: fuse 6-12 s, 20% dud, read from Rust) ----------------------

        void Throw(Spartan s, string item, Vector3 landed, int stuckTo)
        {
            if (item != "satchel" || s.Vitals.Dead || !s.Inv.Take(item, 1)) return;
            var row = Explosives.All.First(x => x.Item == "items." + item);
            var ex = Rules.Explosive(row.RustPrefab);
            var e = Add(EKind.Satchel, item, landed, 0f, 1f, s.Id, stuckTo);
            bool dud = UnityEngine.Random.value < ex.DudChance;
            fuse[e.Id] = Time.time + UnityEngine.Random.Range(ex.TimerMin, ex.TimerMax);
            if (dud) fuse[e.Id] = -fuse[e.Id]; // negative = will fizzle
            SendInv(s);
        }

        void TickExplosives()
        {
            foreach (var (id, at) in fuse.ToList())
            {
                if (Time.time < Mathf.Abs(at) || !ents.TryGetValue(id, out var e)) continue;
                if (at < 0)
                {
                    // Rust duds can be picked back up; here they fizzle and drop the satchel back
                    fuse.Remove(id);
                    Fx(FxDud, e.Pos, e.Pos);
                    Remove(id);
                    var drop = Add(EKind.Drop, "dud", e.Pos, 0f, 1f);
                    storage[drop.Id] = new Inventory();
                    storage[drop.Id].Add(e.Sub, 1);
                    continue;
                }
                fuse.Remove(id);
                var row = Explosives.All.First(x => x.Item == "items." + e.Sub);
                Explode(e.Pos, Rules.Explosive(row.RustPrefab), e.Owner);
                Remove(id);
            }
        }

        void Explode(Vector3 at, RExplosive ex, ulong owner)
        {
            Fx(FxExplosion, at, at);
            var done = new HashSet<(bool, ulong, int)>();
            foreach (var c in Physics.OverlapSphere(at, ex.Radius, ~0, QueryTriggerInteraction.Ignore))
            {
                var er = c.GetComponentInParent<EntityRef>();
                if (er == null || !done.Add((er.IsPlayer, er.PlayerId, er.EntId))) continue;
                float d = Vector3.Distance(at, c.ClosestPoint(at));
                // Rust: full damage inside minExplosionRadius, then falls off to the edge
                float k = d <= ex.MinRadius ? 1f : Mathf.Clamp01(1f - (d - ex.MinRadius) / Mathf.Max(0.01f, ex.Radius - ex.MinRadius));
                foreach (var (type, amount) in ex.DamageByType)
                    ApplyDamage(er, amount * k, int.Parse(type), owner, at);
            }
        }

        // ---- tick ----------------------------------------------------------------------------------

        void Update()
        {
            float now = Time.time, dt = Time.deltaTime;
            foreach (var s in spartans.Values) s.Vitals.Tick(now, dt);
            TickExplosives();
            TickSmelters(dt);
            TickRaids(dt);
            TickUpkeep();
            if (now >= nextSnapshot)
            {
                nextSnapshot = now + 0.05f;
                var w = Net.Begin(Msg.Players, out var ms);
                w.Write(spartans.Count);
                foreach (var s in spartans.Values)
                {
                    w.Write(s.Id); w.Write(s.Name); w.Write(s.Pos); w.Write(s.Yaw); w.Write(s.Pitch);
                    w.Write(s.Vitals.Body); w.Write(s.Vitals.MaxBody); w.Write(s.Vitals.Shield); w.Write(s.Vitals.MaxShield);
                    w.Write(s.Vitals.Dead); w.Write(Mathf.Max(0f, s.RespawnAt - now));
                }
                Net.ToAll(ms, reliable: false);
                SendEnemies();
            }
        }

        // ---- upkeep (Rust: the cupboard pays a share of the base's cost, or the base decays) --------

        float nextUpkeep = -1f;

        void TickUpkeep()
        {
            float period = float.Parse(Rules.Design("upkeep_period_min"), System.Globalization.CultureInfo.InvariantCulture) * 60f;
            if (nextUpkeep < 0f) nextUpkeep = Time.time + period;
            if (Time.time < nextUpkeep) return;
            nextUpkeep = Time.time + period;
            float share = float.Parse(Rules.Design("upkeep_share"), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var b in ents.Values.Where(x => x.Kind == EKind.Beacon).ToList())
            {
                var zone = ents.Values.Where(x => x.Kind == EKind.Piece && (x.Pos - b.Pos).sqrMagnitude < privilegeRadius * privilegeRadius).ToList();
                var due = new Dictionary<string, int>();
                foreach (var piece in zone)
                {
                    var row = Pieces.All.First(p => p.Id == piece.Sub);
                    float mult = Content.Rust.Upkeep.TryGetValue(row.RustConstruction.ToLowerInvariant(), out var m) ? m : 1f;
                    foreach (var (id, n) in Rules.PieceCost(row, piece.Grade))
                        due[id] = due.GetValueOrDefault(id) + Mathf.CeilToInt(n * share * mult);
                }
                var inv = storage[b.Id];
                bool paid = due.All(kv => inv.Total(kv.Key) >= kv.Value);
                if (paid) foreach (var kv in due) inv.Take(kv.Key, kv.Value);
                else
                    foreach (var piece in zone)
                    {
                        piece.Hp -= piece.MaxHp * 0.1f;
                        if (piece.Hp <= 0f) Remove(piece.Id); else SendHealth(piece);
                    }
                if (!paid && authorized.TryGetValue(b.Id, out var owners))
                    foreach (var o in owners) Toast(o, "Your beacon is out of upkeep: the base is decaying");
            }
        }

        // ---- smelting (Rust's furnace: fuel units/second, ore cook times, wood -> charcoal) ---------

        readonly Dictionary<int, float> fuelLeft = new(), cookTimer = new();

        void TickSmelters(float dt)
        {
            foreach (var e in ents.Values.Where(x => x.Kind == EKind.Smelter && x.Open).ToList())
            {
                var inv = storage[e.Id];
                fuelLeft.TryGetValue(e.Id, out var fuel);
                if (fuel <= 0f)
                {
                    // burn the next fuel item: Rust's fuelAmount, and its byproduct chance (wood -> charcoal 25%)
                    var fuelItem = new[] { "lgf", "wood" }.FirstOrDefault(i => inv.Total(i) > 0);
                    if (fuelItem == null) { e.Open = false; SendHealth(e); continue; }
                    inv.Take(fuelItem, 1);
                    var burn = Content.Rust.Burning[ItemDb.Get(fuelItem).RustShortname];
                    fuel = burn.Fuel;
                    if (!string.IsNullOrEmpty(burn.Byproduct) && UnityEngine.Random.value < burn.ByproductChance)
                        inv.Add(ItemDb.IdOfRust(burn.Byproduct) ?? burn.Byproduct, burn.ByproductAmount);
                }
                fuelLeft[e.Id] = fuel - dt;
                cookTimer.TryGetValue(e.Id, out var t);
                t += dt;
                // Rust furnaces cook every ore slot at once: one item per slot per cookTime
                var oreSlots = Enumerable.Range(0, Inventory.SlotCount).Where(i => inv.Item[i] is "metal_ore" or "sulfur_ore").Take(3).ToList();
                if (oreSlots.Count > 0)
                {
                    var cook = Content.Rust.Cooking[ItemDb.Get(inv.Item[oreSlots[0]]).RustShortname];
                    if (t >= cook.Time)
                    {
                        t = 0f;
                        foreach (var slot in oreSlots)
                        {
                            var ore = inv.Item[slot];
                            if (ore == null) continue;
                            var c = Content.Rust.Cooking[ItemDb.Get(ore).RustShortname];
                            inv.Take(ore, 1);
                            inv.Add(ItemDb.IdOfRust(c.Become) ?? c.Become, Mathf.Max(1, Mathf.RoundToInt(c.Amount)));
                        }
                    }
                }
                else t = 0f;
                cookTimer[e.Id] = t;
            }
        }
    }
}
