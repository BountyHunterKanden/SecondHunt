namespace MphRead.Rendering;

// GLSL for the scene program, in two flavours:
// - desktop: GLSL 1.20 (compatibility profile), vertex data from the built-ins (gl_Vertex, gl_Normal, gl_Color,
//   gl_MultiTexCoord0 with the bone index in its z);
// - Android: GLSL ES 3.00, vertex data from attribute locations 0-4, plus the u_alphaPass fragment filter.
// The maths (lighting, texture coordinates, texture combine, fog) is one shared block of text per stage, so the two
// flavours can't drift apart; only the inputs, outputs and the ES-only alpha pass differ.
//
// The arithmetic is written in a fixed order on purpose: the output has to match the previous renderer bit for bit, so
// sums and products keep the grouping given in the spec. Don't tidy them up (no factoring, no reordering).
public static class SceneShaderSource
{
    // ---- vertex stage ----

    private const string VertexUniforms = """
        uniform bool u_lit;
        uniform bool u_textured;
        uniform bool u_vertexColors;
        uniform vec3 u_light0Dir;
        uniform vec3 u_light0Color;
        uniform vec3 u_light1Dir;
        uniform vec3 u_light1Color;
        uniform vec3 u_matDiffuse;
        uniform vec3 u_matAmbient;
        uniform vec3 u_matSpecular;
        uniform vec3 u_matEmission;
        uniform mat4 u_projection;
        uniform mat4 u_view;
        uniform mat4 u_billboard;
        uniform mat4 u_texMatrix;
        uniform int u_texgen;
        uniform mat4 u_bones[32];
        """;

    private const string VertexMath = """
        // One light's share of a lit vertex colour. The three products are colour times colour first, then the
        // scalar, and they're summed specular + diffuse + ambient, left to right.
        vec3 lightShare(vec3 lightDir, vec3 lightColor, vec3 n, vec3 diffuseSource, vec3 ambientSource)
        {
            float diffuseAmount = max(0.0, -dot(lightDir, n));
            vec3 halfway = (lightDir + vec3(0.0, 0.0, -1.0)) / 2.0;
            float shine = max(0.0, dot(-halfway, n));
            shine = shine * shine;
            return ((u_matSpecular * lightColor) * shine) + ((diffuseSource * lightColor) * diffuseAmount)
                + (ambientSource * lightColor);
        }

        // Everything the vertex stage computes. inColor is the input colour (on ES, after the "no vertex colour"
        // substitution); boneIndex picks the entry of u_bones.
        void shadeVertex(vec3 position, vec3 normal, vec4 inColor, vec2 texcoord, int boneIndex,
            out vec4 clipPosition, out vec4 color, out vec2 uv)
        {
            mat4 bone = u_bones[boneIndex];
            mat4 model = bone * u_billboard;
            // matrices multiplied together first, then the vector
            clipPosition = ((u_projection * u_view) * model) * vec4(position, 1.0);

            vec4 working = vec4(1.0, 1.0, 1.0, 1.0);
            if (u_vertexColors) {
                working = inColor;
            }
            vec3 n = normalize(mat3(model) * normal);

            if (u_lit) {
                vec3 diffuseSource = u_matDiffuse;
                vec3 ambientSource = u_matAmbient;
                // An input alpha of exactly 0 means the display list sent a diffuse colour through the vertex colour.
                // The test reads the input colour; the value taken is the working colour.
                if (inColor.a == 0.0) {
                    diffuseSource = working.rgb;
                    ambientSource = vec3(0.0, 0.0, 0.0);
                }
                vec3 share0 = lightShare(u_light0Dir, u_light0Color, n, diffuseSource, ambientSource);
                vec3 share1 = lightShare(u_light1Dir, u_light1Color, n, diffuseSource, ambientSource);
                color = vec4(min((share0 + share1) + u_matEmission, vec3(1.0, 1.0, 1.0)), 1.0);
            } else {
                color = vec4(working.rgb, 1.0);
            }

            if (!u_textured) {
                uv = vec2(0.0, 0.0);
            } else if (u_texgen == 2 || u_texgen == 3) {
                // Generated coordinates: 2 from the normal, 3 from the position (both the raw inputs).
                mat4 gen = u_texMatrix;
                vec4 genSource = vec4(position, 1.0);
                if (u_texgen == 2) {
                    mat4 viewPart = mat4(1.0);
                    if (u_lit) {
                        viewPart = u_view;
                    }
                    // the bone's upper-left 3x3 (no billboard), padded out with identity
                    mat4 boneRotation = mat4(mat3(bone));
                    gen = transpose((u_texMatrix * viewPart) * boneRotation);
                    genSource = vec4(normal, 1.0);
                }
                // gen[c][r] is column c, row r
                uv = vec2(dot(genSource, vec4(gen[0][0], gen[0][1], gen[0][2], texcoord.x)),
                    dot(genSource, vec4(gen[1][0], gen[1][1], gen[1][2], texcoord.y)));
            } else {
                // 0 (none) and 1 (texcoord): the texture matrix applied to (s, t, 0, 1)
                uv = (u_texMatrix * vec4(texcoord.x, texcoord.y, 0.0, 1.0)).xy;
            }
        }
        """;

