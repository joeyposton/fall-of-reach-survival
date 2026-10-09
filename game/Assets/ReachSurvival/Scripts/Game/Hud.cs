using System.Linq;
using ReachSurvival.Sheets;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>Halo-style HUD (shields, health, ammo, hotbar) and the Tab panel (inventory + Rust's recipes).</summary>
    public sealed class Hud : MonoBehaviour
    {
        public static Hud Instance { get; private set; }
        public bool CursorWanted => panel || storageId != 0;
        bool panel;
        int storageId;
        string toast; float toastUntil;
        Vector2 craftScroll;
        GUIStyle small, big, label, title;

        void Awake() => Instance = this;

        public void Toast(string s, float seconds = 3f) { toast = s; toastUntil = Time.time + seconds; }
        public void OpenStorage(int id) => storageId = id;
        public void TogglePanel(bool? on = null) => panel = on ?? !panel;

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab)) { panel = !panel; storageId = 0; }
            if (Input.GetKeyDown(KeyCode.Escape)) { panel = false; storageId = 0; }
            if (storageId != 0 && Client.Instance != null && !Client.Instance.Ents.ContainsKey(storageId)) storageId = 0;
        }

        void Styles()
        {
            if (small != null) return;
            small = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            label = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            var p = Game.Instance ? Game.Instance.LocalPlayer : null;
            var c = Client.Instance;
            if (p == null || c == null) return;
            Styles();
            float w = Screen.width, h = Screen.height;

            // shields (Reach blue) and health
            var v = p.Vitals;
            var bar = new Rect(w / 2 - 220, 24, 440, 16);
            Fill(bar, new Color(0, 0, 0, 0.45f));
            Fill(new Rect(bar.x, bar.y, bar.width * (v.Shield / Mathf.Max(1f, v.MaxShield)), bar.height), new Color(0.35f, 0.75f, 1f, 0.9f));
            var hb = new Rect(w / 2 - 220, 44, 440, 6);
            Fill(hb, new Color(0, 0, 0, 0.45f));
            Fill(new Rect(hb.x, hb.y, hb.width * (v.Body / Mathf.Max(1f, v.MaxBody)), hb.height), new Color(0.95f, 0.35f, 0.3f, 0.9f));

            // ammo, Halo-style, top right
            GUI.Label(new Rect(w - 260, 18, 230, 44), c.Reloading ? "RELOADING" : $"{c.Mag}", big);
            GUI.Label(new Rect(w - 260, 60, 230, 22), $"{ItemDb.Name("ar_ammo")}: {c.Inv.Total("ar_ammo")}", new GUIStyle(small) { alignment = TextAnchor.MiddleRight });

            if (!string.IsNullOrEmpty(OnlineNet.Status))
                GUI.Label(new Rect(16, 12, 520, 22), OnlineNet.Status + (OnlineNet.Instance != null && Net.IsHost && OnlineNet.Instance.PlayerCount > 0 ? $"  ·  {OnlineNet.Instance.PlayerCount}/{OnlineNet.MaxPlayers} Spartans" : ""), small);
            Hotbar(p, c, w, h);
            Prompt(p, c, w, h);
            Names(p, c);

            if (!CursorWanted && !c.Dead)
            {
                Fill(new Rect(w / 2 - 1, h / 2 - 8, 2, 16), new Color(1, 1, 1, 0.8f));
                Fill(new Rect(w / 2 - 8, h / 2 - 1, 16, 2), new Color(1, 1, 1, 0.8f));
            }

            if (c.Dead)
            {
                Fill(new Rect(0, 0, w, h), new Color(0.3f, 0, 0, 0.35f));
                var st = new GUIStyle(title) { alignment = TextAnchor.MiddleCenter, fontSize = 28 };
                GUI.Label(new Rect(0, h / 2 - 40, w, 40), "SPARTAN DOWN", st);
                GUI.Label(new Rect(0, h / 2 + 4, w, 30), c.RespawnIn > 0 ? $"Respawn in {c.RespawnIn:0}" : "R: respawn on the island    B: respawn at your beacon", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
            }

            if (toast != null && Time.time < toastUntil)
                GUI.Label(new Rect(0, h * 0.70f, w, 30), toast, new GUIStyle(label) { fontSize = 20, alignment = TextAnchor.MiddleCenter });

            if (panel) Panel(c, w, h);
            if (storageId != 0) StoragePanel(p, c, w, h);
        }

        void Hotbar(Player p, Client c, float w, float h)
        {
            string[] names = { "MA37", "Forge Tool", "Upgrade Tool", "Place", "Satchel" };
            float x0 = w / 2 - 5 * 120 / 2f;
            for (int i = 0; i < 5; i++)
            {
                var r = new Rect(x0 + i * 120, h - 64, 114, 48);
                bool on = (int)p.Equipped == i + 1;
                Fill(r, on ? new Color(0.35f, 0.75f, 1f, 0.45f) : new Color(0, 0, 0, 0.45f));
                string sub = i switch
                {
                    1 => Pieces.All.First(x => x.Id == p.ForgePiece).HaloName,
                    3 => p.PlaceItem != null ? ItemDb.Name(p.PlaceItem) : "nothing to place",
                    4 => $"x{c.Inv.Total("satchel")}",
                    _ => "",
                };
                GUI.Label(new Rect(r.x + 6, r.y + 2, r.width, 22), $"{i + 1} {names[i]}", label);
                GUI.Label(new Rect(r.x + 6, r.y + 24, r.width, 20), sub, small);
            }
            if (p.Equipped == Player.Tool.Forge)
            {
                var piece = Pieces.All.First(x => x.Id == p.ForgePiece);
                GUI.Label(new Rect(x0, h - 92, 600, 24), $"{piece.HaloName}: {Cost(Rules.PieceCost(piece, 0))}   (scroll / right-click to change)", small);
            }
        }

        void Prompt(Player p, Client c, float w, float h)
        {
            var e = c.Find(p.Looked(p.Equipped == Player.Tool.Upgrade ? 6f : 4f));
            if (e == null) return;
            string text = e.Kind switch
            {
                EKind.Node => $"{NodeName(e.Sub)}  [F] gather",
                EKind.Piece => $"{Pieces.All.First(x => x.Id == e.Sub).HaloName} ({Rules.Grade(e.Grade).HaloName}) {e.Hp:0}/{e.MaxHp:0}" +
                               (p.Equipped == Player.Tool.Upgrade && e.Grade < 3 ? $"   [LMB] upgrade to {Rules.Grade(e.Grade + 1).HaloName}: {Cost(Rules.PieceCost(Pieces.All.First(x => x.Id == e.Sub), e.Grade + 1))}" : ""),
                EKind.Door => $"{ItemDb.Name(e.Sub)} {e.Hp:0}/{e.MaxHp:0}  [E] {(e.Open ? "close" : "open")}",
                EKind.Beacon => $"Forward Base Beacon {e.Hp:0}/{e.MaxHp:0}  [E] authorise / upkeep",
                EKind.Smelter => $"Forge Smelter {(e.Open ? "(lit)" : "")}  [E] open",
                EKind.Armory => "Armory (Tier 1): craft Tier 1 items nearby",
                EKind.Drop => "Supplies  [E] open",
                EKind.Satchel => "Satchel Charge, armed!",
                EKind.Enemy when c.EnemyVitals.TryGetValue(e.Id, out var ev) => $"{(e.Sub == "elite" ? "Elite" : "Grunt")}  {ev.body + ev.shield:0}",
                _ => null,
            };
            if (text != null) GUI.Label(new Rect(0, h / 2 + 24, w, 24), text, new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
        }

        void Names(Player p, Client c)
        {
            foreach (var (pos, name) in c.RemoteNames())
            {
                var sp = p.Cam.WorldToScreenPoint(pos + Vector3.up * 2.4f);
                if (sp.z <= 0 || sp.z > 60f) continue;
                GUI.Label(new Rect(sp.x - 80, Screen.height - sp.y - 10, 160, 20), name, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            }
        }

        static string NodeName(string sub) => sub switch { "tree" => "Tree", "titanium" => "Titanium deposit", "alloy" => "Alloy deposit", _ => "Plasma deposit" };
        static string Cost(System.Collections.Generic.List<(string id, int n)> cost) => string.Join(", ", cost.Select(x => $"{x.n} {ItemDb.Name(x.id)}"));

        void Panel(Client c, float w, float h)
        {
            var area = new Rect(w / 2 - 520, 100, 1040, h - 220);
            Fill(area, new Color(0.04f, 0.06f, 0.08f, 0.92f));
            GUI.Label(new Rect(area.x + 20, area.y + 12, 400, 30), "INVENTORY", title);
            for (int i = 0; i < Inventory.SlotCount; i++)
            {
                var r = new Rect(area.x + 20 + (i % 6) * 82, area.y + 50 + (i / 6) * 70, 76, 64);
                Fill(r, new Color(1, 1, 1, 0.07f));
                if (c.Inv.Item[i] == null) continue;
                GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, 40), ItemDb.Name(c.Inv.Item[i]), new GUIStyle(small) { wordWrap = true });
                GUI.Label(new Rect(r.x + 4, r.y + 42, r.width - 8, 20), $"x{c.Inv.Count[i]}", small);
            }

            GUI.Label(new Rect(area.x + 540, area.y + 12, 480, 30), "CRAFT (Rust's recipes)", title);
            var list = ItemDb.Craftable().ToList();
            var view = new Rect(area.x + 540, area.y + 50, 480, area.height - 70);
            craftScroll = GUI.BeginScrollView(view, craftScroll, new Rect(0, 0, 460, list.Count * 62));
            for (int i = 0; i < list.Count; i++)
            {
                var row = list[i];
                var bp = ItemDb.Recipe(row.Id);
                var ing = Rules.Ingredients(bp);
                bool can = c.Inv.Has(ing);
                var r = new Rect(0, i * 62, 460, 58);
                Fill(r, new Color(1, 1, 1, can ? 0.10f : 0.04f));
                GUI.Label(new Rect(8, r.y + 4, 300, 22), $"{row.HaloName}" + (bp.Amount > 1 ? $" x{bp.Amount}" : "") + (bp.Workbench > 0 ? $"  Â· Armory T{bp.Workbench}" : ""), label);
                GUI.Label(new Rect(8, r.y + 28, 340, 24), Cost(ing) + $"  Â· {bp.Time:0}s", small);
                GUI.enabled = can;
                if (GUI.Button(new Rect(370, r.y + 12, 80, 32), "Craft")) Player.Request(Msg.Craft, wr => wr.Write(row.Id));
                GUI.enabled = true;
            }
            GUI.EndScrollView();
        }

        void StoragePanel(Player p, Client c, float w, float h)
        {
            if (!c.Ents.TryGetValue(storageId, out var e)) return;
            var area = new Rect(w / 2 - 520, 100, 1040, 470);
            Fill(area, new Color(0.04f, 0.06f, 0.08f, 0.92f));
            string name = e.Kind switch { EKind.Beacon => "FORWARD BASE BEACON (upkeep)", EKind.Smelter => "FORGE SMELTER", _ => "SUPPLIES" };
            GUI.Label(new Rect(area.x + 20, area.y + 12, 600, 30), name, title);
            GUI.Label(new Rect(area.x + 20, area.y + 40, 480, 22), "Yours: click to put in", small);
            GUI.Label(new Rect(area.x + 540, area.y + 40, 480, 22), c.OpenStorageId == e.Id ? "Inside: click to take" : "â€¦", small);
            for (int i = 0; i < Inventory.SlotCount; i++)
            {
                var r = new Rect(area.x + 20 + (i % 6) * 82, area.y + 66 + (i / 6) * 70, 76, 64);
                if (c.Inv.Item[i] != null && GUI.Button(r, $"{ItemDb.Name(c.Inv.Item[i])}\nx{c.Inv.Count[i]}", small))
                    p.Interact(e.Id, "put", c.Inv.Item[i], c.Inv.Count[i]);
                var r2 = new Rect(area.x + 540 + (i % 6) * 82, area.y + 66 + (i / 6) * 70, 76, 64);
                if (c.OpenStorageId == e.Id && c.Storage.Item[i] != null && GUI.Button(r2, $"{ItemDb.Name(c.Storage.Item[i])}\nx{c.Storage.Count[i]}", small))
                    p.Interact(e.Id, "take", c.Storage.Item[i], c.Storage.Count[i]);
            }
            if (e.Kind == EKind.Smelter && GUI.Button(new Rect(area.x + 20, area.yMax - 44, 160, 32), e.Open ? "Extinguish" : "Light"))
                p.Interact(e.Id, "light");
            if (GUI.Button(new Rect(area.xMax - 120, area.yMax - 44, 100, 32), "Close")) storageId = 0;
        }

        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
