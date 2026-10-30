using System;
using System.IO;

namespace MphRecomp.Import.Disc
{
    // LZMA and LZMA2 decoders, written from Igor Pavlov's "LZMA specification" (lzma-specification.txt in the LZMA
    // SDK, public domain) and the LZMA2 chunk format as used by xz (xz-file-format, public domain). They decode a whole
    // raw stream into a flat output buffer, which doubles as the dictionary, so the dictionary size needs no memory of
    // its own. WIA stores raw streams: LZMA's properties come from wia_disc_t.compr_data (5 bytes: lc/lp/pb, dictionary
    // size), LZMA2's from its 1-byte dictionary property. An instance is not thread-safe.
    public sealed class LzmaDecoder
    {
        const int NumStates = 12;
        const int NumPosBitsMax = 4;
        const int NumLenToPosStates = 4;
        const int NumAlignBits = 4;
        const int StartPosModelIndex = 4;
        const int EndPosModelIndex = 14;
        const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
        const int MatchMinLen = 2;
        const ushort ProbInit = 1024;

        int _lc, _lp, _pb;
        ushort[] _literal = Array.Empty<ushort>();
        readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
        readonly ushort[] _isRep = new ushort[NumStates];
        readonly ushort[] _isRepG0 = new ushort[NumStates];
        readonly ushort[] _isRepG1 = new ushort[NumStates];
        readonly ushort[] _isRepG2 = new ushort[NumStates];
        readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
        readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
        readonly ushort[] _posSpecial = new ushort[1 + NumFullDistances - EndPosModelIndex];
        readonly ushort[] _align = new ushort[1 << NumAlignBits];
        readonly LenDecoder _len = new(), _repLen = new();
        int _state;
        uint _rep0, _rep1, _rep2, _rep3;

        // range decoder
        uint _range, _code;
        int _in;

        sealed class LenDecoder
        {
            public ushort Choice, Choice2;
            public readonly ushort[] Low = new ushort[1 << NumPosBitsMax << 3];
            public readonly ushort[] Mid = new ushort[1 << NumPosBitsMax << 3];
            public readonly ushort[] High = new ushort[256];

            public void Reset()
            {
                Choice = Choice2 = ProbInit;
                Array.Fill(Low, ProbInit); Array.Fill(Mid, ProbInit); Array.Fill(High, ProbInit);
            }
        }

        // ---- LZMA (WIA compression 3): props = lc/lp/pb byte + dictionary size (little endian)
        public int DecompressLzma(ReadOnlySpan<byte> src, ReadOnlySpan<byte> props, Span<byte> dst)
        {
            if (props.Length < 1) throw new InvalidDataException("lzma: missing properties");
            SetProps(props[0]);
            ResetState();
            _in = 0;
            InitRange(src);
            int outPos = 0;
            // no known size: stop at the end marker, when dst is full, or when the input runs out
            DecodeLz(src, dst, ref outPos, dst.Length, 0, allowEnd: true);
            return outPos;
        }

        // ---- LZMA2 (WIA compression 4): chunks of LZMA or stored data, ended by a 0x00 control byte
        public int DecompressLzma2(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int p = 0, outPos = 0, dictStart = 0;
            bool needProps = true, needDictReset = true;
            while (true)
            {
                if (p >= src.Length) throw new InvalidDataException("lzma2: truncated stream");
                int control = src[p++];
                if (control == 0) break;
                if (control == 1 || control == 2)
                {
                    if (control == 1) { dictStart = outPos; needDictReset = false; }
                    else if (needDictReset) throw new InvalidDataException("lzma2: missing dictionary reset");
                    if (p + 2 > src.Length) throw new InvalidDataException("lzma2: truncated stream");
                    int size = (src[p] << 8 | src[p + 1]) + 1;
                    p += 2;
                    if (p + size > src.Length || outPos + size > dst.Length) throw new InvalidDataException("lzma2: stored chunk out of range");
                    src.Slice(p, size).CopyTo(dst.Slice(outPos));
                    p += size; outPos += size;
                    continue;
                }
                if (control < 0x80) throw new InvalidDataException($"lzma2: bad control byte 0x{control:X2}");
                if (p + 4 > src.Length) throw new InvalidDataException("lzma2: truncated stream");
                int unpacked = ((control & 0x1F) << 16) + (src[p] << 8 | src[p + 1]) + 1;
                int packed = (src[p + 2] << 8 | src[p + 3]) + 1;
                p += 4;
                int reset = (control >> 5) & 3;
                if (reset == 3) { dictStart = outPos; needDictReset = false; }
                else if (needDictReset) throw new InvalidDataException("lzma2: missing dictionary reset");
                if (reset >= 2)
                {
                    if (p >= src.Length) throw new InvalidDataException("lzma2: truncated stream");
                    SetProps(src[p++]);
                    if (_lc + _lp > 4) throw new InvalidDataException("lzma2: lc + lp > 4");
                    needProps = false;
                }
                else if (needProps) throw new InvalidDataException("lzma2: missing properties");
                if (reset >= 1) ResetState();
                if (p + packed > src.Length || outPos + unpacked > dst.Length) throw new InvalidDataException("lzma2: chunk out of range");
                ReadOnlySpan<byte> chunk = src.Slice(p, packed);
                _in = 0;
                InitRange(chunk);
                int end = outPos + unpacked;
                DecodeLz(chunk, dst, ref outPos, end, dictStart, allowEnd: false);
                if (outPos != end) throw new InvalidDataException("lzma2: chunk decoded short");
                p += packed;
            }
            return outPos;
        }

