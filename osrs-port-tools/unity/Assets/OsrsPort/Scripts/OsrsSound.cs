using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// OSRS synth sound effects. The exporter copies the cache's raw sound effects into
    /// StreamingAssets/world/sounds.bin.gz; they are synthesised here on first use with the same
    /// integer maths as the client (22050 Hz, 8-bit). The server sends `SND synth loops delay`
    /// for sounds the player hears directly and `SNDA synth x z level loops delay range` for
    /// sounds played at a tile.
    /// </summary>
    public class OsrsSound : MonoBehaviour
    {
        public const int SampleRate = 22050;
        private const float ClientCycleSeconds = 0.02f;
        private const int MaxVoices = 16;

        public static OsrsSound Instance { get; private set; }
        /// <summary>How many sounds have been started, and the synth id of the latest one.</summary>
        public int Plays { get; private set; }
        public int LastSynth { get; private set; }

        [Range(0f, 1f)] public float volume = 0.6f;

        private Dictionary<int, byte[]> raw;
        private readonly Dictionary<int, AudioClip> clips = new Dictionary<int, AudioClip>();
        private readonly List<AudioSource> voices = new List<AudioSource>();
        private bool loadFailed;

        public static OsrsSound Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("OsrsSound");
            DontDestroyOnLoad(go);
            return go.AddComponent<OsrsSound>();
        }

        private void Awake()
        {
            Instance = this;
        }

        private static void EnsureListener()
        {
            var listener = FindFirstObjectByType<AudioListener>();
            if (listener != null && listener.isActiveAndEnabled) return;
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
        }

        /// <summary>Plays a sound for the local player (not positional).</summary>
        public void Play(int synth, int loops, int delayCycles) => PlayAt(synth, loops, delayCycles, null, 0f);

        /// <summary>Plays a sound at a world position that fades out over <paramref name="rangeTiles"/>.</summary>
        public void PlayAt(int synth, int loops, int delayCycles, Vector3? position, float rangeTiles)
        {
            Plays++;
            LastSynth = synth;
            var clip = Clip(synth);
            if (clip == null) return;
            EnsureListener();
            var voice = FreeVoice();
            voice.transform.position = position ?? Vector3.zero;
            voice.spatialBlend = position.HasValue ? 1f : 0f;
            voice.maxDistance = Mathf.Max(rangeTiles, 1f) + 1f;
            voice.minDistance = 1f;
            voice.rolloffMode = AudioRolloffMode.Linear;
            voice.clip = clip;
            voice.volume = volume;
            voice.loop = false;
            float delay = Mathf.Max(0, delayCycles) * ClientCycleSeconds;
            voice.PlayDelayed(delay);
            if (loops > 1) StartCoroutine(Repeat(voice, clip, loops - 1, delay + clip.length));
        }

        private System.Collections.IEnumerator Repeat(AudioSource voice, AudioClip clip, int times, float wait)
        {
            for (int i = 0; i < times; i++)
            {
                yield return new WaitForSeconds(wait);
                if (voice == null || voice.clip != clip) yield break;
                voice.Play();
                wait = clip.length;
            }
        }

        private AudioSource FreeVoice()
        {
            foreach (var v in voices)
                if (v != null && !v.isPlaying) return v;
            if (voices.Count < MaxVoices)
            {
                var go = new GameObject("voice");
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.dopplerLevel = 0f;
                voices.Add(src);
                return src;
            }
            var oldest = voices[0];
            voices.RemoveAt(0);
            voices.Add(oldest);
            oldest.Stop();
            return oldest;
        }

        public AudioClip Clip(int synth)
        {
            if (clips.TryGetValue(synth, out var cached)) return cached;
            AudioClip clip = null;
            if (Load() && raw.TryGetValue(synth, out var data))
            {
                try
                {
                    var pcm = new SoundEffect(data).Mix();
                    if (pcm.Length > 0)
                    {
                        var f = new float[pcm.Length];
                        for (int i = 0; i < pcm.Length; i++) f[i] = (sbyte)pcm[i] / 128f;
                        clip = AudioClip.Create("synth_" + synth, f.Length, 1, SampleRate, false);
                        clip.SetData(f, 0);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"OsrsPort: sound {synth} failed to synthesise: {e.Message}");
                }
            }
            clips[synth] = clip;
            return clip;
        }

        private bool Load()
        {
            if (raw != null) return true;
            if (loadFailed) return false;
            string path = Path.Combine(Application.streamingAssetsPath, "world", "sounds.bin.gz");
            if (!File.Exists(path))
            {
                loadFailed = true;
                Debug.LogWarning("OsrsPort: no sounds.bin.gz - run exporter/export_sounds.py to hear OSRS sounds.");
                return false;
            }
            raw = new Dictionary<int, byte[]>();
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (new string(r.ReadChars(4)) != "OSN1") throw new InvalidDataException("sounds.bin.gz: bad magic");
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadInt32();
                    int len = r.ReadInt32();
                    raw[id] = r.ReadBytes(len);
                }
            }
            Debug.Log($"OsrsPort: {raw.Count} sound effects loaded");
            return true;
        }

        // ---- cache format + synthesis (the client's SoundEffect / Instrument / SoundEnvelope / AudioFilter) ----

        private sealed class Reader
        {
            private readonly byte[] d;
            public int p;
            public Reader(byte[] data) { d = data; }
            public int U8() => d[p++];
            public int Peek() => d[p];
            public int U16() { int v = (d[p] << 8) | d[p + 1]; p += 2; return v; }
            public int I32() { int v = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; p += 4; return v; }
            public int USmart() => d[p] < 128 ? U8() : U16() - 32768;
            public int Smart() => d[p] < 128 ? U8() - 64 : U16() - 49152;
            public int Remaining => d.Length - p;
        }

        /// <summary>Index-4 synth as the music engine uses it: signed bytes plus the loop, in samples.</summary>
        public static bool TryMix(byte[] data, out byte[] pcm, out int loopStart, out int loopEnd)
        {
            pcm = null;
            loopStart = 0;
            loopEnd = 0;
            if (data == null || data.Length == 0) return false;
            try
            {
                var fx = new SoundEffect(data);
                pcm = fx.Mix();
                if (pcm == null || pcm.Length == 0) return false;
                loopStart = fx.LoopStart;
                loopEnd = fx.LoopEnd;
                if (loopStart < 0) loopStart = 0;
                if (loopEnd > pcm.Length) loopEnd = pcm.Length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryMixId(int id, out byte[] pcm, out int loopStart, out int loopEnd)
        {
            pcm = null;
            loopStart = 0;
            loopEnd = 0;
            if (!Load() || !raw.TryGetValue(id, out var data)) return false;
            return TryMix(data, out pcm, out loopStart, out loopEnd);
        }

        private sealed class SoundEffect
        {
            private readonly Instrument[] instruments = new Instrument[10];
            public int LoopStart;
            public int LoopEnd;

            public SoundEffect(byte[] data)
            {
                var r = new Reader(data);
                for (int i = 0; i < 10; i++)
                {
                    if (r.Peek() != 0) instruments[i] = new Instrument(r);
                    else r.p++;
                }
                if (r.Remaining >= 4)
                {
                    LoopStart = r.U16() * SampleRate / 1000;
                    LoopEnd = r.U16() * SampleRate / 1000;
                }
            }

            public byte[] Mix()
            {
                int duration = 0;
                foreach (var ins in instruments)
                    if (ins != null && ins.duration + ins.offset > duration) duration = ins.duration + ins.offset;
                if (duration == 0) return Array.Empty<byte>();
                var output = new byte[duration * SampleRate / 1000];
                foreach (var ins in instruments)
                {
                    if (ins == null) continue;
                    int n = ins.duration * SampleRate / 1000;
                    int start = ins.offset * SampleRate / 1000;
                    var s = ins.Synthesize(n, ins.duration);
                    for (int i = 0; i < n && i + start < output.Length; i++)
                    {
                        int v = (s[i] >> 8) + (sbyte)output[i + start];
                        if (((v + 128) & -256) != 0) v = (v >> 31) ^ 127;
                        output[i + start] = (byte)(sbyte)v;
                    }
                }
                return output;
            }
        }

        private sealed class Envelope
        {
            public int segments = 2;
            public int[] durations = { 0, 65535 };
            public int[] phases = { 0, 65535 };
            public int start, end, form;
            private int ticks, phaseIndex, step, amplitude, max;

            public void Decode(Reader r)
            {
                form = r.U8();
                start = r.I32();
                end = r.I32();
                DecodeSegments(r);
            }

            public void DecodeSegments(Reader r)
            {
                segments = r.U8();
                durations = new int[segments];
                phases = new int[segments];
                for (int i = 0; i < segments; i++)
                {
                    durations[i] = r.U16();
                    phases[i] = r.U16();
                }
            }

            public void Reset() { ticks = 0; phaseIndex = 0; step = 0; amplitude = 0; max = 0; }

            public int Step(int period)
            {
                if (max >= ticks)
                {
                    amplitude = phases[phaseIndex++] << 15;
                    if (phaseIndex >= segments) phaseIndex = segments - 1;
                    ticks = (int)(durations[phaseIndex] / 65536.0 * period);
                    if (ticks > max) step = ((phases[phaseIndex] << 15) - amplitude) / (ticks - max);
                }
                amplitude += step;
                max++;
                return (amplitude - step) >> 15;
            }
        }

        private sealed class Filter
        {
            public readonly int[] pairs = new int[2];
            private readonly int[,,] filterPhases = new int[2, 2, 4];
            private readonly int[,,] magnitudes = new int[2, 2, 4];
            private readonly int[] unity = new int[2];
            public static readonly float[,] Coefficients = new float[2, 8];
            public static readonly int[,] CoefficientsInt = new int[2, 8];
            public static float forwardMultiplier;
            public static int forwardMultiplierInt;

            public void Decode(Reader r, Envelope env)
            {
                int count = r.U8();
                pairs[0] = count >> 4;
                pairs[1] = count & 15;
                if (count == 0)
                {
                    unity[0] = unity[1] = 0;
                    return;
                }
                unity[0] = r.U16();
                unity[1] = r.U16();
                int migrated = r.U8();
                for (int dir = 0; dir < 2; dir++)
                    for (int p = 0; p < pairs[dir]; p++)
                    {
                        filterPhases[dir, 0, p] = r.U16();
                        magnitudes[dir, 0, p] = r.U16();
                    }
                for (int dir = 0; dir < 2; dir++)
                    for (int p = 0; p < pairs[dir]; p++)
                    {
                        if ((migrated & (1 << (dir * 4) << p)) != 0)
                        {
                            filterPhases[dir, 1, p] = r.U16();
                            magnitudes[dir, 1, p] = r.U16();
                        }
                        else
                        {
                            filterPhases[dir, 1, p] = filterPhases[dir, 0, p];
                            magnitudes[dir, 1, p] = magnitudes[dir, 0, p];
                        }
                    }
                if (migrated != 0 || unity[1] != unity[0]) env.DecodeSegments(r);
            }

            private float Magnitude(int dir, int i, float t)
            {
                float a = magnitudes[dir, 0, i] + t * (magnitudes[dir, 1, i] - magnitudes[dir, 0, i]);
                a *= 0.0015258789f;
                return 1f - (float)Math.Pow(10.0, -a / 20f);
            }

            private float Phase(int dir, int i, float t)
            {
                float f = filterPhases[dir, 0, i] + t * (filterPhases[dir, 1, i] - filterPhases[dir, 0, i]);
                f *= 1.2207031E-4f;
                float hz = 32.703197f * (float)Math.Pow(2.0, f);
                return hz * 3.1415927f / 11025f;
            }

            public int Compute(int dir, float t)
            {
                if (dir == 0)
                {
                    float u = unity[0] + (unity[1] - unity[0]) * t;
                    u *= 0.0030517578f;
                    forwardMultiplier = (float)Math.Pow(0.1, u / 20f);
                    forwardMultiplierInt = (int)(forwardMultiplier * 65536f);
                }
                if (pairs[dir] == 0) return 0;
                float m = Magnitude(dir, 0, t);
                Coefficients[dir, 0] = -2f * m * (float)Math.Cos(Phase(dir, 0, t));
                Coefficients[dir, 1] = m * m;
                for (int p = 1; p < pairs[dir]; p++)
                {
                    m = Magnitude(dir, p, t);
                    float a = -2f * m * (float)Math.Cos(Phase(dir, p, t));
                    float b = m * m;
                    Coefficients[dir, p * 2 + 1] = Coefficients[dir, p * 2 - 1] * b;
                    Coefficients[dir, p * 2] = Coefficients[dir, p * 2 - 1] * a + Coefficients[dir, p * 2 - 2] * b;
                    for (int j = p * 2 - 1; j >= 2; j--)
                        Coefficients[dir, j] += Coefficients[dir, j - 1] * a + Coefficients[dir, j - 2] * b;
                    Coefficients[dir, 1] += Coefficients[dir, 0] * a + b;
                    Coefficients[dir, 0] += a;
                }
                if (dir == 0)
                    for (int j = 0; j < pairs[0] * 2; j++) Coefficients[0, j] *= forwardMultiplier;
                for (int j = 0; j < pairs[dir] * 2; j++) CoefficientsInt[dir, j] = (int)(Coefficients[dir, j] * 65536f);
                return pairs[dir] * 2;
            }
        }

        private sealed class Instrument
        {
            private static readonly int[] Noise = new int[32768];
            private static readonly int[] Sine = new int[32768];
            private static int[] samples = new int[220500];

            static Instrument()
            {
                var rng = new System.Random(0);
                for (int i = 0; i < 32768; i++) Noise[i] = (rng.Next() & 2) - 1;
                for (int i = 0; i < 32768; i++) Sine[i] = (int)(Math.Sin(i / 5215.1903) * 16384.0);
            }

            private readonly Envelope pitch = new Envelope(), volume = new Envelope();
            private Envelope pitchModifier, pitchModifierAmplitude, volumeMultiplier, volumeMultiplierAmplitude, release, attack;
            private readonly int[] oscVolume = new int[5], oscPitch = new int[5], oscDelay = new int[5];
            private readonly int delayTime, delayDecay;
            public readonly int duration, offset;
            private readonly Filter filter = new Filter();
            private readonly Envelope filterEnvelope = new Envelope();

            public Instrument(Reader r)
            {
                pitch.Decode(r);
                volume.Decode(r);
                if (r.Peek() != 0)
                {
                    pitchModifier = new Envelope(); pitchModifier.Decode(r);
                    pitchModifierAmplitude = new Envelope(); pitchModifierAmplitude.Decode(r);
                }
                else r.p++;
                if (r.Peek() != 0)
                {
                    volumeMultiplier = new Envelope(); volumeMultiplier.Decode(r);
                    volumeMultiplierAmplitude = new Envelope(); volumeMultiplierAmplitude.Decode(r);
                }
                else r.p++;
                if (r.Peek() != 0)
                {
                    release = new Envelope(); release.Decode(r);
                    attack = new Envelope(); attack.Decode(r);
                }
                else r.p++;
                for (int i = 0; i < 10; i++)
                {
                    int vol = r.USmart();
                    if (vol == 0) break;
                    int pit = r.Smart();
                    int del = r.USmart();
                    if (i < 5) { oscVolume[i] = vol; oscPitch[i] = pit; oscDelay[i] = del; }
                }
                delayTime = r.USmart();
                delayDecay = r.USmart();
                duration = r.U16();
                offset = r.U16();
                filter.Decode(r, filterEnvelope);
            }

            private static int Wave(int phase, int amplitude, int form)
            {
                switch (form)
                {
                    case 1: return (phase & 32767) < 16384 ? amplitude : -amplitude;
                    case 2: return (Sine[phase & 32767] * amplitude) >> 14;
                    case 3: return ((amplitude * (phase & 32767)) >> 14) - amplitude;
                    case 4: return amplitude * Noise[(phase / 2607) & 32767];
                    default: return 0;
                }
            }

            public int[] Synthesize(int n, int ms)
            {
                if (samples.Length < n) samples = new int[n];
                Array.Clear(samples, 0, n);
                if (ms < 10) return samples;
                double perMs = n / (ms + 0.0);
                pitch.Reset();
                volume.Reset();
                int pmStep = 0, pmBase = 0, pmPhase = 0;
                if (pitchModifier != null)
                {
                    pitchModifier.Reset(); pitchModifierAmplitude.Reset();
                    pmStep = (int)((pitchModifier.end - pitchModifier.start) * 32.768 / perMs);
                    pmBase = (int)(pitchModifier.start * 32.768 / perMs);
                }
                int vmStep = 0, vmBase = 0, vmPhase = 0;
                if (volumeMultiplier != null)
                {
                    volumeMultiplier.Reset(); volumeMultiplierAmplitude.Reset();
                    vmStep = (int)((volumeMultiplier.end - volumeMultiplier.start) * 32.768 / perMs);
                    vmBase = (int)(volumeMultiplier.start * 32.768 / perMs);
                }
                var phases = new int[5];
                var delays = new int[5];
                var volSteps = new int[5];
                var pitchSteps = new int[5];
                var pitchBase = new int[5];
                for (int i = 0; i < 5; i++)
                {
                    if (oscVolume[i] == 0) continue;
                    delays[i] = (int)(oscDelay[i] * perMs);
                    volSteps[i] = (oscVolume[i] << 14) / 100;
                    pitchSteps[i] = (int)((pitch.end - pitch.start) * 32.768 * Math.Pow(1.0057929410678534, oscPitch[i]) / perMs);
                    pitchBase[i] = (int)(pitch.start * 32.768 / perMs);
                }
                for (int s = 0; s < n; s++)
                {
                    int p = pitch.Step(n);
                    int v = volume.Step(n);
                    if (pitchModifier != null)
                    {
                        int a = pitchModifier.Step(n);
                        int b = pitchModifierAmplitude.Step(n);
                        p += Wave(pmPhase, b, pitchModifier.form) >> 1;
                        pmPhase = pmPhase + pmBase + ((a * pmStep) >> 16);
                    }
                    if (volumeMultiplier != null)
                    {
                        int a = volumeMultiplier.Step(n);
                        int b = volumeMultiplierAmplitude.Step(n);
                        v = (v * ((Wave(vmPhase, b, volumeMultiplier.form) >> 1) + 32768)) >> 15;
                        vmPhase = vmPhase + vmBase + ((a * vmStep) >> 16);
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        if (oscVolume[i] == 0) continue;
                        int idx = s + delays[i];
                        if (idx < n)
                        {
                            samples[idx] += Wave(phases[i], (v * volSteps[i]) >> 15, pitch.form);
                            phases[i] += ((p * pitchSteps[i]) >> 16) + pitchBase[i];
                        }
                    }
                }
                if (release != null)
                {
                    release.Reset(); attack.Reset();
                    int counter = 0;
                    bool on = true;
                    for (int s = 0; s < n; s++)
                    {
                        int rs = release.Step(n);
                        int at = attack.Step(n);
                        int threshold = on
                            ? ((rs * (release.end - release.start)) >> 8) + release.start
                            : ((at * (release.end - release.start)) >> 8) + release.start;
                        counter += 256;
                        if (counter >= threshold) { counter = 0; on = !on; }
                        if (on) samples[s] = 0;
                    }
                }
                if (delayTime > 0 && delayDecay > 0)
                {
                    int d = (int)(delayTime * perMs);
                    for (int s = d; s < n; s++) samples[s] += samples[s - d] * delayDecay / 100;
                }
                if (filter.pairs[0] > 0 || filter.pairs[1] > 0) ApplyFilter(n);
                for (int s = 0; s < n; s++)
                {
                    if (samples[s] < -32768) samples[s] = -32768;
                    if (samples[s] > 32767) samples[s] = 32767;
                }
                return samples;
            }

            private void ApplyFilter(int n)
            {
                filterEnvelope.Reset();
                int t = filterEnvelope.Step(n + 1);
                int fwd = filter.Compute(0, t / 65536f);
                int back = filter.Compute(1, t / 65536f);
                if (n < fwd + back) return;
                var c0 = Filter.CoefficientsInt;
                int s = 0;
                int limit = Math.Min(back, n - fwd);
                while (s < limit)
                {
                    int acc = (int)((long)samples[s + fwd] * Filter.forwardMultiplierInt >> 16);
                    for (int k = 0; k < fwd; k++) acc += (int)((long)samples[s + fwd - 1 - k] * c0[0, k] >> 16);
                    for (int k = 0; k < s; k++) acc -= (int)((long)samples[s - 1 - k] * c0[1, k] >> 16);
                    samples[s] = acc;
                    t = filterEnvelope.Step(n + 1);
                    s++;
                }
                int chunk = 128;
                while (true)
                {
                    if (chunk > n - fwd) chunk = n - fwd;
                    while (s < chunk)
                    {
                        int acc = (int)((long)samples[s + fwd] * Filter.forwardMultiplierInt >> 16);
                        for (int k = 0; k < fwd; k++) acc += (int)((long)samples[s + fwd - 1 - k] * c0[0, k] >> 16);
                        for (int k = 0; k < back; k++) acc -= (int)((long)samples[s - 1 - k] * c0[1, k] >> 16);
                        samples[s] = acc;
                        t = filterEnvelope.Step(n + 1);
                        s++;
                    }
                    if (s >= n - fwd)
                    {
                        while (s < n)
                        {
                            int acc = 0;
                            for (int k = s + fwd - n; k < fwd; k++) acc += (int)((long)samples[s + fwd - 1 - k] * c0[0, k] >> 16);
                            for (int k = 0; k < back; k++) acc -= (int)((long)samples[s - 1 - k] * c0[1, k] >> 16);
                            samples[s] = acc;
                            filterEnvelope.Step(n + 1);
                            s++;
                        }
                        break;
                    }
                    fwd = filter.Compute(0, t / 65536f);
                    back = filter.Compute(1, t / 65536f);
                    chunk += 128;
                }
            }
        }
    }
}
