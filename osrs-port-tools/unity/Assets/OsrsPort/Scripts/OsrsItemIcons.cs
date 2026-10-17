using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Item inventory icons exported by `gradlew :or-cache:exportItemIcons` into
    /// StreamingAssets/items: icons_&lt;page&gt;.png sheets (32x32 grid of 36x32 cells, 1024 ids per sheet)
    /// plus icons.txt listing the ids that have an icon. Sheets load lazily on first use and stay
    /// cached. Ids without an icon return false so callers keep their name/count fallback.
    /// </summary>
    public static class OsrsItemIcons
    {
        public const int CellW = 36, CellH = 32;
        private const int PerPage = 1024, Cols = 32, Rows = PerPage / Cols;

        private static HashSet<int> available;
        private static readonly Dictionary<int, Texture2D> pages = new Dictionary<int, Texture2D>();
        private static string dir;

        private static void EnsureIndex()
        {
            if (available != null) return;
            available = new HashSet<int>();
            dir = Path.Combine(Application.streamingAssetsPath, "items");
            var index = Path.Combine(dir, "icons.txt");
            if (!File.Exists(index))
            {
                Debug.LogWarning("[OsrsItemIcons] No item icons found (run gradlew :or-cache:exportItemIcons); showing names only.");
                return;
            }
            foreach (var line in File.ReadAllLines(index))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (int.TryParse(tab < 0 ? line : line.Substring(0, tab), out int id)) available.Add(id);
            }
        }

        public static bool Has(int id)
        {
            EnsureIndex();
            return available.Contains(id);
        }

        private static Texture2D Page(int page)
        {
            if (pages.TryGetValue(page, out var tex)) return tex;
            tex = null;
            var path = Path.Combine(dir, $"icons_{page}.png");
            if (File.Exists(path))
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                if (!tex.LoadImage(File.ReadAllBytes(path))) { Object.Destroy(tex); tex = null; }
            }
            pages[page] = tex;
            return tex;
        }

        /// <summary>Sheet texture and UV rect for an item's icon, or false if it has none.</summary>
        public static bool TryGet(int id, out Texture2D tex, out Rect uv)
        {
            tex = null; uv = default;
            if (id < 0 || !Has(id)) return false;
            tex = Page(id / PerPage);
            if (tex == null) return false;
            int cell = id % PerPage;
            int col = cell % Cols, row = cell / Cols;
            // Texture UVs start bottom-left; sheet rows start at the top.
            uv = new Rect((float)col / Cols, 1f - (float)(row + 1) / Rows, 1f / Cols, 1f / Rows);
            return true;
        }

        /// <summary>
        /// Draws the icon centred in <paramref name="r"/> at a whole-number scale that fits (pixel-crisp),
        /// tinted by GUI.color. Returns false (drawing nothing) when the item has no icon. OnGUI only.
        /// </summary>
        public static bool Draw(Rect r, int id, float alpha = 1f)
        {
            if (!TryGet(id, out var tex, out var uv)) return false;
            if (Event.current != null && Event.current.type != EventType.Repaint) return true;
            float scale = Mathf.Min(r.width / CellW, r.height / CellH);
            if (scale >= 1f) scale = Mathf.Floor(scale);
            float w = CellW * scale, h = CellH * scale;
            var dst = new Rect(r.x + (r.width - w) / 2f, r.y + (r.height - h) / 2f, w, h);
            var prev = GUI.color;
            GUI.color = new Color(prev.r, prev.g, prev.b, prev.a * alpha);
            GUI.DrawTextureWithTexCoords(dst, tex, uv, true);
            GUI.color = prev;
            return true;
        }

        /// <summary>OSRS stack-count colours: yellow, white from 100K, green from 10M.</summary>
        public static string CountText(int n, out Color color)
        {
            if (n >= 10_000_000) { color = new Color(0f, 1f, 0.5f); return (n / 1_000_000) + "M"; }
            if (n >= 100_000) { color = Color.white; return (n / 1000) + "K"; }
            color = Color.yellow;
            return n.ToString();
        }

        private static GUIStyle countStyle;

        /// <summary>Name of the iconed item under the mouse this frame (set by DrawSlot); see DrawHover.</summary>
        public static string HoverText;

        /// <summary>
        /// Item slot: a box tinted <paramref name="boxColor"/>, then the icon with an OSRS-style count in
        /// the top-left (when count &gt; 1). Without an icon it falls back to the old name/count label.
        /// </summary>
        public static void DrawSlot(Rect r, int id, int count, string name, string fallbackLabel, GUIStyle style, Color boxColor, float alpha = 1f)
        {
            var prev = GUI.color;
            GUI.color = boxColor;
            if (!Has(id) || !TryGet(id, out _, out _))
            {
                GUI.Box(r, fallbackLabel, style);
                GUI.color = prev;
                return;
            }
            GUI.Box(r, GUIContent.none, style);
            GUI.color = Color.white;
            Draw(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), id, alpha);
            if (count > 1)
            {
                if (countStyle == null) countStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
                string t = CountText(count, out var c);
                var lr = new Rect(r.x + 3, r.y + 1, r.width - 4, 16);
                countStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(lr.x + 1, lr.y + 1, lr.width, lr.height), t, countStyle);
                countStyle.normal.textColor = c;
                GUI.Label(lr, t, countStyle);
            }
            GUI.color = prev;
            if (Event.current != null && r.Contains(Event.current.mousePosition))
                HoverText = count > 1 ? $"{name} x{count:N0}" : name;
        }

        private static GUIStyle hoverStyle;

        /// <summary>Draws the hovered item's name beside the mouse; call once at the end of OnGUI.</summary>
        public static void DrawHover()
        {
            if (string.IsNullOrEmpty(HoverText) || Event.current == null) return;
            if (Event.current.type == EventType.Repaint)
            {
                if (hoverStyle == null) hoverStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
                hoverStyle.normal.textColor = new Color(1f, 0.6f, 0.2f);
                var size = hoverStyle.CalcSize(new GUIContent(HoverText));
                var m = Event.current.mousePosition;
                GUI.Box(new Rect(m.x + 14, m.y + 10, size.x + 6, size.y + 2), HoverText, hoverStyle);
                HoverText = null;
            }
        }
    }
}
