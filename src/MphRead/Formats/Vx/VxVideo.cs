using System;

namespace MphRead.Formats.Vx
{
    // Decodes the video part of a frame payload into a picture. Positions are luma pixel coordinates throughout, also
    // on the chroma planes, where the sample "at (x, y)" is the one at row y / 2, column x / 2 (C# division, so -1 maps
    // to 0). Any access outside a plane is an error.
    internal sealed partial class VxVideo
    {
        private readonly VxBitReader _bits;

        // frame geometry from the header
        private int _frameWidth;
        private int _frameHeight;
        private int _mbColumns;
        private int _mbRows;

        // the picture being decoded and its planes
        private VxPicture _current = null!;
        private byte[] _y = null!;
        private byte[] _u = null!;
        private byte[] _v = null!;
        private int _lumaStride;
        private int _lumaRows;
        private int _chromaStride;
        private int _chromaRows;

        // references: R0 newest
        private VxPicture? _ref0;
        private VxPicture? _ref1;
        private VxPicture? _ref2;

        // count maps: one cell per 4 x 4 (luma) and 8 x 8 (chroma) luma area, with spare columns and rows on the right
        // and bottom for areas that reach past the frame edge
        private int[] _lumaCounts = Array.Empty<int>();
        private int _lumaCountStride;
        private int[] _chromaCounts = Array.Empty<int>();
        private int _chromaCountStride;

        // vector map with a zero border: macroblock (mx, my) is at (my + 1) * stride + mx + 1
        private int[] _vectorX = Array.Empty<int>();
        private int[] _vectorY = Array.Empty<int>();
        private int _vectorStride;

        // dequantisation weight per raster position (row * 4 + col)
        private readonly int[] _weights = new int[16];
        // work arrays
        private readonly int[] _coefficients = new int[16];
        private readonly int[] _subModes = new int[16];
        private readonly int[] _top = new int[9];   // p[-1, -1], then p[0..7, -1]
        private readonly int[] _left = new int[5];  // p[-1, -1], then p[-1, 0..3]
        private readonly int[] _predicted = new int[16];
        private readonly int[] _sourceColumns = new int[8];
        private readonly int[] _sourceRows = new int[8];

        // clamp8 by table: entry v + ClampOffset holds v limited to 0..255, for v in -ClampOffset .. ClampOffset * 2 - 1
        private const int ClampOffset = 1024;
        private static readonly byte[] _clamp = BuildClamp();

        private static readonly int[] _codedFlags =
        {
            0, 8, 4, 2, 1, 31, 15, 10, 5, 12, 3, 16, 14, 13, 11, 7,
            9, 6, 30, 27, 26, 29, 23, 21, 24, 18, 17, 28, 20, 19, 22, 25
        };

        // scan position -> raster position (row * 4 + col)
        private static readonly int[] _zigZag =
        {
            0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15
        };

        private static readonly int[,] _normalisation =
        {
            { 10, 16, 13 },
            { 11, 18, 14 },
            { 13, 20, 16 },
            { 14, 23, 18 },
            { 16, 25, 20 },
            { 18, 29, 23 }
        };

        // the level suffix length grows when a magnitude is above the limit for the current length
        private static readonly int[] _levelLimits = { 3, 6, 12, 24, 48 };

        public VxVideo(VxBitReader bits)
        {
            _bits = bits;
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

        private static byte Clamp8(int value)
        {
            if ((uint)(value + ClampOffset) < ClampOffset * 3)
            {
                return _clamp[value + ClampOffset];
            }
            return value < 0 ? (byte)0 : (byte)255;
        }

        private static Exception Error(string message)
        {
            return new ProgramException($"VX video: {message}");
        }

        // called when a header is read
        public void Start(int width, int height, int quantiser)
        {
            _frameWidth = width;
            _frameHeight = height;
            _mbColumns = width / 16;
            _mbRows = height / 16;
            _lumaCountStride = width / 4 + 2;
            _lumaCounts = new int[_lumaCountStride * (height / 4 + 2)];
            _chromaCountStride = width / 8 + 2;
            _chromaCounts = new int[_chromaCountStride * (height / 8 + 2)];
            _vectorStride = _mbColumns + 2;
            _vectorX = new int[_vectorStride * (_mbRows + 1)];
            _vectorY = new int[_vectorStride * (_mbRows + 1)];
            int shift = quantiser / 6;
            int m = quantiser % 6;
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    int kind = (row & 1) == 0 && (col & 1) == 0 ? 0 : (row & 1) == 1 && (col & 1) == 1 ? 1 : 2;
                    _weights[row * 4 + col] = _normalisation[m, kind] << shift;
                }
            }
            _ref0 = null;
            _ref1 = null;
            _ref2 = null;
        }

