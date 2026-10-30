using System;
using System.Buffers.Binary;
using System.IO;
using MphRecomp.Import.Retro;

namespace MphRecomp.PrimeMusic
{
    // A DSP-ADPCM track whose channels are each a plain run of 8-byte frames (Import/Retro/DspAdpcm.cs), the way Prime 1
    // and Corruption store their music:
    //   Prime 1 (GameCube, Audio/<name>L.dsp + <name>R.dsp, or one mono .dsp): the standard Nintendo DSP header, 0x60
    //     bytes big-endian, then the frames: +0 u32 samples, +4 u32 nibbles, +8 u32 rate, +0xC u16 loop flag, +0x10 u32
    //     loop start and +0x14 loop end (nibble addresses: 16 a frame, the first 2 its header byte; the end is the last
    //     looped sample), +0x1C s16 coefficients[16], +0x44 u16 loop ps, +0x46 s16 loop hist1, +0x48 s16 loop hist2.
    //   Corruption (Wii, Audio/**/<name>.fsb): FMOD FSB3 (version 3.1) holding one GameCube-ADPCM sample, little-endian
    //     except the ADPCM block: "FSB3", +4 s32 samples in the file (1), +8 s32 sample headers' size, +0xC s32 data size,
    //     +0x10 u32 version; the sample header at 0x18: +0 u16 size, +2 name[30], +0x20 u32 samples, +0x24 u32 data bytes,
    //     +0x28 u32 loop start, +0x2C u32 loop end (the last looped sample), +0x30 u32 mode (2 = loop, 0x02000000 =
    //     GameCube ADPCM), +0x34 u32 rate, +0x3E u16 channels, then at +0x50 per channel 0x2E bytes big-endian: s16
    //     coefficients[16], u16 gain, u16 ps, s16 hist1, s16 hist2, u16 loop ps, s16 loop hist1, s16 loop hist2. The data
    //     follows the sample headers; a stereo file interleaves its channels 2 bytes at a time.
    // Checked on the owner's discs (GM8E01, RM3E01) by the 2026-10-14 survey: every file decodes without clipping.
    // A loop goes back to the loop start with the decoder history the first pass had there (= the header's loop hist).
    public sealed class DspMusic : IMusicStream
    {
        const int HeaderSize = 0x60;

        readonly byte[][] _frames;      // per channel: its frames, back to back
        readonly int[] _offsets;        // where they start in _frames[c]
        readonly short[][] _coefs;

        public int Channels { get; }
        public int SampleRate { get; }
        public int SampleCount { get; }
        public bool Loops { get; }
        public int LoopStart { get; }
        public int LoopEnd { get; }

        DspMusic(byte[][] frames, int[] offsets, short[][] coefs, int rate, int samples, bool loops, int loopStart, int loopEnd)
        {
            _frames = frames;
            _offsets = offsets;
            _coefs = coefs;
            Channels = frames.Length;
            SampleRate = rate;
            // a damaged header can't claim more samples than the data holds
            int capacity = int.MaxValue;
            for (int c = 0; c < Channels; c++)
            {
                capacity = Math.Min(capacity, (frames[c].Length - offsets[c]) / DspAdpcm.FrameBytes * DspAdpcm.SamplesPerFrame);
            }
            SampleCount = Math.Min(samples, capacity);
            if (SampleCount < 1) throw new InvalidDataException("the track holds no samples");
            LoopStart = 0;
            LoopEnd = SampleCount;
            loopEnd = Math.Min(loopEnd, SampleCount);
            if (loops && loopStart >= 0 && loopStart < loopEnd)
            {
                Loops = true;
                LoopStart = loopStart;
                LoopEnd = loopEnd;
            }
        }

        // ---- Prime 1: standard DSP files ----

        public static bool IsDsp(ReadOnlySpan<byte> file) =>
            file.Length >= HeaderSize && U32(file, 0x08) is >= 4000 and <= 96000 && U16(file, 0x0E) == 0
            && U32(file, 0x04) >= U32(file, 0x00);

