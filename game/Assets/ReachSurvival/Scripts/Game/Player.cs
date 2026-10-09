using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// The local Spartan: movement, look and the controls that become requests to the host.
    /// 1 MA37 · 2 Forge Tool · 3 Upgrade Tool · 4 Place item · 5 Satchel · F melee/gather · E use · Tab inventory.
    /// </summary>
    public sealed class Player : MonoBehaviour
    {
        public const float Height = 2.0f;
        public bool IsLocal { get; private set; }
        public Camera Cam { get; private set; }
        public Vitals Vitals { get; private set; }
        public bool Swimming { get; private set; }

        public enum Tool { Rifle = 1, Forge = 2, Upgrade = 3, Place = 4, Satchel = 5 }
        public Tool Equipped { get; private set; } = Tool.Rifle;
        public string ForgePiece { get; private set; } = "foundation";
        public string PlaceItem { get; private set; }

        CharacterController cc;
        Transform body;
        GameObject rifle;
        Ghost ghost;
        float yaw, pitch, vy, nextFire, nextSend;
        public float Yaw => yaw;
        public float Pitch => pitch;

        public static Player Create(Vector3 at, bool isLocal)
        {
            var go = new GameObject(isLocal ? "Spartan (you)" : "Spartan");
            go.transform.position = at + Vector3.up * 0.1f;
            var p = go.AddComponent<Player>();
            p.IsLocal = isLocal;
            p.cc = go.AddComponent<CharacterController>();
            p.cc.height = Height;
            p.cc.radius = 0.45f;
            p.cc.center = Vector3.up * (Height / 2f);
            p.cc.stepOffset = 0.5f;
            p.cc.slopeLimit = 50f;
            var er = go.AddComponent<EntityRef>(); er.IsPlayer = true; er.PlayerId = Net.LocalId;

            p.body = Content.Spawn("model_spartan", go.transform, Height, "body").transform;
            Client.Tint(p.body.gameObject, Game.TeamColor(Net.LocalId));
            p.Vitals = go.AddComponent<Vitals>();
            foreach (var r in p.body.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            var cam = new GameObject("Eyes").AddComponent<Camera>();
            cam.transform.SetParent(go.transform, false);
            cam.transform.localPosition = Vector3.up * 1.75f;
            cam.fieldOfView = 78f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 3000f;
            cam.gameObject.AddComponent<AudioListener>();
            cam.tag = "MainCamera";
            p.Cam = cam;
            p.yaw = Random.Range(0f, 360f);

            // first-person MA37, Reach's own model
            // Reach's model already points down +z (Halo's forward), so it only needs placing low and right
            p.rifle = Content.Spawn("model_assault_rifle", cam.transform, 0.62f, "MA37");
            p.rifle.transform.localPosition = new Vector3(0.20f, -0.26f, 0.42f);
            p.rifle.transform.localRotation = Quaternion.Euler(1.5f, -2f, 0);
            foreach (var r in p.rifle.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.ghost = go.AddComponent<Ghost>();
            return p;
        }

        void Update()
        {
            var client = Client.Instance;
            bool uiOpen = Hud.Instance != null && Hud.Instance.CursorWanted;
            bool driving = AutoTest.Driving;
            Cursor.lockState = uiOpen || driving ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = uiOpen;
            if (!uiOpen && !driving)
            {
                yaw += Input.GetAxisRaw("Mouse X") * 2.2f;
                pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * 2.2f, -85f, 85f);
            }
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            if (client.Dead) { Respawning(); return; }
            Move(uiOpen, driving);
            if (!uiOpen && !driving) Controls();

            rifle.SetActive(Equipped == Tool.Rifle);
            ghost.Show(Equipped == Tool.Forge ? ForgePiece : Equipped == Tool.Place && PlaceItem != null ? PlaceItem : null);

            if (Time.time >= nextSend)
            {
                nextSend = Time.time + 0.05f;
                var w = Net.Begin(Msg.PlayerState, out var ms);
                w.Write(transform.position); w.Write(yaw); w.Write(pitch);
                Net.ToHost(ms);
            }
        }

        void Move(bool uiOpen, bool driving)
        {
            var move = uiOpen ? Vector2.zero : driving ? AutoTest.Move : new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            Swimming = transform.position.y < World.WaterLevel - 1.4f;
            float speed = Swimming ? 2.5f : Input.GetKey(KeyCode.LeftShift) ? 7.5f : 5.0f;
            var wish = transform.TransformDirection(new Vector3(move.x, 0f, move.y).normalized) * speed;
            if (Swimming)
            {
                // float with the head just above Reach's sea; Space swims up, Ctrl dives
                float target = Input.GetKey(KeyCode.LeftControl) ? -2f : Input.GetKey(KeyCode.Space) ? 2.5f : 1.2f;
                vy = Mathf.MoveTowards(vy, target, 12f * Time.deltaTime);
            }
            else if (cc.isGrounded)
            {
                vy = -2f;
                if (!uiOpen && Input.GetKeyDown(KeyCode.Space)) vy = 6.5f;
            }
            if (!Swimming) vy += Physics.gravity.y * Time.deltaTime;
            cc.Move((wish + Vector3.up * vy) * Time.deltaTime);
        }

        void Controls()
        {
            for (int k = 1; k <= 5; k++) if (Input.GetKeyDown(KeyCode.Alpha0 + k)) Equip((Tool)k);
            float wheel = Input.GetAxisRaw("Mouse ScrollWheel");
            if (wheel != 0f || Input.GetMouseButtonDown(1)) Cycle(wheel < 0 ? -1 : 1);

            if (Input.GetKeyDown(KeyCode.F)) Melee();
            if (Input.GetKeyDown(KeyCode.E)) Use();
            if (Input.GetKeyDown(KeyCode.R)) Request(Msg.Interact, w => { w.Write(0); w.Write("reload"); w.Write(""); w.Write(0); });
            if (Equipped == Tool.Rifle && Input.GetMouseButton(0)) Fire();
            if (Input.GetMouseButtonDown(0))
            {
                if (Equipped == Tool.Forge) ghost.Commit();
                else if (Equipped == Tool.Place) ghost.Commit();
                else if (Equipped == Tool.Upgrade) UpgradeLooked();
                else if (Equipped == Tool.Satchel) ThrowSatchel();
            }
        }

        public void Equip(Tool t)
        {
            Equipped = t;
            if (t == Tool.Place) Cycle(0);
        }

        void Cycle(int dir)
        {
            if (Equipped == Tool.Forge)
            {
                var list = Pieces.All.Select(p => p.Id).ToList();
                ForgePiece = list[(list.IndexOf(ForgePiece) + dir + list.Count) % list.Count];
            }
            else if (Equipped == Tool.Place)
            {
                var have = Items.All.Where(i => i.Kind == "deployable" && Client.Instance.Inv.Total(i.Id) > 0).Select(i => i.Id).ToList();
                if (have.Count == 0) { PlaceItem = null; return; }
                int at = PlaceItem == null ? -1 : have.IndexOf(PlaceItem);
                PlaceItem = have[((at < 0 ? 0 : at + dir) + have.Count) % have.Count];
            }
        }

        /// <summary>Forge/Place: what to build/put, from the HUD or tests.</summary>
        public void SetForgePiece(string id) { Equipped = Tool.Forge; ForgePiece = id; }
        public void SetPlaceItem(string id) { Equipped = Tool.Place; PlaceItem = id; }

        public Ray Aim => new Ray(Cam.transform.position, Cam.transform.forward);

        public void Fire()
        {
            var st = Content.HaloStats["stats_assault_rifle"];
            if (Time.time < nextFire || Client.Instance.Mag <= 0 || Client.Instance.Reloading) return;
            nextFire = Time.time + 1f / st["roundsPerSecondMax"];
            Client.Instance.Mag--; // predicted; the host's count comes back with the next inventory update
            var a = Aim;
            Request(Msg.Fire, w => { w.Write(a.origin); w.Write(a.direction); });
        }

        public void Melee()
        {
            var a = Aim;
            Request(Msg.Melee, w => { w.Write(a.origin); w.Write(a.direction); });
        }

        public EntityRef Looked(float range = 4f)
        {
            if (Physics.Raycast(Aim, out var hit, range, ~0, QueryTriggerInteraction.Ignore))
            {
                var er = hit.collider.GetComponentInParent<EntityRef>();
                if (er != null && !(er.IsPlayer && er.PlayerId == Net.LocalId)) return er;
            }
            return null;
        }

        public void Use()
        {
            var e = Client.Instance.Find(Looked());
            if (e == null) return;
            switch (e.Kind)
            {
                case EKind.Door: Interact(e.Id, "door"); break;
                case EKind.Beacon: Interact(e.Id, "authorize"); Interact(e.Id, "open"); Hud.Instance.OpenStorage(e.Id); break;
                case EKind.Smelter: case EKind.Drop: Interact(e.Id, "open"); Hud.Instance.OpenStorage(e.Id); break;
            }
        }

        public void Interact(int id, string action, string item = "", int amount = 0) =>
            Request(Msg.Interact, w => { w.Write(id); w.Write(action); w.Write(item ?? ""); w.Write(amount); });

        public void UpgradeLooked()
        {
            var e = Client.Instance.Find(Looked(6f));
            if (e != null && e.Kind == EKind.Piece) Request(Msg.Upgrade, w => w.Write(e.Id));
        }

        public void ThrowSatchel()
        {
            if (Client.Instance.Inv.Total("satchel") <= 0) { Hud.Instance.Toast("No Satchel Charges"); return; }
            // short lob: sticks to the first thing within 8 m along the aim, else drops at your feet ahead
            Vector3 at; int stuck = 0;
            if (Physics.Raycast(Aim, out var hit, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                at = hit.point + hit.normal * 0.1f;
                var er = hit.collider.GetComponentInParent<EntityRef>();
                if (er != null && !er.IsPlayer) stuck = er.EntId;
            }
            else at = World.Ground(transform.position.x + transform.forward.x * 3f, transform.position.z + transform.forward.z * 3f, 0f) ?? transform.position;
            Request(Msg.ThrowExplosive, w => { w.Write("satchel"); w.Write(at); w.Write(stuck); });
        }

        void Respawning()
        {
            rifle.SetActive(false);
            ghost.Show(null);
            if (Client.Instance.RespawnIn > 0f) return;
            if (Input.GetKeyDown(KeyCode.R) || AutoTest.Driving) Request(Msg.Respawn, w => w.Write(false));
            if (Input.GetKeyDown(KeyCode.B)) Request(Msg.Respawn, w => w.Write(true));
        }

        public static void Request(Msg m, System.Action<System.IO.BinaryWriter> body)
        {
            var w = Net.Begin(m, out var ms);
            body(w);
            Net.ToHost(ms);
        }

        public void Look(float newYaw, float newPitch) { yaw = newYaw; pitch = newPitch; }
        public void Teleport(Vector3 at) { cc.enabled = false; transform.position = at + Vector3.up * 0.1f; cc.enabled = true; vy = 0f; }
    }

    /// <summary>What the HUD shows for the local Spartan; the numbers come from the host.</summary>
    public sealed class Vitals : MonoBehaviour
    {
        public float MaxBody { get; private set; } = 1f;
        public float MaxShield { get; private set; } = 1f;
        public float Body { get; private set; } = 1f;
        public float Shield { get; private set; } = 1f;
        public void Set(float body, float maxBody, float shield, float maxShield) { Body = body; MaxBody = maxBody; Shield = shield; MaxShield = maxShield; }
    }
}