        // decodes the video part of the payload (the bit reader is at its start) into the picture
        public void DecodeFrame(VxPicture picture)
        {
            _current = picture;
            _y = picture.Y;
            _u = picture.U;
            _v = picture.V;
            _lumaStride = picture.Width;
            _lumaRows = picture.Height;
            _chromaStride = picture.ChromaWidth;
            _chromaRows = picture.ChromaHeight;
            if (_frameWidth > picture.Width || _frameHeight > picture.Height)
            {
                throw Error("frame is larger than the picture buffer.");
            }
            Array.Clear(_lumaCounts);
            Array.Clear(_chromaCounts);
            Array.Clear(_vectorX);
            Array.Clear(_vectorY);
            for (int my = 0; my < _mbRows; my++)
            {
                for (int mx = 0; mx < _mbColumns; mx++)
                {
                    int left = (my + 1) * _vectorStride + mx;
                    int above = my * _vectorStride + mx + 1;
                    int aboveRight = above + 1;
                    int px = Median(_vectorX[left], _vectorX[above], _vectorX[aboveRight]);
                    int py = Median(_vectorY[left], _vectorY[above], _vectorY[aboveRight]);
                    DecodeBlock(mx * 16, my * 16, 16, 16, px, py);
                }
            }
            _bits.Align();
            // the references shift once the frame is done
            _ref2 = _ref1;
            _ref1 = _ref0;
            _ref0 = picture;
        }