        // one file = mono, two (left, right) = stereo
        public static DspMusic FromDsp(params byte[][] files)
        {
            if (files.Length is < 1 or > 2) throw new ArgumentException("one or two DSP files", nameof(files));
            var coefs = new short[files.Length][];
            int samples = int.MaxValue, rate = 0;
            bool loops = true;
            int loopStart = 0, loopEnd = int.MaxValue;
            for (int c = 0; c < files.Length; c++)
            {
                byte[] f = files[c];
                if (!IsDsp(f)) throw new InvalidDataException("not a standard DSP file");
                int r = (int)U32(f, 0x08);
                if (c > 0 && r != rate) throw new InvalidDataException("the channels' rates differ");
                rate = r;
                samples = Math.Min(samples, (int)U32(f, 0x00));
                coefs[c] = Coefs(f, 0x1C);
                bool l = U16(f, 0x0C) != 0;
                loops &= l;
                if (l)
                {
                    loopStart = Math.Max(loopStart, NibbleToSample(U32(f, 0x10)));
                    loopEnd = Math.Min(loopEnd, NibbleToSample(U32(f, 0x14)) + 1);
                }
            }
            var offsets = new int[files.Length];
            Array.Fill(offsets, HeaderSize);
            return new DspMusic(files, offsets, coefs, rate, samples, loops, loopStart, loopEnd);
        }

        // a nibble address (16 a frame, 2 of them the frame's header) as a sample index
        static int NibbleToSample(uint nibble) => (int)(nibble / 16 * DspAdpcm.SamplesPerFrame + Math.Max(0, (int)(nibble % 16) - 2));

        // ---- Corruption: FSB3 files ----

        public static bool IsFsb3(ReadOnlySpan<byte> file) =>
            file.Length >= 0x18 + 0x50 && file[0] == (byte)'F' && file[1] == (byte)'S' && file[2] == (byte)'B' && file[3] == (byte)'3';

