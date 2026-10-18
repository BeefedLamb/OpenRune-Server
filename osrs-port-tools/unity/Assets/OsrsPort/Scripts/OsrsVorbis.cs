using System;

namespace OsrsPort
{
    /// <summary>
    /// The client's VorbisSample decoder: index 14 archive 0 is the shared setup,
    /// and each other archive is one sample. Output is signed 8-bit, as toRawSound.
    /// </summary>
    public static class OsrsVorbis
    {
        public sealed class Pcm
        {
            public sbyte[] Samples;
            public int Rate;
            public int Start;
            public int End;
            public bool PingPong;
        }

        public static void ReadSetup(byte[] data) => Vorbis.ReadSetup(data);

        public static Pcm Decode(byte[] data) => new Sample(data).ToRaw();

        internal static int ILog(int n)
        {
            int v = 0;
            if (n < 0 || n >= 65536)
            {
                v += 16;
                n = (int)((uint)n >> 16);
            }
            if (n >= 256) { v += 8; n >>= 8; }
            if (n >= 16) { v += 4; n >>= 4; }
            if (n >= 4) { v += 2; n >>= 2; }
            if (n >= 1) { n >>= 1; v += 1; }
            return v;
        }

        internal static int Ushr(int n, int s) => (int)((uint)n >> s);

        internal static float Float32Unpack(int v)
        {
            int mantissa = v & 2097151;
            int exp = (v & 2145386496) >> 21;
            if ((v & int.MinValue) != 0) mantissa = -mantissa;
            return (float)(mantissa * Math.Pow(2.0, exp - 788));
        }

        internal static class Bits
        {
            public static byte[] Data;
            public static int Byte;
            public static int Bit;

            public static void Set(byte[] data, int offset)
            {
                Data = data;
                Byte = offset;
                Bit = 0;
            }

            public static int ReadBit()
            {
                int v = (Data[Byte] >> Bit) & 1;
                Bit++;
                Byte += Bit >> 3;
                Bit &= 7;
                return v;
            }

            public static int ReadBits(int n)
            {
                int acc = 0;
                int shift = 0;
                while (n >= 8 - Bit)
                {
                    int take = 8 - Bit;
                    int mask = (1 << take) - 1;
                    acc += ((Data[Byte] >> Bit) & mask) << shift;
                    Bit = 0;
                    Byte++;
                    shift += take;
                    n -= take;
                }
                if (n > 0)
                {
                    int mask = (1 << n) - 1;
                    acc += ((Data[Byte] >> Bit) & mask) << shift;
                    Bit += n;
                }
                return acc;
            }
        }

        internal sealed class Codebook
        {
            public readonly int Dimensions;
            public readonly int Entries;
            private readonly int[] lengthMap;
            private int[] keys;
            private float[][] values;

            public Codebook()
            {
                Bits.ReadBits(24);
                Dimensions = Bits.ReadBits(16);
                Entries = Bits.ReadBits(24);
                lengthMap = new int[Entries];
                if (Bits.ReadBit() != 0)
                {
                    int i = 0;
                    int length = Bits.ReadBits(5) + 1;
                    while (i < Entries)
                    {
                        int run = Bits.ReadBits(ILog(Entries - i));
                        for (int k = 0; k < run; k++) lengthMap[i++] = length;
                        length++;
                    }
                }
                else
                {
                    bool sparse = Bits.ReadBit() != 0;
                    for (int i = 0; i < Entries; i++)
                    {
                        if (sparse && Bits.ReadBit() == 0) lengthMap[i] = 0;
                        else lengthMap[i] = Bits.ReadBits(5) + 1;
                    }
                }
                BuildTree();
                int lookup = Bits.ReadBits(4);
                if (lookup <= 0) return;
                float minimum = Float32Unpack(Bits.ReadBits(32));
                float delta = Float32Unpack(Bits.ReadBits(32));
                int quant = Bits.ReadBits(4) + 1;
                bool sequence = Bits.ReadBit() != 0;
                int count = lookup == 1 ? MapType1(Entries, Dimensions) : Entries * Dimensions;
                var qvals = new int[count];
                for (int i = 0; i < count; i++) qvals[i] = Bits.ReadBits(quant);
                values = new float[Entries][];
                if (lookup == 1)
                {
                    for (int entry = 0; entry < Entries; entry++)
                    {
                        values[entry] = new float[Dimensions];
                        float last = 0f;
                        int step = 1;
                        for (int dim = 0; dim < Dimensions; dim++)
                        {
                            int off = (entry / step) % count;
                            float val = qvals[off] * delta + minimum + last;
                            values[entry][dim] = val;
                            if (sequence) last = val;
                            step *= count;
                        }
                    }
                }
                else
                {
                    for (int entry = 0; entry < Entries; entry++)
                    {
                        values[entry] = new float[Dimensions];
                        float last = 0f;
                        int idx = entry * Dimensions;
                        for (int dim = 0; dim < Dimensions; dim++)
                        {
                            float val = qvals[idx] * delta + minimum + last;
                            values[entry][dim] = val;
                            if (sequence) last = val;
                            idx++;
                        }
                    }
                }
            }

