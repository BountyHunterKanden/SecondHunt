using System;

namespace MphRead.Formats.Vx
{
    // Intra prediction (spec 4.4 to 4.6) and the residual (spec Part 5).
    internal sealed partial class VxVideo
    {
        // ---- whole-block intra (codes 11, 22) ----

        private void WholeBlockIntra(int x, int y, int w, int h)
        {
            int mode = _bits.ReadUe();
            switch (mode)
            {
            case 0:
                Vertical(_y, _lumaStride, 1, x, y, w, h);
                break;
            case 1:
                Horizontal(_y, _lumaStride, 1, x, y, w, h);
                break;
            case 2:
                Mean(_y, _lumaStride, 1, x, y, w, h);
                break;
            case 3:
                Gradient(_y, _lumaStride, _lumaRows, 1, x, y, w, h, 0);
                break;
            default:
                throw Error($"invalid luma intra mode {mode}.");
            }
            ChromaMode(x, y, w, h);
        }

        // spec 4.6: one mode for both chroma planes, U first
        private void ChromaMode(int x, int y, int w, int h)
        {
            int mode = _bits.ReadUe();
            switch (mode)
            {
            case 0:
                Mean(_u, _chromaStride, 2, x, y, w, h);
                Mean(_v, _chromaStride, 2, x, y, w, h);
                break;
            case 1:
                Horizontal(_u, _chromaStride, 2, x, y, w, h);
                Horizontal(_v, _chromaStride, 2, x, y, w, h);
                break;
            case 2:
                Vertical(_u, _chromaStride, 2, x, y, w, h);
                Vertical(_v, _chromaStride, 2, x, y, w, h);
                break;
            case 3:
                Gradient(_u, _chromaStride, _chromaRows, 2, x, y, w, h, 0);
                Gradient(_v, _chromaStride, _chromaRows, 2, x, y, w, h, 0);
                break;
            default:
                throw Error($"invalid chroma intra mode {mode}.");
            }
        }

        // every position takes the sample at (its x, Y - 1); on chroma at the top edge that row is the block's own
        // first row, which then stays as it is and is copied down
        private void Vertical(byte[] plane, int stride, int unit, int x, int y, int w, int h)
        {
            int sourceRow = (y - 1) / unit;
            if (y - 1 < 0 && unit == 1)
            {
                throw Error("vertical prediction reads above the picture.");
            }
            int column = x / unit;
            int columns = w / unit;
            int firstRow = y / unit;
            int rows = h / unit;
            int source = sourceRow * stride + column;
            for (int j = 0; j < rows; j++)
            {
                if (firstRow + j != sourceRow)
                {
                    Buffer.BlockCopy(plane, source, plane, (firstRow + j) * stride + column, columns);
                }
            }
        }

        // every position takes the sample at (X - 1, its y)
        private void Horizontal(byte[] plane, int stride, int unit, int x, int y, int w, int h)
        {
            int sourceColumn = (x - 1) / unit;
            if (x - 1 < 0 && unit == 1)
            {
                throw Error("horizontal prediction reads left of the picture.");
            }
            int column = x / unit;
            int columns = w / unit;
            int firstRow = y / unit;
            int rows = h / unit;
            for (int j = 0; j < rows; j++)
            {
                int row = (firstRow + j) * stride;
                byte value = plane[row + sourceColumn];
                plane.AsSpan(row + column, columns).Fill(value);
            }
        }

