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
            public int x, y, w, h, sprite, tiled, frame;
        }

        private static readonly int[] FilterX = { 3, 65, 127, 189, 251, 313, 375 };
        private static readonly string[] FilterName = {
            "All", "Game", "Public", "Private", "Channel", "Clan", "Trade",
        };

        private Piece[] pieces;
        private readonly Dictionary<int, Texture2D> sprites = new Dictionary<int, Texture2D>();
        private OsrsFont small;

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
            {
                if (pieces[i].sprite == 1183) continue;
                DrawPiece(pieces[i]);
            }
            FindAnyObjectByType<OsrsMinimap>()?.DrawHudOrbs();
            DrawFilters();
            var interaction = FindAnyObjectByType<OsrsInteraction>();
            if (interaction != null) interaction.DrawGameMessages();
            DrawInventory();
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
            var tex = Sprite(p.sprite, p.frame);
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

        private void DrawFilters()
        {
            if (small == null) return;
            const int bar = 480;
            for (int i = 0; i < FilterX.Length; i++)
            {
                int cx = FilterX[i] + 28;
                if (i == 0)
                {
                    small.Draw(FilterName[i], cx, bar + 16, 0xFFFFFF, true, true);
                    continue;
                }
                small.Draw(FilterName[i], cx, bar + 10, 0xFFFFFF, true, true);
                small.Draw("On", cx, bar + 20, 0x00FF00, true, true);
            }
            small.Draw("Report", 437 + 40, bar + 16, 0xFFFFFF, true, true);
        }

        private void DrawInventory()
        {
            var link = OsrsServerLink.Instance;
            if (link == null) return;
            const int ox = 547, oy = 205, pitchX = 51, pitchY = 38;
            for (int i = 0; i < link.inventory.Count; i++)
            {
                var it = link.inventory[i];
                if (it.slot < 0 || it.slot >= 28 || it.id < 0) continue;
                int col = it.slot % 4, row = it.slot / 4;
                int x = ox + col * pitchX, y = oy + row * pitchY;
                OsrsItemIcons.Draw(OsrsUi.Page(x, y, 36, 32), it.id);
                if (it.count > 1 && small != null)
                {
                    int rgb = it.count >= 10000000 ? 0x00FF80 : it.count >= 100000 ? 0xFFFFFF : 0xFFFF00;
                    string text = it.count >= 10000000 ? (it.count / 1000000) + "M"
                        : it.count >= 100000 ? (it.count / 1000) + "K" : it.count.ToString();
                    small.Draw(text, x, y + 10, rgb, false, true);
                }
            }
        }

        private Texture2D Sprite(int id, int frame)
        {
            int key = id + frame * 100000;
            if (sprites.TryGetValue(key, out var tex)) return tex;
            string name = frame == 0 ? id + ".png" : id + "_" + frame + ".png";
            string path = Path.Combine(Application.streamingAssetsPath, "ui", "if", "sprite", name);
            tex = LoadTex(path);
            sprites[key] = tex;
            return tex;
        }

        private void Load()
        {
            if (pieces != null) return;
            string dir = Path.Combine(Application.streamingAssetsPath, "ui", "if");
            string path = Path.Combine(dir, "548.txt");
            if (!File.Exists(path)) return;
            small = OsrsFont.Load(Path.Combine(Application.streamingAssetsPath, "ui", "p11.fnt"));
            var list = new List<Piece>();
            ReadPieces(path, list);
            string chat = Path.Combine(dir, "chat.txt");
            if (File.Exists(chat)) ReadPieces(chat, list);
            pieces = list.ToArray();
        }

        private static void ReadPieces(string path, List<Piece> list)
        {
            var lines = File.ReadAllLines(path);
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
                    frame = p.Length > 7 ? int.Parse(p[7]) : 0,
                });
            }
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