            private void BuildTree()
            {
                var assigned = new int[Entries];
                var next = new int[33];
                for (int i = 0; i < Entries; i++)
                {
                    int length = lengthMap[i];
                    if (length == 0) continue;
                    int bit = 1 << (32 - length);
                    int code = next[length];
                    assigned[i] = code;
                    int taken;
                    if ((code & bit) != 0) taken = next[length - 1];
                    else
                    {
                        taken = code | bit;
                        for (int back = length - 1; back >= 1; back--)
                        {
                            int prev = next[back];
                            if (prev != code) break;
                            int marker = 1 << (32 - back);
                            if ((prev & marker) != 0)
                            {
                                next[back] = next[back - 1];
                                break;
                            }
                            next[back] = prev | marker;
                        }
                    }
                    next[length] = taken;
                    for (int longer = length + 1; longer <= 32; longer++)
                        if (next[longer] == code) next[longer] = taken;
                }
                keys = new int[8];
                int used = 0;
                for (int i = 0; i < Entries; i++)
                {
                    int length = lengthMap[i];
                    if (length == 0) continue;
                    int code = assigned[i];
                    int node = 0;
                    for (int bitI = 0; bitI < length; bitI++)
                    {
                        int bit = Ushr(unchecked((int)0x80000000), bitI);
                        if ((code & bit) != 0)
                        {
                            if (keys[node] == 0) keys[node] = used;
                            node = keys[node];
                        }
                        else node++;
                        if (node >= keys.Length)
                        {
                            var grown = new int[keys.Length * 2];
                            Array.Copy(keys, grown, keys.Length);
                            keys = grown;
                        }
                    }
                    keys[node] = ~i;
                    if (node >= used) used = node + 1;
                }
            }

            public int Decode()
            {
                int node = 0;
                while (keys[node] >= 0) node = Bits.ReadBit() != 0 ? keys[node] : node + 1;
                return ~keys[node];
            }

            public float[] DecodeVector() => values[Decode()];
        }

        private static int MapType1(int entries, int dimensions)
        {
            int n = (int)Math.Pow(entries, 1.0 / dimensions) + 1;
            while (true)
            {
                int acc = 1;
                int basis = n;
                int dim = dimensions;
                while (dim > 1)
                {
                    if ((dim & 1) != 0) acc *= basis;
                    basis *= basis;
                    dim >>= 1;
                }
                if (dim == 1) acc *= basis;
                if (acc <= entries) return n;
                n--;
            }
        }

