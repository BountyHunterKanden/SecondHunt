using System;
using System.Collections.Generic;
using System.IO;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // Metroid Prime 3: Corruption ANIM decoder ("version 1", the bitstream kind -- the only one in MP3's SamusGun.pak),
    // returning the same Retro.Anim as the MP1/2 decoder (keys evenly Tick apart; bone ids = the MP3 CINF's), so its
    // Sample() and the gun exporter work on it unchanged. Port of prime3_anim.py (parse_anim3 / load_anim3).
    //
    // FORMAT (all big-endian):
    //   0x00 u32 version = 1
    //   0x04 u16 (4)  0x06 u8 (0)  0x07 u16 (0)  0x09 u8 channel kinds present (3 = rotation + translation, 7 = + scale)
    //   0x0A f32 rotation multiplier (1/32767)  0x0E f32 translation multiplier (0.001)  0x12 f32 scale multiplier
    //   0x16 u32 0   0x1A f32 0   0x1E f32 0?   0x22 f32 duration (s)   0x26 u16 key count (incl. the initial key)
    //   0x28 u32 size of everything after the 0x45-byte header
    //   0x2C u8 2..5, unknown   0x2D u32 key-times bytes   0x31 u32 channel-flag bytes
    //   0x35 u32 init-block bytes   0x39 u32 stream-block bytes   0x3D u32 (3)   0x41 u32 bone count + 1
    //   0x45 f32[keys]  key times in seconds (explicit; frames may be skipped = key reduction)
    //        u8 n, u8[n] channel flags: channel i drives CINF bone id i; 1 = rotation, 2 = translation, 4 = scale
    //        s16[] init block, bone-major: rotation 4 values [w-sign field, x, y, z], translation 3, scale 3
    //        stream block: u32 value count (= init s16 count), u32 stream word count, u32 value count * (keys-1),
    //                      u8[value count] bit width of each value's delta, then the stream words
    //        u32 n, u8[n]  per channel: 1 = channel not (near) identity/zero
    //   stream: 32-bit big-endian words, bits taken LSB-first (MP1/2's CBitStreamInWrapper). Per key after the first:
    //        1 head bit, then every value's signed two's-complement delta, in init-block order; value += delta
    //        (running integers). Rotation: x,y,z = v * rot mult, w = +sqrt(1-x^2-y^2-z^2), negated when the w-sign field
    //        is odd. Translation = v * translation mult: the ABSOLUTE local position (parent space). Scale = v * scale mult.
    // Key reduction: missing frames are rebuilt by slerp / lerp between the stored neighbours, resampled onto the
    // coarsest 1/30, 1/60 or 1/120 s grid holding every stored key.
    // Sources: Retro Modding Wiki "ANIM (Metroid Prime 3)" (layout); metaforce DataSpec DNAMP3/ANIM.cpp +
    // DNACommon/ANIM.cpp (ANIM1 reader, RotationMP3 / KfHead channels, DequantizeRotation_3) --
    // https://github.com/AxioDL/metaforce, MIT License, Copyright (c) AxioDL. This is a clean re-implementation.
    public static class Anim3
    {
        public sealed class Raw
        {
            public int Kinds, Reduced, Bones;
            public double RotMul, TransMul, ScaleMul, Duration;
            public uint Blob;
            public double[] Times = Array.Empty<double>();
            public byte[] Flags = Array.Empty<byte>();
            public byte[] Widths = Array.Empty<byte>();
            public byte[] Live = Array.Empty<byte>();
            public readonly List<int> Heads = new();
            public long[,] Values = new long[0, 0];     // keys x values: every value's running integer per key
            // per channel (= bone id): the first column of its rotation (4) / translation (3) / scale (3) values, or -1
            public int[] RotCol = Array.Empty<int>(), TransCol = Array.Empty<int>(), ScaleCol = Array.Empty<int>();
            public int KeyCount => Times.Length;
        }

        // 32-bit big-endian words, bits taken least-significant first, signed values sign-extended
        sealed class Bits
        {
            readonly byte[] _d;
            readonly int _end;
            int _o;
            ulong _pool;
            int _left;

            public Bits(byte[] d, int start, int end) { _d = d; _o = start; _end = end; }

            public long Read(int n, bool signed = true)
            {
                if (n == 0) return 0;
                ulong outv = 0;
                int shift = 0, need = n;
                while (need > 0)
                {
                    if (_left == 0)
                    {
                        if (_o + 4 > _end) throw new InvalidDataException("ANIM: stream shorter than the keys need");
                        _pool = Be.U32(_d, _o); _o += 4; _left = 32;
                    }
                    int take = Math.Min(need, _left);
                    ulong part = _pool & ((1UL << take) - 1);
                    if (shift < 64) outv |= part << shift;
                    _pool >>= take; _left -= take; need -= take; shift += take;
                }
                long v = (long)outv;
                if (signed && n <= 63 && ((outv >> (n - 1)) & 1) != 0) v -= 1L << n;
                return v;
            }
        }

        // the ANIM as stored: header fields, key times, channel flags and every value's integer track
        public static Raw Parse(byte[] a)
        {
            uint ver = Be.U32(a, 0);
            if (ver != 1) throw new InvalidDataException($"MP3 ANIM version {ver}: only version 1 (bitstream) is decoded");
            var r = new Raw
            {
                Kinds = a[0x09], RotMul = Be.F32(a, 0x0A), TransMul = Be.F32(a, 0x0E), ScaleMul = Be.F32(a, 0x12),
                Duration = Be.F32(a, 0x22), Blob = Be.U32(a, 0x28), Reduced = a[0x2C],
            };
            int nkeys = Be.U16(a, 0x26);
            int ktSz = checked((int)Be.U32(a, 0x2D)), cfSz = checked((int)Be.U32(a, 0x31)), initSz = checked((int)Be.U32(a, 0x35));
            int stSz = checked((int)Be.U32(a, 0x39));
            r.Bones = checked((int)Be.U32(a, 0x41));
            if (0x45 + (long)r.Blob != a.Length) throw new InvalidDataException("ANIM: size field disagrees with the file");
            int o = 0x45;
            r.Times = new double[nkeys];
            for (int k = 0; k < nkeys; k++) r.Times[k] = Be.F32(a, o + 4 * k);
            o += ktSz;
            int nch = a[o];
            r.Flags = Be.Slice(a, o + 1, nch);
            o += cfSz;
            int nInit = initSz / 2;
            var init = new long[nInit];
            for (int i = 0; i < nInit; i++) init[i] = Be.I16(a, o + 2 * i);
            o += initSz;
            int s0 = o;
            int nval = checked((int)Be.U32(a, o)), nwords = checked((int)Be.U32(a, o + 4));
            long total = Be.U32(a, o + 8);
            o += 12;
            r.Widths = Be.Slice(a, o, nval);
            o += nval;
            int streamStart = o, streamEnd = s0 + stSz;
            o = s0 + stSz;
            int nb = checked((int)Be.U32(a, o));
            r.Live = Be.Slice(a, o + 4, nb);
            o += 4 + nb;
            if (o != a.Length || nval != init.Length || total != (long)nval * (nkeys - 1)) throw new InvalidDataException("ANIM: layout mismatch");
            long perKey = 1;
            foreach (byte w in r.Widths) perKey += w;
            long streamBytes = Math.Max(0, streamEnd - streamStart);
            if (!((nkeys - 1) * perKey <= 32L * nwords && 32L * nwords == streamBytes * 8))
            {
                throw new InvalidDataException("ANIM: stream shorter than the keys need");
            }
            r.Values = new long[nkeys, nval];
            for (int i = 0; i < nval; i++) r.Values[0, i] = init[i];
            var bs = new Bits(a, streamStart, streamEnd);
            var cur = (long[])init.Clone();
            for (int k = 1; k < nkeys; k++)
            {
                r.Heads.Add((int)bs.Read(1, signed: false));
                for (int i = 0; i < nval; i++) cur[i] += bs.Read(r.Widths[i]);
                for (int i = 0; i < nval; i++) r.Values[k, i] = cur[i];
            }
            // split the value columns into per-bone channels
            r.RotCol = new int[r.Flags.Length]; r.TransCol = new int[r.Flags.Length]; r.ScaleCol = new int[r.Flags.Length];
            int col = 0;
            for (int bone = 0; bone < r.Flags.Length; bone++)
            {
                int f = r.Flags[bone];
                r.RotCol[bone] = r.TransCol[bone] = r.ScaleCol[bone] = -1;
                if ((f & 1) != 0) { r.RotCol[bone] = col; col += 4; }
                if ((f & 2) != 0) { r.TransCol[bone] = col; col += 3; }
                if ((f & 4) != 0) { r.ScaleCol[bone] = col; col += 3; }
            }
            if (col > nval) throw new InvalidDataException($"ANIM: channels need {col} values, the file has {nval}");
            return r;
        }

        static double[] Quat(long[,] v, int k, int c, double mul)
        {
            double x = v[k, c + 1] * mul, y = v[k, c + 2] * mul, z = v[k, c + 3] * mul;
            double w = Math.Sqrt(Math.Max(1.0 - (x * x + y * y + z * z), 0.0));
            if ((v[k, c] & 1) != 0) w = -w;
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new[] { x / n, y / n, z / n, w / n };
        }

        // prime3_anim._slerp_rows for one row: slerp (or lerp when nearly equal), always renormalised
        static double[] SlerpRow(double[] a, double[] b, double t)
        {
            double d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            if (d < 0)
            {
                b = new[] { -b[0], -b[1], -b[2], -b[3] };
            }
            d = Math.Abs(d);
            var o = new double[4];
            if (d > 0.9995)
            {
                for (int i = 0; i < 4; i++) o[i] = a[i] + (b[i] - a[i]) * t;
            }
            else
            {
                double th = Math.Acos(Math.Clamp(d, -1.0, 1.0)), s = Math.Sin(th);
                double sa = Math.Sin((1 - t) * th), sb = Math.Sin(t * th);
                for (int i = 0; i < 4; i++) o[i] = (sa * a[i] + sb * b[i]) / s;
            }
            double n = Math.Sqrt(o[0] * o[0] + o[1] * o[1] + o[2] * o[2] + o[3] * o[3]);
            for (int i = 0; i < 4; i++) o[i] /= n;
            return o;
        }

        // numpy.searchsorted(T, t, side="right"): the first index whose time is > t
        static int SearchRight(double[] T, double t)
        {
            int lo = 0, hi = T.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (t < T[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        // the coarsest frame grid holding every stored key: 1/30 s for the gun (1/60 for two grapple anims)
        public static double FrameTick(double[] T)
        {
            foreach (int fps in new[] { 30, 60, 120 })
            {
                double worst = 0;
                foreach (double x in T) worst = Math.Max(worst, Math.Abs(x * fps - Math.Round(x * fps, MidpointRounding.ToEven)));
                if (T.Length > 0 && worst < 1e-3) return 1.0 / fps;
            }
            double best = Double.PositiveInfinity;
            for (int i = 1; i < T.Length; i++)
            {
                double d = T[i] - T[i - 1];
                if (d > 1e-6) best = Math.Min(best, d);
            }
            return Double.IsPositiveInfinity(best) ? 1.0 / 30 : best;
        }

        // prime3_anim.load_anim3: keys evenly `tick` apart (default: the file's frame grid)
        public static Retro.Anim Load(byte[] a, double? tickArg = null) => FromRaw(Parse(a), tickArg);

        public static Retro.Anim FromRaw(Raw raw, double? tickArg = null)
        {
            double[] T = raw.Times;
            double dur = raw.Duration;
            double tick = tickArg ?? FrameTick(T);
            int n = Math.Max(2, (int)Math.Round(dur / tick, MidpointRounding.ToEven) + 1);
            var lo = new int[n];
            var hi = new int[n];
            var f = new double[n];
            for (int k = 0; k < n; k++)
            {
                double t = Math.Min(k * tick, T[^1]);
                if (T.Length > 1)
                {
                    int h = Math.Clamp(SearchRight(T, t), 1, T.Length - 1);
                    hi[k] = h; lo[k] = h - 1;
                    f[k] = Math.Clamp((t - T[h - 1]) / Math.Max(T[h] - T[h - 1], 1e-9), 0.0, 1.0);
                }
            }
            var anim = new Retro.Anim { Duration = dur, Tick = tick, Keys = n };
            int nk = raw.KeyCount;
            for (int bone = 0; bone < raw.Flags.Length; bone++)
            {
                if (raw.RotCol[bone] >= 0)
                {
                    var Q = new double[nk][];
                    for (int k = 0; k < nk; k++) Q[k] = Quat(raw.Values, k, raw.RotCol[bone], raw.RotMul);
                    var keys = new List<double[]>(n);
                    for (int k = 0; k < n; k++) keys.Add(SlerpRow(Q[lo[k]], Q[hi[k]], f[k]));
                    anim.Rot[(uint)bone] = keys;
                }
                foreach ((int c, double mul, Dictionary<uint, List<double[]>> table) in new[]
                    { (raw.TransCol[bone], raw.TransMul, anim.Trans), (raw.ScaleCol[bone], raw.ScaleMul, anim.Scale) })
                {
                    if (c < 0) continue;
                    var keys = new List<double[]>(n);
                    for (int k = 0; k < n; k++)
                    {
                        var v = new double[3];
                        for (int i = 0; i < 3; i++)
                        {
                            double va = raw.Values[lo[k], c + i] * mul, vb = raw.Values[hi[k], c + i] * mul;
                            v[i] = va + (vb - va) * f[k];
                        }
                        keys.Add(v);
                    }
                    table[(uint)bone] = keys;
                }
            }
            return anim;
        }
    }
}
