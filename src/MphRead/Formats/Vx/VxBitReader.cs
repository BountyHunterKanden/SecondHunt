using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MphRead.Formats.Vx
{
    // Reads a frame payload: a sequence of 16-bit little-endian words, each read from bit 15 down to bit 0.
    // Unread bits are kept left-aligned in a 64-bit cache that is topped up a word at a time. Reading past the end of
    // the payload yields zero bits (a valid file never does it).
    internal sealed class VxBitReader
    {
        private byte[] _data = Array.Empty<byte>();
        private int _wordCount;
        private int _wordPos;
        private ulong _cache;
        private int _cacheBits;

        public void Start(byte[] data, int byteCount)
        {
            _data = data;
            _wordCount = byteCount / 2;
            _wordPos = 0;
            _cache = 0;
            _cacheBits = 0;
        }

        // bits consumed since the start of the payload
        public int Position => _wordPos * 16 - _cacheBits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Refill()
        {
            while (_cacheBits <= 48)
            {
                ulong word = 0;
                if (_wordPos < _wordCount)
                {
                    int p = _wordPos * 2;
                    word = (ulong)(_data[p] | (_data[p + 1] << 8));
                }
                _wordPos++;
                _cache |= word << (48 - _cacheBits);
                _cacheBits += 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Skip(int count)
        {
            _cache <<= count;
            _cacheBits -= count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBit()
        {
            if (_cacheBits < 1)
            {
                Refill();
            }
            int bit = (int)(_cache >> 63);
            Skip(1);
            return bit;
        }

        // u(n) for 0 <= n <= 32
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBits(int count)
        {
            if (count == 0)
            {
                return 0;
            }
            if (_cacheBits < count)
            {
                Refill();
            }
            int value = (int)(uint)(_cache >> (64 - count));
            Skip(count);
            return value;
        }

        public int ReadWord()
        {
            return ReadBits(16);
        }

        // ue: unsigned Exp-Golomb
        public int ReadUe()
        {
            if (_cacheBits < 32)
            {
                Refill();
            }
            uint top = (uint)(_cache >> 32);
            int zeros = BitOperations.LeadingZeroCount(top);
            if (zeros < 16)
            {
                int length = 2 * zeros + 1;
                int value = (int)(uint)(_cache >> (64 - length)) - 1;
                Skip(length);
                return value;
            }
            return ReadUeLong();
        }

        private int ReadUeLong()
        {
            int zeros = CountZerosThenOne();
            return (1 << zeros) - 1 + ReadBits(zeros);
        }

        // se: signed Exp-Golomb (0, 1, -1, 2, -2, ...)
        public int ReadSe()
        {
            int k = ReadUe();
            return (k & 1) != 0 ? (int)(((uint)k + 1) >> 1) : -(int)((uint)k >> 1);
        }

        // the number of 0 bits before the next 1 bit; the 1 is consumed
        public int CountZerosThenOne()
        {
            if (_cacheBits < 32)
            {
                Refill();
            }
            uint top = (uint)(_cache >> 32);
            if (top != 0)
            {
                int zeros = BitOperations.LeadingZeroCount(top);
                Skip(zeros + 1);
                return zeros;
            }
            int count = 0;
            while (ReadBit() == 0)
            {
                count++;
                if (count > 31)
                {
                    throw new ProgramException("VX bitstream: run of zero bits too long.");
                }
            }
            return count;
        }

        // decodes one symbol of a prefix-free code table
        public int ReadCode(VxCodeTable table)
        {
            if (_cacheBits < 32)
            {
                Refill();
            }
            int bits = table.RootBits;
            int offset = 0;
            while (true)
            {
                int index = offset + (int)(_cache >> (64 - bits));
                int length = table.Lengths[index];
                if (length > 0)
                {
                    Skip(length);
                    return table.Symbols[index];
                }
                if (length == 0)
                {
                    throw new ProgramException("VX bitstream: invalid code.");
                }
                Skip(bits);
                offset = table.Symbols[index];
                bits = -length;
            }
        }

        // skips to the next multiple of 16 bits from the start of the payload
        public void Align()
        {
            int rest = Position & 15;
            if (rest != 0)
            {
                ReadBits(16 - rest);
            }
        }
    }
}
