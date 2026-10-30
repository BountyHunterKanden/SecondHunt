using System;
using System.IO;

namespace MphRecomp.Import.Disc
{
    // A bzip2 decoder written from the format as bzip2 1.0 writes it (Julian Seward's bzip2 is BSD-style licensed;
    // the stream layout is described in its manual and in Joe Tsai's "bzip2 format specification"): "BZh1".."BZh9",
    // then blocks (0x314159265359: CRC, origPtr, symbol map, Huffman groups and MTF selectors, the MTF/RUNA/RUNB symbol
    // stream, inverse Burrows-Wheeler, the initial run-length stage) up to the end-of-stream mark 0x177245385090 and
    // the combined CRC. Concatenated streams are decoded in turn. Each block's CRC is checked. The long-obsolete
    // "randomised" blocks are not supported. An instance is not thread-safe.
    public sealed class Bzip2Decoder
    {
        const int MaxGroups = 6, MaxAlpha = 258, MaxCodeLen = 20, GroupSize = 50, MaxSelectors = 18002;

        static readonly uint[] CrcTable = MakeCrcTable();

        int[] _tt = Array.Empty<int>();
        readonly byte[] _selectors = new byte[MaxSelectors];
        readonly byte[,] _lengths = new byte[MaxGroups, MaxAlpha];
        // per group: canonical decode tables (limit / base / perm by code length)
        readonly int[,] _limit = new int[MaxGroups, MaxCodeLen + 2];
        readonly int[,] _base = new int[MaxGroups, MaxCodeLen + 2];
        readonly int[,] _perm = new int[MaxGroups, MaxAlpha];
        readonly int[] _minLen = new int[MaxGroups];