        internal sealed class Floor
        {
            private static readonly int[] Multipliers = { 256, 128, 86, 64 };
            private static readonly float[] Db = {
                            1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f, 1.369995e-07f, 1.459025e-07f, 1.5538409e-07f, 1.6548181e-07f,
            1.7623574e-07f, 1.8768856e-07f, 1.998856e-07f, 2.128753e-07f, 2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384212e-07f,
            2.9163792e-07f, 3.1059022e-07f, 3.307741e-07f, 3.5226967e-07f, 3.7516213e-07f, 3.995423e-07f, 4.255068e-07f, 4.5315863e-07f,
            4.8260745e-07f, 5.1397e-07f, 5.4737063e-07f, 5.829419e-07f, 6.208247e-07f, 6.611694e-07f, 7.041359e-07f, 7.4989464e-07f,
            7.98627e-07f, 8.505263e-07f, 9.057983e-07f, 9.646621e-07f, 1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
            1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f, 1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.053526e-06f,
            2.1869757e-06f, 2.3290977e-06f, 2.4804558e-06f, 2.6416496e-06f, 2.813319e-06f, 2.9961443e-06f, 3.1908505e-06f, 3.39821e-06f,
            3.619045e-06f, 3.8542307e-06f, 4.1047006e-06f, 4.371447e-06f, 4.6555283e-06f, 4.958071e-06f, 5.280274e-06f, 5.623416e-06f,
            5.988857e-06f, 6.3780467e-06f, 6.7925284e-06f, 7.2339453e-06f, 7.704048e-06f, 8.2047e-06f, 8.737888e-06f, 9.305725e-06f,
            9.910464e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f, 1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399271e-05f,
            1.6400005e-05f, 1.7465769e-05f, 1.8600793e-05f, 1.9809577e-05f, 2.1096914e-05f, 2.2467912e-05f, 2.3928002e-05f, 2.5482977e-05f,
            2.7139005e-05f, 2.890265e-05f, 3.078091e-05f, 3.2781227e-05f, 3.4911533e-05f, 3.718028e-05f, 3.9596467e-05f, 4.2169668e-05f,
            4.491009e-05f, 4.7828602e-05f, 5.0936775e-05f, 5.424693e-05f, 5.7772202e-05f, 6.152657e-05f, 6.552491e-05f, 6.9783084e-05f,
            7.4317984e-05f, 7.914758e-05f, 8.429104e-05f, 8.976875e-05f, 9.560242e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
            0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f, 0.00015820454f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
            0.00020351382f, 0.0002167393f, 0.00023082423f, 0.00024582449f, 0.00026179955f, 0.00027881275f, 0.00029693157f, 0.00031622787f,
            0.00033677815f, 0.00035866388f, 0.00038197188f, 0.00040679457f, 0.00043323037f, 0.0004613841f, 0.0004913675f, 0.00052329927f,
            0.0005573062f, 0.0005935231f, 0.0006320936f, 0.0006731706f, 0.000716917f, 0.0007635063f, 0.00081312325f, 0.00086596457f,
            0.00092223985f, 0.0009821722f, 0.0010459992f, 0.0011139743f, 0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
            0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f, 0.0019632196f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
            0.0025254795f, 0.0026895993f, 0.0028643848f, 0.0030505287f, 0.003248769f, 0.0034598925f, 0.0036847359f, 0.0039241905f,
            0.0041792067f, 0.004450795f, 0.004740033f, 0.005048067f, 0.0053761187f, 0.005725489f, 0.0060975635f, 0.0064938175f,
            0.0069158226f, 0.0073652514f, 0.007843887f, 0.008353627f, 0.008896492f, 0.009474637f, 0.010090352f, 0.01074608f,
            0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f, 0.014722068f, 0.015678791f, 0.016697686f, 0.017782796f,
            0.018938422f, 0.020169148f, 0.021479854f, 0.022875736f, 0.02436233f, 0.025945531f, 0.027631618f, 0.029427277f,
            0.031339627f, 0.03337625f, 0.035545226f, 0.037855156f, 0.0403152f, 0.042935107f, 0.045725275f, 0.048696756f,
            0.05186135f, 0.05523159f, 0.05882085f, 0.062643364f, 0.06671428f, 0.07104975f, 0.075666964f, 0.08058423f,
            0.08582105f, 0.09139818f, 0.097337745f, 0.1036633f, 0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
            0.14201812f, 0.15124726f, 0.16107617f, 0.1715438f, 0.18269168f, 0.19456401f, 0.20720787f, 0.22067343f,
            0.23501402f, 0.25028655f, 0.26655158f, 0.28387362f, 0.3023213f, 0.32196787f, 0.34289113f, 0.36517414f,
            0.3889052f, 0.41417846f, 0.44109413f, 0.4697589f, 0.50028646f, 0.53279793f, 0.5674221f, 0.6042964f,
            0.64356697f, 0.6853896f, 0.72993004f, 0.777365f, 0.8278826f, 0.88168305f, 0.9389798f, 1.0f,
            };
            private static int[] sortX;
            private static int[] sortY;
            private static bool[] sortUsed;
            private readonly int[] partitionClass;
            private readonly int[] classDim;
            private readonly int[] classSub;
            private readonly int[] classMaster;
            private readonly int[][] classBooks;
            private readonly int multiplier;
            private readonly int[] x;

            public Floor()
            {
                if (Bits.ReadBits(16) != 1) throw new InvalidOperationException("vorbis floor");
                int partitions = Bits.ReadBits(5);
                partitionClass = new int[partitions];
                int classCount = 0;
                for (int i = 0; i < partitions; i++)
                {
                    partitionClass[i] = Bits.ReadBits(4);
                    if (partitionClass[i] >= classCount) classCount = partitionClass[i] + 1;
                }
                classDim = new int[classCount];
                classSub = new int[classCount];
                classMaster = new int[classCount];
                classBooks = new int[classCount][];
                for (int i = 0; i < classCount; i++)
                {
                    classDim[i] = Bits.ReadBits(3) + 1;
                    int sub = Bits.ReadBits(2);
                    classSub[i] = sub;
                    if (sub != 0) classMaster[i] = Bits.ReadBits(8);
                    int n = 1 << sub;
                    classBooks[i] = new int[n];
                    for (int k = 0; k < n; k++) classBooks[i][k] = Bits.ReadBits(8) - 1;
                }
                multiplier = Bits.ReadBits(2) + 1;
                int rangeBits = Bits.ReadBits(4);
                int points = 2;
                for (int i = 0; i < partitions; i++) points += classDim[partitionClass[i]];
                x = new int[points];
                x[0] = 0;
                x[1] = 1 << rangeBits;
                int p = 2;
                for (int i = 0; i < partitions; i++)
                    for (int k = 0; k < classDim[partitionClass[i]]; k++)
                        x[p++] = Bits.ReadBits(rangeBits);
                if (sortX == null || sortX.Length < p)
                {
                    sortX = new int[p];
                    sortY = new int[p];
                    sortUsed = new bool[p];
                }
            }

