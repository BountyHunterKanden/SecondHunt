using System;
using System.IO;

namespace MphRead.Formats.Vx
{
    // The audio blocks of a VX movie: 128 samples each, an excitation (a scaled copy of earlier excitation plus coded
    // pulses) run through an 8-tap synthesis filter whose coefficients come from three codebooks. All arithmetic is
    // 32-bit and wraps; divisions round toward zero.
    internal sealed class VxAudio
    {
        public const int BlockSize = 128;

        // tables from the file
        private readonly int[] _codebooks = new int[3 * 64 * 8];
        private readonly int[] _scaleFactors = new int[8];
        private readonly int[] _baseFilter = new int[8];
        private int _initialScale;

        // state carried from block to block
        private readonly int[] _history = new int[256];   // E: excitation of the block before last, then the last block's
        private readonly int[] _filter = new int[8];      // F
        private readonly int[] _previous = new int[8];    // P: the previous block's coefficients
        private bool _hasScale;
        private int _scale;

        // per-block work arrays
        private readonly int[] _words = new int[8];
        private readonly int[] _pulses = new int[42];
        private readonly int[] _excitation = new int[BlockSize];
        private readonly int[] _coefficients = new int[8];
        private readonly int[] _copy = new int[8];
        private readonly int[] _sets = new int[4 * 8];
        // output samples with the previous block's last 8 in front: entry 8 + n is y[n], entries 0..7 are L
        private readonly int[] _output = new int[8 + BlockSize];

        // g(i) = min(8, i + 1, 128 - i)
        private static readonly int[] _ramp = BuildRamp();

        private static int[] BuildRamp()
        {
            var ramp = new int[BlockSize];
            for (int i = 0; i < BlockSize; i++)
            {
                ramp[i] = Math.Min(8, Math.Min(i + 1, BlockSize - i));
            }
            return ramp;
        }

        // reads the audio tables (spec 1.2) from 3,124 bytes
        public void ReadTables(byte[] data)
        {
            int pos = 0;
            for (int i = 0; i < _codebooks.Length; i++)
            {
                _codebooks[i] = (short)(data[pos] | (data[pos + 1] << 8));
                pos += 2;
            }
            for (int i = 0; i < 8; i++)
            {
                _scaleFactors[i] = (ushort)(data[pos] | (data[pos + 1] << 8));
                pos += 2;
            }
            for (int i = 0; i < 8; i++)
            {
                _baseFilter[i] = BitConverter.ToInt32(data, pos);
                pos += 4;
            }
            _initialScale = BitConverter.ToInt32(data, pos);
        }

        public const int TableBytes = 3 * 64 * 8 * 2 + 8 * 2 + 8 * 4 + 4;

        public void ResetState()
        {
            Array.Clear(_history);
            Array.Clear(_output);
            Array.Clear(_filter);
            Array.Clear(_previous);
            _hasScale = false;
            _scale = 0;
        }