        static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                t[i] = c;
            }
            return t;
        }

        // MSB-first bit reader
        ref struct Bits
        {
            readonly ReadOnlySpan<byte> _s;
            int _pos;
            ulong _buf;
            int _n;

            public Bits(ReadOnlySpan<byte> s) { _s = s; _pos = 0; _buf = 0; _n = 0; }

            public readonly bool AtEnd => _pos >= _s.Length && _n == 0;
            public readonly long BitPosition => (long)_pos * 8 - _n;

            void Fill()
            {
                while (_n <= 56)
                {
                    if (_pos < _s.Length) _buf |= (ulong)_s[_pos++] << (56 - _n);
                    else if (_n == 0) throw new InvalidDataException("bzip2: truncated stream");
                    else return;
                    _n += 8;
                }
            }

            public uint Read(int count)
            {
                if (_n < count) Fill();
                if (_n < count) throw new InvalidDataException("bzip2: truncated stream");
                uint v = (uint)(_buf >> (64 - count));
                _buf <<= count;
                _n -= count;
                return v;
            }

            public int Bit() => (int)Read(1);

            // the next `count` bits without consuming them; missing bits past the end read as zero
            public uint Peek(int count)
            {
                if (_n < count)
                {
                    while (_n <= 56 && _pos < _s.Length)
                    {
                        _buf |= (ulong)_s[_pos++] << (56 - _n);
                        _n += 8;
                    }
                }
                return (uint)(_buf >> (64 - count));
            }

            public void Skip(int count)
            {
                if (count > _n) throw new InvalidDataException("bzip2: truncated stream");
                _buf <<= count;
                _n -= count;
            }

            public void AlignToByte()
            {
                int drop = _n & 7;
                _buf <<= drop;
                _n -= drop;
            }
        }

        public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            var br = new Bits(src);
            int outPos = 0;
            bool first = true;
            while (true)
            {
                if (!first)
                {
                    // another stream may follow (concatenated .bz2); anything else ends decoding
                    br.AlignToByte();
                    if (br.AtEnd || br.Peek(24) != 0x425A68) break;
                }
                first = false;
                if (br.Read(8) != 'B' || br.Read(8) != 'Z' || br.Read(8) != 'h') throw new InvalidDataException("bzip2: bad stream header");
                int level = (int)br.Read(8) - '0';
                if (level < 1 || level > 9) throw new InvalidDataException("bzip2: bad block size");
                int maxBlock = level * 100000;
                if (_tt.Length < maxBlock) _tt = new int[maxBlock];
                uint combined = 0;
                while (true)
                {
                    ulong magic = (ulong)br.Read(24) << 24 | br.Read(24);
                    if (magic == 0x177245385090)
                    {
                        uint streamCrc = br.Read(32);
                        if (streamCrc != combined) throw new InvalidDataException("bzip2: stream CRC mismatch");
                        break;
                    }
                    if (magic != 0x314159265359) throw new InvalidDataException("bzip2: bad block magic");
                    uint blockCrc = br.Read(32);
                    int start = outPos;
                    outPos = DecodeBlock(ref br, maxBlock, dst, outPos);
                    uint crc = 0xFFFFFFFF;
                    Span<byte> produced = dst.Slice(start, outPos - start);
                    for (int i = 0; i < produced.Length; i++) crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ produced[i]];
                    crc = ~crc;
                    if (crc != blockCrc) throw new InvalidDataException("bzip2: block CRC mismatch");
                    combined = ((combined << 1) | (combined >> 31)) ^ blockCrc;
                }
                if (outPos >= dst.Length) break;
            }
            return outPos;
        }

        int DecodeBlock(ref Bits br, int maxBlock, Span<byte> dst, int outPos)
        {
            if (br.Bit() != 0) throw new NotSupportedException("bzip2: randomised blocks are not supported");
            int origPtr = (int)br.Read(24);

            // symbol map: which byte values occur
            Span<byte> seqToUnseq = stackalloc byte[256];
            int nInUse = 0;
            uint used16 = br.Read(16);
            for (int i = 0; i < 16; i++)
            {
                if ((used16 & (0x8000u >> i)) == 0) continue;
                uint bits = br.Read(16);
                for (int j = 0; j < 16; j++)
                {
                    if ((bits & (0x8000u >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
                }
            }
            if (nInUse == 0) throw new InvalidDataException("bzip2: empty symbol map");
            int alphaSize = nInUse + 2;

            int nGroups = (int)br.Read(3);
            if (nGroups < 2 || nGroups > MaxGroups) throw new InvalidDataException("bzip2: bad group count");
            int nSelectors = (int)br.Read(15);
            if (nSelectors < 1) throw new InvalidDataException("bzip2: no selectors");
            Span<byte> mtfGroups = stackalloc byte[MaxGroups];
            for (int i = 0; i < nGroups; i++) mtfGroups[i] = (byte)i;
            int kept = 0;
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0;
                while (br.Bit() != 0)
                {
                    if (++j >= nGroups) throw new InvalidDataException("bzip2: bad selector");
                }
                // selectors are move-to-front coded; bzip2 1.0.8 ignores any past 18002
                byte g = mtfGroups[j];
                for (int k = j; k > 0; k--) mtfGroups[k] = mtfGroups[k - 1];
                mtfGroups[0] = g;
                if (i < MaxSelectors) _selectors[kept++] = g;
            }
            nSelectors = kept;

            // code lengths, delta coded
            for (int t = 0; t < nGroups; t++)
            {
                int curr = (int)br.Read(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > MaxCodeLen) throw new InvalidDataException("bzip2: bad code length");
                        if (br.Bit() == 0) break;
                        curr += br.Bit() == 0 ? 1 : -1;
                    }
                    _lengths[t, i] = (byte)curr;
                }
                BuildTable(t, alphaSize);
            }

            // the MTF / RUNA-RUNB symbol stream
            int eob = nInUse + 1;
            Span<byte> mtf = stackalloc byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            Span<int> counts = stackalloc int[256];
            counts.Clear();
            int[] tt = _tt;
            int nblock = 0;
            int groupLeft = 0, selector = 0, group = 0;
            int run = 0, runWeight = 1;
            bool inRun = false;
            while (true)
            {
                if (groupLeft == 0)
                {
                    if (selector >= nSelectors) throw new InvalidDataException("bzip2: ran out of selectors");
                    group = _selectors[selector++];
                    groupLeft = GroupSize;
                }
                groupLeft--;
                int sym = DecodeSymbol(ref br, group);
                if (sym <= 1)
                {
                    // RUNA / RUNB: a bijective base-2 run length of the front symbol
                    if (!inRun) { inRun = true; run = 0; runWeight = 1; }
                    run += runWeight << sym;
                    runWeight <<= 1;
                    if (run > maxBlock) throw new InvalidDataException("bzip2: run too long");
                    continue;
                }
                if (inRun)
                {
                    inRun = false;
                    if (nblock + run > maxBlock) throw new InvalidDataException("bzip2: block too large");
                    byte b = seqToUnseq[mtf[0]];
                    counts[b] += run;
                    tt.AsSpan(nblock, run).Fill(b);
                    nblock += run;
                }
                if (sym == eob) break;
                {
                    int idx = sym - 1;
                    byte v = mtf[idx];
                    for (int k = idx; k > 0; k--) mtf[k] = mtf[k - 1];
                    mtf[0] = v;
                    byte b = seqToUnseq[v];
                    if (nblock >= maxBlock) throw new InvalidDataException("bzip2: block too large");
                    counts[b]++;
                    tt[nblock++] = b;
                }
            }
            if (origPtr >= nblock) throw new InvalidDataException("bzip2: bad origPtr");

            // inverse BWT: link each position to the next in the original order (high 24 bits of tt)
            Span<int> cftab = stackalloc int[256];
            int sum = 0;
            for (int i = 0; i < 256; i++) { cftab[i] = sum; sum += counts[i]; }
            for (int i = 0; i < nblock; i++)
            {
                int b = tt[i] & 0xFF;
                tt[cftab[b]++] |= i << 8;
            }

            // walk the chain and undo the initial run-length stage (4 equal bytes, then a repeat count)
            int tPos = tt[origPtr] >> 8;
            int last = -1, same = 0;
            int op = outPos;
            for (int k = 0; k < nblock; k++)
            {
                int e = tt[tPos];
                int ch = e & 0xFF;
                tPos = e >> 8;
                if (same == 4)
                {
                    if (op + ch > dst.Length) throw new InvalidDataException("bzip2: output too small");
                    dst.Slice(op, ch).Fill((byte)last);
                    op += ch;
                    same = 0;
                    last = -1;
                    continue;
                }
                if (ch == last) same++;
                else { last = ch; same = 1; }
                if (op >= dst.Length) throw new InvalidDataException("bzip2: output too small");
                dst[op++] = (byte)ch;
            }
            return op;
        }

        void BuildTable(int t, int alphaSize)
        {
            int minLen = 32, maxLen = 0;
            for (int i = 0; i < alphaSize; i++)
            {
                int l = _lengths[t, i];
                if (l > maxLen) maxLen = l;
                if (l < minLen) minLen = l;
            }
            // canonical codes: by increasing length, then symbol
            int pp = 0;
            for (int len = minLen; len <= maxLen; len++)
            {
                for (int s = 0; s < alphaSize; s++)
                {
                    if (_lengths[t, s] == len) _perm[t, pp++] = s;
                }
            }
            Span<int> count = stackalloc int[MaxCodeLen + 2];
            count.Clear();
            for (int i = 0; i < alphaSize; i++) count[_lengths[t, i]]++;
            int code = 0, idx = 0;
            for (int len = 1; len <= MaxCodeLen; len++)
            {
                // codes of this length are [code, code + count); _limit is the last code (or -1), _base maps a code to
                // its index in _perm
                _base[t, len] = idx - code;
                code += count[len];
                idx += count[len];
                _limit[t, len] = code - 1;
                code <<= 1;
            }
            _limit[t, MaxCodeLen + 1] = int.MaxValue;
            _minLen[t] = minLen;
        }

        int DecodeSymbol(ref Bits br, int t)
        {
            int len = _minLen[t];
            uint peek = br.Peek(MaxCodeLen);
            int code = (int)(peek >> (MaxCodeLen - len));
            while (len <= MaxCodeLen)
            {
                if (code <= _limit[t, len])
                {
                    br.Skip(len);
                    int i = _base[t, len] + code;
                    if ((uint)i >= MaxAlpha) throw new InvalidDataException("bzip2: bad Huffman code");
                    return _perm[t, i];
                }
                len++;
                code = (int)(peek >> (MaxCodeLen - len));
            }
            throw new InvalidDataException("bzip2: bad Huffman code");
        }
    }
}
