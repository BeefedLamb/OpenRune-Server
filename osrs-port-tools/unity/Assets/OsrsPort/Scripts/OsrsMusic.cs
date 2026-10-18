using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Plays the cache MIDI the server sends as <c>SONG</c> and <c>JINGLE</c>
    /// with the client's own patches, Vorbis samples and synths.
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
        private OsrsMusicBank.Song liveSong;
        private OsrsMusicBank.Song liveJingle;
        private bool loaded;
        private bool missing;
        private bool synthWired;

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

        public void PlaySong(int id, int fadeOutDelay, int fadeOutSpeed, int fadeInDelay, int fadeInSpeed)
        {
            Load();
            if (id == StopMidi)
            {
                if (liveSong != null) liveSong.Dead = true;
                songSource.Stop();
                Debug.Log("OsrsPort: music stop");
                return;
            }
            if (!songs.TryGetValue(id, out var midi))
            {
                Debug.Log("OsrsPort: music missing track " + id);
                return;
            }
            var probe = OsrsMusicBank.Start(midi);
            Measure(probe);
            if (liveSong != null) liveSong.Dead = true;
            liveSong = OsrsMusicBank.Start(midi);
            liveSong.SetFade(fadeInDelay, Mathf.Max(fadeInSpeed, fadeOutSpeed));
            var song = liveSong;
            var clip = AudioClip.Create(
                "song-" + id,
                Mathf.Max(OsrsMusicBank.Rate, (int)(liveSong.Duration * OsrsMusicBank.Rate)),
                1,
                OsrsMusicBank.Rate,
                true,
                data => FillClip(song, data));
            songSource.volume = jingleSource.isPlaying ? 0.3f : 1f;
            songSource.clip = clip;
            songSource.loop = false;
            songSource.Play();
            Plays++;
            LastTrack = id;
            Duration = liveSong.Duration;
            Debug.Log($"OsrsPort: music track={id} voices={Voices} peak={Peak:0.000} dur={Duration:0.0}s notes={liveSong.Notes} plays={Plays}");
        }

        public void PlayJingle(int id)
        {
            Load();
            if (!jingles.TryGetValue(id, out var midi))
            {
                Debug.Log("OsrsPort: music missing jingle " + id);
                return;
            }
            var probe = OsrsMusicBank.Start(midi);
            Measure(probe);
            if (liveJingle != null) liveJingle.Dead = true;
            liveJingle = OsrsMusicBank.Start(midi);
            var jingle = liveJingle;
            var clip = AudioClip.Create(
                "jingle-" + id,
                Mathf.Max(OsrsMusicBank.Rate, (int)(liveJingle.Duration * OsrsMusicBank.Rate)),
                1,
                OsrsMusicBank.Rate,
                true,
                data => FillClip(jingle, data));
            jingleSource.clip = clip;
            jingleSource.loop = false;
            jingleSource.Play();
            songSource.volume = 0.3f;
            Plays++;
            LastJingle = id;
            Debug.Log($"OsrsPort: jingle id={id} voices={Voices} peak={Peak:0.000} dur={liveJingle.Duration:0.0}s notes={liveJingle.Notes} plays={Plays}");
        }

        private void Update()
        {
            if (songSource != null && songSource.volume < 1f && (liveJingle == null || liveJingle.Dead || liveJingle.Finished))
                songSource.volume = 1f;
        }

        private static void FillClip(OsrsMusicBank.Song song, float[] data)
        {
            if (song == null || song.Dead)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }
            song.Fill(data);
        }

        private void Measure(OsrsMusicBank.Song song)
        {
            song.SkipToAudible();
            var buf = new float[OsrsMusicBank.Rate / 2];
            song.Fill(buf);
            float peak = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > peak) peak = a;
            }
            Peak = peak;
            Voices = song.MaxVoices;
        }

        private void Load()
        {
            WireSynth();
            if (loaded || missing) return;
            string musicPath = Path.Combine(Application.streamingAssetsPath, "world", "music.bin.gz");
            string instrumentPath = Path.Combine(Application.streamingAssetsPath, "world", "instruments.bin.gz");
            if (!File.Exists(musicPath) || !File.Exists(instrumentPath))
            {
                missing = true;
                Debug.LogWarning("OsrsPort: music files missing - run export_music.py and export_instruments.py");
                return;
            }
            using (var fs = File.OpenRead(musicPath))
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
            OsrsMusicBank.Load(instrumentPath);
            loaded = true;
            Debug.Log($"OsrsPort: {songs.Count} songs, {jingles.Count} jingles, {OsrsMusicBank.PatchCount} patches, {OsrsMusicBank.SampleCount} samples");
        }

        private void WireSynth()
        {
            if (synthWired) return;
            synthWired = true;
            var sound = OsrsSound.Ensure();
            OsrsMusicBank.LoadSynth = id =>
            {
                if (!sound.TryMixId(id, out var pcm, out var loopStart, out var loopEnd)) return null;
                var samples = new sbyte[pcm.Length];
                for (int i = 0; i < pcm.Length; i++) samples[i] = (sbyte)pcm[i];
                return new OsrsVorbis.Pcm
                {
                    Samples = samples,
                    Rate = OsrsSound.SampleRate,
                    Start = loopStart,
                    End = loopEnd,
                };
            };
        }
    }
}
