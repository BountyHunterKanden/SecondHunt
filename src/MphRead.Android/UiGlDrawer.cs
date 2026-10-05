using Android.Opengl;
using Android.Util;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// Draws a front-end UiDrawList with GLES 3: one tiny program (texture * vertex colour, alpha blended) in canvas pixels.
// Shared by the menus (FrontendRenderer) and the in-game pause menu (PauseOverlay). Make one per GL context, on its
// thread; it leaves depth test and face culling off and blending on.
internal sealed class UiGlDrawer
{
    int _program, _vao, _vbo, _capacity;
    // the list last uploaded: drawn again unchanged (same list, version and size), it isn't uploaded again
    UiDrawList? _lastList;
    int _lastVersion, _lastCount;
    Java.Nio.FloatBuffer? _buffer;
    float[] _floats = Array.Empty<float>();
    readonly Dictionary<int, int> _glTextures = new();
    readonly Dictionary<string, int> _u = new();
    int U(string name) => _u.TryGetValue(name, out int loc) ? loc : _u[name] = GLES30.GlGetUniformLocation(_program, name);

    const string VertexShader = @"#version 300 es
precision highp float;
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
layout(location = 2) in vec4 a_col;
uniform vec2 u_screen;
out vec2 v_uv;
out vec4 v_col;
void main()
{
    gl_Position = vec4(a_pos.x / u_screen.x * 2.0 - 1.0, 1.0 - a_pos.y / u_screen.y * 2.0, 0.0, 1.0);
    v_uv = a_uv;
    v_col = a_col;
}";

    const string FragmentShader = @"#version 300 es
precision mediump float;
uniform sampler2D u_tex;
uniform int u_textured;
in vec2 v_uv;
in vec4 v_col;
out vec4 o_col;
void main()
{
    vec4 c = v_col;
    if (u_textured != 0) c *= texture(u_tex, v_uv);
    if (c.a <= 0.0) discard;
    o_col = c;
}";

    // (re)create the GL objects: call from OnSurfaceCreated -- a new context has none of the old ones
    public void Init()
    {
        _program = Link(VertexShader, FragmentShader);
        var ids = new int[1];
        GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
        _capacity = 0;
        _lastList = null;
        _glTextures.Clear();
        _u.Clear();
    }

    // the list's vertices into the buffer (the VAO and buffer bound)
    void Upload(UiDrawList list, int count, int floats)
    {
        if (_floats.Length < floats) _floats = new float[floats * 2];
        for (int i = 0; i < count; i++)
        {
            UiVertex v = list.Vertices[i];
            int o = i * 8;
            _floats[o] = v.X; _floats[o + 1] = v.Y; _floats[o + 2] = v.U; _floats[o + 3] = v.V;
            _floats[o + 4] = v.R; _floats[o + 5] = v.G; _floats[o + 6] = v.B; _floats[o + 7] = v.A;
        }
        if (_capacity < floats)
        {
            _capacity = floats * 2;
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _capacity * 4, null, GLES30.GlDynamicDraw);
            var bb = Java.Nio.ByteBuffer.AllocateDirect(_capacity * 4);
            bb.Order(Java.Nio.ByteOrder.NativeOrder());
            _buffer = bb.AsFloatBuffer()!;
            GLES30.GlVertexAttribPointer(0, 2, GLES30.GlFloat, false, 32, 0);
            GLES30.GlVertexAttribPointer(1, 2, GLES30.GlFloat, false, 32, 8);
            GLES30.GlVertexAttribPointer(2, 4, GLES30.GlFloat, false, 32, 16);
            GLES30.GlEnableVertexAttribArray(0);
            GLES30.GlEnableVertexAttribArray(1);
            GLES30.GlEnableVertexAttribArray(2);
        }
        _buffer!.Position(0);
        _buffer.Put(_floats, 0, floats);
        _buffer.Position(0);
        GLES30.GlBufferSubData(GLES30.GlArrayBuffer, 0, floats * 4, _buffer);
    }

    // smooth: SMOOTH MENUS (linear filtering for the pixel art; the full-screen backgrounds are always smoothed)
    public void Draw(UiDrawList list, int width, int height, bool smooth)
    {
        int count = list.Vertices.Count;
        if (count == 0 || _program == 0) return;
        int floats = count * 8;
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
        bool same = ReferenceEquals(list, _lastList) && list.Version == _lastVersion && count == _lastCount;
        if (!same)
        {
            _lastList = list;
            _lastVersion = list.Version;
            _lastCount = count;
            Upload(list, count, floats);
        }
        GLES30.GlViewport(0, 0, width, height);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE (the binding names the function and the constant alike)
        // drawn over a game's frame (the pause menu): its stencil passes and sampler objects must not apply here
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlColorMask(true, true, true, true);
        GLES30.GlBindSampler(0, 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlUseProgram(_program);
        GLES30.GlUniform2f(U("u_screen"), width, height);
        GLES30.GlUniform1i(U("u_tex"), 0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        foreach (UiBatch batch in list.Batches)
        {
            if (batch.Blend == UiBlend.Additive) GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOne);
            else GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
            if (batch.Clip.IsNone)
            {
                GLES30.GlDisable(GLES30.GlScissorTest);
            }
            else
            {
                GLES30.GlEnable(GLES30.GlScissorTest);
                GLES30.GlScissor((int)batch.Clip.X, (int)(height - batch.Clip.Y - batch.Clip.H), (int)batch.Clip.W, (int)batch.Clip.H);
            }
            if (batch.TextureId >= 0)
            {
                UiTexture tex = list.Textures[batch.TextureId];
                GLES30.GlBindTexture(GLES30.GlTexture2d, Texture(tex));
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, Wrap(batch.WrapS));
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, Wrap(batch.WrapT));
                // the menu art is pixel art: nearest, as the DS shows it, unless SMOOTH MENUS is on
                int filter = smooth || tex.Key.StartsWith("bg/", StringComparison.Ordinal) ? GLES30.GlLinear : GLES30.GlNearest;
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, filter);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, filter);
                GLES30.GlUniform1i(U("u_textured"), 1);
            }
            else
            {
                GLES30.GlUniform1i(U("u_textured"), 0);
            }
            GLES30.GlDrawArrays(GLES30.GlTriangles, batch.Start, batch.Count);
        }
        GLES30.GlDisable(GLES30.GlScissorTest);
        GLES30.GlBindVertexArray(0);
    }

    static int Wrap(UiWrap w) => w switch
    {
        UiWrap.Repeat => GLES30.GlRepeat,
        UiWrap.Mirror => GLES30.GlMirroredRepeat,
        _ => GLES30.GlClampToEdge
    };

    int Texture(UiTexture tex)
    {
        if (_glTextures.TryGetValue(tex.Id, out int id)) return id;
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        id = ids[0];
        GLES30.GlBindTexture(GLES30.GlTexture2d, id);
        var bb = Java.Nio.ByteBuffer.AllocateDirect(tex.Rgba.Length);
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        bb.Put(tex.Rgba);
        bb.Position(0);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, tex.Width, tex.Height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, bb);
        _glTextures[tex.Id] = id;
        return id;
    }

    static int Link(string vs, string fs)
    {
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, Compile(GLES30.GlVertexShader, vs));
        GLES30.GlAttachShader(p, Compile(GLES30.GlFragmentShader, fs));
        GLES30.GlLinkProgram(p);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHFrontend", "link: " + GLES30.GlGetProgramInfoLog(p));
        return p;
    }

    static int Compile(int type, string src)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHFrontend", "compile: " + GLES30.GlGetShaderInfoLog(s));
        return s;
    }
}