            public bool Read()
            {
                if (Bits.ReadBit() == 0) return false;
                int n = x.Length;
                for (int i = 0; i < n; i++) sortX[i] = x[i];
                int amp = Multipliers[multiplier - 1];
                int bits = ILog(amp - 1);
                sortY[0] = Bits.ReadBits(bits);
                sortY[1] = Bits.ReadBits(bits);
                int p = 2;
                var books = Vorbis.Codebooks;
                for (int part = 0; part < partitionClass.Length; part++)
                {
                    int cls = partitionClass[part];
                    int dim = classDim[cls];
                    int sub = classSub[cls];
                    int mask = (1 << sub) - 1;
                    int cascade = 0;
                    if (sub > 0) cascade = books[classMaster[cls]].Decode();
                    for (int k = 0; k < dim; k++)
                    {
                        int book = classBooks[cls][cascade & mask];
                        cascade >>= sub;
                        sortY[p++] = book >= 0 ? books[book].Decode() : 0;
                    }
                }
                return true;
            }

            public void Apply(float[] residue, int n)
            {
                int count = x.Length;
                int amp = Multipliers[multiplier - 1];
                sortUsed[0] = true;
                sortUsed[1] = true;
                for (int i = 2; i < count; i++)
                {
                    int low = Low(i);
                    int high = High(i);
                    int predicted = Predict(sortX[low], sortY[low], sortX[high], sortY[high], sortX[i]);
                    int val = sortY[i];
                    int room = amp - predicted;
                    int half = (room < predicted ? room : predicted) << 1;
                    if (val != 0)
                    {
                        sortUsed[high] = true;
                        sortUsed[low] = true;
                        sortUsed[i] = true;
                        if (val >= half)
                            sortY[i] = room > predicted ? predicted + (val - predicted) : room + (predicted - val) - 1;
                        else
                            sortY[i] = (val & 1) != 0 ? predicted - (val + 1) / 2 : val / 2 + predicted;
                    }
                    else
                    {
                        sortUsed[i] = false;
                        sortY[i] = predicted;
                    }
                }
                Sort(0, count - 1);
                int lx = 0;
                int ly = sortY[0] * multiplier;
                for (int i = 1; i < count; i++)
                {
                    if (!sortUsed[i]) continue;
                    int hx = sortX[i];
                    int hy = sortY[i] * multiplier;
                    Render(lx, ly, hx, hy, residue, n);
                    if (hx >= n) return;
                    lx = hx;
                    ly = hy;
                }
                float scale = Db[ly];
                for (int i = lx; i < n; i++) residue[i] *= scale;
            }

            private static int Predict(int x0, int y0, int x1, int y1, int at)
            {
                int dy = y1 - y0;
                int ady = dy < 0 ? -dy : dy;
                int dx = x1 - x0;
                int off = dx != 0 ? (at - x0) * ady / dx : 0;
                return dy < 0 ? y0 - off : off + y0;
            }

            private static void Render(int x0, int y0, int x1, int y1, float[] output, int limit)
            {
                int dy = y1 - y0;
                int ady = dy < 0 ? -dy : dy;
                int dx = x1 - x0;
                int step = dx != 0 ? dy / dx : 0;
                int y = y0;
                int err = 0;
                int stepErr = dy < 0 ? step - 1 : step + 1;
                ady -= (step < 0 ? -step : step) * dx;
                output[x0] *= Db[y0];
                if (x1 > limit) x1 = limit;
                for (int x = x0 + 1; x < x1; x++)
                {
                    err += ady;
                    if (err >= dx) { err -= dx; y += stepErr; }
                    else y += step;
                    output[x] *= Db[y];
                }
            }

