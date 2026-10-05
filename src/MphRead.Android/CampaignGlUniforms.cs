using System.Runtime.InteropServices;

namespace MphRecomp.App;

// The array uniforms the Prime gun and the HD suit set for every material in every pass, straight into libGLESv3.so
// (same arguments as Android.Opengl.GLES30's overloads). GLES30 copies each array into a new Java array: ~20 per
// material, the MP4 gun's 98-bone palette alone 6 KB, for 8 materials x 2 passes a frame. Like GlNative.cs (the
// campaign draw list's calls): the calling thread's current EGL context, arrays pinned in place, no Java garbage.
internal static unsafe class GlUniforms
{
    const string Lib = "libGLESv3.so";

    [DllImport(Lib)] static extern void glUniform1iv(int location, int count, int* value);
    [DllImport(Lib)] static extern void glUniform2iv(int location, int count, int* value);
    [DllImport(Lib)] static extern void glUniform4iv(int location, int count, int* value);
    [DllImport(Lib)] static extern void glUniform2fv(int location, int count, float* value);
    [DllImport(Lib)] static extern void glUniform3fv(int location, int count, float* value);
    [DllImport(Lib)] static extern void glUniform4fv(int location, int count, float* value);
    [DllImport(Lib)] static extern void glUniformMatrix3fv(int location, int count, byte transpose, float* value);
    [DllImport(Lib)] static extern void glUniformMatrix4fv(int location, int count, byte transpose, float* value);

    public static void Uniform1iv(int location, int count, int[] v, int offset) { fixed (int* p = v) glUniform1iv(location, count, p + offset); }
    public static void Uniform2iv(int location, int count, int[] v, int offset) { fixed (int* p = v) glUniform2iv(location, count, p + offset); }
    public static void Uniform4iv(int location, int count, int[] v, int offset) { fixed (int* p = v) glUniform4iv(location, count, p + offset); }
    public static void Uniform2fv(int location, int count, float[] v, int offset) { fixed (float* p = v) glUniform2fv(location, count, p + offset); }
    public static void Uniform3fv(int location, int count, float[] v, int offset) { fixed (float* p = v) glUniform3fv(location, count, p + offset); }
    public static void Uniform4fv(int location, int count, float[] v, int offset) { fixed (float* p = v) glUniform4fv(location, count, p + offset); }

    public static void UniformMatrix3fv(int location, int count, bool transpose, float[] v, int offset)
    {
        fixed (float* p = v) glUniformMatrix3fv(location, count, transpose ? (byte)1 : (byte)0, p + offset);
    }

    public static void UniformMatrix4fv(int location, int count, bool transpose, float[] v, int offset)
    {
        fixed (float* p = v) glUniformMatrix4fv(location, count, transpose ? (byte)1 : (byte)0, p + offset);
    }
}