        public static DspMusic FromFsb3(byte[] file)
        {
            if (!IsFsb3(file)) throw new InvalidDataException("not an FSB3 file");
            int headers = (int)L32(file, 0x08), dataSize = (int)L32(file, 0x0C);
            if (L32(file, 0x04) < 1) throw new InvalidDataException("the FSB3 file holds no sample");
            const int h = 0x18;
            uint mode = L32(file, h + 0x30);
            if ((mode & 0x02000000) == 0) throw new InvalidDataException($"FSB3 sample mode {mode:x8} isn't GameCube ADPCM");
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(h + 0x3E));
            if (channels is < 1 or > 2) throw new InvalidDataException($"FSB3 sample with {channels} channels");
            int size = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(h));
            if (size < 0x50 + 0x2E * channels) throw new InvalidDataException("FSB3 sample header too short for its ADPCM blocks");
            int samples = (int)L32(file, h + 0x20), bytes = (int)L32(file, h + 0x24);
            int rate = (int)L32(file, h + 0x34);
            int data = h + headers;
            bytes = Math.Min(Math.Min(bytes, dataSize), file.Length - data);
            var frames = new byte[channels][];
            var coefs = new short[channels][];
            for (int c = 0; c < channels; c++)
            {
                coefs[c] = Coefs(file, h + 0x50 + c * 0x2E);
                if (channels == 1)
                {
                    frames[c] = file.AsSpan(data, bytes).ToArray();
                    continue;
                }
                // 2-byte interleave: channel c's byte j sits at (j / 2) * 2 * channels + c * 2 + j % 2
                int per = bytes / channels;
                var b = new byte[per];
                for (int j = 0; j < per; j++)
                {
                    b[j] = file[data + (j >> 1) * 2 * channels + c * 2 + (j & 1)];
                }
                frames[c] = b;
            }
            bool loops = (mode & 2) != 0;
            int loopStart = (int)L32(file, h + 0x28), loopEnd = (int)L32(file, h + 0x2C) + 1;
            return new DspMusic(frames, new int[channels], coefs, rate, samples, loops, loopStart, loopEnd);
        }

        public IMusicReader OpenReader() => new Reader(this);

        static short[] Coefs(ReadOnlySpan<byte> f, int at)
        {
            var c = new short[16];
            for (int i = 0; i < 16; i++) c[i] = BinaryPrimitives.ReadInt16BigEndian(f.Slice(at + 2 * i));
            return c;
        }

        static uint U32(ReadOnlySpan<byte> f, int at) => BinaryPrimitives.ReadUInt32BigEndian(f.Slice(at));
        static ushort U16(ReadOnlySpan<byte> f, int at) => BinaryPrimitives.ReadUInt16BigEndian(f.Slice(at));
        static uint L32(ReadOnlySpan<byte> f, int at) => BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(at));

        // DspAdpcm's frame maths, decoding samples [from, to) of a frame (a loop can start inside one); the samples before
        // `from` are left as they are
        static void Decode(ReadOnlySpan<byte> frame, short[] coefs, ref int hist1, ref int hist2, Span<short> output, int from, int to)
        {
            byte header = frame[0];
            int predictor = (header >> 4) & 7;
            int shift = (header & 15) + 11;
            long coefA = coefs[2 * predictor], coefB = coefs[2 * predictor + 1];
            long h1 = hist1, h2 = hist2;
            for (int i = from; i < to; i++)
            {
                int packed = frame[1 + (i >> 1)];
                int nibble = (i & 1) == 0 ? packed >> 4 : packed & 15;
                long code = (nibble ^ 8) - 8;
                long value = ((code << shift) + 1024 + coefA * h1 + coefB * h2) >> 11;
                value = Math.Clamp(value, short.MinValue, short.MaxValue);
                output[i] = (short)value;
                h2 = h1;
                h1 = value;
            }
            hist1 = (int)h1;
            hist2 = (int)h2;
        }

        sealed class Reader : IMusicReader
        {
            const int N = DspAdpcm.SamplesPerFrame;
            readonly DspMusic _m;
            readonly int[] _h1, _h2;            // per channel, the history after the decoded frame
            readonly int[] _loopH1, _loopH2;    // the history the first pass had at the loop start
            readonly short[] _decoded;          // one frame per channel, channel c at [14c, 14c + 14)
            long _position, _decodedFrame = -1;
            bool _haveLoop, _finished;

            public Reader(DspMusic m)
            {
                _m = m;
                int c = m.Channels;
                _h1 = new int[c]; _h2 = new int[c]; _loopH1 = new int[c]; _loopH2 = new int[c];
                _decoded = new short[N * c];
            }

            public long Position => _position;
            public bool Finished => _finished;

            public void Restart()
            {
                _position = 0;
                _decodedFrame = -1;
                _finished = false;
                Array.Clear(_h1);
                Array.Clear(_h2);
            }

            public int Read(Span<short> destination, int frames)
            {
                int ch = _m.Channels;
                if (destination.Length < (long)frames * ch) throw new ArgumentException("destination too small", nameof(destination));
                long end = _m.Loops ? _m.LoopEnd : _m.SampleCount;
                int written = 0;
                while (written < frames)
                {
                    if (_position >= end)
                    {
                        if (!_m.Loops)
                        {
                            _finished = true;
                            break;
                        }
                        Jump();
                    }
                    long frame = _position / N;
                    int within = (int)(_position - frame * N);
                    if (frame != _decodedFrame) DecodeAt(frame, within);
                    int frameEnd = (int)Math.Min(N, end - frame * N);
                    int run = Math.Min(frameEnd - within, frames - written);
                    for (int i = 0; i < run; i++)
                    {
                        for (int c = 0; c < ch; c++) destination[(written + i) * ch + c] = _decoded[c * N + within + i];
                    }
                    written += run;
                    _position += run;
                }
                if (!_m.Loops && _position >= end) _finished = true;
                return written;
            }

            void Jump()
            {
                _position = _m.LoopStart;
                _decodedFrame = -1;
                for (int c = 0; c < _m.Channels; c++)
                {
                    _h1[c] = _loopH1[c];
                    _h2[c] = _loopH2[c];
                }
                _jumped = true;
            }

            bool _jumped;

            void DecodeAt(long frame, int within)
            {
                // after a loop jump decoding starts at the loop start inside its frame, with the history kept for it
                int from = _jumped ? within : 0;
                _jumped = false;
                long first = frame * N;
                long loopStart = _m.LoopStart;
                bool holdsLoopStart = _m.Loops && !_haveLoop && loopStart >= first && loopStart < first + N;
                for (int c = 0; c < _m.Channels; c++)
                {
                    int h1 = _h1[c], h2 = _h2[c];
                    ReadOnlySpan<byte> f = _m._frames[c].AsSpan(_m._offsets[c] + (int)(frame * DspAdpcm.FrameBytes), DspAdpcm.FrameBytes);
                    Span<short> o = _decoded.AsSpan(c * N, N);
                    if (holdsLoopStart)
                    {
                        // the history right before the loop start's sample
                        int k = (int)(loopStart - first);
                        if (k == 0) { _loopH1[c] = h1; _loopH2[c] = h2; }
                        Decode(f, _m._coefs[c], ref h1, ref h2, o, from, N);
                        if (k == 1) { _loopH1[c] = o[0]; _loopH2[c] = _h1[c]; }
                        else if (k >= 2) { _loopH1[c] = o[k - 1]; _loopH2[c] = o[k - 2]; }
                    }
                    else
                    {
                        Decode(f, _m._coefs[c], ref h1, ref h2, o, from, N);
                    }
                    _h1[c] = h1;
                    _h2[c] = h2;
                }
                if (holdsLoopStart) _haveLoop = true;
                _decodedFrame = frame;
            }
        }
    }
}
