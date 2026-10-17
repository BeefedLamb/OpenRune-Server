using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// Spellbook window (M), driven by the server's `SPELLS` line: the current spellbook's spells with
    /// their icons (spell objs are in the exported item icon set), level and rune costs. Spells you
    /// lack the level or runes for are greyed out. Click a self-cast spell to send `CAST objId`; click an
    /// item-target spell (Enchant, Superheat, Alchemy, Plank Make, Reanimation) to arm it, then click a
    /// backpack item to send `CAST objId slot`. Combat spells are listed but cast by attacking. Esc or M
    /// closes it (Esc first cancels an armed spell).
    /// </summary>
    public class OsrsSpellbook : MonoBehaviour
    {
        /// <summary>True while the spellbook owns the mouse; movement, hotkeys and panels stand down.</summary>
        public static bool Active { get; private set; }

        private OsrsServerLink link;
        private GUIStyle cellStyle, titleStyle, smallStyle, tipStyle;
        private Vector2 scroll;
        private OsrsServerLink.Spell armed;
        private string hover;

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
        }

        private void Update()
        {
            if (link == null) return;
            if (OsrsInteraction.TextInputActive || OsrsCharacterDesign.Active || OsrsBank.Active || OsrsShop.Active)
            {
                if (Active) SetOpen(false);
                return;
            }
            bool m;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            m = kb.mKey.wasPressedThisFrame;
#else
            m = Input.GetKeyDown(KeyCode.M);
#endif
            if (m) SetOpen(!Active);
            if (Active)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void OnDisable()
        {
            Active = false;
        }

        private void SetOpen(bool open)
        {
            Active = open;
            armed = null;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
        }

        private void OnGUI()
        {
            if (!Active || link == null) return;
            if (cellStyle == null)
            {
                cellStyle = new GUIStyle(GUI.skin.box) { fontSize = 10, wordWrap = true, alignment = TextAnchor.MiddleCenter, richText = true };
                cellStyle.normal.textColor = Color.white;
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                titleStyle.normal.textColor = new Color(1f, 0.6f, 0.12f);
                smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
                smallStyle.normal.textColor = Color.white;
                tipStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, wordWrap = true, alignment = TextAnchor.UpperLeft, richText = true };
                tipStyle.normal.textColor = Color.white;
            }

            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (armed != null) armed = null; else SetOpen(false);
                e.Use();
                return;
            }

            float bookW = 460f, invW = 300f, h = 520f, gap = 12f;
            float x0 = (Screen.width - bookW - invW - gap) / 2f, y0 = (Screen.height - h) / 2f;
            var bookArea = new Rect(x0, y0, bookW, h);
            var invArea = new Rect(x0 + bookW + gap, y0, invW, h);
            Panel(bookArea);
            Panel(invArea);
            hover = null;
            DrawSpells(bookArea);
            DrawBackpack(invArea);
            if (hover != null)
            {
                var mp = e.mousePosition;
                var r = new Rect(mp.x + 16, mp.y + 12, 240, 92);
                if (r.xMax > Screen.width) r.x = Screen.width - r.width;
                if (r.yMax > Screen.height) r.y = Screen.height - r.height;
                GUI.Box(r, hover, tipStyle);
            }
        }

        private static void Panel(Rect r)
        {
            GUI.color = new Color(0.12f, 0.09f, 0.06f, 0.94f);
            GUI.Box(r, GUIContent.none);
            GUI.color = Color.white;
        }

        private int MagicLevel()
        {
            foreach (var s in link.stats)
                if (s.name != null && s.name.ToLowerInvariant() == "magic") return s.level;
            return -1;
        }

        private void DrawSpells(Rect area)
        {
            string book = string.IsNullOrEmpty(link.SpellbookName) ? "Spellbook" : char.ToUpper(link.SpellbookName[0]) + link.SpellbookName.Substring(1);
            int magic = MagicLevel();
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), $"{book} spellbook" + (magic >= 0 ? $"  (Magic {magic})" : ""), titleStyle);

            const int cols = 6;
            float cell = 66f, g = 6f;
            var view = new Rect(area.x + 8, area.y + 36, area.width - 16, area.height - 36 - 50);
            int rows = Mathf.Max(1, (link.spells.Count + cols - 1) / cols);
            var content = new Rect(0, 0, view.width - 20, rows * (cell + g));
            scroll = GUI.BeginScrollView(view, scroll, content);
            float ox = (content.width - cols * cell - (cols - 1) * g) / 2f;
            for (int i = 0; i < link.spells.Count; i++)
            {
                var sp = link.spells[i];
                var r = new Rect(ox + (i % cols) * (cell + g), (i / cols) * (cell + g), cell, cell);
                bool usable = sp.levelOk && sp.runesOk && (sp.target == 's' || sp.target == 'i');
                bool isArmed = armed != null && armed.id == sp.id;
                Color box = isArmed ? new Color(0.55f, 0.42f, 0.12f) : usable ? new Color(0.30f, 0.24f, 0.36f) : new Color(0.18f, 0.16f, 0.16f, 0.7f);
                OsrsItemIcons.DrawSlot(r, sp.id, 1, sp.name, sp.name, cellStyle, box, usable ? 1f : 0.35f);
                if (!sp.levelOk)
                    GUI.Label(new Rect(r.x + 2, r.yMax - 16, r.width, 16), $"<size=10><color=#ff6060>{sp.level}</color></size>", smallStyle);
                if (r.Contains(e_mouse()))
                    hover = Tooltip(sp);
                var ev = Event.current;
                if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
                {
                    OnSpellClicked(sp);
                    ev.Use();
                }
            }
            GUI.EndScrollView();

            float fy = area.yMax - 40;
            string hint = armed != null
                ? $"<color=#ffd040>Casting {armed.name}:</color> click a backpack item (Esc cancels)"
                : "Click a spell to cast.  Grey = level/runes missing.";
            GUI.Label(new Rect(area.x + 12, fy, area.width - 130, 32), hint, smallStyle);
            if (GUI.Button(new Rect(area.xMax - 112, fy, 100, 26), "Close [M]")) SetOpen(false);
        }

        private static Vector2 e_mouse() => Event.current.mousePosition;

        private string Tooltip(OsrsServerLink.Spell sp)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(sp.name).Append("</b>\n");
            sb.Append(sp.levelOk ? "Level " : "<color=#ff6060>Level ").Append(sp.level).Append(sp.levelOk ? "" : "</color>");
            sb.Append("  ").Append(sp.type).Append('\n');
            if (sp.runes.Count > 0)
            {
                sb.Append(sp.runesOk ? "" : "<color=#ff6060>");
                for (int i = 0; i < sp.runes.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(sp.runes[i].count).Append("x ").Append(OsrsItemIcons.Has(sp.runes[i].id) ? RuneName(sp.runes[i].id) : "#" + sp.runes[i].id);
                }
                sb.Append(sp.runesOk ? "" : "</color>");
            }
            if (sp.target == 'i') sb.Append("\n<i>Cast on a backpack item</i>");
            else if (sp.target == 'n') sb.Append("\n<i>Combat spell: attack a target to cast</i>");
            else if (sp.target == 'x') sb.Append("\n<i>Not available yet</i>");
            return sb.ToString();
        }

        private string RuneName(int id)
        {
            foreach (var it in link.inventory) if (it.id == id) return it.name;
            return "#" + id;
        }

        private void OnSpellClicked(OsrsServerLink.Spell sp)
        {
            if (sp.target == 'i')
            {
                armed = armed != null && armed.id == sp.id ? null : sp;
                return;
            }
            armed = null;
            if (sp.target == 's') link.Send($"CAST {sp.id}");
            else link.AddMessage(sp.target == 'n' ? "Attack a target to cast combat spells." : $"{sp.name} isn't available yet.");
        }

        private void DrawBackpack(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), armed != null ? "Choose an item" : "Backpack", titleStyle);
            float cell = 64f, g = 4f;
            float ox = area.x + (area.width - 4 * cell - 3 * g) / 2f, oy = area.y + 38;
            var bySlot = new Dictionary<int, OsrsServerLink.Item>();
            foreach (var it in link.inventory) bySlot[it.slot] = it;
            for (int slot = 0; slot < 28; slot++)
            {
                var r = new Rect(ox + (slot % 4) * (cell + g), oy + (slot / 4) * (cell - 6 + g), cell, cell - 6);
                bySlot.TryGetValue(slot, out var item);
                if (item == null)
                {
                    GUI.color = new Color(0.2f, 0.17f, 0.13f);
                    GUI.Box(r, "", cellStyle);
                    GUI.color = Color.white;
                    continue;
                }
                OsrsItemIcons.DrawSlot(r, item.id, item.count, item.name, item.name, cellStyle,
                    armed != null ? new Color(0.42f, 0.33f, 0.18f) : new Color(0.35f, 0.28f, 0.2f));
                var ev = Event.current;
                if (armed != null && ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
                {
                    link.Send($"CAST {armed.id} {item.slot}");
                    if (!ev.shift) armed = null; // Shift keeps the spell armed for repeat casts
                    ev.Use();
                }
            }
            OsrsItemIcons.DrawHover();
        }
    }
}
