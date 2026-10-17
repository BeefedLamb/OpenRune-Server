using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// The exported world on disk (StreamingAssets/world): the shared model library, object
    /// variants, and an index of every map square. All reading happens off the main thread.
    /// </summary>
    public class WorldData
    {
        public struct SquareEntry
        {
            public int pack;
            public uint offset;
            public uint length;
        }

        public class Model
        {
            public short[] positions; // x, y, z per vertex, 1/128 tile units
            public ushort[] faces;    // a, b, c per face
            public ushort[] colors;   // packed HSL per face
            public short[] textures;  // texture id per face, -1 = none
        }

        public class Variant
        {
            public int locId;
            public int modelType;
            public bool mirror;
            public int[] models;
            public ushort[] recolorFrom, recolorTo;
            public ushort[] retextureFrom, retextureTo;
            public short scaleX, scaleY, scaleZ;
        }

        public class LocDef
        {
            public int id;
            public string name;
            public int sizeX, sizeY;
            public string[] ops = new string[5];
            public bool rotated;
            public int solid = 2;
            public int offX, offY, offZ;
            public int resolvedId;

            public bool HasOps
            {
                get { foreach (var op in ops) if (!string.IsNullOrEmpty(op)) return true; return false; }
            }
        }

        public class NpcDef
        {
            public int id;
            public string name;
            public int size, combatLevel, scaleXZ, scaleY;
            public int[] models;
            public ushort[] recolorFrom, recolorTo, retextureFrom, retextureTo;
            public string[] ops = new string[5];
            /// <summary>Base animation set from the cache (-1 = none).</summary>
            public int standSeq = -1, walkSeq = -1, runSeq = -1;
        }

        /// <summary>An identkit (player body part): its models and recolours.</summary>
        public class KitDef
        {
            public int id, bodyPart;
            public int[] models;
            public ushort[] recolorFrom, recolorTo, retextureFrom, retextureTo;
        }

        /// <summary>The worn (body) models of an equipable item, per body type, and its recolours.</summary>
        public class WornItemDef
        {
            public int id;
            public int[] male, female;
            public int maleOffset, femaleOffset;
            public ushort[] recolorFrom, recolorTo, retextureFrom, retextureTo;
        }

        /// <summary>A variant resolved to flat-shaded triangles: 9 floats and 3 colours per face.</summary>
        public class VariantMesh
        {
            public float[] positions;
            public Color32[] colors;
            public int faceCount;
            /// <summary>Per corner: averaged OSRS vertex normal (length ~256) and the unlit colour, for re-lighting at a yaw.</summary>
            public float[] normals;
            public ushort[] hsls;
        }

        public readonly Dictionary<(int, int), SquareEntry> squares = new Dictionary<(int, int), SquareEntry>();
        public readonly Dictionary<int, Model> models = new Dictionary<int, Model>();
        public Variant[] variants = Array.Empty<Variant>();
        public readonly Dictionary<int, ushort> textureColors = new Dictionary<int, ushort>();
        public readonly Dictionary<int, LocDef> locs = new Dictionary<int, LocDef>();
        public readonly Dictionary<(int, int, bool), int> variantIndex = new Dictionary<(int, int, bool), int>();
        public readonly Dictionary<int, NpcDef> npcs = new Dictionary<int, NpcDef>();
        public readonly Dictionary<int, KitDef> kits = new Dictionary<int, KitDef>();
        public readonly Dictionary<int, WornItemDef> wornItems = new Dictionary<int, WornItemDef>();
        /// <summary>True when player_kits.txt / player_models.bin.gz were exported (players can be drawn).</summary>
        public bool HasPlayerParts => kits.Count > 0;
        /// <summary>Sequences, frames and vertex labels (null until loaded; check Loaded).</summary>
        public OsrsAnimData anim;
        private readonly ConcurrentDictionary<int, VariantMesh> variantMeshes = new ConcurrentDictionary<int, VariantMesh>();
        private readonly ConcurrentDictionary<int, VariantMesh> npcMeshes = new ConcurrentDictionary<int, VariantMesh>();
        private readonly ConcurrentDictionary<int, float[]> vertexNormals = new ConcurrentDictionary<int, float[]>();
        private readonly string root;

        public WorldData(string root)
        {
            this.root = root;
        }

        public string Root => root;

        public void Load()
        {
            LoadIndex();
            LoadVariants();
            LoadModels(Path.Combine(root, "models.bin.gz"));
            if (File.Exists(Path.Combine(root, "models_extra.bin.gz")))
                LoadModels(Path.Combine(root, "models_extra.bin.gz"));
            if (File.Exists(Path.Combine(root, "npc_models.bin.gz")))
                LoadModels(Path.Combine(root, "npc_models.bin.gz"));
            if (File.Exists(Path.Combine(root, "player_models.bin.gz")))
                LoadModels(Path.Combine(root, "player_models.bin.gz"));
            for (int i = 0; i < variants.Length; i++)
                variantIndex[(variants[i].locId, variants[i].modelType, variants[i].mirror)] = i;
            LoadLocs();
            LoadNpcs();
            LoadPlayerParts();
            anim = OsrsAnimData.Load(root);
        }

        /// <summary>player_kits.txt (export_player_models.py) and player_items.txt (exportPlayerParts).</summary>
        private void LoadPlayerParts()
        {
            string kitPath = Path.Combine(root, "player_kits.txt");
            if (File.Exists(kitPath))
                foreach (var line in File.ReadLines(kitPath))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split('\t');
                    if (p.Length < 6) continue;
                    var k = new KitDef { id = int.Parse(p[0]), bodyPart = int.Parse(p[1]), models = ParseInts(p[3]) };
                    ParsePairs(p[4], out k.recolorFrom, out k.recolorTo);
                    ParsePairs(p[5], out k.retextureFrom, out k.retextureTo);
                    kits[k.id] = k;
                }
            string itemPath = Path.Combine(root, "player_items.txt");
            if (File.Exists(itemPath))
                foreach (var line in File.ReadLines(itemPath))
                {
                    var p = line.Split('\t');
                    if (p.Length < 8 || p[0] != "I") continue;
                    var w = new WornItemDef
                    {
                        id = int.Parse(p[1]), male = ParseInts(p[2]), female = ParseInts(p[3]),
                        maleOffset = int.Parse(p[4]), femaleOffset = int.Parse(p[5]),
                    };
                    ParsePairs(p[6], out w.recolorFrom, out w.recolorTo);
                    ParsePairs(p[7], out w.retextureFrom, out w.retextureTo);
                    wornItems[w.id] = w;
                }
        }

        private void LoadLocs()
        {
            string path = Path.Combine(root, "locs.txt");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split('\t');
                if (p.Length < 5) continue;
                var d = new LocDef { id = int.Parse(p[0]), name = p[1], sizeX = int.Parse(p[2]), sizeY = int.Parse(p[3]) };
                var ops = p[4].Split('|');
                for (int i = 0; i < 5 && i < ops.Length; i++) d.ops[i] = ops[i];
                d.resolvedId = d.id;
                if (p.Length >= 11)
                {
                    d.rotated = p[5] == "1";
                    d.solid = int.Parse(p[6]);
                    d.offX = int.Parse(p[7]); d.offY = int.Parse(p[8]); d.offZ = int.Parse(p[9]);
                    d.resolvedId = int.Parse(p[10]);
                }
                locs[d.id] = d;
            }
        }

        private void LoadNpcs()
        {
            string path = Path.Combine(root, "npcs.txt");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split('\t');
                if (p.Length < 10) continue;
                var d = new NpcDef
                {
                    id = int.Parse(p[0]), name = p[1], size = int.Parse(p[2]), combatLevel = int.Parse(p[3]),
                    scaleXZ = int.Parse(p[4]), scaleY = int.Parse(p[5]),
                    models = ParseInts(p[6]),
                };
                ParsePairs(p[7], out d.recolorFrom, out d.recolorTo);
                ParsePairs(p[8], out d.retextureFrom, out d.retextureTo);
                var ops = p[9].Split('|');
                for (int i = 0; i < 5 && i < ops.Length; i++) d.ops[i] = ops[i]; if (p.Length >= 13) { int.TryParse(p[10], out d.standSeq); int.TryParse(p[11], out d.walkSeq); int.TryParse(p[12], out d.runSeq); }
                npcs[d.id] = d;
            }
        }

        private static int[] ParseInts(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<int>();
            var parts = s.Split(',');
            var o = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) o[i] = int.Parse(parts[i]);
            return o;
        }

        private static void ParsePairs(string s, out ushort[] a, out ushort[] b)
        {
            if (string.IsNullOrEmpty(s)) { a = Array.Empty<ushort>(); b = Array.Empty<ushort>(); return; }
            var parts = s.Split(',');
            a = new ushort[parts.Length]; b = new ushort[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var kv = parts[i].Split(':');
                a[i] = ushort.Parse(kv[0]); b[i] = ushort.Parse(kv[1]);
            }
        }

        private void LoadIndex()
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "index.bin"));
            if (bytes[0] != 'O' || bytes[1] != 'S' || bytes[2] != 'W' || bytes[3] != '1')
                throw new InvalidDataException("index.bin is not an OSW1 file");
            int count = BitConverter.ToInt32(bytes, 4);
            int p = 8;
            for (int i = 0; i < count; i++)
            {
                int rx = bytes[p], ry = bytes[p + 1], pack = bytes[p + 2];
                uint off = BitConverter.ToUInt32(bytes, p + 3);
                uint len = BitConverter.ToUInt32(bytes, p + 7);
                p += 11;
                squares[(rx, ry)] = new SquareEntry { pack = pack, offset = off, length = len };
            }
        }

        private static byte[] ReadGzip(string path)
        {
            using (var file = File.OpenRead(path))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            using (var mem = new MemoryStream())
            {
                gz.CopyTo(mem);
                return mem.ToArray();
            }
        }

        private void LoadVariants()
        {
            var d = ReadGzip(Path.Combine(root, "variants.bin.gz"));
            int p = 0;
            int nt = BitConverter.ToUInt16(d, p); p += 2;
            for (int i = 0; i < nt; i++)
            {
                textureColors[BitConverter.ToUInt16(d, p)] = BitConverter.ToUInt16(d, p + 2);
                p += 4;
            }
            int nv = BitConverter.ToInt32(d, p); p += 4;
            variants = new Variant[nv];
            for (int i = 0; i < nv; i++)
            {
                var v = new Variant
                {
                    locId = BitConverter.ToInt32(d, p),
                    modelType = d[p + 4],
                    mirror = d[p + 5] != 0,
                };
                int nm = d[p + 6];
                p += 7;
                v.models = new int[nm];
                for (int k = 0; k < nm; k++) { v.models[k] = BitConverter.ToInt32(d, p); p += 4; }
                int nr = d[p++];
                v.recolorFrom = new ushort[nr]; v.recolorTo = new ushort[nr];
                for (int k = 0; k < nr; k++) { v.recolorFrom[k] = BitConverter.ToUInt16(d, p); v.recolorTo[k] = BitConverter.ToUInt16(d, p + 2); p += 4; }
                int nx = d[p++];
                v.retextureFrom = new ushort[nx]; v.retextureTo = new ushort[nx];
                for (int k = 0; k < nx; k++) { v.retextureFrom[k] = BitConverter.ToUInt16(d, p); v.retextureTo[k] = BitConverter.ToUInt16(d, p + 2); p += 4; }
                v.scaleX = BitConverter.ToInt16(d, p); v.scaleY = BitConverter.ToInt16(d, p + 2); v.scaleZ = BitConverter.ToInt16(d, p + 4);
                p += 6;
                variants[i] = v;
            }
        }

        private void LoadModels(string path)
        {
            var d = ReadGzip(path);
            int p = 0;
            int count = BitConverter.ToInt32(d, p); p += 4;
            for (int i = 0; i < count; i++)
            {
                int id = BitConverter.ToInt32(d, p);
                int nv = BitConverter.ToUInt16(d, p + 4);
                int nf = BitConverter.ToInt32(d, p + 6);
                p += 10;
                var m = new Model
                {
                    positions = new short[nv * 3],
                    faces = new ushort[nf * 3],
                    colors = new ushort[nf],
                    textures = new short[nf],
                };
                Buffer.BlockCopy(d, p, m.positions, 0, nv * 6);
                p += nv * 6;
                for (int f = 0; f < nf; f++)
                {
                    m.faces[f * 3] = BitConverter.ToUInt16(d, p);
                    m.faces[f * 3 + 1] = BitConverter.ToUInt16(d, p + 2);
                    m.faces[f * 3 + 2] = BitConverter.ToUInt16(d, p + 4);
                    m.colors[f] = BitConverter.ToUInt16(d, p + 6);
                    m.textures[f] = BitConverter.ToInt16(d, p + 8);
                    p += 10;
                }
                models[id] = m;
            }
        }

        public byte[] ReadSquare(int rx, int ry)
        {
            if (!squares.TryGetValue((rx, ry), out var e))
                return null;
            var buf = new byte[e.length];
            using (var file = File.OpenRead(Path.Combine(root, $"pack_{e.pack}.bin")))
            {
                file.Seek(e.offset, SeekOrigin.Begin);
                int read = 0;
                while (read < buf.Length)
                {
                    int n = file.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            using (var gz = new GZipStream(new MemoryStream(buf), CompressionMode.Decompress))
            using (var mem = new MemoryStream())
            {
                gz.CopyTo(mem);
                return mem.ToArray();
            }
        }

        public VariantMesh GetVariantMesh(int id)
        {
            return variantMeshes.GetOrAdd(id, BuildVariantMesh);
        }

        private VariantMesh BuildVariantMesh(int id)
        {
            var v = variants[id];
            return BuildMesh(v.models, v.mirror, v.scaleX / 128f, v.scaleY / 128f, v.scaleZ / 128f,
                v.recolorFrom, v.recolorTo, v.retextureFrom, v.retextureTo, false);
        }

        public VariantMesh GetNpcMesh(int npcId)
        {
            return npcMeshes.GetOrAdd(npcId, BuildNpcMesh);
        }

        private VariantMesh BuildNpcMesh(int npcId)
        {
            if (!npcs.TryGetValue(npcId, out var n))
                return new VariantMesh { positions = Array.Empty<float>(), colors = Array.Empty<Color32>() };
            float s = n.scaleXZ / 128f;
            return BuildMesh(n.models, false, s, n.scaleY / 128f, s, n.recolorFrom, n.recolorTo, n.retextureFrom, n.retextureTo, true);
        }

        private VariantMesh BuildMesh(int[] modelIds, bool mirror, float sx, float sy, float sz,
            ushort[] recolorFrom, ushort[] recolorTo, ushort[] retextureFrom, ushort[] retextureTo, bool actorLight)
        {
            int faces = 0;
            foreach (var mid in modelIds)
                if (models.TryGetValue(mid, out var m)) faces += m.colors.Length;
            var mesh = new VariantMesh { positions = new float[faces * 9], colors = new Color32[faces * 3], faceCount = faces };
            if (!actorLight) { mesh.normals = new float[faces * 9]; mesh.hsls = new ushort[faces * 3]; }
            int f = 0;
            foreach (var mid in modelIds)
            {
                if (!models.TryGetValue(mid, out var m)) continue;
                var vn = VertexNormals(mid, m);
                int nf = m.colors.Length;
                for (int i = 0; i < nf; i++)
                {
                    int a = m.faces[i * 3], b = m.faces[i * 3 + 1], c = m.faces[i * 3 + 2];
                    if (mirror) { int t = b; b = c; c = t; }
                    ushort hsl = m.colors[i];
                    for (int k = 0; k < recolorFrom.Length; k++)
                        if (hsl == recolorFrom[k]) { hsl = recolorTo[k]; break; }
                    int tex = m.textures[i];
                    if (tex != -1)
                        for (int k = 0; k < retextureFrom.Length; k++)
                            if (tex == retextureFrom[k]) { tex = retextureTo[k]; break; }
                    ushort baseHsl = (tex != -1 && textureColors.TryGetValue(tex, out var th)) ? th : hsl;
                    int o = f * 9;
                    WriteVertex(mesh.positions, o, m.positions, a, mirror, sx, sy, sz);
                    WriteVertex(mesh.positions, o + 3, m.positions, b, mirror, sx, sy, sz);
                    WriteVertex(mesh.positions, o + 6, m.positions, c, mirror, sx, sy, sz);
                    int ca = a, cb = b, cc = c;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = k == 0 ? ca : k == 1 ? cb : cc;
                        float nx = vn[vi * 3], ny = vn[vi * 3 + 1], nz = mirror ? -vn[vi * 3 + 2] : vn[vi * 3 + 2];
                        mesh.colors[f * 3 + k] = actorLight ? OsrsLight.Actor(baseHsl, nx, ny, nz) : OsrsLight.Loc(baseHsl, nx, ny, nz, 0f);
                        if (!actorLight)
                        {
                            mesh.normals[o + k * 3] = nx; mesh.normals[o + k * 3 + 1] = ny; mesh.normals[o + k * 3 + 2] = nz;
                            mesh.hsls[f * 3 + k] = baseHsl;
                        }
                    }
                    f++;
                }
            }
            return mesh;
        }

        // OSRS body colour recolours (client PlayerComposition tables): the default colour in each
        // part's models is replaced by entry [chosen colour] of its row. Rows: hair, torso, legs,
        // feet, skin. Skin only lists the 8 classic tones; other skin choices keep the default.
        private static readonly ushort[] BodyColourFind = { 6798, 8741, 25238, 4626, 4550 };
        private static readonly short[][] BodyColourTable =
        {
            new short[] { 6798, 107, 10283, 16, 4797, 7744, 5799, 4634, -31839, 22433, 2983, -11343, 8, 5281, 10438, 3650, -27322, -21845, 200, 571, 908, 21830, 28946, -15701, -14010 },
            new short[] { 8741, 12, -1506, -22374, 7735, 8404, 1701, -27106, 24094, 10153, -8915, 4783, 1341, 16578, -30533, 25239, 8, 5281, 10438, 3650, -27322, -21845, 200, 571, 908, 21830, 28946, -15701, -14010 },
            new short[] { 25238, 8742, 12, -1506, -22374, 7735, 8404, 1701, -27106, 24094, 10153, -8915, 4783, 1341, 16578, -30533, 8, 5281, 10438, 3650, -27322, -21845, 200, 571, 908, 21830, 28946, -15701, -14010 },
            new short[] { 4626, 11146, 6439, 12, 4758, 10270 },
            new short[] { 4550, 4537, 5681, 5673, 5790, 6806, 8076, 4574 },
        };

        /// <summary>
        /// Builds a player's body from a resolved look: [bodyType, 5 colours, 12 slots] where a slot
        /// is -1 (nothing), 256 + identkit id or 512 + item id (the server's PLOOK line). Each part
        /// gets its own recolours/retextures (and item Y offset), then the body colours are applied
        /// to the whole model like the OSRS client does. Static pose (no animation).
        /// </summary>
        public VariantMesh BuildPlayerMesh(int[] look)
        {
            var parts = new List<(int[] models, ushort[] rf, ushort[] rt, ushort[] xf, ushort[] xt, int offset)>();
            bool female = look[0] == 1;
            for (int s = 0; s < 12 && 6 + s < look.Length; s++)
            {
                int v = look[6 + s];
                if (v >= 512)
                {
                    if (!wornItems.TryGetValue(v - 512, out var w)) continue;
                    var ms = female ? w.female : w.male;
                    parts.Add((ms, w.recolorFrom, w.recolorTo, w.retextureFrom, w.retextureTo, female ? w.femaleOffset : w.maleOffset));
                }
                else if (v >= 256)
                {
                    if (!kits.TryGetValue(v - 256, out var k)) continue;
                    parts.Add((k.models, k.recolorFrom, k.recolorTo, k.retextureFrom, k.retextureTo, 0));
                }
            }
            var bodyTo = new ushort[5];
            for (int i = 0; i < 5; i++)
            {
                int c = 1 + i < look.Length ? look[1 + i] : 0;
                var row = BodyColourTable[i];
                bodyTo[i] = (ushort)(c >= 0 && c < row.Length ? row[c] : row[0]);
            }
            int faces = 0;
            foreach (var part in parts)
                foreach (var mid in part.models)
                    if (mid >= 0 && models.TryGetValue(mid, out var m)) faces += m.colors.Length;
            var mesh = new VariantMesh { positions = new float[faces * 9], colors = new Color32[faces * 3], faceCount = faces };
            int f = 0;
            foreach (var part in parts)
                foreach (var mid in part.models)
                {
                    if (mid < 0 || !models.TryGetValue(mid, out var m)) continue;
                    var vn = VertexNormals(mid, m);
                    int nf = m.colors.Length;
                    // OSRS model offset is in model units with Y pointing down; exported Y points up.
                    float dy = -part.offset;
                    for (int i = 0; i < nf; i++)
                    {
                        ushort hsl = m.colors[i];
                        for (int k = 0; k < part.rf.Length; k++)
                            if (hsl == part.rf[k]) { hsl = part.rt[k]; break; }
                        for (int k = 0; k < 5; k++)
                            if (hsl == BodyColourFind[k]) { hsl = bodyTo[k]; break; }
                        int tex = m.textures[i];
                        if (tex != -1)
                            for (int k = 0; k < part.xf.Length; k++)
                                if (tex == part.xf[k]) { tex = part.xt[k]; break; }
                        ushort baseHsl = (tex != -1 && textureColors.TryGetValue(tex, out var th)) ? th : hsl;
                        int o = f * 9;
                        for (int c = 0; c < 3; c++)
                        {
                            int vi = m.faces[i * 3 + c];
                            WriteVertex(mesh.positions, o + c * 3, m.positions, vi, false, 1f, 1f, 1f);
                            mesh.positions[o + c * 3 + 1] += dy;
                            mesh.colors[f * 3 + c] = OsrsLight.Actor(baseHsl, vn[vi * 3], vn[vi * 3 + 1], vn[vi * 3 + 2]);
                        }
                        f++;
                    }
                }
            return mesh;
        }

        // ------------------------------------------------------------------ animatable bodies

        private struct AnimPart
        {
            public int[] models;
            public ushort[] rf, rt, xf, xt;
            public int offset;
        }

        /// <summary>An NPC as an animatable body, or null if animations / labels are missing.</summary>
        public AnimMeshData BuildNpcAnimData(int npcId)
        {
            if (anim == null || !anim.Loaded || !npcs.TryGetValue(npcId, out var n)) return null;
            var parts = new List<AnimPart> { new AnimPart { models = n.models, rf = n.recolorFrom, rt = n.recolorTo, xf = n.retextureFrom, xt = n.retextureTo } };
            var d = BuildAnimData(parts, null);
            if (d == null) return null;
            d.sx = d.sz = n.scaleXZ / 128f;
            d.sy = n.scaleY / 128f;
            return d;
        }

        /// <summary>A player look (see BuildPlayerMesh) as an animatable body, or null.</summary>
        public AnimMeshData BuildPlayerAnimData(int[] look)
        {
            if (anim == null || !anim.Loaded || look == null) return null;
            var parts = new List<AnimPart>();
            bool female = look[0] == 1;
            for (int s = 0; s < 12 && 6 + s < look.Length; s++)
            {
                int v = look[6 + s];
                if (v >= 512)
                {
                    if (!wornItems.TryGetValue(v - 512, out var w)) continue;
                    parts.Add(new AnimPart { models = female ? w.female : w.male, rf = w.recolorFrom, rt = w.recolorTo, xf = w.retextureFrom, xt = w.retextureTo, offset = female ? w.femaleOffset : w.maleOffset });
                }
                else if (v >= 256)
                {
                    if (!kits.TryGetValue(v - 256, out var k)) continue;
                    parts.Add(new AnimPart { models = k.models, rf = k.recolorFrom, rt = k.recolorTo, xf = k.retextureFrom, xt = k.retextureTo });
                }
            }
            var bodyTo = new ushort[5];
            for (int i = 0; i < 5; i++)
            {
                int c = 1 + i < look.Length ? look[1 + i] : 0;
                var row = BodyColourTable[i];
                bodyTo[i] = (ushort)(c >= 0 && c < row.Length ? row[c] : row[0]);
            }
            return BuildAnimData(parts, bodyTo);
        }

        /// <summary>
        /// Welds the parts' models into one vertex list in OSRS space (Y down, item offsets applied)
        /// with their labels, plus the same flat-shaded triangles/colours the static meshes use.
        /// Models without exported labels still draw (all label 0).
        /// </summary>
        private AnimMeshData BuildAnimData(List<AnimPart> parts, ushort[] bodyTo)
        {
            int nu = 0, nf = 0;
            foreach (var part in parts)
                if (part.models != null)
                    foreach (var mid in part.models)
                        if (mid >= 0 && models.TryGetValue(mid, out var m)) { nu += m.positions.Length / 3; nf += m.colors.Length; }
            if (nf == 0) return null;
            var d = new AnimMeshData
            {
                bx = new int[nu], by = new int[nu], bz = new int[nu], labels = new byte[nu],
                corner = new int[nf * 3], colors = new Color32[nf * 3],
            };
            int vb = 0, f = 0;
            foreach (var part in parts)
            {
                if (part.models == null) continue;
                foreach (var mid in part.models)
                {
                    if (mid < 0 || !models.TryGetValue(mid, out var m)) continue;
                    int mv = m.positions.Length / 3;
                    var vn = VertexNormals(mid, m);
                    anim.skins.TryGetValue(mid, out var lab);
                    for (int i = 0; i < mv; i++)
                    {
                        d.bx[vb + i] = m.positions[i * 3];
                        d.by[vb + i] = -m.positions[i * 3 + 1] + part.offset;
                        d.bz[vb + i] = m.positions[i * 3 + 2];
                        d.labels[vb + i] = lab != null && i < lab.Length ? lab[i] : (byte)0;
                    }
                    int mf = m.colors.Length;
                    for (int i = 0; i < mf; i++)
                    {
                        ushort hsl = m.colors[i];
                        if (part.rf != null)
                            for (int k = 0; k < part.rf.Length; k++)
                                if (hsl == part.rf[k]) { hsl = part.rt[k]; break; }
                        if (bodyTo != null)
                            for (int k = 0; k < 5; k++)
                                if (hsl == BodyColourFind[k]) { hsl = bodyTo[k]; break; }
                        int tex = m.textures[i];
                        if (tex != -1 && part.xf != null)
                            for (int k = 0; k < part.xf.Length; k++)
                                if (tex == part.xf[k]) { tex = part.xt[k]; break; }
                        ushort baseHsl = (tex != -1 && textureColors.TryGetValue(tex, out var th)) ? th : hsl;
                        for (int c = 0; c < 3; c++)
                        {
                            int vi = m.faces[i * 3 + c];
                            d.corner[f * 3 + c] = vb + vi;
                            d.colors[f * 3 + c] = OsrsLight.Actor(baseHsl, vn[vi * 3], vn[vi * 3 + 1], vn[vi * 3 + 2]);
                        }
                        f++;
                    }
                    vb += mv;
                }
            }
            d.Index();
            return d;
        }
        /// <summary>
        /// Smooth vertex normals of a model like the OSRS client: each face normal is scaled to
        /// length 256 and summed into its corners, then averaged. Exported space (Y up, swapped
        /// winding) yields (nx, -ny, nz) of the client's normal, matching the Y-flipped lights.
        /// </summary>
        private float[] VertexNormals(int id, Model m)
        {
            return vertexNormals.GetOrAdd(id, _ =>
            {
                int nv = m.positions.Length / 3;
                var n = new float[nv * 3];
                var count = new int[nv];
                int nf = m.colors.Length;
                for (int i = 0; i < nf; i++)
                {
                    int a = m.faces[i * 3], b = m.faces[i * 3 + 1], c = m.faces[i * 3 + 2];
                    float ux = m.positions[b * 3] - m.positions[a * 3], uy = m.positions[b * 3 + 1] - m.positions[a * 3 + 1], uz = m.positions[b * 3 + 2] - m.positions[a * 3 + 2];
                    float vx = m.positions[c * 3] - m.positions[a * 3], vy = m.positions[c * 3 + 1] - m.positions[a * 3 + 1], vz = m.positions[c * 3 + 2] - m.positions[a * 3 + 2];
                    float fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
                    float len = Mathf.Sqrt(fx * fx + fy * fy + fz * fz);
                    if (len < 1e-6f) continue;
                    float k = 256f / len;
                    fx *= k; fy *= k; fz *= k;
                    foreach (int v in new[] { a, b, c })
                    {
                        n[v * 3] += fx; n[v * 3 + 1] += fy; n[v * 3 + 2] += fz;
                        count[v]++;
                    }
                }
                for (int v = 0; v < nv; v++)
                    if (count[v] > 1) { n[v * 3] /= count[v]; n[v * 3 + 1] /= count[v]; n[v * 3 + 2] /= count[v]; }
                return n;
            });
        }

        private static void WriteVertex(float[] dst, int o, short[] src, int index, bool mirror, float sx, float sy, float sz)
        {
            dst[o] = src[index * 3] * sx;
            dst[o + 1] = src[index * 3 + 1] * sy;
            float z = src[index * 3 + 2] * sz;
            dst[o + 2] = mirror ? -z : z;
        }
    }

    /// <summary>
    /// The OSRS client's model lighting, baked into vertex colours: lightness = ambient +
    /// dot(light, normal) / (contrast * |light| / 256), multiplied into the HSL lightness.
    /// Lights are the client's vectors with Y negated for the exported (Y up) space.
    /// </summary>
    public static class OsrsLight
    {
        private const float LocLx = -50f, LocLy = 10f, LocLz = -50f;
        private const float ActorLx = -30f, ActorLy = 50f, ActorLz = -30f;
        private const int Ambient = 64;
        private static readonly float LocK = Mathf.Floor(768f * Mathf.Floor(Mathf.Sqrt(LocLx * LocLx + LocLy * LocLy + LocLz * LocLz)) / 256f);
        private static readonly float ActorK = Mathf.Floor(850f * Mathf.Floor(Mathf.Sqrt(ActorLx * ActorLx + ActorLy * ActorLy + ActorLz * ActorLz)) / 256f);

        /// <summary>A loc corner placed with <paramref name="yaw"/> radians: the light is turned into model space.</summary>
        public static Color32 Loc(ushort hsl, float nx, float ny, float nz, float yaw)
        {
            float s = Mathf.Sin(yaw), c = Mathf.Cos(yaw);
            float lx = c * LocLx - s * LocLz, lz = s * LocLx + c * LocLz;
            return Shade(hsl, Ambient + (lx * nx + LocLy * ny + lz * nz) / LocK);
        }

        public static Color32 Actor(ushort hsl, float nx, float ny, float nz)
        {
            return Shade(hsl, Ambient + (ActorLx * nx + ActorLy * ny + ActorLz * nz) / ActorK);
        }

        public static void ShadeLoc(WorldData.VariantMesh vm, float yaw, Color32[] dst, int offset)
        {
            float s = Mathf.Sin(yaw), c = Mathf.Cos(yaw);
            float lx = c * LocLx - s * LocLz, lz = s * LocLx + c * LocLz;
            int n = vm.faceCount * 3;
            if (vm.normals == null) { Array.Copy(vm.colors, 0, dst, offset, n); return; }
            for (int k = 0; k < n; k++)
            {
                float d = lx * vm.normals[k * 3] + LocLy * vm.normals[k * 3 + 1] + lz * vm.normals[k * 3 + 2];
                dst[offset + k] = Shade(vm.hsls[k], Ambient + d / LocK);
            }
        }

        private static Color32 Shade(ushort hsl, float light)
        {
            int l = ((hsl & 127) * (int)light) >> 7;
            if (l < 2) l = 2; else if (l > 126) l = 126;
            return JagexColor.ToColor32((ushort)((hsl & 0xFF80) | l));
        }

        /// <summary>The client's tile light for a terrain normal (Y up, unit length): flat ground is about 108.</summary>
        public static float Terrain(float nx, float ny, float nz)
        {
            return 96f + 256f * (LocLx * nx + LocLy * ny + LocLz * nz) / LocK;
        }
    }

    public static class JagexColor
    {
        private static readonly Color32[] cache = new Color32[65536];
        private static readonly bool[] cached = new bool[65536];

        public static Color32 ToColor32(ushort hsl)
        {
            if (cached[hsl]) return cache[hsl];
            double hue = ((hsl >> 10) & 63) / 64.0 + 0.5 / 64.0;
            double sat = ((hsl >> 7) & 7) / 8.0 + 0.5 / 8.0;
            double lum = (hsl & 127) / 128.0;
            double chroma = (1.0 - Math.Abs(2.0 * lum - 1.0)) * sat;
            double x = chroma * (1 - Math.Abs(((hue * 6.0) % 2.0) - 1.0));
            double m = lum - chroma / 2;
            double r = m, g = m, b = m;
            switch ((int)(hue * 6.0))
            {
                case 0: r += chroma; g += x; break;
                case 1: g += chroma; r += x; break;
                case 2: g += chroma; b += x; break;
                case 3: b += chroma; g += x; break;
                case 4: b += chroma; r += x; break;
                default: r += chroma; b += x; break;
            }
            var c = new Color32(Gamma(r), Gamma(g), Gamma(b), 255);
            cache[hsl] = c;
            cached[hsl] = true;
            return c;
        }

        private static byte Gamma(double v)
        {
            v = Math.Min(1.0, Math.Max(0.0, v));
            return (byte)Math.Round(Math.Pow(v, 0.7) * 255.0);
        }
    }
}