    private const string DesktopVertexMain = """
        varying vec4 v_color;
        varying vec2 v_uv;

        void main()
        {
            // The bone index rides in the third texture coordinate: its integer part (truncated) picks the bone.
            int boneIndex = int(gl_MultiTexCoord0.z);
            vec4 clipPosition;
            vec4 color;
            vec2 uv;
            shadeVertex(gl_Vertex.xyz, gl_Normal, gl_Color, gl_MultiTexCoord0.xy, boneIndex, clipPosition, color, uv);
            gl_Position = clipPosition;
            v_color = color;
            v_uv = uv;
        }
        """;

    private const string EsVertexInputs = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_normal;
        layout(location = 2) in vec4 a_color;
        layout(location = 3) in vec2 a_texcoord;
        layout(location = 4) in float a_boneIndex;

        out vec4 v_color;
        out vec2 v_uv;
        """;

    private const string EsVertexMain = """
        void main()
        {
            // An input alpha above 1.5 is the "no per-vertex colour" marker: use (diffuse, 1) instead.
            vec4 inColor = a_color;
            if (a_color.a > 1.5) {
                inColor = vec4(u_matDiffuse, 1.0);
            }
            // rounded to the nearest integer
            int boneIndex = int(a_boneIndex + 0.5);
            vec4 clipPosition;
            vec4 color;
            vec2 uv;
            shadeVertex(a_position, a_normal, inColor, a_texcoord, boneIndex, clipPosition, color, uv);
            gl_Position = clipPosition;
            v_color = color;
            v_uv = uv;
        }
        """;

    // ---- fragment stage ----

    private const string FragmentUniforms = """
        uniform bool u_textured;
        uniform bool u_fogOn;
        uniform vec4 u_fogColor;
        uniform float u_fogNear;
        uniform float u_fogFar;
        uniform sampler2D u_texture;
        uniform bool u_colorOverrideOn;
        uniform vec4 u_colorOverride;
        uniform bool u_paletteOverrideOn;
        uniform vec4 u_paletteOverride;
        uniform float u_matAlpha;
        uniform int u_polyMode;
        uniform vec3 u_toonRamp[32];
        """;

    private const string FragmentMath = """
        // The surface colour before the alpha pass and fog, as (rgb, alpha). c is the interpolated vertex colour;
        // texel is the texture sample (only read when u_textured).
        vec4 surfaceColor(vec4 c, vec4 texel)
        {
            vec3 rgb;
            float alpha;
            if (u_textured) {
                vec4 t = texel;
                if (u_paletteOverrideOn) {
                    t = vec4(u_paletteOverride.rgb, t.a);
                }
                if (u_polyMode == 1) {
                    // decal
                    rgb = (t.rgb * t.a) + (c.rgb * (1.0 - t.a));
                    alpha = u_matAlpha * c.a;
                } else if (u_polyMode == 2) {
                    // toon / highlight: float multiply, then truncate to pick the ramp entry
                    vec3 ramp = u_toonRamp[int(c.r * 31.0)];
                    rgb = (t.rgb * c.r) + ramp;
                    alpha = (u_matAlpha * t.a) * c.a;
                } else {
                    // modulate (shadow polygons are drawn this way too)
                    rgb = c.rgb * t.rgb;
                    alpha = c.a * (u_matAlpha * t.a);
                }
                if (u_colorOverrideOn) {
                    rgb = u_colorOverride.rgb;
                    alpha = alpha * u_colorOverride.a;
                }
            } else if (u_colorOverrideOn) {
                rgb = u_colorOverride.rgb;
                alpha = u_colorOverride.a;
            } else {
                if (u_polyMode == 2) {
                    rgb = u_toonRamp[int(c.r * 31.0)];
                } else {
                    rgb = c.rgb;
                }
                alpha = c.a * u_matAlpha;
            }
            return vec4(rgb, alpha);
        }

