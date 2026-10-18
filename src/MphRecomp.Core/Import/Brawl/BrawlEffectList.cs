using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Brawl
{
    // One effect list entry (spec 14): ModelFile is the index among the archive's model resource files (kind 2) this
    // effect draws, or -1 for none; ModelFileRepeat the second copy of that number as stored (0 with none); Name the
    // effect's name in the effect file, or null for an entry with no name; NameOffset its position counted from the
    // start of the list (0 = no name); Reserved08 the zero word; Extra the byte at +0x0C; Reserved0D the three zero bytes.
    public sealed record BrawlEffectListEntry(int ModelFile, int ModelFileRepeat, uint NameOffset, string? Name,
        uint Reserved08, byte Extra, byte[] Reserved0D);

    // An effect list (an archive entry's data starting "EFLS"). Clean-room from docs/cleanroom/E_brawl_effects_spec.md
    // (section 14).
    public sealed class BrawlEffectList
    {
        public int EntryCount { get; }
        // number of model resource files in the archive (+0x06)
        public int ModelFileCount { get; }
        public byte[] Reserved08 { get; }
        public IReadOnlyList<BrawlEffectListEntry> Entries { get; }

        BrawlEffectList(int entryCount, int modelFileCount, byte[] reserved, List<BrawlEffectListEntry> entries)
        {
            EntryCount = entryCount;
            ModelFileCount = modelFileCount;
            Reserved08 = reserved;
            Entries = entries;
        }

        // the first entry naming this effect, or null
        public BrawlEffectListEntry? Find(string effectName)
        {
            foreach (var e in Entries) if (e.Name == effectName) return e;
            return null;
        }

        public static BrawlEffectList Read(byte[] data) => Read(new ReadOnlyMemory<byte>(data));

        public static BrawlEffectList Read(ReadOnlyMemory<byte> data)
        {
            var d = data.Span;
            const string hp = "effect list header";
            BrawlBytes.Magic(d, 0, "EFLS", hp);
            int count = BrawlBytes.U16(d, 4, hp);
            int models = BrawlBytes.U16(d, 6, hp);
            BrawlBytes.Need(d, 8, 8, hp);
            var reserved = d.Slice(8, 8).ToArray();
            BrawlBytes.Need(d, 0x10, 16L * count, "effect list entries");
            var entries = new List<BrawlEffectListEntry>(count);
            for (int i = 0; i < count; i++)
            {
                string ep = $"effect list entry {i}";
                long at = 0x10 + 16L * i;
                int model = BrawlBytes.U16(d, at, ep);
                int repeat = BrawlBytes.U16(d, at + 2, ep);
                uint nameAt = BrawlBytes.U32(d, at + 4, ep);
                uint zero = BrawlBytes.U32(d, at + 8, ep);
                byte extra = BrawlBytes.U8(d, at + 0x0C, ep);
                var pad = d.Slice((int)at + 0x0D, 3).ToArray();
                string? name = nameAt == 0 ? null : CString(d, nameAt, ep + " name");
                entries.Add(new BrawlEffectListEntry(model == 0xFFFF ? -1 : model, repeat, nameAt, name, zero, extra, pad));
            }
            return new BrawlEffectList(count, models, reserved, entries);
        }

        // zero-terminated ASCII at o; the terminator must lie inside the data
        static string CString(ReadOnlySpan<byte> d, long o, string part)
        {
            BrawlBytes.Need(d, o, 1, part);
            var s = d.Slice((int)o);
            int z = s.IndexOf((byte)0);
            if (z < 0) throw new InvalidDataException($"{part}: no terminating zero after 0x{o:X}");
            return Encoding.ASCII.GetString(s.Slice(0, z));
        }
    }
}