        private void Mean(byte[] plane, int stride, int unit, int x, int y, int w, int h)
        {
            // the sums step through every luma coordinate (each chroma sample counts twice); a sum is only formed when
            // the mean uses it
            int sumTop = 0;
            int sumLeft = 0;
            if (y != 0)
            {
                int row = ((y - 1) / unit) * stride;
                sumTop = w / 2;
                for (int i = 0; i < w; i++)
                {
                    sumTop += plane[row + (x + i) / unit];
                }
            }
            if (x != 0)
            {
                int column = (x - 1) / unit;
                sumLeft = h / 2;
                for (int j = 0; j < h; j++)
                {
                    sumLeft += plane[((y + j) / unit) * stride + column];
                }
            }
            int mean;
            if (x != 0 && y != 0)
            {
                mean = (sumTop / w + sumLeft / h + 1) / 2;
            }
            else if (y != 0)
            {
                mean = sumTop / w;
            }
            else if (x != 0)
            {
                mean = sumLeft / h;
            }
            else
            {
                mean = 128;
            }
            int firstColumn = x / unit;
            int columns = w / unit;
            int firstRow = y / unit;
            int rows = h / unit;
            for (int j = 0; j < rows; j++)
            {
                plane.AsSpan((firstRow + j) * stride + firstColumn, columns).Fill((byte)mean);
            }
        }

        // ---- 4 x 4 intra (codes 15, 19) ----

        private void SubBlockIntra(int x, int y, int w, int h)
        {
            int columns = w / 4;
            int rows = h / 4;
            int[] modes = _subModes;
            for (int j = 0; j < rows; j++)
            {
                for (int i = 0; i < columns; i++)
                {
                    int above = j > 0 ? modes[(j - 1) * 4 + i] : -1;
                    int left = i > 0 ? modes[j * 4 + i - 1] : -1;
                    int predicted = above < 0 ? (left < 0 ? 2 : left) : (left < 0 ? above : Math.Min(above, left));
                    int mode;
                    if (_bits.ReadBit() == 1)
                    {
                        mode = predicted;
                    }
                    else
                    {
                        int r = _bits.ReadBits(3);
                        mode = r < predicted ? r : r + 1;
                    }
                    modes[j * 4 + i] = mode;
                    Predict4x4(x + 4 * i, y + 4 * j, mode);
                }
            }
            ChromaMode(x, y, w, h);
        }