            private static void Sort(int lo, int hi)
            {
                if (lo >= hi) return;
                int pivot = lo;
                int px = sortX[lo];
                int py = sortY[lo];
                bool pu = sortUsed[lo];
                for (int i = lo + 1; i <= hi; i++)
                {
                    int xv = sortX[i];
                    if (xv < px)
                    {
                        sortX[pivot] = xv;
                        sortY[pivot] = sortY[i];
                        sortUsed[pivot] = sortUsed[i];
                        pivot++;
                        sortX[i] = sortX[pivot];
                        sortY[i] = sortY[pivot];
                        sortUsed[i] = sortUsed[pivot];
                    }
                }
                sortX[pivot] = px;
                sortY[pivot] = py;
                sortUsed[pivot] = pu;
                Sort(lo, pivot - 1);
                Sort(pivot + 1, hi);
            }

            private static int Low(int i)
            {
                int target = sortX[i];
                int best = -1;
                int score = int.MinValue;
                for (int j = 0; j < i; j++)
                {
                    int xv = sortX[j];
                    if (xv < target && xv > score) { best = j; score = xv; }
                }
                return best;
            }

            private static int High(int i)
            {
                int target = sortX[i];
                int best = -1;
                int score = int.MaxValue;
                for (int j = 0; j < i; j++)
                {
                    int xv = sortX[j];
                    if (xv > target && xv < score) { best = j; score = xv; }
                }
                return best;
            }
        }

        internal sealed class Residue
        {
            private readonly int kind;
            private readonly int begin;
            private readonly int end;
            private readonly int partition;
            private readonly int classes;
            private readonly int classbook;
            private readonly int[] cascade;

            public Residue()
            {
                kind = Bits.ReadBits(16);
                begin = Bits.ReadBits(24);
                end = Bits.ReadBits(24);
                partition = Bits.ReadBits(24) + 1;
                classes = Bits.ReadBits(6) + 1;
                classbook = Bits.ReadBits(8);
                var packed = new int[classes];
                for (int i = 0; i < classes; i++)
                {
                    int low = 0;
                    int high = Bits.ReadBits(3);
                    if (Bits.ReadBit() != 0) low = Bits.ReadBits(5);
                    packed[i] = (low << 3) | high;
                }
                cascade = new int[classes * 8];
                for (int i = 0; i < cascade.Length; i++)
                    cascade[i] = (packed[i >> 3] & (1 << (i & 7))) != 0 ? Bits.ReadBits(8) : -1;
            }

            public void Apply(float[] output, int n, bool mute)
            {
                for (int i = 0; i < n; i++) output[i] = 0f;
                if (mute) return;
                var book = Vorbis.Codebooks[classbook];
                int parts = (end - begin) / partition;
                var classify = new int[parts];
                for (int pass = 0; pass < 8; pass++)
                {
                    int part = 0;
                    while (part < parts)
                    {
                        if (pass == 0)
                        {
                            int code = book.Decode();
                            for (int dim = book.Dimensions - 1; dim >= 0; dim--)
                            {
                                if (part + dim < parts) classify[part + dim] = code % classes;
                                code /= classes;
                            }
                        }
                        for (int dim = 0; dim < book.Dimensions; dim++)
                        {
                            int cls = classify[part];
                            int which = cascade[pass + cls * 8];
                            if (which >= 0)
                            {
                                int offset = part * partition + begin;
                                var vecbook = Vorbis.Codebooks[which];
                                if (kind == 0)
                                {
                                    int steps = partition / vecbook.Dimensions;
                                    for (int step = 0; step < steps; step++)
                                    {
                                        var vec = vecbook.DecodeVector();
                                        for (int d = 0; d < vecbook.Dimensions; d++)
                                            output[offset + step + d * steps] += vec[d];
                                    }
                                }
                                else
                                {
                                    int left = 0;
                                    while (left < partition)
                                    {
                                        var vec = vecbook.DecodeVector();
                                        for (int d = 0; d < vecbook.Dimensions; d++)
                                            output[offset + left++] += vec[d];
                                    }
                                }
                            }
                            part++;
                            if (part >= parts) break;
                        }
                    }
                }
            }
        }

        internal sealed class Mapping
        {
            public readonly int Submaps;
            public readonly int Mux;
            public readonly int[] Floors;
            public readonly int[] Residues;

            public Mapping()
            {
                Bits.ReadBits(16);
                Submaps = Bits.ReadBit() != 0 ? Bits.ReadBits(4) + 1 : 1;
                if (Bits.ReadBit() != 0) Bits.ReadBits(8);
                Bits.ReadBits(2);
                Mux = Submaps > 1 ? Bits.ReadBits(4) : 0;
                Floors = new int[Submaps];
                Residues = new int[Submaps];
                for (int i = 0; i < Submaps; i++)
                {
                    Bits.ReadBits(8);
                    Floors[i] = Bits.ReadBits(8);
                    Residues[i] = Bits.ReadBits(8);
                }
            }
        }

