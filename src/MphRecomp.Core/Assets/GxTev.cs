using System;
using System.Numerics;

namespace MphRecomp.Assets
{
    // PHASE 2 (evaluator core): faithfully EXECUTE a parsed GxMaterial's TEV combine program to a final
    // RGBA. This is the CPU reference the GLSL interpreter (RenderActivity) mirrors -- validated headlessly
    // (no device) so the shader is a PORT of proven logic, not a fresh guess. It runs the same combine the
    // GameCube/Wii GX pixel pipeline runs: per stage,
    //     out = clamp( ((d [op] ((1-c)*a + c*b)) + bias) * scale )
    // registers CPREV/C0/C1/C2 thread between stages; RASC/RASA (rasterised light colour), TEXC/TEXA
    // (sampled texture) and KONST feed in. duochrome / metal / energy / glow all emerge from this program
    // -- there are no per-material special cases. Reads only the user's own extracted data (via Mdl0Gx).
    public static class GxTev
    {
        public struct Inputs
        {
            public Vector3 Rasc;              // rasterised colour channel (RASC) -- caller computes via Rasterize()
            public float Rasa;                // rasterised alpha (RASA)
            public Func<int, Vector4>? Tex;   // texmap index -> sampled RGBA (0..1); null => black (TEXC/TEXA=0)
        }

        // Rasterised colour for light-channel 0. Lit materials modulate matColor by (ambient + the ONE
        // global scene light); unlit materials show flat matColor (no lighting). Models carry no light
        // objects, so the scene light is the single external rig -- matches BrawlCrate's studio light /
        // the arena light. NOTE: this is the scene input; the exact rig is tuned on-device, but the combine
        // that consumes RASC is fully determined here.
        public static Vector3 Rasterize(GxMaterial m, Vector3 sceneLight)
            => m.Lit ? m.MatColor * Clamp01(m.AmbColor + sceneLight) : m.MatColor;

        public static Vector4 Evaluate(GxMaterial m, in Inputs inp)
        {
            // GX TEV registers: index 0 = PREV, 1 = C0, 2 = C1, 3 = C2 (the colour-input enum maps here).
            Vector4 r0 = m.Reg[0], r1 = m.Reg[1], r2 = m.Reg[2], r3 = m.Reg[3];
            Span<Vector4> reg = stackalloc Vector4[4] { r0, r1, r2, r3 };
            foreach (var s in m.Stages)
            {
                if (s == null) continue;
                Vector4 tex = (s.TexMap >= 0 && inp.Tex != null) ? inp.Tex(s.TexMap) : Vector4.Zero;

                // Gather ALL inputs before writing (GX reads current regs, commits at stage end).
                Vector3 ca = CIn(s.Ca, reg, tex, inp, m, s.KcSel);
                Vector3 cb = CIn(s.Cb, reg, tex, inp, m, s.KcSel);
                Vector3 cc = CIn(s.Cc, reg, tex, inp, m, s.KcSel);
                Vector3 cd = CIn(s.Cd, reg, tex, inp, m, s.KcSel);
                Vector3 rgb = Combine3(ca, cb, cc, cd, s.Cop, s.Cbias, s.Cscale, s.Cclamp);

                float aa = AIn(s.Aa, reg, tex, inp, m, s.KaSel);
                float ab = AIn(s.Ab, reg, tex, inp, m, s.KaSel);
                float ac = AIn(s.Ac, reg, tex, inp, m, s.KaSel);
                float ad = AIn(s.Ad, reg, tex, inp, m, s.KaSel);
                float alpha = Combine1(aa, ab, ac, ad, s.Aop, s.Abias, s.Ascale, s.Aclamp);

                int cDst = s.Cdest & 3, aDst = s.Adest & 3;
                reg[cDst] = new Vector4(rgb, reg[cDst].W);          // colour write keeps that reg's alpha
                reg[aDst] = new Vector4(reg[aDst].X, reg[aDst].Y, reg[aDst].Z, alpha); // alpha write keeps colour
            }
            return reg[0]; // GX_TEVPREV is the framebuffer colour.
        }

        // ---- colour combine: ((d op lerp(a,b,c)) + bias) * scale, optional clamp ----
        static Vector3 Combine3(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int op, int bias, int scale, int clamp)
        {
            Vector3 lerp = a * (Vector3.One - c) + b * c;
            Vector3 r = (op == 1) ? d - lerp : d + lerp;
            r += new Vector3(Bias(bias));
            r *= Scale(scale);
            return clamp == 1 ? Clamp01(r) : Clamp(r, -4f, 4f);
        }

