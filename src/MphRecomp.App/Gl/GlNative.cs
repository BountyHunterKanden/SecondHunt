namespace MphRecomp.App.Gl;

// The campaign renderer's per-draw hot path (CampaignRenderer.DrawItem and friends): a draw item uploads MphRead's whole
// 496-float mtx_stack plus ~25 more calls, and each item is drawn 2-3 times a frame. These were direct driver imports
// when the rest of the renderer went through Android's JNI binding (every array argument copied into a new Java array:
// tens of MB of garbage a second in a busy fight, 2026-09-30). GLES30 is now direct itself, so this is just the short
// names the hot path is written with.
internal static unsafe class GlNative
{
    public static void Uniform1i(int location, int v0) => GLES30.GlUniform1i(location, v0);
    public static void Uniform1f(int location, float v0) => GLES30.GlUniform1f(location, v0);
    public static void Uniform3f(int location, float v0, float v1, float v2) => GLES30.GlUniform3f(location, v0, v1, v2);
    public static void Uniform4f(int location, float v0, float v1, float v2, float v3) => GLES30.GlUniform4f(location, v0, v1, v2, v3);
    public static void BindVertexArray(int array) => GLES30.GlBindVertexArray(array);
    public static void BindBuffer(int target, int buffer) => GLES30.GlBindBuffer(target, buffer);
    public static void DrawArrays(int mode, int first, int count) => GLES30.GlDrawArrays(mode, first, count);
    public static void ActiveTexture(int texture) => GLES30.GlActiveTexture(texture);
    public static void BindTexture(int target, int texture) => GLES30.GlBindTexture(target, texture);
    public static void BindSampler(int unit, int sampler) => GLES30.GlBindSampler(unit, sampler);
    public static void Enable(int cap) => GLES30.GlEnable(cap);
    public static void Disable(int cap) => GLES30.GlDisable(cap);
    public static void CullFace(int mode) => GLES30.GlCullFace(mode);
    public static void StencilFunc(int func, int reference, int mask) => GLES30.GlStencilFunc(func, reference, mask);

    // count matrices from the start of value (untransposed, OpenTK/MphRead layout)
    public static void UniformMatrix4(int location, int count, float[] value) => GLES30.GlUniformMatrix4fv(location, count, false, value, 0);

    // the first count floats of data into the bound buffer at offset 0
    public static void BufferSubData(int target, float[] data, int count)
    {
        fixed (float* p = data)
        {
            GLES30.BufferSubData(target, 0, count * sizeof(float), p);
        }
    }
}
