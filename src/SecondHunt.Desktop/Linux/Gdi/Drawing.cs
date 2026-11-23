using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MphRecomp.App.Platform;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32>;
using IsPoint = SixLabors.ImageSharp.PointF;
using SlFont = SixLabors.Fonts.Font;
using SlStyle = SixLabors.Fonts.FontStyle;

namespace System.Drawing;

public abstract class Image : IDisposable
{
    internal IsImage Pixels { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;

    private protected Image(int width, int height)
    {
        Pixels = new IsImage(GdiShim.Config, Math.Max(1, width), Math.Max(1, height));
    }

    public void Dispose() => Pixels.Dispose();
}

public sealed class Bitmap : Image
{
    public Bitmap(int width, int height) : base(width, height) { }
    public Bitmap(int width, int height, PixelFormat format) : base(width, height) { }

    // the pixels as GDI+ hands them out: 4 bytes a pixel, B G R A, rows top to bottom
    public unsafe BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
    {
        if (!Pixels.DangerousTryGetSinglePixelMemory(out Memory<Bgra32> memory))
        {
            throw new InvalidOperationException("bitmap isn't one block of memory");
        }
        var pin = memory.Pin();
        int stride = Width * 4;
        return new BitmapData(pin, (IntPtr)((byte*)pin.Pointer + rect.Y * stride + rect.X * 4), stride, rect.Width, rect.Height);
    }

    public void UnlockBits(BitmapData data) => data.Release();
}

public enum FontStyle { Regular = 0, Bold = 1, Italic = 2, Underline = 4, Strikeout = 8 }
public enum GraphicsUnit { World, Display, Pixel, Point, Inch, Document, Millimeter }
public enum StringAlignment { Near, Center, Far }
public enum StringTrimming { None, Character, Word, EllipsisCharacter, EllipsisWord, EllipsisPath }
[Flags] public enum StringFormatFlags { NoWrap = 0x1000, NoClip = 0x4000, MeasureTrailingSpaces = 0x0800 }

public sealed class StringFormat : IDisposable
{
    public StringAlignment Alignment { get; set; }
    public StringAlignment LineAlignment { get; set; }
    public StringTrimming Trimming { get; set; } = StringTrimming.Character;
    public StringFormatFlags FormatFlags { get; set; }
    public static StringFormat GenericTypographic => new() { Trimming = StringTrimming.None };
    public void Dispose() { }
}

public abstract class Brush : IDisposable
{
    internal abstract Color Color { get; }
    public void Dispose() { }
}

public sealed class SolidBrush : Brush
{
    public SolidBrush(Color color) { Color = color; }
    internal override Color Color { get; }
}

public sealed class Pen : IDisposable
{
    public Color Color { get; }
    public float Width { get; }
    public Pen(Color color, float width = 1) { Color = color; Width = width; }
    public void Dispose() { }
}

// A font from the system's (fontconfig's folders): Windows' "Segoe UI" and "Consolas" become the nearest ones Linux
// desktops carry (DejaVu, Noto, Liberation, Ubuntu, Cantarell), with the rest as fallbacks for missing characters
public sealed class Font : IDisposable
{
    public string Name { get; }
    public float Size { get; }
    internal SlFont? Native { get; }
    internal float LineHeight { get; }
    internal float GlyphTop { get; } // the glyphs' own line height centred in LineHeight

    public Font(string familyName, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        Name = familyName;
        Size = emSize;
        float px = unit == GraphicsUnit.Point ? emSize * 96 / 72 : emSize;
        FontFamily? family = GdiShim.Family(familyName);
        if (family is FontFamily f)
        {
            SlStyle want = (style & FontStyle.Bold) != 0 ? SlStyle.Bold : SlStyle.Regular;
            if (!f.GetAvailableStyles().Contains(want)) want = f.GetAvailableStyles().FirstOrDefault();
            Native = f.CreateFont(Math.Max(1, px), want);
            // at least Segoe UI's line spacing (1.33 em), which the screens' layouts were made with: DejaVu's own 1.16
            // em puts one wrapped line's descenders on the next one's capitals
            float h = TextMeasurer.MeasureAdvance("Ag", GdiShim.Options(this, 0, 0)).Height;
            LineHeight = Math.Max(h, px * 1.33f);
            GlyphTop = (LineHeight - h) / 2;
        }
        else LineHeight = px * 1.33f;
    }

