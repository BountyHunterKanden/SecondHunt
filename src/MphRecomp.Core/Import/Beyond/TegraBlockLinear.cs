using System;
using System.IO;

namespace MphRecomp.Import.Beyond
{
    // The Switch GPU's block-linear surface arrangement (spec 6.3), for one mip level of a 2D texture given in format
    // blocks: rows of R = W x bytesPerBlock bytes are cut into GROUPS of 64 bytes x 8 rows (512 bytes), G = ceil(R / 64)
    // groups across, stacked K groups high (K from the level's height in blocks). Byte x of block row y comes from
    //   (floor(y / 8K) x G + floor(x / 64)) x 512K + floor((y mod 8K) / 8) x 512 + I(x mod 64, y mod 8)
    //   I(a, b) = floor(a/32) x 256 + floor(b/2) x 64 + floor((a mod 32)/16) x 32 + (b mod 2) x 16 + (a mod 16)
    public static class TegraBlockLinear
    {
        public const int GroupBytes = 512;

        // K, the block height in groups, from the level's height in blocks
        public static int BlockHeightGroups(int blocksHigh)
        {
            int t = blocksHigh + blocksHigh / 2;
            return t >= 128 ? 16 : t >= 64 ? 8 : t >= 32 ? 4 : t >= 16 ? 2 : 1;
        }

        // the bytes one level occupies: whole K-group-high bands of G groups
        public static long LevelSize(int blocksWide, int blocksHigh, int bytesPerBlock)
        {
            int k = BlockHeightGroups(blocksHigh);
            long g = ((long)blocksWide * bytesPerBlock + 63) / 64;
            long bands = (blocksHigh + 8L * k - 1) / (8L * k);
            return bands * g * GroupBytes * k;
        }

        // position inside a group
        static int InGroup(int a, int b) => (a >> 5) * 256 + (b >> 1) * 64 + ((a & 31) >> 4) * 32 + (b & 1) * 16 + (a & 15);

        // the level at surface[start..] as linear rows of blocks (H rows of W x bytesPerBlock bytes)
        public static byte[] Linearize(ReadOnlySpan<byte> surface, int start, int blocksWide, int blocksHigh, int bytesPerBlock, string what)
        {
            int r = checked(blocksWide * bytesPerBlock);
            int k = BlockHeightGroups(blocksHigh);
            int g = (r + 63) / 64;
            int bandRows = 8 * k;
            var linear = new byte[checked((long)r * blocksHigh)];
            for (int y = 0; y < blocksHigh; y++)
            {
                long rowBase = start + (long)(y / bandRows) * g * GroupBytes * k + ((y % bandRows) >> 3) * GroupBytes;
                int b = y & 7;
                for (int x = 0; x < r; x += 16)   // 16-byte runs are contiguous in both arrangements
                {
                    long src = rowBase + (long)(x >> 6) * GroupBytes * k + InGroup(x & 63, b);
                    int n = Math.Min(16, r - x);
                    if (src + n > surface.Length)
                    {
                        throw new InvalidDataException($"{what}: block row {y} byte {x} maps to surface offset {src}, past its {surface.Length} bytes");
                    }
                    surface.Slice((int)src, n).CopyTo(linear.AsSpan(y * r + x, n));
                }
            }
            return linear;
        }
    }
}