        // decodes one block from the (word-aligned) bitstream into destination[offset .. offset + 127]
        public void DecodeBlock(VxBitReader bits, short[] destination, int offset)
        {
            // 1. header
            int h1 = bits.ReadWord();
            int h2 = bits.ReadWord();
            int entry0 = h1 & 0x3F;
            int scaleIndex = (h1 >> 6) & 7;
            int back = (h1 >> 9) & 0x7F;
            int entry2 = h2 & 0x3F;
            int entry1 = (h2 >> 6) & 0x3F;
            int pack = (h2 >> 12) & 3;
            int start = (h2 >> 14) & 3;

            // 2-3. pulse words and values
            int[] words = _words;
            int[] pulses = _pulses;
            int pulseCount;
            int spacing;
            if (pack == 0)
            {
                for (int w = 0; w < 8; w++)
                {
                    words[w] = bits.ReadWord();
                }
                int n = 0;
                for (int w = 0; w < 8; w++)
                {
                    int word = words[w];
                    for (int shift = 13; shift >= 1; shift -= 3)
                    {
                        pulses[n++] = 2 * ((word >> shift) & 7) - 7;
                    }
                }
                int f = 4 * (words[0] & 1) + 2 * (words[1] & 1) + (words[2] & 1);
                pulses[n++] = 2 * f - 7;
                f = 4 * (words[3] & 1) + 2 * (words[4] & 1) + (words[5] & 1);
                pulses[n++] = 2 * f - 7;
                pulseCount = n;
                spacing = 3;
            }
            else
            {
                int wordCount = pack == 1 ? 5 : pack == 2 ? 4 : 3;
                int n = 0;
                for (int w = 0; w < wordCount; w++)
                {
                    int word = bits.ReadWord();
                    for (int shift = 14; shift >= 0; shift -= 2)
                    {
                        pulses[n++] = 2 * ((word >> shift) & 3) - 3;
                    }
                }
                pulseCount = n;
                spacing = pack == 1 ? 3 : pack == 2 ? 4 : 5;
            }

            // 4. scale
            int baseScale;
            if (back == 127)
            {
                baseScale = _initialScale;
            }
            else
            {
                if (!_hasScale)
                {
                    throw new ProgramException("VX audio: block refers to a previous scale that doesn't exist.");
                }
                baseScale = _scale;
            }
            int scale = baseScale * _scaleFactors[scaleIndex] / 8192;
            _scale = scale;
            _hasScale = true;

            // 6. excitation
            int[] x = _excitation;
            int[] history = _history;
            if (back <= 125)
            {
                int source = 127 - back;
                for (int i = 0; i < BlockSize; i++)
                {
                    x[i] = history[i + source] * _ramp[i] / 16;
                }
            }
            else
            {
                Array.Clear(x);
            }
            for (int k = 0; k < pulseCount; k++)
            {
                x[start + k * spacing] += pulses[k] * scale;
            }

            // 7. filter accumulator
            int[] filter = _filter;
            if (back == 127)
            {
                Array.Copy(_baseFilter, filter, 8);
            }
            int book0 = entry0 * 8;
            int book1 = 64 * 8 + entry1 * 8;
            int book2 = 2 * 64 * 8 + entry2 * 8;
            for (int i = 0; i < 8; i++)
            {
                filter[i] += _codebooks[book0 + i] + _codebooks[book1 + i] + _codebooks[book2 + i];
            }

            // 8. coefficients
            int[] a = _coefficients;
            int[] c = _copy;
            Array.Clear(a);
            for (int i = 0; i < 8; i++)
            {
                Array.Copy(a, c, 8);
                int fi = filter[i];
                for (int j = 0; j < i; j++)
                {
                    a[j] += c[i - 1 - j] * fi / 32768;
                }
                a[i] = fi;
            }
            for (int i = 0; i < 8; i++)
            {
                a[i] = a[i] / -2;
            }

            // 9. four coefficient sets, S0 at 0, S1 at 8, S2 at 16, S3 at 24
            int[] sets = _sets;
            int[] previous = _previous;
            for (int j = 0; j < 8; j++)
            {
                if (back == 127)
                {
                    sets[j] = a[j];
                    sets[8 + j] = a[j];
                    sets[16 + j] = a[j];
                    sets[24 + j] = a[j];
                }
                else
                {
                    int s3 = a[j];
                    int s1 = (previous[j] + s3) / 2;
                    int s0 = (previous[j] + s1) / 2;
                    int s2 = (s1 + s3) / 2;
                    sets[j] = s0;
                    sets[8 + j] = s1;
                    sets[16 + j] = s2;
                    sets[24 + j] = s3;
                }
            }

            // 10. synthesis
            int[] y = _output;
            for (int i = 0; i < BlockSize; i++)
            {
                int set = (i / 32) * 8;
                int acc = x[i] * 16384;
                int prior = 8 + i - 1; // y[i - 1]
                for (int j = 0; j < 8; j++)
                {
                    acc += y[prior - j] * sets[set + j];
                }
                int sample = acc / 16384;
                if (sample < -32768)
                {
                    sample = -32768;
                }
                else if (sample > 32767)
                {
                    sample = 32767;
                }
                y[8 + i] = sample;
                destination[offset + i] = (short)sample;
            }

            // 11. state update
            Array.Copy(history, BlockSize, history, 0, BlockSize);
            Array.Copy(x, 0, history, BlockSize, BlockSize);
            Array.Copy(y, BlockSize, y, 0, 8);
            Array.Copy(a, previous, 8);
        }
    }
}
