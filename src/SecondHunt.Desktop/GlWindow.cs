using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MphRecomp.App.Gl;
using MphRecomp.App.Platform;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SecondHunt.Desktop;

// The game window and its GL contexts. The screens in MphRecomp.App are written the way Android runs them: each one on
// its own GL context, which it fills as it goes (meshes, textures, programs) and which is thrown away with the screen
// (a GLSurfaceView's EGL context dies with its activity). So each screen here gets a fresh WGL context on this window
// (same pixel format: 8-bit RGBA, 24-bit depth, 8-bit stencil), made current while it draws and deleted when it closes,
// which frees everything it made. The window's own GLFW context only loads the GL entry points.
//
// GL: a desktop 4.3 compatibility context, whose ES 3 compatibility runs the screens' "#version 300 es" shaders as they
// are. A driver without 4.3 gets 3.3 and the shaders' version line adapted (ShaderSourceFilter).
internal sealed unsafe class GlWindow : IDisposable
{
    public NativeWindow Window { get; }
    readonly IntPtr _hdc, _bootContext;
    readonly int _major, _minor;
    readonly delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr> _createContextAttribs;
    readonly delegate* unmanaged<int, int> _swapInterval;
    IntPtr _current;
    public string Renderer { get; } = "";

    public GlWindow(string title, WindowIcon? icon)
    {
        NativeWindow? window = null;
        foreach (Version v in new[] { new Version(4, 3), new Version(3, 3) })
        {
            var settings = new NativeWindowSettings
            {
                Title = title,
                ClientSize = new OpenTK.Mathematics.Vector2i(1280, 720),
                MinimumClientSize = new OpenTK.Mathematics.Vector2i(320, 240),
                API = ContextAPI.OpenGL,
                APIVersion = v,
                Profile = ContextProfile.Compatability,
                Flags = ContextFlags.Default,
                RedBits = 8, GreenBits = 8, BlueBits = 8, AlphaBits = 8,
                DepthBits = 24,
                StencilBits = 8,
                StartVisible = false,
                Vsync = VSyncMode.Off,
                Icon = icon,
                AutoLoadBindings = false,
            };
            try
            {
                window = new NativeWindow(settings);
                _major = v.Major;
                _minor = v.Minor;
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("MPHGl", $"no GL {v} compatibility context: {ex.Message}");
            }
        }
        Window = window ?? throw new InvalidOperationException(
            "This PC's graphics driver has no OpenGL 3.3. Update the graphics driver and try again.");
        Window.MakeCurrent();
        _hdc = wglGetCurrentDC();
        _bootContext = wglGetCurrentContext();
        _current = _bootContext;
        _createContextAttribs = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)wglGetProcAddress("wglCreateContextAttribsARB");
        _swapInterval = (delegate* unmanaged<int, int>)wglGetProcAddress("wglSwapIntervalEXT");
        if (_createContextAttribs == null)
        {
            throw new InvalidOperationException("The graphics driver doesn't offer wglCreateContextAttribsARB.");
        }
        GLES30.Load(name => GLFW.GetProcAddress(name));
        Renderer = $"{GLES30.GlGetString(GLES30.GlRenderer)} | {GLES30.GlGetString(GLES30.GlVersion)} | {GLES30.GlGetString(GLES30.GlVendor)}";
        string extensions = GLES30.GlGetString(GLES30.GlExtensions) ?? "";
        bool es3 = (_major, _minor).CompareTo((4, 3)) >= 0 || extensions.Contains("GL_ARB_ES3_compatibility", StringComparison.Ordinal);
        if (!es3)
        {
            GLES30.ShaderSourceFilter = DesktopShader;
        }
        Log.Info("MPHGl", $"GL {_major}.{_minor} compatibility: {Renderer}{(es3 ? "" : " (no ES3 compatibility: shaders adapted)")}");
    }

    // "#version 300 es" -> GLSL 3.30 (the same language; the precision statements mean nothing there)
    static string DesktopShader(string source)
    {
        if (!source.StartsWith("#version 300 es", StringComparison.Ordinal)) return source;
        string body = source["#version 300 es".Length..];
        body = Regex.Replace(body, @"^\s*precision\s+\w+\s+\w+\s*;", "", RegexOptions.Multiline);
        return "#version 330" + body;
    }

    // a new GL context for a screen, made current
    public IntPtr CreateContext()
    {
        const int MajorVersion = 0x2091, MinorVersion = 0x2092, ProfileMask = 0x9126, CompatibilityProfile = 0x0002;
        int* attribs = stackalloc int[] { MajorVersion, _major, MinorVersion, _minor, ProfileMask, CompatibilityProfile, 0 };
        IntPtr context = _createContextAttribs(_hdc, IntPtr.Zero, attribs);
        if (context == IntPtr.Zero)
        {
            throw new InvalidOperationException($"wglCreateContextAttribsARB failed (error {Marshal.GetLastWin32Error()})");
        }
        MakeCurrent(context);
        if (_swapInterval != null) _swapInterval(1); // vsync, per context
        // ES 3 behaviour a desktop context has off by default: shader-set point sizes, seamless cube maps
        GLES30.GlEnable(0x8642); // GL_PROGRAM_POINT_SIZE
        GLES30.GlEnable(0x884F); // GL_TEXTURE_CUBE_MAP_SEAMLESS
        var bits = new int[1];
        GLES30.GlGetIntegerv(0x0D56, bits, 0); // GL_DEPTH_BITS
        int depth = bits[0];
        GLES30.GlGetIntegerv(0x0D57, bits, 0); // GL_STENCIL_BITS
        Log.Info("MPHGl", $"screen context: depth {depth} bits, stencil {bits[0]} bits, error 0x{GLES30.GlGetError():X}");
        return context;
    }

    public void MakeCurrent(IntPtr context)
    {
        if (context == _current) return;
        if (!wglMakeCurrent(_hdc, context))
        {
            throw new InvalidOperationException($"wglMakeCurrent failed (error {Marshal.GetLastWin32Error()})");
        }
        _current = context;
    }

    // frees everything the screen made in it
    public void DeleteContext(IntPtr context)
    {
        if (context == IntPtr.Zero) return;
        if (_current == context) MakeCurrent(_bootContext);
        wglDeleteContext(context);
    }

    public void Swap() => SwapBuffers(_hdc);

    public void Dispose()
    {
        Window.Dispose();
    }

    [DllImport("opengl32.dll")] static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll")] static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll", SetLastError = true)] static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] static extern IntPtr wglGetProcAddress(string name);
    [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr hdc);
}
