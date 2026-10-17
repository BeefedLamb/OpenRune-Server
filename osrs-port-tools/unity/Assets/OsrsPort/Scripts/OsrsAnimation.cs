using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>
    /// OSRS animation data exported from the cache: sequences (seqs.txt, exportSequences), frame-bases
    /// / frames (anims.bin.gz) and model vertex labels (model_skins.bin.gz, export_animations.py).
    /// OSRS animates by transforming groups of labelled vertices (origin / translate / rotate / scale),
    /// not with bone skeletons; <see cref="OsrsAnimator"/> replays that on the CPU.
    /// Skeletal ("maya") sequences are not exported and simply do not play.
    /// </summary>
    public class OsrsAnimData
    {
        public struct FrameSound
        {
            public int id, loops, location, retain;
        }

        public class Seq
        {
            public int id, frameStep, maxLoops, forcedPriority, priority, replyMode, precedenceAnimating;
            public int[] delays, frames, interleave;
            public FrameSound[] sounds;
        }

        public class FrameMap
        {
            public byte[] types;
            public byte[][] groups;
        }

        public class Frame
        {
            public FrameMap map;
            public ushort[] group;
            public short[] dx, dy, dz;
        }

        public readonly Dictionary<int, Seq> seqs = new Dictionary<int, Seq>();
        public readonly Dictionary<int, Frame> frames = new Dictionary<int, Frame>();
        public readonly Dictionary<int, byte[]> skins = new Dictionary<int, byte[]>();
        public bool Loaded => seqs.Count > 0 && frames.Count > 0 && skins.Count > 0;

        public static OsrsAnimData Load(string root)
        {
            var a = new OsrsAnimData();
            try
            {
                string seqPath = Path.Combine(root, "seqs.txt");
                string animPath = Path.Combine(root, "anims.bin.gz");
                string skinPath = Path.Combine(root, "model_skins.bin.gz");
                if (!File.Exists(seqPath) || !File.Exists(animPath) || !File.Exists(skinPath)) return a;
                foreach (var line in File.ReadLines(seqPath))
                {
                    if (line.Length == 0 || line[0] != 'S') continue;
                    var p = line.Split('\t');
                    if (p.Length < 16 || int.Parse(p[8]) >= 0) continue; // skeletal: unsupported
                    var s = new Seq
                    {
                        id = int.Parse(p[1]), frameStep = int.Parse(p[2]), maxLoops = int.Parse(p[3]),
                        forcedPriority = int.Parse(p[4]), priority = int.Parse(p[5]), replyMode = int.Parse(p[6]),
                        precedenceAnimating = int.Parse(p[7]),
                        delays = Ints(p[13]), frames = Ints(p[14]), interleave = Ints(p[15]),
                    };
                    s.sounds = FrameSounds(p.Length > 16 ? p[16] : "", s.frames.Length);
                    if (s.frames.Length == 0) continue;
                    a.seqs[s.id] = s;
                }
                var d = Gunzip(animPath);
                int o = 4; // "OAN1"
                var maps = new Dictionary<int, FrameMap>();
                int nm = BitConverter.ToInt32(d, o); o += 4;
                for (int i = 0; i < nm; i++)
                {
                    int id = BitConverter.ToInt32(d, o); int n = BitConverter.ToUInt16(d, o + 4); o += 6;
                    var fm = new FrameMap { types = new byte[n], groups = new byte[n][] };
                    Buffer.BlockCopy(d, o, fm.types, 0, n); o += n;
                    for (int g = 0; g < n; g++)
                    {
                        int c = BitConverter.ToUInt16(d, o); o += 2;
                        fm.groups[g] = new byte[c];
                        Buffer.BlockCopy(d, o, fm.groups[g], 0, c); o += c;
                    }
                    maps[id] = fm;
                }
                int nf = BitConverter.ToInt32(d, o); o += 4;
                for (int i = 0; i < nf; i++)
                {
                    int id = BitConverter.ToInt32(d, o); int map = BitConverter.ToUInt16(d, o + 4); int t = BitConverter.ToUInt16(d, o + 6); o += 8;
                    var f = new Frame { group = new ushort[t], dx = new short[t], dy = new short[t], dz = new short[t] };
                    maps.TryGetValue(map, out f.map);
                    for (int k = 0; k < t; k++)
                    {
                        f.group[k] = BitConverter.ToUInt16(d, o); f.dx[k] = BitConverter.ToInt16(d, o + 2);
                        f.dy[k] = BitConverter.ToInt16(d, o + 4); f.dz[k] = BitConverter.ToInt16(d, o + 6); o += 8;
                    }
                    if (f.map != null) a.frames[id] = f;
                }
                var s2 = Gunzip(skinPath);
                o = 0;
                int ns = BitConverter.ToInt32(s2, o); o += 4;
                for (int i = 0; i < ns; i++)
                {
                    int id = BitConverter.ToInt32(s2, o); int n = BitConverter.ToUInt16(s2, o + 4); o += 6;
                    var lab = new byte[n];
                    Buffer.BlockCopy(s2, o, lab, 0, n); o += n;
                    a.skins[id] = lab;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("OSRS animations not loaded: " + e.Message);
                a.seqs.Clear();
            }
            return a;
        }

        public bool Playable(int seq) => seq >= 0 && seqs.ContainsKey(seq);

        private static FrameSound[] FrameSounds(string s, int frames)
        {
            var r = new FrameSound[frames];
            if (string.IsNullOrEmpty(s)) return r;
            foreach (var tok in s.Split(','))
            {
                var a = tok.Split(':');
                if (a.Length < 5) continue;
                if (!int.TryParse(a[0], out int frame) || frame < 0 || frame >= frames) continue;
                r[frame] = new FrameSound
                {
                    id = int.Parse(a[1]), loops = int.Parse(a[2]), location = int.Parse(a[3]), retain = int.Parse(a[4]),
                };
            }
            return r;
        }

        private static int[] Ints(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<int>();
            var parts = s.Split(',');
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) r[i] = int.Parse(parts[i]);
            return r;
        }

        private static byte[] Gunzip(string path)
        {
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            using (var mem = new MemoryStream())
            {
                gz.CopyTo(mem);
                return mem.ToArray();
            }
        }
    }

    /// <summary>
    /// An animatable body: the unique (welded) model vertices in OSRS space (Y down, model units),
    /// their labels, and the flat-shaded render corners pointing back at them.
    /// </summary>
    public class AnimMeshData
    {
        public int[] bx, by, bz;
        public byte[] labels;
        public int[] corner;       // render vertex -> unique vertex
        public Color32[] colors;   // per render vertex
        public float sx = 1f, sy = 1f, sz = 1f;
        public int[][] byLabel;    // label -> unique vertex indices

        public void Index()
        {
            var lists = new List<int>[256];
            for (int i = 0; i < labels.Length; i++)
                (lists[labels[i]] ??= new List<int>()).Add(i);
            byLabel = new int[256][];
            for (int l = 0; l < 256; l++) byLabel[l] = lists[l] != null ? lists[l].ToArray() : Array.Empty<int>();
        }
    }

    /// <summary>
    /// Plays OSRS sequences on one body by redoing the client's frame transforms each time the frame
    /// changes. Loops the base set (stand / walk / run, chosen from <see cref="moving"/> and
    /// <see cref="running"/>) and overlays one-shot actions from the server (ANIM / NANIM).
    /// Not yet done: interleaved (upper/lower body) blending, skeletal seqs, frame tweening.
    /// </summary>
    public class OsrsAnimator : MonoBehaviour
    {
        /// <summary>The local player's third-person avatar (gets `ANIM`).</summary>
        public static OsrsAnimator Local;

        private const float Cycle = 0.02f; // OSRS client cycle: frame delays are in 20 ms units
        public int standSeq = -1, walkSeq = -1, runSeq = -1;
        public bool moving, running;
        public float cullDistance = 50f;

        private OsrsAnimData anims;
        private AnimMeshData data;
        private Mesh mesh;
        private int[] wx, wy, wz;
        private Vector3[] verts, normals;
        private int baseSeq = -1, baseFrame;
        private float baseTime;
        private int actionSeq = -1, actionFrame, actionLoops;
        private float actionTime;
        private int shownSeq = -2, shownFrame = -2;

        private static readonly int[] Sin = new int[2048], Cos = new int[2048];

        static OsrsAnimator()
        {
            for (int i = 0; i < 2048; i++)
            {
                Sin[i] = (int)(65536.0 * Math.Sin(i * 0.0030679615));
                Cos[i] = (int)(65536.0 * Math.Cos(i * 0.0030679615));
            }
        }

        public Mesh Mesh => mesh;

        /// <summary>Builds this body's own mesh (static pose) and returns it for the MeshFilter.</summary>
        public Mesh Init(OsrsAnimData animData, AnimMeshData d, string name)
        {
            anims = animData;
            data = d;
            if (d.byLabel == null) d.Index();
            int nu = d.bx.Length, nv = d.corner.Length;
            wx = new int[nu]; wy = new int[nu]; wz = new int[nu];
            verts = new Vector3[nv]; normals = new Vector3[nv];
            mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            Array.Copy(d.bx, wx, nu); Array.Copy(d.by, wy, nu); Array.Copy(d.bz, wz, nu);
            WriteMesh();
            var idx = new int[nv];
            for (int i = 0; i < nv; i++) idx[i] = i;
            mesh.SetColors(d.colors);
            mesh.SetTriangles(idx, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Starts a one-shot action sequence (-1 or an unknown/skeletal seq cancels it).</summary>
        public void Play(int seq)
        {
            if (anims == null || !anims.Playable(seq)) { actionSeq = -1; return; }
            actionSeq = seq; actionFrame = 0; actionTime = 0f; actionLoops = 0;
            Hear(anims.seqs[seq], 0);
        }

        private void Update()
        {
            if (anims == null || data == null) return;
            float dt = Time.deltaTime;
            int want = moving ? (running && anims.Playable(runSeq) ? runSeq : anims.Playable(walkSeq) ? walkSeq : standSeq) : standSeq;
            if (!anims.Playable(want)) want = -1;
            if (want != baseSeq)
            {
                baseSeq = want; baseFrame = 0; baseTime = 0f;
                if (baseSeq >= 0) Hear(anims.seqs[baseSeq], 0);
            }
            if (baseSeq >= 0) Advance(anims.seqs[baseSeq], ref baseFrame, ref baseTime, dt, true, ref actionLoops, false);
            if (actionSeq >= 0 && !Advance(anims.seqs[actionSeq], ref actionFrame, ref actionTime, dt, false, ref actionLoops, true))
                actionSeq = -1;

            int seq = actionSeq >= 0 ? actionSeq : baseSeq;
            int frame = actionSeq >= 0 ? actionFrame : baseFrame;
            if (seq == shownSeq && frame == shownFrame) return;
            var cam = Camera.main;
            if (cam != null && (cam.transform.position - transform.position).sqrMagnitude > cullDistance * cullDistance) return;
            shownSeq = seq; shownFrame = frame;
            Pose(seq, frame);
        }

        /// <summary>Steps a sequence by dt. Returns false when a one-shot finished.</summary>
        private bool Advance(OsrsAnimData.Seq s, ref int frame, ref float time, float dt, bool loop, ref int loops, bool countLoops)
        {
            time += dt;
            int guard = 0;
            while (guard++ < 64)
            {
                int delay = frame < s.delays.Length ? Math.Max(1, s.delays[frame]) : 1;
                if (time < delay * Cycle) break;
                time -= delay * Cycle;
                frame++;
                if (frame < s.frames.Length) { Hear(s, frame); continue; }
                if (loop) { frame = 0; Hear(s, frame); continue; }
                if (s.frameStep < 0) return false;
                frame -= s.frameStep;
                if (countLoops && ++loops >= Math.Max(1, s.maxLoops)) return false;
                if (frame < 0 || frame >= s.frames.Length) return false;
                Hear(s, frame);
            }
            return true;
        }

        /// <summary>Plays the sound stored on this frame. The local player hears it directly; everyone else is positional.</summary>
        private void Hear(OsrsAnimData.Seq s, int frame)
        {
            if (s.sounds == null || frame < 0 || frame >= s.sounds.Length) return;
            int id = s.sounds[frame].id;
            if (id <= 0) return;
            var audio = OsrsSound.Ensure();
            if (this == Local)
                audio.Play(id, s.sounds[frame].loops, 0);
            else
            {
                int loc = s.sounds[frame].location;
                audio.PlayAt(id, s.sounds[frame].loops, 0, transform.position, loc > 0 ? loc : 8f);
            }
        }

        private void Pose(int seq, int frame)
        {
            int nu = data.bx.Length;
            Array.Copy(data.bx, wx, nu); Array.Copy(data.by, wy, nu); Array.Copy(data.bz, wz, nu);
            if (seq >= 0 && anims.seqs.TryGetValue(seq, out var s) && frame < s.frames.Length
                && anims.frames.TryGetValue(s.frames[frame], out var f))
                ApplyFrame(f);
            WriteMesh();
        }

        private int ox, oy, oz;

        /// <summary>The OSRS client's Model.animate: runs every transform of the frame in order.</summary>
        private void ApplyFrame(OsrsAnimData.Frame f)
        {
            ox = oy = oz = 0;
            var map = f.map;
            for (int t = 0; t < f.group.Length; t++)
            {
                int g = f.group[t];
                if (g >= map.types.Length) continue;
                Transform(map.types[g], map.groups[g], f.dx[t], f.dy[t], f.dz[t]);
            }
        }

        private void Transform(int type, byte[] labels, int dx, int dy, int dz)
        {
            var by = data.byLabel;
            if (type == 0)
            {
                int count = 0; long sx = 0, sy = 0, sz = 0;
                foreach (var l in labels)
                    foreach (var v in by[l]) { sx += wx[v]; sy += wy[v]; sz += wz[v]; count++; }
                if (count > 0) { ox = (int)(sx / count) + dx; oy = (int)(sy / count) + dy; oz = (int)(sz / count) + dz; }
                else { ox = dx; oy = dy; oz = dz; }
            }
            else if (type == 1)
            {
                foreach (var l in labels)
                    foreach (var v in by[l]) { wx[v] += dx; wy[v] += dy; wz[v] += dz; }
            }
            else if (type == 2)
            {
                int rx = (dx & 255) * 8, ry = (dy & 255) * 8, rz = (dz & 255) * 8;
                foreach (var l in labels)
                    foreach (var v in by[l])
                    {
                        int x = wx[v] - ox, y = wy[v] - oy, z = wz[v] - oz;
                        if (rz != 0)
                        {
                            int s = Sin[rz], c = Cos[rz];
                            int t = (y * s + x * c) >> 16; y = (y * c - x * s) >> 16; x = t;
                        }
                        if (rx != 0)
                        {
                            int s = Sin[rx], c = Cos[rx];
                            int t = (y * c - z * s) >> 16; z = (y * s + z * c) >> 16; y = t;
                        }
                        if (ry != 0)
                        {
                            int s = Sin[ry], c = Cos[ry];
                            int t = (z * s + x * c) >> 16; z = (z * c - x * s) >> 16; x = t;
                        }
                        wx[v] = x + ox; wy[v] = y + oy; wz[v] = z + oz;
                    }
            }
            else if (type == 3)
            {
                foreach (var l in labels)
                    foreach (var v in by[l])
                    {
                        wx[v] = (wx[v] - ox) * dx / 128 + ox;
                        wy[v] = (wy[v] - oy) * dy / 128 + oy;
                        wz[v] = (wz[v] - oz) * dz / 128 + oz;
                    }
            }
            // type 5 (face alpha) is not applied.
        }

        private void WriteMesh()
        {
            var c = data.corner;
            float kx = data.sx / 128f, ky = -data.sy / 128f, kz = data.sz / 128f;
            for (int i = 0; i < c.Length; i++)
            {
                int u = c[i];
                verts[i] = new Vector3(wx[u] * kx, wy[u] * ky, wz[u] * kz);
            }
            for (int i = 0; i + 2 < c.Length; i += 3)
            {
                var n = Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]).normalized;
                normals[i] = n; normals[i + 1] = n; normals[i + 2] = n;
            }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        private void OnDestroy()
        {
            if (Local == this) Local = null;
            if (mesh != null) Destroy(mesh);
        }
    }
}
