using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace OsrsPort
{
    /// <summary>
    /// Plays cache MIDI with the client's patches: Vorbis samples from index 14,
    /// synths from index 4, and the MusicPatch pitch, volume, pan and envelopes.
    /// </summary>
    public static class OsrsMusicBank
    {
        public const int Rate = 22050;

        public static Func<int, OsrsVorbis.Pcm> LoadSynth;

        private static Dictionary<int, byte[]> patchBytes;
        private static Dictionary<int, byte[]> sampleBytes;
        private static readonly Dictionary<int, Patch> patches = new Dictionary<int, Patch>();
        private static readonly Dictionary<long, OsrsVorbis.Pcm> pcmCache = new Dictionary<long, OsrsVorbis.Pcm>();
        private static bool setupReady;

        public static int PatchCount => patchBytes == null ? 0 : patchBytes.Count;
        public static int SampleCount => sampleBytes == null ? 0 : sampleBytes.Count;

        public static void Load(string path)
        {
            if (patchBytes != null) return;
            patchBytes = new Dictionary<int, byte[]>();
            sampleBytes = new Dictionary<int, byte[]>();
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (new string(r.ReadChars(4)) != "OSI1") throw new InvalidDataException("instruments.bin.gz");
                int patchesN = r.ReadInt32();
                for (int i = 0; i < patchesN; i++)
                {
                    int id = r.ReadInt32();
                    int len = r.ReadInt32();
                    patchBytes[id] = r.ReadBytes(len);
                }
                int samplesN = r.ReadInt32();
                for (int i = 0; i < samplesN; i++)
                {
                    int id = r.ReadInt32();
                    int len = r.ReadInt32();
                    sampleBytes[id] = r.ReadBytes(len);
                }
                int setupLen = r.ReadInt32();
                var setup = r.ReadBytes(setupLen);
                OsrsVorbis.ReadSetup(setup);
                setupReady = true;
            }
        }

        public static Song Start(byte[] midi)
        {
            var song = new Song(midi);
            song.Prepare();
            return song;
        }

        private static Patch PatchFor(int id)
        {
            if (patches.TryGetValue(id, out var cached)) return cached;
            if (patchBytes == null || !patchBytes.TryGetValue(id, out var raw)) return null;
            var patch = Patch.Decode(raw);
            patches[id] = patch;
            return patch;
        }

        private static OsrsVorbis.Pcm SampleFor(int key)
        {
            if (key == 0) return null;
            int id = key - 1;
            bool synth = (id & 1) == 0;
            int archive = id >> 2;
            long cacheKey = ((long)(uint)archive << 1) | (uint)(synth ? 0 : 1);
            if (pcmCache.TryGetValue(cacheKey, out var hit)) return hit;
            OsrsVorbis.Pcm pcm = null;
            if (synth)
            {
                if (LoadSynth != null) pcm = LoadSynth(archive);
            }
            else if (setupReady && sampleBytes != null && sampleBytes.TryGetValue(archive, out var raw))
            {
                try { pcm = OsrsVorbis.Decode(raw); }
                catch { pcm = null; }
            }
            pcmCache[cacheKey] = pcm;
            return pcm;
        }

        private sealed class Env
        {
            public byte[] Vol;
            public byte[] Pitch;
            public int Decay;
            public int VolRate;
            public int PitchRate;
            public int DecayRate;
            public int Vib;
            public int VibDepth;
            public int VibDelay;
        }

        private sealed class Patch
        {
            public readonly short[] Tune = new short[128];
            public readonly sbyte[] Vol = new sbyte[128];
            public readonly byte[] Pan = new byte[128];
            public readonly sbyte[] Exclusive = new sbyte[128];
            public readonly int[] Key = new int[128];
            public readonly Env[] Envs = new Env[128];
            public int Master;

            public static Patch Decode(byte[] data)
            {
                var b = new R(data);
                var a = b.Z(out int aAt, out int aSkip);
                b.p += aSkip;
                var c = b.Z(out int cAt, out int cSkip);
                b.p += cSkip;
                var d = b.Z(out _, out _);
                int nmap = d.Length + 1;
                var mapping = new int[nmap];
                int groups;
                if (nmap > 1)
                {
                    mapping[1] = 1;
                    int group = 1;
                    int nxt = 2;
                    for (int i = 2; i < nmap; i++)
                    {
                        int v = b.U8();
                        if (v == 0) { group = nxt++; }
                        else { if (v <= group) v--; group = v; }
                        mapping[i] = group;
                    }
                    groups = nxt;
                }
                else groups = nmap;
                var envs = new Env[groups];
                for (int i = 0; i < groups; i++)
                {
                    var e = new Env();
                    int n = b.U8();
                    if (n > 0) e.Vol = new byte[n * 2];
                    n = b.U8();
                    if (n > 0)
                    {
                        e.Pitch = new byte[n * 2 + 2];
                        e.Pitch[1] = 64;
                    }
                    envs[i] = e;
                }
                int vn = b.U8();
                byte[] volCurve = vn > 0 ? new byte[vn * 2] : null;
                int pn = b.U8();
                byte[] panCurve = pn > 0 ? new byte[pn * 2] : null;
                var runs = b.Z(out _, out _);
                var patch = new Patch();
                int acc = 0;
                var pitch = new int[128];
                for (int i = 0; i < 128; i++) { acc += b.U8(); pitch[i] = acc; }
                acc = 0;
                for (int i = 0; i < 128; i++) { acc += b.U8(); pitch[i] += acc << 8; }
                int run = 0, ri = 0, cur = 0;
                for (int i = 0; i < 128; i++)
                {
                    if (run == 0)
                    {
                        run = ri < runs.Length ? runs[ri++] : -1;
                        cur = b.VarInt();
                    }
                    pitch[i] += ((cur - 1) & 2) << 14;
                    patch.Tune[i] = (short)pitch[i];
                    patch.Key[i] = cur;
                    run--;
                }
                run = 0; ri = 0; cur = 0;
                var exclusive = new int[128];
                for (int i = 0; i < 128; i++)
                {
                    if (patch.Key[i] == 0) continue;
                    if (run == 0)
                    {
                        run = ri < a.Length ? a[ri++] : -1;
                        cur = (sbyte)data[aAt++] - 1;
                    }
                    exclusive[i] = cur;
                    run--;
                }
                run = 0; ri = 0; cur = 0;
                for (int i = 0; i < 128; i++)
                {
                    if (patch.Key[i] == 0) continue;
                    if (run == 0)
                    {
                        run = ri < c.Length ? c[ri++] : -1;
                        cur = ((sbyte)data[cAt++] + 16) << 2;
                    }
                    patch.Pan[i] = (byte)cur;
                    run--;
                }
                run = 0; ri = 0;
                Env env = envs.Length > 0 ? envs[0] : new Env();
                for (int i = 0; i < 128; i++)
                {
                    if (patch.Key[i] == 0) continue;
                    if (run == 0)
                    {
                        env = envs[mapping[ri]];
                        run = ri < d.Length ? d[ri++] : -1;
                    }
                    patch.Envs[i] = env;
                    run--;
                }
                run = 0; ri = 0; cur = 0;
                for (int i = 0; i < 128; i++)
                {
                    if (run == 0)
                    {
                        run = ri < runs.Length ? runs[ri++] : -1;
                        if (patch.Key[i] > 0) cur = b.U8() + 1;
                    }
                    patch.Vol[i] = (sbyte)cur;
                    run--;
                }
                for (int i = 0; i < 128; i++) patch.Exclusive[i] = (sbyte)exclusive[i];
                patch.Master = b.U8() + 1;
                foreach (var e in envs)
                {
                    if (e.Vol != null)
                        for (int k = 1; k < e.Vol.Length; k += 2) e.Vol[k] = (byte)b.I8();
                    if (e.Pitch != null)
                        for (int k = 3; k < e.Pitch.Length - 2; k += 2) e.Pitch[k] = (byte)b.I8();
                }
                if (volCurve != null)
                    for (int k = 1; k < volCurve.Length; k += 2) volCurve[k] = (byte)b.I8();
                if (panCurve != null)
                    for (int k = 1; k < panCurve.Length; k += 2) panCurve[k] = (byte)b.I8();
                foreach (var e in envs)
                {
                    if (e.Pitch != null)
                    {
                        acc = 0;
                        for (int k = 2; k < e.Pitch.Length; k += 2)
                        {
                            acc = 1 + acc + b.U8();
                            e.Pitch[k] = (byte)acc;
                        }
                    }
                }
                foreach (var e in envs)
                {
                    if (e.Vol != null)
                    {
                        acc = 0;
                        for (int k = 2; k < e.Vol.Length; k += 2)
                        {
                            acc = 1 + acc + b.U8();
                            e.Vol[k] = (byte)acc;
                        }
                    }
                }
                if (volCurve != null)
                {
                    acc = b.U8();
                    volCurve[0] = (byte)acc;
                    for (int k = 2; k < volCurve.Length; k += 2)
                    {
                        acc = 1 + acc + b.U8();
                        volCurve[k] = (byte)acc;
                    }
                    int x = volCurve[0];
                    int y = (sbyte)volCurve[1];
                    for (int i = 0; i < x; i++) patch.Vol[i] = (sbyte)((y * patch.Vol[i] + 32) >> 6);
                    for (int k = 2; k < volCurve.Length; k += 2)
                    {
                        int x2 = volCurve[k];
                        int y2 = (sbyte)volCurve[k + 1];
                        int span = x2 - x;
                        int acc2 = y * span + span / 2;
                        for (int i = x; i < x2; i++)
                        {
                            int m = span != 0 ? acc2 / span : 0;
                            patch.Vol[i] = (sbyte)((m * patch.Vol[i] + 32) >> 6);
                            acc2 += y2 - y;
                        }
                        x = x2;
                        y = y2;
                    }
                    for (int i = x; i < 128; i++) patch.Vol[i] = (sbyte)((y * patch.Vol[i] + 32) >> 6);
                }
                if (panCurve != null)
                {
                    acc = b.U8();
                    panCurve[0] = (byte)acc;
                    for (int k = 2; k < panCurve.Length; k += 2)
                    {
                        acc = 1 + acc + b.U8();
                        panCurve[k] = (byte)acc;
                    }
                    int x = panCurve[0];
                    int y = (sbyte)panCurve[1] << 1;
                    for (int i = 0; i < x; i++) patch.Pan[i] = ClampPan(y + patch.Pan[i]);
                    for (int k = 2; k < panCurve.Length; k += 2)
                    {
                        int x2 = panCurve[k];
                        int y2 = (sbyte)panCurve[k + 1] << 1;
                        int span = x2 - x;
                        int acc2 = y * span + span / 2;
                        for (int i = x; i < x2; i++)
                        {
                            int m = span != 0 ? acc2 / span : 0;
                            patch.Pan[i] = ClampPan(m + patch.Pan[i]);
                            acc2 += y2 - y;
                        }
                        x = x2;
                        y = y2;
                    }
                    for (int i = x; i < 128; i++) patch.Pan[i] = ClampPan(y + patch.Pan[i]);
                }
                foreach (var e in envs) e.Decay = b.U8();
                foreach (var e in envs)
                {
                    if (e.Vol != null) e.VolRate = b.U8();
                    if (e.Pitch != null) e.PitchRate = b.U8();
                    if (e.Decay > 0) e.DecayRate = b.U8();
                }
                foreach (var e in envs) e.Vib = b.U8();
                foreach (var e in envs) if (e.Vib > 0) e.VibDepth = b.U8();
                foreach (var e in envs) if (e.VibDepth > 0) e.VibDelay = b.U8();
                return patch;
            }

            private static byte ClampPan(int v)
            {
                if (v < 0) return 0;
                if (v > 128) return 128;
                return (byte)v;
            }
        }

        private sealed class R
        {
            private readonly byte[] d;
            public int p;
            public R(byte[] data) { d = data; }
            public int U8() => d[p++];
            public int I8() { int v = d[p++]; return v > 127 ? v - 256 : v; }
            public int VarInt()
            {
                int value = I8();
                int shifted = 0;
                while (value < 0)
                {
                    shifted = (shifted | (value & 0x7F)) << 7;
                    value = I8();
                }
                return shifted | value;
            }
            public sbyte[] Z(out int after, out int skip)
            {
                int n = 0;
                while (d[p + n] != 0) n++;
                var raw = new sbyte[n];
                for (int i = 0; i < n; i++) raw[i] = (sbyte)I8();
                p++;
                after = p;
                skip = n + 1;
                return raw;
            }
        }

        public sealed class Song
        {
            public float Duration;
            public int Notes;
            public int MaxVoices;
            public float Peak;
            public bool Dead;
            public bool Finished;

            private readonly Ev[] ev;
            private readonly int division;
            private readonly Voice[] voices = new Voice[48];

            internal int BendValue(int ch) => bend[ch];
            internal int BendRangeValue(int ch) => bendRange[ch];
            internal int ModValue(int ch) => mod[ch];
            internal int ExprValue(int ch) => expr[ch];
            internal int VolValue(int ch) => chVol[ch];
            internal int PanValue(int ch) => pan[ch];
            private readonly int[] bank = new int[16];
            private readonly int[] program = new int[16];
            private readonly int[] chVol = new int[16];
            private readonly int[] expr = new int[16];
            private readonly int[] pan = new int[16];
            private readonly int[] bend = new int[16];
            private readonly int[] bendRange = new int[16];
            private readonly int[] mod = new int[16];
            private int next;
            private double tick;
            private double samplesPerTick;
            private int tempo = 500000;
            private int clock;
            private int delayLeft;
            private float gain = 1f;
            private float fadeStep;

            public Song(byte[] midi)
            {
                for (int i = 0; i < 16; i++)
                {
                    chVol[i] = 12800;
                    expr[i] = 16383;
                    pan[i] = 8192;
                    bend[i] = 8192;
                    bendRange[i] = 256;
                }
                bank[9] = 128;
                for (int i = 0; i < voices.Length; i++) voices[i] = new Voice();
                ev = Parse(midi, out division, out Duration, out Notes);
                samplesPerTick = SamplesPerTick(Rate, tempo, division);
            }

            public void SetFade(int delayCycles, int fadeCycles)
            {
                delayLeft = Math.Max(0, delayCycles) * Rate / 50;
                int fadeSamples = Math.Max(0, fadeCycles) * Rate / 50;
                if (fadeSamples == 0) { gain = 1f; fadeStep = 0f; }
                else { gain = 0f; fadeStep = 1f / fadeSamples; }
            }

            public void Prepare()
            {
                int savedNext = next;
                double savedTick = tick;
                int savedTempo = tempo;
                var savedBank = (int[])bank.Clone();
                var savedProgram = (int[])program.Clone();
                for (int i = 0; i < ev.Length; i++)
                {
                    var e = ev[i];
                    int ch = e.Status & 15;
                    int kind = e.Status & 0xF0;
                    if (kind == 0xB0)
                    {
                        if (e.A == 0) bank[ch] = (e.B << 14) + (bank[ch] & ~0x1FC000);
                        else if (e.A == 32) bank[ch] = (bank[ch] & ~0x3F80) + (e.B << 7);
                    }
                    else if (kind == 0xC0) program[ch] = e.A & 127;
                    else if (kind == 0x90 && e.B > 0)
                    {
                        var patch = PatchFor(program[ch] + bank[ch]);
                        if (patch != null) SampleFor(patch.Key[e.A & 127]);
                    }
                }
                next = savedNext;
                tick = savedTick;
                tempo = savedTempo;
                Array.Copy(savedBank, bank, 16);
                Array.Copy(savedProgram, program, 16);
            }

            public void SkipToAudible()
            {
                int noteAt = -1;
                for (int i = next; i < ev.Length; i++)
                {
                    if ((ev[i].Status & 0xF0) == 0x90 && ev[i].B > 0) { noteAt = i; break; }
                }
                if (noteAt < 0) return;
                while (next < noteAt) Apply(ev[next++]);
                tick = ev[noteAt].Tick;
            }

            public void Fill(float[] data)
            {
                if (Dead)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                int activePeak = 0;
                float peak = 0f;
                for (int i = 0; i < data.Length; i++)
                {
                    if (delayLeft > 0) { delayLeft--; data[i] = 0f; continue; }
                    if (gain < 1f) { gain += fadeStep; if (gain > 1f) gain = 1f; }
                    tick += 1.0 / samplesPerTick;
                    while (next < ev.Length && ev[next].Tick <= tick) Apply(ev[next++]);
                    if ((clock++ % 220) == 0) TickEnvelopes();
                    int mix = 0;
                    int active = 0;
                    for (int v = 0; v < voices.Length; v++)
                    {
                        if (!voices[v].On) continue;
                        active++;
                        mix += voices[v].Render();
                    }
                    if (active > activePeak) activePeak = active;
                    data[i] = Clip(mix) * gain;
                    float a = data[i] < 0f ? -data[i] : data[i];
                    if (a > peak) peak = a;
                }
                if (activePeak > MaxVoices) MaxVoices = activePeak;
                if (peak > Peak) Peak = peak;
                if (next >= ev.Length && activePeak == 0 && delayLeft <= 0) Finished = true;
            }

            private static float Clip(int mix)
            {
                if (((mix + 8388608) & -16777216) != 0) mix = 0x7FFFFF ^ (mix >> 31);
                return (mix >> 8) / 32768f;
            }

            private void TickEnvelopes()
            {
                for (int i = 0; i < voices.Length; i++)
                {
                    if (!voices[i].On) continue;
                    voices[i].Tick(this);
                }
            }

            private void Apply(Ev e)
            {
                int ch = e.Status & 15;
                int kind = e.Status & 0xF0;
                if (e.Status == 0xFF)
                {
                    tempo = e.Extra;
                    samplesPerTick = SamplesPerTick(Rate, tempo, division);
                    return;
                }
                if (kind == 0xC0) { program[ch] = e.A & 127; return; }
                if (kind == 0xB0)
                {
                    int val = e.B & 127;
                    if (e.A == 0) bank[ch] = (val << 14) + (bank[ch] & ~0x1FC000);
                    else if (e.A == 32) bank[ch] = (bank[ch] & ~0x3F80) + (val << 7);
                    else if (e.A == 7) chVol[ch] = (val << 7) + (chVol[ch] & ~0x3F80);
                    else if (e.A == 39) chVol[ch] = (chVol[ch] & ~127) + val;
                    else if (e.A == 11) expr[ch] = (val << 7) + (expr[ch] & ~0x3F80);
                    else if (e.A == 43) expr[ch] = (expr[ch] & ~127) + val;
                    else if (e.A == 10) pan[ch] = (val << 7) + (pan[ch] & ~0x3F80);
                    else if (e.A == 42) pan[ch] = (pan[ch] & ~127) + val;
                    else if (e.A == 1) mod[ch] = (val << 7) + (mod[ch] & ~0x3F80);
                    else if (e.A == 33) mod[ch] = (mod[ch] & ~127) + val;
                    else if (e.A == 123 || e.A == 120) ReleaseChannel(ch);
                    return;
                }
                if (kind == 0xE0)
                {
                    bend[ch] = ((e.B & 127) << 7) | (e.A & 127);
                    return;
                }
                if (kind == 0x90 && e.B > 0) NoteOn(ch, e.A & 127, e.B & 127);
                else if (kind == 0x80 || (kind == 0x90 && e.B == 0)) NoteOff(ch, e.A & 127);
            }

            private void NoteOn(int ch, int note, int velocity)
            {
                var patch = PatchFor(program[ch] + bank[ch]);
                if (patch == null) return;
                var pcm = SampleFor(patch.Key[note]);
                if (pcm == null || pcm.Samples == null || pcm.Samples.Length == 0) return;
                int exclusive = patch.Exclusive[note];
                if (exclusive >= 0)
                {
                    for (int i = 0; i < voices.Length; i++)
                    {
                        if (voices[i].On && voices[i].Channel == ch && voices[i].Exclusive == exclusive && voices[i].Release < 0)
                            voices[i].Release = 0;
                    }
                }
                int slot = -1;
                int quiet = int.MaxValue;
                for (int i = 0; i < voices.Length; i++)
                {
                    if (!voices[i].On) { slot = i; break; }
                    if (voices[i].Level < quiet) { quiet = voices[i].Level; slot = i; }
                }
                var env = patch.Envs[note] ?? new Env();
                bool loop = patch.Tune[note] < 0 && pcm.End > pcm.Start;
                voices[slot] = new Voice
                {
                    On = true,
                    Channel = ch,
                    Note = note,
                    Pcm = pcm,
                    Loop = loop,
                    Ping = loop && pcm.PingPong,
                    Dir = 1,
                    LoopStart = pcm.Start << 8,
                    LoopEnd = pcm.End << 8,
                    Env = env,
                    Tune = patch.Tune[note],
                    VolByte = patch.Vol[note],
                    NotePan = patch.Pan[note],
                    Master = patch.Master,
                    Velocity = velocity,
                    Exclusive = exclusive,
                    Release = -1,
                };
                voices[slot].Retune(this);
                voices[slot].Relevel(this);
            }

            private void NoteOff(int ch, int note)
            {
                for (int i = 0; i < voices.Length; i++)
                {
                    if (voices[i].On && voices[i].Release < 0 && voices[i].Channel == ch && voices[i].Note == note)
                    {
                        voices[i].Release = 0;
                        return;
                    }
                }
            }

            private void ReleaseChannel(int ch)
            {
                for (int i = 0; i < voices.Length; i++)
                    if (voices[i].On && voices[i].Channel == ch && voices[i].Release < 0)
                        voices[i].Release = 0;
            }

            private static double SamplesPerTick(int rate, int tempoUs, int div)
            {
                if (div < 1) div = 960;
                return rate * (tempoUs / 1000000.0) / div;
            }

            private static Ev[] Parse(byte[] midi, out int division, out float duration, out int notes)
            {
                division = 960;
                duration = 0f;
                notes = 0;
                if (midi == null || midi.Length < 14 || midi[0] != (byte)'M') return Array.Empty<Ev>();
                int pos = 8;
                int ntracks = Be16(midi, pos + 2);
                division = Be16(midi, pos + 4);
                if (division >= 0x8000) division = 960;
                pos = 14;
                var list = new List<Ev>();
                int seq = 0;
                int lastTick = 0;
                int tempo = 500000;
                for (int t = 0; t < ntracks && pos + 8 <= midi.Length; t++)
                {
                    int len = Be32(midi, pos + 4);
                    int end = Math.Min(midi.Length, pos + 8 + len);
                    int i = pos + 8;
                    int tick = 0;
                    int running = 0;
                    while (i < end)
                    {
                        tick += Var(midi, ref i);
                        if (i >= end) break;
                        int status;
                        if (midi[i] >= 128) { status = midi[i++]; running = status; }
                        else status = running;
                        int kind = status & 0xF0;
                        if (status == 0xFF)
                        {
                            if (i >= end) break;
                            int meta = midi[i++];
                            int n = Var(midi, ref i);
                            if (meta == 0x51 && n >= 3 && i + 2 < midi.Length)
                            {
                                tempo = (midi[i] << 16) | (midi[i + 1] << 8) | midi[i + 2];
                                list.Add(new Ev { Tick = tick, Seq = seq++, Status = 0xFF, Extra = tempo });
                            }
                            if (meta == 0x2F) break;
                            i += n;
                        }
                        else if (kind == 0xC0 || kind == 0xD0)
                        {
                            if (i >= end) break;
                            list.Add(new Ev { Tick = tick, Seq = seq++, Status = status, A = midi[i++] });
                        }
                        else if (status == 0xF0 || status == 0xF7)
                        {
                            i += Var(midi, ref i);
                        }
                        else
                        {
                            if (i + 1 >= end) break;
                            int a = midi[i++];
                            int b = midi[i++];
                            list.Add(new Ev { Tick = tick, Seq = seq++, Status = status, A = a, B = b });
                            if (kind == 0x90 && b > 0) notes++;
                        }
                    }
                    if (tick > lastTick) lastTick = tick;
                    pos = end;
                }
                list.Sort((x, y) =>
                {
                    int cmp = x.Tick.CompareTo(y.Tick);
                    return cmp != 0 ? cmp : x.Seq.CompareTo(y.Seq);
                });
                duration = division > 0 ? lastTick * (tempo / 1000000f) / division : 0f;
                return list.ToArray();
            }

            private static int Be16(byte[] d, int i) => (d[i] << 8) | d[i + 1];
            private static int Be32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
            private static int Var(byte[] d, ref int i)
            {
                int v = 0;
                int guard = 0;
                while (i < d.Length && guard++ < 4)
                {
                    int b = d[i++];
                    v = (v << 7) | (b & 0x7F);
                    if (b < 128) break;
                }
                return v;
            }

            private struct Ev
            {
                public int Tick, Seq, Status, A, B, Extra;
            }
        }

        private sealed class Voice
        {
            public bool On;
            public int Channel, Note, Exclusive;
            public OsrsVorbis.Pcm Pcm;
            public bool Loop, Ping;
            public int Dir;
            public int Pos, LoopStart, LoopEnd;
            public int Step;
            public Env Env;
            public short Tune;
            public sbyte VolByte;
            public byte NotePan;
            public int Master, Velocity;
            public int Release;
            public int Level;
            public int VolPos, VolIdx, RelPos, RelIdx, DecayPos, VibPhase, VibCount;

            public int Render()
            {
                var samples = Pcm.Samples;
                int len = samples.Length << 8;
                if (Pos < 0 || Pos >= len) { On = false; return 0; }
                int idx = Pos >> 8;
                int frac = Pos & 255;
                int s0 = samples[idx];
                int s1 = idx + 1 < samples.Length ? samples[idx + 1] : s0;
                int interp = (s0 << 8) + (s1 - s0) * frac;
                Pos += Step * Dir;
                if (Loop && LoopEnd > LoopStart)
                {
                    if (Ping)
                    {
                        if (Dir > 0 && Pos >= LoopEnd) { Pos = LoopEnd - (Pos - LoopEnd); Dir = -1; }
                        else if (Dir < 0 && Pos < LoopStart) { Pos = LoopStart + (LoopStart - Pos); Dir = 1; }
                    }
                    else if (Pos >= LoopEnd)
                    {
                        int span = LoopEnd - LoopStart;
                        Pos = LoopStart + (Pos - LoopStart) % span;
                    }
                }
                else if (Pos >= len || Pos < 0)
                {
                    On = false;
                }
                return (interp * Level) >> 6;
            }

            public void Tick(Song song)
            {
                var env = Env;
                double scale = ((Note - 60) << 8) * 5.086263020833333E-6;
                if (env.Vol != null)
                {
                    VolPos += env.VolRate > 0
                        ? (int)(128.0 * Math.Pow(2.0, env.VolRate * scale) + 0.5)
                        : 128;
                    while (VolIdx < env.Vol.Length - 2 && VolPos > (env.Vol[VolIdx + 2] << 8)) VolIdx += 2;
                    if (VolIdx == env.Vol.Length - 2 && env.Vol[VolIdx + 1] == 0) { On = false; return; }
                }
                if (env.Decay > 0)
                {
                    DecayPos += env.DecayRate > 0
                        ? (int)(128.0 * Math.Pow(2.0, env.DecayRate * scale) + 0.5)
                        : 128;
                }
                if (env.Vib > 0) VibPhase += env.Vib;
                VibCount++;
                if (Release >= 0)
                {
                    if (env.Pitch == null) { On = false; return; }
                    RelPos += env.PitchRate > 0
                        ? (int)(128.0 * Math.Pow(2.0, scale * env.PitchRate) + 0.5)
                        : 128;
                    while (RelIdx < env.Pitch.Length - 2 && RelPos > (env.Pitch[RelIdx + 2] << 8)) RelIdx += 2;
                    if (RelIdx >= env.Pitch.Length - 2) { On = false; return; }
                }
                Retune(song);
                Relevel(song);
            }

            public void Retune(Song song)
            {
                int units = (Note << 8) - (Tune & 32767);
                units += (BendOf(song) - 8192) * BendRangeOf(song) >> 12;
                var env = Env;
                if (env.Vib > 0 && (env.VibDepth > 0 || ModOf(song) > 0))
                {
                    int depth = env.VibDepth << 2;
                    int delay = env.VibDelay << 1;
                    if (VibCount < delay && delay > 0) depth = depth * VibCount / delay;
                    depth += ModOf(song) >> 7;
                    units += (int)(Math.Sin((VibPhase & 511) * 0.01227184630308513) * depth);
                }
                int step = (int)(Pcm.Rate * 256.0 * Math.Pow(2.0, units * 3.255208333333333E-4) / Rate + 0.5);
                Step = step < 1 ? 1 : step;
            }

            public void Relevel(Song song)
            {
                int shaped = (Velocity * Velocity * VolByte * Master + 1024) >> 11;
                int v = (ExprOf(song) * ChVolOf(song) + 4096) >> 13;
                v = (v * v + 16384) >> 15;
                v = (v * shaped + 16384) >> 15;
                v = (v * 256 + 128) >> 8;
                var env = Env;
                if (env.Decay > 0)
                    v = (int)(v * Math.Pow(0.5, env.Decay * DecayPos * 1.953125E-5) + 0.5);
                if (env.Vol != null) v = (v * EnvLevel(env.Vol, VolIdx, VolPos) + 32) >> 6;
                if (Release >= 0 && env.Pitch != null) v = (v * EnvLevel(env.Pitch, RelIdx, RelPos) + 32) >> 6;
                int p = PanOf(song);
                int sided = p < 8192
                    ? (p * NotePan + 32) >> 6
                    : 16384 - (((128 - NotePan) * (16384 - p) + 32) >> 6);
                double left = sided < 0 ? 1 : Math.Sqrt((16384 - sided) * 1.220703125E-4);
                double right = sided < 0 ? 1 : Math.Sqrt(sided * 1.220703125E-4);
                v = (int)(v * (left + right) * 0.5 + 0.5);
                if (v < 0) v = 0;
                Level = v;
            }

            private static int EnvLevel(byte[] curve, int idx, int pos)
            {
                int level = (sbyte)curve[idx + 1];
                if (idx < curve.Length - 2)
                {
                    int t0 = curve[idx] << 8;
                    int t1 = curve[idx + 2] << 8;
                    int next = (sbyte)curve[idx + 3];
                    if (t1 != t0) level += (next - level) * (pos - t0) / (t1 - t0);
                }
                return level;
            }

            private int BendOf(Song song) => song.BendValue(Channel);
            private int BendRangeOf(Song song) => song.BendRangeValue(Channel);
            private int ModOf(Song song) => song.ModValue(Channel);
            private int ExprOf(Song song) => song.ExprValue(Channel);
            private int ChVolOf(Song song) => song.VolValue(Channel);
            private int PanOf(Song song) => song.PanValue(Channel);
        }
    }
}
