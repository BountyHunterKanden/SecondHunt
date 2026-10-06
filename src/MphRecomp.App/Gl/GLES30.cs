using System.Runtime.InteropServices;
using System.Text;

namespace MphRecomp.App.Gl;

// The OpenGL ES 3.0 API every renderer in this layer draws with, bound straight to the driver's own entry points.
// Android: libGLESv3.so on the GLSurfaceView's EGL context. Windows: the desktop GL context's functions (GL 4.3 is a
// superset of ES 3.0 with the same entry points; its ES3 compatibility runs "#version 300 es" shaders as they are).
// Each call is one native call through a function pointer: no JNI and no Java arrays (Android's own GLES30 binding
// copied every array argument into a new Java array, which is why GlNative existed). Names and argument shapes follow
// Android's GLES30 (GlFoo(..., int[] array, int offset)), so the renderers read the same on both platforms; arrays are
// pinned for the call, never copied.
//
// The host calls Load once its first context is current, with the platform's symbol lookup.
public static unsafe class GLES30
{
    // ---------------------------------------------------------------- constants (GL ES 3.0 values)

    public const int GlZero = 0, GlOne = 1;
    public const int GlTriangles = 0x0004, GlTriangleStrip = 0x0005, GlTriangleFan = 0x0006;
    public const int GlNever = 0x0200, GlLess = 0x0201, GlEqual = 0x0202, GlLequal = 0x0203, GlGreater = 0x0204,
        GlNotequal = 0x0205, GlGequal = 0x0206, GlAlways = 0x0207;
    public const int GlSrcColor = 0x0300, GlOneMinusSrcColor = 0x0301, GlSrcAlpha = 0x0302, GlOneMinusSrcAlpha = 0x0303,
        GlDstAlpha = 0x0304, GlOneMinusDstAlpha = 0x0305, GlDstColor = 0x0306, GlOneMinusDstColor = 0x0307;
    public const int GlFront = 0x0404, GlBack = 0x0405, GlCw = 0x0900, GlCcw = 0x0901;
    public const int GlDepthTest = 0x0B71, GlStencilTest = 0x0B90, GlBlend = 0x0BE2,
        GlScissorTest = 0x0C11, GlPolygonOffsetFill = 0x8037;
    public const int GlUnpackAlignment = 0x0CF5, GlPackAlignment = 0x0D05;
    public const int GlTexture2d = 0x0DE1;
    public const int GlUnsignedByte = 0x1401, GlUnsignedShort = 0x1403, GlFloat = 0x1406;
    public const int GlRgb = 0x1907, GlRgba = 0x1908, GlRgba8 = 0x8058, GlDepthComponent24 = 0x81A6, GlDepth24Stencil8 = 0x88F0;
    public const int GlKeep = 0x1E00, GlReplace = 0x1E01;
    public const int GlVendor = 0x1F00, GlRenderer = 0x1F01, GlVersion = 0x1F02, GlExtensions = 0x1F03;
    public const int GlNearest = 0x2600, GlLinear = 0x2601, GlLinearMipmapLinear = 0x2703;
    public const int GlTextureMagFilter = 0x2800, GlTextureMinFilter = 0x2801, GlTextureWrapS = 0x2802, GlTextureWrapT = 0x2803;
    public const int GlRepeat = 0x2901, GlClampToEdge = 0x812F, GlMirroredRepeat = 0x8370;
    public const int GlDepthBufferBit = 0x0100, GlStencilBufferBit = 0x0400, GlColorBufferBit = 0x4000;
    public const int GlTexture0 = 0x84C0, GlTexture1 = 0x84C1;
    public const int GlProgramBinaryLength = 0x8741;
    public const int GlArrayBuffer = 0x8892, GlStaticDraw = 0x88E4, GlDynamicDraw = 0x88E8;
    public const int GlFragmentShader = 0x8B30, GlVertexShader = 0x8B31, GlCompileStatus = 0x8B81, GlLinkStatus = 0x8B82,
        GlInfoLogLength = 0x8B84;
    public const int GlReadFramebuffer = 0x8CA8, GlDrawFramebuffer = 0x8CA9, GlFramebufferComplete = 0x8CD5,
        GlColorAttachment0 = 0x8CE0, GlDepthAttachment = 0x8D00, GlDepthStencilAttachment = 0x821A,
        GlFramebuffer = 0x8D40, GlRenderbuffer = 0x8D41;

