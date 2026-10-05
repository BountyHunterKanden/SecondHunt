using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MphRecomp.Render;

namespace MphRecomp.Arenas
{
    // A GX material's TEV as its own fragment program: GxShader.Frag's stage loop with this material's inputs, swaps,
    // konst selections, ops and alpha test written in, so the GPU runs straight-line code instead of the interpreter's
    // branch ladders (every input of every stage picks among ~16 cases per pixel, every texture read among 8 samplers).
    // On the Odin's Adreno the interpreter was most of an Echoes arena's GPU time: 22 ms a frame before the lightmap fast
    // path took the plain opaque surfaces (17:56), and what's left on it -- skies, glass, glow layers -- covers whole
    // windows in several layers (Sidehopper: GPU 6.8 ms avg, 14 max, 10-03 19:01).
    // Same math as GxShader.Frag for a material with no normal map and no tint (the arenas' materials), reading the same
    // GxShader.Vert outputs; uReg, uKonst and uExposure stay uniforms. Null: the material stays on the interpreter.
    // Campaign's Prime gun (2026-10-03) also passes its uTint recolour (tintMode 1 = the whole material, 2 = what texture
    // map tintUnit reads; the colour stays the uTint uniform) and normalMaps: a normal-mapped material then gets
    // GxShader.Frag's per-pixel bend (channel 0 and the reflection texcoords from the bent normal, GxShader's own
    // uniforms) instead of null. The defaults write exactly the arenas' programs.
    public static class ArenaGxSpec
    {
        public static string? Frag(GxShader.Params p, int tintMode = 0, int tintUnit = -1, bool normalMaps = false)
        {
            bool nmap = p.NmapUnit >= 0;
            if (nmap && !normalMaps || p.NumStages <= 0 || p.NumStages > 8) return null;
            var sb = new StringBuilder();
            sb.Append("#version 300 es\nprecision highp float;\nprecision highp int;\n");
            sb.Append("in vec4 vChan0; in vec4 vChan1;\nin vec2 vTc0, vTc1, vTc2, vTc3, vTc4, vTc5, vTc6, vTc7;\nout vec4 o;\n");
            SortedSet<int> units = Units(p);
            if (nmap) units.Add(p.NmapUnit);
            foreach (int u in units) sb.Append($"uniform sampler2D uTex{u};\n");
            sb.Append("uniform vec4 uReg[4]; uniform vec4 uKonst[4]; uniform float uExposure;\n");
            if (tintMode != 0) sb.Append("uniform vec4 uTint;\n");
            // GxShader.Frag's chanPix: only a lit channel 0 with register material and ambient is lit again per pixel
            bool pixChan = nmap && p.ColCtrl[0] == 1 && p.ColCtrl[1] != 1 && p.ColCtrl[2] != 1;
            if (nmap) sb.Append(NmapUniforms(p, pixChan));
            sb.Append("void main(){\n");
            sb.Append("  vec3 c0 = uReg[0].rgb, c1 = uReg[1].rgb, c2 = uReg[2].rgb, c3 = uReg[3].rgb;\n");
            sb.Append("  float a0 = uReg[0].a, a1 = uReg[1].a, a2 = uReg[2].a, a3 = uReg[3].a;\n");
            sb.Append("  vec4 t, r; vec3 xc; float xa;\n");
            if (nmap) sb.Append(Bend(p, pixChan));
            for (int s = 0; s < p.NumStages; s++)
            {
                int tm = p.Dst[s * 4 + 2], tc = p.Dst[s * 4 + 3];
                int kc = p.Ksel[s * 4], ka = p.Ksel[s * 4 + 1], rs = p.Ksel[s * 4 + 2], ts = p.Ksel[s * 4 + 3];
                string uv = nmap && p.TgMode[tc] == 1
                    ? $"(gPix ? (uTexMtx[{tc}] * vec3(0.5 + 0.5 * gNe.x, 0.5 - 0.5 * gNe.y, 1.0)).xy : vTc{tc})" : $"vTc{tc}";
                sb.Append(tm >= 0 ? $"  t = texture(uTex{Unit(tm)}, {uv}){Swz(p, ts)};\n" : "  t = vec4(0.0);\n");
                if (tintMode == 2 && tm >= 0 && tm == tintUnit) sb.Append("  t.rgb = uTint.rgb * max(t.r, max(t.g, t.b));\n");
                string ras = p.RasChan[s] switch { 0 => nmap ? "ch0" : "vChan0", 1 => "vChan1", _ => "vec4(0.0)" };
                sb.Append($"  r = {ras}{Swz(p, rs)};\n");
                string Ci(int k) => k switch
                {
                    0 => "c0", 1 => "vec3(a0)", 2 => "c1", 3 => "vec3(a1)", 4 => "c2", 5 => "vec3(a2)", 6 => "c3", 7 => "vec3(a3)",
                    8 => "t.rgb", 9 => "vec3(t.a)", 10 => "r.rgb", 11 => "vec3(r.a)", 12 => "vec3(1.0)", 13 => "vec3(0.5)",
                    14 => KonstC(kc), _ => "vec3(0.0)",
                };
                string Ai(int k) => k switch
                {
                    0 => "a0", 1 => "a1", 2 => "a2", 3 => "a3", 4 => "t.a", 5 => "r.a", 6 => KonstA(ka), _ => "0.0",
                };
                int o = s * 4;
                sb.Append($"  xc = {Combine(Ci(p.Cabcd[o]), Ci(p.Cabcd[o + 1]), Ci(p.Cabcd[o + 2]), Ci(p.Cabcd[o + 3]), p.Cmod, o, "vec3(1.0)", "vec3")};\n");
                sb.Append($"  xa = {Combine(Ai(p.Aabcd[o]), Ai(p.Aabcd[o + 1]), Ai(p.Aabcd[o + 2]), Ai(p.Aabcd[o + 3]), p.Amod, o, "1.0", "")};\n");
                sb.Append($"  c{p.Dst[o] & 3} = xc; a{p.Dst[o + 1] & 3} = xa;\n");
            }
            sb.Append("  float outa = clamp(a0, 0.0, 1.0);\n");
            if (p.AlphaTest[3] == 1)
            {
                string p0 = Cmp(p.AlphaTest[0], p.AlphaRef[0]), p1 = Cmp(p.AlphaTest[1], p.AlphaRef[1]);
                string pass = p.AlphaTest[2] switch { 0 => $"({p0}) && ({p1})", 1 => $"({p0}) || ({p1})", 2 => $"({p0}) != ({p1})", _ => $"({p0}) == ({p1})" };
                sb.Append($"  if (!({pass})) discard;\n");
            }
            if (tintMode == 1)
            {
                sb.Append("  vec3 fc = clamp(c0, 0.0, 1.0);\n  fc = uTint.rgb * max(fc.r, max(fc.g, fc.b));\n");
                sb.Append($"  o = vec4(fc * uExposure, {(p.Blend ? "outa" : "1.0")});\n}}\n");
            }
            else sb.Append($"  o = vec4(clamp(c0, 0.0, 1.0) * uExposure, {(p.Blend ? "outa" : "1.0")});\n}}\n");
            return sb.ToString();
        }

