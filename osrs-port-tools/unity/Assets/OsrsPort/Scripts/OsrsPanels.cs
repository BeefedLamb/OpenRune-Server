using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// Side panels in the OSRS spirit: I = backpack, O = worn equipment, K = skills. A panel frees
    /// the mouse; left-click an item for its first option, right-click for the full menu. The same
    /// key or Escape closes it.
    /// </summary>
    public class OsrsPanels : MonoBehaviour
    {
        public enum Panel { None, Inventory, Equipment, Skills }

        /// <summary>True while a panel owns the mouse; movement and crosshair scripts stand down.</summary>
        public static bool UiOpen => panelOpen || OsrsCharacterDesign.Active || OsrsBank.Active || OsrsShop.Active || OsrsSpellbook.Active;
        private static bool panelOpen;

        private Panel panel = Panel.None;
        private OsrsServerLink link;
        private GUIStyle cellStyle, titleStyle, smallStyle, menuStyle;
        private OsrsServerLink.Item menuItem;
        private bool menuWorn;
        private Vector2 menuPos;

        private static readonly string[] WornSlotNames =
            { "Head", "Cape", "Neck", "Weapon", "Body", "Shield", "", "Legs", "", "Hands", "Feet", "", "Ring", "Ammo" };

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
        }

        private void Update()
        {
            if (OsrsInteraction.TextInputActive || OsrsCharacterDesign.Active) return;
            if (OsrsBank.Active || OsrsShop.Active || OsrsSpellbook.Active)
            {
                // The bank window owns the mouse and keys; drop any side panel without re-locking the cursor.
                panel = Panel.None; menuItem = null; panelOpen = false;
                return;
            }
            bool i, o, k, esc;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            i = kb.iKey.wasPressedThisFrame; o = kb.oKey.wasPressedThisFrame; k = kb.kKey.wasPressedThisFrame;
            esc = kb.escapeKey.wasPressedThisFrame;
#else
            i = Input.GetKeyDown(KeyCode.I); o = Input.GetKeyDown(KeyCode.O); k = Input.GetKeyDown(KeyCode.K);
            esc = Input.GetKeyDown(KeyCode.Escape);
#endif
            if (i) Toggle(Panel.Inventory);
            else if (o) Toggle(Panel.Equipment);
            else if (k) Toggle(Panel.Skills);
            else if (esc && panel != Panel.None) Toggle(panel);
        }

        private void Toggle(Panel p)
        {
            panel = panel == p ? Panel.None : p;
            menuItem = null;
            panelOpen = panel != Panel.None;
            Cursor.lockState = panelOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = panelOpen;
        }

        private void OnGUI()
        {
            if (panel == Panel.None || link == null) return;
            if (cellStyle == null)
            {
                cellStyle = new GUIStyle(GUI.skin.box) { fontSize = 11, wordWrap = true, alignment = TextAnchor.MiddleCenter, richText = true };
                cellStyle.normal.textColor = Color.white;
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                titleStyle.normal.textColor = new Color(1f, 0.6f, 0.12f);
                smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                smallStyle.normal.textColor = Color.white;
                menuStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            }
            float w = 300f, h = 470f;
            var area = new Rect(Screen.width - w - 16, (Screen.height - h) / 2f, w, h);
            GUI.color = new Color(0.12f, 0.09f, 0.06f, 0.92f);
            GUI.Box(area, GUIContent.none);
            GUI.color = Color.white;
            switch (panel)
            {
                case Panel.Inventory: DrawInventory(area); break;
                case Panel.Equipment: DrawEquipment(area); break;
                case Panel.Skills: DrawSkills(area); break;
            }
            GUI.Label(new Rect(area.x, area.yMax - 24, area.width, 20), "[I] Backpack   [O] Worn   [K] Skills   [Esc] close", smallStyle);
            DrawContextMenu();
            OsrsItemIcons.DrawHover();
        }

        private void DrawInventory(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), "Backpack", titleStyle);
            float cell = 64f, gap = 4f;
            float ox = area.x + (area.width - 4 * cell - 3 * gap) / 2f, oy = area.y + 38;
            var bySlot = new Dictionary<int, OsrsServerLink.Item>();
            foreach (var it in link.inventory) bySlot[it.slot] = it;
            for (int slot = 0; slot < 28; slot++)
            {
                var r = new Rect(ox + (slot % 4) * (cell + gap), oy + (slot / 4) * (cell - 6 + gap), cell, cell - 6);
                bySlot.TryGetValue(slot, out var item);
                if (item == null)
                {
                    GUI.color = new Color(0.2f, 0.17f, 0.13f);
                    GUI.Box(r, "", cellStyle);
                    GUI.color = Color.white;
                    continue;
                }
                OsrsItemIcons.DrawSlot(r, item.id, item.count, item.name,
                    item.count > 1 ? $"{item.name}\n<color=#ffff00>{item.count:N0}</color>" : item.name,
                    cellStyle, new Color(0.35f, 0.28f, 0.2f));
                HandleItemClick(r, item, false);
            }
        }

        private void DrawEquipment(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), "Worn Equipment", titleStyle);
            var bySlot = new Dictionary<int, OsrsServerLink.Item>();
            foreach (var it in link.equipment) bySlot[it.slot] = it;
            float y = area.y + 40;
            for (int slot = 0; slot < WornSlotNames.Length; slot++)
            {
                if (WornSlotNames[slot].Length == 0) continue;
                bySlot.TryGetValue(slot, out var item);
                var r = new Rect(area.x + 12, y, area.width - 24, 26);
                GUI.color = item != null ? new Color(0.35f, 0.28f, 0.2f) : new Color(0.2f, 0.17f, 0.13f);
                GUI.Box(r, "", cellStyle);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 6, r.y + 3, 70, 20), WornSlotNames[slot] + ":", smallStyle);
                float nameX = 76;
                if (item != null && OsrsItemIcons.Draw(new Rect(r.x + 72, r.y, OsrsItemIcons.CellW * 26f / OsrsItemIcons.CellH, 26), item.id))
                    nameX = 106;
                GUI.Label(new Rect(r.x + nameX, r.y + 3, r.width - nameX - 4, 20), item != null ? (item.count > 1 ? $"{item.name} x{item.count:N0}" : item.name) : "-", smallStyle);
                if (item != null) HandleItemClick(r, item, true);
                y += 30;
            }
            GUI.Label(new Rect(area.x + 12, y + 8, area.width - 24, 40), "Left-click: remove   Right-click: options", smallStyle);
        }

        private void DrawSkills(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), "Skills", titleStyle);
            float y = area.y + 40;
            int total = 0;
            for (int i = 0; i < link.stats.Count; i++)
            {
                var st = link.stats[i];
                total += st.baseLevel;
                var r = new Rect(area.x + 12 + (i % 2) * 140, y + (i / 2) * 30, 132, 26);
                GUI.color = new Color(0.25f, 0.2f, 0.15f);
                GUI.Box(r, "", cellStyle);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 6, r.y + 3, 80, 20), st.name, smallStyle);
                var lv = new GUIStyle(smallStyle) { alignment = TextAnchor.MiddleRight };
                lv.normal.textColor = st.level < st.baseLevel ? new Color(1f, 0.5f, 0.3f) : (st.level > st.baseLevel ? new Color(0.4f, 1f, 0.4f) : Color.white);
                GUI.Label(new Rect(r.x + 60, r.y + 3, r.width - 66, 20), $"{st.level}/{st.baseLevel}", lv);
                if (r.Contains(Event.current.mousePosition))
                    GUI.Label(new Rect(area.x + 12, area.yMax - 48, area.width - 24, 20), $"{st.name} xp: {st.xp:N0}", smallStyle);
            }
            GUI.Label(new Rect(area.x + 12, area.yMax - 68, area.width - 24, 20), $"Total level: {total}", smallStyle);
        }

        private void HandleItemClick(Rect r, OsrsServerLink.Item item, bool worn)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || !r.Contains(e.mousePosition)) return;
            if (e.button == 1)
            {
                menuItem = item; menuWorn = worn; menuPos = e.mousePosition;
            }
            else
            {
                int op = FirstOp(item.ops);
                if (worn) op = 1;
                if (op > 0) link.Send($"{(worn ? "OPWORN" : "OPHELD")} {item.slot} {op}");
                menuItem = null;
            }
            e.Use();
        }

        private static int FirstOp(string[] ops)
        {
            for (int i = 0; i < ops.Length && i < 4; i++)
                if (!string.IsNullOrEmpty(ops[i])) return i + 1;
            return -1;
        }

        private void DrawContextMenu()
        {
            if (menuItem == null) return;
            var entries = new List<(string label, int op)>();
            if (menuWorn) entries.Add(("Remove", 1));
            for (int i = 0; i < menuItem.ops.Length; i++)
                if (!string.IsNullOrEmpty(menuItem.ops[i]) && !(menuWorn && i == 0)) entries.Add((menuItem.ops[i], i + 1));
            entries.Add(("Examine", 10));
            entries.Add(("Cancel", -1));
            float w = 160f, rowH = 24f;
            var box = new Rect(menuPos.x - w + 10, menuPos.y - 6, w, entries.Count * rowH + 12 + 20);
            if (box.x < 0) box.x = 0;
            GUI.color = new Color(0.1f, 0.08f, 0.06f, 0.97f);
            GUI.Box(box, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 6, box.y + 4, w - 12, 20), menuItem.name, smallStyle);
            for (int i = 0; i < entries.Count; i++)
            {
                var r = new Rect(box.x + 6, box.y + 26 + i * rowH, w - 12, rowH - 2);
                if (GUI.Button(r, entries[i].label, menuStyle))
                {
                    if (entries[i].op > 0)
                        link.Send($"{(menuWorn ? "OPWORN" : "OPHELD")} {menuItem.slot} {entries[i].op}");
                    menuItem = null;
                }
            }
        }
    }
}
