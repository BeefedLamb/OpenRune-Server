using System.Collections.Generic;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Shop window, driven by the server's real shops. Use a shopkeeper's "Trade" option (E menu,
    /// Shift+N, the Trade hotbar ability, or right-click in cursor mode); the server answers SHOPOPEN
    /// and streams SHOP lines. Stock on the left, backpack on the right. Left-click = buy/sell 1,
    /// Shift+left-click = 10, right-click = Value / Buy-or-Sell 1/5/10/50. Esc or Close shuts the
    /// shop on the server (SHOPCLOSE).
    /// </summary>
    public class OsrsShop : MonoBehaviour
    {
        /// <summary>True while the shop window owns the mouse; movement, hotkeys and panels stand down.</summary>
        public static bool Active { get; private set; }

        private const float CloseTimeout = 2f;
        private static readonly int[] Quantities = { 1, 5, 10, 50 };

        private OsrsServerLink link;
        private GUIStyle cellStyle, titleStyle, smallStyle, menuStyle;
        private Vector2 shopScroll;
        private int menuSlot = -1, menuId;
        private string menuName;
        private bool menuFromShop;
        private Vector2 menuPos;
        private Rect menuBox;
        private bool wasOpen;
        private float closeRequestedAt = -1f;

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
        }

        private void Update()
        {
            if (link == null) return;
            bool open = link.ShopOpen;
            if (open && !wasOpen)
            {
                closeRequestedAt = -1f;
                menuSlot = -1;
                shopScroll = Vector2.zero;
            }
            if (!open && wasOpen)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            wasOpen = open;
            if (closeRequestedAt >= 0f && Time.time - closeRequestedAt > CloseTimeout && open)
                closeRequestedAt = -1f;
            Active = open;
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void OnDisable()
        {
            Active = false;
        }

        private void RequestClose()
        {
            if (closeRequestedAt >= 0f) return;
            link.Send("SHOPCLOSE");
            closeRequestedAt = Time.time;
            menuSlot = -1;
        }

        private void OnGUI()
        {
            if (link == null || !link.ShopOpen) return;
            if (closeRequestedAt >= 0f) return;
            if (cellStyle == null)
            {
                cellStyle = new GUIStyle(GUI.skin.box) { fontSize = 11, wordWrap = true, alignment = TextAnchor.MiddleCenter, richText = true };
                cellStyle.normal.textColor = Color.white;
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                titleStyle.normal.textColor = new Color(1f, 0.6f, 0.12f);
                smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
                smallStyle.normal.textColor = Color.white;
                menuStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            }

            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (menuSlot >= 0) menuSlot = -1; else RequestClose();
                e.Use();
                return;
            }

            float shopW = 560f, invW = 300f, h = 520f, gap = 12f;
            float x0 = (Screen.width - shopW - invW - gap) / 2f, y0 = (Screen.height - h) / 2f;
            var shopArea = new Rect(x0, y0, shopW, h);
            var invArea = new Rect(x0 + shopW + gap, y0, invW, h);
            Panel(shopArea);
            Panel(invArea);

            DrawStock(shopArea);
            DrawBackpack(invArea);
            DrawContextMenu();
            OsrsItemIcons.DrawHover();
        }

        private static void Panel(Rect r)
        {
            GUI.color = new Color(0.12f, 0.09f, 0.06f, 0.94f);
            GUI.Box(r, GUIContent.none);
            GUI.color = Color.white;
        }

        private void DrawStock(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), string.IsNullOrEmpty(link.ShopTitle) ? "Shop" : link.ShopTitle, titleStyle);

            const int cols = 8;
            float cell = 64f, g = 4f;
            var view = new Rect(area.x + 8, area.y + 36, area.width - 16, area.height - 36 - 50);
            int rows = Mathf.Max(1, (link.shop.Count + cols - 1) / cols);
            var content = new Rect(0, 0, view.width - 20, rows * (cell + 8 + g));
            shopScroll = GUI.BeginScrollView(view, shopScroll, content);
            float ox = (content.width - cols * cell - (cols - 1) * g) / 2f;
            for (int i = 0; i < link.shop.Count; i++)
            {
                var item = link.shop[i];
                var r = new Rect(ox + (i % cols) * (cell + g), (i / cols) * (cell + 8 + g), cell, cell - 6);
                bool empty = item.count <= 0;
                string label = $"{item.name}\n<color=#ffff00>{FormatCount(item.count)}</color>";
                OsrsItemIcons.DrawSlot(r, item.id, item.count, item.name, label, cellStyle,
                    empty ? new Color(0.2f, 0.17f, 0.13f, 0.6f) : new Color(0.35f, 0.28f, 0.2f), empty ? 0.4f : 1f);
                GUI.Label(new Rect(r.x - 4, r.yMax, cell + 8, 14), $"<size=10><color=#ffd040>{FormatCount(item.price)}</color></size>", smallStyle);
                HandleClick(r, item.slot, item.id, item.name, true);
            }
            GUI.EndScrollView();

            float fy = area.yMax - 40;
            GUI.Label(new Rect(area.x + 12, fy, area.width - 130, 26),
                $"Prices in {link.ShopCurrency}.  Click: buy 1  Shift: 10  Right: more", smallStyle);
            if (GUI.Button(new Rect(area.xMax - 112, fy, 100, 26), "Close [Esc]")) RequestClose();
        }

        private void DrawBackpack(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), "Backpack (sell)", titleStyle);
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
                OsrsItemIcons.DrawSlot(r, item.id, item.count, item.name,
                    item.count > 1 ? $"{item.name}\n<color=#ffff00>{FormatCount(item.count)}</color>" : item.name,
                    cellStyle, new Color(0.35f, 0.28f, 0.2f));
                HandleClick(r, item.slot, item.id, item.name, false);
            }
        }

        private void HandleClick(Rect r, int slot, int id, string name, bool fromShop)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || !r.Contains(e.mousePosition)) return;
            if (menuSlot >= 0 && menuBox.Contains(GUIUtility.GUIToScreenPoint(e.mousePosition))) return;
            if (e.button == 1)
            {
                menuSlot = slot; menuId = id; menuName = name; menuFromShop = fromShop;
                menuPos = GUIUtility.GUIToScreenPoint(e.mousePosition);
            }
            else if (e.button == 0)
            {
                Trade(slot, id, fromShop, e.shift ? 10 : 1);
                menuSlot = -1;
            }
            e.Use();
        }

        private void Trade(int slot, int id, bool fromShop, int count)
        {
            link.Send($"{(fromShop ? "BUY" : "SELL")} {slot} {id} {count}");
        }

        private void DrawContextMenu()
        {
            if (menuSlot < 0) return;
            string verb = menuFromShop ? "Buy" : "Sell";
            var entries = new List<(string label, int count)> { ("Value", -1) };
            foreach (var q in Quantities) entries.Add(($"{verb} {q}", q));
            entries.Add(("Cancel", 0));
            float w = 180f, rowH = 24f;
            var box = new Rect(menuPos.x - 10, menuPos.y - 6, w, entries.Count * rowH + 12 + 20);
            if (box.xMax > Screen.width) box.x = Screen.width - w;
            if (box.yMax > Screen.height) box.y = Screen.height - box.height;
            menuBox = box;
            GUI.color = new Color(0.1f, 0.08f, 0.06f, 0.97f);
            GUI.Box(box, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 6, box.y + 4, w - 12, 20), menuName, smallStyle);
            for (int i = 0; i < entries.Count; i++)
            {
                var r = new Rect(box.x + 6, box.y + 26 + i * rowH, w - 12, rowH - 2);
                if (GUI.Button(r, entries[i].label, menuStyle))
                {
                    int c = entries[i].count;
                    if (c == -1) link.Send($"SHOPVALUE {(menuFromShop ? 0 : 1)} {menuSlot} {menuId}");
                    else if (c > 0) Trade(menuSlot, menuId, menuFromShop, c);
                    menuSlot = -1;
                }
            }
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !box.Contains(ev.mousePosition)) menuSlot = -1;
        }

        private static string FormatCount(int n)
        {
            if (n >= 10_000_000) return (n / 1_000_000) + "M";
            if (n >= 100_000) return (n / 1000) + "K";
            return n.ToString("N0");
        }
    }
}