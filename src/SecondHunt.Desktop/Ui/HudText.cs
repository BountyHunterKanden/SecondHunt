using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MphRead;
using MphRead.Formats;

namespace SecondHunt.Desktop.Ui;

// The menus' ROM font for GDI pictures: the glyph atlas in models/hudfont_Model.bin (16 columns of 8x8 cells,
// glyph = char - 32) with the game's advance widths and offsets, read the way MenuFont reads them, drawn
// nearest-neighbour at a whole-number scale and tinted like the frontend's text. A plain bold font stands in
// when the games aren't imported yet.
internal sealed class HudText
{
    const int Cell = 8, Columns = 16;

    static HudText? _shared;
    public static HudText Shared => _shared ??= new HudText();

    readonly Bitmap? _atlas;
    readonly IReadOnlyList<int> _widths = Array.Empty<int>();
    readonly IReadOnlyList<int> _offsets = Array.Empty<int>();
    readonly Dictionary<Color, ImageAttributes> _tints = new();
    readonly Dictionary<int, Font> _fallback = new();
    readonly Bitmap _probe = new(1, 1);

    HudText()
    {
        try
        {
            if ((object?)MphRead.Text.Font.Normal.Widths == null) Extract.LoadRuntimeData();
            _widths = MphRead.Text.Font.Normal.Widths ?? (IReadOnlyList<int>)Array.Empty<int>();
            _offsets = MphRead.Text.Font.Normal.Offsets ?? (IReadOnlyList<int>)Array.Empty<int>();
            Model model = Read.ReadModelFile("hudfont", "models/hudfont_Model.bin", null);
            Material material = model.Materials[0];
            Texture texture = model.Recolors[0].Textures[material.TextureId];
            IReadOnlyList<ColorRgba> pixels = model.GetPixels(material.TextureId, material.PaletteId, recolorId: 0);
            var bmp = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var bgra = new byte[bmp.Width * bmp.Height * 4];
            for (int i = 0; i < pixels.Count && i * 4 + 3 < bgra.Length; i++)
            {
                bgra[i * 4] = pixels[i].Blue;
                bgra[i * 4 + 1] = pixels[i].Green;
                bgra[i * 4 + 2] = pixels[i].Red;
                bgra[i * 4 + 3] = pixels[i].Alpha;
            }
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * bmp.Width * 4, data.Scan0 + y * data.Stride, bmp.Width * 4);
            }
            bmp.UnlockBits(data);
            _atlas = bmp;
        }
        catch (Exception)
        {
            _atlas = null;
        }
    }

    // a line's height on screen (the 8px cell)
    public static float Height(int scale) => Cell * scale;

    int Advance(int glyph) => glyph >= 0 && glyph < _widths.Count ? _widths[glyph] : Cell;

    public float Measure(string text, int scale)
    {
        if (_atlas == null)
        {
            using Graphics g = Graphics.FromImage(_probe);
            return text.Length == 0 ? 0 : g.MeasureString(text, Fallback(scale), int.MaxValue, StringFormat.GenericTypographic).Width;
        }
        float width = 0;
        foreach (char ch in text) width += Advance(ch - 32);
        return width * scale;
    }

    // text with its cell's top-left at (x, y)
    public void Draw(Graphics g, string text, float x, float y, Color color, int scale)
    {
        if (text.Length == 0) return;
        if (_atlas == null)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, Fallback(scale), brush, x, y, StringFormat.GenericTypographic);
            return;
        }
        InterpolationMode mode = g.InterpolationMode;
        PixelOffsetMode offset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        ImageAttributes tint = Tint(color);
        int pen = (int)MathF.Round(x), top = (int)MathF.Round(y);
        foreach (char ch in text)
        {
            int glyph = ch - 32;
            if (ch != ' ' && glyph >= 0 && glyph < _widths.Count)
            {
                int gy = top + (glyph < _offsets.Count ? _offsets[glyph] : 0) * scale;
                var dst = new Rectangle(pen, gy, Cell * scale, Cell * scale);
                g.DrawImage(_atlas, dst, glyph % Columns * Cell, glyph / Columns * Cell, Cell, Cell, GraphicsUnit.Pixel, tint);
            }
            pen += Advance(glyph) * scale;
        }
        g.InterpolationMode = mode;
        g.PixelOffsetMode = offset;
    }

    ImageAttributes Tint(Color c)
    {
        if (_tints.TryGetValue(c, out ImageAttributes? a)) return a;
        var m = new ColorMatrix(new[]
        {
            new[] { c.R / 255f, 0, 0, 0, 0 },
            new[] { 0, c.G / 255f, 0, 0, 0 },
            new[] { 0, 0, c.B / 255f, 0, 0 },
            new[] { 0, 0, 0, c.A / 255f, 0 },
            new[] { 0f, 0, 0, 0, 1 },
        });
        a = new ImageAttributes();
        a.SetColorMatrix(m);
        a.SetWrapMode(WrapMode.TileFlipXY);   // no fading at the cell's edges
        _tints[c] = a;
        return a;
    }

    Font Fallback(int scale)
    {
        if (!_fallback.TryGetValue(scale, out Font? f))
        {
            f = new Font("Consolas", 7 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fallback[scale] = f;
        }
        return f;
    }
}
