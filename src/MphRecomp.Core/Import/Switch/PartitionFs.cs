using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MphRecomp.Import.Disc;

namespace MphRecomp.Import.Switch
{
    // One file of a PFS0 / HFS0 container: Offset is absolute in the container's blob.
    public readonly record struct PartitionFile(string Name, long Offset, long Size);

    // The flat file containers of Switch packages (switchbrew "NCA" PartitionFS, "XCI" HFS0), little-endian:
    //   PFS0 (an .nsp, or an NCA's ExeFS):  "PFS0", u32 file count, u32 string table size, u32 reserved;
    //                                        entries of 0x18 (u64 offset, u64 size, u32 name offset, u32 reserved)
    //   HFS0 (a game card's partitions):     "HFS0", the same header; entries of 0x40 (u64 offset, u64 size,
    //                                        u32 name offset, u32 hashed region size, u64 reserved, SHA-256 of the
    //                                        file's first hashed-region bytes)
    // then the string table (zero-terminated names) and the file data; file offsets count from the data's start.
    public static class PartitionFs
    {
        public const uint Pfs0Magic = 0x30534650, Hfs0Magic = 0x30534648;   // "PFS0", "HFS0" read little-endian

        public static bool IsPartitionFs(IDiscBlob blob, long offset)
        {
            if (offset + 0x10 > blob.Size) return false;
            Span<byte> m = stackalloc byte[4];
            blob.Read(offset, m);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(m);
            return magic == Pfs0Magic || magic == Hfs0Magic;
        }

        public static List<PartitionFile> Read(IDiscBlob blob, long offset)
        {
            var head = new byte[0x10];
            blob.Read(offset, head);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            int entrySize = magic == Pfs0Magic ? 0x18 : magic == Hfs0Magic ? 0x40 : throw new InvalidDataException("not a PFS0/HFS0 partition");
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            int strings = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            if (count < 0 || count > 0x10000 || strings < 0 || strings > 0x100000) throw new InvalidDataException("PFS0/HFS0: implausible header");
            var table = new byte[count * entrySize + strings];
            blob.Read(offset + 0x10, table);
            long data = offset + 0x10 + table.Length;
            var files = new List<PartitionFile>(count);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = table.AsSpan(i * entrySize, entrySize);
                long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(e);
                long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
                int name = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                int at = count * entrySize + name;
                if (name < 0 || at >= table.Length) throw new InvalidDataException("PFS0/HFS0: name outside the string table");
                int end = Array.IndexOf(table, (byte)0, at);
                string n = Encoding.UTF8.GetString(table, at, (end < 0 ? table.Length : end) - at);
                if (off < 0 || size < 0 || data + off + size > blob.Size) throw new InvalidDataException($"PFS0/HFS0: {n} lies outside the file (truncated dump?)");
                files.Add(new PartitionFile(n, data + off, size));
            }
            return files;
        }
    }

    // A window [start, start + size) of a blob as a blob of its own.
    public sealed class SubBlob : IDiscBlob
    {
        readonly IDiscBlob _blob;
        readonly long _start;

        public SubBlob(IDiscBlob blob, long start, long size)
        {
            _blob = blob; _start = start; Size = size;
        }

        public long Size { get; }

        public void Read(long offset, Span<byte> dst)
        {
            int n = offset >= Size ? 0 : (int)Math.Min(dst.Length, Size - offset);
            if (n > 0) _blob.Read(_start + offset, dst[..n]);
            dst[n..].Clear();
        }
    }
}
