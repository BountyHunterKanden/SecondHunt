using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace MphRecomp.Import.Disc
{
    // A Zstandard decoder written from RFC 8878 ("Zstandard Compression and the application/zstd Media Type", IETF,
    // February 2021): frames, raw / RLE / compressed blocks, Huffman-coded literals (1 or 4 streams, FSE-compressed or
    // direct weights, treeless reuse), FSE-coded sequences (predefined / RLE / FSE / repeat tables) and repeat offsets.
    // Skippable frames are skipped. Dictionaries are not supported (RVZ never uses them); the optional content checksum
    // is read but not verified. Each call decodes whole frames into dst, which must be large enough for all of them;
    // it returns the number of bytes written (up to 32 bytes of dst past that count may be scratched by the fast copy
    // paths). An instance is not thread-safe (it keeps its tables between blocks).
    public sealed class ZstdDecoder
    {
        const uint FrameMagic = 0xFD2FB528;
        const int MaxBlockSize = 128 * 1024;
        const int MaxHuffBits = 11;

        // RFC 8878 3.1.1.3.2.1.1 / 3.1.1.3.2.2 (literal length and match length codes: baseline and extra bits)
        static readonly int[] LlBase =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
            8192, 16384, 32768, 65536,
        };
        static readonly byte[] LlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
            13, 14, 15, 16,
        };
        static readonly int[] MlBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051,
            4099, 8195, 16387, 32771, 65539,
        };
        static readonly byte[] MlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16,
        };

        // RFC 8878 3.1.1.3.2.2 default distributions
        static readonly short[] LlDefault =
        {
            4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
            -1, -1, -1, -1,
        };
        static readonly short[] MlDefault =
        {
            1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
            -1, -1, -1, -1, -1,
        };
        static readonly short[] OfDefault =
        {
            1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
        };

        // one FSE decoding table: per state, the symbol, the bits to read and the base of the next state. Sequence
        // tables also carry Seq: per state, packed as baseline (bits 0-31, the code's value base), extra bits of the
        // value (32-39), state bits (40-47) and next-state base (48-63), so one load decodes a state.
        sealed class FseTable
        {
            public readonly byte[] Symbol;
            public readonly byte[] NbBits;
            public readonly ushort[] Base;
            public readonly ulong[] Seq;
            public int AccuracyLog;

            public FseTable(int maxLog)
            {
                Symbol = new byte[1 << maxLog];
                NbBits = new byte[1 << maxLog];
                Base = new ushort[1 << maxLog];
                Seq = new ulong[1 << maxLog];
            }
        }

        enum SeqKind { LiteralLength, MatchLength, Offset }

        static readonly FseTable LlPredefined = BuildPredefined(LlDefault, 6, SeqKind.LiteralLength);
        static readonly FseTable MlPredefined = BuildPredefined(MlDefault, 6, SeqKind.MatchLength);
        static readonly FseTable OfPredefined = BuildPredefined(OfDefault, 5, SeqKind.Offset);

        static FseTable BuildPredefined(short[] norm, int log, SeqKind kind)
        {
            var t = new FseTable(log);
            BuildFse(norm, norm.Length, log, t);
            FillSeq(t, kind);
            return t;
        }

        static void FillSeq(FseTable t, SeqKind kind)
        {
            int size = 1 << t.AccuracyLog;
            for (int u = 0; u < size; u++)
            {
                int code = t.Symbol[u];
                uint baseline;
                int extra;
                switch (kind)
                {
                    case SeqKind.LiteralLength:
                        if (code > 35) throw new InvalidDataException("zstd: bad literal length code");
                        baseline = (uint)LlBase[code]; extra = LlBits[code];
                        break;
                    case SeqKind.MatchLength:
                        if (code > 52) throw new InvalidDataException("zstd: bad match length code");
                        baseline = (uint)MlBase[code]; extra = MlBits[code];
                        break;
                    default:
                        if (code > 31) throw new InvalidDataException("zstd: bad offset code");
                        baseline = 1u << code; extra = code;
                        break;
                }
                t.Seq[u] = baseline | (ulong)extra << 32 | (ulong)t.NbBits[u] << 40 | (ulong)t.Base[u] << 48;
            }
        }

        // per-frame state (tables carry over between the blocks of a frame)
        readonly FseTable _llTable = new(9), _mlTable = new(9), _ofTable = new(8);
        readonly FseTable _llRle = new(0), _mlRle = new(0), _ofRle = new(0);
        FseTable? _ll, _ml, _of;
        readonly FseTable _weightTable = new(6);
        readonly ushort[] _huff = new ushort[1 << MaxHuffBits];   // (symbol << 4) | nbBits, indexed by the next maxBits bits
        int _huffBits;                                               // 0 = no table yet in this frame
        readonly byte[] _literals = new byte[MaxBlockSize + 64];
        readonly short[] _norm = new short[256];
        readonly byte[] _weights = new byte[256];
        int _rep0, _rep1, _rep2;

        public int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int sp = 0, dp = 0;
            while (sp < src.Length)
            {
                if (src.Length - sp < 4)
                {
                    if (src.Slice(sp).IndexOfAnyExcept((byte)0) < 0) break;   // alignment padding after the last frame
                    throw new InvalidDataException("zstd: truncated frame header");
                }
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(sp));
                if (magic == FrameMagic)
                {
                    dp = DecodeFrame(src, ref sp, dst, dp);
                }
                else if ((magic & 0xFFFFFFF0) == 0x184D2A50)
                {
                    if (src.Length - sp < 8) throw new InvalidDataException("zstd: truncated skippable frame");
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(sp + 4));
                    if (sp + 8 + size > src.Length) throw new InvalidDataException("zstd: truncated skippable frame");
                    sp += 8 + (int)size;
                }
                else if (magic == 0 && src.Slice(sp).IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }
                else
                {
                    throw new InvalidDataException($"zstd: bad frame magic 0x{magic:X8}");
                }
            }
            return dp;
        }

        int DecodeFrame(ReadOnlySpan<byte> src, ref int sp, Span<byte> dst, int dp)
        {
            sp += 4;
            Need(src, sp, 1);
            int fhd = src[sp++];
            int fcsFlag = fhd >> 6;
            bool single = (fhd & 0x20) != 0;
            bool checksum = (fhd & 0x04) != 0;
            int dictFlag = fhd & 3;
            if ((fhd & 0x08) != 0) throw new InvalidDataException("zstd: reserved frame header bit set");
            if (!single)
            {
                Need(src, sp, 1);
                sp++;   // window descriptor: the whole frame is decoded into dst, so the window size needs no buffer
            }
            int dictSize = dictFlag == 0 ? 0 : dictFlag == 1 ? 1 : dictFlag == 2 ? 2 : 4;
            Need(src, sp, dictSize);
            uint dictId = 0;
            for (int i = 0; i < dictSize; i++) dictId |= (uint)src[sp + i] << (8 * i);
            sp += dictSize;
            if (dictId != 0) throw new NotSupportedException("zstd: frames with a dictionary are not supported");
            int fcsSize = fcsFlag == 0 ? (single ? 1 : 0) : fcsFlag == 1 ? 2 : fcsFlag == 2 ? 4 : 8;
            Need(src, sp, fcsSize);
            long fcs = -1;
            if (fcsSize > 0)
            {
                ulong v = 0;
                for (int i = 0; i < fcsSize; i++) v |= (ulong)src[sp + i] << (8 * i);
                if (fcsSize == 2) v += 256;
                fcs = (long)v;
            }
            sp += fcsSize;

            int frameStart = dp;
            _rep0 = 1; _rep1 = 4; _rep2 = 8;
            _ll = _ml = _of = null;
            _huffBits = 0;
            while (true)
            {
                Need(src, sp, 3);
                int bh = src[sp] | src[sp + 1] << 8 | src[sp + 2] << 16;
                sp += 3;
                bool last = (bh & 1) != 0;
                int type = (bh >> 1) & 3;
                int size = bh >> 3;
                switch (type)
                {
                    case 0:   // raw
                        Need(src, sp, size);
                        if (dp + size > dst.Length) throw new InvalidDataException("zstd: output too small");
                        src.Slice(sp, size).CopyTo(dst.Slice(dp));
                        sp += size; dp += size;
                        break;
                    case 1:   // RLE: one byte, repeated size times
                        Need(src, sp, 1);
                        if (dp + size > dst.Length) throw new InvalidDataException("zstd: output too small");
                        dst.Slice(dp, size).Fill(src[sp]);
                        sp += 1; dp += size;
                        break;
                    case 2:
                        if (size > MaxBlockSize) throw new InvalidDataException("zstd: compressed block too large");
                        Need(src, sp, size);
                        dp = DecodeBlock(src.Slice(sp, size), dst, dp, frameStart);
                        sp += size;
                        break;
                    default:
                        throw new InvalidDataException("zstd: reserved block type");
                }
                if (last) break;
            }
            if (checksum)
            {
                Need(src, sp, 4);
                sp += 4;
            }
            if (fcs >= 0 && dp - frameStart != fcs)
            {
                throw new InvalidDataException($"zstd: frame decoded to {dp - frameStart} bytes, its header says {fcs}");
            }
            return dp;
        }

        static void Need(ReadOnlySpan<byte> src, int at, int n)
        {
            if ((uint)at + (uint)n > (uint)src.Length) throw new InvalidDataException("zstd: truncated input");
        }

        int DecodeBlock(ReadOnlySpan<byte> blk, Span<byte> dst, int dp, int frameStart)
        {
            if (blk.Length < 1) throw new InvalidDataException("zstd: empty compressed block");
            // ---- literals section (RFC 8878 3.1.1.3.1)
            int litType = blk[0] & 3, sf = (blk[0] >> 2) & 3;
            int p, regen;
            ReadOnlySpan<byte> literals;
            if (litType <= 1)
            {
                int hdr;
                if ((sf & 1) == 0) { regen = blk[0] >> 3; hdr = 1; }
                else if (sf == 1) { Need(blk, 0, 2); regen = (blk[0] >> 4) | (blk[1] << 4); hdr = 2; }
                else { Need(blk, 0, 3); regen = (blk[0] >> 4) | (blk[1] << 4) | (blk[2] << 12); hdr = 3; }
                if (regen > MaxBlockSize) throw new InvalidDataException("zstd: literals too large");
                if (litType == 0)
                {
                    Need(blk, hdr, regen);
                    literals = blk.Slice(hdr, regen);
                    p = hdr + regen;
                }
                else
                {
                    Need(blk, hdr, 1);
                    _literals.AsSpan(0, regen).Fill(blk[hdr]);
                    literals = _literals.AsSpan(0, regen);
                    p = hdr + 1;
                }
            }
            else
            {
                int hdr, comp;
                bool four = sf != 0;
                if (sf <= 1)
                {
                    Need(blk, 0, 3);
                    int v = blk[0] | blk[1] << 8 | blk[2] << 16;
                    regen = (v >> 4) & 0x3FF; comp = (v >> 14) & 0x3FF; hdr = 3;
                }
                else if (sf == 2)
                {
                    Need(blk, 0, 4);
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(blk);
                    regen = (int)((v >> 4) & 0x3FFF); comp = (int)(v >> 18); hdr = 4;
                }
                else
                {
                    Need(blk, 0, 5);
                    ulong v = BinaryPrimitives.ReadUInt32LittleEndian(blk) | (ulong)blk[4] << 32;
                    regen = (int)((v >> 4) & 0x3FFFF); comp = (int)((v >> 22) & 0x3FFFF); hdr = 5;
                }
                if (regen > MaxBlockSize) throw new InvalidDataException("zstd: literals too large");
                Need(blk, hdr, comp);
                ReadOnlySpan<byte> data = blk.Slice(hdr, comp);
                if (litType == 2)
                {
                    int used = ReadHuffmanTable(data);
                    data = data.Slice(used);
                }
                else if (_huffBits == 0)
                {
                    throw new InvalidDataException("zstd: treeless literals without a previous Huffman table");
                }
                Span<byte> lit = _literals.AsSpan(0, regen);
                if (!four)
                {
                    DecodeHuffmanStream(data, lit);
                }
                else
                {
                    if (data.Length < 10) throw new InvalidDataException("zstd: bad literal jump table");
                    int s1 = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    int s2 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2));
                    int s3 = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
                    int s4 = data.Length - 6 - s1 - s2 - s3;
                    if (s1 < 1 || s2 < 1 || s3 < 1 || s4 < 1) throw new InvalidDataException("zstd: bad literal jump table");
                    int seg = (regen + 3) / 4;
                    if (regen - 3 * seg < 0) throw new InvalidDataException("zstd: bad literal size");
                    DecodeHuffman4(data, s1, s2, s3, lit, seg);
                }
                literals = lit;
                p = hdr + comp;
            }

            // ---- sequences section (RFC 8878 3.1.1.3.2)
            if (p >= blk.Length) throw new InvalidDataException("zstd: missing sequences section");
            int nbSeq, b0 = blk[p];
            if (b0 == 0) { nbSeq = 0; p++; }
            else if (b0 < 128) { nbSeq = b0; p++; }
            else if (b0 < 255) { Need(blk, p, 2); nbSeq = ((b0 - 128) << 8) + blk[p + 1]; p += 2; }
            else { Need(blk, p, 3); nbSeq = blk[p + 1] + (blk[p + 2] << 8) + 0x7F00; p += 3; }

            if (nbSeq == 0)
            {
                if (p != blk.Length) throw new InvalidDataException("zstd: trailing bytes after an empty sequences section");
                if (dp + literals.Length > dst.Length) throw new InvalidDataException("zstd: output too small");
                literals.CopyTo(dst.Slice(dp));
                return dp + literals.Length;
            }

            Need(blk, p, 1);
            int modes = blk[p++];
            if ((modes & 3) != 0) throw new InvalidDataException("zstd: reserved sequence mode bits set");
            _ll = SelectTable(blk, ref p, modes >> 6, 35, 9, LlPredefined, _llTable, _llRle, _ll, SeqKind.LiteralLength);
            _of = SelectTable(blk, ref p, (modes >> 4) & 3, 31, 8, OfPredefined, _ofTable, _ofRle, _of, SeqKind.Offset);
            _ml = SelectTable(blk, ref p, (modes >> 2) & 3, 52, 9, MlPredefined, _mlTable, _mlRle, _ml, SeqKind.MatchLength);
            ulong[] llT = _ll.Seq, ofT = _of.Seq, mlT = _ml.Seq;

            // the sequence bitstream, read backward with the container in locals (see BackBits)
            ReadOnlySpan<byte> bs = blk.Slice(p);
            var init = new BackBits(bs);
            int llState = (int)init.Read(_ll.AccuracyLog);
            int ofState = (int)init.Read(_of.AccuracyLog);
            int mlState = (int)init.Read(_ml.AccuracyLog);
            init.GetState(out int bp, out ulong bc, out int bu);
            int litPos = 0, litLen = literals.Length, dstLen = dst.Length;
            int rep0 = _rep0, rep1 = _rep1, rep2 = _rep2;
            for (int i = 0; i < nbSeq; i++)
            {
                ulong of = ofT[ofState], ml = mlT[mlState], ll = llT[llState];
                // refill, then the offset's extra bits (up to 31)
                if (bp >= 8) { bp -= bu >> 3; bu &= 7; bc = BinaryPrimitives.ReadUInt64LittleEndian(bs.Slice(bp)); }
                else BackBits.SlowRefill(bs, ref bp, ref bc, ref bu);
                int n = (int)(of >> 32) & 0xFF;
                uint ofValue = (uint)of + (uint)(((bc << bu) >> 1) >> (63 - n));
                bu += n;
                // the match and literal lengths' extra bits (up to 16 each)
                if (bp >= 8) { bp -= bu >> 3; bu &= 7; bc = BinaryPrimitives.ReadUInt64LittleEndian(bs.Slice(bp)); }
                else BackBits.SlowRefill(bs, ref bp, ref bc, ref bu);
                n = (int)(ml >> 32) & 0xFF;
                int matchLen = (int)(uint)ml + (int)(((bc << bu) >> 1) >> (63 - n));
                bu += n;
                n = (int)(ll >> 32) & 0xFF;
                int litRun = (int)(uint)ll + (int)(((bc << bu) >> 1) >> (63 - n));
                bu += n;
                int offset;
                if (ofValue > 3)
                {
                    offset = (int)Math.Min(ofValue - 3, int.MaxValue);
                    rep2 = rep1; rep1 = rep0; rep0 = offset;
                }
                else
                {
                    int idx = (int)ofValue - 1 + (litRun == 0 ? 1 : 0);
                    if (idx == 0)
                    {
                        offset = rep0;
                    }
                    else
                    {
                        offset = idx == 1 ? rep1 : idx == 2 ? rep2 : rep0 - 1;
                        if (offset == 0) throw new InvalidDataException("zstd: zero offset");
                        if (idx != 1) rep2 = rep1;
                        rep1 = rep0; rep0 = offset;
                    }
                }
                if (i != nbSeq - 1)
                {
                    // next states: literal length, match length, offset (up to 9 + 9 + 8 bits)
                    if (bp >= 8) { bp -= bu >> 3; bu &= 7; bc = BinaryPrimitives.ReadUInt64LittleEndian(bs.Slice(bp)); }
                    else BackBits.SlowRefill(bs, ref bp, ref bc, ref bu);
                    n = (int)(ll >> 40) & 0xFF;
                    llState = (int)(ll >> 48) + (int)(((bc << bu) >> 1) >> (63 - n));
                    bu += n;
                    n = (int)(ml >> 40) & 0xFF;
                    mlState = (int)(ml >> 48) + (int)(((bc << bu) >> 1) >> (63 - n));
                    bu += n;
                    n = (int)(of >> 40) & 0xFF;
                    ofState = (int)(of >> 48) + (int)(((bc << bu) >> 1) >> (63 - n));
                    bu += n;
                }

                // execute: litRun literals, then matchLen bytes from offset back
                if ((uint)(litPos + litRun) > (uint)litLen) throw new InvalidDataException("zstd: literal run past the literals");
                if (dp + litRun + matchLen > dstLen) throw new InvalidDataException("zstd: output too small");
                if (litRun <= 16 && litPos + 16 <= litLen && dp + 16 <= dstLen)
                {
                    // short run: two 8-byte moves (the overcopy is overwritten by what follows)
                    BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(dp), BinaryPrimitives.ReadUInt64LittleEndian(literals.Slice(litPos)));
                    BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(dp + 8), BinaryPrimitives.ReadUInt64LittleEndian(literals.Slice(litPos + 8)));
                }
                else if (litRun > 0)
                {
                    literals.Slice(litPos, litRun).CopyTo(dst.Slice(dp));
                }
                litPos += litRun; dp += litRun;
                if (offset > dp - frameStart) throw new InvalidDataException("zstd: match offset before the frame start");
                int from = dp - offset;
                if (offset >= 8 && matchLen <= 32 && dp + 32 <= dstLen)
                {
                    // 8 bytes at a time: every read is of bytes already final (offset >= 8)
                    for (int k = 0; k < matchLen; k += 8)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(dp + k), BinaryPrimitives.ReadUInt64LittleEndian(dst.Slice(from + k)));
                    }
                }
                else if (offset >= matchLen)
                {
                    dst.Slice(from, matchLen).CopyTo(dst.Slice(dp));
                }
                else if (offset == 1)
                {
                    dst.Slice(dp, matchLen).Fill(dst[from]);
                }
                else
                {
                    // overlapping: the pattern repeats every `offset` bytes; copy in growing non-overlapping pieces
                    int done = 0;
                    while (done < matchLen)
                    {
                        int k = Math.Min(matchLen - done, offset + done);
                        dst.Slice(from, k).CopyTo(dst.Slice(dp + done));
                        done += k;
                    }
                }
                dp += matchLen;
            }
            if (bp != 0 || bu != 64) throw new InvalidDataException("zstd: sequence bitstream not fully consumed");
            _rep0 = rep0; _rep1 = rep1; _rep2 = rep2;
            int rest = literals.Length - litPos;
            if (dp + rest > dst.Length) throw new InvalidDataException("zstd: output too small");
            literals.Slice(litPos).CopyTo(dst.Slice(dp));
            return dp + rest;
        }

        FseTable SelectTable(ReadOnlySpan<byte> blk, ref int p, int mode, int maxSymbol, int maxLog,
            FseTable predefined, FseTable own, FseTable rle, FseTable? previous, SeqKind kind)
        {
            switch (mode)
            {
                case 0:
                    return predefined;
                case 1:
                {
                    Need(blk, p, 1);
                    int sym = blk[p++];
                    if (sym > maxSymbol) throw new InvalidDataException("zstd: bad RLE sequence symbol");
                    rle.AccuracyLog = 0;
                    rle.Symbol[0] = (byte)sym; rle.NbBits[0] = 0; rle.Base[0] = 0;
                    FillSeq(rle, kind);
                    return rle;
                }
                case 2:
                {
                    p += ReadFseDescription(blk.Slice(p), maxSymbol, maxLog, out int log, out int count);
                    BuildFse(_norm, count, log, own);
                    FillSeq(own, kind);
                    return own;
                }
                default:
                    return previous ?? throw new InvalidDataException("zstd: repeat table mode without a previous table");
            }
        }

        // RFC 8878 4.1.1: the FSE table description (normalized counts), read forward, little-endian bit order.
        // Returns the bytes used; the counts land in _norm[0..count).
        int ReadFseDescription(ReadOnlySpan<byte> src, int maxSymbol, int maxLog, out int log, out int count)
        {
            long bitPos = 0;
            uint Peek(ReadOnlySpan<byte> s, long pos)
            {
                int byteAt = (int)(pos >> 3);
                uint v = 0;
                for (int i = 0; i < 4 && byteAt + i < s.Length; i++) v |= (uint)s[byteAt + i] << (8 * i);
                return v >> (int)(pos & 7);
            }
            log = (int)(Peek(src, 0) & 15) + 5;
            bitPos = 4;
            if (log > maxLog) throw new InvalidDataException("zstd: FSE accuracy too high");
            int remaining = (1 << log) + 1;
            int threshold = 1 << log;
            int nbBits = log + 1;
            int sym = 0;
            bool previous0 = false;
            while (remaining > 1 && sym <= maxSymbol)
            {
                if (previous0)
                {
                    while (true)
                    {
                        int r = (int)(Peek(src, bitPos) & 3);
                        bitPos += 2;
                        for (int k = 0; k < r; k++)
                        {
                            if (sym > maxSymbol) throw new InvalidDataException("zstd: FSE zero run past the alphabet");
                            _norm[sym++] = 0;
                        }
                        if (r != 3) break;
                    }
                    if (sym > maxSymbol) break;
                }
                uint bits = Peek(src, bitPos);
                int max = (2 * threshold - 1) - remaining;
                int c;
                if ((bits & (uint)(threshold - 1)) < (uint)max)
                {
                    c = (int)(bits & (uint)(threshold - 1));
                    bitPos += nbBits - 1;
                }
                else
                {
                    c = (int)(bits & (uint)(2 * threshold - 1));
                    if (c >= threshold) c -= max;
                    bitPos += nbBits;
                }
                c--;
                remaining -= c < 0 ? -c : c;
                _norm[sym++] = (short)c;
                previous0 = c == 0;
                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }
            if (remaining != 1 || bitPos > (long)src.Length * 8) throw new InvalidDataException("zstd: bad FSE table description");
            count = sym;
            return (int)((bitPos + 7) >> 3);
        }

        // RFC 8878 4.1.1: spread the symbols over the states and give each state its bits and next-state base
        static void BuildFse(short[] norm, int count, int log, FseTable t)
        {
            int size = 1 << log;
            int high = size - 1;
            Span<ushort> next = stackalloc ushort[256];
            for (int s = 0; s < count; s++)
            {
                if (norm[s] == -1)
                {
                    t.Symbol[high--] = (byte)s;
                    next[s] = 1;
                }
                else
                {
                    next[s] = (ushort)Math.Max((int)norm[s], 0);
                }
            }
            int pos = 0, step = (size >> 1) + (size >> 3) + 3, mask = size - 1;
            for (int s = 0; s < count; s++)
            {
                for (int i = 0; i < norm[s]; i++)
                {
                    t.Symbol[pos] = (byte)s;
                    do { pos = (pos + step) & mask; } while (pos > high);
                }
            }
            if (pos != 0) throw new InvalidDataException("zstd: bad FSE distribution");
            for (int u = 0; u < size; u++)
            {
                int s = t.Symbol[u];
                int x = next[s]++;
                int nb = log - BitOperations.Log2((uint)x);
                t.NbBits[u] = (byte)nb;
                t.Base[u] = (ushort)((x << nb) - size);
            }
            t.AccuracyLog = log;
        }

        // RFC 8878 4.2.1: the Huffman tree description; returns the bytes used
        int ReadHuffmanTable(ReadOnlySpan<byte> src)
        {
            if (src.Length < 1) throw new InvalidDataException("zstd: missing Huffman tree description");
            int header = src[0];
            int n;
            int used;
            if (header < 128)
            {
                // FSE-compressed weights: a table description, then a backward bitstream decoded by two
                // interleaved states until it runs out
                if (1 + header > src.Length) throw new InvalidDataException("zstd: truncated Huffman weights");
                ReadOnlySpan<byte> fse = src.Slice(1, header);
                int descLen = ReadFseDescription(fse, 255, 6, out int log, out int count);
                BuildFse(_norm, count, log, _weightTable);
                var br = new BackBits(fse.Slice(descLen));
                int s1 = (int)br.Read(log), s2 = (int)br.Read(log);
                FseTable t = _weightTable;
                n = 0;
                while (true)
                {
                    if (n > 253) throw new InvalidDataException("zstd: too many Huffman weights");
                    _weights[n++] = t.Symbol[s1];
                    s1 = t.Base[s1] + (int)br.Read(t.NbBits[s1]);
                    if (br.Overflowed) { _weights[n++] = t.Symbol[s2]; break; }
                    _weights[n++] = t.Symbol[s2];
                    s2 = t.Base[s2] + (int)br.Read(t.NbBits[s2]);
                    if (br.Overflowed) { _weights[n++] = t.Symbol[s1]; break; }
                }
                used = 1 + header;
            }
            else
            {
                n = header - 127;
                int bytes = (n + 1) / 2;
                if (1 + bytes > src.Length) throw new InvalidDataException("zstd: truncated Huffman weights");
                for (int i = 0; i < n; i++)
                {
                    int b = src[1 + i / 2];
                    _weights[i] = (byte)((i & 1) == 0 ? b >> 4 : b & 15);
                }
                used = 1 + bytes;
            }
            // the last symbol's weight is implied: it completes the sum to a power of two
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                int w = _weights[i];
                if (w > MaxHuffBits) throw new InvalidDataException("zstd: Huffman weight too large");
                if (w > 0) sum += 1 << (w - 1);
            }
            if (sum == 0) throw new InvalidDataException("zstd: all Huffman weights zero");
            int maxBits = BitOperations.Log2((uint)sum) + 1;
            int left = (1 << maxBits) - sum;
            if (!BitOperations.IsPow2(left) || maxBits > MaxHuffBits) throw new InvalidDataException("zstd: bad Huffman weights");
            _weights[n++] = (byte)(BitOperations.Log2((uint)left) + 1);
            // codes by increasing weight, then symbol: each symbol of weight w fills 2^(w-1) slots
            int pos = 0;
            for (int w = 1; w <= maxBits; w++)
            {
                int len = 1 << (w - 1);
                ushort entry = (ushort)(maxBits + 1 - w);
                for (int s = 0; s < n; s++)
                {
                    if (_weights[s] != w) continue;
                    ushort e = (ushort)(s << 4 | entry);
                    _huff.AsSpan(pos, len).Fill(e);
                    pos += len;
                }
            }
            if (pos != 1 << maxBits) throw new InvalidDataException("zstd: bad Huffman table");
            _huffBits = maxBits;
            return used;
        }

        void DecodeHuffmanStream(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            var br = new BackBits(src);
            FinishHuffmanStream(ref br, dst, 0);
        }

        // the careful path: one symbol per refill, to the stream's end
        void FinishHuffmanStream(ref BackBits br, Span<byte> dst, int i)
        {
            int bits = _huffBits;
            ushort[] table = _huff;
            int n = dst.Length;
            while (i < n)
            {
                br.Refill();
                ushort e = table[br.Peek(bits)];
                br.Skip(e & 15);
                dst[i++] = (byte)(e >> 4);
            }
            if (!br.Finished) throw new InvalidDataException("zstd: Huffman stream not fully consumed");
        }

        // four Huffman streams (after the 6-byte jump table) into four quarters of dst, decoded side by side with
        // their bit containers in registers: 4 symbols per stream per refill (4 x 11 bits + 7 < 64)
        void DecodeHuffman4(ReadOnlySpan<byte> data, int s1, int s2, int s3, Span<byte> dst, int seg)
        {
            int b0 = 6, b1 = b0 + s1, b2 = b1 + s2, b3 = b2 + s3, end = data.Length;
            int n3 = dst.Length - 3 * seg;
            if (s1 < 8 || s2 < 8 || s3 < 8 || end - b3 < 8)
            {
                DecodeHuffmanStream(data.Slice(b0, s1), dst.Slice(0, seg));
                DecodeHuffmanStream(data.Slice(b1, s2), dst.Slice(seg, seg));
                DecodeHuffmanStream(data.Slice(b2, s3), dst.Slice(2 * seg, seg));
                DecodeHuffmanStream(data.Slice(b3, end - b3), dst.Slice(3 * seg, n3));
                return;
            }
            static int Pad(byte last) => last == 0 ? throw new InvalidDataException("zstd: bitstream without an end mark") : 8 - BitOperations.Log2(last);
            int p0 = b1 - 8, p1 = b2 - 8, p2 = b3 - 8, p3 = end - 8;
            int u0 = Pad(data[b1 - 1]), u1 = Pad(data[b2 - 1]), u2 = Pad(data[b3 - 1]), u3 = Pad(data[end - 1]);
            ulong c0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p0)), c1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p1));
            ulong c2 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p2)), c3 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p3));
            int shift = 64 - _huffBits;
            ushort[] table = _huff;
            Span<byte> d0 = dst.Slice(0, seg), d1 = dst.Slice(seg, seg), d2 = dst.Slice(2 * seg, seg), d3 = dst.Slice(3 * seg, n3);
            int i = 0;
            int lim = Math.Min(seg, n3) - 4;
            while (i <= lim && p0 >= b0 + 8 && p1 >= b1 + 8 && p2 >= b2 + 8 && p3 >= b3 + 8)
            {
                p0 -= u0 >> 3; u0 &= 7; c0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p0));
                p1 -= u1 >> 3; u1 &= 7; c1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p1));
                p2 -= u2 >> 3; u2 &= 7; c2 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p2));
                p3 -= u3 >> 3; u3 &= 7; c3 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p3));
                for (int k = 0; k < 4; k++)
                {
                    ushort e0 = table[(int)((c0 << u0) >> shift)];
                    ushort e1 = table[(int)((c1 << u1) >> shift)];
                    ushort e2 = table[(int)((c2 << u2) >> shift)];
                    ushort e3 = table[(int)((c3 << u3) >> shift)];
                    u0 += e0 & 15; u1 += e1 & 15; u2 += e2 & 15; u3 += e3 & 15;
                    d0[i + k] = (byte)(e0 >> 4);
                    d1[i + k] = (byte)(e1 >> 4);
                    d2[i + k] = (byte)(e2 >> 4);
                    d3[i + k] = (byte)(e3 >> 4);
                }
                i += 4;
            }
            var r0 = new BackBits(data.Slice(b0, s1), p0 - b0, c0, u0);
            FinishHuffmanStream(ref r0, d0, i);
            var r1 = new BackBits(data.Slice(b1, s2), p1 - b1, c1, u1);
            FinishHuffmanStream(ref r1, d1, i);
            var r2 = new BackBits(data.Slice(b2, s3), p2 - b2, c2, u2);
            FinishHuffmanStream(ref r2, d2, i);
            var r3 = new BackBits(data.Slice(b3, end - b3), p3 - b3, c3, u3);
            FinishHuffmanStream(ref r3, d3, i);
        }

        // A backward bitstream (RFC 8878 4.1): the last byte's highest set bit marks the end; bits are read from
        // there towards the first byte, most significant first. Reading past the start gives zero bits and sets
        // Overflowed.
        ref struct BackBits
        {
            readonly ReadOnlySpan<byte> _s;
            int _ptr;       // index of the 8 bytes held in _c
            ulong _c;
            int _used;      // bits of _c already consumed, from the top

            public BackBits(ReadOnlySpan<byte> s)
            {
                if (s.Length == 0) throw new InvalidDataException("zstd: empty bitstream");
                byte last = s[s.Length - 1];
                if (last == 0) throw new InvalidDataException("zstd: bitstream without an end mark");
                _s = s;
                int pad = 8 - BitOperations.Log2(last);
                if (s.Length >= 8)
                {
                    _ptr = s.Length - 8;
                    _c = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(_ptr));
                    _used = pad;
                }
                else
                {
                    // a short stream sits in the low bytes; the missing high bytes count as already consumed
                    _ptr = 0;
                    ulong c = 0;
                    for (int i = 0; i < s.Length; i++) c |= (ulong)s[i] << (8 * i);
                    _c = c;
                    _used = pad + 8 * (8 - s.Length);
                }
            }

            // resume a stream whose 8-byte window is at ptr (>= 0) with `used` bits of it consumed
            public BackBits(ReadOnlySpan<byte> s, int ptr, ulong c, int used)
            {
                _s = s; _ptr = ptr; _c = c; _used = used;
            }

            public readonly void GetState(out int ptr, out ulong c, out int used)
            {
                ptr = _ptr; c = _c; used = _used;
            }

            // a refill for a window near the stream's start (ptr < 8); callers do the ptr >= 8 case inline
            public static void SlowRefill(ReadOnlySpan<byte> s, ref int ptr, ref ulong c, ref int used)
            {
                if (used > 64 || ptr == 0) return;
                int nb = used >> 3;
                if (nb > ptr) nb = ptr;
                ptr -= nb;
                used -= nb * 8;
                c = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(ptr));
            }

            public readonly bool Overflowed => _used > 64;
            public readonly bool Finished => _ptr == 0 && _used == 64;

            public void Refill()
            {
                if (_used > 64) return;
                if (_ptr >= 8)
                {
                    _ptr -= _used >> 3;
                    _used &= 7;
                    _c = BinaryPrimitives.ReadUInt64LittleEndian(_s.Slice(_ptr));
                }
                else if (_ptr > 0)
                {
                    int nb = _used >> 3;
                    if (nb > _ptr) nb = _ptr;
                    _ptr -= nb;
                    _used -= nb * 8;
                    _c = BinaryPrimitives.ReadUInt64LittleEndian(_s.Slice(_ptr));
                }
            }

            public readonly int Peek(int n) => _used >= 64 ? 0 : (int)((_c << _used) >> (64 - n));

            public void Skip(int n) => _used += n;

            public uint Read(int n)
            {
                if (n == 0) return 0;
                if (_used + n > 64) Refill();
                uint v = _used >= 64 ? 0u : (uint)((_c << _used) >> (64 - n));
                _used += n;
                return v;
            }
        }
    }
}
