namespace MphRecomp.App.Gl;

// The array uniforms the Prime gun and the HD suit set for every material in every pass (the MP4 gun's 98-bone palette
// alone is 6 KB, for 8 materials x 2 passes a frame). Written when Android's JNI binding copied each array into a new
// Java array; GLES30 pins them in place now, so these are its calls under the names those renderers use.
internal static class GlUniforms
{
    public static void Uniform1iv(int location, int count, int[] v, int offset) => GLES30.GlUniform1iv(location, count, v, offset);
    public static void Uniform2iv(int location, int count, int[] v, int offset) => GLES30.GlUniform2iv(location, count, v, offset);
    public static void Uniform4iv(int location, int count, int[] v, int offset) => GLES30.GlUniform4iv(location, count, v, offset);
    public static void Uniform2fv(int location, int count, float[] v, int offset) => GLES30.GlUniform2fv(location, count, v, offset);
    public static void Uniform3fv(int location, int count, float[] v, int offset) => GLES30.GlUniform3fv(location, count, v, offset);
    public static void Uniform4fv(int location, int count, float[] v, int offset) => GLES30.GlUniform4fv(location, count, v, offset);
    public static void UniformMatrix3fv(int location, int count, bool transpose, float[] v, int offset)
        => GLES30.GlUniformMatrix3fv(location, count, transpose, v, offset);
    public static void UniformMatrix4fv(int location, int count, bool transpose, float[] v, int offset)
        => GLES30.GlUniformMatrix4fv(location, count, transpose, v, offset);
}
