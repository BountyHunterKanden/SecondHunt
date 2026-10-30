using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Beyond
{
    // Metroid Prime 4: Beyond's container shape, shared by the archive and every asset: a FORM (32-byte header: "RFRM",
    // s64 length of what follows, u64, the 4-byte kind, u32 version A, u32 version B) followed by CHUNKS (24-byte
    // header: 4-byte kind, s64 size S, u32, s64 data skip K; data at start + 24 + K, the next chunk at start + 24 + S).
    // Clean-room: from docs/cleanroom/G_beyond_spec.md sections 1 and 2.
    public readonly struct BeyondChunk
    {
        public readonly string Tag;
        public readonly int Start;       // of the chunk's 24-byte header
        public readonly int DataStart;   // start + 24 + K
        public readonly int End;         // start + 24 + S (the next chunk's start)

        public BeyondChunk(string tag, int start, int dataStart, int end)
        {
            Tag = tag; Start = start; DataStart = dataStart; End = end;
        }

        public int DataLength => End - DataStart;
    }

    public static class BeyondForm
    {
        public const int FormHeaderSize = 32;
        public const int ChunkHeaderSize = 24;

        public static string Tag(ReadOnlySpan<byte> b, int o) => Encoding.ASCII.GetString(b.Slice(o, 4));

        // a GUID: u32, u16, u16, then 8 bytes in order -- System.Guid's own byte layout
        public static Guid ReadGuid(ReadOnlySpan<byte> b, int o) => new Guid(b.Slice(o, 16));

        // the form's kind, after checking the "RFRM" tag
        public static string Kind(byte[] asset, string what)
        {
            if (asset.Length < FormHeaderSize || Tag(asset, 0) != "RFRM")
            {
                throw new InvalidDataException($"{what}: not a form (no RFRM header)");
            }
            return Tag(asset, 0x14);
        }

        public static (uint A, uint B) Versions(byte[] asset) =>
            (BinaryPrimitives.ReadUInt32LittleEndian(asset.AsSpan(0x18)), BinaryPrimitives.ReadUInt32LittleEndian(asset.AsSpan(0x1c)));

        // the form's kind, which must be the expected one
        public static void Expect(byte[] asset, string kind, string what)
        {
            string k = Kind(asset, what);
            if (k != kind) throw new InvalidDataException($"{what}: a {k} form, not {kind}");
        }

        // the asset's chunks, from +32 to the end of the form (its length field, or the buffer if shorter)
        public static List<BeyondChunk> Chunks(byte[] asset, string what)
        {
            Kind(asset, what);
            long formEnd = FormHeaderSize + BinaryPrimitives.ReadInt64LittleEndian(asset.AsSpan(4));
            if (formEnd < FormHeaderSize || formEnd > asset.Length) formEnd = asset.Length;
            return ChunksIn(asset, FormHeaderSize, (int)formEnd, what);
        }

        // the chunks in [from, to); a chunk that would run past `to` is an error
        public static List<BeyondChunk> ChunksIn(ReadOnlySpan<byte> b, int from, int to, string what)
        {
            var list = new List<BeyondChunk>();
            int o = from;
            while (o + ChunkHeaderSize <= to)
            {
                string tag = Tag(b, o);
                long size = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o + 4));
                long skip = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o + 0x10));
                long end = o + ChunkHeaderSize + size;
                long data = o + ChunkHeaderSize + skip;
                if (size < 0 || skip < 0 || end > to || data > end)
                {
                    throw new InvalidDataException($"{what}: chunk {tag} at 0x{o:x} (size {size}, skip {skip}) runs past the end");
                }
                list.Add(new BeyondChunk(tag, o, (int)data, (int)end));
                o = (int)end;
            }
            return list;
        }

        // the first chunk of that kind
        public static BeyondChunk Find(List<BeyondChunk> chunks, string tag, string what)
        {
            foreach (BeyondChunk c in chunks)
            {
                if (c.Tag == tag) return c;
            }
            throw new InvalidDataException($"{what}: no {tag} chunk");
        }
    }

    // A bounds-checked little-endian reader over one byte range; every read past its end throws, naming the asset.
    internal sealed class BeyondCursor
    {
        readonly byte[] b;
        readonly int end;
        readonly string what;
        public int Pos;

        public BeyondCursor(byte[] data, int start, int end, string what)
        {
            b = data; Pos = start; this.end = end; this.what = what;
        }

        public int End => end;
        public int Left => end - Pos;

        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + (long)n > end) throw new InvalidDataException($"{what}: read of {n} bytes at 0x{Pos:x} runs past 0x{end:x}");
            var s = new ReadOnlySpan<byte>(b, Pos, n);
            Pos += n;
            return s;
        }

        public void Skip(long n)
        {
            if (n < 0 || Pos + n > end) throw new InvalidDataException($"{what}: skip of {n} bytes at 0x{Pos:x} runs past 0x{end:x}");
            Pos += (int)n;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public long S64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public string Tag() => Encoding.ASCII.GetString(Take(4));
        public Guid Guid() => new Guid(Take(16));

        // a count, checked against the bytes left (each item at least itemSize bytes)
        public int Count(int itemSize, string name)
        {
            uint n = U32();
            if (itemSize > 0 && (long)n * itemSize > Left) throw new InvalidDataException($"{what}: {name} count {n} does not fit in the {Left} bytes left");
            if (n > int.MaxValue) throw new InvalidDataException($"{what}: {name} count {n} is too large");
            return (int)n;
        }
    }
}
