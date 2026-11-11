using System;
using System.Collections.Generic;
using System.Text;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 area (MREA version 0x1E), read far enough for its script layers. Header: magic, version,
    // area transform (3x4), then mesh count, layer count, section count, compressed block count and section-number
    // count, padded to 32. Then the section sizes, the block table (buffer size, decompressed size, compressed size,
    // section count; compressed size 0 = stored) and the section-number table (fourCC + first section, e.g. SOBJ =
    // the first script layer, SGEN = generated objects), each padded to 32, then the blocks. A compressed block is
    // padded at its front to 32 bytes and holds the same zlib/LZO segments as Echoes. The script layers use Echoes'
    // layout (Mrea.ReadLayer), so objects come back as Mrea.ScriptObject
    public sealed class Mrea3
    {
        public const uint Version = 0x1E;

        public float[] Transform = new float[12];
        public int MeshCount, LayerCount;
        public byte[][] Sections = Array.Empty<byte[]>();
        public Dictionary<string, int> SectionNumbers = new();

        public static bool IsCorruptionArea(byte[] raw) => raw.Length >= 0x60 && Be.U32(raw, 0) == Mrea.Magic && Be.U32(raw, 4) == Version;

        public static Mrea3 Read(byte[] raw)
        {
            if (!IsCorruptionArea(raw))
            {
                throw new InvalidOperationException($"not a Corruption MREA ({Be.U32(raw, 0):x8} v{Be.U32(raw, 4):x})");
            }
            var a = new Mrea3();
            for (int i = 0; i < 12; i++) a.Transform[i] = Be.F32(raw, 8 + 4 * i);
            a.MeshCount = (int)Be.U32(raw, 0x38);
            a.LayerCount = (int)Be.U32(raw, 0x3C);
            int sectionCount = (int)Be.U32(raw, 0x40), blockCount = (int)Be.U32(raw, 0x44), numberCount = (int)Be.U32(raw, 0x48);
            int o = 0x60;
            var sizes = new int[sectionCount];
            for (int i = 0; i < sectionCount; i++) sizes[i] = (int)Be.U32(raw, o + 4 * i);
            o = Align32(o + 4 * sectionCount);
            int table = o;
            o = Align32(o + 16 * blockCount);
            for (int i = 0; i < numberCount; i++)
            {
                a.SectionNumbers[Encoding.ASCII.GetString(raw, o + 8 * i, 4)] = (int)Be.U32(raw, o + 8 * i + 4);
            }
            o = Align32(o + 8 * numberCount);
            var data = new List<byte>();
            for (int i = 0; i < blockCount; i++)
            {
                int decompSize = (int)Be.U32(raw, table + 16 * i + 4), compSize = (int)Be.U32(raw, table + 16 * i + 8);
                if (compSize == 0)
                {
                    data.AddRange(Be.Slice(raw, o, decompSize));
                    o += decompSize;
                }
                else
                {
                    if (compSize % 32 != 0) o += 32 - compSize % 32;
                    data.AddRange(Pak.Segmented(Be.Slice(raw, o, compSize), 0, decompSize));
                    o += compSize;
                }
            }
            byte[] all = data.ToArray();
            a.Sections = new byte[sectionCount][];
            int p = 0;
            for (int i = 0; i < sectionCount; i++)
            {
                if (p + sizes[i] > all.Length) throw new InvalidOperationException($"section {i} runs past the data ({p + sizes[i]} > {all.Length})");
                a.Sections[i] = Be.Slice(all, p, sizes[i]);
                p += sizes[i];
            }
            return a;
        }

        static int Align32(int o) => (o + 31) & ~31;

        public int Section(string fourCC) => SectionNumbers.TryGetValue(fourCC, out int s) ? s : -1;

        // every script layer's objects (Layer = its index), plus the generated-object layer (Layer -1) when asked
        public List<Mrea.ScriptObject> ReadScripts(bool includeGenerated = false)
        {
            var res = new List<Mrea.ScriptObject>();
            int first = Section("SOBJ");
            if (first < 0) return res;
            for (int i = 0; i < LayerCount; i++)
            {
                Mrea.ReadLayer(Sections[first + i], res);
            }
            int gen = Section("SGEN");
            if (includeGenerated && gen >= 0 && Sections[gen].Length >= 10)
            {
                Mrea.ReadLayer(Sections[gen], res);
            }
            return res;
        }
    }
}
