using System;
using System.Collections.Generic;
using System.IO;

internal static class Program
{
    private static int Main()
    {
        string root = @"C:\Users\mfears\Projects\OpenRune-Server";
        string instruments = Path.Combine(root, "osrs-port-tools", "unity", "Assets", "StreamingAssets", "world", "instruments.bin.gz");
        string music = Path.Combine(root, "osrs-port-tools", "unity", "Assets", "StreamingAssets", "world", "music.bin.gz");
        if (!File.Exists(music)) music = @"E:\osrs\My project\Assets\StreamingAssets\world\music.bin.gz";
        OsrsPort.OsrsMusicBank.Load(instruments);
        var synths = LoadSynths(@"E:\osrs\My project\Assets\StreamingAssets\world\sounds.bin.gz");
        OsrsPort.OsrsMusicBank.LoadSynth = id =>
        {
            if (!synths.TryGetValue(id, out var data)) return null;
            if (!OsrsPort.OsrsSound.TryMix(data, out var pcm, out var a, out var b)) return null;
            var samples = new sbyte[pcm.Length];
            for (int i = 0; i < pcm.Length; i++) samples[i] = (sbyte)pcm[i];
            return new OsrsPort.OsrsVorbis.Pcm { Samples = samples, Rate = 22050, Start = a, End = b, PingPong = false };
        };
        Console.WriteLine($"patches={OsrsPort.OsrsMusicBank.PatchCount} samples={OsrsPort.OsrsMusicBank.SampleCount} synths={synths.Count}");
        int code = 0;
        foreach (int id in new[] { 64, 2 })
        {
            var midi = Song(music, id);
            var song = OsrsPort.OsrsMusicBank.Start(midi);
            song.SkipToAudible();
            var buf = new float[OsrsPort.OsrsMusicBank.Rate * 8];
            song.Fill(buf);
            float peak = 0f;
            double sum = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = buf[i] < 0 ? -buf[i] : buf[i];
                if (a > peak) peak = a;
                sum += buf[i] * buf[i];
            }
            double rms = Math.Sqrt(sum / buf.Length);
            string wav = Path.Combine(@"E:\osrs", "music-song" + id + ".wav");
            WriteWav(wav, buf);
            Console.WriteLine($"song{id} notes={song.Notes} voices={song.MaxVoices} peak={peak:0.000} rms={rms:0.000} dur={song.Duration:0.0} wav={wav}");
            if (peak <= 0.01f) code = 1;
        }
        return code;
    }

    private static Dictionary<int, byte[]> LoadSynths(string path)
    {
        var map = new Dictionary<int, byte[]>();
        using (var fs = File.OpenRead(path))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
        using (var r = new BinaryReader(gz))
        {
            r.ReadChars(4);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                int len = r.ReadInt32();
                map[id] = r.ReadBytes(len);
            }
        }
        return map;
    }

    private static byte[] Song(string path, int id)
    {
        using (var fs = File.OpenRead(path))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
        using (var r = new BinaryReader(gz))
        {
            r.ReadChars(4);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int sid = r.ReadInt32();
                int kind = r.ReadByte();
                int len = r.ReadInt32();
                var data = r.ReadBytes(len);
                if (kind == 0 && sid == id) return data;
            }
        }
        throw new InvalidDataException("missing song " + id);
    }

    private static void WriteWav(string path, float[] samples)
    {
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            int data = samples.Length * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + data);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(22050);
            w.Write(22050 * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(data);
            for (int i = 0; i < samples.Length; i++)
            {
                float s = samples[i];
                if (s > 1f) s = 1f;
                if (s < -1f) s = -1f;
                w.Write((short)(s * 32767f));
            }
        }
    }
}
