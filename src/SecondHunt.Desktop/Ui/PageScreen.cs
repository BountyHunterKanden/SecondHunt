using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MphRecomp.App;
using MphRecomp.App.Gl;

namespace SecondHunt.Desktop.Ui;

// A screen of this host's own (first-launch setup, the multiplayer lobby, the credits): painted with GDI+ into one
// window-sized picture whenever it changes, and shown as a texture. The look follows the Android app's own screens
// (dark page, light text, cyan accent, rounded boxes). Input comes from DesktopApp as menu moves, presses and clicks.
internal abstract class PageScreen : IGlScreen
{
    protected static readonly Color Background = ColorTranslator.FromHtml("#0A0E14");
    protected static readonly Color Accent = ColorTranslator.FromHtml("#4FC3F7");
    protected static readonly Color Label = ColorTranslator.FromHtml("#C9D3E0");
    protected static readonly Color Muted = ColorTranslator.FromHtml("#8A96A8");
    protected static readonly Color Warning = ColorTranslator.FromHtml("#FFB74D");
    static readonly Color BoxFill = ColorTranslator.FromHtml("#151B24"), BoxEdge = ColorTranslator.FromHtml("#263241");
    static readonly Color FocusFill = ColorTranslator.FromHtml("#17324A"), PressFill = ColorTranslator.FromHtml("#24557A");

    readonly OverlayDrawer _drawer = new();
    int _texture;
    protected int Width { get; private set; } = 1;
    protected int Height { get; private set; } = 1;
    volatile bool _dirty = true; // set from worker threads too (setup's unpack, LAN status)
    // the window's scale against the 1280 x 720 the layouts are written for (Android dp)
    protected float S => Math.Max(0.5f, Math.Min(Width / 1280f, Height / 720f));

    public void Invalidate() => _dirty = true;

    public virtual void OnSurfaceCreated()
    {
        _drawer.Init();
        _texture = 0;
        _dirty = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _dirty = true;
    }

    public virtual void OnDrawFrame()
    {
        Update();
        GLES30.GlViewport(0, 0, Width, Height);
        GLES30.GlClearColor(Background.R / 255f, Background.G / 255f, Background.B / 255f, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        if (_dirty)
        {
            _dirty = false;
            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Gdi.Begin(bmp))
            {
                g.Clear(Background);
                Paint(g);
            }
            OverlayDrawer.Upload(ref _texture, Gdi.ToImage(bmp));
        }
        _drawer.Draw(_texture, 0, 0, Width, Height, Width, Height);
    }

    // once a frame before drawing (poll background work, call Invalidate when something shows differently)
    protected virtual void Update() { }

    protected abstract void Paint(Graphics g);

    // menu input: a move (dx: left / right; dy: up / down, +1 = down), A, B, X / Y, L / R, a click, the mouse wheel, typed text
    public virtual void Move(int dx, int dy) { }
    public virtual void Press() { }
    public virtual void Back() { }
    public virtual void Click(float x, float y) { }
    public virtual void Hover(float x, float y) { }
    public virtual void Wheel(float steps) { }
    public virtual void Text(char c) { }
    public virtual void Key(OpenTK.Windowing.GraphicsLibraryFramework.Keys key) { }

    // ---- painting helpers (sizes in 720p pixels, scaled) ----

    protected Font Font(float size, bool bold = false, bool mono = false) =>
        new(mono ? "Consolas" : "Segoe UI", size * S, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    protected static void DrawText(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment h = StringAlignment.Near,
        StringAlignment v = StringAlignment.Near, bool wrap = true)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter };
        if (!wrap) format.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(text, font, brush, r, format);
    }

    protected float MeasureHeight(Graphics g, string text, Font font, float width) => g.MeasureString(text, font, (int)width).Height;

    // a row or button box: dark; focused: lit with the accent edge; pressed: brighter
    protected void Box(Graphics g, RectangleF r, bool focused, bool pressed = false)
    {
        using GraphicsPath path = Gdi.RoundRect(r, 6 * S);
        using var fill = new SolidBrush(pressed ? PressFill : focused ? FocusFill : BoxFill);
        g.FillPath(fill, path);
        using var edge = new Pen(focused || pressed ? Accent : BoxEdge, (focused || pressed ? 2 : 1) * S);
        g.DrawPath(edge, path);
    }
}
