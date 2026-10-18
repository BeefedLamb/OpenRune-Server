using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The client's minimap: tile colours from the loaded ground, rotated with the camera,
    /// the cache frame, compass, dots and the four orbs. Clicks on it do not walk.
    /// </summary>
    public class OsrsMinimap : MonoBehaviour
    {
        private const float PixelsPerTile = 4f;

        private Texture2D frameTex;
        private Texture2D compassTex;
        private Texture2D arrowTex;
        private Texture2D[] dots;
        private Texture2D orbFrame;
        private Texture2D orbHp, orbPrayer, orbRun, orbSpec;
        private Texture2D iconHp, iconPrayer, iconWalk, iconRun, iconSpec;
        private Color32[] framePx;
        private Texture2D page;
        private Color32[] pagePx;
        private int pageW, pageH;
        private int builtFrame = -1;
        private OsrsFont numbers;
        private Rect frameRect, compassRect;
        private readonly Dictionary<int, Texture2D> sceneTex = new Dictionary<int, Texture2D>();
        private readonly Dictionary<int, Color32[]> scenePx = new Dictionary<int, Color32[]>();
        private readonly Dictionary<int, Texture2D> funcTex = new Dictionary<int, Texture2D>();
        private readonly List<FuncIcon> funcIcons = new List<FuncIcon>();
        private static readonly Color32 WallWhite = new Color32(238, 238, 238, 255);
        private static readonly Color32 DoorRed = new Color32(238, 0, 0, 255);

        private struct FuncIcon
        {
            public int area;
            public float x, y;
        }
        private Rect[] orbRects = new Rect[4];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject("OsrsMinimap");
            DontDestroyOnLoad(go);
            go.AddComponent<OsrsMinimap>();
        }

        /// <summary>True when a cursor-mode click lands on the map, the compass or an orb.</summary>
        public static bool Hit(Vector2 guiPos)
        {
            var map = FindAnyObjectByType<OsrsMinimap>();
            if (map == null || map.frameTex == null) return false;
            if (map.frameRect.Contains(guiPos) || map.compassRect.Contains(guiPos)) return true;
            for (int i = 0; i < map.orbRects.Length; i++)
                if (map.orbRects[i].Contains(guiPos)) return true;
            return false;
        }

        private void OnGUI()
        {
            if (!OsrsTitleScreen.Released) return;
            var link = OsrsServerLink.Instance;
            var world = OsrsWorldLoader.Instance;
            if (link == null || !link.LoggedIn || world == null) return;
            var body = link.transform;
            Load();
            if (frameTex == null) return;
            Layout();
            if (Event.current.type == EventType.Repaint && builtFrame != Time.frameCount)
            {
                builtFrame = Time.frameCount;
                Build(body, world, link.CurrentLevel);
            }
            int depth = GUI.depth;
            GUI.depth = -20;
            if (page != null)
                GUI.DrawTexture(frameRect, page, ScaleMode.StretchToFill, true);
            DrawDots(body, link);
            DrawFuncIcons();
            DrawCompass(body);
            DrawOrbs(link);
            GUI.depth = depth;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                && compassRect.Contains(Event.current.mousePosition))
            {
                body.GetComponent<FirstPersonController>()?.FaceNorth();
                Event.current.Use();
            }
        }

        private void Layout()
        {
            int s = OsrsUi.Scale;
            int fw = pageW * s, fh = pageH * s;
            int fx = Screen.width - fw - 4 * s;
            int fy = 2 * s;
            frameRect = new Rect(fx, fy, fw, fh);
            compassRect = new Rect(fx + fw / 2 - 25 * s, fy - 6 * s, 51 * s, 51 * s);
            int x = fx - 52 * s;
            int y = fy + 8 * s;
            for (int i = 0; i < 4; i++)
                orbRects[i] = new Rect(x, y + i * 36 * s, 57 * s, 34 * s);
        }

        private void Build(Transform body, OsrsWorldLoader world, int level)
        {
            float yaw = body.eulerAngles.y * Mathf.Deg2Rad;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float px = body.position.x, pz = body.position.z;
            float cx = (pageW - 1) * 0.5f, cy = (pageH - 1) * 0.5f;
            for (int y = 0; y < pageH; y++)
                for (int x = 0; x < pageW; x++)
                {
                    int i = y * pageW + x;
                    var fp = framePx[i];
                    if (fp.a > 16)
                    {
                        pagePx[i] = fp;
                        continue;
                    }
                    float dx = (x - cx) / PixelsPerTile;
                    float dy = (cy - y) / PixelsPerTile;
                    int wx = Mathf.FloorToInt(px + dx * cos + dy * sin);
                    int wz = Mathf.FloorToInt(pz - dx * sin + dy * cos);
                    pagePx[i] = world.TryTileColor(wx, wz, level, out var col) ? col : new Color32(0, 0, 0, 255);
                }
            DrawFeatures(body, world, level, cos, sin, px, pz);
            var up = new Color32[pagePx.Length];
            for (int y = 0; y < pageH; y++)
                System.Array.Copy(pagePx, y * pageW, up, (pageH - 1 - y) * pageW, pageW);
            page.SetPixels32(up);
            page.Apply(false);
        }

        private void DrawDots(Transform body, OsrsServerLink link)
        {
            float yaw = body.eulerAngles.y * Mathf.Deg2Rad;
            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            float px = body.position.x, pz = body.position.z;
            int level = link.CurrentLevel;
            foreach (var n in link.npcs)
            {
                if (n.level != level) continue;
                int dot = string.IsNullOrEmpty(n.playerName) ? 1 : 2;
                Mark(n.x + 0.5f, n.z + 0.5f, px, pz, cos, sin, dot);
            }
            foreach (var g in link.groundItems)
            {
                if (g.level != level) continue;
                Mark(g.x + 0.5f, g.z + 0.5f, px, pz, cos, sin, 0);
            }
            if (arrowTex != null)
            {
                int s = OsrsUi.Scale;
                float ax = frameRect.x + frameRect.width * 0.5f - arrowTex.width * s * 0.5f;
                float ay = frameRect.y + frameRect.height * 0.5f - arrowTex.height * s * 0.5f;
                GUI.DrawTexture(new Rect(ax, ay, arrowTex.width * s, arrowTex.height * s), arrowTex, ScaleMode.StretchToFill, true);
            }
        }

        private void Mark(float wx, float wz, float px, float pz, float cos, float sin, int dot)
        {
            if (dots == null || dot < 0 || dot >= dots.Length || dots[dot] == null) return;
            float dx = wx - px, dz = wz - pz;
            float dxp = dx * cos - dz * sin;
            float dyp = dx * sin + dz * cos;
            int s = OsrsUi.Scale;
            float x = frameRect.x + (pageW - 1) * 0.5f * s + dxp * PixelsPerTile * s;
            float y = frameRect.y + (pageH - 1) * 0.5f * s - dyp * PixelsPerTile * s;
            int ix = Mathf.RoundToInt((x - frameRect.x) / s), iy = Mathf.RoundToInt((y - frameRect.y) / s);
            if (ix < 0 || iy < 0 || ix >= pageW || iy >= pageH) return;
            if (framePx[iy * pageW + ix].a > 16) return;
            var t = dots[dot];
            GUI.DrawTexture(new Rect(x - t.width * s * 0.5f, y - t.height * s * 0.5f, t.width * s, t.height * s), t, ScaleMode.StretchToFill, true);
        }

        private void DrawCompass(Transform body)
        {
            if (compassTex == null) return;
            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(-body.eulerAngles.y, compassRect.center);
            GUI.DrawTexture(compassRect, compassTex, ScaleMode.StretchToFill, true);
            GUI.matrix = saved;
        }

        private void DrawOrbs(OsrsServerLink link)
        {
            int prayer = link.Prayer, maxPrayer = link.MaxPrayer;
            if (prayer < 0)
                foreach (var s in link.stats)
                    if (s.name != null && s.name.Equals("Prayer", System.StringComparison.OrdinalIgnoreCase))
                    {
                        prayer = s.level;
                        maxPrayer = s.baseLevel;
                    }
            Orb(orbRects[0], orbHp, iconHp, link.MaxHp > 0 ? link.Hp / (float)link.MaxHp : 0f, link.Hp);
            Orb(orbRects[1], orbPrayer, iconPrayer, maxPrayer > 0 ? prayer / (float)maxPrayer : 0f, prayer);
            float run = link.RunEnergy < 0 ? -1f : link.RunEnergy / 10000f;
            Orb(orbRects[2], orbRun, link.RunEnergy > 0 ? iconRun : iconWalk, run < 0f ? 1f : run, run < 0f ? -1 : link.RunEnergy / 100);
            Orb(orbRects[3], orbSpec, iconSpec, link.SpecEnergy < 0 ? 0f : link.SpecEnergy / 1000f, link.SpecEnergy < 0 ? -1 : link.SpecEnergy / 10);
        }

        private void Orb(Rect well, Texture2D fill, Texture2D icon, float amount, int number)
        {
            if (orbFrame == null) return;
            int s = OsrsUi.Scale;
            var flipped = new Rect(well.x + well.width, well.y, -well.width, well.height);
            GUI.DrawTexture(flipped, orbFrame, ScaleMode.StretchToFill, true);
            amount = Mathf.Clamp01(amount);
            var circle = new Rect(well.x + 2 * s, well.y + 4 * s, 26 * s, 26 * s);
            if (fill != null && amount > 0f)
            {
                float shown = 26f * s * amount;
                GUI.BeginGroup(new Rect(circle.x, circle.y + (26 * s - shown), 26 * s, shown));
                GUI.DrawTexture(new Rect(0f, -(26 * s - shown), 26 * s, 26 * s), fill, ScaleMode.StretchToFill, true);
                GUI.EndGroup();
            }
            if (icon != null)
                GUI.DrawTexture(circle, icon, ScaleMode.StretchToFill, true);
            if (number >= 0 && numbers != null)
            {
                int rgb = number <= 0 ? 0xFF0000 : number < 20 ? 0xFFFF00 : 0x00FF00;
                numbers.DrawScreen(number.ToString(), Mathf.RoundToInt(well.x + 30 * s), Mathf.RoundToInt(well.y + 22 * s), rgb, true);
            }
        }

        private void DrawFeatures(Transform body, OsrsWorldLoader world, int level, float cos, float sin, float px, float pz)
        {
            funcIcons.Clear();
            int s = OsrsUi.Scale;
            var data = world.Data;
            if (data == null) return;
            world.VisitPlacements(p =>
            {
                if (p.level != level) return;
                int ddx = p.x - (int)px, ddz = p.z - (int)pz;
                if (ddx > 28 || ddx < -28 || ddz > 28 || ddz < -28) return;
                if (!data.locs.TryGetValue(p.locId, out var def)) return;
                int shape = p.modelType;
                if (shape == 0 || shape == 1 || shape == 2 || shape == 3 || shape == 9)
                    DrawWall(p.x, p.z, shape, p.rotation, def.wallOrDoor != 0 ? DoorRed : WallWhite, cos, sin, px, pz);
                if (def.mapScene >= 0)
                    StampScene(def.mapScene, p.x + p.sizeX * 0.5f, p.z + p.sizeZ * 0.5f, cos, sin, px, pz);
                if (def.mapArea >= 0 && Project(p.x + p.sizeX * 0.5f, p.z + p.sizeZ * 0.5f, px, pz, cos, sin, out float sx, out float sy))
                {
                    int ix = Mathf.RoundToInt(sx), iy = Mathf.RoundToInt(sy);
                    if (ix >= 0 && iy >= 0 && ix < pageW && iy < pageH && framePx[iy * pageW + ix].a <= 16)
                        funcIcons.Add(new FuncIcon { area = def.mapArea, x = frameRect.x + sx * s, y = frameRect.y + sy * s });
                }
            });
        }

        private void DrawWall(int tx, int tz, int shape, int rot, Color32 col, float cos, float sin, float px, float pz)
        {
            if (shape == 0 || shape == 2)
            {
                if (rot == 0) WallLine(tx, tz, tx, tz + 1, col, cos, sin, px, pz);
                else if (rot == 1) WallLine(tx, tz + 1, tx + 1, tz + 1, col, cos, sin, px, pz);
                else if (rot == 2) WallLine(tx + 1, tz, tx + 1, tz + 1, col, cos, sin, px, pz);
                else WallLine(tx, tz, tx + 1, tz, col, cos, sin, px, pz);
            }
            else if (shape == 1 || shape == 9)
            {
                bool slash = shape == 9 ? (rot & 1) == 0 : (rot & 1) == 1;
                if (slash) WallLine(tx, tz, tx + 1, tz + 1, col, cos, sin, px, pz);
                else WallLine(tx, tz + 1, tx + 1, tz, col, cos, sin, px, pz);
            }
            else
            {
                float cx = tx + (rot == 1 || rot == 2 ? 0.85f : 0.15f);
                float cz = tz + (rot == 0 || rot == 1 ? 0.85f : 0.15f);
                if (Project(cx, cz, px, pz, cos, sin, out float sx, out float sy))
                    Plot(Mathf.RoundToInt(sx), Mathf.RoundToInt(sy), col);
            }
        }

        private void WallLine(float x0, float z0, float x1, float z1, Color32 col, float cos, float sin, float px, float pz)
        {
            if (!Project(x0, z0, px, pz, cos, sin, out float ax, out float ay)) return;
            if (!Project(x1, z1, px, pz, cos, sin, out float bx, out float by)) return;
            int x = Mathf.RoundToInt(ax), y = Mathf.RoundToInt(ay);
            int ex = Mathf.RoundToInt(bx), ey = Mathf.RoundToInt(by);
            int dx = Mathf.Abs(ex - x), dy = Mathf.Abs(ey - y);
            int sx = x < ex ? 1 : -1, sy = y < ey ? 1 : -1;
            int err = dx - dy;
            for (int n = 0; n < 12; n++)
            {
                Plot(x, y, col);
                if (x == ex && y == ey) break;
                int e2 = err * 2;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx) { err += dx; y += sy; }
            }
        }

        private void Plot(int x, int y, Color32 col)
        {
            if (x < 0 || y < 0 || x >= pageW || y >= pageH) return;
            if (framePx[y * pageW + x].a > 16) return;
            pagePx[y * pageW + x] = col;
        }

        private void StampScene(int id, float wx, float wz, float cos, float sin, float px, float pz)
        {
            var pix = ScenePixels(id);
            if (pix.px == null) return;
            int sw = pix.w, sh = pix.h;
            var src = pix.px;
            for (int sy = 0; sy < sh; sy++)
                for (int sx = 0; sx < sw; sx++)
                {
                    var c = src[sy * sw + sx];
                    if (c.a < 16) continue;
                    float lx = (sx + 0.5f - sw * 0.5f) / PixelsPerTile;
                    float lz = (sh * 0.5f - (sy + 0.5f)) / PixelsPerTile;
                    if (!Project(wx + lx, wz + lz, px, pz, cos, sin, out float ox, out float oy)) continue;
                    Plot(Mathf.RoundToInt(ox), Mathf.RoundToInt(oy), c);
                }
        }

        private bool Project(float wx, float wz, float px, float pz, float cos, float sin, out float x, out float y)
        {
            float dxw = wx - px, dzw = wz - pz;
            float dx = dxw * cos - dzw * sin;
            float dy = dxw * sin + dzw * cos;
            x = (pageW - 1) * 0.5f + dx * PixelsPerTile;
            y = (pageH - 1) * 0.5f - dy * PixelsPerTile;
            return x >= -8f && y >= -8f && x < pageW + 8f && y < pageH + 8f;
        }

        private void DrawFuncIcons()
        {
            int s = OsrsUi.Scale;
            for (int i = 0; i < funcIcons.Count; i++)
            {
                var icon = funcIcons[i];
                var tex = FuncSprite(icon.area);
                if (tex == null) continue;
                float w = tex.width * s, h = tex.height * s;
                GUI.DrawTexture(new Rect(icon.x - w * 0.5f, icon.y - h * 0.5f, w, h), tex, ScaleMode.StretchToFill, true);
            }
        }

        private (int w, int h, Color32[] px) ScenePixels(int id)
        {
            if (scenePx.TryGetValue(id, out var pix))
                return pix == null ? default : (sceneTex[id].width, sceneTex[id].height, pix);
            var tex = LoadHud(Path.Combine("mapscene", id + ".png"));
            sceneTex[id] = tex;
            if (tex == null)
            {
                scenePx[id] = null;
                return default;
            }
            var raw = tex.GetPixels32();
            var top = new Color32[raw.Length];
            for (int y = 0; y < tex.height; y++)
                System.Array.Copy(raw, y * tex.width, top, (tex.height - 1 - y) * tex.width, tex.width);
            scenePx[id] = top;
            return (tex.width, tex.height, top);
        }

        private Texture2D FuncSprite(int area)
        {
            if (funcTex.TryGetValue(area, out var tex)) return tex;
            tex = LoadHud(Path.Combine("mapfunc", area + ".png"));
            funcTex[area] = tex;
            return tex;
        }

        private static Texture2D LoadHud(string relative)
        {
            return LoadTex(Path.Combine(Application.streamingAssetsPath, "ui", "hud", relative));
        }

        private void Load()
        {
            if (frameTex != null) return;
            string dir = Path.Combine(Application.streamingAssetsPath, "ui", "hud");
            frameTex = LoadTex(Path.Combine(dir, "mapback.png"));
            compassTex = LoadTex(Path.Combine(dir, "compass.png"));
            KeyCompass(compassTex);
            arrowTex = LoadTex(Path.Combine(dir, "maparrow.png"));
            orbFrame = LoadTex(Path.Combine(dir, "orb_frame.png"));
            orbHp = LoadTex(Path.Combine(dir, "orb_hp.png"));
            orbPrayer = LoadTex(Path.Combine(dir, "orb_prayer.png"));
            orbRun = LoadTex(Path.Combine(dir, "orb_run.png"));
            orbSpec = LoadTex(Path.Combine(dir, "orb_spec.png"));
            iconHp = LoadTex(Path.Combine(dir, "icon_hp.png"));
            iconPrayer = LoadTex(Path.Combine(dir, "icon_prayer.png"));
            iconWalk = LoadTex(Path.Combine(dir, "icon_walk.png"));
            iconRun = LoadTex(Path.Combine(dir, "icon_run.png"));
            iconSpec = LoadTex(Path.Combine(dir, "icon_spec.png"));
            dots = new Texture2D[3];
            for (int i = 0; i < 3; i++) dots[i] = LoadTex(Path.Combine(dir, "mapdots" + i + ".png"));
            numbers = OsrsFont.Load(Path.Combine(Application.streamingAssetsPath, "ui", "p11.fnt"));
            if (numbers == null) numbers = OsrsTitle.Bold;
            if (frameTex == null) return;
            pageW = frameTex.width;
            pageH = frameTex.height;
            var raw = frameTex.GetPixels32();
            framePx = new Color32[raw.Length];
            for (int y = 0; y < pageH; y++)
                System.Array.Copy(raw, y * pageW, framePx, (pageH - 1 - y) * pageW, pageW);
            page = new Texture2D(pageW, pageH, TextureFormat.RGBA32, false);
            page.filterMode = FilterMode.Point;
            pagePx = new Color32[raw.Length];
        }

        private static void KeyCompass(Texture2D tex)
        {
            if (tex == null) return;
            var pix = tex.GetPixels32();
            for (int i = 0; i < pix.Length; i++)
            {
                int mx = pix[i].r > pix[i].g ? pix[i].r : pix[i].g;
                if (pix[i].b > mx) mx = pix[i].b;
                int mn = pix[i].r < pix[i].g ? pix[i].r : pix[i].g;
                if (pix[i].b < mn) mn = pix[i].b;
                if (mx < 130 && mx - mn < 40) pix[i].a = 0;
            }
            tex.SetPixels32(pix);
            tex.Apply(false);
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
