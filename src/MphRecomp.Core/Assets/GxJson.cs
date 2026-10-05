using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace MphRecomp.Assets
{
    // GX programs for an HD model that has no .brres: "<dae>.gx.json", written on the PC (prime_to_trophy.py turns a
    // Retro material into it; the owner's own disc, never shipped). The same GxMaterial the .brres parser builds, so the
    // GX interpreter runs it unchanged. Per material:
    //   name        = the .dae material it drives
    //   stages[]    = c[4] cop cbias cscale cclamp cdest / a[4] aop abias ascale aclamp adest / texmap texcoord ras kc ka
    //                 (GX enums as GxTevStage holds them; ras 0 = channel 0, 1 = channel 1, 7 = zero)
    //   konst[4]    = rgba 0..1;  lit = light channel 0 on (else the material colour as is)
    //   layers[]    = name (texture = <name>.png), texmap, texcoord, env (camera-space normal map instead of the UVs),
    //                 mtx[6] (static UV matrix: s' = m0 s + m1 t + m2, t' = m3 s + m4 t + m5), scroll[2] (UV per second),
    //                 nmap (optional: a tangent-space normal map, rgb = n x 0.5 + 0.5 with green along +t -- no TEV stage
    //                 reads it; it bends the normal that lights channel 0 and the reflection maps, per pixel)
    //   blend blendSrc blendDst depthWrite alphaTest cull
    public static class GxJson
    {
        public static string PathFor(string daePath) => Path.ChangeExtension(daePath, ".gx.json");

        public static List<GxMaterial> Read(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var res = new List<GxMaterial>();
            foreach (var jm in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                var m = new GxMaterial { Name = jm.GetProperty("name").GetString() ?? "" };
                var stages = new List<GxTevStage>();
                foreach (var js in jm.GetProperty("stages").EnumerateArray())
                {
                    int[] c = Ints(js.GetProperty("c")), a = Ints(js.GetProperty("a"));
                    stages.Add(new GxTevStage
                    {
                        Ca = c[0], Cb = c[1], Cc = c[2], Cd = c[3], Cop = I(js, "cop"), Cbias = I(js, "cbias"), Cscale = I(js, "cscale"), Cclamp = I(js, "cclamp"), Cdest = I(js, "cdest"),
                        Aa = a[0], Ab = a[1], Ac = a[2], Ad = a[3], Aop = I(js, "aop"), Abias = I(js, "abias"), Ascale = I(js, "ascale"), Aclamp = I(js, "aclamp"), Adest = I(js, "adest"),
                        TexMap = I(js, "texmap"), TexCoord = I(js, "texcoord"), RasChan = I(js, "ras"), KcSel = I(js, "kc"), KaSel = I(js, "ka"),
                    });
                }
                m.Stages = stages.ToArray(); m.NumTevStage = stages.Count;
                int k = 0;
                foreach (var jk in jm.GetProperty("konst").EnumerateArray())
                {
                    if (k >= 4) break;
                    var v = Floats(jk); m.Konst[k++] = new Vector4(v[0], v[1], v[2], v[3]);
                }
                // light channel 0: lit = material colour (white) x clamp(ambient + the scene's light, clamped diffuse); the
                // alpha is the material's (1)
                bool lit = jm.GetProperty("lit").GetBoolean();
                uint ctrl = lit ? 0x2u | 0x4u | (2u << 7) : 0u;
                var ch = new GxChannel { Mat = Vector4.One, Amb = Vector4.One, ColorCtrl = ctrl, AlphaCtrl = 0 };
                m.Chan[0] = ch; m.Chan[1] = ch; m.NumChans = 1; m.ColorControl = ctrl;
                int ng = 0;
                foreach (var jl in jm.GetProperty("layers").EnumerateArray())
                {
                    int tc = I(jl, "texcoord"), tm = I(jl, "texmap");
                    if (tc < 0 || tc >= 8 || tm < 0 || tm >= 8) continue;
                    bool env = jl.GetProperty("env").GetBoolean();
                    var mtx = jl.TryGetProperty("mtx", out var jx) ? Floats(jx) : new[] { 1f, 0, 0, 0, 1, 0 };
                    var sc = jl.TryGetProperty("scroll", out var jsc) ? Floats(jsc) : new[] { 0f, 0 };
                    m.Layers[tc] = new GxLayer
                    {
                        Name = jl.GetProperty("name").GetString(), TexMapId = tm, MapMode = env ? 1 : 0,
                        UvMtx = new[] { mtx[0], mtx[3], 0, mtx[1], mtx[4], 0, mtx[2], mtx[5], 1 },   // column-major mat3
                        ScrollPerSec = new Vector2(sc[0], sc[1]),
                    };
                    m.TexNames[tm] = jl.GetProperty("name").GetString();
                    m.TexGenSrc[tc] = env ? 1 : 5; m.TexCoordSrc[tc] = env ? 1 : 0;
                    if (jl.TryGetProperty("nmap", out var jn) && jn.GetBoolean()) { m.NormalMap = tm; m.NormalMapCoord = tc; }
                    ng = Math.Max(ng, tc + 1);
                }
                m.NumTexGen = ng;
                m.BlendEnable = jm.GetProperty("blend").GetBoolean();
                m.BlendSrc = I(jm, "blendSrc"); m.BlendDst = I(jm, "blendDst");
                m.DepthWrite = jm.GetProperty("depthWrite").GetBoolean();
                if (jm.GetProperty("alphaTest").GetBoolean()) { m.AlphaTest = true; m.AlphaComp0 = 6; m.AlphaRef0 = 128; m.AlphaComp1 = 7; m.AlphaLogic = 0; }
                m.CullMode = I(jm, "cull");
                res.Add(m);
            }
            return res;
        }

        static int I(JsonElement e, string n) => e.GetProperty(n).GetInt32();
        static int[] Ints(JsonElement e) { var l = new List<int>(); foreach (var x in e.EnumerateArray()) l.Add(x.GetInt32()); return l.ToArray(); }
        static float[] Floats(JsonElement e) { var l = new List<float>(); foreach (var x in e.EnumerateArray()) l.Add((float)x.GetDouble()); return l.ToArray(); }
    }
}
