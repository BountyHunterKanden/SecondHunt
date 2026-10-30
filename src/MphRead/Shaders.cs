namespace MphRead
{
    public static class Shaders
    {
        public static string RttVertexShader { get; } = @"
#version 120

varying vec2 texcoord;

void main()
{
    gl_Position = vec4(gl_Vertex.xy, 0, 1);
    texcoord = gl_MultiTexCoord0.xy;
}
";

        public static string RttFragmentShader { get; } = @"
#version 120

uniform float alpha;
uniform bool use_mask;
uniform float view_width;
uniform float view_height;
uniform vec4 fade_color;
uniform sampler2D tex;
uniform sampler2D mask;
varying vec2 texcoord;
varying vec4 color;

void main()
{
    if (fade_color.a > 0) {
        gl_FragColor = fade_color;
    }
    else {
        gl_FragColor = texture2D(tex, texcoord);
        if (use_mask) {
            float maskY = gl_FragCoord.y + (view_width - view_height) / 2;
            vec2 maskTexcoord = vec2(gl_FragCoord.x / view_width, 1 - maskY / view_width);
            vec4 maskColor = texture2D(mask, maskTexcoord);
            if (maskColor.a > 0) {
                gl_FragColor.a = 0;
            }
        }
        gl_FragColor.a *= alpha;
    }
}
";

        public static string ShiftFragmentShader { get; } = @"
#version 120

uniform float[64] shift_table;
uniform int shift_idx;
uniform float shift_fac;
uniform float lerp_fac;
uniform float[192] white_table;
uniform float white_fac;
uniform sampler2D tex;

varying vec2 texcoord;
varying vec4 color;

void main()
{
    int band = int((1.0 - texcoord.y) * 192.0);
    int index = int(mod((band + shift_idx + mod(band, 2) * 32), 64));
    float value1 = shift_table[index];
    float value2 = shift_table[int(mod(index + 1, 64))];
    float value = mix(value1, value2, lerp_fac) * shift_fac;
    vec2 shifted = vec2(texcoord.x + value, texcoord.y);
    if (shifted.x < 0.0 || shifted.x > 1.0) {
        gl_FragColor = vec4(0, 0, 0, 1);
    }
    else {
        gl_FragColor = texture2D(tex, shifted);
    }
    if (white_fac != 0) {
        float factor = white_table[band];
        if (white_fac < 0) {
            gl_FragColor = vec4(factor, factor, factor, 1);
        }
        else {
            factor *= white_fac;
            if (factor >= 0) {
                float r = gl_FragColor.r + (1 - gl_FragColor.r) * factor;
                float g = gl_FragColor.g + (1 - gl_FragColor.g) * factor;
                float b = gl_FragColor.b + (1 - gl_FragColor.b) * factor;
                gl_FragColor = vec4(r, g, b, 1);
            }
            else {
                factor = -factor;
                float r = gl_FragColor.r  - gl_FragColor.r * factor;
                float g = gl_FragColor.g  - gl_FragColor.g * factor;
                float b = gl_FragColor.b  - gl_FragColor.b * factor;
                gl_FragColor = vec4(r, g, b, 1);
            }
        }
    }
}
";
    }

    public class ShaderLocations
    {
        public int UseLight { get; set; }
        public int ShowColors { get; set; }
        public int UseTexture { get; set; }
        public int Light1Color { get; set; }
        public int Light1Vector { get; set; }
        public int Light2Color { get; set; }
        public int Light2Vector { get; set; }
        public int Diffuse { get; set; }
        public int Ambient { get; set; }
        public int Specular { get; set; }
        public int Emission { get; set; }
        public int UseFog { get; set; }
        public int FogColor { get; set; }
        public int FogMinDistance { get; set; }
        public int FogMaxDistance { get; set; }
        public int UseOverride { get; set; }
        public int OverrideColor { get; set; }
        public int UsePaletteOverride { get; set; }
        public int PaletteOverrideColor { get; set; }
        public int MaterialAlpha { get; set; }
        public int MaterialMode { get; set; }
        public int ViewMatrix { get; set; }
        public int ViewInvMatrix { get; set; }
        public int ProjectionMatrix { get; set; }
        public int TextureMatrix { get; set; }
        public int TexgenMode { get; set; }
        public int MatrixStack { get; set; }
        public int ToonTable { get; set; }
        public int FadeColor { get; set; }
        public int LayerAlpha { get; set; }
        public int UseMask { get; set; }
        public int ViewWidth { get; set; }
        public int ViewHeight { get; set; }
        public int ShiftTable { get; set; }
        public int ShiftIndex { get; set; }
        public int ShiftFactor { get; set; }
        public int LerpFactor { get; set; }
        public int WhiteoutTable { get; set; }
        public int WhiteoutFactor { get; set; }
    }
}