        // Fog by window depth z. 124/128 is the top of the game's fog density table.
        vec3 applyFog(vec3 rgb, float z)
        {
            float amount = 0.0;
            if (z >= u_fogFar) {
                amount = 1.0;
            } else if (z > u_fogNear) {
                amount = (((z - u_fogNear) / (u_fogFar - u_fogNear)) * 124.0) / 128.0;
            }
            return (rgb * (1.0 - amount)) + (u_fogColor.rgb * amount);
        }
        """;

    private const string DesktopFragmentMain = """
        varying vec4 v_color;
        varying vec2 v_uv;

        void main()
        {
            vec4 texel = vec4(0.0, 0.0, 0.0, 0.0);
            if (u_textured) {
                texel = texture2D(u_texture, v_uv);
            }
            vec4 result = surfaceColor(v_color, texel);
            if (u_fogOn) {
                result = vec4(applyFog(result.rgb, gl_FragCoord.z), result.a);
            }
            gl_FragColor = result;
        }
        """;

    private const string EsFragmentMain = """
        in vec4 v_color;
        in vec2 v_uv;

        // 0 keeps every fragment, 1 only alpha exactly 1, 2 only alpha below 1 (the layered draw's alpha filter)
        uniform int u_alphaPass;

        layout(location = 0) out vec4 o_fragColor;

        void main()
        {
            vec4 texel = vec4(0.0, 0.0, 0.0, 0.0);
            if (u_textured) {
                texel = texture(u_texture, v_uv);
            }
            vec4 result = surfaceColor(v_color, texel);
            // the alpha filter runs before fog. "Solid" is alpha 1: the game's other alphas are 5-bit (at most 30/31,
            // 0.968) or a texel's 0, so 0.99 splits them exactly as 1.0 does, and also takes the 1.0 that a GPU's
            // interpolation leaves a hair short (desktop GPUs do, on scattered pixels: they fell out of the solid pass
            // as speckle; the Odin's Adreno happens to land on 1.0 exactly)
            if (u_alphaPass == 1) {
                if (result.a < 0.99) {
                    discard;
                }
            } else if (u_alphaPass == 2) {
                if (result.a >= 0.99) {
                    discard;
                }
            }
            if (u_fogOn) {
                result = vec4(applyFog(result.rgb, gl_FragCoord.z), result.a);
            }
            o_fragColor = result;
        }
        """;

    // Each source starts with its #version line (it has to be the first line).
    private const string DesktopHeader = "#version 120\n";
    private const string EsHeader = "#version 300 es\nprecision highp float;\nprecision highp int;\n";

    public static readonly string DesktopVertex =
        DesktopHeader + "\n" + VertexUniforms + "\n\n" + VertexMath + "\n\n" + DesktopVertexMain + "\n";

    public static readonly string DesktopFragment =
        DesktopHeader + "\n" + FragmentUniforms + "\n\n" + FragmentMath + "\n\n" + DesktopFragmentMain + "\n";

    public static readonly string EsVertex =
        EsHeader + "\n" + EsVertexInputs + "\n\n" + VertexUniforms + "\n\n" + VertexMath + "\n\n" + EsVertexMain + "\n";

    public static readonly string EsFragment =
        EsHeader + "\n" + FragmentUniforms + "\n\n" + FragmentMath + "\n\n" + EsFragmentMain + "\n";
}
