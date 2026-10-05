namespace NCSFPlayer.Engine;

/// <summary>
/// Spec Part 3: tables built once from their formulas, the two driver constant lists, and the small helper functions
/// built on them. The static constructor runs the Part 3 self-checks and throws if any of them fails.
/// </summary>
internal static class Tables
{
    /// <summary>3.1 pitch fraction table P[0..767].</summary>
    internal static readonly ushort[] Pitch = new ushort[768];

    /// <summary>3.2 attenuation-to-volume table V[0..723].</summary>
    internal static readonly byte[] Level = new byte[724];

    /// <summary>3.3 level-to-decibel table D[0..127].</summary>
    internal static readonly short[] Decibel = new short[128];

    /// <summary>3.4 quarter-sine table S[0..32].</summary>
    internal static readonly byte[] QuarterSine = new byte[33];

    /// <summary>3.6 voice search order.</summary>
    internal static readonly int[] SearchOrder = [4, 5, 6, 7, 2, 0, 3, 1, 8, 9, 10, 11, 14, 12, 15, 13];

    /// <summary>3.6 fast-attack table A[0..18].</summary>
    internal static readonly byte[] FastAttack = [0, 1, 5, 14, 26, 38, 51, 63, 73, 84, 92, 100, 109, 116, 123, 127, 132, 137, 143];

    /// <summary>Right shifts for the divider codes 0..3 (used by the voice-loudness comparison, 6.2).</summary>
    internal static readonly int[] DividerShift = [0, 1, 2, 4];

    static double RoundNearest(double x) => Math.Floor(x + 0.5);

    static Tables()
    {
        for (int i = 0; i < 768; i++)
            Pitch[i] = (ushort)RoundNearest(65536 * (Math.Pow(2, i / 768.0) - 1));

        for (int k = 0; k <= 723; k++)
        {
            int a = k - 723;
            int s = a < -240 ? 4 : a < -120 ? 2 : a < -60 ? 1 : 0;
            double m = 1 << s;
            double v = RoundNearest(128 * Math.Pow(10, a / 200.0) * m);
            Level[k] = (byte)Math.Min(127.0, v);
        }
        Level[602] = 126;
        Level[662] = 126;
        Level[722] = 126;

        Decibel[0] = -32768;
        for (int x = 1; x < 128; x++)
            Decibel[x] = (short)Math.Max(-722.0, RoundNearest(400 * Math.Log10(x / 127.0)));

        for (int i = 0; i <= 32; i++)
            QuarterSine[i] = (byte)RoundNearest(127 * Math.Sin(Math.PI * i / 64));

        SelfCheck();
    }

    static void Check(string table, bool ok)
    {
        if (!ok)
            throw new InvalidOperationException($"Sequencer table self-check failed: {table}");
    }

    static (long Sum, long WSum) Sums(Func<int, long> entry, int count)
    {
        long sum = 0, wsum = 0;
        for (int i = 0; i < count; i++)
        {
            long e = entry(i);
            sum += e;
            wsum += (i + 1) * e;
        }
        return (sum, wsum);
    }

    /// <summary>Part 3 self-checks (values from the specification).</summary>
    static void SelfCheck()
    {
        var p = Sums(i => Pitch[i], Pitch.Length);
        Check("P", Pitch[0] == 0 && Pitch[1] == 59 && Pitch[767] == 65418 && p.Sum == 22_248_808 && p.WSum == 11_748_945_286);
        var v = Sums(i => Level[i], Level.Length);
        Check("V", Level[0] == 0 && Level[1] == 1 && Level[482] == 127 && Level[483] == 32 && Level[723] == 127 &&
            v.Sum == 30_610 && v.WSum == 16_515_296);
        var d = Sums(i => Decibel[i], Decibel.Length);
        Check("D", Decibel[1] == -722 && Decibel[2] == -721 && Decibel[3] == -651 && Decibel[127] == 0 &&
            d.Sum == -54_129 && d.WSum == -754_298);
        var s = Sums(i => QuarterSine[i], QuarterSine.Length);
        Check("S", QuarterSine[1] == 6 && QuarterSine[3] == 19 && QuarterSine[32] == 127 && s.Sum == 2_653 && s.WSum == 57_454);
    }

