using System.Buffers;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32>;

// the rest of the Linux build's GDI+ stand-in (Drawing.cs): locked pixels, colour-matrix tints, paths, drawing modes
namespace System.Drawing.Imaging
{
    public enum PixelFormat { Format32bppArgb = 2498570 }
    public enum ImageLockMode { ReadOnly = 1, WriteOnly = 2, ReadWrite = 3 }

    public sealed class BitmapData
    {
        MemoryHandle _pin;
        public IntPtr Scan0 { get; }
        public int Stride { get; }
        public int Width { get; }
        public int Height { get; }

        internal BitmapData(MemoryHandle pin, IntPtr scan0, int stride, int width, int height)
        {
            _pin = pin;
            Scan0 = scan0;
            Stride = stride;
            Width = width;
            Height = height;
        }

        internal void Release() => _pin.Dispose();
    }

    public sealed class ColorMatrix
    {
        internal readonly float[][] Rows;
        public ColorMatrix(float[][] rows) { Rows = rows; }
    }

    // a colour matrix for DrawImage: each pixel's (R G B A 1) times the matrix, as GDI+ does. The scaled, tinted pieces
    // are kept (HudText draws the same glyphs at the same sizes over and over)
    public sealed class ImageAttributes : IDisposable
    {
        ColorMatrix? _matrix;
        readonly Dictionary<(IsImage, Rectangle, Size, bool), IsImage> _pieces = new();

        public void SetColorMatrix(ColorMatrix matrix)
        {
            _matrix = matrix;
            Clear();
        }

        // edges sampled from inside the picture: ImageSharp's resampler already clamps there
        public void SetWrapMode(Drawing2D.WrapMode mode) { }

        internal IsImage Piece(Image image, Rectangle src, Size size, bool nearest)
        {
            var key = (image.Pixels, src, size, nearest);
            if (_pieces.TryGetValue(key, out IsImage? piece)) return piece;
            if (_pieces.Count > 2048) Clear();
            return _pieces[key] = Make(image, src, size, nearest, _matrix);
        }

        internal static IsImage Make(Image image, Rectangle src, Size size, bool nearest, ColorMatrix? matrix)
        {
            IsImage piece = image.Pixels.Clone(c =>
            {
                c.Crop(new SixLabors.ImageSharp.Rectangle(src.X, src.Y, src.Width, src.Height));
                if (src.Width != size.Width || src.Height != size.Height)
                {
                    c.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(size.Width, size.Height),
                        Mode = ResizeMode.Stretch,
                        Sampler = nearest ? KnownResamplers.NearestNeighbor : KnownResamplers.Bicubic,
                    });
                }
            });
            if (matrix != null) Apply(piece, matrix.Rows);
            return piece;
        }

        static void Apply(IsImage image, float[][] m)
        {
            float At(int r, int c) => r < m.Length && c < m[r].Length ? m[r][c] : (r == c ? 1 : 0);
            // GDI's row-vector form: out = in * M (rows 0-3 per input channel, row 4 the offset)
            var mx = new Matrix4x4(At(0, 0), At(0, 1), At(0, 2), At(0, 3), At(1, 0), At(1, 1), At(1, 2), At(1, 3),
                At(2, 0), At(2, 1), At(2, 2), At(2, 3), At(3, 0), At(3, 1), At(3, 2), At(3, 3));
            var offset = new Vector4(At(4, 0), At(4, 1), At(4, 2), At(4, 3));
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                {
                    Span<Bgra32> row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        Vector4 v = Vector4.Transform(row[x].ToScaledVector4(), mx) + offset; // R G B A, 0..1
                        row[x].FromScaledVector4(Vector4.Clamp(v, Vector4.Zero, Vector4.One));
                    }
                }
            });
        }

        void Clear()
        {
            foreach (IsImage p in _pieces.Values) p.Dispose();
            _pieces.Clear();
        }

        public void Dispose() => Clear();
    }
}

namespace System.Drawing.Drawing2D
{
    public enum SmoothingMode { Invalid = -1, Default, HighSpeed, HighQuality, None, AntiAlias }
    public enum InterpolationMode { Invalid = -1, Default, Low, High, Bilinear, Bicubic, NearestNeighbor, HighQualityBilinear, HighQualityBicubic }
    public enum PixelOffsetMode { Invalid = -1, Default, HighSpeed, HighQuality, None, Half }
    public enum WrapMode { Tile, TileFlipX, TileFlipY, TileFlipXY, Clamp }

    // the figures of a path, as points (arcs are flattened into short lines)
    public sealed class GraphicsPath : IDisposable
    {
        readonly List<List<SixLabors.ImageSharp.PointF>> _figures = new();
        readonly List<bool> _closed = new();
        List<SixLabors.ImageSharp.PointF>? _open;

        List<SixLabors.ImageSharp.PointF> Open()
        {
            if (_open == null)
            {
                _open = new();
                _figures.Add(_open);
                _closed.Add(false);
            }
            return _open;
        }

        // an arc of the ellipse in the box, degrees clockwise from the +x axis (screen y points down), as GDI+
        public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
        {
            List<SixLabors.ImageSharp.PointF> f = Open();
            float cx = x + width / 2, cy = y + height / 2, rx = width / 2, ry = height / 2;
            int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweepAngle) / 360 * MathF.Max(16, MathF.Max(rx, ry) * 2)));
            for (int i = 0; i <= steps; i++)
            {
                float a = (startAngle + sweepAngle * i / steps) * MathF.PI / 180;
                f.Add(new SixLabors.ImageSharp.PointF(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a)));
            }
        }

        public void AddLine(float x1, float y1, float x2, float y2)
        {
            List<SixLabors.ImageSharp.PointF> f = Open();
            f.Add(new SixLabors.ImageSharp.PointF(x1, y1));
            f.Add(new SixLabors.ImageSharp.PointF(x2, y2));
        }

        public void CloseFigure()
        {
            if (_open != null) _closed[^1] = true;
            _open = null;
        }

        internal SixLabors.ImageSharp.Drawing.IPath? ToPath()
        {
            var paths = new List<SixLabors.ImageSharp.Drawing.IPath>();
            for (int i = 0; i < _figures.Count; i++)
            {
                SixLabors.ImageSharp.PointF[] pts = _figures[i].ToArray();
                if (pts.Length < 2) continue;
                var segment = new SixLabors.ImageSharp.Drawing.LinearLineSegment(pts);
                paths.Add(_closed[i]
                    ? new SixLabors.ImageSharp.Drawing.Polygon(segment)
                    : new SixLabors.ImageSharp.Drawing.Path(segment));
            }
            return paths.Count switch
            {
                0 => null,
                1 => paths[0],
                _ => new SixLabors.ImageSharp.Drawing.ComplexPolygon(paths),
            };
        }

        public void Dispose() { }
    }
}

namespace System.Drawing.Text
{
    public enum TextRenderingHint { SystemDefault, SingleBitPerPixelGridFit, SingleBitPerPixel, AntiAliasGridFit, AntiAlias, ClearTypeGridFit }
}
