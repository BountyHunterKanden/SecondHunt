using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MphRecomp.App.Gl;
using MphRecomp.App.Platform;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SecondHunt.Desktop;

// The Linux build's game window and its GL contexts (the Windows build's is ../GlWindow.cs, with WGL; same API). Each
// screen gets a fresh GL context on this window, made current while it draws and destroyed when it closes, as on
// Windows and Android. The contexts are EGL ones: GLFW makes the window's own context with EGL (asked for below), and
// every screen context uses that context's config (8-bit RGBA, 24-bit depth, 8-bit stencil) on the window's surface.
// EGL works the same under X11 and Wayland and with every Mesa and NVIDIA driver.
//
// GL: a desktop 4.3 compatibility context, whose ES 3 compatibility runs the screens' "#version 300 es" shaders as they
// are. A driver without 4.3 gets 3.3 and the shaders' version line adapted (ShaderSourceFilter).
internal sealed unsafe class GlWindow : IDisposable
{
    public NativeWindow Window { get; }
    readonly IntPtr _display, _surface, _config, _bootContext;
    readonly int _major, _minor;
    IntPtr _current;
    // the swap interval each context should have (1 = vsync, 0 = none: SETTINGS frame rate, DesktopApp), and what each
    // context was last given (eglSwapInterval applies to the current context's surface binding)
    int _interval = 1;
    readonly Dictionary<IntPtr, int> _applied = new();
    public string Renderer { get; } = "";

    // GLFW's errors, logged: OpenTK's own callback throws, and an exception thrown through GLFW's native frames aborts
    // the process on Linux (Windows unwinds it). A failed call still returns its failure (a null window, ...)
    static readonly GLFWCallbacks.ErrorCallback OnError = (code, description) => Log.Warn("MPHGl", $"GLFW {code}: {description}");

    public GlWindow(string title, WindowIcon? icon)
    {
        GLFWProvider.SetErrorCallback(OnError);
        GLFWProvider.EnsureInitialized();
        NativeWindow? window = null;
        foreach (Version v in new[] { new Version(4, 3), new Version(3, 3) })
        {
            // NativeWindow sets its own hints over the current ones and never resets them, so this one stays
            GLFW.WindowHint(WindowHintContextApi.ContextCreationApi, ContextApi.EglContextApi);
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
                if (window.WindowPtr == null)
                {
                    window = null;
                    Log.Warn("MPHGl", $"no GL {v} compatibility context");
                    continue;
                }
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
            "This PC's graphics driver has no OpenGL 3.3 through EGL. Install your distribution's Mesa (or the NVIDIA driver) and try again.");
        Window.MakeCurrent();
        _display = GLFW.GetEGLDisplay();
        _surface = GLFW.GetEGLSurface(Window.WindowPtr);
        _bootContext = GLFW.GetEGLContext(Window.WindowPtr);
        _current = _bootContext;
        if (_display == IntPtr.Zero || _surface == IntPtr.Zero || _bootContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("GLFW didn't make the window's GL context with EGL.");
        }
        // the boot context's config, for every screen context
        int id;
        if (!eglQueryContext(_display, _bootContext, ConfigId, &id)) throw Fail("eglQueryContext");
        int* want = stackalloc int[] { ConfigId, id, None };
        IntPtr config;
        int count;
        if (!eglChooseConfig(_display, want, &config, 1, &count) || count < 1) throw Fail("eglChooseConfig");
        _config = config;
        if (!eglBindAPI(OpenGlApi)) throw Fail("eglBindAPI");
        GLES30.Load(name => GLFW.GetProcAddress(name));
        Renderer = $"{GLES30.GlGetString(GLES30.GlRenderer)} | {GLES30.GlGetString(GLES30.GlVersion)} | {GLES30.GlGetString(GLES30.GlVendor)}";
        string extensions = GLES30.GlGetString(GLES30.GlExtensions) ?? "";
        bool es3 = (_major, _minor).CompareTo((4, 3)) >= 0 || extensions.Contains("GL_ARB_ES3_compatibility", StringComparison.Ordinal);
        if (!es3)
        {
            GLES30.ShaderSourceFilter = DesktopShader;
        }
        Log.Info("MPHGl", $"GL {_major}.{_minor} compatibility (EGL, {GLFW.GetPlatform()}): {Renderer}{(es3 ? "" : " (no ES3 compatibility: shaders adapted)")}");
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
        const int MajorVersion = 0x3098, MinorVersion = 0x30FB, ProfileMask = 0x30FD, CompatibilityProfile = 0x0002;
        int* attribs = stackalloc int[] { MajorVersion, _major, MinorVersion, _minor, ProfileMask, CompatibilityProfile, None };
        IntPtr context = eglCreateContext(_display, _config, IntPtr.Zero, attribs);
        if (context == IntPtr.Zero) throw Fail("eglCreateContext");
        MakeCurrent(context);
        ApplyInterval(); // vsync, per context (SetVsync)
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
        if (!eglMakeCurrent(_display, _surface, _surface, context)) throw Fail("eglMakeCurrent");
        _current = context;
        if (context != _bootContext && _applied.TryGetValue(context, out int had) && had != _interval) ApplyInterval();
    }

    // vsync on or off for every screen's context (each gets it as it's next made current)
    public void SetVsync(bool on)
    {
        _interval = on ? 1 : 0;
        if (_current != _bootContext && _applied.ContainsKey(_current)) ApplyInterval();
    }

    void ApplyInterval()
    {
        bool ok = eglSwapInterval(_display, _interval);
        _applied[_current] = _interval;
        Log.Info("MPHGl", $"swap interval {_interval} {(ok ? "set" : $"refused (EGL error 0x{eglGetError():X})")}");
    }

    // frees everything the screen made in it
    public void DeleteContext(IntPtr context)
    {
        if (context == IntPtr.Zero) return;
        if (_current == context) MakeCurrent(_bootContext);
        _applied.Remove(context);
        eglDestroyContext(_display, context);
    }

    public void Swap() => eglSwapBuffers(_display, _surface);

    // shows the window without taking focus: a test run (--shot, --quit-after, --script) mustn't take the owner's focus
    // or mouse while they work (owner 2026-10-24). X11 and Wayland have no "behind the others", so it opens unfocused
    public void ShowBehind()
    {
        GLFW.SetWindowAttrib(Window.WindowPtr, WindowAttribute.FocusOnShow, false);
        GLFW.ShowWindow(Window.WindowPtr);
    }

    // Windows' "Not Responding" nudge (../GlWindow.cs); Linux desktops only ping a window that handles no events, and
    // DesktopApp polls events every frame
    public void KeepResponsive() { }

    public void Dispose()
    {
        Window.Dispose();
    }

    static InvalidOperationException Fail(string what) => new($"{what} failed (EGL error 0x{eglGetError():X})");

    const int ConfigId = 0x3028, None = 0x3038, OpenGlApi = 0x30A2;
    const string Egl = "libEGL.so.1";
    [DllImport(Egl)] static extern int eglGetError();
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglQueryContext(IntPtr dpy, IntPtr ctx, int attribute, int* value);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglChooseConfig(IntPtr dpy, int* attribs, IntPtr* configs, int size, int* count);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglBindAPI(int api);
    [DllImport(Egl)] static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int* attribs);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglSwapBuffers(IntPtr dpy, IntPtr surface);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglSwapInterval(IntPtr dpy, int interval);
    [DllImport(Egl)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool eglDestroyContext(IntPtr dpy, IntPtr ctx);
}