        void SetProps(int d)
        {
            if (d >= 9 * 5 * 5) throw new InvalidDataException("lzma: bad properties byte");
            _lc = d % 9; d /= 9;
            _lp = d % 5;
            _pb = d / 5;
            int n = 0x300 << (_lc + _lp);
            if (_literal.Length != n) _literal = new ushort[n];
        }

        void ResetState()
        {
            Array.Fill(_literal, ProbInit);
            Array.Fill(_isMatch, ProbInit); Array.Fill(_isRep, ProbInit);
            Array.Fill(_isRepG0, ProbInit); Array.Fill(_isRepG1, ProbInit); Array.Fill(_isRepG2, ProbInit);
            Array.Fill(_isRep0Long, ProbInit);
            Array.Fill(_posSlot, ProbInit); Array.Fill(_posSpecial, ProbInit); Array.Fill(_align, ProbInit);
            _len.Reset(); _repLen.Reset();
            _state = 0;
            _rep0 = _rep1 = _rep2 = _rep3 = 0;
        }

        void InitRange(ReadOnlySpan<byte> src)
        {
            if (src.Length < 5 || src[0] != 0) throw new InvalidDataException("lzma: bad range coder start");
            _code = (uint)(src[1] << 24 | src[2] << 16 | src[3] << 8 | src[4]);
            _range = 0xFFFFFFFF;
            _in = 5;
            if (_code == _range) throw new InvalidDataException("lzma: bad range coder start");
        }

        // the input running out ends decoding (a raw LZMA stream need not carry an end marker)
        sealed class EndOfInput : Exception { }
        static readonly EndOfInput EndOfInputSignal = new();

        int DecodeBit(ReadOnlySpan<byte> src, ref ushort prob)
        {
            uint bound = (_range >> 11) * prob;
            int bit;
            if (_code < bound)
            {
                _range = bound;
                prob += (ushort)((2048 - prob) >> 5);
                bit = 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                prob -= (ushort)(prob >> 5);
                bit = 1;
            }
            if (_range < (1u << 24))
            {
                if (_in >= src.Length) throw EndOfInputSignal;
                _range <<= 8;
                _code = (_code << 8) | src[_in++];
            }
            return bit;
        }

        uint DirectBits(ReadOnlySpan<byte> src, int n)
        {
            uint res = 0;
            do
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0 - (_code >> 31);
                _code += _range & t;
                if (_code == _range) throw new InvalidDataException("lzma: corrupted direct bits");
                res = (res << 1) + (t + 1);
                if (_range < (1u << 24))
                {
                    if (_in >= src.Length) throw EndOfInputSignal;
                    _range <<= 8;
                    _code = (_code << 8) | src[_in++];
                }
            }
            while (--n > 0);
            return res;
        }

        int BitTree(ReadOnlySpan<byte> src, ushort[] probs, int offset, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + DecodeBit(src, ref probs[offset + m]);
            return m - (1 << numBits);
        }

