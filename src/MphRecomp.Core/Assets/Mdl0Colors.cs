using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace MphRecomp.Assets
{
    // Reads a Brawl .brres MDL0's per-material GX colour inputs -- the data the DAE export drops.
    // OUR own clean-room parser (no BrawlLib/third-party), so it can ship: the app only reads the
    // user's own extracted files, nothing bundled. This is the INPUT layer for the baked GX colour
    // evaluator (register + konst colours now; light-channel + TEV emulation build on top).
    //
    // MDL0 stores no final albedo -- the on-screen colour is the GX pipeline's OUTPUT (verified: the
    // yellow-port material has all-black registers yet renders yellow). So this reader exposes the
    // raw shader inputs; a separate evaluator computes the final colour from them.
    public sealed class Mdl0Material
    {
        public string Name = "";
        public readonly Vector3?[] Register = new Vector3?[3]; // C0,C1,C2 (TEV register colours, 0..1)
        public readonly Vector3?[] Konst = new Vector3?[4];    // K0..K3 (TEV constant colours, 0..1)
        public Vector3? MaterialColor;                          // light-channel 0 material colour (GXColor)
        public Vector3? AmbientColor;                           // light-channel 0 ambient colour (GXColor)
        public uint ColorControl;                               // light-channel 0 colour control (GX chan ctrl)

        // Whether this material is LIT (chan-ctrl bit1 = "enable lighting"). Kanden's muscle/armour are
        // lit (0x703); the yellow ports are UNLIT (0x700) -> they render as flat, full-brightness
        // material colour with no diffuse/reflection. THIS is the per-material difference the trophy
        // stores and the renderer must honour (unlit parts must NOT get the metallic reflection path).
        public bool Lit => (ColorControl & 0x2) != 0;
        // Chan-ctrl bit0 = material-colour source: 0 = register (use MaterialColor), 1 = per-vertex.
        public bool MatSrcVertex => (ColorControl & 0x1) != 0;
        // Duochrome lerp direction, read from this material's TEV stage-0 combine: false = lerp(C0->C1)
        // by lighting, true = lerp(C1->C0). Kanden's armour swaps; Sylux's don't -- so it MUST come from
        // the data per material, not a by-order heuristic. Only meaningful for Lit materials.
        public bool CombineSwap;
        // Per-stage GX scale factors, also read from the TEV: LerpScale = the colour-blend stage's
        // output scale, ReflScale = the reflection (xTEXC) stage's scale. Kanden = 2/2; Sylux = 1/4
        // (grey), 1/2 (navy) -- different per hunter, so read from the data. Final metal =
        // clamp(ReflScale * TEXC * clamp(LerpScale * mix(C0,C1,light))).
        public float LerpScale = 2f, ReflScale = 2f;
        // Output scale for a FLAT unlit material (its TEV is just RASC*scale): Kanden ports = 1,
        // Weavel's energy blade = 4 (207,44,0)*4 -> clamps to bright orange-gold. From the data.
        public float UnlitScale = 1f;

        // Pixel-engine render mode, read from the material's GX BP writes (0x41 CMODE0, 0xF3 ALPHAFUNC,
        // 0x40 ZMODE). These decide TRANSPARENCY, which MDL0 stores per material and the renderer must
        // honour or parts look wrong:
        //  - Additive: CMODE0 blend ON with dst=ONE -> the part ADDS onto the scene (Weavel's energy
        //    blade: a translucent orange glow, not a solid orange bar). Usually paired with no z-write.
        //  - DepthWrite: ZMODE write bit. Translucent parts (the blade) test depth but don't write it.
        //  - AlphaCutout/AlphaRef: ALPHAFUNC GEQ ref -> a hard cutout (Weavel's hair: keep texels whose
        //    opacity >= ref/255, discard the rest -> real negative space between strands, no black gaps).
        public bool Additive;
        public bool Translucent;   // CMODE0 blend ON with dst = INVSRCA -> regular alpha-blend see-through
        public bool DepthWrite = true;
        public bool AlphaCutout;
        public int AlphaRef = 128;

        // The part's true base colour: the TEV register C0 when it carries colour, else the
        // light-channel material colour (that's where the yellow ports live -- their registers are
        // black). Verified against Kanden: muscle=green C0, armour=olive C0, ports=yellow matColor.
        public Vector3 BaseColor
        {
            get
            {
                if (Register[0] is { } c && c.X + c.Y + c.Z > 0.05f) return c;
                if (MaterialColor is { } m && m.X + m.Y + m.Z > 0.05f) return m;
                return new Vector3(0.5f);
            }
        }
    }

    public static class Mdl0Colors
    {
        static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        // Parse every material's register/konst colours from the first MDL0 in a .brres.
        public static List<Mdl0Material> Read(string brresPath)
        {
            byte[] b = File.ReadAllBytes(brresPath);
            var result = new List<Mdl0Material>();
            int mdl0 = Find(b, 0, "MDL0");
            if (mdl0 < 0) return result;
            int end = (int)Math.Min(b.Length, mdl0 + U32(b, mdl0 + 4));

            // Find the MATERIALS resource group: a group (u32 size, u32 count, (count+1) 0x10-byte
            // entries) whose first entry's struct is material-sized (~0x300-0x800) with a valid header
            // (size then a negative back-offset to the MDL0). Robust across hunters -- no name keying.
            for (int g = mdl0; g < end - 0x40; g += 4)
            {
                uint count = U32(b, g + 4);
                if (count < 1 || count > 64 || g + 8 + (count + 1) * 0x10 > end) continue;
                int e1 = g + 8 + 0x10;
                foreach (bool grpRel in new[] { true, false })
                {
                    int @base = grpRel ? g : mdl0;
                    int st1 = @base + (int)U32(b, e1 + 0xC);
                    if (st1 <= mdl0 || st1 >= end - 8) continue;
                    uint sz = U32(b, st1);
                    if (sz < 0x300 || sz > 0x800) continue;          // material-sized
                    if ((U32(b, st1 + 4) & 0xFF000000) != 0xFF000000) continue; // negative mdl0 back-offset
                    // looks like the materials group -- read each material
                    for (int i = 1; i <= count; i++)
                    {
                        int e = g + 8 + i * 0x10;
                        int st = @base + (int)U32(b, e + 0xC);
                        int lo = st, hi = (int)Math.Min(end, st + U32(b, st));
                        var m = new Mdl0Material { Name = Str(b, @base + (int)U32(b, e + 8)) };
                        for (int c = 0; c < 3; c++) m.Register[c] = Color(b, lo, hi, 0xE2 + c * 2, konst: false);
                        for (int k = 0; k < 4; k++) m.Konst[k] = Color(b, lo, hi, 0xE0 + k * 2, konst: true);
                        // Light-channel 0 block (each channel: flags,matColor,ambColor,colorCtrl,alphaCtrl
                        // -- GXColors are RGBA bytes). matColor@+0x3F0 is where the yellow ports hide;
                        // ambColor@+0x3F4; colorCtrl@+0x3F8 carries the lit/unlit + colour-source flags.
                        if (st + 0x3F3 < b.Length)
                            m.MaterialColor = new Vector3(b[st + 0x3F0] / 255f, b[st + 0x3F1] / 255f, b[st + 0x3F2] / 255f);
                        if (st + 0x3F7 < b.Length)
                            m.AmbientColor = new Vector3(b[st + 0x3F4] / 255f, b[st + 0x3F5] / 255f, b[st + 0x3F6] / 255f);
                        if (st + 0x3FB < b.Length)
                            m.ColorControl = U32(b, st + 0x3F8);
                        ReadPeMode(b, st, hi, m);   // blend / alpha-compare / z-write (transparency)
                        result.Add(m);
                    }
                    AssignCombineSwaps(b, mdl0, end, result);
                    return result;
                }
            }
            return result;
        }

        // Read a material's pixel-engine transparency mode from the GX BP writes in its byte range:
        //   0x41 CMODE0  -> blend enable + src/dst factors (dst=ONE => additive glow)
        //   0xF3 ALPHAFUNC -> alpha compare (GEQ ref => a hard cutout; the hair opacity mask)
        //   0x40 ZMODE   -> depth write bit (translucent parts don't write z)
        static void ReadPeMode(byte[] b, int st, int hi, Mdl0Material m)
        {
            for (int k = st; k < hi - 4 && k < b.Length - 4; k++)
            {
                if (b[k] != 0x61) continue;
                int reg = b[k + 1];
                uint data = (uint)((b[k + 2] << 16) | (b[k + 3] << 8) | b[k + 4]);
                if (reg == 0x41)                       // CMODE0: blend
                {
                    bool en = (data & 1) != 0;
                    int dst = (int)((data >> 5) & 7);  // 1 = ONE (additive), 5 = INVSRCA (alpha blend)
                    m.Additive = en && dst == 1;
                    m.Translucent = en && dst == 5;
                }
                else if (reg == 0xF3)                  // ALPHAFUNC: alpha compare
                {
                    int ref0 = (int)(data & 0xFF), c0 = (int)((data >> 16) & 7);
                    if (c0 == 6) { m.AlphaCutout = true; m.AlphaRef = ref0; } // 6 = GEQ
                }
                else if (reg == 0x40)                  // ZMODE: depth write bit4
                {
                    m.DepthWrite = ((data >> 4) & 1) != 0;
                }
            }
        }

        // Read the full duochrome combine for each LIT material from the model's TEV program: the lerp
        // direction (swap) AND the two GX stage scales (lerp stage, reflection stage). The metal shader
        // is a lerp stage (C0<->C1 by RASC) immediately followed by an xTEXC (reflection) stage. These
        // sit contiguously near the MDL0 start (before binary-noise false positives), one pair per LIT
        // material in material order, so we take the first nLit and assign in order. Everything per-model:
        // Kanden = {no-swap 2/2, swap 2/2}; Sylux = {no-swap 1/4, no-swap 1/2}.
        static readonly float[] GxScale = { 1f, 2f, 4f, 0.5f };  // GX scale enum -> factor
        static void AssignCombineSwaps(byte[] b, int mdl0, int end, List<Mdl0Material> mats)
        {
            int nLit = 0; foreach (var m in mats) if (m.Lit) nLit++;
            if (nLit == 0) return;
            var found = new List<(bool swap, float lerpScale, float reflScale)>();
            for (int i = mdl0; i < end - 5 && found.Count < nLit; i++)
            {
                if (b[i] != 0x61) continue;
                int addr = b[i + 1];
                if (addr < 0xC0 || addr > 0xDF || (addr & 1) != 0) continue;   // TEV colour-env reg
                uint data = (uint)((b[i + 2] << 16) | (b[i + 3] << 8) | b[i + 4]);
                int d = (int)(data & 0xF), c = (int)((data >> 4) & 0xF), bb = (int)((data >> 8) & 0xF), a = (int)((data >> 12) & 0xF);
                // duochrome lerp stage: mix C0(2)/C1(4) by RASC(10), no bias (d=ZERO=15)
                if (!(c == 10 && d == 15 && (a == 2 || a == 4) && (bb == 2 || bb == 4) && a != bb)) continue;
                float lerpScale = GxScale[(data >> 20) & 3];
                // the immediately-following xTEXC stage carries the reflection scale (TEXC=8 * CPREV=0)
                float reflScale = 2f;
                for (int j = i + 5; j < Math.Min(end - 5, i + 0x40); j++)
                {
                    if (b[j] != 0x61) continue;
                    int a2 = b[j + 1]; if (a2 < 0xC0 || a2 > 0xDF || (a2 & 1) != 0) continue;
                    uint d2 = (uint)((b[j + 2] << 16) | (b[j + 3] << 8) | b[j + 4]);
                    int bb2 = (int)((d2 >> 8) & 0xF), c2 = (int)((d2 >> 4) & 0xF);
                    if (bb2 == 8 && c2 == 0) { reflScale = GxScale[(d2 >> 20) & 3]; break; } // TEXC*CPREV
                    if (c2 == 10) break; // hit the next material's lerp stage first
                }
                found.Add((a == 4, lerpScale, reflScale));
            }
            int s = 0;
            foreach (var m in mats) if (m.Lit && s < found.Count) { m.CombineSwap = found[s].swap; m.LerpScale = found[s].lerpScale; m.ReflScale = found[s].reflScale; s++; }

            // Flat unlit materials (ports, blade): their whole TEV is just `RASC * scale` (one input is
            // RASC, the rest ZERO, no texture). Find the first such stage's scale and apply it to every
            // unlit material -- Kanden ports = 1 (no change), Weavel blade = 4 (-> orange-gold).
            float unlitScale = 1f;
            for (int i = mdl0; i < end - 5; i++)
            {
                if (b[i] != 0x61) continue;
                int addr = b[i + 1];
                if (addr < 0xC0 || addr > 0xDF || (addr & 1) != 0) continue;
                uint data = (uint)((b[i + 2] << 16) | (b[i + 3] << 8) | b[i + 4]);
                int d = (int)(data & 0xF), c = (int)((data >> 4) & 0xF), bb = (int)((data >> 8) & 0xF), a = (int)((data >> 12) & 0xF);
                int rascN = (a == 10 ? 1 : 0) + (bb == 10 ? 1 : 0) + (c == 10 ? 1 : 0) + (d == 10 ? 1 : 0);
                int zeroN = (a == 15 ? 1 : 0) + (bb == 15 ? 1 : 0) + (c == 15 ? 1 : 0) + (d == 15 ? 1 : 0);
                if (rascN == 1 && zeroN == 3) { unlitScale = GxScale[(data >> 20) & 3]; break; }
            }
            foreach (var m in mats) if (!m.Lit) m.UnlitScale = unlitScale;
        }

        // Decode a GX TEV colour loaded via BP: even reg (RA) + odd reg (BG). Register colours have
        // bit23=0 (s10), konst colours bit23=1 (8-bit). We match the requested kind so register vs
        // konst loads to the same E-address don't collide.
        static Vector3? Color(byte[] buf, int lo, int hi, int loReg, bool konst)
        {
            for (int i = lo; i < hi - 9 && i < buf.Length - 9; i++)
            {
                if (buf[i] != 0x61 || buf[i + 1] != loReg) continue;
                uint dLo = (uint)((buf[i + 2] << 16) | (buf[i + 3] << 8) | buf[i + 4]);
                if (((dLo & 0x800000) != 0) != konst) continue;
                for (int j = i + 5; j < Math.Min(hi - 4, i + 0x30); j++)
                {
                    if (buf[j] != 0x61 || buf[j + 1] != loReg + 1) continue;
                    uint dHi = (uint)((buf[j + 2] << 16) | (buf[j + 3] << 8) | buf[j + 4]);
                    int r = (int)(dLo & 0x7FF), gr = (int)((dHi >> 12) & 0x7FF), bl = (int)(dHi & 0x7FF);
                    return new Vector3(r / 255f, gr / 255f, bl / 255f);
                }
            }
            return null;
        }

        static string Str(byte[] b, int o) { var sb = new StringBuilder(); for (int i = o; i < b.Length && b[i] >= 32 && b[i] < 127; i++) sb.Append((char)b[i]); return sb.ToString(); }
        static int Find(byte[] b, int start, string s)
        { byte[] p = Encoding.ASCII.GetBytes(s); for (int i = Math.Max(0, start); i <= b.Length - p.Length; i++) { bool ok = true; for (int j = 0; j < p.Length; j++) if (b[i + j] != p[j]) { ok = false; break; } if (ok) return i; } return -1; }
    }
}
