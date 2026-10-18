using System;
using System.Buffers.Binary;

namespace MphRecomp.FirstHunt.Movies
{
    // The video codec of First Hunt's FVDS movies, written from the cart's decoder (arm9 0x2067300-0x206b6a0, frame
    // entry 0x206b6a0) and checked frame for frame, bit for bit, against that code run on every movie (Tools
    // -fhgame movie). 256x192 RGB555, two buffers swapped each frame (the new frame is built from the previous one).
    //
    // A frame's payload: u32 len0, u32 len1, a byte stream (len0: motion bytes), a halfword stream (len1: colours, VQ
    // blocks), then a bit stream of LE u32 words read from the top bit down (the entry primes it and drops the first
    // bit). The frame is a 32x24 grid of 8x8 blocks, each decoded by a mode tree that can split it in halves down to
    // 1x2 / 2x1 (per-size trees in Block). Modes: skip (copy the previous frame), motion copy (byte: dx = low nibble - 8,
    // dy = high nibble - 8), motion + delta (adds a halfword to each pixel, pairs as one u32), flat colour blended into
    // its top / left edge (the blend writes into the neighbouring row / column too), 4x4 two-bit VQ with a 4-colour
    // palette c0, c1, (2c0+c1)/3, (c0+2c1)/3, and an 8x8 VQ upscaled from 4x4 by averaging.
    //
    // Pixels keep the codec's bit 15 exactly as the cart leaves it (the u32 pair arithmetic leaks into it); the cart
    // ORs 0x8000 when it copies a frame out, and so should anything showing Frame.
    public sealed class FvdsVideo
    {
        public const int Width = 256, Height = 192;
        // rows kept before and after each buffer: motion reaches 8 rows out and the edge blend writes row -1 (the cart
        // reads and writes whatever memory is there; these start zeroed, like the reference run)
        private const int Guard = 16 * Width;
        private const int Mask = 0x7bdf, Lsb = 0x0420;
        private const uint Mask2 = 0x7bdf7bdf, Lsb2 = 0x04200420;

        private readonly ushort[][] _buffers = { new ushort[Guard * 2 + Width * Height], new ushort[Guard * 2 + Width * Height] };
        private int _current = 1;
        private int _shown = 0;
        private ushort[] _p = null!, _c = null!;

        private byte[] _data = null!;
        private int _bytePos, _byteEnd, _halfPos, _halfEnd, _wordPos, _wordEnd;
        private uint _bits;
        private int _bitCount;

        // the last decoded frame, Width x Height RGB555 (bit 15 as decoded)
        public ReadOnlySpan<ushort> Frame => _buffers[_shown].AsSpan(Guard, Width * Height);

        public void Reset()
        {
            Array.Clear(_buffers[0]);
            Array.Clear(_buffers[1]);
            _current = 1;
            _shown = 0;
        }

        // decodes one frame payload; false (frame left as it was) if the data runs out
        public bool Decode(byte[] data, int offset, int length)
        {
            if (length < 8) return false;
            int len0 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            int len1 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            if (len0 < 0 || len1 < 0 || 8L + len0 + len1 > length) return false;
            _data = data;
            _bytePos = offset + 8;
            _byteEnd = _bytePos + len0;
            _halfPos = _byteEnd;
            _halfEnd = _halfPos + len1 / 2 * 2;
            _wordPos = _byteEnd + len1;
            _wordEnd = _wordPos + (offset + length - _wordPos) / 4 * 4;
            _bitCount = 0;
            _p = _buffers[_current ^ 1];
            _c = _buffers[_current];
            try
            {
                Bit(); // the entry's priming read
                for (int by = 0; by < Height; by += 8)
                {
                    for (int bx = 0; bx < Width; bx += 8)
                    {
                        Block(bx, by, 8, 8);
                    }
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException)
            {
                return false;
            }
            _shown = _current;
            _current ^= 1;
            return true;
        }

        private bool Bit()
        {
            if (_bitCount == 0)
            {
                if (_wordPos + 4 > _wordEnd) throw new IndexOutOfRangeException();
                _bits = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_wordPos));
                _wordPos += 4;
                _bitCount = 32;
            }
            _bitCount--;
            return (_bits >> _bitCount & 1) != 0;
        }

