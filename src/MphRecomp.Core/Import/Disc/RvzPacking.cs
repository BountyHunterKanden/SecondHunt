using System;
using System.Buffers.Binary;
using System.IO;

namespace MphRecomp.Import.Disc
{
    // RVZ packing (docs/WiaAndRvz.md in the dolphin-emu repository, "RVZ packing"): a group's data is a run of
    // segments, each a big-endian u32 size; with the top bit clear, that many stored bytes follow; with it set, a
    // 68-byte seed follows and the bytes are regenerated with the junk generator Nintendo's mastering tools used to
    // fill unused disc space. Written from that description.
    public static class RvzPacking
    {
        // unpacks src into dst (dst.Length bytes); dataOffset = where dst starts: a disc offset for raw data, or an
        // offset in the partition's hash-free data for Wii partition data. Returns the bytes of src used.
        public static int Unpack(ReadOnlySpan<byte> src, Span<byte> dst, long dataOffset, JunkGenerator junk)
        {
            int s = 0, d = 0;
            while (d < dst.Length)
            {
                if (s + 4 > src.Length) throw new InvalidDataException("rvz: packed data ends early");
                uint size = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(s));
                s += 4;
                bool isJunk = (size & 0x80000000) != 0;
                size &= 0x7FFFFFFF;
                if (size > dst.Length - d) throw new InvalidDataException("rvz: packed segment past the group end");
                int n = (int)size;
                if (isJunk)
                {
                    if (s + JunkGenerator.SeedBytes > src.Length) throw new InvalidDataException("rvz: packed data ends early");
                    junk.Seed(src.Slice(s, JunkGenerator.SeedBytes));
                    s += JunkGenerator.SeedBytes;
                    junk.Skip((int)((dataOffset + d) % 0x8000));
                    junk.Generate(dst.Slice(d, n));
                }
                else
                {
                    if (s + n > src.Length) throw new InvalidDataException("rvz: packed data ends early");
                    src.Slice(s, n).CopyTo(dst.Slice(d));
                    s += n;
                }
                d += n;
            }
            return s;
        }
    }

    // The lagged Fibonacci generator of RVZ junk data, as docs/WiaAndRvz.md describes it: xor, j = 32, k = 521; the
    // 521-word state is filled from 17 big-endian seed words by buffer[i] = (buffer[i-17] << 23) ^ (buffer[i-16] >> 9)
    // ^ buffer[i-1], advanced 4 times before any output and once after every 521 words; each word gives the bytes
    // w >> 24, w >> 18 (sic, not 16), w >> 8, w. Not thread-safe.
    public sealed class JunkGenerator
    {
        public const int SeedBytes = 17 * 4;
        const int K = 521, J = 32, BlockBytes = K * 4;

        readonly uint[] _w = new uint[K];
        readonly byte[] _out = new byte[BlockBytes];
        int _pos;

        public void Seed(ReadOnlySpan<byte> seed)
        {
            for (int i = 0; i < 17; i++) _w[i] = BinaryPrimitives.ReadUInt32BigEndian(seed.Slice(i * 4));
            for (int i = 17; i < K; i++) _w[i] = (_w[i - 17] << 23) ^ (_w[i - 16] >> 9) ^ _w[i - 1];
            for (int i = 0; i < 4; i++) Advance();
            Render();
            _pos = 0;
        }

        void Advance()
        {
            uint[] w = _w;
            for (int i = 0; i < J; i++) w[i] ^= w[i + K - J];
            for (int i = J; i < K; i++) w[i] ^= w[i - J];
        }

        void Render()
        {
            for (int i = 0; i < K; i++)
            {
                uint w = _w[i];
                int o = i * 4;
                _out[o] = (byte)(w >> 24);
                _out[o + 1] = (byte)(w >> 18);
                _out[o + 2] = (byte)(w >> 8);
                _out[o + 3] = (byte)w;
            }
        }

        public void Skip(int bytes)
        {
            _pos += bytes;
            if (_pos >= BlockBytes)
            {
                while (_pos >= BlockBytes)
                {
                    Advance();
                    _pos -= BlockBytes;
                }
                Render();
            }
        }

        public void Generate(Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                int n = Math.Min(dst.Length, BlockBytes - _pos);
                _out.AsSpan(_pos, n).CopyTo(dst);
                dst = dst.Slice(n);
                _pos += n;
                if (_pos == BlockBytes)
                {
                    Advance();
                    Render();
                    _pos = 0;
                }
            }
        }
    }
}