        static float Combine1(float a, float b, float c, float d, int op, int bias, int scale, int clamp)
        {
            float lerp = a * (1f - c) + b * c;
            float r = (op == 1) ? d - lerp : d + lerp;
            r = (r + Bias(bias)) * Scale(scale);
            return clamp == 1 ? Math.Clamp(r, 0f, 1f) : Math.Clamp(r, -4f, 4f);
        }

        static float Bias(int b) => b == 1 ? 0.5f : b == 2 ? -0.5f : 0f;   // 0:0  1:+0.5  2:-0.5
        static float Scale(int s) => s == 1 ? 2f : s == 2 ? 4f : s == 3 ? 0.5f : 1f; // 0:*1 1:*2 2:*4 3:/2

        // Colour input enum 0..15: CPREV,APREV,C0,A0,C1,A1,C2,A2,TEXC,TEXA,RASC,RASA,ONE,HALF,KONST,ZERO
        static Vector3 CIn(int sel, Span<Vector4> reg, Vector4 tex, in Inputs inp, GxMaterial m, int kcSel) => sel switch
        {
            0 => V3(reg[0]),                       // CPREV
            1 => new Vector3(reg[0].W),            // APREV
            2 => V3(reg[1]),                       // C0
            3 => new Vector3(reg[1].W),            // A0
            4 => V3(reg[2]),                       // C1
            5 => new Vector3(reg[2].W),            // A1
            6 => V3(reg[3]),                       // C2
            7 => new Vector3(reg[3].W),            // A2
            8 => V3(tex),                          // TEXC
            9 => new Vector3(tex.W),               // TEXA
            10 => inp.Rasc,                         // RASC
            11 => new Vector3(inp.Rasa),           // RASA
            12 => Vector3.One,                      // ONE
            13 => new Vector3(0.5f),               // HALF
            14 => KonstColor(m, kcSel),            // KONST
            _ => Vector3.Zero,                      // ZERO (15)
        };

        // Alpha input enum 0..7: APREV,A0,A1,A2,TEXA,RASA,KONST,ZERO
        static float AIn(int sel, Span<Vector4> reg, Vector4 tex, in Inputs inp, GxMaterial m, int kaSel) => sel switch
        {
            0 => reg[0].W,   // APREV
            1 => reg[1].W,   // A0
            2 => reg[2].W,   // A1
            3 => reg[3].W,   // A2
            4 => tex.W,      // TEXA
            5 => inp.Rasa,   // RASA
            6 => KonstAlpha(m, kaSel), // KONST
            _ => 0f,         // ZERO (7)
        };

        // KSEL konst-value decode (GX_TEV_KCSEL): 0x00-0x07 constant fractions 1.0..1/8; 0x0C-0x0F K0-K3
        // rgb; 0x10-0x1F single-channel replicate (R/G/B/A of K0-K3). Selectors we don't recognise -> 0.
        static Vector3 KonstColor(GxMaterial m, int sel)
        {
            if (sel >= 0x00 && sel <= 0x07) return new Vector3((8 - sel) / 8f);
            if (sel >= 0x0C && sel <= 0x0F) return V3(m.Konst[sel - 0x0C]);
            if (sel >= 0x10 && sel <= 0x13) return new Vector3(m.Konst[sel - 0x10].X);
            if (sel >= 0x14 && sel <= 0x17) return new Vector3(m.Konst[sel - 0x14].Y);
            if (sel >= 0x18 && sel <= 0x1B) return new Vector3(m.Konst[sel - 0x18].Z);
            if (sel >= 0x1C && sel <= 0x1F) return new Vector3(m.Konst[sel - 0x1C].W);
            return Vector3.Zero;
        }

        // GX_TEV_KASEL: 0x00-0x07 constant fractions; 0x10-0x1F single-channel R/G/B/A of K0-K3.
        static float KonstAlpha(GxMaterial m, int sel)
        {
            if (sel >= 0x00 && sel <= 0x07) return (8 - sel) / 8f;
            if (sel >= 0x10 && sel <= 0x13) return m.Konst[sel - 0x10].X;
            if (sel >= 0x14 && sel <= 0x17) return m.Konst[sel - 0x14].Y;
            if (sel >= 0x18 && sel <= 0x1B) return m.Konst[sel - 0x18].Z;
            if (sel >= 0x1C && sel <= 0x1F) return m.Konst[sel - 0x1C].W;
            return 0f;
        }

        static Vector3 V3(Vector4 v) => new Vector3(v.X, v.Y, v.Z);
        static Vector3 Clamp01(Vector3 v) => Clamp(v, 0f, 1f);
        static Vector3 Clamp(Vector3 v, float lo, float hi) =>
            new Vector3(Math.Clamp(v.X, lo, hi), Math.Clamp(v.Y, lo, hi), Math.Clamp(v.Z, lo, hi));
    }
}
