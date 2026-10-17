using System.Collections.Generic;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Bank window, driven by the server's real bank. Open a bank booth or talk to a banker with its
    /// "Bank" option; the server answers BANKOPEN and streams BANK lines. Bank items on the left,
    /// backpack on the right. Left-click = 1, Shift+left-click = All, right-click = Withdraw/Deposit
    /// 1/5/10/X/All. Esc or Close shuts the bank on the server (BANKCLOSE).
    /// </summary>
    public class OsrsBank : MonoBehaviour
    {
        /// <summary>True while the bank window owns the mouse; movement, hotkeys and panels stand down.</summary>
        public static bool Active { get; private set; }

        private const int All = -1;
        private const float CloseTimeout = 2f;

        private OsrsServerLink link;
        private GUIStyle cellStyle, titleStyle, smallStyle, menuStyle;
        private Vector2 bankScroll;
        private OsrsServerLink.Item menuItem;
        private bool menuFromBank;
        private Vector2 menuPos;
        private Rect menuBox;
        private string xText = "100";
        private bool wasOpen;
        private float closeRequestedAt = -1f;

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
        }

        private void Update()
        {
            if (link == null) return;
            bool open = link.BankOpen;
            if (open && !wasOpen)
            {
                closeRequestedAt = -1f;
                menuItem = null;
                bankScroll = Vector2.zero;
            }
            if (!open && wasOpen)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            wasOpen = open;
            // Stay Active (blocking movement) until the server confirms the close, with a fallback.
            if (closeRequestedAt >= 0f && Time.time - closeRequestedAt > CloseTimeout && open)
            {
                closeRequestedAt = -1f;
            }
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
            link.Send("BANKCLOSE");
            closeRequestedAt = Time.time;
            menuItem = null;
        }

        private void OnGUI()
        {
            if (link == null || !link.BankOpen) return;
            if (closeRequestedAt >= 0f) return; // hidden while the server closes it
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

            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (menuItem != null) menuItem = null; else RequestClose();
                e.Use();
                return;
            }

            float bankW = 560f, invW = 300f, h = 520f, gap = 12f;
            float x0 = (Screen.width - bankW - invW - gap) / 2f, y0 = (Screen.height - h) / 2f;
            var bankArea = new Rect(x0, y0, bankW, h);
            var invArea = new Rect(x0 + bankW + gap, y0, invW, h);
            Panel(bankArea);
            Panel(invArea);

            DrawBank(bankArea);
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

        private void DrawBank(Rect area)
        {
            int used = 0;
            foreach (var it in link.bank) if (it.count > 0) used++;
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), $"The Bank of Gielinor  ({used} items)", titleStyle);

            const int cols = 8;
            float cell = 64f, g = 4f;
            var view = new Rect(area.x + 8, area.y + 36, area.width - 16, area.height - 36 - 74);
            int rows = Mathf.Max(1, (link.bank.Count + cols - 1) / cols);
            var content = new Rect(0, 0, view.width - 20, rows * (cell - 6 + g));
            bankScroll = GUI.BeginScrollView(view, bankScroll, content);
            float ox = (content.width - cols * cell - (cols - 1) * g) / 2f;
            for (int i = 0; i < link.bank.Count; i++)
            {
                var item = link.bank[i];
                var r = new Rect(ox + (i % cols) * (cell + g), (i / cols) * (cell - 6 + g), cell, cell - 6);
                bool placeholder = item.count <= 0;
                string label = placeholder ? $"<color=#888888>{item.name}</color>"
                    : (item.count > 1 ? $"{item.name}\n<color=#ffff00>{FormatCount(item.count)}</color>" : item.name);
                // Placeholders: faded icon, no count (as in OSRS).
                OsrsItemIcons.DrawSlot(r, item.id, placeholder ? 0 : item.count, item.name, label, cellStyle,
                    placeholder ? new Color(0.2f, 0.17f, 0.13f, 0.6f) : new Color(0.35f, 0.28f, 0.2f), placeholder ? 0.35f : 1f);
                HandleClick(r, item, true);
            }
            GUI.EndScrollView();

            // Footer: note toggle, X amount, deposit buttons, close.
            float fy = area.yMax - 66;
            bool note = GUI.Toggle(new Rect(area.x + 12, fy, 150, 22), link.BankNote, " Withdraw as note");
            if (note != link.BankNote) link.Send("BANKNOTE " + (note ? "1" : "0"));
            GUI.Label(new Rect(area.x + 170, fy, 70, 22), "X amount:", smallStyle);
            xText = GUI.TextField(new Rect(area.x + 240, fy, 80, 22), xText, 10);
            if (GUI.Button(new Rect(area.x + 12, fy + 30, 160, 26), "Deposit inventory")) link.Send("DEPOSITALL");
            if (GUI.Button(new Rect(area.x + 180, fy + 30, 160, 26), "Deposit worn items")) link.Send("DEPOSITWORN");
            if (GUI.Button(new Rect(area.xMax - 112, fy + 30, 100, 26), "Close [Esc]")) RequestClose();
            GUI.Label(new Rect(area.x + 340, fy, area.width - 350, 22), "Click: 1  Shift: All  Right: more", smallStyle);
        }

        private void DrawBackpack(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y + 6, area.width, 24), "Backpack", titleStyle);
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
                HandleClick(r, item, false);
            }
        }

        private void HandleClick(Rect r, OsrsServerLink.Item item, bool fromBank)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || !r.Contains(e.mousePosition)) return;
            if (menuItem != null && menuBox.Contains(GUIUtility.GUIToScreenPoint(e.mousePosition))) return; // click belongs to the open menu
            if (e.button == 1)
            {
                menuItem = item; menuFromBank = fromBank;
                menuPos = GUIUtility.GUIToScreenPoint(e.mousePosition);
            }
            else if (e.button == 0)
            {
                Transfer(item, fromBank, e.shift ? All : 1);
                menuItem = null;
            }
            e.Use();
        }

        private void Transfer(OsrsServerLink.Item item, bool fromBank, int count)
        {
            link.Send($"{(fromBank ? "WITHDRAW" : "DEPOSIT")} {item.slot} {item.id} {count}");
        }

        private int XAmount()
        {
            if (!int.TryParse(xText.Trim().ToLowerInvariant().Replace("k", "000").Replace("m", "000000"), out int n)) return 0;
            return n;
        }

        private void DrawContextMenu()
        {
            if (menuItem == null) return;
            string verb = menuFromBank ? "Withdraw" : "Deposit";
            int x = XAmount();
            var entries = new List<(string label, int count)>
            {
                ($"{verb}-1", 1), ($"{verb}-5", 5), ($"{verb}-10", 10),
            };
            if (x > 0) entries.Add(($"{verb}-{x:N0} (X)", x));
            entries.Add(($"{verb}-All", All));
            entries.Add(("Cancel", 0));
            float w = 180f, rowH = 24f;
            var box = new Rect(menuPos.x - 10, menuPos.y - 6, w, entries.Count * rowH + 12 + 20);
            if (box.xMax > Screen.width) box.x = Screen.width - w;
            if (box.yMax > Screen.height) box.y = Screen.height - box.height;
            menuBox = box;
            GUI.color = new Color(0.1f, 0.08f, 0.06f, 0.97f);
            GUI.Box(box, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 6, box.y + 4, w - 12, 20), menuItem.name, smallStyle);
            for (int i = 0; i < entries.Count; i++)
            {
                var r = new Rect(box.x + 6, box.y + 26 + i * rowH, w - 12, rowH - 2);
                if (GUI.Button(r, entries[i].label, menuStyle))
                {
                    if (entries[i].count != 0) Transfer(menuItem, menuFromBank, entries[i].count);
                    menuItem = null;
                }
            }
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && !box.Contains(ev.mousePosition)) menuItem = null;
        }

        private static string FormatCount(int n)
        {
            if (n >= 10_000_000) return (n / 1_000_000) + "M";
            if (n >= 100_000) return (n / 1000) + "K";
            return n.ToString("N0");
        }
    }
}