using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The cache title screen at 765x503, scaled to the window.
    /// title.jpg is the left half; the right half is that image mirrored.
    /// The box is drawn where the client draws it, at (202, 171).
    /// </summary>
    public static class OsrsTitle
    {
        public const float Width = 765f;
        public const float Height = 503f;
        public const int ScapeMain = 0;
        public const int BoxX = 202;
        public const int BoxY = 171;

        private static Texture2D background;
        private static Texture2D logo;
        private static Texture2D box;
        private static Texture2D button;
        private static Color32[] page;
        private static OsrsFlames flames;
        private static int flameFrame = -1;
        private static bool musicStarted;

        public static OsrsFont Bold { get; private set; }
        public static OsrsFont Plain { get; private set; }
        public static bool Placed { get; private set; }
        public static bool Ready => background != null && logo != null && box != null && button != null && Bold != null;

        public static void Draw()
        {
            Load();
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (!Ready) return;
            Place();
            GUI.DrawTexture(PageRect(0, 0, 765, 503), background, ScaleMode.StretchToFill, false);
            if (flames != null && flames.Left != null && Event.current.type == EventType.Repaint && flameFrame != Time.frameCount)
            {
                flameFrame = Time.frameCount;
                flames.Step();
            }
            if (flames != null && flames.Left != null)
            {
                GUI.DrawTexture(PageRect(0, 0, OsrsFlames.Width, OsrsFlames.Height), flames.Left, ScaleMode.StretchToFill, false);
                GUI.DrawTexture(PageRect(637, 0, OsrsFlames.Width, OsrsFlames.Height), flames.Right, ScaleMode.StretchToFill, false);
            }
            GUI.DrawTexture(PageRect(234, 18, logo.width, logo.height), logo, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(PageRect(BoxX, BoxY, box.width, box.height), box, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(PageRect(BoxX + 27, BoxY + 130, button.width, button.height), button, ScaleMode.StretchToFill, true);
            if (!musicStarted)
            {
                musicStarted = true;
                OsrsMusic.Ensure().PlaySong(ScapeMain, 0, 0, 0, 0);
            }
        }

        /// <summary>The username line, the Login label and an optional message, in the cache fonts.</summary>
        public static void DrawLogin(string name, bool caret, string message, string status)
        {
            if (!Ready) return;
            if (!string.IsNullOrEmpty(message))
                Bold.Draw(message, BoxX + 180, BoxY + 53, 0xFFFF00, true, true);
            string typed = "Username: " + (name ?? "");
            if (caret) typed += "|";
            Bold.Draw(typed, BoxX + 90, BoxY + 90, 0xFFFFFF, false, true);
            Bold.Draw("Login", BoxX + 100, BoxY + 155, 0xFFFFFF, true, true);
            if (!string.IsNullOrEmpty(status) && Plain != null)
                Plain.Draw(status, BoxX + 180, BoxY + 175, 0xFFFFFF, true, true);
        }

        public static bool LoginHit() => Hit(BoxX + 27, BoxY + 130, 147, 41);

        public static Rect PageRect(int x, int y, int w, int h)
        {
            float scale = Scale;
            float ox = (Screen.width - Width * scale) * 0.5f;
            float oy = (Screen.height - Height * scale) * 0.5f;
            return new Rect(ox + x * scale, oy + y * scale, w * scale, h * scale);
        }

        public static bool Hit(int x, int y, int w, int h)
        {
            if (!Placed || Event.current == null) return false;
            float scale = Scale;
            if (scale <= 0f) return false;
            var m = Event.current.mousePosition;
            float ox = (Screen.width - Width * scale) * 0.5f;
            float oy = (Screen.height - Height * scale) * 0.5f;
            float px = (m.x - ox) / scale;
            float py = (m.y - oy) / scale;
            return px >= x && py >= y && px < x + w && py < y + h;
        }

        private static float Scale => Mathf.Min(Screen.width / Width, Screen.height / Height);

        private static void Place()
        {
            if (Placed) return;
            Placed = true;
            page = new Color32[765 * 503];
            var src = background.GetPixels32();
            int sw = background.width, sh = background.height;
            for (int y = 0; y < 503; y++)
                for (int x = 0; x < 383; x++)
                {
                    int sy = sh - 1 - Mathf.Clamp(y, 0, sh - 1);
                    int sx = Mathf.Clamp(x, 0, sw - 1);
                    page[y * 765 + x] = src[sy * sw + sx];
                    int mx = 382 + (382 - x);
                    if (mx >= 0 && mx < 765) page[y * 765 + mx] = src[sy * sw + sx];
                }
            var masks = new byte[12][];
            string dir = Path.Combine(Application.streamingAssetsPath, "ui");
            for (int i = 0; i < 12; i++)
                masks[i] = RuneMask(Path.Combine(dir, "rune" + i + ".png"));
            var full = new Texture2D(765, 503, TextureFormat.RGBA32, false);
            var pix = new Color32[page.Length];
            for (int y = 0; y < 503; y++)
                System.Array.Copy(page, y * 765, pix, (502 - y) * 765, 765);
            full.SetPixels32(pix);
            full.Apply();
            full.filterMode = FilterMode.Point;
            background = full;
            flames = new OsrsFlames();
            flames.Load(masks, page, 765);
        }

        private static byte[] RuneMask(string path)
        {
            var mask = new byte[96 * 96];
            if (!File.Exists(path)) return mask;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return mask;
            var pix = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            for (int y = 0; y < 96 && y < h; y++)
                for (int x = 0; x < 96 && x < w; x++)
                    mask[y * 96 + x] = pix[(h - 1 - y) * w + x].a;
            Object.Destroy(tex);
            return mask;
        }

        private static void Load()
        {
            if (Ready) return;
            musicStarted = false;
            Placed = false;
            string dir = Path.Combine(Application.streamingAssetsPath, "ui");
            background = LoadTex(Path.Combine(dir, "title.jpg"));
            logo = LoadTex(Path.Combine(dir, "logo.png"));
            box = LoadTex(Path.Combine(dir, "titlebox.png"));
            button = LoadTex(Path.Combine(dir, "titlebutton.png"));
            Bold = OsrsFont.Load(Path.Combine(dir, "b12.fnt"));
            Plain = OsrsFont.Load(Path.Combine(dir, "p12.fnt"));
            if (!Ready) Debug.LogWarning("OsrsPort: title art missing from " + dir);
        }

        private static Texture2D LoadTex(string path)
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
            tex.filterMode = FilterMode.Point;
            return tex;
        }
    }
}
