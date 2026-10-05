using System.Runtime.InteropServices;

namespace MphRecomp.App;

// Direct calls into the GLES 3 driver for the campaign renderer's per-draw hot path (CampaignActivity.DrawItem and
// friends). Android.Opengl.GLES30 goes through JNI: every call is a Java transition, and every array argument is
// copied into a NEW Java array and back. A draw item uploads MphRead's whole 496-float mtx_stack (2 KB of Java garbage)
// plus ~25 more JNI calls, and each item is drawn 2-3 times a frame (opaque passes 1+4, translucent 3+5+6), so a busy
// fight made tens of MB of Java garbage a second: the GC bridge then stalled the GL thread ~10 ms every ~0.6 s
// (logcat "Explicit concurrent copying GC", 2026-09-30). These go straight to libGLESv3.so on the calling thread's
// current EGL context (the GLSurfaceView thread), with arrays pinned in place: no Java transition, no copies, no garbage.
// Only for calls made every frame; one-off setup keeps using GLES30.
internal static unsafe class GlNative
{
    const string Lib = "libGLESv3.so";

    [DllImport(Lib, EntryPoint = "glUniform1i")] public static extern void Uniform1i(int location, int v0);
    [DllImport(Lib, EntryPoint = "glUniform1f")] public static extern void Uniform1f(int location, float v0);
    [DllImport(Lib, EntryPoint = "glUniform3f")] public static extern void Uniform3f(int location, float v0, float v1, float v2);
    [DllImport(Lib, EntryPoint = "glUniform4f")] public static extern void Uniform4f(int location, float v0, float v1, float v2, float v3);
    [DllImport(Lib, EntryPoint = "glUniformMatrix4fv")] static extern void UniformMatrix4fv(int location, int count, byte transpose, float* value);
    [DllImport(Lib, EntryPoint = "glBindVertexArray")] public static extern void BindVertexArray(int array);
    [DllImport(Lib, EntryPoint = "glBindBuffer")] public static extern void BindBuffer(int target, int buffer);
    [DllImport(Lib, EntryPoint = "glBufferSubData")] static extern void BufferSubData(int target, nint offset, nint size, void* data);
    [DllImport(Lib, EntryPoint = "glDrawArrays")] public static extern void DrawArrays(int mode, int first, int count);
    [DllImport(Lib, EntryPoint = "glActiveTexture")] public static extern void ActiveTexture(int texture);
    [DllImport(Lib, EntryPoint = "glBindTexture")] public static extern void BindTexture(int target, int texture);
    [DllImport(Lib, EntryPoint = "glBindSampler")] public static extern void BindSampler(int unit, int sampler);
    [DllImport(Lib, EntryPoint = "glEnable")] public static extern void Enable(int cap);
    [DllImport(Lib, EntryPoint = "glDisable")] public static extern void Disable(int cap);
    [DllImport(Lib, EntryPoint = "glCullFace")] public static extern void CullFace(int mode);
    [DllImport(Lib, EntryPoint = "glStencilFunc")] public static extern void StencilFunc(int func, int reference, int mask);

    // count matrices from the start of value (untransposed, OpenTK/MphRead layout)
    public static void UniformMatrix4(int location, int count, float[] value)
    {
        fixed (float* p = value)
        {
            UniformMatrix4fv(location, count, 0, p);
        }
    }

    // the first count floats of data into the bound buffer at offset 0
    public static void BufferSubData(int target, float[] data, int count)
    {
        fixed (float* p = data)
        {
            BufferSubData(target, 0, count * sizeof(float), p);
        }
    }
}
