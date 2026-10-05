using System;
using System.Collections.Generic;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Retro3
{
    // A Metroid Prime 3 material's texture passes turned into a GX program shaped like Retro's older (Prime 1 / Echoes)
    // stored ones, so the same GX conversion downstream reads both. Port of prime3_char.py's to_gx_material:
    //   CLR  (diffuse)    stage: texture x lit colour
    //   INCA (glow)       stage: + texture                    (a scrolling glow keeps its scroll)
    //   RFLV + RFLD       stage: reflectivity -> C2; stage: + C2 x reflection map (camera-space normals)
    //   TRAN / OPAC       see-through: alpha from the TRAN texture, or the OPAC value
    // XRAY, bloom (BLO*) and the other passes are ignored.
    public sealed class GxMaterial3
    {
        public uint Flags;
        public int[] Tex = Array.Empty<int>();            // 0..n-1
        public ulong[] TexIds = Array.Empty<ulong>();     // TXTR ids, one per texture slot (a texture may repeat)
        public uint VertexAttributes;
        public uint Group;
        public int[][] Konst = Array.Empty<int[]>();      // rgba 0..255 (the alpha may be OPAC's value)
        public int BlendDst, BlendSrc;
        public uint[] Channels = Array.Empty<uint>();
        public Cmdl.TevStage[] Stages = Array.Empty<Cmdl.TevStage>();
        public Cmdl.TexGen[] TexGens = Array.Empty<Cmdl.TexGen>();
        public Cmdl.UvAnim[] UvAnims = Array.Empty<Cmdl.UvAnim>();
    }

    public static class Material3Gx
    {
        // TEV inputs
        const int CPrev = 0, C2 = 6, TexC = 8, RasC = 10, One = 12, Zero = 15;
        const int AZero = 7, ATexA = 4, AKonst = 6, APrev = 0;

        static Cmdl.TevStage Stage(int[] ci, int[] ai, int tex, int tc, int outReg = 0, int ras = 6, int kc = 0, int ka = 0)
        {
            var s = new Cmdl.TevStage
            {
                ColorIn = (int[])ci.Clone(), AlphaIn = (int[])ai.Clone(),
                ColorOp = new Cmdl.TevOp { Clamp = 1, Op = 0, Bias = 0, Scale = 0, Out = outReg },
                AlphaOp = new Cmdl.TevOp { Clamp = 1, Op = 0, Bias = 0, Scale = 0, Out = 0 },
                KAlpha = ka, KColor = kc, Ras = ras, Tex = tex, TexCoord = tc,
            };
            return s;
        }

        public static GxMaterial3 ToGx(Cmdl3.Material mm)
        {
            // the passes by type (a repeated type: the last one counts, like the reference's dict)
            var P = new Dictionary<string, Cmdl3.Pass>(StringComparer.Ordinal);
            foreach (Cmdl3.Pass p in mm.Passes) P[p.Type] = p;
            var tex = new List<ulong>();
            var gens = new List<Cmdl.TexGen>();
            var anims = new List<Cmdl.UvAnim>();
            var st = new List<Cmdl.TevStage>();
            int AddTex(Cmdl3.Pass p, bool env = false)
            {
                tex.Add(p.Tex);
                int ti = tex.Count - 1;
                Cmdl.UvAnim? a = p.Anims.Count > 0 ? p.Anims[0] : null;
                if (env || (a != null && a.Mode is 0 or 1))
                {
                    gens.Add(new Cmdl.TexGen { Type = 0, Src = 1, Mtx = 0, Normalize = true, Post = 0 });
                    anims.Add(new Cmdl.UvAnim { Mode = 0, Params = Array.Empty<double>() });
                }
                else if (a != null && a.Mode == 2)
                {
                    gens.Add(new Cmdl.TexGen { Type = 0, Src = 4, Mtx = 3 * anims.Count, Normalize = false, Post = 61 });
                    anims.Add(new Cmdl.UvAnim { Mode = 2, Params = (double[])a.Params.Clone() });
                }
                else
                {
                    gens.Add(new Cmdl.TexGen { Type = 0, Src = 4, Mtx = 30, Normalize = false, Post = 61 });
                }
                return ti;
            }
            uint? opac = mm.Int("OPAC");
            if (P.TryGetValue("CLR ", out Cmdl3.Pass? clr))
            {
                int t = AddTex(clr);
                st.Add(Stage(new[] { Zero, RasC, TexC, Zero }, new[] { AZero, AZero, AZero, opac != null ? AKonst : ATexA }, t, t, ras: 4, ka: 0x1C));
            }
            else
            {
                st.Add(Stage(new[] { Zero, Zero, Zero, Zero }, new[] { AZero, AZero, AZero, AKonst }, 255, 0, ka: 0x1C));
            }
            if (P.TryGetValue("INCA", out Cmdl3.Pass? inca))
            {
                int t = AddTex(inca);
                st.Add(Stage(new[] { Zero, TexC, One, CPrev }, new[] { AZero, AZero, AZero, APrev }, t, t));
            }
            if (P.TryGetValue("RFLV", out Cmdl3.Pass? rflv) && P.TryGetValue("RFLD", out Cmdl3.Pass? rfld))
            {
                int t = AddTex(rflv);
                st.Add(Stage(new[] { Zero, Zero, Zero, TexC }, new[] { AZero, AZero, AZero, APrev }, t, t, outReg: 3));
                t = AddTex(rfld, env: true);
                st.Add(Stage(new[] { Zero, C2, TexC, CPrev }, new[] { AZero, AZero, AZero, APrev }, t, t));
            }
            if (P.TryGetValue("TRAN", out Cmdl3.Pass? tran))
            {
                int t = AddTex(tran);
                st.Add(Stage(new[] { Zero, Zero, Zero, CPrev }, new[] { AZero, AZero, AZero, ATexA }, t, t));
            }
            bool see = opac != null || P.ContainsKey("TRAN");
            var texIdx = new int[tex.Count];
            for (int i = 0; i < texIdx.Length; i++) texIdx[i] = i;
            return new GxMaterial3
            {
                Flags = see ? 0x10u : 0u, Tex = texIdx, TexIds = tex.ToArray(), VertexAttributes = mm.VertexAttributes, Group = 0,
                Konst = new[] { new[] { 255, 255, 255, opac != null ? (int)opac.Value : 255 } },
                BlendDst = see ? 5 : 0, BlendSrc = see ? 4 : 1, Channels = new uint[] { 0x3001 },
                Stages = st.ToArray(), TexGens = gens.ToArray(), UvAnims = anims.ToArray(),
            };
        }
    }
}
