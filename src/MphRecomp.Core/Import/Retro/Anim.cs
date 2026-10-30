using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // Retro ANIM decoder (Metroid Prime 1 + Echoes): the compressed "type 2" and uncompressed "type 0" layouts, decoded
    // to per-bone key lists (rotation quaternions x y z w, translations, Echoes scales) and sampled like
    // CAnimation::EvaluateTransform. Ported from prime_anim.py, itself a port of PrimeWorldEditor's CAnimationLoader and
    // its LibCommon CBitStreamInWrapper (32-bit big-endian words, bits taken LSB first, sign-extended).
    //   PrimeWorldEditor / LibCommon: https://github.com/AxioDL/PrimeWorldEditor -- MIT License, Copyright (c) AxioDL.
    // All arithmetic is double precision, as the Python's is.
    public sealed class Anim
    {
        public double Duration, Tick;
        public int Keys;
        // bone id -> per-key values
        public readonly Dictionary<uint, List<double[]>> Rot = new(), Trans = new(), Scale = new();

        sealed class BitStream
        {
            readonly byte[] _d;
            public int O;
            ulong _pool;
            int _left;

            public BitStream(byte[] d, int o) { _d = d; O = o; }

            void Refill()
            {
                _pool = Be.U32(_d, O); O += 4; _left = 32;
            }

            public long Bits(int n, bool signed = true)
            {
                if (n == 0) return 0;
                int need = n, shift = 0;
                ulong outv = 0;
                while (need > 0)
                {
                    if (_left < need)
                    {
                        if (shift < 32) outv |= (_pool << shift) & 0xFFFFFFFF;
                        need -= _left; shift += _left;
                        Refill();
                    }
                    else
                    {
                        ulong part = _pool & ((1UL << need) - 1);
                        if (shift < 64) outv |= part << shift;
                        _pool >>= need; _left -= need; need = 0;
                    }
                }
                outv &= 0xFFFFFFFF;
                if (signed && n <= 32 && ((outv >> (n - 1)) & 1) != 0)
                {
                    outv |= (0xFFFFFFFFUL << n) & 0xFFFFFFFF;
                    return (long)outv - (1L << 32);
                }
                return (long)outv;
            }

            public bool Bit() => Bits(1, signed: false) != 0;
        }

        static int S16(long v)
        {
            v &= 0xFFFF;
            return (int)((v & 0x8000) != 0 ? v - 0x10000 : v);
        }

        // Python list indexing: a negative index counts from the end
        static T At<T>(List<T> l, int i) => l[i < 0 ? i + l.Count : i];

        public static double[] Slerp(double[] a, double[] b, double t)
        {
            double d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            if (d < 0)
            {
                b = new[] { -b[0], -b[1], -b[2], -b[3] };
                d = -d;
            }
            if (d > 0.9995)
            {
                var r = new double[4];
                for (int i = 0; i < 4; i++) r[i] = a[i] + (b[i] - a[i]) * t;
                double len = Math.Sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3]);
                for (int i = 0; i < 4; i++) r[i] /= len;
                return r;
            }
            double th = Math.Acos(Math.Min(1.0, d)), s = Math.Sin(th);
            double sa = Math.Sin((1 - t) * th), sb = Math.Sin(t * th);
            var q = new double[4];
            for (int i = 0; i < 4; i++) q[i] = (sa * a[i] + sb * b[i]) / s;
            return q;
        }

        static double[] Lerp(double[] a, double[] b, double t)
        {
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * t;
            return r;
        }

        public static Anim Load(byte[] a, bool echoes)
        {
            uint kind = Be.U32(a, 0);
            if (kind == 2) return Compressed(a, echoes);
            if (kind == 0) return Uncompressed(a, echoes);
            throw new InvalidDataException($"ANIM compression type {kind}");
        }

        sealed class Channel
        {
            public uint Bone;
            public int[]? RotV, RotBits, TransV, TransBits, ScaleV, ScaleBits;
        }

        static Anim Compressed(byte[] a, bool echoes)
        {
            int o = 4 + 4;                                  // type, alloc size
            if (!echoes) o += 4;                            // EVNT id (MP1)
            o += echoes ? 2 : 4;                            // unknowns
            double duration = Be.F32(a, o), tick = Be.F32(a, o + 4); o += 8;
            o += 8;                                         // two unknowns
            uint rotDiv = Be.U32(a, o);
            double transMul = Be.F32(a, o + 4); o += 8;
            double scaleMul = 1.0;
            if (echoes)
            {
                scaleMul = Be.F32(a, o); o += 4;
            }
            int nchan = checked((int)Be.U32(a, o)); o += 8; // + unknown
            int nkeys = checked((int)Be.U32(a, o)); o += 4;
            var bs = new BitStream(a, o);
            var flags = new bool[nkeys];
            for (int k = 0; k < nkeys; k++) flags[k] = bs.Bit();
            o = bs.O + (echoes ? 4 : 8);
            var chans = new List<Channel>();
            for (int c = 0; c < nchan; c++)
            {
                var ch = new Channel();
                if (echoes) { ch.Bone = a[o]; o += 1; }
                else { ch.Bone = Be.U32(a, o); o += 4; }
                for (int kind = 0; kind < (echoes ? 3 : 2); kind++)
                {
                    int n = Be.U16(a, o); o += 2;
                    if (n == 0) continue;
                    var vals = new int[3];
                    var bits = new int[3];
                    for (int i = 0; i < 3; i++)
                    {
                        vals[i] = Be.I16(a, o); bits[i] = a[o + 2]; o += 3;
                    }
                    if (kind == 0) { ch.RotV = vals; ch.RotBits = bits; }
                    else if (kind == 1) { ch.TransV = vals; ch.TransBits = bits; }
                    else { ch.ScaleV = vals; ch.ScaleBits = bits; }
                }
                chans.Add(ch);
            }
            double mult = (Math.PI / 2) / rotDiv;
            double[] Deq(bool sign, int[] v)
            {
                double x = Math.Sin(v[0] * mult), y = Math.Sin(v[1] * mult), z = Math.Sin(v[2] * mult);
                double w = Math.Sqrt(Math.Max(1 - x * x - y * y - z * z, 0));
                return new[] { x, y, z, sign ? -w : w };
            }
            double[] Mul(int[] v, double m) => new[] { v[0] * m, v[1] * m, v[2] * m };

            var anim = new Anim { Duration = duration, Tick = tick, Keys = nkeys };
            foreach (Channel ch in chans)
            {
                if (ch.RotV != null) anim.Rot[ch.Bone] = new List<double[]> { Deq(false, ch.RotV) };
                if (ch.TransV != null) anim.Trans[ch.Bone] = new List<double[]> { Mul(ch.TransV, transMul) };
                if (ch.ScaleV != null) anim.Scale[ch.Bone] = new List<double[]> { Mul(ch.ScaleV, scaleMul) };
            }
            bs = new BitStream(a, o);
            for (int k = 0; k < nkeys - 1; k++)
            {
                bool present = flags[k + 1];
                foreach (Channel ch in chans)
                {
                    if (ch.RotV != null)
                    {
                        bool sign = present && bs.Bit();
                        if (present)
                        {
                            for (int i = 0; i < 3; i++) ch.RotV[i] = S16(ch.RotV[i] + bs.Bits(ch.RotBits![i]));
                        }
                        anim.Rot[ch.Bone].Add(Deq(sign, ch.RotV));
                    }
                    if (ch.TransV != null)
                    {
                        if (present)
                        {
                            for (int i = 0; i < 3; i++) ch.TransV[i] = S16(ch.TransV[i] + bs.Bits(ch.TransBits![i]));
                        }
                        anim.Trans[ch.Bone].Add(Mul(ch.TransV, transMul));
                    }
                    if (ch.ScaleV != null)
                    {
                        if (present)
                        {
                            for (int i = 0; i < 3; i++) ch.ScaleV[i] = S16(ch.ScaleV[i] + bs.Bits(ch.ScaleBits![i]));
                        }
                        anim.Scale[ch.Bone].Add(Mul(ch.ScaleV, scaleMul));
                    }
                }
            }
            // keys the stream skipped: interpolate between the neighbours that were stored
            int missed = 0;
            for (int k = 0; k < nkeys; k++)
            {
                if (!flags[k])
                {
                    missed++;
                }
                else if (missed > 0)
                {
                    int first = k - missed - 1, last = k;
                    for (int m = 0; m < missed; m++)
                    {
                        int ki = first + m + 1;
                        double t = (double)(ki - first) / (last - first);
                        foreach (List<double[]> keys in anim.Rot.Values)
                        {
                            keys[ki] = Slerp(At(keys, first), keys[last], t);
                        }
                        foreach (Dictionary<uint, List<double[]>> table in new[] { anim.Trans, anim.Scale })
                        {
                            foreach (List<double[]> keys in table.Values)
                            {
                                keys[ki] = Lerp(At(keys, first), keys[last], t);
                            }
                        }
                    }
                    missed = 0;
                }
            }
            return anim;
        }

        static Anim Uncompressed(byte[] a, bool echoes)
        {
            int o = 4;
            double duration = Be.F32(a, o); o += 8;
            double tick = Be.F32(a, o); o += 8;
            int nkeys = checked((int)Be.U32(a, o)); o += 8;
            int nbi = checked((int)Be.U32(a, o)); o += 4;
            byte[] boneIdx = Be.Slice(a, o, nbi); o += nbi;
            List<int> rotIdx;
            if (echoes)
            {
                int n = checked((int)Be.U32(a, o)); o += 4;
                rotIdx = new List<int>(Array.ConvertAll(Be.Slice(a, o, n), x => (int)x)); o += n;
            }
            else
            {
                rotIdx = new List<int>();
                foreach (byte i in boneIdx) if (i != 0xFF) rotIdx.Add(i);
            }
            int nt = checked((int)Be.U32(a, o)); o += 4;
            var transIdx = new List<int>(Array.ConvertAll(Be.Slice(a, o, nt), x => (int)x)); o += nt;
            var scaleIdx = new List<int>();
            if (echoes)
            {
                int n = checked((int)Be.U32(a, o)); o += 4;
                scaleIdx = new List<int>(Array.ConvertAll(Be.Slice(a, o, n), x => (int)x)); o += n;
            }
            int nrot = rotIdx.FindAll(i => i != 0xFF).Count, ntrans = transIdx.FindAll(i => i != 0xFF).Count;
            int nscale = scaleIdx.FindAll(i => i != 0xFF).Count;
            double[] Vec3(int at) => new double[] { Be.F32(a, at), Be.F32(a, at + 4), Be.F32(a, at + 8) };
            var scaleCh = new List<List<double[]>>();
            if (echoes)
            {
                o += 4;
                for (int c = 0; c < nscale; c++)
                {
                    var ks = new List<double[]>();
                    for (int k = 0; k < nkeys; k++) ks.Add(Vec3(o + 12 * k));
                    scaleCh.Add(ks);
                    o += 12 * nkeys;
                }
            }
            o += 4;
            var rotCh = new List<List<double[]>>();
            for (int c = 0; c < nrot; c++)
            {
                var ks = new List<double[]>();
                for (int k = 0; k < nkeys; k++)
                {
                    double w = Be.F32(a, o), x = Be.F32(a, o + 4), y = Be.F32(a, o + 8), z = Be.F32(a, o + 12);
                    o += 16;
                    ks.Add(new[] { x, y, z, w });
                }
                rotCh.Add(ks);
            }
            o += 4;
            var transCh = new List<List<double[]>>();
            for (int c = 0; c < ntrans; c++)
            {
                var ks = new List<double[]>();
                for (int k = 0; k < nkeys; k++) ks.Add(Vec3(o + 12 * k));
                transCh.Add(ks);
                o += 12 * nkeys;
            }
            var anim = new Anim { Duration = duration, Tick = tick, Keys = nkeys };
            int chan = 0;
            for (int bone = 0; bone < boneIdx.Length; bone++)
            {
                if (boneIdx[bone] == 0xFF) continue;
                if (chan < rotIdx.Count && rotIdx[chan] != 0xFF) anim.Rot[(uint)bone] = rotCh[rotIdx[chan]];
                if (chan < transIdx.Count && transIdx[chan] != 0xFF) anim.Trans[(uint)bone] = transCh[transIdx[chan]];
                if (scaleIdx.Count > 0 && chan < scaleIdx.Count && scaleIdx[chan] != 0xFF) anim.Scale[(uint)bone] = scaleCh[scaleIdx[chan]];
                chan++;
            }
            return anim;
        }

        // (rotation, translation) per bone at time t (seconds), interpolated between keys like CAnimation::EvaluateTransform
        public void Sample(double t, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> trans)
        {
            t = Math.Min(Math.Max(t, 0.0), Duration);
            if (t >= 1e-7) t -= 1e-7;
            int lo = (int)(t / Tick);
            double f = (t % Tick) / Tick;
            if (lo >= Keys - 1)
            {
                lo = Keys - 2; f = 1.0;
            }
            rot = new Dictionary<uint, double[]>(Rot.Count);
            foreach (KeyValuePair<uint, List<double[]>> kv in Rot)
            {
                rot[kv.Key] = Slerp(At(kv.Value, lo), At(kv.Value, lo + 1), f);
            }
            trans = new Dictionary<uint, double[]>(Trans.Count);
            foreach (KeyValuePair<uint, List<double[]>> kv in Trans)
            {
                trans[kv.Key] = Lerp(At(kv.Value, lo), At(kv.Value, lo + 1), f);
            }
        }
    }
}
