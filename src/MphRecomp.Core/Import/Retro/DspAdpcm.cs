using System;

namespace MphRecomp.Import.Retro
{
    /// <summary>GameCube DSP-ADPCM codec: 8-byte frames of 14 four-bit codes.</summary>
    public static class DspAdpcm
    {
        public const int FrameBytes = 8;
        public const int SamplesPerFrame = 14;

        /// <summary>One channel's decoder state. The default value is the start-of-stream state.</summary>
        public struct ChannelState
        {
            /// <summary>Previous output sample (H1).</summary>
            public int Hist1;

            /// <summary>The output sample before that (H2).</summary>
            public int Hist2;

            /// <summary>Most recent output sample, repeated by filler frames.</summary>
            public short Held;
        }

        /// <summary>Decodes one frame into exactly 14 samples, updating <paramref name="state"/>.</summary>
        public static void DecodeFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<short> coefficients,
            ref ChannelState state, Span<short> output)
        {
            if (frame.Length < FrameBytes)
            {
                throw new ArgumentException("A frame needs 8 bytes.", nameof(frame));
            }
            if (coefficients.Length < 16)
            {
                throw new ArgumentException("16 coefficients are needed.", nameof(coefficients));
            }
            if (output.Length < SamplesPerFrame)
            {
                throw new ArgumentException("The output needs room for 14 samples.", nameof(output));
            }
            DecodePartial(frame, coefficients, ref state, output, SamplesPerFrame);
        }

        // Decodes only the first `count` samples, so a caller can stop the history exactly on a
        // sample that isn't the frame's last (the loop end, which needn't be frame aligned).
        internal static void DecodePartial(ReadOnlySpan<byte> frame, ReadOnlySpan<short> coefficients,
            ref ChannelState state, Span<short> output, int count)
        {
            byte header = frame[0];
            if ((header & 0x80) != 0)
            {
                // Filler past the real sound data: hold the last sample, leave the history alone.
                output.Slice(0, count).Fill(state.Held);
                return;
            }

            int predictor = (header >> 4) & 7;
            int shift = (header & 15) + 11;
            long coefA = coefficients[2 * predictor];
            long coefB = coefficients[2 * predictor + 1];
            long h1 = state.Hist1;
            long h2 = state.Hist2;
            int last = state.Held;

            for (int i = 0; i < count; i++)
            {
                int packed = frame[1 + (i >> 1)];
                int nibble = (i & 1) == 0 ? packed >> 4 : packed & 15;
                // Sign-extend the 4-bit two's-complement code.
                long code = (nibble ^ 8) - 8;
                long sum = (code << shift) + 1024 + coefA * h1 + coefB * h2;
                long value = sum >> 11;
                if (value > short.MaxValue)
                {
                    value = short.MaxValue;
                }
                else if (value < short.MinValue)
                {
                    value = short.MinValue;
                }
                output[i] = (short)value;
                h2 = h1;
                h1 = value;
                last = (int)value;
            }

            state.Hist1 = (int)h1;
            state.Hist2 = (int)h2;
            state.Held = (short)last;
        }
    }
}
