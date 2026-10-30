using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MphRecomp.App.Gl;
using MphRecomp.App.Platform;

namespace SecondHunt.Desktop.Ui;

// Draws this host's own pictures (the status line, the centred text panel, the lobby / setup / credits pages) over or
// instead of a screen: RGBA images as textured quads in window pixels. One per GL context (Init after it's current).
internal sealed class OverlayDrawer
{
    int _program, _vao, _locScreen, _locRect, _locAlpha;

    const string VertexShader = @"#version 300 es
precision highp float;
uniform vec2 u_screen;
uniform vec4 u_rect;
out vec2 v_uv;
void main()
{
    vec2 c = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    vec2 p = u_rect.xy + c * u_rect.zw;
    gl_Position = vec4(p.x / u_screen.x * 2.0 - 1.0, 1.0 - p.y / u_screen.y * 2.0, 0.0, 1.0);
    v_uv = c;
}";

    const string FragmentShader = @"#version 300 es
precision mediump float;
uniform sampler2D u_tex;
uniform float u_alpha;
in vec2 v_uv;
out vec4 o_col;
void main()
{
    vec4 c = texture(u_tex, v_uv);
    o_col = vec4(c.rgb, c.a * u_alpha);
}";

    public void Init()
    {
        _program = GlUtil.Link(VertexShader, FragmentShader, "overlay");
        _locScreen = GLES30.GlGetUniformLocation(_program, "u_screen");
        _locRect = GLES30.GlGetUniformLocation(_program, "u_rect");
        _locAlpha = GLES30.GlGetUniformLocation(_program, "u_alpha");
        var ids = new int[1];
        GLES30.GlGenVertexArrays(1, ids, 0);
        _vao = ids[0];
    }

    // an image into a texture (made on first use; replaced in place after)
    public static void Upload(ref int texture, TextImage image)
    {
        if (texture == 0)
        {
            var ids = new int[1];
            GLES30.GlGenTextures(1, ids, 0);
            texture = ids[0];
        }
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, texture);
        GLES30.GlPixelStorei(GLES30.GlUnpackAlignment, 4);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, image.Width, image.Height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, image.Rgba);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
    }

    // the texture at (x, y, w, h) window pixels, top-left origin
    public void Draw(int texture, float x, float y, float w, float h, int viewWidth, int viewHeight, float alpha = 1)
    {
        if (texture == 0 || _program == 0) return;
        GLES30.GlViewport(0, 0, viewWidth, viewHeight);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlDisable(GLES30.GlScissorTest);
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE
        GLES30.GlColorMask(true, true, true, true);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlUseProgram(_program);
        GLES30.GlUniform2f(_locScreen, viewWidth, viewHeight);
        GLES30.GlUniform4f(_locRect, x, y, w, h);
        GLES30.GlUniform1f(_locAlpha, alpha);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindSampler(0, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, texture);
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlDrawArrays(GLES30.GlTriangleStrip, 0, 4);
        GLES30.GlBindVertexArray(0);
    }
}

internal sealed record TextImage(byte[] Rgba, int Width, int Height);

// GDI+ pictures: text and simple panels, as straight-alpha RGBA
internal static class Gdi
{
    public static TextImage ToImage(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
            }
            for (int i = 0; i < bytes.Length; i += 4)
            {
                (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]); // BGRA -> RGBA
            }
            return new TextImage(bytes, bmp.Width, bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static Graphics Begin(Bitmap bmp)
    {
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        return g;
    }

    // a block of text on an optional background box: the status line (shadowed, no box) and the centred panel
    public static TextImage Text(string text, Font font, Color color, Color? box, int padX, int padY, bool shadow)
    {
        using var probe = new Bitmap(1, 1);
        SizeF size;
        using (Graphics g = Begin(probe))
        {
            size = g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);
        }
        int w = Math.Max(1, (int)Math.Ceiling(size.Width) + padX * 2 + 4), h = Math.Max(1, (int)Math.Ceiling(size.Height) + padY * 2 + 4);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Begin(bmp))
        {
            g.Clear(Color.Transparent);
            if (box is Color b)
            {
                using var fill = new SolidBrush(b);
                g.FillRectangle(fill, 0, 0, w, h);
            }
            if (shadow)
            {
                using var dark = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                g.DrawString(text, font, dark, padX + 1.5f, padY + 1.5f, StringFormat.GenericTypographic);
            }
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, padX, padY, StringFormat.GenericTypographic);
        }
        return ToImage(bmp);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class GlUtil
{
    public static int Link(string vs, string fs, string name)
    {
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, Compile(GLES30.GlVertexShader, vs, name));
        GLES30.GlAttachShader(p, Compile(GLES30.GlFragmentShader, fs, name));
        GLES30.GlLinkProgram(p);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHDesktop", $"{name} link: " + GLES30.GlGetProgramInfoLog(p));
        return p;
    }

    static int Compile(int type, string src, string name)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHDesktop", $"{name} compile: " + GLES30.GlGetShaderInfoLog(s));
        return s;
    }
}
