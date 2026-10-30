using System;

namespace MphRead.Formats.Vx
{
    // One picture: a luma plane and two half-size chroma planes, row 0 at the top, rows stored one after another.
    internal sealed class VxPicture
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int ChromaWidth;
        public readonly int ChromaHeight;
        public readonly byte[] Y;
        public readonly byte[] U;
        public readonly byte[] V;

        // clamp8 for the colour conversion: entry v + ClampOffset holds v limited to 0..255
        private const int ClampOffset = 512;
        private static readonly byte[] _clamp = BuildClamp();

        public VxPicture(int width, int height)
        {
            Width = width;
            Height = height;
            ChromaWidth = width / 2;
            ChromaHeight = height / 2;
            Y = new byte[Width * Height];
            U = new byte[ChromaWidth * ChromaHeight];
            V = new byte[ChromaWidth * ChromaHeight];
        }

        private static byte[] BuildClamp()
        {
            var table = new byte[ClampOffset * 3];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (byte)Math.Clamp(i - ClampOffset, 0, 255);
            }
            return table;
        }

        // Converts frameWidth x frameHeight pixels to RGB24; pixel (x, y) goes to byte (y * stride + x) * 3.
        public void ToRgb(byte[] destination, int frameWidth, int frameHeight, int stride)
        {
            byte[] clamp = _clamp;
            for (int y = 0; y < frameHeight; y++)
            {
                int lumaRow = y * Width;
                int chromaRow = (y / 2) * ChromaWidth;
                int dest = y * stride * 3;
                for (int x = 0; x < frameWidth; x++)
                {
                    int luma = Y[lumaRow + x] + ClampOffset;
                    int u = U[chromaRow + x / 2] - 128;
                    int v = V[chromaRow + x / 2] - 128;
                    destination[dest] = clamp[luma + 2 * v];
                    destination[dest + 1] = clamp[luma - u / 2 - v];
                    destination[dest + 2] = clamp[luma + 2 * u];
                    dest += 3;
                }
            }
        }
    }
}