    // ---------------------------------------------------------------- loading

    public static bool Loaded { get; private set; }

    // Windows contexts without ES3 compatibility (before GL 4.3) need the shader text adapted; set by that host
    public static Func<string, string>? ShaderSourceFilter { get; set; }

    // getProc: the driver's address for a GL function name, or 0. Every function this binding offers must resolve,
    // except the few with a fallback below.
    public static void Load(Func<string, IntPtr> getProc)
    {
        IntPtr Need(string name)
        {
            IntPtr p = getProc(name);
            return p != IntPtr.Zero ? p : throw new EntryPointNotFoundException($"GL function {name} not found");
        }
        p_glActiveTexture = (delegate* unmanaged<int, void>)Need("glActiveTexture");
        p_glAttachShader = (delegate* unmanaged<int, int, void>)Need("glAttachShader");
        p_glBeginQuery = (delegate* unmanaged<int, int, void>)Need("glBeginQuery");
        p_glBindBuffer = (delegate* unmanaged<int, int, void>)Need("glBindBuffer");
        p_glBindFramebuffer = (delegate* unmanaged<int, int, void>)Need("glBindFramebuffer");
        p_glBindRenderbuffer = (delegate* unmanaged<int, int, void>)Need("glBindRenderbuffer");
        p_glBindSampler = (delegate* unmanaged<int, int, void>)Need("glBindSampler");
        p_glBindTexture = (delegate* unmanaged<int, int, void>)Need("glBindTexture");
        p_glBindVertexArray = (delegate* unmanaged<int, void>)Need("glBindVertexArray");
        p_glBlendFunc = (delegate* unmanaged<int, int, void>)Need("glBlendFunc");
        p_glBlitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void>)Need("glBlitFramebuffer");
        p_glBufferData = (delegate* unmanaged<int, nint, void*, int, void>)Need("glBufferData");
        p_glBufferSubData = (delegate* unmanaged<int, nint, nint, void*, void>)Need("glBufferSubData");
        p_glCheckFramebufferStatus = (delegate* unmanaged<int, int>)Need("glCheckFramebufferStatus");
        p_glClear = (delegate* unmanaged<int, void>)Need("glClear");
        p_glClearColor = (delegate* unmanaged<float, float, float, float, void>)Need("glClearColor");
        p_glClearStencil = (delegate* unmanaged<int, void>)Need("glClearStencil");
        p_glColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)Need("glColorMask");
        p_glCompileShader = (delegate* unmanaged<int, void>)Need("glCompileShader");
        p_glCreateProgram = (delegate* unmanaged<int>)Need("glCreateProgram");
        p_glCreateShader = (delegate* unmanaged<int, int>)Need("glCreateShader");
        p_glCullFace = (delegate* unmanaged<int, void>)Need("glCullFace");
        p_glDeleteBuffers = (delegate* unmanaged<int, int*, void>)Need("glDeleteBuffers");
        p_glDeleteFramebuffers = (delegate* unmanaged<int, int*, void>)Need("glDeleteFramebuffers");
        p_glDeleteProgram = (delegate* unmanaged<int, void>)Need("glDeleteProgram");
        p_glDeleteQueries = (delegate* unmanaged<int, int*, void>)Need("glDeleteQueries");
        p_glDeleteRenderbuffers = (delegate* unmanaged<int, int*, void>)Need("glDeleteRenderbuffers");
        p_glDeleteSamplers = (delegate* unmanaged<int, int*, void>)Need("glDeleteSamplers");
        p_glDeleteShader = (delegate* unmanaged<int, void>)Need("glDeleteShader");
        p_glDeleteTextures = (delegate* unmanaged<int, int*, void>)Need("glDeleteTextures");
        p_glDeleteVertexArrays = (delegate* unmanaged<int, int*, void>)Need("glDeleteVertexArrays");
        p_glDepthFunc = (delegate* unmanaged<int, void>)Need("glDepthFunc");
        p_glDepthMask = (delegate* unmanaged<byte, void>)Need("glDepthMask");
        p_glDisable = (delegate* unmanaged<int, void>)Need("glDisable");
        p_glDrawArrays = (delegate* unmanaged<int, int, int, void>)Need("glDrawArrays");
        p_glEnable = (delegate* unmanaged<int, void>)Need("glEnable");
        p_glEnableVertexAttribArray = (delegate* unmanaged<int, void>)Need("glEnableVertexAttribArray");
        p_glEndQuery = (delegate* unmanaged<int, void>)Need("glEndQuery");
        p_glFramebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>)Need("glFramebufferRenderbuffer");
        p_glFramebufferTexture2D = (delegate* unmanaged<int, int, int, int, int, void>)Need("glFramebufferTexture2D");
        p_glFrontFace = (delegate* unmanaged<int, void>)Need("glFrontFace");
        p_glGenBuffers = (delegate* unmanaged<int, int*, void>)Need("glGenBuffers");
        p_glGenFramebuffers = (delegate* unmanaged<int, int*, void>)Need("glGenFramebuffers");
        p_glGenQueries = (delegate* unmanaged<int, int*, void>)Need("glGenQueries");
        p_glGenRenderbuffers = (delegate* unmanaged<int, int*, void>)Need("glGenRenderbuffers");
        p_glGenSamplers = (delegate* unmanaged<int, int*, void>)Need("glGenSamplers");
        p_glGenTextures = (delegate* unmanaged<int, int*, void>)Need("glGenTextures");
        p_glGenVertexArrays = (delegate* unmanaged<int, int*, void>)Need("glGenVertexArrays");
        p_glGenerateMipmap = (delegate* unmanaged<int, void>)Need("glGenerateMipmap");
        p_glGetError = (delegate* unmanaged<int>)Need("glGetError");
        p_glGetIntegerv = (delegate* unmanaged<int, int*, void>)Need("glGetIntegerv");
        p_glGetProgramBinary = (delegate* unmanaged<int, int, int*, int*, void*, void>)Need("glGetProgramBinary");
        p_glGetProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)Need("glGetProgramInfoLog");
        p_glGetProgramiv = (delegate* unmanaged<int, int, int*, void>)Need("glGetProgramiv");
        p_glGetQueryObjectuiv = (delegate* unmanaged<int, int, int*, void>)Need("glGetQueryObjectuiv");
        p_glGetShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)Need("glGetShaderInfoLog");
        p_glGetShaderiv = (delegate* unmanaged<int, int, int*, void>)Need("glGetShaderiv");
        p_glGetString = (delegate* unmanaged<int, byte*>)Need("glGetString");
        p_glGetUniformLocation = (delegate* unmanaged<int, byte*, int>)Need("glGetUniformLocation");
        p_glLinkProgram = (delegate* unmanaged<int, void>)Need("glLinkProgram");
        p_glPixelStorei = (delegate* unmanaged<int, int, void>)Need("glPixelStorei");
        p_glPolygonOffset = (delegate* unmanaged<float, float, void>)Need("glPolygonOffset");
        p_glProgramBinary = (delegate* unmanaged<int, int, void*, int, void>)Need("glProgramBinary");
        p_glProgramParameteri = (delegate* unmanaged<int, int, int, void>)Need("glProgramParameteri");
        p_glReadPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)Need("glReadPixels");
        p_glRenderbufferStorage = (delegate* unmanaged<int, int, int, int, void>)Need("glRenderbufferStorage");
        p_glRenderbufferStorageMultisample = (delegate* unmanaged<int, int, int, int, int, void>)Need("glRenderbufferStorageMultisample");
        p_glSamplerParameteri = (delegate* unmanaged<int, int, int, void>)Need("glSamplerParameteri");
        p_glScissor = (delegate* unmanaged<int, int, int, int, void>)Need("glScissor");
        p_glShaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)Need("glShaderSource");
        p_glStencilFunc = (delegate* unmanaged<int, int, int, void>)Need("glStencilFunc");
        p_glStencilMask = (delegate* unmanaged<int, void>)Need("glStencilMask");
        p_glStencilOp = (delegate* unmanaged<int, int, int, void>)Need("glStencilOp");
        p_glTexImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)Need("glTexImage2D");
        p_glTexParameteri = (delegate* unmanaged<int, int, int, void>)Need("glTexParameteri");
        p_glTexSubImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)Need("glTexSubImage2D");
        p_glUniform1f = (delegate* unmanaged<int, float, void>)Need("glUniform1f");
        p_glUniform1i = (delegate* unmanaged<int, int, void>)Need("glUniform1i");
        p_glUniform2f = (delegate* unmanaged<int, float, float, void>)Need("glUniform2f");
        p_glUniform3f = (delegate* unmanaged<int, float, float, float, void>)Need("glUniform3f");
        p_glUniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Need("glUniform4f");
        p_glUniform1iv = (delegate* unmanaged<int, int, int*, void>)Need("glUniform1iv");
        p_glUniform2iv = (delegate* unmanaged<int, int, int*, void>)Need("glUniform2iv");
        p_glUniform4iv = (delegate* unmanaged<int, int, int*, void>)Need("glUniform4iv");
        p_glUniform2fv = (delegate* unmanaged<int, int, float*, void>)Need("glUniform2fv");
        p_glUniform3fv = (delegate* unmanaged<int, int, float*, void>)Need("glUniform3fv");
        p_glUniform4fv = (delegate* unmanaged<int, int, float*, void>)Need("glUniform4fv");
        p_glUniformMatrix3fv = (delegate* unmanaged<int, int, byte, float*, void>)Need("glUniformMatrix3fv");
        p_glUniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)Need("glUniformMatrix4fv");
        p_glUseProgram = (delegate* unmanaged<int, void>)Need("glUseProgram");
        p_glVertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, nint, void>)Need("glVertexAttribPointer");
        p_glViewport = (delegate* unmanaged<int, int, int, int, void>)Need("glViewport");
        // glDepthRangef is ES / GL 4.1; older desktop contexts only have the double version
        IntPtr rangef = getProc("glDepthRangef");
        if (rangef != IntPtr.Zero)
        {
            p_glDepthRangef = (delegate* unmanaged<float, float, void>)rangef;
        }
        else
        {
            p_glDepthRange = (delegate* unmanaged<double, double, void>)Need("glDepthRange");
        }
        Loaded = true;
    }

    // ---------------------------------------------------------------- calls

    static delegate* unmanaged<int, void> p_glActiveTexture;
    public static void GlActiveTexture(int texture) => p_glActiveTexture(texture);

    static delegate* unmanaged<int, int, void> p_glAttachShader;
    public static void GlAttachShader(int program, int shader) => p_glAttachShader(program, shader);

    static delegate* unmanaged<int, int, void> p_glBeginQuery;
    public static void GlBeginQuery(int target, int id) => p_glBeginQuery(target, id);

    static delegate* unmanaged<int, int, void> p_glBindBuffer;
    public static void GlBindBuffer(int target, int buffer) => p_glBindBuffer(target, buffer);

    static delegate* unmanaged<int, int, void> p_glBindFramebuffer;
    public static void GlBindFramebuffer(int target, int framebuffer) => p_glBindFramebuffer(target, framebuffer);

    static delegate* unmanaged<int, int, void> p_glBindRenderbuffer;
    public static void GlBindRenderbuffer(int target, int renderbuffer) => p_glBindRenderbuffer(target, renderbuffer);

    static delegate* unmanaged<int, int, void> p_glBindSampler;
    public static void GlBindSampler(int unit, int sampler) => p_glBindSampler(unit, sampler);

    static delegate* unmanaged<int, int, void> p_glBindTexture;
    public static void GlBindTexture(int target, int texture) => p_glBindTexture(target, texture);

    static delegate* unmanaged<int, void> p_glBindVertexArray;
    public static void GlBindVertexArray(int array) => p_glBindVertexArray(array);

    static delegate* unmanaged<int, int, void> p_glBlendFunc;
    public static void GlBlendFunc(int sfactor, int dfactor) => p_glBlendFunc(sfactor, dfactor);

    static delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void> p_glBlitFramebuffer;
    public static void GlBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1,
        int mask, int filter) => p_glBlitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter);

    // size in bytes; data null = allocate only
    static delegate* unmanaged<int, nint, void*, int, void> p_glBufferData;
    public static void GlBufferData(int target, int size, float[]? data, int usage)
    {
        fixed (float* p = data)
        {
            p_glBufferData(target, size, p, usage);
        }
    }

    // offset and size in bytes, from the start of data
    static delegate* unmanaged<int, nint, nint, void*, void> p_glBufferSubData;
    public static void GlBufferSubData(int target, int offset, int size, float[] data)
    {
        fixed (float* p = data)
        {
            p_glBufferSubData(target, offset, size, p);
        }
    }

    internal static void BufferSubData(int target, nint offset, nint size, void* data) => p_glBufferSubData(target, offset, size, data);

    static delegate* unmanaged<int, int> p_glCheckFramebufferStatus;
    public static int GlCheckFramebufferStatus(int target) => p_glCheckFramebufferStatus(target);

    static delegate* unmanaged<int, void> p_glClear;
    public static void GlClear(int mask) => p_glClear(mask);

    static delegate* unmanaged<float, float, float, float, void> p_glClearColor;
    public static void GlClearColor(float r, float g, float b, float a) => p_glClearColor(r, g, b, a);

    static delegate* unmanaged<int, void> p_glClearStencil;
    public static void GlClearStencil(int s) => p_glClearStencil(s);

    static delegate* unmanaged<byte, byte, byte, byte, void> p_glColorMask;
    public static void GlColorMask(bool r, bool g, bool b, bool a) => p_glColorMask(B(r), B(g), B(b), B(a));

    static delegate* unmanaged<int, void> p_glCompileShader;
    public static void GlCompileShader(int shader) => p_glCompileShader(shader);

    static delegate* unmanaged<int> p_glCreateProgram;
    public static int GlCreateProgram() => p_glCreateProgram();

    static delegate* unmanaged<int, int> p_glCreateShader;
    public static int GlCreateShader(int type) => p_glCreateShader(type);

    static delegate* unmanaged<int, void> p_glCullFace;
    public static void GlCullFace(int mode) => p_glCullFace(mode);

    static delegate* unmanaged<int, int*, void> p_glDeleteBuffers;
    public static void GlDeleteBuffers(int n, int[] buffers, int offset) { fixed (int* p = buffers) p_glDeleteBuffers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glDeleteFramebuffers;
    public static void GlDeleteFramebuffers(int n, int[] framebuffers, int offset) { fixed (int* p = framebuffers) p_glDeleteFramebuffers(n, p + offset); }

    static delegate* unmanaged<int, void> p_glDeleteProgram;
    public static void GlDeleteProgram(int program) => p_glDeleteProgram(program);

    static delegate* unmanaged<int, int*, void> p_glDeleteQueries;
    public static void GlDeleteQueries(int n, int[] ids, int offset) { fixed (int* p = ids) p_glDeleteQueries(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glDeleteRenderbuffers;
    public static void GlDeleteRenderbuffers(int n, int[] renderbuffers, int offset) { fixed (int* p = renderbuffers) p_glDeleteRenderbuffers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glDeleteSamplers;
    public static void GlDeleteSamplers(int n, int[] samplers, int offset) { fixed (int* p = samplers) p_glDeleteSamplers(n, p + offset); }

    static delegate* unmanaged<int, void> p_glDeleteShader;
    public static void GlDeleteShader(int shader) => p_glDeleteShader(shader);

    static delegate* unmanaged<int, int*, void> p_glDeleteTextures;
    public static void GlDeleteTextures(int n, int[] textures, int offset) { fixed (int* p = textures) p_glDeleteTextures(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glDeleteVertexArrays;
    public static void GlDeleteVertexArrays(int n, int[] arrays, int offset) { fixed (int* p = arrays) p_glDeleteVertexArrays(n, p + offset); }

    static delegate* unmanaged<int, void> p_glDepthFunc;
    public static void GlDepthFunc(int func) => p_glDepthFunc(func);

    static delegate* unmanaged<byte, void> p_glDepthMask;
    public static void GlDepthMask(bool flag) => p_glDepthMask(B(flag));

    static delegate* unmanaged<float, float, void> p_glDepthRangef;
    static delegate* unmanaged<double, double, void> p_glDepthRange;
    public static void GlDepthRangef(float n, float f)
    {
        if (p_glDepthRangef != null)
        {
            p_glDepthRangef(n, f);
        }
        else
        {
            p_glDepthRange(n, f);
        }
    }

    static delegate* unmanaged<int, void> p_glDisable;
    public static void GlDisable(int cap) => p_glDisable(cap);

    static delegate* unmanaged<int, int, int, void> p_glDrawArrays;
    public static void GlDrawArrays(int mode, int first, int count) => p_glDrawArrays(mode, first, count);

    static delegate* unmanaged<int, void> p_glEnable;
    public static void GlEnable(int cap) => p_glEnable(cap);

    static delegate* unmanaged<int, void> p_glEnableVertexAttribArray;
    public static void GlEnableVertexAttribArray(int index) => p_glEnableVertexAttribArray(index);

    static delegate* unmanaged<int, void> p_glEndQuery;
    public static void GlEndQuery(int target) => p_glEndQuery(target);

    static delegate* unmanaged<int, int, int, int, void> p_glFramebufferRenderbuffer;
    public static void GlFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer)
        => p_glFramebufferRenderbuffer(target, attachment, renderbuffertarget, renderbuffer);

    static delegate* unmanaged<int, int, int, int, int, void> p_glFramebufferTexture2D;
    public static void GlFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
        => p_glFramebufferTexture2D(target, attachment, textarget, texture, level);

    static delegate* unmanaged<int, void> p_glFrontFace;
    public static void GlFrontFace(int mode) => p_glFrontFace(mode);

    static delegate* unmanaged<int, int*, void> p_glGenBuffers;
    public static void GlGenBuffers(int n, int[] buffers, int offset) { fixed (int* p = buffers) p_glGenBuffers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenFramebuffers;
    public static void GlGenFramebuffers(int n, int[] framebuffers, int offset) { fixed (int* p = framebuffers) p_glGenFramebuffers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenQueries;
    public static void GlGenQueries(int n, int[] ids, int offset) { fixed (int* p = ids) p_glGenQueries(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenRenderbuffers;
    public static void GlGenRenderbuffers(int n, int[] renderbuffers, int offset) { fixed (int* p = renderbuffers) p_glGenRenderbuffers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenSamplers;
    public static void GlGenSamplers(int n, int[] samplers, int offset) { fixed (int* p = samplers) p_glGenSamplers(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenTextures;
    public static void GlGenTextures(int n, int[] textures, int offset) { fixed (int* p = textures) p_glGenTextures(n, p + offset); }

    static delegate* unmanaged<int, int*, void> p_glGenVertexArrays;
    public static void GlGenVertexArrays(int n, int[] arrays, int offset) { fixed (int* p = arrays) p_glGenVertexArrays(n, p + offset); }

    static delegate* unmanaged<int, void> p_glGenerateMipmap;
    public static void GlGenerateMipmap(int target) => p_glGenerateMipmap(target);

    static delegate* unmanaged<int> p_glGetError;
    public static int GlGetError() => p_glGetError();

    static delegate* unmanaged<int, int*, void> p_glGetIntegerv;
    public static void GlGetIntegerv(int pname, int[] values, int offset) { fixed (int* p = values) p_glGetIntegerv(pname, p + offset); }

    static delegate* unmanaged<int, int, int*, int*, void*, void> p_glGetProgramBinary;
    public static void GlGetProgramBinary(int program, int bufSize, int[] length, int lengthOffset, int[] binaryFormat,
        int binaryFormatOffset, byte[] binary)
    {
        fixed (int* l = length, f = binaryFormat)
        fixed (byte* b = binary)
        {
            p_glGetProgramBinary(program, bufSize, l + lengthOffset, f + binaryFormatOffset, b);
        }
    }

    static delegate* unmanaged<int, int, int*, byte*, void> p_glGetProgramInfoLog;
    public static string GlGetProgramInfoLog(int program)
    {
        var len = new int[1];
        GlGetProgramiv(program, GlInfoLogLength, len, 0);
        if (len[0] <= 1) return "";
        var buf = new byte[len[0]];
        int written;
        fixed (byte* b = buf)
        {
            p_glGetProgramInfoLog(program, buf.Length, &written, b);
        }
        return Encoding.UTF8.GetString(buf, 0, Math.Clamp(written, 0, buf.Length));
    }

    static delegate* unmanaged<int, int, int*, void> p_glGetProgramiv;
    public static void GlGetProgramiv(int program, int pname, int[] values, int offset) { fixed (int* p = values) p_glGetProgramiv(program, pname, p + offset); }

    static delegate* unmanaged<int, int, int*, void> p_glGetQueryObjectuiv;
    public static void GlGetQueryObjectuiv(int id, int pname, int[] values, int offset) { fixed (int* p = values) p_glGetQueryObjectuiv(id, pname, p + offset); }

    static delegate* unmanaged<int, int, int*, byte*, void> p_glGetShaderInfoLog;
    public static string GlGetShaderInfoLog(int shader)
    {
        var len = new int[1];
        GlGetShaderiv(shader, GlInfoLogLength, len, 0);
        if (len[0] <= 1) return "";
        var buf = new byte[len[0]];
        int written;
        fixed (byte* b = buf)
        {
            p_glGetShaderInfoLog(shader, buf.Length, &written, b);
        }
        return Encoding.UTF8.GetString(buf, 0, Math.Clamp(written, 0, buf.Length));
    }

    static delegate* unmanaged<int, int, int*, void> p_glGetShaderiv;
    public static void GlGetShaderiv(int shader, int pname, int[] values, int offset) { fixed (int* p = values) p_glGetShaderiv(shader, pname, p + offset); }

    static delegate* unmanaged<int, byte*> p_glGetString;
    public static string? GlGetString(int name) => Marshal.PtrToStringUTF8((IntPtr)p_glGetString(name));

    static delegate* unmanaged<int, byte*, int> p_glGetUniformLocation;
    public static int GlGetUniformLocation(int program, string name)
    {
        int n = Encoding.UTF8.GetByteCount(name);
        Span<byte> s = n < 256 ? stackalloc byte[n + 1] : new byte[n + 1];
        Encoding.UTF8.GetBytes(name, s);
        s[n] = 0;
        fixed (byte* p = s)
        {
            return p_glGetUniformLocation(program, p);
        }
    }

    static delegate* unmanaged<int, void> p_glLinkProgram;
    public static void GlLinkProgram(int program) => p_glLinkProgram(program);

    static delegate* unmanaged<int, int, void> p_glPixelStorei;
    public static void GlPixelStorei(int pname, int param) => p_glPixelStorei(pname, param);

    static delegate* unmanaged<float, float, void> p_glPolygonOffset;
    public static void GlPolygonOffset(float factor, float units) => p_glPolygonOffset(factor, units);

    // length bytes of binary from offset
    static delegate* unmanaged<int, int, void*, int, void> p_glProgramBinary;
    public static void GlProgramBinary(int program, int binaryFormat, byte[] binary, int offset, int length)
    {
        fixed (byte* b = binary)
        {
            p_glProgramBinary(program, binaryFormat, b + offset, length);
        }
    }

    static delegate* unmanaged<int, int, int, void> p_glProgramParameteri;
    public static void GlProgramParameteri(int program, int pname, int value) => p_glProgramParameteri(program, pname, value);

    static delegate* unmanaged<int, int, int, int, int, int, void*, void> p_glReadPixels;
    public static void GlReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels)
    {
        fixed (byte* p = pixels)
        {
            p_glReadPixels(x, y, width, height, format, type, p);
        }
    }

    static delegate* unmanaged<int, int, int, int, void> p_glRenderbufferStorage;
    public static void GlRenderbufferStorage(int target, int internalformat, int width, int height)
        => p_glRenderbufferStorage(target, internalformat, width, height);

    static delegate* unmanaged<int, int, int, int, int, void> p_glRenderbufferStorageMultisample;
    public static void GlRenderbufferStorageMultisample(int target, int samples, int internalformat, int width, int height)
        => p_glRenderbufferStorageMultisample(target, samples, internalformat, width, height);

    static delegate* unmanaged<int, int, int, void> p_glSamplerParameteri;
    public static void GlSamplerParameteri(int sampler, int pname, int param) => p_glSamplerParameteri(sampler, pname, param);

    static delegate* unmanaged<int, int, int, int, void> p_glScissor;
    public static void GlScissor(int x, int y, int width, int height) => p_glScissor(x, y, width, height);

    static delegate* unmanaged<int, int, byte**, int*, void> p_glShaderSource;
    public static void GlShaderSource(int shader, string source)
    {
        if (ShaderSourceFilter is { } filter)
        {
            source = filter(source);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        int length = bytes.Length;
        fixed (byte* p = bytes)
        {
            byte* s = p;
            p_glShaderSource(shader, 1, &s, &length);
        }
    }

    static delegate* unmanaged<int, int, int, void> p_glStencilFunc;
    public static void GlStencilFunc(int func, int reference, int mask) => p_glStencilFunc(func, reference, mask);

    static delegate* unmanaged<int, void> p_glStencilMask;
    public static void GlStencilMask(int mask) => p_glStencilMask(mask);

    static delegate* unmanaged<int, int, int, void> p_glStencilOp;
    public static void GlStencilOp(int fail, int zfail, int zpass) => p_glStencilOp(fail, zfail, zpass);

    // pixels null = allocate only
    static delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> p_glTexImage2D;
    public static void GlTexImage2D(int target, int level, int internalformat, int width, int height, int border, int format,
        int type, byte[]? pixels)
    {
        fixed (byte* p = pixels)
        {
            p_glTexImage2D(target, level, internalformat, width, height, border, format, type, p);
        }
    }

    // packed pixels, one int per RGBA texel (the byte order in memory is what GL reads)
    public static void GlTexImage2D(int target, int level, int internalformat, int width, int height, int border, int format,
        int type, int[] pixels)
    {
        fixed (int* p = pixels)
        {
            p_glTexImage2D(target, level, internalformat, width, height, border, format, type, p);
        }
    }

    static delegate* unmanaged<int, int, int, void> p_glTexParameteri;
    public static void GlTexParameteri(int target, int pname, int param) => p_glTexParameteri(target, pname, param);

    static delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> p_glTexSubImage2D;
    public static void GlTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format,
        int type, byte[] pixels)
    {
        fixed (byte* p = pixels)
        {
            p_glTexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, p);
        }
    }

    static delegate* unmanaged<int, float, void> p_glUniform1f;
    public static void GlUniform1f(int location, float x) => p_glUniform1f(location, x);

    static delegate* unmanaged<int, int, void> p_glUniform1i;
    public static void GlUniform1i(int location, int x) => p_glUniform1i(location, x);

    static delegate* unmanaged<int, float, float, void> p_glUniform2f;
    public static void GlUniform2f(int location, float x, float y) => p_glUniform2f(location, x, y);

    static delegate* unmanaged<int, float, float, float, void> p_glUniform3f;
    public static void GlUniform3f(int location, float x, float y, float z) => p_glUniform3f(location, x, y, z);

    static delegate* unmanaged<int, float, float, float, float, void> p_glUniform4f;
    public static void GlUniform4f(int location, float x, float y, float z, float w) => p_glUniform4f(location, x, y, z, w);

    static delegate* unmanaged<int, int, int*, void> p_glUniform1iv;
    public static void GlUniform1iv(int location, int count, int[] v, int offset) { fixed (int* p = v) p_glUniform1iv(location, count, p + offset); }

    static delegate* unmanaged<int, int, int*, void> p_glUniform2iv;
    public static void GlUniform2iv(int location, int count, int[] v, int offset) { fixed (int* p = v) p_glUniform2iv(location, count, p + offset); }

    static delegate* unmanaged<int, int, int*, void> p_glUniform4iv;
    public static void GlUniform4iv(int location, int count, int[] v, int offset) { fixed (int* p = v) p_glUniform4iv(location, count, p + offset); }

    static delegate* unmanaged<int, int, float*, void> p_glUniform2fv;
    public static void GlUniform2fv(int location, int count, float[] v, int offset) { fixed (float* p = v) p_glUniform2fv(location, count, p + offset); }

    static delegate* unmanaged<int, int, float*, void> p_glUniform3fv;
    public static void GlUniform3fv(int location, int count, float[] v, int offset) { fixed (float* p = v) p_glUniform3fv(location, count, p + offset); }

    static delegate* unmanaged<int, int, float*, void> p_glUniform4fv;
    public static void GlUniform4fv(int location, int count, float[] v, int offset) { fixed (float* p = v) p_glUniform4fv(location, count, p + offset); }

    static delegate* unmanaged<int, int, byte, float*, void> p_glUniformMatrix3fv;
    public static void GlUniformMatrix3fv(int location, int count, bool transpose, float[] v, int offset)
    {
        fixed (float* p = v) p_glUniformMatrix3fv(location, count, B(transpose), p + offset);
    }

    static delegate* unmanaged<int, int, byte, float*, void> p_glUniformMatrix4fv;
    public static void GlUniformMatrix4fv(int location, int count, bool transpose, float[] v, int offset)
    {
        fixed (float* p = v) p_glUniformMatrix4fv(location, count, B(transpose), p + offset);
    }

    static delegate* unmanaged<int, void> p_glUseProgram;
    public static void GlUseProgram(int program) => p_glUseProgram(program);

    // offset: bytes into the bound GL_ARRAY_BUFFER
    static delegate* unmanaged<int, int, int, byte, int, nint, void> p_glVertexAttribPointer;
    public static void GlVertexAttribPointer(int index, int size, int type, bool normalized, int stride, int offset)
        => p_glVertexAttribPointer(index, size, type, B(normalized), stride, offset);

    static delegate* unmanaged<int, int, int, int, void> p_glViewport;
    public static void GlViewport(int x, int y, int width, int height) => p_glViewport(x, y, width, height);

    static byte B(bool b) => b ? (byte)1 : (byte)0;
}
