using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Brawl
{
    // A decoded Brawl texture: Format is the file's pixel format number (BrawlPixels), Rgba the base image as 8-bit RGBA
    // rows, the top row first.
    public sealed record BrawlTexture(string Name, int Width, int Height, int Format, byte[] Rgba);

    // A texture left out as unsupported: a colour-table format (BrawlPixels.C4 / C8 / C14X2; spec 4.3), its name and
    // format number.
    public sealed record BrawlSkippedTexture(string Name, int Format);

    // A particle texture file: its set name, every readable texture in directory order, and the textures skipped as
    // unsupported (colour-table formats), also in directory order.
    public sealed record BrawlParticleTextureFile(string SetName, IReadOnlyList<BrawlTexture> Textures)
    {
        public IReadOnlyList<BrawlSkippedTexture> Skipped { get; init; } = Array.Empty<BrawlSkippedTexture>();

        public BrawlParticleTextureFile(string setName, IReadOnlyList<BrawlTexture> textures,
            IReadOnlyList<BrawlSkippedTexture> skipped) : this(setName, textures)
        {
            Skipped = skipped;
        }
    }

    // The two Brawl texture containers: a resource file's TEX0 texture resources and the particle texture file (REFT).
    // A colour-table texture is skipped on its own and reported (spec 4.3); the rest of the file is still read.
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md (sections 2.4, 3 and 4.3).
    public static class BrawlTextures
    {
        public static bool UsesColourTable(int format) => format is BrawlPixels.C4 or BrawlPixels.C8 or BrawlPixels.C14X2;

        // the TEX0 resource starting at `position` in the resource file; a colour-table texture throws (use
        // ReadResources to skip those)
        public static BrawlTexture ReadResource(BrawlResFile file, int position)
        {
            var t = ReadResource(file, position, out var skipped);
            if (t != null) return t;
            throw new InvalidDataException(
                $"texture resource {skipped!.Name}: {BrawlPixels.FormatName(skipped.Format)} uses a colour table, not supported");
        }

        // the TEX0 resource starting at `position`; null with `skipped` set when it uses a colour table
        public static BrawlTexture? ReadResource(BrawlResFile file, int position, out BrawlSkippedTexture? skipped)
        {
            skipped = null;
            var d = file.Data.Span;
            string part = $"texture resource at 0x{position:X}";
            BrawlBytes.Magic(d, position, "TEX0", part);
            BrawlBytes.Need(d, position, 0x40, part + " header");
            uint size = BrawlBytes.U32(d, position + 4L, part);
            BrawlBytes.Need(d, position, size, part);
            var r = d.Slice(position, (int)size);
            uint pixelAt = BrawlBytes.U32(r, 0x10, part);
            uint nameAt = BrawlBytes.U32(r, 0x14, part);
            uint flags = BrawlBytes.U32(r, 0x18, part);
            int width = BrawlBytes.U16(r, 0x1C, part);
            int height = BrawlBytes.U16(r, 0x1E, part);
            uint format = BrawlBytes.U32(r, 0x20, part);
            string name = BrawlResFile.Name(d, position + (long)nameAt, part);
            part = $"texture resource {name}";
            if (format > int.MaxValue) throw new InvalidDataException($"{part}: unknown pixel format {format}");
            if ((flags & 1) != 0 || UsesColourTable((int)format))
            {
                skipped = new BrawlSkippedTexture(name, (int)format);
                return null;
            }
            BrawlBytes.Need(r, pixelAt, 0, part + " pixels");
            var rgba = BrawlPixels.Decode(r.Slice((int)pixelAt), (int)format, width, height, part);
            return new BrawlTexture(name, width, height, (int)format, rgba);
        }

        // every readable texture resource in the file's "Textures(NW4R)" folder, in record order (colour-table ones
        // left out; the overload below reports them)
        public static List<BrawlTexture> ReadResources(BrawlResFile file) => ReadResources(file, out _);

        // every readable texture resource in record order, and the colour-table ones skipped (name, format)
        public static List<BrawlTexture> ReadResources(BrawlResFile file, out List<BrawlSkippedTexture> skipped)
        {
            var r = new List<BrawlTexture>();
            skipped = new List<BrawlSkippedTexture>();
            foreach (var item in file.Folder(BrawlResFile.Textures))
            {
                var t = ReadResource(file, item.Position, out var skip);
                if (t != null) r.Add(t);
                else skipped.Add(skip!);
            }
            return r;
        }

        // every texture in a particle texture file (an archive entry's data starting "REFT")
        public static BrawlParticleTextureFile ReadParticleTextures(ReadOnlyMemory<byte> data)
        {
            var all = data.Span;
            const string hp = "particle texture file header";
            BrawlBytes.Magic(all, 0, "REFT", hp);
            int bom = BrawlBytes.U16(all, 4, hp);
            if (bom != 0xFEFF)
            {
                throw new InvalidDataException($"{hp}: byte-order mark 0x{bom:X4}, only big-endian (0xFEFF) is read");
            }
            uint fileSize = BrawlBytes.U32(all, 8, hp);
            BrawlBytes.Need(all, 0, fileSize, hp);
            var d = all.Slice(0, (int)fileSize);
            int blockAt = BrawlBytes.U16(d, 0x0C, hp);

            const string bp = "particle texture block";
            BrawlBytes.Magic(d, blockAt, "REFT", bp);
            uint dirPos = BrawlBytes.U32(d, blockAt + 8L, bp);
            int setLen = BrawlBytes.U16(d, blockAt + 0x14L, bp);
            string setName = BrawlBytes.Ascii(d, blockAt + 0x18L, setLen, bp + " set name");

            const string dp = "particle texture directory";
            long dir = blockAt + 8L + dirPos;
            int count = BrawlBytes.U16(d, dir + 4, dp);
            var textures = new List<BrawlTexture>(count);
            var skipped = new List<BrawlSkippedTexture>();
            long at = dir + 8;
            for (int i = 0; i < count; i++)
            {
                string ip = $"{dp} item {i}";
                int nameLen = BrawlBytes.U16(d, at, ip);
                string name = BrawlBytes.Ascii(d, at + 2, nameLen, ip + " name");
                at += 2 + nameLen;
                uint texPos = BrawlBytes.U32(d, at, ip);
                uint texSize = BrawlBytes.U32(d, at + 4, ip);
                at += 8;
                var t = ParticleTexture(d, dir + texPos, texSize, name, out var skip);
                if (t != null) textures.Add(t);
                else skipped.Add(skip!);
            }
            return new BrawlParticleTextureFile(setName, textures, skipped);
        }

        public static BrawlParticleTextureFile ReadParticleTextures(byte[] data) => ReadParticleTextures(new ReadOnlyMemory<byte>(data));

        // a 0x20-byte texture header at `at`, the pixels right after it (`size` bytes, the directory's data size; the
        // directory, not the header's pixel data size, says where the texture is). A colour-table texture returns null
        // with `skipped` set: its item also holds the table, so its size is larger than the header's pixel data size.
        static BrawlTexture? ParticleTexture(ReadOnlySpan<byte> d, long at, uint size, string name,
            out BrawlSkippedTexture? skipped)
        {
            skipped = null;
            string part = $"particle texture {name}";
            BrawlBytes.Need(d, at, 0x20L + size, part);
            int width = BrawlBytes.U16(d, at + 4, part);
            int height = BrawlBytes.U16(d, at + 6, part);
            int format = BrawlBytes.U8(d, at + 0x0C, part);
            if (UsesColourTable(format))
            {
                skipped = new BrawlSkippedTexture(name, format);
                return null;
            }
            var rgba = BrawlPixels.Decode(d.Slice((int)(at + 0x20), (int)size), format, width, height, part);
            return new BrawlTexture(name, width, height, format, rgba);
        }
    }
}