        internal static class Vorbis
        {
            public static Codebook[] Codebooks;
            public static Floor[] Floors;
            public static Residue[] Residues;
            public static Mapping[] Mappings;
            public static bool[] BlockFlags;
            public static int[] ModeMap;
            public static int Block0;
            public static int Block1;
            public static float[] Buf;
            public static float[] Win0, Win1, Cos0, Cos1, Sin0, Sin1;
            public static int[] Rev0, Rev1;

            public static void ReadSetup(byte[] data)
            {
                Bits.Set(data, 0);
                Block0 = 1 << Bits.ReadBits(4);
                Block1 = 1 << Bits.ReadBits(4);
                Buf = new float[Block1];
                for (int big = 0; big < 2; big++)
                {
                    int n = big != 0 ? Block1 : Block0;
                    int half = n >> 1;
                    int quarter = n >> 2;
                    int eighth = n >> 3;
                    var a = new float[half];
                    for (int i = 0; i < quarter; i++)
                    {
                        a[i * 2] = (float)Math.Cos(i * 4 * Math.PI / n);
                        a[i * 2 + 1] = -(float)Math.Sin(i * 4 * Math.PI / n);
                    }
                    var b = new float[half];
                    for (int i = 0; i < quarter; i++)
                    {
                        b[i * 2] = (float)Math.Cos((i * 2 + 1) * Math.PI / (n * 2));
                        b[i * 2 + 1] = (float)Math.Sin((i * 2 + 1) * Math.PI / (n * 2));
                    }
                    var c = new float[quarter];
                    for (int i = 0; i < eighth; i++)
                    {
                        c[i * 2] = (float)Math.Cos((i * 4 + 2) * Math.PI / n);
                        c[i * 2 + 1] = -(float)Math.Sin((i * 4 + 2) * Math.PI / n);
                    }
                    int bits = ILog(eighth - 1);
                    var rev = new int[eighth];
                    for (int i = 0; i < eighth; i++)
                    {
                        int outv = 0;
                        int nv = i;
                        int left = bits;
                        while (left > 0)
                        {
                            left--;
                            outv = (outv << 1) | (nv & 1);
                            nv >>= 1;
                        }
                        rev[i] = outv;
                    }
                    if (big != 0) { Win1 = a; Cos1 = b; Sin1 = c; Rev1 = rev; }
                    else { Win0 = a; Cos0 = b; Sin0 = c; Rev0 = rev; }
                }
                int nbooks = Bits.ReadBits(8) + 1;
                Codebooks = new Codebook[nbooks];
                for (int i = 0; i < nbooks; i++) Codebooks[i] = new Codebook();
                int ntimes = Bits.ReadBits(6) + 1;
                for (int i = 0; i < ntimes; i++) Bits.ReadBits(16);
                int nfloors = Bits.ReadBits(6) + 1;
                Floors = new Floor[nfloors];
                for (int i = 0; i < nfloors; i++) Floors[i] = new Floor();
                int nres = Bits.ReadBits(6) + 1;
                Residues = new Residue[nres];
                for (int i = 0; i < nres; i++) Residues[i] = new Residue();
                int nmaps = Bits.ReadBits(6) + 1;
                Mappings = new Mapping[nmaps];
                for (int i = 0; i < nmaps; i++) Mappings[i] = new Mapping();
                int nmodes = Bits.ReadBits(6) + 1;
                BlockFlags = new bool[nmodes];
                ModeMap = new int[nmodes];
                for (int i = 0; i < nmodes; i++)
                {
                    BlockFlags[i] = Bits.ReadBit() != 0;
                    Bits.ReadBits(16);
                    Bits.ReadBits(16);
                    ModeMap[i] = Bits.ReadBits(8);
                }
            }
        }

        private sealed class Sample
        {
            private readonly int rate;
            private readonly int count;
            private readonly int start;
            private readonly int end;
            private readonly bool pingpong;
            private readonly byte[][] packets;
            private float[] prev;
            private int prevN;
            private int prevKeep;
            private bool prevMute;

            public Sample(byte[] data)
            {
                rate = Be(data, 0);
                count = Be(data, 4);
                start = Be(data, 8);
                end = Be(data, 12);
                if (end < 0)
                {
                    end = ~end;
                    pingpong = true;
                }
                int packetsN = Be(data, 16);
                packets = new byte[packetsN][];
                int p = 20;
                for (int i = 0; i < packetsN; i++)
                {
                    int size = 0;
                    int b;
                    do
                    {
                        b = data[p++];
                        size += b;
                    } while (b >= 255);
                    packets[i] = new byte[size];
                    Buffer.BlockCopy(data, p, packets[i], 0, size);
                    p += size;
                }
            }