        private static int Median(int a, int b, int c)
        {
            return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        private void DecodeBlock(int x, int y, int w, int h, int px, int py)
        {
            int code = _bits.ReadUe();
            bool residual;
            switch (code)
            {
            case 0:
            case 8:
                if (w == 2)
                {
                    throw Error("vertical split of a block 2 wide.");
                }
                DecodeBlock(x, y, w / 2, h, px, py);
                DecodeBlock(x + w / 2, y, w / 2, h, px, py);
                residual = code == 8;
                break;
            case 2:
            case 13:
                if (h == 2)
                {
                    throw Error("horizontal split of a block 2 high.");
                }
                DecodeBlock(x, y, w, h / 2, px, py);
                DecodeBlock(x, y + h / 2, w, h / 2, px, py);
                residual = code == 13;
                break;
            case 1:
            case 12:
                CopyAt(_ref0, x, y, w, h, px, py);
                residual = code == 12;
                break;
            case 9:
            case 20:
                CopyAt(_ref1, x, y, w, h, px, py);
                residual = code == 20;
                break;
            case 14:
            case 21:
                CopyAt(_ref2, x, y, w, h, px, py);
                residual = code == 21;
                break;
            case 4:
            case 16:
                CopyDelta(_ref0, x, y, w, h, px, py);
                residual = code == 16;
                break;
            case 5:
            case 17:
                CopyDelta(_ref1, x, y, w, h, px, py);
                residual = code == 17;
                break;
            case 6:
            case 18:
                CopyDelta(_ref2, x, y, w, h, px, py);
                residual = code == 18;
                break;
            case 3:
            case 10:
                CopyWithOffsets(x, y, w, h);
                residual = code == 10;
                break;
            case 7:
            case 23:
                GradientWithOffsets(x, y, w, h);
                residual = code == 23;
                break;
            case 11:
            case 22:
                WholeBlockIntra(x, y, w, h);
                residual = code == 22;
                break;
            case 15:
            case 19:
                SubBlockIntra(x, y, w, h);
                residual = code == 19;
                break;
            default:
                throw Error($"invalid block code {code}.");
            }
            if (residual)
            {
                Residual(x, y, w, h);
            }
            else
            {
                bool clear = code == 0 ? w == 8 && h >= 8 : code == 2 ? w >= 8 && h == 8 : w >= 8 && h >= 8;
                if (clear)
                {
                    ClearCounts(x, y, w, h);
                }
            }
        }

        // ---- copies (spec 4.1, 4.2) ----

        private void CopyDelta(VxPicture? reference, int x, int y, int w, int h, int px, int py)
        {
            int dx = _bits.ReadSe();
            int dy = _bits.ReadSe();
            CopyAt(reference, x, y, w, h, px + dx, py + dy);
        }

        private void CopyAt(VxPicture? reference, int x, int y, int w, int h, int vx, int vy)
        {
            int cell = (y / 16 + 1) * _vectorStride + x / 16 + 1;
            _vectorX[cell] = vx;
            _vectorY[cell] = vy;
            if (reference == null)
            {
                throw Error("block refers to a reference picture that doesn't exist.");
            }
            // luma
            int sx = x + vx;
            int sy = y + vy;
            if (sx < 0 || sy < 0 || sx + w > reference.Width || sy + h > reference.Height)
            {
                throw Error("motion vector points outside the reference picture.");
            }
            byte[] src = reference.Y;
            int srcStride = reference.Width;
            for (int j = 0; j < h; j++)
            {
                Buffer.BlockCopy(src, (sy + j) * srcStride + sx, _y, (y + j) * _lumaStride + x, w);
            }
            // chroma
            CopyChroma(reference, x, y, w, h, vx, vy, 0, 0, plain: true);
        }

        // Copies (plain) or copies with an added offset and clamp8 (not plain) the U and V samples of a block from the
        // reference at luma vector (vx, vy).
        private void CopyChroma(VxPicture reference, int x, int y, int w, int h, int vx, int vy, int offsetU, int offsetV, bool plain)
        {
            int columns = w / 2;
            int rows = h / 2;
            int sx = x + vx;
            int sy = y + vy;
            int srcStride = reference.ChromaWidth;
            int destColumn = x / 2;
            int destRow = y / 2;
            if (sx >= 0 && sy >= 0)
            {
                int firstColumn = sx >> 1;
                int firstRow = sy >> 1;
                if (firstColumn + columns > reference.ChromaWidth || firstRow + rows > reference.ChromaHeight)
                {
                    throw Error("motion vector points outside the reference picture.");
                }
                for (int j = 0; j < rows; j++)
                {
                    int s = (firstRow + j) * srcStride + firstColumn;
                    int d = (destRow + j) * _chromaStride + destColumn;
                    if (plain)
                    {
                        Buffer.BlockCopy(reference.U, s, _u, d, columns);
                        Buffer.BlockCopy(reference.V, s, _v, d, columns);
                    }
                    else
                    {
                        for (int i = 0; i < columns; i++)
                        {
                            _u[d + i] = Clamp8(reference.U[s + i] + offsetU);
                            _v[d + i] = Clamp8(reference.V[s + i] + offsetV);
                        }
                    }
                }
                return;
            }
            // a source coordinate of -1 maps to row or column 0 (C# division rounds toward zero)
            for (int i = 0; i < columns; i++)
            {
                _sourceColumns[i] = (sx + 2 * i) / 2;
            }
            for (int j = 0; j < rows; j++)
            {
                _sourceRows[j] = (sy + 2 * j) / 2;
            }
            if (_sourceColumns[0] < 0 || _sourceRows[0] < 0
                || _sourceColumns[columns - 1] >= reference.ChromaWidth || _sourceRows[rows - 1] >= reference.ChromaHeight)
            {
                throw Error("motion vector points outside the reference picture.");
            }
            for (int j = 0; j < rows; j++)
            {
                int srcRow = _sourceRows[j] * srcStride;
                int d = (destRow + j) * _chromaStride + destColumn;
                for (int i = 0; i < columns; i++)
                {
                    int s = srcRow + _sourceColumns[i];
                    _u[d + i] = plain ? reference.U[s] : Clamp8(reference.U[s] + offsetU);
                    _v[d + i] = plain ? reference.V[s] : Clamp8(reference.V[s] + offsetV);
                }
            }
        }

        private int ReadOffset()
        {
            int value = _bits.ReadSe();
            if (value < -65536 || value >= 65536)
            {
                throw Error("offset out of range.");
            }
            return value * 2;
        }

        private void CopyWithOffsets(int x, int y, int w, int h)
        {
            int vx = _bits.ReadSe();
            int vy = _bits.ReadSe();
            if (x + vx < 0 || x + vx + w > _frameWidth || y + vy < 0 || y + vy + h > _frameHeight)
            {
                throw Error("motion vector points outside the frame.");
            }
            int dY = ReadOffset();
            int dU = ReadOffset();
            int dV = ReadOffset();
            VxPicture reference = _ref0 ?? throw Error("block refers to a reference picture that doesn't exist.");
            int sx = x + vx;
            int sy = y + vy;
            if (sx + w > reference.Width || sy + h > reference.Height)
            {
                throw Error("motion vector points outside the reference picture.");
            }
            byte[] src = reference.Y;
            int srcStride = reference.Width;
            for (int j = 0; j < h; j++)
            {
                int s = (sy + j) * srcStride + sx;
                int d = (y + j) * _lumaStride + x;
                for (int i = 0; i < w; i++)
                {
                    _y[d + i] = Clamp8(src[s + i] + dY);
                }
            }
            CopyChroma(reference, x, y, w, h, vx, vy, dU, dV, plain: false);
        }

        // ---- gradient fill (spec 4.3) ----

        // the plane the gradient works on
        private byte[] _gPlane = null!;
        private int _gStride;
        private int _gUnit;

        private int GetSample(int x, int y)
        {
            return _gUnit == 1 ? _gPlane[y * _gStride + x] : _gPlane[(y / 2) * _gStride + x / 2];
        }

        private void PutSample(int x, int y, int value)
        {
            if (_gUnit == 1)
            {
                _gPlane[y * _gStride + x] = (byte)value;
            }
            else
            {
                _gPlane[(y / 2) * _gStride + x / 2] = (byte)value;
            }
        }

        private void GradientWithOffsets(int x, int y, int w, int h)
        {
            int o = ReadOffset();
            Gradient(_y, _lumaStride, _lumaRows, 1, x, y, w, h, o);
            o = ReadOffset();
            Gradient(_u, _chromaStride, _chromaRows, 2, x, y, w, h, o);
            o = ReadOffset();
            Gradient(_v, _chromaStride, _chromaRows, 2, x, y, w, h, o);
        }

        private void Gradient(byte[] plane, int stride, int rows, int unit, int x, int y, int w, int h, int o)
        {
            // every read is in the block, the column left of it or the row above it, and step 1 reads both the left
            // column and the row above, so checking those once covers every access
            if ((x - 1) / unit < 0 || (y - 1) / unit < 0 || (x + w - 1) / unit >= stride || (y + h - 1) / unit >= rows)
            {
                throw Error("gradient reads outside the picture.");
            }
            _gPlane = plane;
            _gStride = stride;
            _gUnit = unit;
            int a = GetSample(x - 1, y + h - 1);
            int b = GetSample(x + w - 1, y - 1);
            PutSample(x + w - 1, y + h - 1, (a + b + 1) / 2 + o);
            Fill(x, y, w, h);
        }

        private void Fill(int x, int y, int w, int h)
        {
            int u = _gUnit;
            if (w == u)
            {
                if (h == u)
                {
                    return;
                }
                int t = GetSample(x, y - 1);
                int d = GetSample(x, y + h - 1);
                PutSample(x, y + h / 2 - 1, (t + d) / 2);
                Fill(x, y, w, h / 2);
                Fill(x, y + h / 2, w, h / 2);
                return;
            }
            if (h == u)
            {
                int l = GetSample(x - 1, y);
                int r = GetSample(x + w - 1, y);
                PutSample(x + w / 2 - 1, y, (l + r) / 2);
                Fill(x, y, w / 2, h);
                Fill(x + w / 2, y, w / 2, h);
                return;
            }
            int bottomLeft = GetSample(x - 1, y + h - 1);
            int topRight = GetSample(x + w - 1, y - 1);
            int bottomRight = GetSample(x + w - 1, y + h - 1);
            int bottomMiddle = (bottomLeft + bottomRight) / 2;
            PutSample(x + w / 2 - 1, y + h - 1, bottomMiddle);
            int rightMiddle = (topRight + bottomRight) / 2;
            PutSample(x + w - 1, y + h / 2 - 1, rightMiddle);
            bool wideQuarter = w == 4 * u || w == 16 * u;
            bool highQuarter = h == 4 * u || h == 16 * u;
            int centre = wideQuarter != highQuarter
                ? (GetSample(x - 1, y + h / 2 - 1) + rightMiddle) / 2
                : (GetSample(x + w / 2 - 1, y - 1) + bottomMiddle) / 2;
            PutSample(x + w / 2 - 1, y + h / 2 - 1, centre);
            Fill(x, y, w / 2, h / 2);
            Fill(x + w / 2, y, w / 2, h / 2);
            Fill(x, y + h / 2, w / 2, h / 2);
            Fill(x + w / 2, y + h / 2, w / 2, h / 2);
        }
    }
}
