using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The fixed-mode game frame (interface 548) from the cache, on the 765x503 page.
    /// The rotating map stays in OsrsMinimap, on the frame's map circle.
    /// </summary>
    public class OsrsFrame : MonoBehaviour
    {
        public const int MapX = 545;
        public const int MapY = 4;

        public static bool Showing { get; private set; }

        private struct Piece
        {
            public int x, y, w, h, sprite, tiled;
        }

        private Piece[] pieces;
        private readonly Dictionary<int, Texture2D> sprites = new Dictionary<int, Texture2D>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject("OsrsFrame");
            DontDestroyOnLoad(go);
            go.AddComponent<OsrsFrame>();
        }

        private void OnGUI()
        {
            if (!OsrsTitleScreen.Released)
            {
                Showing = false;
                return;
            }
            var link = OsrsServerLink.Instance;
            if (link == null || !link.LoggedIn)
            {
                Showing = false;
                return;
            }
            Load();
            if (pieces == null) return;
            Showing = true;
            int depth = GUI.depth;
            GUI.depth = 10;
            DrawOutside();
            for (int i = 0; i < pieces.Length; i++)
                DrawPiece(pieces[i]);
            GUI.depth = depth;
        }

        private void DrawOutside()
        {
            int s = OsrsUi.Scale;
            int ox = OsrsUi.OriginX;
            int oy = OsrsUi.OriginY;
            int pw = OsrsUi.PageWidth * s;
            int ph = OsrsUi.PageHeight * s;
            GUI.color = Color.black;
            if (ox > 0)
                GUI.DrawTexture(new Rect(0, 0, ox, Screen.height), Texture2D.whiteTexture);
            int right = ox + pw;
            if (right < Screen.width)
                GUI.DrawTexture(new Rect(right, 0, Screen.width - right, Screen.height), Texture2D.whiteTexture);
            if (oy > 0)
                GUI.DrawTexture(new Rect(ox, 0, pw, oy), Texture2D.whiteTexture);
            int bottom = oy + ph;
            if (bottom < Screen.height)
                GUI.DrawTexture(new Rect(ox, bottom, pw, Screen.height - bottom), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private void DrawPiece(Piece p)
        {
            var tex = Sprite(p.sprite);
            if (tex == null) return;
            var group = OsrsUi.Page(p.x, p.y, p.w, p.h);
            GUI.BeginGroup(group);
            int s = OsrsUi.Scale;
            if (p.tiled != 0)
            {
                for (int ty = 0; ty < p.h; ty += tex.height)
                    for (int tx = 0; tx < p.w; tx += tex.width)
                        GUI.DrawTexture(new Rect(tx * s, ty * s, tex.width * s, tex.height * s), tex,
                            ScaleMode.StretchToFill, true);
            }
            else
            {
                GUI.DrawTexture(new Rect(0, 0, group.width, group.height), tex, ScaleMode.StretchToFill, true);
            }
            GUI.EndGroup();
        }

        private Texture2D Sprite(int id)
        {
            if (sprites.TryGetValue(id, out var tex)) return tex;
            string path = Path.Combine(Application.streamingAssetsPath, "ui", "if", "sprite", id + ".png");
            tex = LoadTex(path);
            sprites[id] = tex;
            return tex;
        }

        private void Load()
        {
            if (pieces != null) return;
            string path = Path.Combine(Application.streamingAssetsPath, "ui", "if", "548.txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            var list = new List<Piece>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                var p = line.Split('\t');
                if (p.Length < 6) continue;
                list.Add(new Piece
                {
                    x = int.Parse(p[0]),
                    y = int.Parse(p[1]),
                    w = int.Parse(p[2]),
                    h = int.Parse(p[3]),
                    sprite = int.Parse(p[4]),
                    tiled = int.Parse(p[5]),
                });
            }
            pieces = list.ToArray();
        }

        private static Texture2D LoadTex(string path)
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }
    }
}
