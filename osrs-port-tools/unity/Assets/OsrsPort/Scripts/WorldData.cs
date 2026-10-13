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

        /// <summary>A variant resolved to flat-shaded triangles: 9 floats and 3 colours per face.</summary>
        public class VariantMesh
        {
            public float[] positions;
            public Color32[] colors;
            public int faceCount;
        }

        public readonly Dictionary<(int, int), SquareEntry> squares = new Dictionary<(int, int), SquareEntry>();
        public readonly Dictionary<int, Model> models = new Dictionary<int, Model>();
        public Variant[] variants = Array.Empty<Variant>();
        public readonly Dictionary<int, ushort> textureColors = new Dictionary<int, ushort>();
        private readonly ConcurrentDictionary<int, VariantMesh> variantMeshes = new ConcurrentDictionary<int, VariantMesh>();
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
            LoadModels();
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

        private void LoadModels()
        {
            var d = ReadGzip(Path.Combine(root, "models.bin.gz"));
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
            int faces = 0;
            foreach (var mid in v.models)
                if (models.TryGetValue(mid, out var m)) faces += m.colors.Length;
            var mesh = new VariantMesh { positions = new float[faces * 9], colors = new Color32[faces * 3], faceCount = faces };
            float sx = v.scaleX / 128f, sy = v.scaleY / 128f, sz = v.scaleZ / 128f;
            int f = 0;
            foreach (var mid in v.models)
            {
                if (!models.TryGetValue(mid, out var m)) continue;
                int nf = m.colors.Length;
                for (int i = 0; i < nf; i++)
                {
                    int a = m.faces[i * 3], b = m.faces[i * 3 + 1], c = m.faces[i * 3 + 2];
                    if (v.mirror) { int t = b; b = c; c = t; }
                    ushort hsl = m.colors[i];
                    for (int k = 0; k < v.recolorFrom.Length; k++)
                        if (hsl == v.recolorFrom[k]) { hsl = v.recolorTo[k]; break; }
                    int tex = m.textures[i];
                    if (tex != -1)
                        for (int k = 0; k < v.retextureFrom.Length; k++)
                            if (tex == v.retextureFrom[k]) { tex = v.retextureTo[k]; break; }
                    Color32 col = (tex != -1 && textureColors.TryGetValue(tex, out var th)) ? JagexColor.ToColor32(th) : JagexColor.ToColor32(hsl);
                    int o = f * 9;
                    WriteVertex(mesh.positions, o, m.positions, a, v.mirror, sx, sy, sz);
                    WriteVertex(mesh.positions, o + 3, m.positions, b, v.mirror, sx, sy, sz);
                    WriteVertex(mesh.positions, o + 6, m.positions, c, v.mirror, sx, sy, sz);
                    mesh.colors[f * 3] = col; mesh.colors[f * 3 + 1] = col; mesh.colors[f * 3 + 2] = col;
                    f++;
                }
            }
            return mesh;
        }

        private static void WriteVertex(float[] dst, int o, short[] src, int index, bool mirror, float sx, float sy, float sz)
        {
            dst[o] = src[index * 3] * sx;
            dst[o + 1] = src[index * 3 + 1] * sy;
            float z = src[index * 3 + 2] * sz;
            dst[o + 2] = mirror ? -z : z;
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
