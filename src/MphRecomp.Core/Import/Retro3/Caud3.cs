using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // Corruption sounds (docs/MP3_HYPER_EFFECTS_FORMATS.md section 5, from our own survey of the disc):
    //   CAUD (version 9): name, groups, a few header values, flags (1 = loops), then entries: u32 size + that many bytes,
    //     each starting u64 CSMP id, f32, u32, u32, f32, f32, f32 volume (1.0 = full), ... (the rest unparsed). Several
    //     entries = alternatives (an impact picks one).
    //   CSMP: "CSMP" u32 version, then chunks (4cc + u32 size): INFO, PAD, DATA = a standard 0x60-byte Nintendo DSP-ADPCM
    //     header (samples, nibbles, rate, loop flag, format, loop start/end, current, 16 coefficients, gain, ps, yn1,
    //     yn2, ...) and its 8-byte frames, mono.
    // Added for the Dark Samus Hyper Mode thread, 2026-10-12.
    public sealed class Caud3
    {
        public string Name = "";
        public uint Flags;
        public bool Loops => (Flags & 1) != 0;
        public readonly List<(ulong Csmp, float Volume)> Entries = new();

        public static Caud3 Load(byte[] b)
        {
            if (Encoding.Latin1.GetString(b, 0, 4) != "CAUD") throw new InvalidDataException("not a Corruption sound (CAUD)");
            var c = new Caud3();
            int o = 8;
            c.Name = Be.CString(b, o, out int end);
            o = end + 1;
            uint groups = Be.U32(b, o);
            o += 4;
            for (uint i = 0; i < groups; i++)
            {
                Be.CString(b, o, out end);
                o = end + 1;
            }
            o += 16;    // f32, u32, f32, f32
            c.Flags = Be.U32(b, o);
            uint n = Be.U32(b, o + 4);
            o += 8;
            for (uint i = 0; i < n; i++)
            {
                int size = (int)Be.U32(b, o);
                int e = o + 4;
                if (size < 32 || e + size > b.Length) throw new InvalidDataException($"CAUD {c.Name}: entry {i} runs past the file");
                c.Entries.Add((Part.U64(b, e), Be.F32(b, e + 28)));
                o = e + size;
            }
            return c;
        }

        // a CSMP as 16-bit PCM: samples, rate, loops
        public static (short[] Pcm, int Rate, bool Loop) Decode(byte[] b)
        {
            if (Encoding.Latin1.GetString(b, 0, 4) != "CSMP") throw new InvalidDataException("not a Corruption sample (CSMP)");
            int o = 8, data = -1, dataSize = 0;
            while (o + 8 <= b.Length)
            {
                string tag = Encoding.Latin1.GetString(b, o, 4);
                int size = (int)Be.U32(b, o + 4);
                if (tag == "DATA")
                {
                    data = o + 8;
                    dataSize = size;
                    break;
                }
                o += 8 + size;
            }
            if (data < 0 || dataSize < 0x60) throw new InvalidDataException("CSMP: no DATA chunk");
            int samples = (int)Be.U32(b, data);
            int rate = (int)Be.U32(b, data + 8);
            bool loop = Be.U16(b, data + 12) != 0;
            var coef = new short[16];
            for (int i = 0; i < 16; i++) coef[i] = Be.I16(b, data + 28 + i * 2);
            var pcm = new short[samples];
            var state = new DspAdpcm.ChannelState();
            Span<short> frame = stackalloc short[DspAdpcm.SamplesPerFrame];
            int p = data + 0x60, at = 0;
            while (at < samples && p + DspAdpcm.FrameBytes <= b.Length)
            {
                DspAdpcm.DecodeFrame(b.AsSpan(p, DspAdpcm.FrameBytes), coef, ref state, frame);
                int k = Math.Min(DspAdpcm.SamplesPerFrame, samples - at);
                frame[..k].CopyTo(pcm.AsSpan(at));
                at += k;
                p += DspAdpcm.FrameBytes;
            }
            return (pcm, rate, loop);
        }
    }
}
