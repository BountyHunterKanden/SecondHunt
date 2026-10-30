using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Brawl
{
    // One decoded sample sound. Rate in Hz; Volume = the sound entry's volume byte (0-127); LoopStart = a sample number;
    // Channels = one array of decoded samples per channel.
    public sealed record BrawlSoundSample(string Name, int Rate, int Volume, bool Loops, int LoopStart, short[][] Channels);

    // A Brawl sound archive ("RSAR", sound/smashbros_sound.brsar). Too big to hold whole on a phone, so it works on a
    // seekable stream: the name and description sections stay in memory, a sound's file header part and sample data
    // are read when that sound is decoded. Clean-room from docs/cleanroom/F_brawl_sound_spec.md.
    public sealed class BrawlSoundArchive
    {
        const int KindSample = 3;
        const int EncodingDspAdpcm = 2;
        const int NibblesPerFrame = 16;

        readonly Stream _stream;
        readonly long _length;
        readonly byte[] _info;
        readonly Dictionary<string, int> _byName;
        readonly Dictionary<int, byte[]> _files = new Dictionary<int, byte[]>();
        readonly object _gate = new object();

        BrawlSoundArchive(Stream stream, long length, byte[] info, Dictionary<string, int> byName)
        {
            _stream = stream;
            _length = length;
            _info = info;
            _byName = byName;
        }

        // Reads and checks the header and keeps the name and description sections. The stream stays the caller's:
        // it's kept for later reads and never disposed here.
        public static BrawlSoundArchive Open(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new ArgumentException("The sound archive needs a readable, seekable stream.", nameof(stream));
            }
            long length = stream.Length;

            const string hp = "sound archive header";
            byte[] h = ReadAt(stream, length, 0, 0x28, hp);
            BrawlBytes.Magic(h, 0, "RSAR", hp);
            CheckByteOrder(h, 4, hp);
            uint symbOff = BrawlBytes.U32(h, 0x10, hp), symbLen = BrawlBytes.U32(h, 0x14, hp);
            uint infoOff = BrawlBytes.U32(h, 0x18, hp), infoLen = BrawlBytes.U32(h, 0x1C, hp);
            uint fileOff = BrawlBytes.U32(h, 0x20, hp);

            byte[] symb = ReadAt(stream, length, symbOff, symbLen, "name section");
            BrawlBytes.Magic(symb, 0, "SYMB", "name section");
            byte[] info = ReadAt(stream, length, infoOff, infoLen, "description section");
            BrawlBytes.Magic(info, 0, "INFO", "description section");
            // only the data section's tag is checked; its contents are read per sound
            BrawlBytes.Magic(ReadAt(stream, length, fileOff, 4, "data section"), 0, "FILE", "data section");

            var names = ReadNames(symb);

            // every sound's name, first sound wins on a repeat
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            long sounds = Target(info, 8 + 0x00, 8, "sound list link");
            if (sounds >= 0)
            {
                uint count = BrawlBytes.U32(info, sounds, "sound list");
                for (int i = 0; i < count; i++)
                {
                    string sp = $"sound {i}";
                    long e = Target(info, sounds + 4 + 8L * i, 8, sp + " link");
                    if (e < 0)
                    {
                        continue;
                    }
                    uint nameNo = BrawlBytes.U32(info, e, sp);
                    if (nameNo == uint.MaxValue)
                    {
                        continue;
                    }
                    if (nameNo >= names.Length)
                    {
                        throw new InvalidDataException($"{sp}: name number {nameNo} past the name table ({names.Length} names)");
                    }
                    byName.TryAdd(names[nameNo], i);
                }
            }
            return new BrawlSoundArchive(stream, length, info, byName);
        }

        // the sound's number, -1 when no sound has that name (exact, ordinal)
        public int Find(string name) => _byName.TryGetValue(name, out int n) ? n : -1;

        // the named sample sound decoded, null when no sound has that name
        public BrawlSoundSample? Decode(string name)
        {
            int number = Find(name);
            if (number < 0)
            {
                return null;
            }
            lock (_gate)
            {
                return DecodeSound(name, number);
            }
        }

        BrawlSoundSample DecodeSound(string name, int number)
        {
            var d = _info;
            const long B = 8;
            string sp = $"sound {number} ({name})";

            // sound entry (3.1)
            long sound = Element(d, B + 0x00, (uint)number, B, "sound list");
            uint fileNo = BrawlBytes.U32(d, sound + 0x04, sp);
            int volume = BrawlBytes.U8(d, sound + 0x14, sp);
            int kind = BrawlBytes.U8(d, sound + 0x16, sp);
            if (kind != KindSample)
            {
                throw new InvalidDataException($"{sp}: kind {kind}, not a sample sound ({KindSample})");
            }
            long detail = Required(d, sound + 0x18, B, sp + " detail");
            uint soundIndex = BrawlBytes.U32(d, detail, sp + " detail");

            // file entry (3.2): its first placement names the group and the item
            string fp = $"file {fileNo}";
            long file = Element(d, B + 0x18, fileNo, B, "file list");
            long placement = Element(d, file + 0x14, 0, B, fp + " placements");
            uint groupNo = BrawlBytes.U32(d, placement, fp + " placement");
            uint itemNo = BrawlBytes.U32(d, placement + 4, fp + " placement");

            // group entry and item (3.3)
            string gp = $"group {groupNo}";
            long group = Element(d, B + 0x20, groupNo, B, "group list");
            uint headerBlock = BrawlBytes.U32(d, group + 0x10, gp);
            uint sampleBlock = BrawlBytes.U32(d, group + 0x18, gp);
            // the item list holds links to the 0x18-byte items, not the items themselves
            long item = Element(d, group + 0x20, itemNo, B, gp + " items");
            string ip = $"{gp} item {itemNo}";
            BrawlBytes.Need(d, item, 0x18, ip);
            uint itemFile = BrawlBytes.U32(d, item, ip);
            if (itemFile != fileNo)
            {
                throw new InvalidDataException($"{ip}: holds file {itemFile}, not file {fileNo}");
            }
            long partAt = (long)headerBlock + BrawlBytes.U32(d, item + 0x04, ip);
            uint partLen = BrawlBytes.U32(d, item + 0x08, ip);
            long fileData = (long)sampleBlock + BrawlBytes.U32(d, item + 0x0C, ip);

            if (!_files.TryGetValue((int)fileNo, out var w))
            {
                w = ReadAt(_stream, _length, partAt, partLen, fp + " header part");
                _files[(int)fileNo] = w;
            }

            // sample-sound file header (4.1)
            BrawlBytes.Magic(w, 0, "RWSD", fp);
            CheckByteOrder(w, 4, fp);
            long dataSec = BrawlBytes.U32(w, 0x10, fp);
            long waveSec = BrawlBytes.U32(w, 0x18, fp);
            BrawlBytes.Magic(w, dataSec, "DATA", fp + " sound section");
            BrawlBytes.Magic(w, waveSec, "WAVE", fp + " sample section");

            // sound table -> first note -> sample number (4.2)
            long D = dataSec + 8;
            // the sound table is the list at D itself, not a link to one
            long entry = ListElement(w, D, soundIndex, D, fp + " sound table");
            long note = Element(w, entry + 0x10, 0, D, $"{fp} sound {soundIndex} notes");
            int sampleNo = BrawlBytes.I32(w, note, $"{fp} sound {soundIndex} note");

            // sample header (4.3)
            uint sampleCount = BrawlBytes.U32(w, waveSec + 0x08, fp + " sample section");
            if (sampleNo < 0 || sampleNo >= sampleCount)
            {
                throw new InvalidDataException($"{fp}: sample {sampleNo} past the sample table ({sampleCount} samples)");
            }
            string hp = $"{fp} sample {sampleNo}";
            long H = waveSec + BrawlBytes.U32(w, waveSec + 0x0C + 4L * sampleNo, hp);
            int encoding = BrawlBytes.U8(w, H, hp);
            if (encoding != EncodingDspAdpcm)
            {
                throw new InvalidDataException($"{hp}: encoding {encoding}, only DSP-ADPCM ({EncodingDspAdpcm}) is read");
            }
            bool loops = BrawlBytes.U8(w, H + 0x01, hp) == 1;
            int channels = BrawlBytes.U8(w, H + 0x02, hp);
            int rate = (BrawlBytes.U8(w, H + 0x03, hp) << 16) | BrawlBytes.U16(w, H + 0x04, hp);
            uint loopAddress = BrawlBytes.U32(w, H + 0x08, hp);
            uint endAddress = BrawlBytes.U32(w, H + 0x0C, hp);
            long chanTable = H + BrawlBytes.U32(w, H + 0x10, hp);
            long sampleData = fileData + BrawlBytes.U32(w, H + 0x14, hp);

            int count = SampleCount(endAddress);
            int frames = (count + DspAdpcm.SamplesPerFrame - 1) / DspAdpcm.SamplesPerFrame;
            var output = new short[channels][];
            Span<short> coefs = stackalloc short[16];
            Span<short> block = stackalloc short[DspAdpcm.SamplesPerFrame];
            for (int c = 0; c < channels; c++)
            {
                string cp = $"{hp} channel {c}";
                long chan = H + BrawlBytes.U32(w, chanTable + 4L * c, cp);
                long chanData = sampleData + BrawlBytes.U32(w, chan, cp);
                long settings = H + BrawlBytes.U32(w, chan + 0x04, cp);
                for (int k = 0; k < 16; k++)
                {
                    coefs[k] = BrawlBytes.I16(w, settings + 2L * k, cp + " decoder settings");
                }
                var state = new DspAdpcm.ChannelState
                {
                    Hist1 = BrawlBytes.I16(w, settings + 0x24, cp + " decoder settings"),
                    Hist2 = BrawlBytes.I16(w, settings + 0x26, cp + " decoder settings"),
                };
                state.Held = (short)state.Hist1;

                byte[] encoded = ReadAt(_stream, _length, chanData, (long)frames * DspAdpcm.FrameBytes, cp + " data");
                var samples = new short[count];
                for (int f = 0; f < frames; f++)
                {
                    DspAdpcm.DecodeFrame(encoded.AsSpan(f * DspAdpcm.FrameBytes, DspAdpcm.FrameBytes), coefs, ref state, block);
                    int at = f * DspAdpcm.SamplesPerFrame;
                    int n = Math.Min(DspAdpcm.SamplesPerFrame, count - at);
                    block.Slice(0, n).CopyTo(samples.AsSpan(at, n));
                }
                output[c] = samples;
            }

            return new BrawlSoundSample(name, rate, volume, loops, SampleNumber(loopAddress), output);
        }

        // Nibble address -> sample number: nibbles 0 and 1 of each 16 are the frame's header byte. An address on a
        // header byte stands for the next frame's first sample.
        static int SampleNumber(uint address)
        {
            long frame = address / NibblesPerFrame;
            long nibble = address % NibblesPerFrame;
            long n = frame * DspAdpcm.SamplesPerFrame + Math.Max(0, nibble - 2);
            return (int)Math.Min(n, int.MaxValue);
        }

        // Samples 0 through the end address's sample, inclusive. An end on a header byte ends with the previous
        // frame's last sample.
        static int SampleCount(uint end)
        {
            long frame = end / NibblesPerFrame;
            long nibble = end % NibblesPerFrame;
            long n = frame * DspAdpcm.SamplesPerFrame + (nibble >= 2 ? nibble - 1 : 0);
            if (n > int.MaxValue / 2)
            {
                throw new InvalidDataException($"sample end address 0x{end:X} is too large");
            }
            return (int)n;
        }

        // the name table (section 2): name number k = the k-th string
        static string[] ReadNames(byte[] symb)
        {
            const long N = 8;
            const string np = "name table";
            long table = N + BrawlBytes.U32(symb, N, np);
            uint count = BrawlBytes.U32(symb, table, np);
            BrawlBytes.Need(symb, table + 4, 4L * count, np);
            var names = new string[count];
            for (int k = 0; k < count; k++)
            {
                long at = N + BrawlBytes.U32(symb, table + 4 + 4L * k, np);
                if (at >= symb.Length)
                {
                    throw new InvalidDataException($"name {k}: offset 0x{at:X} past the name section (0x{symb.Length:X} bytes)");
                }
                var rest = symb.AsSpan((int)at);
                int z = rest.IndexOf((byte)0);
                if (z < 0)
                {
                    throw new InvalidDataException($"name {k}: no terminating zero before the end of the name section");
                }
                names[k] = Encoding.ASCII.GetString(rest.Slice(0, z));
            }
            return names;
        }

        // A link (8 bytes) at `at`: its target's position in `d` (offset + `baseAt`), or -1 when it has no target.
        static long Target(byte[] d, long at, long baseAt, string part)
        {
            BrawlBytes.Need(d, at, 8, part);
            int flag = d[at];
            if (flag == 0)
            {
                return -1;
            }
            if (flag != 1)
            {
                throw new InvalidDataException($"{part}: link kind {flag} at 0x{at:X} is neither an offset (1) nor empty (0)");
            }
            long target = baseAt + BrawlBytes.U32(d, at + 4, part);
            if (target >= d.Length)
            {
                throw new InvalidDataException($"{part}: target 0x{target:X} past the end (0x{d.Length:X} bytes)");
            }
            return target;
        }

        // a link that must have a target
        static long Required(byte[] d, long at, long baseAt, string part)
        {
            long t = Target(d, at, baseAt, part);
            if (t < 0)
            {
                throw new InvalidDataException($"{part}: the link at 0x{at:X} has no target");
            }
            return t;
        }

        // Element `index` of the list a link at `linkAt` points to: the element's position in `d`.
        static long Element(byte[] d, long linkAt, uint index, long baseAt, string part) =>
            ListElement(d, Required(d, linkAt, baseAt, part), index, baseAt, part);

        // Element `index` of the list that starts at `list` (its count): the element's position in `d`.
        static long ListElement(byte[] d, long list, uint index, long baseAt, string part)
        {
            uint count = BrawlBytes.U32(d, list, part);
            if (index >= count)
            {
                throw new InvalidDataException($"{part}: element {index} past the last ({count} elements)");
            }
            return Required(d, list + 4 + 8L * index, baseAt, $"{part} element {index}");
        }

        static void CheckByteOrder(byte[] d, long at, string part)
        {
            ushort bom = BrawlBytes.U16(d, at, part);
            if (bom != 0xFEFF)
            {
                throw new InvalidDataException($"{part}: byte-order mark 0x{bom:X4}, expected 0xFEFF");
            }
        }

        // `count` bytes at absolute `at` in the archive, which must lie inside it
        static byte[] ReadAt(Stream stream, long length, long at, long count, string part)
        {
            if (at < 0 || count < 0 || at + count > length)
            {
                throw new InvalidDataException($"{part}: 0x{count:X} bytes at 0x{at:X} run past the archive's end (0x{length:X} bytes)");
            }
            if (count > int.MaxValue)
            {
                throw new InvalidDataException($"{part}: 0x{count:X} bytes is too large to read");
            }
            var buf = new byte[count];
            stream.Seek(at, SeekOrigin.Begin);
            stream.ReadExactly(buf);
            return buf;
        }
    }
}