    /// <summary>Forces the static constructor (and so the self-checks) to run.</summary>
    internal static void EnsureBuilt() { }

    /// <summary>3.3 decibel-square function dB(n), n = 0..255.</summary>
    internal static short DecibelSquare(int n)
    {
        if ((n & 0x80) != 0)
            n = 127;
        return Decibel[n];
    }

    /// <summary>3.4 sine lookup, x = 0..127.</summary>
    internal static int Sine(int x)
    {
        if (x < 32)
            return QuarterSine[x];
        if (x < 64)
            return QuarterSine[64 - x];
        if (x < 96)
            return -QuarterSine[x - 64];
        return -QuarterSine[32 - (x - 96)];
    }

    /// <summary>3.5 square-wave shape for duty d and step j.</summary>
    internal static float Square(int duty, int step) => duty < 7 && step >= 7 - duty ? 1f : -1f;

    /// <summary>3.7 attack coefficient, r = 0..127.</summary>
    internal static byte Attack(int r) => r < 109 ? (byte)(255 - r) : FastAttack[127 - r];

    /// <summary>3.7 rate coefficient, r = 0..255.</summary>
    internal static ushort Rate(int r)
    {
        if ((r & 0x80) != 0)
            r = 0;
        if (r == 127)
            return 65535;
        if (r == 126)
            return 15360;
        if (r < 50)
            return (ushort)(2 * r + 1);
        return (ushort)(7680 / (126 - r));
    }

    /// <summary>3.8 volume packing: low byte = level, high byte = divider code.</summary>
    internal static ushort Pack(int v)
    {
        v = Math.Clamp(v, -723, 0);
        int c = v < -240 ? 3 : v < -120 ? 2 : v < -60 ? 1 : 0;
        return (ushort)(Level[v + 723] | (c << 8));
    }

    /// <summary>3.8 divider factor f(c).</summary>
    internal static float Divider(int c) => c switch
    {
        1 => 0.5f,
        2 => 0.25f,
        3 => 0.0625f,
        _ => 1f,
    };

    /// <summary>3.8 scale-by-127ths s7(x, n).</summary>
    internal static float Scale127(float x, int n) => n == 127 ? x : (x * (float)n) * 0.0078125f;

    /// <summary>3.9 timer from pitch.</summary>
    internal static ushort Timer(ushort baseTimer, int p)
    {
        int q = -p;
        int oct = 0;
        while (q < 0)
        {
            oct -= 1;
            q += 768;
        }
        while (q >= 768)
        {
            oct += 1;
            q -= 768;
        }
        ulong r = (ulong)(Pitch[q] + 65536) * baseTimer;
        int sh = oct - 16;
        if (sh <= 0)
            r >>= -sh;
        else if (sh < 32)
        {
            if ((r >> (32 - sh)) != 0)
                return 65535;
            r <<= sh;
        }
        else
            return 65535;
        return (ushort)Math.Clamp(r, 16UL, 65535UL);
    }
}

/// <summary>Spec 8.3: the static sinc and window tables (built on first use of sinc interpolation).</summary>
internal static class SincTables
{
    internal const int Count = 65537;
    internal static readonly float[] Kernel = new float[Count];
    internal static readonly float[] Window = new float[Count];

    static SincTables()
    {
        for (int i = 0; i < Count; i++)
        {
            float x = i * (1f / 8192);
            float y = x / 8;
            if (i == Count - 1)
                Kernel[i] = 0f;
            else
                Kernel[i] = x == 0 ? 1f : MathF.Sin(x * MathF.PI) / (x * MathF.PI);
            Window[i] = (0.40897f + (0.5f * MathF.Cos(MathF.PI * y))) + (0.09103f * MathF.Cos((2f * MathF.PI) * y));
        }
    }
}

/// <summary>Spec 3.10: one random generator shared by every engine in the process.</summary>
internal static class SharedRandom
{
    static uint state = 0x12345678;

    internal static int Draw()
    {
        unchecked
        {
            state = state * 1664525 + 1013904223;
        }
        return (int)(state >> 16);
    }
}