        int BitTreeReverse(ReadOnlySpan<byte> src, ushort[] probs, int offset, int numBits)
        {
            int m = 1, sym = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = DecodeBit(src, ref probs[offset + m]);
                m = (m << 1) + bit;
                sym |= bit << i;
            }
            return sym;
        }

        int DecodeLen(ReadOnlySpan<byte> src, LenDecoder d, int posState)
        {
            if (DecodeBit(src, ref d.Choice) == 0) return BitTree(src, d.Low, posState << 3, 3);
            if (DecodeBit(src, ref d.Choice2) == 0) return 8 + BitTree(src, d.Mid, posState << 3, 3);
            return 16 + BitTree(src, d.High, 0, 8);
        }

        uint DecodeDistance(ReadOnlySpan<byte> src, int len)
        {
            int lenState = Math.Min(len, NumLenToPosStates - 1);
            int posSlot = BitTree(src, _posSlot, lenState << 6, 6);
            if (posSlot < StartPosModelIndex) return (uint)posSlot;
            int numDirectBits = (posSlot >> 1) - 1;
            uint dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
            if (posSlot < EndPosModelIndex)
            {
                dist += (uint)BitTreeReverse(src, _posSpecial, (int)dist - posSlot, numDirectBits);
            }
            else
            {
                dist += DirectBits(src, numDirectBits - NumAlignBits) << NumAlignBits;
                dist += (uint)BitTreeReverse(src, _align, 0, NumAlignBits);
            }
            return dist;
        }

        // decodes into dst[outPos..limit); positions (literal context, pos state, match reach) count from dictStart
        void DecodeLz(ReadOnlySpan<byte> src, Span<byte> dst, ref int outPos, int limit, int dictStart, bool allowEnd)
        {
            int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
            int op = outPos;
            try
            {
                while (op < limit)
                {
                    int pos = op - dictStart;
                    int posState = pos & pbMask;
                    if (DecodeBit(src, ref _isMatch[(_state << NumPosBitsMax) + posState]) == 0)
                    {
                        int prev = pos > 0 ? dst[op - 1] : 0;
                        int litBase = 0x300 * (((pos & lpMask) << _lc) + (prev >> (8 - _lc)));
                        int sym = 1;
                        if (_state >= 7)
                        {
                            if (_rep0 >= (uint)pos) throw new InvalidDataException("lzma: match byte before the start");
                            int matchByte = dst[op - (int)_rep0 - 1];
                            do
                            {
                                int matchBit = (matchByte >> 7) & 1;
                                matchByte <<= 1;
                                int bit = DecodeBit(src, ref _literal[litBase + ((1 + matchBit) << 8) + sym]);
                                sym = (sym << 1) | bit;
                                if (matchBit != bit) break;
                            }
                            while (sym < 0x100);
                        }
                        while (sym < 0x100) sym = (sym << 1) | DecodeBit(src, ref _literal[litBase + sym]);
                        dst[op++] = (byte)sym;
                        _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                        continue;
                    }
                    int len;
                    if (DecodeBit(src, ref _isRep[_state]) != 0)
                    {
                        if (pos == 0) throw new InvalidDataException("lzma: repeat match at the start");
                        if (DecodeBit(src, ref _isRepG0[_state]) == 0)
                        {
                            if (DecodeBit(src, ref _isRep0Long[(_state << NumPosBitsMax) + posState]) == 0)
                            {
                                // short rep: one byte from rep0
                                _state = _state < 7 ? 9 : 11;
                                dst[op] = dst[op - (int)_rep0 - 1];
                                op++;
                                continue;
                            }
                        }
                        else
                        {
                            uint dist;
                            if (DecodeBit(src, ref _isRepG1[_state]) == 0)
                            {
                                dist = _rep1;
                            }
                            else
                            {
                                if (DecodeBit(src, ref _isRepG2[_state]) == 0)
                                {
                                    dist = _rep2;
                                }
                                else
                                {
                                    dist = _rep3;
                                    _rep3 = _rep2;
                                }
                                _rep2 = _rep1;
                            }
                            _rep1 = _rep0;
                            _rep0 = dist;
                        }
                        len = DecodeLen(src, _repLen, posState);
                        _state = _state < 7 ? 8 : 11;
                    }
                    else
                    {
                        _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                        len = DecodeLen(src, _len, posState);
                        _state = _state < 7 ? 7 : 10;
                        _rep0 = DecodeDistance(src, len);
                        if (_rep0 == 0xFFFFFFFF)
                        {
                            if (!allowEnd) throw new InvalidDataException("lzma: end marker inside an LZMA2 chunk");
                            break;   // end of payload marker
                        }
                    }
                    len += MatchMinLen;
                    if (_rep0 >= (uint)(op - dictStart)) throw new InvalidDataException("lzma: match distance before the start");
                    int n = Math.Min(len, limit - op);
                    int from = op - (int)_rep0 - 1;
                    if (_rep0 + 1 >= (uint)n)
                    {
                        dst.Slice(from, n).CopyTo(dst.Slice(op));
                    }
                    else
                    {
                        for (int i = 0; i < n; i++) dst[op + i] = dst[from + i];
                    }
                    op += n;
                    if (n < len && !allowEnd) throw new InvalidDataException("lzma: match past the chunk end");
                }
            }
            catch (EndOfInput)
            {
                // the input ended inside a symbol: a raw stream without an end marker is complete here
                if (!allowEnd) throw new InvalidDataException("lzma2: chunk input ended early");
            }
            outPos = op;
        }
    }
}
