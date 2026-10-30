using System;
using System.Buffers.Binary;
using MphRead;

namespace MphRecomp.FirstHunt.Movies
{
    // The audio codec of First Hunt's FVDS movies, written from the cart's decoder (arm9 0x206c0d8, unpack 0x206b9a0,
    // excitation 0x206befc) and checked sample for sample against that code (Tools -fhgame movie). A GSM-like speech
    // coder: each 40-byte record is 256 samples at 32768 Hz.
    //   word 0: 8 reflection coefficient indices, 6 6 5 5 4 _ 3 3 bits from the top (the 6th, 4 bits, is the low
    //           bit of words 3, 5, 7, 9); each looks up a Q15 coefficient in its own table
    //   word 1: per 64-sample subframe s: 6-bit pulse scale at bit 8 + 6s, 2-bit grid offset at bit 2s
    //   words 2-9: per subframe two words of 21 3-bit pulse levels (10 + 10 from the top, the 21st from the low bits)
    // The subframe is the pulses every third sample from its offset (level -> table[scale][level]), the rest zero,
    // run through an 8-stage lattice synthesis filter and a de-emphasis (28180 / 32768), doubled and clamped.
    // The tables are read from the user's own arm9 (FirstHuntRules), like the rest of the port's values.
    public sealed class FvdsAudio
    {
        public const int SamplesPerRecord = 256;
        public const int RecordSize = 40;

        private static readonly (int Address, int Count)[] CoefTables =
        {
            (0x206b720, 64), (0x206b720, 64), (0x206b820, 32), (0x206b8a0, 32),
            (0x206b920, 16), (0x206bebc, 16), (0x206b960, 8), (0x206b980, 8)
        };
        private const int PulseTable = 0x206c268; // 64 scales x 8 levels

        private static int[][]? _coefTables = null;
        private static int[]? _pulseTable = null;

        private readonly int[] _state = new int[9];
        private int _deemphasis = 0;
        private readonly int[] _coefs = new int[8];
        private readonly short[] _excitation = new short[SamplesPerRecord];

        // false if the cart's code isn't loaded (no First Hunt import)
        public static bool Available => LoadTables();

        private static bool LoadTables()
        {
            if (_pulseTable != null) return true;
            if (FirstHuntRules.ReadArm9Int(PulseTable) == null) return false;
            var coefs = new int[CoefTables.Length][];
            for (int t = 0; t < CoefTables.Length; t++)
            {
                (int address, int count) = CoefTables[t];
                coefs[t] = new int[count];
                for (int i = 0; i < count; i++)
                {
                    coefs[t][i] = FirstHuntRules.ReadArm9Int(address + i * 4) ?? 0;
                }
            }
            var pulses = new int[64 * 8];
            for (int i = 0; i < pulses.Length; i++)
            {
                pulses[i] = FirstHuntRules.ReadArm9Int(PulseTable + i * 4) ?? 0;
            }
            _coefTables = coefs;
            _pulseTable = pulses;
            return true;
        }

        public void Reset()
        {
            Array.Clear(_state);
            _deemphasis = 0;
        }

        // decodes one 40-byte record into 256 samples
        public void Decode(ReadOnlySpan<byte> record, Span<short> output)
        {
            if (!LoadTables())
            {
                output[..SamplesPerRecord].Clear();
                return;
            }
            Span<uint> w = stackalloc uint[10];
            for (int i = 0; i < 10; i++)
            {
                w[i] = BinaryPrimitives.ReadUInt32LittleEndian(record[(i * 4)..]);
            }
            int[][] coefTables = _coefTables!;
            int[] pulseTable = _pulseTable!;
            uint w0 = w[0];
            Span<int> index = stackalloc int[8];
            index[0] = (int)(w0 >> 26);
            index[1] = (int)(w0 >> 20 & 63);
            index[2] = (int)(w0 >> 15 & 31);
            index[3] = (int)(w0 >> 10 & 31);
            index[4] = (int)(w0 >> 6 & 15);
            index[5] = (int)((w[3] & 1) << 3 | (w[5] & 1) << 2 | (w[7] & 1) << 1 | w[9] & 1);
            index[6] = (int)(w0 >> 3 & 7);
            index[7] = (int)(w0 & 7);
            for (int i = 0; i < 8; i++)
            {
                _coefs[i] = coefTables[i][index[i]];
            }
            // excitation (stored as halfwords, as the cart does in the output buffer)
            short[] exc = _excitation;
            Array.Clear(exc);
            for (int s = 0; s < 4; s++)
            {
                int scale = (int)(w[1] >> 8 + 6 * s & 63) * 8;
                int at = s * 64 + (int)(w[1] >> 2 * s & 3);
                uint a = w[2 + 2 * s], b = w[3 + 2 * s];
                for (int i = 0; i < 21; i++)
                {
                    int level = i < 10 ? (int)(a >> 29 - 3 * i & 7)
                        : i < 20 ? (int)(b >> 29 - 3 * (i - 10) & 7)
                        : (int)((a & 3) << 1 | b >> 1 & 1);
                    exc[at + 3 * i] = (short)pulseTable[scale + level];
                }
            }
            // synthesis: 8-stage lattice (Q15, rounded), then de-emphasis; the cart keeps the state in 32-bit registers
            int[] st = _state, k = _coefs;
            int de = _deemphasis;
            for (int n = 0; n < SamplesPerRecord; n++)
            {
                int r = exc[n];
                for (int i = 7; i >= 0; i--)
                {
                    r = unchecked(r - (k[i] * st[i] + 0x4000 >> 15));
                    st[i + 1] = unchecked(st[i] + (k[i] * r + 0x4000 >> 15));
                }
                st[0] = r;
                de = unchecked(r + (de * 0x6e14 + 0x4000 >> 15));
                output[n] = (short)Math.Clamp(unchecked(de + de), short.MinValue, short.MaxValue);
            }
            _deemphasis = de;
        }
    }
}