        private ushort Half()
        {
            if (_halfPos + 2 > _halfEnd) throw new IndexOutOfRangeException();
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_halfPos));
            _halfPos += 2;
            return value;
        }

        private (int, int) Motion()
        {
            if (_bytePos >= _byteEnd) throw new IndexOutOfRangeException();
            byte b = _data[_bytePos++];
            return ((b & 15) - 8, (b >> 4) - 8);
        }

        private static int Index(int x, int y) => Guard + y * Width + x;

        private void Copy(int x, int y, int w, int h, int dx, int dy)
        {
            for (int j = 0; j < h; j++)
            {
                Array.Copy(_p, Index(x + dx, y + dy + j), _c, Index(x, y + j), w);
            }
        }

        // motion copy plus a colour delta: pixel pairs added as one u32 (so carries cross into the right pixel),
        // single-pixel columns as a u16
        private void MotionDelta(int x, int y, int w, int h)
        {
            (int dx, int dy) = Motion();
            uint delta = Half();
            ushort[] p = _p, c = _c;
            for (int j = 0; j < h; j++)
            {
                int s = Index(x + dx, y + dy + j);
                int d = Index(x, y + j);
                if (w == 1)
                {
                    c[d] = (ushort)(p[s] + delta);
                    continue;
                }
                for (int i = 0; i < w; i += 2)
                {
                    uint v = (p[s + i] | (uint)p[s + i + 1] << 16) + delta * 0x10001;
                    c[d + i] = (ushort)v;
                    c[d + i + 1] = (ushort)(v >> 16);
                }
            }
        }

        private void Fill(int x, int y, int w, int h, ushort colour)
        {
            for (int j = 0; j < h; j++)
            {
                _c.AsSpan(Index(x, y + j), w).Fill(colour);
            }
        }

        // flat colour, blended into the top and left edges. The cart does this on the colour doubled into both halves
        // of a u32, so the high copy's low bit shifts down into bit 15 of what it stores: kept exactly.
        private void FlatBlend(int x, int y, int w, int h, ushort colour)
        {
            ushort[] c = _c;
            for (int j = 2; j < h; j++)
            {
                c.AsSpan(Index(x + 2, y + j), w - 2).Fill(colour);
            }
            uint both = colour | (uint)colour << 16;
            uint r0 = both & Mask2, r1 = both & Lsb2;
            void Tri(int p0, int pm, int p1, bool even)
            {
                uint a = c[pm];
                uint r5 = a & Mask, r2 = a & Lsb;
                uint t = (even ? r2 : r1) | (r0 + r5) >> 1;
                c[p0] = (ushort)t;
                t &= Mask2;
                uint o = even ? r1 : r2;
                c[pm] = (ushort)(o | (t + r5) >> 1);
                c[p1] = (ushort)(o | (t + r0) >> 1);
            }
            for (int i = 0; i < w; i++)
            {
                Tri(Index(x + i, y), Index(x + i, y - 1), Index(x + i, y + 1), (i & 1) == 0);
            }
            for (int j = 0; j < h; j++)
            {
                Tri(Index(x, y + j), Index(x - 1, y + j), Index(x + 1, y + j), (j & 1) == 0);
            }
        }

        private static int Third(int a, int b)
        {
            int r = 0;
            for (int shift = 10; shift >= 0; shift -= 5)
            {
                r = r << 5 | (2 * (a >> shift & 31) + (b >> shift & 31) + 1) / 3;
            }
            return r;
        }

        private readonly ushort[] _palette = new ushort[4];

        private uint Palette()
        {
            ushort c0 = Half(), c1 = Half();
            _palette[0] = c0;
            _palette[1] = c1;
            _palette[2] = (ushort)Third(c0, c1);
            _palette[3] = (ushort)Third(c1, c0);
            uint lo = Half();
            return lo | (uint)Half() << 16;
        }

        private void Vq4(int x, int y)
        {
            uint indices = Palette();
            for (int k = 0; k < 16; k++)
            {
                _c[Index(x + (k & 3), y + (k >> 2))] = _palette[indices >> 2 * k & 3];
            }
        }

        // 8x8 from a 4x4 VQ block: odd rows take the block's pixels at odd columns and averages between them, then
        // even rows are the average of the rows around them (u32 pairs, bottom up; row 0 uses the row above the block)
        private void VqUp(int x, int y)
        {
            uint indices = Palette();
            ushort[] c = _c;
            int k = 0;
            for (int j = 1; j < 8; j += 2)
            {
                int left = c[Index(x - 1, y + j)];
                for (int i = 0; i < 4; i++)
                {
                    int px = _palette[indices >> 2 * k++ & 3];
                    c[Index(x + 2 * i + 1, y + j)] = (ushort)px;
                    c[Index(x + 2 * i, y + j)] = (ushort)(((left & Mask) + (px & Mask)) >> 1 | px & Lsb);
                    left = px;
                }
            }
            for (int i = 0; i < 8; i += 2)
            {
                for (int j = 6; j >= 0; j -= 2)
                {
                    int below = Index(x + i, y + j + 1), above = Index(x + i, y + j - 1);
                    uint a = c[below] | (uint)c[below + 1] << 16;
                    uint b = c[above] | (uint)c[above + 1] << 16;
                    uint v = ((a & Mask2) + (b & Mask2)) >> 1 | a & Lsb2;
                    int d = Index(x + i, y + j);
                    c[d] = (ushort)v;
                    c[d + 1] = (ushort)(v >> 16);
                }
            }
        }

        private void Split(int x, int y, int w, int h, bool vertical)
        {
            if (vertical)
            {
                Block(x, y, w / 2, h);
                Block(x + w / 2, y, w / 2, h);
            }
            else
            {
                Block(x, y, w, h / 2);
                Block(x, y + h / 2, w, h / 2);
            }
        }

        // the mode trees (bits from the top): S skip, M motion, MD motion + delta, F flat blend, Fp plain flat,
        // H / V split into top-bottom / left-right halves
        //   8x8: 00 S, 01 M, 100 H, 101 V, 1100 F, 11010 VQ x4, 11011 VQup, 111 MD
        //   8x4, 4x8, 4x4: 00 S, 01 M, 100 H, 101 V, 1100 F, 1101 VQ (x2 for the halves), 111 MD
        //   8x2, 2x8, 4x2, 2x4, 2x2: 00 S, 01 M, 100 H, 101 V, 110 MD, 111 F
        //   8x1, 1x8, 4x1, 1x4: 00 S, 01 M, 10 split (along the long side), 110 MD, 111 Fp
        //   2x1, 1x2: 00 S, 01 M, 10 MD, 110 Fp, 111 one colour per pixel
        private void Block(int x, int y, int w, int h)
        {
            if (!Bit())
            {
                if (!Bit())
                {
                    Copy(x, y, w, h, 0, 0);
                }
                else
                {
                    (int dx, int dy) = Motion();
                    Copy(x, y, w, h, dx, dy);
                }
                return;
            }
            if (w * h == 2)
            {
                if (!Bit())
                {
                    MotionDelta(x, y, w, h);
                }
                else if (!Bit())
                {
                    Fill(x, y, w, h, Half());
                }
                else
                {
                    _c[Index(x, y)] = Half();
                    _c[Index(x + w - 1, y + h - 1)] = Half();
                }
                return;
            }
            if (w == 1 || h == 1)
            {
                if (!Bit())
                {
                    Split(x, y, w, h, vertical: h == 1);
                }
                else if (!Bit())
                {
                    MotionDelta(x, y, w, h);
                }
                else
                {
                    Fill(x, y, w, h, Half());
                }
                return;
            }
            if (!Bit())
            {
                Split(x, y, w, h, vertical: Bit());
                return;
            }
            if (w >= 4 && h >= 4)
            {
                if (Bit())
                {
                    MotionDelta(x, y, w, h);
                }
                else if (!Bit())
                {
                    FlatBlend(x, y, w, h, Half());
                }
                else if (w == 8 && h == 8)
                {
                    if (!Bit())
                    {
                        Vq4(x, y);
                        Vq4(x + 4, y);
                        Vq4(x, y + 4);
                        Vq4(x + 4, y + 4);
                    }
                    else
                    {
                        VqUp(x, y);
                    }
                }
                else
                {
                    Vq4(x, y);
                    if (w == 8) Vq4(x + 4, y);
                    else if (h == 8) Vq4(x, y + 4);
                }
                return;
            }
            if (!Bit())
            {
                MotionDelta(x, y, w, h);
            }
            else
            {
                FlatBlend(x, y, w, h, Half());
            }
        }
    }
}