    public void Dispose() { }
}

public sealed class Graphics : IDisposable
{
    readonly IsImage _image;
    public SmoothingMode SmoothingMode { get; set; }
    public TextRenderingHint TextRenderingHint { get; set; }
    public InterpolationMode InterpolationMode { get; set; }
    public PixelOffsetMode PixelOffsetMode { get; set; }

    Graphics(IsImage image) { _image = image; }

    public static Graphics FromImage(Image image) => new(image.Pixels);

    public void Dispose() { }

    DrawingOptions Shapes => new() { GraphicsOptions = new SixLabors.ImageSharp.GraphicsOptions { Antialias = SmoothingMode != SmoothingMode.None } };
    DrawingOptions Text => new()
    {
        GraphicsOptions = new SixLabors.ImageSharp.GraphicsOptions { Antialias = TextRenderingHint != TextRenderingHint.SingleBitPerPixel }
    };

    public void Clear(Color color) => _image.Mutate(c => c.Clear(GdiShim.Is(color)));

    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        _image.Mutate(c => c.Fill(Shapes, GdiShim.Is(brush.Color), new SixLabors.ImageSharp.Drawing.RectangularPolygon(x, y, width, height)));
    }

    public void FillRectangle(Brush brush, RectangleF r) => FillRectangle(brush, r.X, r.Y, r.Width, r.Height);
    public void FillRectangle(Brush brush, Rectangle r) => FillRectangle(brush, r.X, r.Y, r.Width, r.Height);

    public void FillPath(Brush brush, GraphicsPath path)
    {
        if (path.ToPath() is { } p) _image.Mutate(c => c.Fill(Shapes, GdiShim.Is(brush.Color), p));
    }

    public void DrawPath(Pen pen, GraphicsPath path)
    {
        if (path.ToPath() is { } p) _image.Mutate(c => c.Draw(Shapes, GdiShim.Is(pen.Color), pen.Width, p));
    }

    public void FillPolygon(Brush brush, PointF[] points)
    {
        if (points.Length < 3) return;
        var poly = new SixLabors.ImageSharp.Drawing.Polygon(points.Select(GdiShim.Is).ToArray());
        _image.Mutate(c => c.Fill(Shapes, GdiShim.Is(brush.Color), poly));
    }

    public void FillEllipse(Brush brush, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        var e = new SixLabors.ImageSharp.Drawing.EllipsePolygon(x + width / 2, y + height / 2, width, height);
        _image.Mutate(c => c.Fill(Shapes, GdiShim.Is(brush.Color), e));
    }

