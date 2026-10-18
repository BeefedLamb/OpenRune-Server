using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The client's login flames (calcFlamesPosition + the side-panel blend in doFlamesDrawing).
    /// Left panel is drawn at (0, 0), right panel at (637, 0), each 128x265.
    /// </summary>
    public sealed class OsrsFlames
    {
        public const int Width = 128;
        public const int Height = 265;
        private const int Rows = 256;
        private const int Size = Width * Rows;

        private readonly int[] buf = new int[Size];
        private readonly int[] smooth = new int[Size];
        private readonly int[] field = new int[Size];
        private readonly int[] fieldTmp = new int[Size];
        private readonly int[] wave = new int[Rows];
        private readonly int[] palette1 = new int[256];
        private readonly int[] palette2 = new int[256];
        private readonly int[] palette3 = new int[256];
        private readonly int[] palette = new int[256];
        private readonly byte[][] runes = new byte[12][];
        private readonly Color32[] leftPx = new Color32[Width * Height];
        private readonly Color32[] rightPx = new Color32[Width * Height];
        private readonly int[] leftBg = new int[Width * Height];
        private readonly int[] rightBg = new int[Width * Height];
        private readonly System.Random rng = new System.Random(1);
        private Texture2D leftTex;
        private Texture2D rightTex;
        private int scroll;
        private int tick;
        private int shiftRed;
        private int shiftGreen;
        private bool ready;

        public Texture2D Left => leftTex;
        public Texture2D Right => rightTex;

        public void Load(byte[][] runeAlpha, Color32[] page, int pageW)
        {
            for (int i = 0; i < 12; i++) runes[i] = runeAlpha[i];
            BuildPalette();
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    leftBg[y * Width + x] = Pack(page[y * pageW + x]);
                    rightBg[y * Width + x] = Pack(page[y * pageW + (637 + x)]);
                }
            Randomize(runes[0]);
            leftTex = NewTex();
            rightTex = NewTex();
            ready = true;
            for (int i = 0; i < 220; i++) Step(false);
            Step(true);
        }

        public void Step() => Step(true);

        private void Step(bool upload)
        {
            if (!ready) return;
            for (int j = 10; j < 117; j++)
                if (rng.NextDouble() * 100.0 < 50.0)
                    buf[j + ((Rows - 2) << 7)] = 255;
            for (int n = 0; n < 100; n++)
            {
                int a = (int)(rng.NextDouble() * 124.0) + 2;
                int b = (int)(rng.NextDouble() * 128.0) + 128;
                buf[a + (b << 7)] = 192;
            }
            for (int y = 1; y < Rows - 1; y++)
                for (int x = 1; x < 127; x++)
                {
                    int i = x + (y << 7);
                    smooth[i] = (buf[i - 1] + buf[i + 1] + buf[i - 128] + buf[i + 128]) / 4;
                }
            scroll += 128;
            if (scroll > field.Length)
            {
                scroll -= field.Length;
                Randomize(runes[rng.Next(12)]);
            }
            for (int y = 1; y < Rows - 1; y++)
                for (int x = 1; x < 127; x++)
                {
                    int i = x + (y << 7);
                    int v = smooth[i + 128] - field[(i + scroll) & (field.Length - 1)] / 5;
                    buf[i] = v > 0 ? v : 0;
                }
            System.Array.Copy(wave, 1, wave, 0, Rows - 1);
            wave[Rows - 1] = (int)(System.Math.Sin(tick / 14.0) * 16.0
                + System.Math.Sin(tick / 15.0) * 14.0
                + System.Math.Sin(tick / 16.0) * 12.0);
            if (shiftRed > 0) shiftRed -= 4;
            if (shiftGreen > 0) shiftGreen -= 4;
            if (shiftRed == 0 && shiftGreen == 0)
            {
                int roll = (int)(rng.NextDouble() * 2000.0);
                if (roll == 0) shiftRed = 1024;
                if (roll == 1) shiftGreen = 1024;
            }
            MixPalette();
            Blend(leftBg, leftPx, true);
            Blend(rightBg, rightPx, false);
            if (upload)
            {
                Upload(leftTex, leftPx);
                Upload(rightTex, rightPx);
            }
            tick++;
        }

        private void Blend(int[] bg, Color32[] dst, bool left)
        {
            System.Array.Copy(IntsToTop(bg), dst, dst.Length);
            int src = 0;
            int dsti = left ? 1152 : 1176;
            for (int row = 1; row < Rows - 1; row++)
            {
                int wobble = wave[row] * (Rows - row) / Rows;
                if (left)
                {
                    int skip = 22 + wobble;
                    if (skip < 0) skip = 0;
                    src += skip;
                    for (int x = skip; x < Width; x++)
                    {
                        int a = buf[src++];
                        if (a != 0) dsti = Mix(dst, dsti, a);
                        else dsti++;
                    }
                    dsti += skip;
                }
                else
                {
                    int inset = wobble;
                    int count = 103 - inset;
                    dsti += inset;
                    for (int x = 0; x < count; x++)
                    {
                        int a = buf[src++];
                        if (a != 0) dsti = Mix(dst, dsti, a);
                        else dsti++;
                    }
                    src += Width - count;
                    dsti += Width - count - inset;
                }
            }
        }

        private int Mix(Color32[] dst, int index, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            int fg = palette[alpha];
            int y = index / Width;
            int x = index % Width;
            if (y < 0 || y >= Height) return index + 1;
            var bg = dst[y * Width + x];
            int bgPacked = (bg.r << 16) | (bg.g << 8) | bg.b;
            int inv = 256 - alpha;
            int rb = (((fg & 0xFF00FF) * alpha + (bgPacked & 0xFF00FF) * inv) & ~0xFF) >> 8;
            int g = (((fg & 0x00FF00) * alpha + (bgPacked & 0x00FF00) * inv) & ~0xFF) >> 8;
            int mixed = (rb & 0xFF00FF) | (g & 0x00FF00);
            dst[y * Width + x] = new Color32((byte)((mixed >> 16) & 255), (byte)((mixed >> 8) & 255), (byte)(mixed & 255), 255);
            return index + 1;
        }

        private static Color32[] IntsToTop(int[] src)
        {
            var dst = new Color32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                int p = src[i];
                dst[i] = new Color32((byte)((p >> 16) & 255), (byte)((p >> 8) & 255), (byte)(p & 255), 255);
            }
            return dst;
        }

        private void Randomize(byte[] rune)
        {
            for (int i = 0; i < field.Length; i++) field[i] = 0;
            for (int n = 0; n < 5000; n++)
            {
                int i = (int)(rng.NextDouble() * 128.0 * Rows);
                if (i >= field.Length) i = field.Length - 1;
                field[i] = (int)(rng.NextDouble() * 256.0);
            }
            for (int pass = 0; pass < 20; pass++)
            {
                for (int y = 1; y < Rows - 1; y++)
                    for (int x = 1; x < 127; x++)
                    {
                        int i = x + (y << 7);
                        fieldTmp[i] = (field[i - 1] + field[i + 1] + field[i - 128] + field[i + 128]) / 4;
                    }
                System.Array.Copy(fieldTmp, field, field.Length);
                System.Array.Clear(fieldTmp, 0, fieldTmp.Length);
            }
            if (rune == null) return;
            for (int row = 0; row < 96; row++)
                for (int col = 0; col < 96; col++)
                    if (rune[row * 96 + col] != 0)
                    {
                        int i = (col + 16) + ((row + 16) << 7);
                        if (i >= 0 && i < field.Length) field[i] = 0;
                    }
        }

        private void MixPalette()
        {
            int[] from = palette1, to = palette1;
            int t = 0;
            if (shiftRed > 0) { from = palette1; to = palette2; t = shiftRed; }
            else if (shiftGreen > 0) { from = palette1; to = palette3; t = shiftGreen; }
            if (t > 768)
                for (int i = 0; i < 256; i++) palette[i] = Fade(from[i], to[i], 1024 - t);
            else if (t > 256)
                System.Array.Copy(to, palette, 256);
            else if (t > 0)
                for (int i = 0; i < 256; i++) palette[i] = Fade(to[i], from[i], 256 - t);
            else
                System.Array.Copy(palette1, palette, 256);
        }

        private static int Fade(int a, int b, int shift)
        {
            int inv = 256 - (shift & 255);
            int rb = (((a & 0xFF00FF) * (shift & 255) + (b & 0xFF00FF) * inv) & ~0xFF) >> 8;
            int g = (((a & 0x00FF00) * (shift & 255) + (b & 0x00FF00) * inv) & ~0xFF) >> 8;
            return (rb & 0xFF00FF) | (g & 0x00FF00);
        }

        private void BuildPalette()
        {
            for (int c = 0; c < 64; c++) palette1[c] = c * 0x40000;
            for (int c = 0; c < 64; c++) palette1[c + 64] = 0xFF0000 + 1024 * c;
            for (int c = 0; c < 64; c++) palette1[c + 128] = 0xFFFF00 + 4 * c;
            for (int c = 0; c < 64; c++) palette1[c + 192] = 0xFFFFFF;
            for (int c = 0; c < 64; c++) palette2[c] = c * 1024;
            for (int c = 0; c < 64; c++) palette2[c + 64] = 0x00FF00 + 4 * c;
            for (int c = 0; c < 64; c++) palette2[c + 128] = 0x00FFFF + 0x40000 * c;
            for (int c = 0; c < 64; c++) palette2[c + 192] = 0xFFFFFF;
            for (int c = 0; c < 64; c++) palette3[c] = c * 4;
            for (int c = 0; c < 64; c++) palette3[c + 64] = 255 + 0x40000 * c;
            for (int c = 0; c < 64; c++) palette3[c + 128] = 0xFF00FF + 1024 * c;
            for (int c = 0; c < 64; c++) palette3[c + 192] = 0xFFFFFF;
        }

        private static int Pack(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;

        private static Texture2D NewTex()
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            return tex;
        }

        private static void Upload(Texture2D tex, Color32[] topDown)
        {
            var pix = new Color32[topDown.Length];
            for (int y = 0; y < Height; y++)
                System.Array.Copy(topDown, y * Width, pix, (Height - 1 - y) * Width, Width);
            tex.SetPixels32(pix);
            tex.Apply(false);
        }
    }
}
