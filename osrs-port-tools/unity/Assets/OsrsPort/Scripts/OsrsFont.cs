using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// One cache bitmap font (b12 or p12). Glyphs are white; the draw colour tints them.
    /// The y argument is the baseline, matching the client's font draw.
    /// </summary>
    public sealed class OsrsFont
    {
        private struct Glyph
        {
            public int Advance, Ox, Oy, W, H;
            public Texture2D Tex;
        }

        private readonly Glyph[] glyphs = new Glyph[256];

        public static OsrsFont Load(string path)
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (data.Length < 8 || data[0] != 'F' || data[1] != 'N') return null;
            var font = new OsrsFont();
            int p = 4;
            for (int i = 0; i < 256; i++)
            {
                var g = new Glyph
                {
                    Advance = data[p],
                    Ox = data[p + 1],
                    Oy = data[p + 2],
                    W = data[p + 3],
                    H = data[p + 4],
                };
                p += 5;
                if (g.W > 0 && g.H > 0)
                {
                    var pix = new Color32[g.W * g.H];
                    for (int k = 0; k < pix.Length; k++)
                    {
                        byte a = data[p++];
                        pix[k] = new Color32(255, 255, 255, a);
                    }
                    var tex = new Texture2D(g.W, g.H, TextureFormat.RGBA32, false);
                    Flip(pix, g.W, g.H);
                    tex.SetPixels32(pix);
                    tex.Apply();
                    tex.filterMode = FilterMode.Point;
                    g.Tex = tex;
                }
                font.glyphs[i] = g;
            }
            return font;
        }

        public int Width(string text)
        {
            int w = 0;
            if (text == null) return 0;
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i];
                if (c >= 0 && c < 256) w += glyphs[c].Advance;
            }
            return w;
        }

        /// <summary>Draw at page coordinates. <paramref name="x"/> is the left edge, or the centre when <paramref name="centre"/> is set.</summary>
        public void Draw(string text, int x, int y, int rgb, bool centre, bool shadow)
        {
            if (string.IsNullOrEmpty(text) || !OsrsTitle.Placed) return;
            if (centre) x -= Width(text) / 2;
            if (shadow) DrawRun(text, x + 1, y + 1, 0);
            DrawRun(text, x, y, rgb);
        }

        /// <summary>Draw at screen pixels. <paramref name="y"/> is the baseline.</summary>
        public void DrawScreen(string text, int x, int y, int rgb, bool shadow)
        {
            if (string.IsNullOrEmpty(text)) return;
            int s = OsrsUi.Scale;
            if (shadow) DrawRun(text, x + s, y + s, 0, false);
            DrawRun(text, x, y, rgb, false);
        }

        private void DrawRun(string text, int x, int y, int rgb) => DrawRun(text, x, y, rgb, true);

        private void DrawRun(string text, int x, int y, int rgb, bool page)
        {
            GUI.color = new Color32((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), 255);
            int s = page ? 1 : OsrsUi.Scale;
            for (int i = 0; i < text.Length; i++)
            {
                int c = text[i];
                if (c < 0 || c > 255) continue;
                var g = glyphs[c];
                if (g.Tex != null)
                {
                    int top = y - (g.Oy + g.H) * s;
                    var rect = page ? OsrsTitle.PageRect(x, top, g.W, g.H) : new Rect(x, top, g.W * s, g.H * s);
                    GUI.DrawTexture(rect, g.Tex, ScaleMode.StretchToFill, true);
                }
                x += g.Advance * s;
            }
            GUI.color = Color.white;
        }

        private static void Flip(Color32[] pix, int w, int h)
        {
            var row = new Color32[w];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * w, b = (h - 1 - y) * w;
                System.Array.Copy(pix, a, row, 0, w);
                System.Array.Copy(pix, b, pix, a, w);
                System.Array.Copy(row, 0, pix, b, w);
            }
        }
    }
}
