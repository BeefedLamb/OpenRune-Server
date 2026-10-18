using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Cache UI is drawn in whole-pixel steps so the bitmap fonts stay sharp.
    /// The 765x503 page is the unit: 1x, 2x, 3x, centred, with the rest of the window black.
    /// </summary>
    public static class OsrsUi
    {
        public const int PageWidth = 765;
        public const int PageHeight = 503;

        public static int Scale
        {
            get
            {
                int sx = Screen.width / PageWidth;
                int sy = Screen.height / PageHeight;
                int s = sx < sy ? sx : sy;
                return s < 1 ? 1 : s;
            }
        }

        public static int OriginX => (Screen.width - PageWidth * Scale) / 2;

        public static int OriginY => (Screen.height - PageHeight * Scale) / 2;

        public static Rect Page(int x, int y, int w, int h)
        {
            int s = Scale;
            return new Rect(OriginX + x * s, OriginY + y * s, w * s, h * s);
        }

        public static Rect Pixels(int x, int y, int w, int h)
        {
            int s = Scale;
            return new Rect(x * s, y * s, w * s, h * s);
        }
    }
}
