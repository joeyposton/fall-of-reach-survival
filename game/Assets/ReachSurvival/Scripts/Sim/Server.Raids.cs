using System.Collections.Generic;
using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Covenant raids: a squad (design.raid_squad) lands near a Forward Base Beacon every design.raid_interval_s.
    /// Grunts and Elites use Reach's own vitals and weapons (plasma pistol / plasma rifle stats from the player's Reach).
    /// </summary>
    public sealed partial class Server
    {
        sealed class Enemy
        {
            public Ent E;
            public VitalsState Vitals;
            public string Weapon;
            public float NextShot, Speed;
            public int Target;          // beacon they were sent at
        }

        readonly Dictionary<int, Enemy> enemies = new();
        readonly Dictionary<int, float> raidAt = new();
        float raidInterval;

        void BeginRaids()
        {
            raidInterval = float.Parse(Rules.Design("raid_interval_s"), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>For tests and the host's debug key: send a squad at a beacon now.</summary>
        public void RaidNow()
        {
            foreach (var b in ents.Values.Where(x => x.Kind == EKind.Beacon).ToList()) raidAt[b.Id] = Time.time;
        }

        void TickRaids(float dt)
        {
            foreach (var (beaconId, at) in raidAt.ToList())
            {
                if (!ents.TryGetValue(beaconId, out var beacon)) { raidAt.Remove(beaconId); continue; }
                if (Time.time < at) continue;
                raidAt[beaconId] = Time.time + raidInterval;
                SpawnSquad(beacon);
            }

            float now = Time.time;
            foreach (var en in enemies.Values.ToList())
            {
                en.Vitals.Tick(now, dt);
                var e = en.E;
                // nearest living Spartan within 45 m, else head for the beacon
                Spartan prey = null; float best = 45f * 45f;
                foreach (var s in spartans.Values)
                {
                    if (s.Vitals.Dead) continue;
                    float d = (s.Pos - e.Pos).sqrMagnitude;
                    if (d < best) { best = d; prey = s; }
                }
                Vector3 goal = prey != null ? prey.Pos : ents.TryGetValue(en.Target, out var b) ? b.Pos : e.Pos;
                var to = goal - e.Pos; to.y = 0f;
                float dist = to.magnitude;
                if (dist > 0.1f) e.Yaw = Mathf.MoveTowardsAngle(e.Yaw, Quaternion.LookRotation(to).eulerAngles.y, 240f * dt);
                float keep = prey != null ? 12f : 6f;
                if (dist > keep)
                {
                    var step = Quaternion.Euler(0, e.Yaw, 0) * Vector3.forward * en.Speed * dt;
                    var next = e.Pos + step;
                    // base pieces stop them (they shoot what's in the way instead); terrain they walk over
                    bool walled = Physics.Raycast(e.Pos + Vector3.up * 1f, step.normalized, out var block, 1.2f, ~0, QueryTriggerInteraction.Ignore)
                                  && block.collider.GetComponentInParent<EntityRef>() is { IsPlayer: false } wallRef
                                  && ents.TryGetValue(wallRef.EntId, out var wallEnt) && wallEnt.Kind is EKind.Piece or EKind.Door;
                    if (!walled)
                    {
                        var g = World.Ground(next.x, next.z, 0.35f);
                        if (g.HasValue && Mathf.Abs(g.Value.y - e.Pos.y) < 1.5f) e.Pos = g.Value;
                        else e.Yaw += 35f; // blocked by a cliff or the sea: turn and try around it
                    }
                }
                if (now >= en.NextShot)
                {
                    var st = Content.HaloStats[en.Weapon];
                    float rps = AiRate(e.Sub); // design.ai_shots_per_s: the AI fires slower than a player can
                    en.NextShot = now + 1f / rps + Random.Range(0f, 0.4f);
                    var eye = e.Pos + Vector3.up * (e.Sub == "elite" ? 2.2f : 1.2f);
                    Vector3 aim;
                    if (prey != null && dist < 40f) aim = prey.Pos + Vector3.up * 1.2f;
                    else if (ents.TryGetValue(en.Target, out var tb) && (tb.Pos - e.Pos).sqrMagnitude < 30f * 30f) aim = tb.Pos + Vector3.up * 1f;
                    else continue;
                    var dir = (aim - eye).normalized;
                    float spread = float.Parse(Rules.Design("ai_spread_deg"), System.Globalization.CultureInfo.InvariantCulture);
                    dir = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * dir; // Covenant aren't marksmen
                    float dmg = Random.Range(st["damageLower"], st["damageUpperMax"]);
                    var end = HitScanFromEnemy(eye, dir, dmg, e.Id);
                    Fx(FxPlasma, eye, end);
                }
            }
        }

        static float AiRate(string kind)
        {
            foreach (var part in Rules.Design("ai_shots_per_s").Split(';'))
            {
                var b = part.Trim().Split(' ');
                if (b[0] == kind) return float.Parse(b[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            return 0.6f;
        }

        Vector3 HitScanFromEnemy(Vector3 origin, Vector3 dir, float dmg, int self)
        {
            foreach (var h in Physics.RaycastAll(origin, dir, 80f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                var er = h.collider.GetComponentInParent<EntityRef>();
                if (er != null && !er.IsPlayer && er.EntId == self) continue;
                if (er != null && !er.IsPlayer && ents.TryGetValue(er.EntId, out var other) && other.Kind == EKind.Enemy) return h.point;
                if (er != null) ApplyDamage(er, dmg, Rules.Generic, ulong.MaxValue, h.point);
                return h.point;
            }
            return origin + dir * 80f;
        }

        void SpawnSquad(Ent beacon)
        {
            // "1 elite + 3 grunts"
            var parts = Rules.Design("raid_squad").Split('+').Select(p => p.Trim().Split(' ')).ToList();
            // land on dry ground 50-70 m out, on whichever side has some
            float ang = Random.Range(0f, 360f);
            var center = beacon.Pos;
            for (int t = 0; t < 24; t++)
            {
                var c = beacon.Pos + Quaternion.Euler(0, ang + t * 15f, 0) * Vector3.forward * Random.Range(50f, 70f);
                var g = World.Ground(c.x, c.z, 0.6f);
                if (g.HasValue && Mathf.Abs(g.Value.y - beacon.Pos.y) < 25f) { center = g.Value; ang += t * 15f; break; }
            }
            int n = 0;
            foreach (var p in parts)
            {
                int count = int.Parse(p[0]);
                string kind = p[1].TrimEnd('s'); // "grunts" -> "grunt"
                var row = Enemies.All.First(x => x.Id == kind);
                for (int i = 0; i < count; i++)
                {
                    var off = Quaternion.Euler(0, n++ * 40f, 0) * Vector3.forward * 3f;
                    var g = World.Ground(center.x + off.x, center.z + off.z, 0.4f) ?? center;
                    var vitals = kind == "elite"
                        ? VitalsState.Of("stats_elite_vitality", "stats_elite_shields")
                        : new VitalsState(Content.Stat("stats_grunt_vitality", "body"), float.Parse(Rules.Design("grunt_shield")), 0f, 1f);
                    var e = Add(EKind.Enemy, kind, g, ang + 180f, vitals.MaxBody + vitals.MaxShield);
                    enemies[e.Id] = new Enemy
                    {
                        E = e, Vitals = vitals, Target = beacon.Id,
                        Weapon = row.Weapon.Replace("halo_sources.", ""),
                        Speed = kind == "elite" ? 4.2f : 3.4f,
                        NextShot = Time.time + 2f + Random.Range(0f, 2f),
                    };
                }
            }
            foreach (var s in spartans.Values) Toast(s.Id, "Covenant dropship inbound!");
        }

        void HurtEnemy(Ent e, float dmg, ulong attacker)
        {
            if (!enemies.TryGetValue(e.Id, out var en)) return;
            en.Vitals.Damage(dmg, Time.time);
            e.Hp = en.Vitals.Body + en.Vitals.Shield;
            if (!en.Vitals.Dead) return;
            // drops (design.drop_amounts): Grunts carry Methane Gel, Elites Fabric Weave
            var row = Enemies.All.First(x => x.Id == e.Sub);
            var item = row.Drops.Replace("items.", "");
            int amount = e.Sub == "elite" ? Random.Range(5, 11) : Random.Range(3, 7);
            var bag = Add(EKind.Drop, e.Sub, e.Pos + Vector3.up * 0.3f, 0f, 1f);
            storage[bag.Id] = new Inventory();
            storage[bag.Id].Add(item, amount);
            if (spartans.TryGetValue(attacker, out var k)) Toast(k.Id, $"{row.HaloName} down");
            Remove(e.Id);
        }

        void SendEnemies()
        {
            var w = Net.Begin(Msg.Enemies, out var ms);
            w.Write(enemies.Count);
            foreach (var en in enemies.Values)
            {
                w.Write(en.E.Id); w.Write(en.E.Pos); w.Write(en.E.Yaw);
                w.Write(en.Vitals.Body); w.Write(en.Vitals.MaxBody); w.Write(en.Vitals.Shield); w.Write(en.Vitals.MaxShield);
            }
            Net.ToAll(ms, reliable: false);
        }
    }
}
