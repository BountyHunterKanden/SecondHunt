using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MphRecomp.Import
{
    // Big-endian reads over a byte array, as Python's struct.unpack_from(">...") does them (a read past the end throws).
    // Shared by every disc and Retro reader under Import/.
    public static class Be
    {
        public static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
        public static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o, 4));
        public static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
        public static short I16(byte[] b, int o) => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o, 2));
        public static float F32(byte[] b, int o) => BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o, 4));

        // the index of the first `needle` at or after `start` (Python's bytes.index), -1 when there is none
        public static int IndexOf(byte[] b, ReadOnlySpan<byte> needle, int start)
        {
            if (start < 0 || start > b.Length) return -1;
            int i = b.AsSpan(start).IndexOf(needle);
            return i < 0 ? -1 : start + i;
        }

        // the same, throwing like Python's bytes.index when absent
        public static int Index(byte[] b, ReadOnlySpan<byte> needle, int start)
        {
            int i = IndexOf(b, needle, start);
            if (i < 0) throw new InvalidDataException("subsection not found");
            return i;
        }

        // a NUL-terminated Latin-1 string at o; `end` = the index of its terminator
        public static string CString(byte[] b, int o, out int end)
        {
            end = Index(b, stackalloc byte[] { 0 }, o);
            return Encoding.Latin1.GetString(b, o, end - o);
        }

        public static byte[] Slice(byte[] b, int o, int n)
        {
            // Python slices clamp at the end of the data
            int m = Math.Max(0, Math.Min(n, b.Length - o));
            var r = new byte[m];
            if (m > 0) Buffer.BlockCopy(b, o, r, 0, m);
            return r;
        }
    }
}
