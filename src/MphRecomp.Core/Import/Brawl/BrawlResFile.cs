using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Brawl
{
    // One resource in a subfolder: its name and where its data starts, counted from the start of the resource file.
    public readonly record struct BrawlResItem(string Name, int Position);

    // A subfolder of the top folder ("Textures(NW4R)", "3DModels(NW4R)" ...) and its resources in record order.
    public sealed record BrawlResFolder(string Name, IReadOnlyList<BrawlResItem> Items);

    // A Brawl resource file ("bres"): header, the root block, its top folder of subfolders, each a folder of resources.
    // Folder records' name and data positions count from the folder; names carry a u32 length just before them.
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md (sections 2.1-2.3).
    public sealed class BrawlResFile
    {
        public const string Textures = "Textures(NW4R)", Models = "3DModels(NW4R)", MeshAnimations = "AnmChr(NW4R)";

        // the resource file's bytes, cut to the size its header states
        public ReadOnlyMemory<byte> Data { get; }
        public int BlockCount { get; }
        public IReadOnlyList<BrawlResFolder> Folders { get; }

        BrawlResFile(ReadOnlyMemory<byte> data, int blockCount, IReadOnlyList<BrawlResFolder> folders)
        {
            Data = data;
            BlockCount = blockCount;
            Folders = folders;
        }

        // the resources of the subfolder called `name` (the first, should two share it); empty when there is none
        public IReadOnlyList<BrawlResItem> Folder(string name)
        {
            foreach (var f in Folders)
            {
                if (f.Name == name) return f.Items;
            }
            return Array.Empty<BrawlResItem>();
        }

        public static BrawlResFile Read(byte[] data) => Read(new ReadOnlyMemory<byte>(data));

        public static BrawlResFile Read(ReadOnlyMemory<byte> data)
        {
            const string hp = "resource file header";
            var all = data.Span;
            BrawlBytes.Magic(all, 0, "bres", hp);
            int bom = BrawlBytes.U16(all, 4, hp);
            if (bom != 0xFEFF)
            {
                throw new InvalidDataException($"{hp}: byte-order mark 0x{bom:X4}, only big-endian (0xFEFF) is read");
            }
            uint size = BrawlBytes.U32(all, 8, hp);
            BrawlBytes.Need(all, 0, size, hp);
            data = data.Slice(0, (int)size);
            var d = data.Span;
            int rootAt = BrawlBytes.U16(d, 0x0C, hp);
            int blocks = BrawlBytes.U16(d, 0x0E, hp);

            BrawlBytes.Magic(d, rootAt, "root", "resource file root block");
            var folders = new List<BrawlResFolder>();
            foreach (var (name, at) in Records(d, rootAt + 8L, "resource file top folder"))
            {
                var items = new List<BrawlResItem>();
                foreach (var (iname, iat) in Records(d, at, $"resource folder {name}"))
                {
                    items.Add(new BrawlResItem(iname, (int)iat));
                }
                folders.Add(new BrawlResFolder(name, items));
            }
            return new BrawlResFile(data, blocks, folders);
        }

        // a name at `at` (resource-file position), its u32 length in the four bytes before it
        internal static string Name(ReadOnlySpan<byte> d, long at, string part)
        {
            uint len = BrawlBytes.U32(d, at - 4, part + " name length");
            return BrawlBytes.Ascii(d, at, len, part + " name");
        }

        // records 1..N of the folder at `at`: (name, data position from the resource file's start)
        static List<(string Name, long At)> Records(ReadOnlySpan<byte> d, long at, string part)
        {
            uint n = BrawlBytes.U32(d, at + 4, part);
            BrawlBytes.Need(d, at + 8, 16L * (n + 1L), part + " records");
            var r = new List<(string, long)>((int)n);
            for (long i = 1; i <= n; i++)
            {
                long rec = at + 8 + 16 * i;
                string rp = $"{part} record {i}";
                int namePos = BrawlBytes.I32(d, rec + 8, rp);
                int dataPos = BrawlBytes.I32(d, rec + 12, rp);
                string name = Name(d, at + namePos, rp);
                long dataAt = at + dataPos;
                if (dataAt < 0 || dataAt >= d.Length)
                {
                    throw new InvalidDataException($"{rp}: data position 0x{dataAt:X} outside the file (0x{d.Length:X} bytes)");
                }
                r.Add((name, dataAt));
            }
            return r;
        }
    }
}
