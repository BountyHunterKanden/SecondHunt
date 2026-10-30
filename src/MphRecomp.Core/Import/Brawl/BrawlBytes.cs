using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Brawl
{
    // Bounds-checked big-endian reads shared by the Brawl effect readers. Positions are longs so a u32 position added to
    // a base never wraps; a read past the end throws InvalidDataException naming the file part, never an index error.
    // Clean-room from docs/cleanroom/E_brawl_effects_spec.md.
    internal static class BrawlBytes
    {
        public static void Need(ReadOnlySpan<byte> d, long pos, long count, string part)
        {
            if (pos < 0 || count < 0 || pos + count > d.Length)
            {
                throw new InvalidDataException($"{part}: 0x{count:X} bytes at 0x{pos:X} run past the end (0x{d.Length:X} bytes)");
            }
        }

        public static byte U8(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 1, part);
            return d[(int)o];
        }

        public static ushort U16(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 2, part);
            return BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)o, 2));
        }

        public static short I16(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 2, part);
            return BinaryPrimitives.ReadInt16BigEndian(d.Slice((int)o, 2));
        }

        public static uint U32(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 4, part);
            return BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)o, 4));
        }

        public static int I32(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 4, part);
            return BinaryPrimitives.ReadInt32BigEndian(d.Slice((int)o, 4));
        }

        public static float F32(ReadOnlySpan<byte> d, long o, string part)
        {
            Need(d, o, 4, part);
            return BinaryPrimitives.ReadSingleBigEndian(d.Slice((int)o, 4));
        }

        // the four ASCII letters at o must be `magic`
        public static void Magic(ReadOnlySpan<byte> d, long o, string magic, string part)
        {
            Need(d, o, magic.Length, part);
            for (int i = 0; i < magic.Length; i++)
            {
                if (d[(int)o + i] != magic[i])
                {
                    throw new InvalidDataException($"{part}: expected \"{magic}\" at 0x{o:X}, found \"{Letters(d.Slice((int)o, magic.Length))}\"");
                }
            }
        }

        // the first four bytes as text (what an archive entry's data is), "" when shorter
        public static string FourCC(ReadOnlySpan<byte> d) => d.Length < 4 ? "" : Letters(d.Slice(0, 4));

        // n bytes at o as ASCII, cut at the first zero byte
        public static string Ascii(ReadOnlySpan<byte> d, long o, long n, string part)
        {
            Need(d, o, n, part);
            var s = d.Slice((int)o, (int)n);
            int z = s.IndexOf((byte)0);
            return Encoding.ASCII.GetString(z < 0 ? s : s.Slice(0, z));
        }

        public static long RoundUp(long v, long step) => (v + step - 1) / step * step;

        static string Letters(ReadOnlySpan<byte> s)
        {
            var c = new char[s.Length];
            for (int i = 0; i < s.Length; i++) c[i] = s[i] is >= 0x20 and < 0x7F ? (char)s[i] : '?';
            return new string(c);
        }
    }
}