    public void DrawEllipse(Pen pen, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        var e = new SixLabors.ImageSharp.Drawing.EllipsePolygon(x + width / 2, y + height / 2, width, height);
        _image.Mutate(c => c.Draw(Shapes, GdiShim.Is(pen.Color), pen.Width, e));
    }

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2) =>
        _image.Mutate(c => c.DrawLine(Shapes, GdiShim.Is(pen.Color), pen.Width, new IsPoint(x1, y1), new IsPoint(x2, y2)));

    // the whole picture into dst
    public void DrawImage(Image image, RectangleF dst) =>
        DrawImage(image, Rectangle.Round(dst), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, null);
    public void DrawImage(Image image, Rectangle dst) =>
        DrawImage(image, dst, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, null);

    // a part of the picture into dst, scaled, through the attributes' colour matrix (HudText's tinted glyphs)
    public void DrawImage(Image image, Rectangle dst, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit unit, ImageAttributes? attributes)
    {
        if (dst.Width <= 0 || dst.Height <= 0 || srcWidth <= 0 || srcHeight <= 0) return;
        bool nearest = InterpolationMode == InterpolationMode.NearestNeighbor;
        var src = new Rectangle(srcX, srcY, srcWidth, srcHeight);
        IsImage piece = attributes != null
            ? attributes.Piece(image, src, dst.Size, nearest)
            : ImageAttributes.Make(image, src, dst.Size, nearest, null);
        try
        {
            _image.Mutate(c => c.DrawImage(piece, new SixLabors.ImageSharp.Point(dst.X, dst.Y), 1f));
        }
        finally
        {
            if (attributes == null) piece.Dispose();
        }
    }

    public SizeF MeasureString(string text, Font font, int width) => MeasureString(text, font, width, null);

    public SizeF MeasureString(string text, Font font, int width, StringFormat? format)
    {
        bool wrap = format == null || (format.FormatFlags & StringFormatFlags.NoWrap) == 0;
        List<(string Text, float Width)> lines = GdiShim.Lines(text, font, wrap && width < int.MaxValue ? width : 0);
        return new SizeF(lines.Count == 0 ? 0 : lines.Max(l => l.Width), lines.Count * font.LineHeight);
    }

    public void DrawString(string text, Font font, Brush brush, float x, float y) => DrawString(text, font, brush, x, y, null);

    public void DrawString(string text, Font font, Brush brush, float x, float y, StringFormat? format)
    {
        List<(string Text, float Width)> lines = GdiShim.Lines(text, font, 0);
        float top = y - (format?.LineAlignment ?? StringAlignment.Near) switch
        {
            StringAlignment.Center => lines.Count * font.LineHeight / 2,
            StringAlignment.Far => lines.Count * font.LineHeight,
            _ => 0,
        };
        Draw(lines, font, brush, x, 0, format?.Alignment ?? StringAlignment.Near, top, null);
    }

    public void DrawString(string text, Font font, Brush brush, RectangleF r, StringFormat? format)
    {
        bool wrap = format == null || (format.FormatFlags & StringFormatFlags.NoWrap) == 0;
        StringTrimming trim = format?.Trimming ?? StringTrimming.Character;
        List<(string Text, float Width)> lines = GdiShim.Lines(text, font, wrap ? r.Width : 0);
        int fit = Math.Max(1, (int)((r.Height + 0.5f) / font.LineHeight));
        bool ellipsis = trim is StringTrimming.EllipsisCharacter or StringTrimming.EllipsisWord or StringTrimming.EllipsisPath;
        if (lines.Count > fit && wrap)
        {
            lines.RemoveRange(fit, lines.Count - fit);
            if (ellipsis) lines[^1] = GdiShim.Ellipsis(lines[^1].Text + "…", font, r.Width);
        }
        if (ellipsis)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Width > r.Width) lines[i] = GdiShim.Ellipsis(lines[i].Text, font, r.Width);
            }
        }
        float height = lines.Count * font.LineHeight;
        float top = (format?.LineAlignment ?? StringAlignment.Near) switch
        {
            StringAlignment.Center => r.Y + (r.Height - height) / 2,
            StringAlignment.Far => r.Bottom - height,
            _ => r.Y,
        };
        bool clip = format == null || (format.FormatFlags & StringFormatFlags.NoClip) == 0;
        Draw(lines, font, brush, r.X, r.Width, format?.Alignment ?? StringAlignment.Near, top, clip ? r : null);
    }

    void Draw(List<(string Text, float Width)> lines, Font font, Brush brush, float x, float width, StringAlignment align, float top,
        RectangleF? clip)
    {
        if (font.Native == null || lines.Count == 0) return;
        DrawingOptions options = Text;
        var fill = Brushes.Solid(GdiShim.Is(brush.Color));
        void Lines(IImageProcessingContext c)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                (string line, float w) = lines[i];
                if (line.Length == 0) continue;
                float lx = align switch
                {
                    StringAlignment.Center => x + (width - w) / 2,
                    StringAlignment.Far => x + width - w,
                    _ => x,
                };
                c.DrawText(options, GdiShim.Options(font, lx, top + i * font.LineHeight + font.GlyphTop), line, fill, null);
            }
        }
        if (clip is RectangleF r && r.Width > 0 && r.Height > 0)
        {
            _image.Mutate(c => c.Clip(new SixLabors.ImageSharp.Drawing.RectangularPolygon(r.X, r.Y, r.Width, r.Height), Lines));
        }
        else _image.Mutate(Lines);
    }
}

// the shared parts: pixel buffers, colour and point conversion, fonts and line breaking
internal static class GdiShim
{
    // one block of memory per bitmap, for LockBits
    public static readonly SixLabors.ImageSharp.Configuration Config = MakeConfig();