        // a normal-mapped material's extra inputs, declared as GxShader declares them (shared with its Vert)
        static string NmapUniforms(GxShader.Params p, bool pixChan)
        {
            var sb = new StringBuilder("in vec3 vP; in vec3 vN;\nuniform mat3 uViewRot;\n");
            if (pixChan)
            {
                sb.Append("uniform vec4 uChanMat[2]; uniform vec4 uChanAmb[2]; uniform vec3 uSceneAmb; uniform int uNumLights;\n");
                sb.Append("uniform vec3 uLightDir[4]; uniform vec3 uLightCol[4]; uniform vec3 uLightSpecCol[4]; uniform vec3 uLightSpecK[4];\n");
            }
            for (int s = 0; s < p.NumStages; s++)
            {
                if (p.Dst[s * 4 + 2] >= 0 && p.TgMode[p.Dst[s * 4 + 3]] == 1)
                {
                    sb.Append("uniform mat3 uTexMtx[8];\n");
                    break;
                }
            }
            return sb.ToString();
        }

        // GxShader.Frag's bend() + chanPix(): the normal map read in the surface's frame from screen derivatives, then
        // channel 0 lit by the bent normal (this channel's diffuse and attenuation functions written in)
        static string Bend(GxShader.Params p, bool pixChan)
        {
            var sb = new StringBuilder();
            sb.Append("  bool gPix = false; vec3 gNe = vec3(0.0, 0.0, 1.0);\n");
            sb.Append($"  vec2 nuv = vTc{p.NmapCoord}; vec3 N0 = normalize(vN);\n");
            sb.Append("  vec3 q1 = dFdx(vP), q2 = dFdy(vP); vec2 t1 = dFdx(nuv), t2 = dFdy(nuv);\n");
            sb.Append("  vec3 r1 = cross(q2, N0), r2 = cross(N0, q1);\n");
            sb.Append("  vec3 T = r1 * t1.x + r2 * t2.x, B = r1 * t1.y + r2 * t2.y;\n");
            sb.Append("  float m = max(dot(T, T), dot(B, B));\n");
            sb.Append($"  vec3 nt = texture(uTex{p.NmapUnit}, nuv).xyz * 2.0 - 1.0;\n");
            sb.Append("  if (m > 0.0) { m = inversesqrt(m); gNe = normalize(uViewRot * (T * (m * nt.x) + B * (m * nt.y) + N0 * nt.z)); gPix = true; }\n");
            sb.Append("  vec4 ch0 = vChan0;\n");
            if (pixChan)
            {
                int dif = p.ColCtrl[3];
                string d = dif == 0 ? "1.0" : dif == 1 ? "ndl" : "max(ndl, 0.0)";
                sb.Append("  if (gPix) {\n    vec3 il = uChanAmb[0].rgb * uSceneAmb;\n");
                sb.Append("    for (int i = 0; i < 4; i++) {\n      if (i >= uNumLights) break;\n");
                sb.Append("      vec3 L = uLightDir[i]; float ndl = dot(gNe, L);\n");
                if (p.Attn[0] == 1)
                {
                    sb.Append("      vec3 H = normalize(L + vec3(0.0, 0.0, 1.0));\n");
                    sb.Append("      float x = ndl > 0.0 ? max(dot(gNe, H), 0.0) : 0.0; vec3 k = uLightSpecK[i];\n");
                    sb.Append($"      il += uLightSpecCol[i] * ({d} * (x * x) / max(k.x + k.y * x + k.z * x * x, 1e-6));\n");
                }
                else sb.Append($"      il += uLightCol[i] * {d};\n");
                sb.Append("    }\n    ch0 = vec4(uChanMat[0].rgb * clamp(il, 0.0, 1.0), vChan0.a);\n  }\n");
            }
            return sb.ToString();
        }

