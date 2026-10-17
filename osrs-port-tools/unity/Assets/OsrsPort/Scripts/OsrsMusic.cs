using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Plays the cache MIDI the server sends as <c>SONG</c> (MidiSongV2) and <c>JINGLE</c>
    /// (MidiJingle). Tracks are standard MIDI exported from the cache; a small General MIDI
    /// wavetable soundfont synthesises them.
    /// </summary>
    public class OsrsMusic : MonoBehaviour
    {
        public const int StopMidi = 147;

        public static OsrsMusic Instance { get; private set; }
        public int Plays { get; private set; }
        public int LastTrack { get; private set; } = -1;
        public int LastJingle { get; private set; } = -1;
        public int Voices { get; private set; }
        public float Peak { get; private set; }
        public float Duration { get; private set; }

        private readonly Dictionary<int, byte[]> songs = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> jingles = new Dictionary<int, byte[]>();
        private AudioSource songSource;
        private AudioSource jingleSource;
        private Play liveSong;
        private Play liveJingle;
        private bool loaded;
        private bool missing;

        private static readonly float[][] waves = BuildWaves();

        public static OsrsMusic Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("OsrsMusic");
            DontDestroyOnLoad(go);
            return go.AddComponent<OsrsMusic>();
        }

        private void Awake()
        {
            Instance = this;
            songSource = gameObject.AddComponent<AudioSource>();
            jingleSource = gameObject.AddComponent<AudioSource>();
            songSource.playOnAwake = jingleSource.playOnAwake = false;
            songSource.spatialBlend = jingleSource.spatialBlend = 0f;
            songSource.volume = jingleSource.volume = 1f;
            var listener = FindAnyObjectByType<AudioListener>();
            if (listener == null)
            {
                var cam = Camera.main;
                if (cam != null && cam.GetComponent<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
            }
        }

        /// <summary>MidiSongV2. Id 147 is the cache's stop_music track.</summary>
        public void PlaySong(int id, int fadeOutDelay, int fadeOutSpeed, int fadeInDelay, int fadeInSpeed)
        {
            Load();
            if (id == StopMidi)
            {
                if (liveSong != null) liveSong.dead = true;
                songSource.Stop();
                Debug.Log("OsrsPort: music stop");
                return;
            }
            if (!songs.TryGetValue(id, out var midi))
            {
                Debug.Log("OsrsPort: music missing track " + id);
                return;
            }
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            Measure(Play.Start(id, midi, false, 0, 0), rate);
            if (liveSong != null) liveSong.dead = true;
            liveSong = Play.Start(id, midi, false, fadeInDelay, Mathf.Max(fadeInSpeed, fadeOutSpeed));
            var clip = AudioClip.Create("song-" + id, Mathf.Max(rate, (int)(liveSong.duration * rate)), 1, rate, true, liveSong.OnPcm);
            songSource.volume = 1f;
            songSource.clip = clip;
            songSource.loop = false;
            songSource.Play();
            Plays++;
            LastTrack = id;
            Duration = liveSong.duration;
            Debug.Log($"OsrsPort: music track={id} voices={Voices} peak={Peak:0.000} dur={Duration:0.0}s notes={liveSong.notes} plays={Plays}");
        }

        public void PlayJingle(int id)
        {
            Load();
            if (!jingles.TryGetValue(id, out var midi))
            {
                Debug.Log("OsrsPort: music missing jingle " + id);
                return;
            }
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            Measure(Play.Start(id, midi, true, 0, 0), rate);
            if (liveJingle != null) liveJingle.dead = true;
            liveJingle = Play.Start(id, midi, true, 0, 0);
            var clip = AudioClip.Create("jingle-" + id, Mathf.Max(rate, (int)(liveJingle.duration * rate)), 1, rate, true, liveJingle.OnPcm);
            jingleSource.clip = clip;
            jingleSource.loop = false;
            jingleSource.Play();
            songSource.volume = 0.3f;
            Plays++;
            LastJingle = id;
            Debug.Log($"OsrsPort: jingle id={id} voices={Voices} peak={Peak:0.000} dur={liveJingle.duration:0.0}s notes={liveJingle.notes} plays={Plays}");
        }

        /// <summary>Renders half a second from the first note, so a long rest does not look like silence.</summary>
        private void Measure(Play play, int rate)
        {
            play.SkipToAudible(rate);
            var buf = new float[rate / 2];
            play.Fill(buf);
            float peak = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > peak) peak = a;
            }
            Peak = peak;
            Voices = play.MaxVoices;
        }

        private void Load()
        {
            if (loaded || missing) return;
            string path = Path.Combine(Application.streamingAssetsPath, "world", "music.bin.gz");
            if (!File.Exists(path))
            {
                missing = true;
                Debug.LogWarning("OsrsPort: no music.bin.gz - run exporter/export_music.py");
                return;
            }
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (new string(r.ReadChars(4)) != "OSM1") throw new InvalidDataException("music.bin.gz: bad magic");
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadInt32();
                    int kind = r.ReadByte();
                    int len = r.ReadInt32();
                    var midi = r.ReadBytes(len);
                    (kind == 1 ? jingles : songs)[id] = midi;
                }
            }
            loaded = true;
            Debug.Log($"OsrsPort: {songs.Count} songs, {jingles.Count} jingles");
        }

        private static float[][] BuildWaves()
        {
            var table = new float[6][];
            for (int w = 0; w < 6; w++) table[w] = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 256f;
                float sine = Mathf.Sin(t * Mathf.PI * 2f);
                table[0][i] = sine;
                table[1][i] = t < 0.5f ? (4f * t - 1f) : (3f - 4f * t);
                table[2][i] = t < 0.5f ? 0.7f : -0.7f;
                table[3][i] = 2f * t - 1f;
                table[4][i] = (sine + 0.5f * Mathf.Sin(t * Mathf.PI * 4f) + 0.25f * Mathf.Sin(t * Mathf.PI * 6f)) * 0.55f;
                int n = i * 1103515245 + 12345;
                table[5][i] = (((n >> 16) & 255) / 127.5f - 1f) * 0.6f;
            }
            return table;
        }

        private sealed class Play
        {
            public float duration;
            public int notes;
            public int Active;
            public int MaxVoices;
            public bool dead;

            public void OnPcm(float[] data)
            {
                if (dead)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                Fill(data);
            }

            public void SkipToAudible(int rate)
            {
                int noteAt = -1;
                for (int i = next; i < ev.Length; i++)
                {
                    if ((ev[i].status & 0xF0) == 0x90 && ev[i].b > 0)
                    {
                        noteAt = i;
                        break;
                    }
                }
                if (noteAt < 0) return;
                while (next < noteAt) Apply(ev[next++], rate);
                tick = ev[noteAt].tick;
            }

            public void Fill(float[] data)
            {
                int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
                for (int i = 0; i < data.Length; i++)
                {
                    float s = Render(rate);
                    if (s > 1f) s = 1f;
                    else if (s < -1f) s = -1f;
                    data[i] = s;
                    if (Active > MaxVoices) MaxVoices = Active;
                }
            }

            private readonly Ev[] ev;
            private readonly Voice[] voices = new Voice[32];
            private readonly float[] level = new float[16];
            private readonly float[] bend = new float[16];
            private readonly int[] program = new int[16];
            private readonly int division;
            private int next;
            private double tick;
            private double samplesPerTick;
            private int delayLeft;
            private float gain;
            private float fadeStep;
            private int tempo = 500000;

            public static Play Start(int id, byte[] midi, bool jingle, int delayCycles, int fadeCycles)
            {
                var play = new Play(midi);
                play.delayLeft = Mathf.Max(0, delayCycles) * AudioSettings.outputSampleRate / 50;
                int fadeSamples = Mathf.Max(0, fadeCycles) * AudioSettings.outputSampleRate / 50;
                if (jingle || fadeSamples == 0)
                {
                    play.gain = 1f;
                    play.fadeStep = 0f;
                }
                else
                {
                    play.gain = 0f;
                    play.fadeStep = 1f / fadeSamples;
                }
                return play;
            }

            private Play(byte[] midi)
            {
                for (int i = 0; i < 16; i++)
                {
                    level[i] = 0.8f;
                    bend[i] = 1f;
                }
                ev = Parse(midi, out division, out duration, out notes);
                samplesPerTick = SamplesPerTick(AudioSettings.outputSampleRate, tempo, division);
            }

            public float Render(int rate)
            {
                if (delayLeft > 0)
                {
                    delayLeft--;
                    return 0f;
                }
                if (gain < 1f)
                {
                    gain += fadeStep;
                    if (gain > 1f) gain = 1f;
                }
                tick += 1.0 / samplesPerTick;
                while (next < ev.Length && ev[next].tick <= tick) Apply(ev[next++], rate);
                float s = 0f;
                int active = 0;
                for (int v = 0; v < voices.Length; v++)
                {
                    if (!voices[v].on) continue;
                    active++;
                    var voice = voices[v];
                    voice.phase += voice.freq / rate;
                    if (voice.phase >= 1f) voice.phase -= 1f;
                    float w = waves[voice.wave][(int)(voice.phase * 256f) & 255];
                    s += w * voice.env * voice.gain * gain;
                    voice.env *= voice.held ? voice.decay : 0.996f;
                    if (voice.env < 0.0008f) voice.on = false;
                    voices[v] = voice;
                }
                Active = active;
                return s * 0.22f;
            }

            private void Apply(Ev e, int rate)
            {
                int ch = e.status & 15;
                int kind = e.status & 0xF0;
                if (e.status == 0xFF)
                {
                    tempo = e.extra;
                    samplesPerTick = SamplesPerTick(rate, tempo, division);
                    return;
                }
                if (kind == 0xC0)
                {
                    program[ch] = e.a & 127;
                    return;
                }
                if (kind == 0xB0)
                {
                    if (e.a == 7 || e.a == 11) level[ch] = (e.b & 127) / 127f;
                    return;
                }
                if (kind == 0xE0)
                {
                    int raw = ((e.b & 127) << 7) | (e.a & 127);
                    bend[ch] = Mathf.Pow(2f, (raw - 8192) / 8192f * 2f / 12f);
                    return;
                }
                if (kind == 0x90 && e.b > 0) NoteOn(ch, e.a & 127, (e.b & 127) / 127f, rate);
                else if (kind == 0x80 || (kind == 0x90 && e.b == 0)) NoteOff(ch, e.a & 127);
            }

            private void NoteOn(int ch, int note, float velocity, int rate)
            {
                int slot = -1;
                float quiet = float.MaxValue;
                for (int i = 0; i < voices.Length; i++)
                {
                    if (!voices[i].on) { slot = i; break; }
                    if (voices[i].env < quiet) { quiet = voices[i].env; slot = i; }
                }
                bool drum = ch == 9;
                int prog = program[ch] & 127;
                int family = prog >> 3;
                int wave = drum ? (note < 45 ? 5 : 0) : family switch
                {
                    3 or 4 or 7 or 10 => 3,
                    8 => 2,
                    2 => 4,
                    _ => 0,
                };
                float decay = drum ? 0.992f : family == 0 ? 0.9996f : 0.9999f;
                float freq = 440f * Mathf.Pow(2f, (note - 69) / 12f) * bend[ch];
                voices[slot] = new Voice
                {
                    on = true,
                    held = true,
                    channel = ch,
                    note = note,
                    wave = wave,
                    phase = 0f,
                    freq = freq,
                    env = velocity,
                    decay = decay,
                    gain = level[ch] * (drum ? 0.7f : 1f),
                };
            }

            private void NoteOff(int ch, int note)
            {
                for (int i = 0; i < voices.Length; i++)
                {
                    if (voices[i].on && voices[i].held && voices[i].channel == ch && voices[i].note == note)
                    {
                        var voice = voices[i];
                        voice.held = false;
                        voices[i] = voice;
                        return;
                    }
                }
            }

            private static double SamplesPerTick(int rate, int tempoUs, int division)
            {
                if (division < 1) division = 960;
                return rate * (tempoUs / 1000000.0) / division;
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
                                list.Add(new Ev { tick = tick, seq = seq++, status = 0xFF, extra = tempo });
                            }
                            if (meta == 0x2F) break;
                            i += n;
                        }
                        else if (kind == 0xC0 || kind == 0xD0)
                        {
                            if (i >= end) break;
                            list.Add(new Ev { tick = tick, seq = seq++, status = status, a = midi[i++] });
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
                            list.Add(new Ev { tick = tick, seq = seq++, status = status, a = a, b = b });
                            if (kind == 0x90 && b > 0) notes++;
                        }
                    }
                    if (tick > lastTick) lastTick = tick;
                    pos = end;
                }
                list.Sort((x, y) =>
                {
                    int c = x.tick.CompareTo(y.tick);
                    return c != 0 ? c : x.seq.CompareTo(y.seq);
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
        }

        private struct Ev
        {
            public int tick, seq, status, a, b, extra;
        }

        private struct Voice
        {
            public bool on, held;
            public int channel, note, wave;
            public float phase, freq, env, decay, gain;
        }
    }
}