    static SixLabors.ImageSharp.Configuration MakeConfig()
    {
        var c = SixLabors.ImageSharp.Configuration.Default.Clone();
        c.PreferContiguousImageBuffers = true;
        return c;
    }

    public static SixLabors.ImageSharp.Color Is(Color c) => SixLabors.ImageSharp.Color.FromRgba(c.R, c.G, c.B, c.A);
    public static IsPoint Is(PointF p) => new(p.X, p.Y);

    // Windows' font names -> what a Linux desktop has, best first
    static readonly string[] Sans = { "Segoe UI", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Ubuntu", "Cantarell", "Open Sans", "FreeSans" };
    static readonly string[] Mono = { "Consolas", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "Ubuntu Mono", "FreeMono" };
    static readonly Dictionary<string, FontFamily?> Families = new(StringComparer.OrdinalIgnoreCase);
    static FontFamily[]? _fallbacks;
    static bool _warned;

    public static FontFamily? Family(string name)
    {
        lock (Families)
        {
            if (Families.TryGetValue(name, out FontFamily? known)) return known;
            IEnumerable<string> wanted = Mono.Contains(name, StringComparer.OrdinalIgnoreCase) ? Mono
                : name.Contains("mono", StringComparison.OrdinalIgnoreCase) ? Mono.Prepend(name) : Sans.Prepend(name);
            FontFamily? found = null;
            foreach (string n in wanted)
            {
                if (SystemFonts.TryGet(n, out FontFamily f)) { found = f; break; }
            }
            found ??= SystemFonts.Families.Cast<FontFamily?>().FirstOrDefault();
            if (found == null && !_warned)
            {
                _warned = true;
                Log.Error("MPHDesktop", "no fonts found for this app's own screens: install DejaVu fonts (fonts-dejavu-core / dejavu-fonts)");
            }
            else if (found is FontFamily ff) Log.Info("MPHDesktop", $"font \"{name}\" -> {ff.Name}");
            Families[name] = found;
            return found;
        }
    }

    static FontFamily[] Fallbacks()
    {
        if (_fallbacks != null) return _fallbacks;
        var list = new List<FontFamily>();
        foreach (string n in Sans.Concat(Mono).Concat(new[] { "Noto Sans Symbols", "Noto Sans Symbols2", "DejaVu Serif" }))
        {
            if (SystemFonts.TryGet(n, out FontFamily f) && !list.Contains(f)) list.Add(f);
        }
        return _fallbacks = list.ToArray();
    }

    public static RichTextOptions Options(Font font, float x, float y) => new(font.Native!)
    {
        Origin = new IsPoint(x, y),
        FallbackFontFamilies = Fallbacks(),
    };

    public static float Width(string text, Font font) =>
        font.Native == null || text.Length == 0 ? 0 : TextMeasurer.MeasureAdvance(text, Options(font, 0, 0)).Width;

    // the lines GDI+ would draw: the text's own line breaks, then word wrap at width (0: none)
    public static List<(string Text, float Width)> Lines(string text, Font font, float width)
    {
        var lines = new List<(string, float)>();
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string para = raw.TrimEnd('\r');
            if (width <= 0 || Width(para, font) <= width)
            {
                lines.Add((para, Width(para, font)));
                continue;
            }
            string line = "";
            foreach (string word in para.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Width(next, font) > width)
                {
                    lines.Add((line, Width(line, font)));
                    line = word;
                }
                else line = next;
                // a word wider than the line by itself: broken where it runs out
                while (Width(line, font) > width && line.Length > 1)
                {
                    int cut = line.Length - 1;
                    while (cut > 1 && Width(line[..cut], font) > width) cut--;
                    lines.Add((line[..cut], Width(line[..cut], font)));
                    line = line[cut..];
                }
            }
            lines.Add((line, Width(line, font)));
        }
        return lines;
    }

    // text cut to fit width, ending in an ellipsis
    public static (string, float) Ellipsis(string text, Font font, float width)
    {
        string t = text.TrimEnd('…');
        while (t.Length > 0)
        {
            string s = t.TrimEnd() + "…";
            float w = Width(s, font);
            if (w <= width) return (s, w);
            t = t[..^1];
        }
        return ("", 0);
    }
}
