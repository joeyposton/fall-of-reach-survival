using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Runs on every machine (the host too): turns the host's messages into things you see — other Spartans,
    /// nodes, Forge-block bases, Covenant, tracers and explosions — and keeps your own inventory copy.
    /// </summary>
    public sealed class Client : MonoBehaviour
    {
        public static Client Instance { get; private set; }

        public readonly Dictionary<int, Ent> Ents = new();
        public readonly Dictionary<int, GameObject> Views = new();
        public readonly Inventory Inv = new();
        public int Mag;
        public bool Reloading;
        public int OpenStorageId;
        public readonly Inventory Storage = new();
        public bool Dead;
        public float RespawnIn;

        sealed class Remote { public GameObject Go; public Vector3 Pos; public float Yaw; public string Name; public bool Dead; }
        readonly Dictionary<ulong, Remote> remotes = new();
        readonly Dictionary<int, (Vector3 pos, float yaw)> enemyTargets = new();
        public readonly Dictionary<int, (float body, float maxBody, float shield, float maxShield)> EnemyVitals = new();
        public readonly Dictionary<ulong, string> Names = new();

        void Awake() => Instance = this;

        public void Hello(string name)
        {
            var w = Net.Begin(Msg.Hello, out var ms); w.Write(name); Net.ToHost(ms);
        }

        public void Receive(byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data));
            switch ((Msg)r.ReadByte())
            {
                case Msg.Welcome:
                {
                    r.ReadUInt64();
                    var at = r.ReadV3();
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) SpawnView(Ent.Read(r));
                    var me = Game.Instance.LocalPlayer;
                    if (me == null) Game.Instance.SpawnLocal(at); else me.Teleport(at);
                    break;
                }
                case Msg.Spawn: SpawnView(Ent.Read(r)); break;
                case Msg.Remove:
                {
                    int id = r.ReadInt32();
                    Ents.Remove(id);
                    if (Views.Remove(id, out var go)) Destroy(go);
                    enemyTargets.Remove(id); EnemyVitals.Remove(id);
                    if (OpenStorageId == id) OpenStorageId = 0;
                    break;
                }
                case Msg.Health:
                {
                    int id = r.ReadInt32();
                    float hp = r.ReadSingle(), max = r.ReadSingle(); int grade = r.ReadByte(); bool open = r.ReadBoolean();
                    if (!Ents.TryGetValue(id, out var e)) break;
                    bool regrade = e.Grade != grade, reopen = e.Open != open;
                    e.Hp = hp; e.MaxHp = max; e.Grade = grade; e.Open = open;
                    if (regrade) Views[id].GetComponent<PieceView>()?.SetGrade(grade);
                    if (reopen && e.Kind == EKind.Door) Views[id].GetComponent<DoorView>()?.SetOpen(open);
                    break;
                }
                case Msg.Inv:
                    Inv.Read(r); Mag = r.ReadInt32(); Reloading = r.ReadBoolean();
                    break;
                case Msg.Oven:
                    OpenStorageId = r.ReadInt32(); Storage.Read(r);
                    break;
                case Msg.Toast: Hud.Instance?.Toast(r.ReadString()); break;
                case Msg.Fx: Effects.Play(r.ReadByte(), r.ReadV3(), r.ReadV3()); break;
                case Msg.Players: ReadPlayers(r); break;
                case Msg.Enemies: ReadEnemies(r); break;
            }
        }

        void ReadPlayers(BinaryReader r)
        {
            int n = r.ReadInt32();
            var seen = new HashSet<ulong>();
            for (int i = 0; i < n; i++)
            {
                ulong id = r.ReadUInt64(); string name = r.ReadString(); var pos = r.ReadV3(); float yaw = r.ReadSingle(); r.ReadSingle();
                float body = r.ReadSingle(), maxBody = r.ReadSingle(), shield = r.ReadSingle(), maxShield = r.ReadSingle();
                bool dead = r.ReadBoolean(); float respawnIn = r.ReadSingle();
                seen.Add(id);
                Names[id] = name;
                if (id == Net.LocalId)
                {
                    Game.Instance.LocalPlayer?.Vitals.Set(body, maxBody, shield, maxShield);
                    Dead = dead; RespawnIn = respawnIn;
                    continue;
                }
                if (!remotes.TryGetValue(id, out var rem))
                {
                    var go = Content.Spawn("model_spartan", null, Player.Height, name);
                    var col = go.AddComponent<CapsuleCollider>();
                    col.height = Player.Height; col.radius = 0.45f; col.center = Vector3.up * Player.Height / 2f;
                    var er = go.AddComponent<EntityRef>(); er.IsPlayer = true; er.PlayerId = id;
                    Tint(go, Game.TeamColor(id));
                    rem = remotes[id] = new Remote { Go = go, Pos = pos, Yaw = yaw };
                    go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                }
                rem.Pos = pos; rem.Yaw = yaw; rem.Name = name; rem.Dead = dead;
                rem.Go.SetActive(!dead);
            }
            foreach (var gone in remotes.Keys.Where(k => !seen.Contains(k)).ToList()) { Destroy(remotes[gone].Go); remotes.Remove(gone); }
        }

        void ReadEnemies(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32(); var pos = r.ReadV3(); float yaw = r.ReadSingle();
                EnemyVitals[id] = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                enemyTargets[id] = (pos, yaw);
                if (Ents.TryGetValue(id, out var e)) { e.Pos = pos; e.Yaw = yaw; }
            }
        }

        void Update()
        {
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            foreach (var rem in remotes.Values)
            {
                var t = rem.Go.transform;
                t.position = Vector3.Lerp(t.position, rem.Pos, k);
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0, rem.Yaw, 0), k);
            }
            foreach (var (id, tgt) in enemyTargets)
            {
                if (!Views.TryGetValue(id, out var go)) continue;
                go.transform.position = Vector3.Lerp(go.transform.position, tgt.pos, k);
                go.transform.rotation = Quaternion.Slerp(go.transform.rotation, Quaternion.Euler(0, tgt.yaw, 0), k);
            }
        }

        public IEnumerable<(Vector3 pos, string name)> RemoteNames() => remotes.Values.Where(x => !x.Dead).Select(x => (x.Go.transform.position, x.Name));

        // ---- views ---------------------------------------------------------------------------------

        void SpawnView(Ent e)
        {
            if (Views.TryGetValue(e.Id, out var old)) Destroy(old);
            Ents[e.Id] = e;
            GameObject go;
            switch (e.Kind)
            {
                case EKind.Node:
                    go = NodeView(e);
                    break;
                case EKind.Piece:
                    go = new GameObject("piece " + e.Sub);
                    go.AddComponent<PieceView>().Build(e);
                    break;
                case EKind.Door:
                    go = new GameObject("door");
                    go.AddComponent<DoorView>().Build(e);
                    break;
                case EKind.Beacon:
                    go = Content.Spawn("beacon", null, 1.9f);
                    Solid(go, new Vector3(0.9f, 1.9f, 0.9f));
                    break;
                case EKind.Smelter:
                    go = Content.Spawn("smelter", null, 2.0f);
                    Solid(go, new Vector3(2.0f, 1.2f, 2.0f));
                    break;
                case EKind.Armory:
                    go = Content.Spawn("armory", null, 1.9f);
                    Solid(go, new Vector3(1.9f, 1.4f, 1.6f));
                    break;
                case EKind.Satchel:
                    go = Content.Spawn("charge", null, 0.35f);
                    break;
                case EKind.Drop:
                    go = Content.Spawn("armory", null, 0.6f, "drop");
                    Tint(go, e.Sub == "spartan" ? new Color(0.6f, 0.8f, 1f) : new Color(0.75f, 0.5f, 1f));
                    Solid(go, new Vector3(0.7f, 0.5f, 0.7f));
                    break;
                case EKind.Enemy:
                    go = Content.Spawn(e.Sub == "elite" ? "model_elite" : "model_grunt", null, e.Sub == "elite" ? 2.4f : 1.4f);
                    var cap = go.AddComponent<CapsuleCollider>();
                    cap.height = e.Sub == "elite" ? 2.4f : 1.4f; cap.radius = 0.5f; cap.center = Vector3.up * cap.height / 2f;
                    enemyTargets[e.Id] = (e.Pos, e.Yaw);
                    break;
                default:
                    go = new GameObject(e.Kind.ToString());
                    break;
            }
            go.transform.SetPositionAndRotation(e.Pos, e.Rot);
            var er = go.GetComponent<EntityRef>() ?? go.AddComponent<EntityRef>();
            er.EntId = e.Id;
            Views[e.Id] = go;
        }

        static GameObject NodeView(Ent e)
        {
            GameObject go;
            if (e.Sub == "tree")
            {
                go = Content.Spawn("node_tree", null, 9f, "tree");
                var c = go.AddComponent<CapsuleCollider>(); c.radius = 0.5f; c.height = 9f; c.center = Vector3.up * 4.5f;
            }
            else
            {
                go = Content.Spawn("node_rock", null, 2.2f, e.Sub);
                // Halo has no ore rocks, so the three ores are Forge rocks with a tint
                Tint(go, e.Sub == "alloy" ? new Color(0.85f, 0.62f, 0.45f) : e.Sub == "plasma" ? new Color(0.65f, 0.8f, 0.45f) : new Color(0.85f, 0.85f, 0.9f));
                Solid(go, new Vector3(2.0f, 1.4f, 2.0f));
            }
            return go;
        }

        public static void Solid(GameObject go, Vector3 size)
        {
            var b = go.AddComponent<BoxCollider>();
            b.size = size; b.center = Vector3.up * size.y / 2f;
        }

        public static void Tint(GameObject go, Color c)
        {
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = mr.materials; // instance copies, the shared Halo materials stay untouched
                foreach (var m in mats) m.color = c;
                mr.materials = mats;
            }
        }

        public Ent Find(EntityRef er) => er != null && !er.IsPlayer && Ents.TryGetValue(er.EntId, out var e) ? e : null;
    }
}