        // the texture units the stages read (each needs its sampler uniform set to its own unit)
        public static SortedSet<int> Units(GxShader.Params p)
        {
            var units = new SortedSet<int>();
            for (int s = 0; s < p.NumStages; s++)
            {
                if (p.Dst[s * 4 + 2] >= 0) units.Add(Unit(p.Dst[s * 4 + 2]));
            }
            return units;
        }

        // GxShader.Frag's smp() reads uTex7 for any map past 7
        static int Unit(int texMap) => texMap < 8 ? texMap : 7;

        // d +/- lerp(a, b, c), biased, scaled, clamped (GX: clamp 0..1, else the -4..4 the interpreter keeps)
        static string Combine(string a, string b, string c, string d, int[] mod, int o, string one, string ctor)
        {
            string lerp = $"{a} * ({one} - {c}) + {b} * {c}";
            string x = mod[o] == 1 ? $"{d} - ({lerp})" : $"{d} + ({lerp})";
            string bias = mod[o + 1] switch { 1 => "0.5", 2 => "-0.5", _ => "" };
            if (bias != "") x = $"({x}) + {(ctor == "" ? bias : $"{ctor}({bias})")}";
            string scale = mod[o + 2] switch { 1 => "2.0", 2 => "4.0", 3 => "0.5", _ => "" };
            if (scale != "") x = $"({x}) * {scale}";
            return mod[o + 3] == 1 ? $"clamp({x}, 0.0, 1.0)" : $"clamp({x}, -4.0, 4.0)";
        }

        static string Swz(GxShader.Params p, int table)
        {
            const string rgba = "rgba";
            string s = "." + rgba[p.Swap[table * 4] & 3] + rgba[p.Swap[table * 4 + 1] & 3] + rgba[p.Swap[table * 4 + 2] & 3] + rgba[p.Swap[table * 4 + 3] & 3];
            return s == ".rgba" ? "" : s;
        }

        static string KonstC(int k) => k switch
        {
            <= 7 => $"vec3({F((8 - k) / 8f)})",
            >= 12 and <= 15 => $"uKonst[{k - 12}].rgb",
            >= 16 and <= 19 => $"vec3(uKonst[{k - 16}].r)",
            >= 20 and <= 23 => $"vec3(uKonst[{k - 20}].g)",
            >= 24 and <= 27 => $"vec3(uKonst[{k - 24}].b)",
            >= 28 and <= 31 => $"vec3(uKonst[{k - 28}].a)",
            _ => "vec3(0.0)",
        };

        static string KonstA(int k) => k switch
        {
            <= 7 => F((8 - k) / 8f),
            >= 16 and <= 19 => $"uKonst[{k - 16}].r",
            >= 20 and <= 23 => $"uKonst[{k - 20}].g",
            >= 24 and <= 27 => $"uKonst[{k - 24}].b",
            >= 28 and <= 31 => $"uKonst[{k - 28}].a",
            _ => "0.0",
        };

        static string Cmp(int f, float r) => f switch
        {
            0 => "false", 1 => $"outa < {F(r)}", 2 => $"abs(outa - {F(r)}) < 0.002", 3 => $"outa <= {F(r)}",
            4 => $"outa > {F(r)}", 5 => $"abs(outa - {F(r)}) >= 0.002", 6 => $"outa >= {F(r)}", _ => "true",
        };

        static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);
    }
}