        // H.264 Intra_4x4 prediction, neighbours read straight from the luma plane (no availability rules)
        private void Predict4x4(int x0, int y0, int mode)
        {
            int topCount = 0;
            int leftCount = 0;
            bool corner = false;
            switch (mode)
            {
            case 0:
                topCount = 4;
                break;
            case 1:
                leftCount = 4;
                break;
            case 2:
                topCount = y0 != 0 ? 4 : 0;
                leftCount = x0 != 0 ? 4 : 0;
                break;
            case 3:
                topCount = 8;
                break;
            case 4:
                topCount = 4;
                leftCount = 4;
                corner = true;
                break;
            case 5:
                topCount = 4;
                leftCount = 3;
                corner = true;
                break;
            case 6:
                topCount = 3;
                leftCount = 4;
                corner = true;
                break;
            case 7:
                topCount = 7;
                break;
            case 8:
                leftCount = 4;
                break;
            default:
                throw Error($"invalid 4x4 intra mode {mode}.");
            }
            byte[] plane = _y;
            int stride = _lumaStride;
            int[] t = _top;
            int[] l = _left;
            if (topCount > 0)
            {
                if (y0 < 1 || x0 + topCount > stride)
                {
                    throw Error("4x4 prediction reads outside the picture.");
                }
                int row = (y0 - 1) * stride + x0;
                for (int k = 0; k < topCount; k++)
                {
                    t[k + 1] = plane[row + k];
                }
            }
            if (leftCount > 0)
            {
                if (x0 < 1)
                {
                    throw Error("4x4 prediction reads outside the picture.");
                }
                for (int k = 0; k < leftCount; k++)
                {
                    l[k + 1] = plane[(y0 + k) * stride + x0 - 1];
                }
            }
            if (corner)
            {
                int c = plane[(y0 - 1) * stride + x0 - 1];
                t[0] = c;
                l[0] = c;
            }
            int[] p = _predicted;
            switch (mode)
            {
            case 0:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        p[y * 4 + x] = t[x + 1];
                    }
                }
                break;
            case 1:
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        p[y * 4 + x] = l[y + 1];
                    }
                }
                break;
            case 2:
            {
                int dc;
                if (x0 != 0 && y0 != 0)
                {
                    dc = (t[1] + t[2] + t[3] + t[4] + l[1] + l[2] + l[3] + l[4] + 4) >> 3;
                }
                else if (x0 != 0)
                {
                    dc = (l[1] + l[2] + l[3] + l[4] + 2) >> 2;
                }
                else if (y0 != 0)
                {
                    dc = (t[1] + t[2] + t[3] + t[4] + 2) >> 2;
                }
                else
                {
                    dc = 128;
                }
                for (int i = 0; i < 16; i++)
                {
                    p[i] = dc;
                }
                break;
            }
            case 3: // diagonal down-left
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        p[y * 4 + x] = x == 3 && y == 3
                            ? (t[7] + 3 * t[8] + 2) >> 2
                            : (t[x + y + 1] + 2 * t[x + y + 2] + t[x + y + 3] + 2) >> 2;
                    }
                }
                break;
            case 4: // diagonal down-right
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int value;
                        if (x > y)
                        {
                            value = (t[x - y - 1] + 2 * t[x - y] + t[x - y + 1] + 2) >> 2;
                        }
                        else if (x < y)
                        {
                            value = (l[y - x - 1] + 2 * l[y - x] + l[y - x + 1] + 2) >> 2;
                        }
                        else
                        {
                            value = (t[1] + 2 * t[0] + l[1] + 2) >> 2;
                        }
                        p[y * 4 + x] = value;
                    }
                }
                break;
            case 5: // vertical-right
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int z = 2 * x - y;
                        int s = x - (y >> 1);
                        int value;
                        if (z >= 0 && (z & 1) == 0)
                        {
                            value = (t[s] + t[s + 1] + 1) >> 1;
                        }
                        else if (z > 0)
                        {
                            value = (t[s - 1] + 2 * t[s] + t[s + 1] + 2) >> 2;
                        }
                        else if (z == -1)
                        {
                            value = (l[1] + 2 * t[0] + t[1] + 2) >> 2;
                        }
                        else
                        {
                            value = (l[y] + 2 * l[y - 1] + l[y - 2] + 2) >> 2;
                        }
                        p[y * 4 + x] = value;
                    }
                }
                break;
            case 6: // horizontal-down
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int z = 2 * y - x;
                        int s = y - (x >> 1);
                        int value;
                        if (z >= 0 && (z & 1) == 0)
                        {
                            value = (l[s] + l[s + 1] + 1) >> 1;
                        }
                        else if (z > 0)
                        {
                            value = (l[s - 1] + 2 * l[s] + l[s + 1] + 2) >> 2;
                        }
                        else if (z == -1)
                        {
                            value = (l[1] + 2 * l[0] + t[1] + 2) >> 2;
                        }
                        else
                        {
                            value = (t[x] + 2 * t[x - 1] + t[x - 2] + 2) >> 2;
                        }
                        p[y * 4 + x] = value;
                    }
                }
                break;
            case 7: // vertical-left
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int s = x + (y >> 1);
                        p[y * 4 + x] = (y & 1) == 0
                            ? (t[s + 1] + t[s + 2] + 1) >> 1
                            : (t[s + 1] + 2 * t[s + 2] + t[s + 3] + 2) >> 2;
                    }
                }
                break;
            default: // 8, horizontal-up
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int z = x + 2 * y;
                        int s = y + (x >> 1);
                        int value;
                        if (z > 5)
                        {
                            value = l[4];
                        }
                        else if (z == 5)
                        {
                            value = (l[3] + 3 * l[4] + 2) >> 2;
                        }
                        else if ((z & 1) == 0)
                        {
                            value = (l[s + 1] + l[s + 2] + 1) >> 1;
                        }
                        else
                        {
                            value = (l[s + 1] + 2 * l[s + 2] + l[s + 3] + 2) >> 2;
                        }
                        p[y * 4 + x] = value;
                    }
                }
                break;
            }
            for (int y = 0; y < 4; y++)
            {
                int row = (y0 + y) * stride + x0;
                plane[row] = (byte)p[y * 4];
                plane[row + 1] = (byte)p[y * 4 + 1];
                plane[row + 2] = (byte)p[y * 4 + 2];
                plane[row + 3] = (byte)p[y * 4 + 3];
            }
        }

        // ---- residual (spec Part 5) ----

        private void Residual(int x, int y, int w, int h)
        {
            for (int j = 0; j < h; j += 8)
            {
                for (int i = 0; i < w; i += 8)
                {
                    int ax = x + i;
                    int ay = y + j;
                    int k = _bits.ReadUe();
                    if ((uint)k > 31)
                    {
                        throw Error($"invalid coded-block pattern {k}.");
                    }
                    int flags = _codedFlags[k];
                    LumaResidual(ax, ay, (flags & 1) != 0);
                    LumaResidual(ax + 4, ay, (flags & 2) != 0);
                    LumaResidual(ax, ay + 4, (flags & 4) != 0);
                    LumaResidual(ax + 4, ay + 4, (flags & 8) != 0);
                    int cell = (ay / 8) * _chromaCountStride + ax / 8;
                    if ((flags & 16) != 0)
                    {
                        int nC = (_chromaCounts[(ay / 8) * _chromaCountStride + (ax - 1) / 8]
                            + _chromaCounts[((ay - 1) / 8) * _chromaCountStride + ax / 8] + 1) / 2;
                        int totalU = Residual4x4(nC, _u, _chromaStride, _chromaRows, 2, ax, ay);
                        int totalV = Residual4x4(nC, _v, _chromaStride, _chromaRows, 2, ax, ay);
                        _chromaCounts[cell] = (totalU + totalV + 1) / 2;
                    }
                    else
                    {
                        _chromaCounts[cell] = 0;
                    }
                }
            }
        }

        private void LumaResidual(int x, int y, bool coded)
        {
            int cell = (y / 4) * _lumaCountStride + x / 4;
            if (coded)
            {
                // C# division: at the left or top frame edge, -1 / 4 = 0 picks the block's own cell
                int nC = (_lumaCounts[(y / 4) * _lumaCountStride + (x - 1) / 4]
                    + _lumaCounts[((y - 1) / 4) * _lumaCountStride + x / 4] + 1) / 2;
                _lumaCounts[cell] = Residual4x4(nC, _y, _lumaStride, _lumaRows, 1, x, y);
            }
            else
            {
                _lumaCounts[cell] = 0;
            }
        }

        // spec 5.4
        private void ClearCounts(int x, int y, int w, int h)
        {
            for (int j = 0; j < h; j += 8)
            {
                for (int i = 0; i < w; i += 8)
                {
                    int ax = x + i;
                    int ay = y + j;
                    int top = (ay / 4) * _lumaCountStride;
                    int bottom = ((ay + 4) / 4) * _lumaCountStride;
                    _lumaCounts[top + ax / 4] = 0;
                    _lumaCounts[bottom + ax / 4] = 0;
                    _lumaCounts[top + (ax + 4) / 4] = 0;
                    _lumaCounts[bottom + (ax + 4) / 4] = 0;
                    _chromaCounts[(ay / 8) * _chromaCountStride + ax / 8] = 0;
                }
            }
        }

        // decodes one 4 x 4 block of coefficients and adds its inverse transform to the plane at luma (x, y) with the
        // plane's unit; returns TotalCoeff
        private int Residual4x4(int nC, byte[] plane, int stride, int rows, int unit, int x, int y)
        {
            int tableIndex = nC < 2 ? 0 : nC < 4 ? 1 : nC < 8 ? 2 : 3;
            int symbol = _bits.ReadCode(VxCodeTables.CoeffToken[tableIndex]);
            int total = symbol >> 2;
            if (total == 0)
            {
                return 0;
            }
            int trailingOnes = symbol & 3;
            int zeros = total < 16 ? _bits.ReadCode(VxCodeTables.TotalZeros[total]) : 0;
            int[] c = _coefficients;
            Array.Clear(c);
            int position = total + zeros - 1;
            int zerosLeft = zeros;
            int suffixLength = 0;
            for (int n = 1; n <= total; n++)
            {
                int level;
                if (n <= trailingOnes)
                {
                    level = _bits.ReadBit() == 0 ? 1 : -1;
                }
                else
                {
                    int prefix = _bits.CountZerosThenOne();
                    int suffix = _bits.ReadBits(prefix == 15 ? 11 : suffixLength);
                    int magnitude = (prefix << suffixLength) + suffix + 1;
                    if (suffixLength < 5 && magnitude > _levelLimits[suffixLength])
                    {
                        suffixLength++;
                    }
                    level = _bits.ReadBit() == 1 ? -magnitude : magnitude;
                }
                if ((uint)position > 15)
                {
                    throw Error("coefficient position out of range.");
                }
                int raster = _zigZag[position];
                c[raster] = level * _weights[raster];
                if (n < total)
                {
                    int run = 0;
                    if (zerosLeft > 0)
                    {
                        run = _bits.ReadCode(VxCodeTables.RunBefore[zerosLeft < 7 ? zerosLeft : 7]);
                        zerosLeft -= run;
                    }
                    position -= 1 + run;
                }
            }
            int column = x / unit;
            int row0 = y / unit;
            if (column + 3 >= stride || row0 + 3 >= rows)
            {
                throw Error("residual outside the picture.");
            }
            if (total == 1 && zeros == 0)
            {
                // only the DC coefficient: every output of the transform equals it
                int r = (c[0] + 32) >> 6;
                for (int j = 0; j < 4; j++)
                {
                    int p = (row0 + j) * stride + column;
                    plane[p] = Clamp8(plane[p] + r);
                    plane[p + 1] = Clamp8(plane[p + 1] + r);
                    plane[p + 2] = Clamp8(plane[p + 2] + r);
                    plane[p + 3] = Clamp8(plane[p + 3] + r);
                }
                return total;
            }
            // rows, halvings rounding toward zero
            for (int i = 0; i < 16; i += 4)
            {
                int d0 = c[i];
                int d1 = c[i + 1];
                int d2 = c[i + 2];
                int d3 = c[i + 3];
                int e0 = d0 + d2;
                int e1 = d0 - d2;
                int e2 = d1 / 2 - d3;
                int e3 = d1 + d3 / 2;
                c[i] = e0 + e3;
                c[i + 1] = e1 + e2;
                c[i + 2] = e1 - e2;
                c[i + 3] = e0 - e3;
            }
            // columns
            for (int i = 0; i < 4; i++)
            {
                int f0 = c[i];
                int f1 = c[4 + i];
                int f2 = c[8 + i];
                int f3 = c[12 + i];
                int g0 = f0 + f2;
                int g1 = f0 - f2;
                int g2 = f1 / 2 - f3;
                int g3 = f1 + f3 / 2;
                c[i] = g0 + g3;
                c[4 + i] = g1 + g2;
                c[8 + i] = g1 - g2;
                c[12 + i] = g0 - g3;
            }
            for (int j = 0; j < 4; j++)
            {
                int p = (row0 + j) * stride + column;
                int q = j * 4;
                plane[p] = Clamp8(plane[p] + ((c[q] + 32) >> 6));
                plane[p + 1] = Clamp8(plane[p + 1] + ((c[q + 1] + 32) >> 6));
                plane[p + 2] = Clamp8(plane[p + 2] + ((c[q + 2] + 32) >> 6));
                plane[p + 3] = Clamp8(plane[p + 3] + ((c[q + 3] + 32) >> 6));
            }
            return total;
        }
    }
}