            private static int Be(byte[] d, int i) =>
                (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

            public Pcm ToRaw()
            {
                prevN = 0;
                prev = new float[Vorbis.Block1];
                prevKeep = 0;
                prevMute = false;
                var pcm = new sbyte[count];
                int at = 0;
                for (int i = 0; i < packets.Length; i++)
                {
                    var block = DecodePacket(i);
                    if (block == null) continue;
                    int n = block.Length;
                    if (n > count - at) n = count - at;
                    for (int s = 0; s < n; s++)
                    {
                        int v = (int)(128f + block[s] * 128f);
                        if ((v & -256) != 0) v = v < 0 ? 0 : 255;
                        pcm[at++] = (sbyte)(v - 128);
                    }
                }
                return new Pcm { Samples = pcm, Rate = rate, Start = start, End = end, PingPong = pingpong };
            }

            private float[] DecodePacket(int index)
            {
                Bits.Set(packets[index], 0);
                Bits.ReadBit();
                int mode = Bits.ReadBits(ILog(Vorbis.ModeMap.Length - 1));
                bool big = Vorbis.BlockFlags[mode];
                int n = big ? Vorbis.Block1 : Vorbis.Block0;
                bool leftFlag = false;
                bool rightFlag = false;
                if (big)
                {
                    leftFlag = Bits.ReadBit() != 0;
                    rightFlag = Bits.ReadBit() != 0;
                }
                int half = n >> 1;
                int windowStart;
                int windowEnd;
                int leftN;
                if (big && !leftFlag)
                {
                    windowStart = (n >> 2) - (Vorbis.Block0 >> 2);
                    windowEnd = (Vorbis.Block0 >> 2) + (n >> 2);
                    leftN = Vorbis.Block0 >> 1;
                }
                else
                {
                    windowStart = 0;
                    windowEnd = half;
                    leftN = n >> 1;
                }
                int r0;
                int r1;
                int rightN;
                if (big && !rightFlag)
                {
                    r0 = n - (n >> 2) - (Vorbis.Block0 >> 2);
                    r1 = (Vorbis.Block0 >> 2) + (n - (n >> 2));
                    rightN = Vorbis.Block0 >> 1;
                }
                else
                {
                    r0 = half;
                    r1 = n;
                    rightN = n >> 1;
                }
                var mapping = Vorbis.Mappings[Vorbis.ModeMap[mode]];
                bool mute = !Vorbis.Floors[mapping.Floors[mapping.Mux]].Read();
                var buf = Vorbis.Buf;
                for (int sub = 0; sub < mapping.Submaps; sub++)
                    Vorbis.Residues[mapping.Residues[sub]].Apply(buf, n >> 1, mute);
                if (!mute)
                {
                    Vorbis.Floors[mapping.Floors[mapping.Mux]].Apply(buf, n >> 1);
                    Imdct(buf, n, big);
                    for (int i = windowStart; i < windowEnd; i++)
                    {
                        float w = (float)Math.Sin((i - windowStart + 0.5) / leftN * 0.5 * Math.PI);
                        buf[i] *= (float)Math.Sin(1.5707963267948966 * w * w);
                    }
                    for (int i = r0; i < r1; i++)
                    {
                        float w = (float)Math.Sin((i - r0 + 0.5) / rightN * 0.5 * Math.PI + 1.5707963267948966);
                        buf[i] *= (float)Math.Sin(1.5707963267948966 * w * w);
                    }
                }
                else
                {
                    for (int i = half; i < n; i++) buf[i] = 0f;
                }
                float[] produced = null;
                if (prevN > 0)
                {
                    int outN = (n + prevN) >> 2;
                    produced = new float[outN];
                    if (!prevMute)
                    {
                        for (int i = 0; i < prevKeep; i++)
                            produced[i] += prev[i + (prevN >> 1)];
                    }
                    if (!mute)
                    {
                        for (int i = windowStart; i < half; i++)
                            produced[produced.Length - half + i] += buf[i];
                    }
                }
                prev = (float[])buf.Clone();
                Vorbis.Buf = new float[Vorbis.Block1];
                prevN = n;
                prevKeep = r1 - half;
                prevMute = mute;
                return produced;
            }

            private static void Imdct(float[] buf, int n, bool big)
            {
                int half = n >> 1;
                int quarter = n >> 2;
                int eighth = n >> 3;
                for (int i = 0; i < half; i++) buf[i] *= 0.5f;
                for (int i = half; i < n; i++) buf[i] = -buf[n - i - 1];
                var wa = big ? Vorbis.Win1 : Vorbis.Win0;
                var wb = big ? Vorbis.Cos1 : Vorbis.Cos0;
                var wc = big ? Vorbis.Sin1 : Vorbis.Sin0;
                var rev = big ? Vorbis.Rev1 : Vorbis.Rev0;
                for (int i = 0; i < quarter; i++)
                {
                    float a = buf[i * 4] - buf[n - i * 4 - 1];
                    float b = buf[i * 4 + 2] - buf[n - i * 4 - 3];
                    float c = wa[i * 2];
                    float d = wa[i * 2 + 1];
                    buf[n - i * 4 - 1] = a * c - b * d;
                    buf[n - i * 4 - 3] = a * d + b * c;
                }
                for (int i = 0; i < eighth; i++)
                {
                    float a = buf[half + i * 4 + 3];
                    float b = buf[half + i * 4 + 1];
                    float c = buf[i * 4 + 3];
                    float d = buf[i * 4 + 1];
                    buf[half + i * 4 + 3] = a + c;
                    buf[half + i * 4 + 1] = b + d;
                    float e = wa[half - 4 - i * 4];
                    float f = wa[half - 3 - i * 4];
                    buf[i * 4 + 3] = (a - c) * e - (b - d) * f;
                    buf[i * 4 + 1] = (b - d) * e + (a - c) * f;
                }
                int bits = ILog(n - 1);
                for (int stage = 0; stage < bits - 3; stage++)
                {
                    int span = n >> (stage + 2);
                    int step = 8 << stage;
                    for (int group = 0; group < (2 << stage); group++)
                    {
                        int p = n - span * group * 2;
                        int q = n - span * (group * 2 + 1);
                        for (int i = 0; i < (n >> (stage + 4)); i++)
                        {
                            int k = i * 4;
                            float a = buf[p - 1 - k];
                            float b = buf[p - 3 - k];
                            float c = buf[q - 1 - k];
                            float d = buf[q - 3 - k];
                            buf[p - 1 - k] = a + c;
                            buf[p - 3 - k] = b + d;
                            float e = wa[i * step];
                            float f = wa[i * step + 1];
                            buf[q - 1 - k] = (a - c) * e - (b - d) * f;
                            buf[q - 3 - k] = (b - d) * e + (a - c) * f;
                        }
                    }
                }
                for (int i = 1; i < eighth - 1; i++)
                {
                    int j = rev[i];
                    if (i < j)
                    {
                        int a = i * 8;
                        int b = j * 8;
                        for (int off = 1; off <= 7; off += 2)
                        {
                            float tmp = buf[a + off];
                            buf[a + off] = buf[b + off];
                            buf[b + off] = tmp;
                        }
                    }
                }
                for (int i = 0; i < half; i++) buf[i] = buf[i * 2 + 1];
                for (int i = 0; i < eighth; i++)
                {
                    buf[n - 1 - i * 2] = buf[i * 4];
                    buf[n - 2 - i * 2] = buf[i * 4 + 1];
                    buf[n - quarter - 1 - i * 2] = buf[i * 4 + 2];
                    buf[n - quarter - 2 - i * 2] = buf[i * 4 + 3];
                }
                for (int i = 0; i < eighth; i++)
                {
                    float c = wc[i * 2];
                    float d = wc[i * 2 + 1];
                    float a = buf[half + i * 2];
                    float b = buf[half + i * 2 + 1];
                    float e = buf[n - 2 - i * 2];
                    float f = buf[n - 1 - i * 2];
                    float g = d * (a - e) + c * (b + f);
                    buf[half + i * 2] = (a + e + g) * 0.5f;
                    buf[n - 2 - i * 2] = (a + e - g) * 0.5f;
                    g = d * (b + f) - c * (a - e);
                    buf[half + i * 2 + 1] = (b - f + g) * 0.5f;
                    buf[n - 1 - i * 2] = (-b + f + g) * 0.5f;
                }
                for (int i = 0; i < quarter; i++)
                {
                    buf[i] = buf[half + i * 2] * wb[i * 2] + buf[half + i * 2 + 1] * wb[i * 2 + 1];
                    buf[half - 1 - i] = buf[half + i * 2] * wb[i * 2 + 1] - buf[half + i * 2 + 1] * wb[i * 2];
                }
                for (int i = 0; i < quarter; i++) buf[i + (n - quarter)] = -buf[i];
                for (int i = 0; i < quarter; i++) buf[i] = buf[quarter + i];
                for (int i = 0; i < quarter; i++) buf[quarter + i] = -buf[quarter - i - 1];
                for (int i = 0; i < quarter; i++) buf[half + i] = buf[n - i - 1];
            }
        }
    }
}
