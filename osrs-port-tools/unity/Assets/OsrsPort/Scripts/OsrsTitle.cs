using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The cache title screen, laid out at 765x503 and scaled to the window.
    /// title.jpg is the left half; the right half is that image mirrored.
    /// </summary>
    public static class OsrsTitle
    {
        public const float Width = 765f;
        public const float Height = 503f;
        public const int ScapeMain = 0;

        private static Texture2D background;
        private static Texture2D logo;
        private static Texture2D box;
        private static Texture2D button;
        private static Texture2D composed;
        private static bool musicStarted;

        public static bool Ready => background != null && logo != null && box != null && button != null;

        public static void Draw()
        {
            Load();
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (!Ready) return;
            if (composed == null) composed = Compose();
            float scale = Mathf.Min(Screen.width / Width, Screen.height / Height);
            float ox = (Screen.width - Width * scale) * 0.5f;
            float oy = (Screen.height - Height * scale) * 0.5f;
            GUI.DrawTexture(new Rect(ox, oy, Width * scale, Height * scale), composed, ScaleMode.StretchToFill, true);
            if (!musicStarted)
            {
                musicStarted = true;
                OsrsMusic.Ensure().PlaySong(ScapeMain, 0, 0, 0, 0);
            }
        }

        public static Rect LoginField() => Scaled(262, 268, 240, 28);

        public static Rect LoginButton() => Scaled(308, 320, 147, 41);

        private static Rect Scaled(float x, float y, float w, float h)
        {
            float scale = Mathf.Min(Screen.width / Width, Screen.height / Height);
            float ox = (Screen.width - Width * scale) * 0.5f;
            float oy = (Screen.height - Height * scale) * 0.5f;
            return new Rect(ox + x * scale, oy + y * scale, w * scale, h * scale);
        }

        private static void Load()
        {
            if (Ready) return;
            musicStarted = false;
            composed = null;
            string dir = Path.Combine(Application.streamingAssetsPath, "ui");
            background = LoadTex(Path.Combine(dir, "title.jpg"));
            logo = LoadTex(Path.Combine(dir, "logo.png"));
            box = LoadTex(Path.Combine(dir, "titlebox.png"));
            button = LoadTex(Path.Combine(dir, "titlebutton.png"));
            if (!Ready) Debug.LogWarning("OsrsPort: title art missing from " + dir);
        }

        private static Texture2D Compose()
        {
            const int W = 765, H = 503;
            var pix = new Color32[W * H];
            Blit(pix, W, H, background, 0, 0, false, false);
            Blit(pix, W, H, background, 382, 0, true, false);
            Blit(pix, W, H, logo, 234, 8, false, true);
            Blit(pix, W, H, box, 202, 200, false, true);
            Blit(pix, W, H, button, 308, 320, false, true);
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.SetPixels32(pix);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        /// <summary>Copy a sprite onto the 765x503 page. guiY is from the top. mirror flips the source left-right.</summary>
        private static void Blit(Color32[] dst, int dw, int dh, Texture2D src, int guiX, int guiY, bool mirror, bool useAlpha)
        {
            var sp = src.GetPixels32();
            int sw = src.width, sh = src.height;
            int bottom = dh - guiY - sh;
            for (int y = 0; y < sh; y++)
            {
                int ty = bottom + y;
                if (ty < 0 || ty >= dh) continue;
                for (int x = 0; x < sw; x++)
                {
                    int tx = guiX + (mirror ? sw - 1 - x : x);
                    if (tx < 0 || tx >= dw) continue;
                    var p = sp[y * sw + x];
                    if (useAlpha)
                    {
                        if (p.a == 0) continue;
                    }
                    else p.a = 255;
                    dst[ty * dw + tx] = p;
                }
            }
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
