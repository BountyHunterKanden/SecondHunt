using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Brawl
{
    // One entry of a Brawl effect archive. Number = its position in the archive (0-based); Kind / Index / Group /
    // Redirect as the entry header stores them; Offset = where Data starts in the archive.
    public sealed record BrawlArchiveEntry(int Number, int Kind, int Index, int Group, int Redirect, int Offset, ReadOnlyMemory<byte> Data)
    {
        public int Length => Data.Length;

        // the first four bytes of the data: "EFLS", "REFF", "REFT" or "bres" ("" when shorter)
        public string Magic => BrawlBytes.FourCC(Data.Span);
    }

    // A Brawl effect archive (.pac, "ARC\0"): a 0x40-byte header, then entries, each a 0x20-byte header and its data,
    // the next header at the data's end rounded up to 0x20. Clean-room from docs/cleanroom/E_brawl_effects_spec.md
    // (section 1).
    public sealed class BrawlArchive
    {
        public const int KindMisc = 1, KindModels = 2, KindTextures = 3;

        public string Name { get; }
        public int Version { get; }
        public IReadOnlyList<BrawlArchiveEntry> Entries { get; }

        BrawlArchive(string name, int version, IReadOnlyList<BrawlArchiveEntry> entries)
        {
            Name = name;
            Version = version;
            Entries = entries;
        }

        public static BrawlArchive Read(byte[] pac) => Read(new ReadOnlyMemory<byte>(pac));

        public static BrawlArchive Read(ReadOnlyMemory<byte> pac)
        {
            var d = pac.Span;
            const string hp = "archive header";
            BrawlBytes.Need(d, 0, 0x40, hp);
            if (d[0] != 'A' || d[1] != 'R' || d[2] != 'C' || d[3] != 0)
            {
                throw new InvalidDataException($"{hp}: not an \"ARC\" archive");
            }
            int version = BrawlBytes.U16(d, 4, hp);
            int count = BrawlBytes.U16(d, 6, hp);
            string name = BrawlBytes.Ascii(d, 0x10, 48, hp);

            var entries = new List<BrawlArchiveEntry>(count);
            long pos = 0x40;
            for (int n = 0; n < count; n++)
            {
                string ep = $"archive entry {n} header";
                BrawlBytes.Need(d, pos, 0x20, ep);
                int kind = BrawlBytes.U16(d, pos, ep);
                int index = BrawlBytes.U16(d, pos + 2, ep);
                uint size = BrawlBytes.U32(d, pos + 4, ep);
                int group = BrawlBytes.U8(d, pos + 8, ep);
                int redirect = BrawlBytes.I16(d, pos + 0x0A, ep);
                long start = pos + 0x20;
                BrawlBytes.Need(d, start, size, $"archive entry {n} data");
                entries.Add(new BrawlArchiveEntry(n, kind, index, group, redirect, (int)start, pac.Slice((int)start, (int)size)));
                pos = BrawlBytes.RoundUp(start + size, 0x20);
            }
            return new BrawlArchive(name, version, entries);
        }

        // the bytes an entry stands for: its own, or, when it redirects, those of the entry it names (followed until an
        // entry that doesn't redirect)
        public ReadOnlyMemory<byte> DataOf(BrawlArchiveEntry e)
        {
            var cur = e;
            for (int hops = 0; cur.Redirect >= 0; hops++)
            {
                if (cur.Redirect >= Entries.Count)
                {
                    throw new InvalidDataException($"archive entry {cur.Number}: redirect to entry {cur.Redirect}, past the last ({Entries.Count - 1})");
                }
                if (hops >= Entries.Count)
                {
                    throw new InvalidDataException($"archive entry {e.Number}: redirects loop");
                }
                cur = Entries[cur.Redirect];
            }
            return cur.Data;
        }
    }
}
