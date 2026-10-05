using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace MphRecomp.Assets
{
    // PHASE 1 of the HD GX-TEV evaluator: a clean-room parser that reads each material's FULL GX program
    // from a Brawl .brres MDL0 -- the TEV combine stages, register/konst colours, light channels, texture
    // bindings, cull, and pixel-engine mode. The GLSL interpreter (Phase 2) executes this data faithfully,
    // so duochrome / metal / energy / glow / translucent all fall out by construction instead of bespoke
    // hand-tuned shader paths. Our own code (no BrawlLib); reads only the user's own extracted files.
    //
    // Navigation (Phase-0 recon, uniform across all models): section-offset table at mdl0+0x10; section i
    // = U32(mdl0+0x10+i*4) rel mdl0. v9 order: 6=Materials, 7=Shaders(0x200-byte TEV blocks), 9=Textures.
    // Resource-group entry name/data offsets are rel the GROUP start; names point into the file-level
    // string pool AFTER the MDL0 (bound by file length, NOT mdl0 size). Material struct fields:
    //   +0x14 numTexGens, +0x16 numTevStages, +0x18 cull, +0x2C numTextures,
    //   +0x414 texture-layer list (stride 0x34; layer+0x00 = texName offset, LAYER-relative),
    //   +0x3F0 light-channel 0 (matColor GXColor, +4 ambColor, +8 colorControl).
    // TEV register/konst colours load via BP 0xE0..0xE7 (RA/BG s10 words); PE mode via BP 0x41/0xF3/0x40.
    public sealed class GxTevStage
    {
        // colour combine: out = (d [op] (a*(1-c) + b*c)) [bias] * scale, optionally clamped. Inputs 0..15.
        public int Ca, Cb, Cc, Cd, Cbias, Cop, Cclamp, Cscale, Cdest;
        // alpha combine: inputs 0..7 (GX alpha input enum).
        public int Aa, Ab, Ac, Ad, Abias, Aop, Aclamp, Ascale, Adest;
        public int TexMap = -1, TexCoord, RasChan;   // TexMap -1 = stage samples no texture
        public int KcSel = 0xFF, KaSel = 0xFF;        // konst selectors (0xFF = default/not set)
        public int RasSwap, TexSwap;                  // swap table used for the raster colour / the texture sample
    }

    // One light channel (GX colour + alpha control): out = mat * clamp(amb + sum of lights), or just mat when unlit.
    // Control word: bit0 material source (1 = vertex colour), bit1 lighting on, bits2-5 + 11-14 light mask, bit6
    // ambient source (1 = vertex colour), bits7-8 diffuse function (0 none, 1 signed, 2 clamped), bits9-10
    // attenuation (bit9 on, bit10 select: 1 = specular, 3 = spotlight/directional, 0/2 = none).
    public struct GxChannel
    {
        public Vector4 Mat, Amb;
        public uint ColorCtrl, AlphaCtrl;
        public static bool VtxMat(uint c) => (c & 1) != 0;
        public static bool Lit(uint c) => (c & 2) != 0;
        public static bool VtxAmb(uint c) => (c & 0x40) != 0;
        public static int DiffFn(uint c) => (int)((c >> 7) & 3);
        public static int Attn(uint c) => (int)((c >> 9) & 3);
    }

    // One texture layer of a material: sampler state, its texture transform (scale / rotation in degrees /
    // translation) and how its coordinates are produced (map mode: 0 = the mesh's UVs, 1 = camera-space normals
    // (a reflection sphere map), 2 = projection, 3 = light-space normals, 4 = specular (half-vector) map).
    public sealed class GxLayer
    {
        public string? Name;
        public int TexMapId, WrapS = 1, WrapT = 1, MinFilter = 1, MagFilter = 1;   // wrap 0 clamp 1 repeat 2 mirror
        public float LodBias;
        public Vector2 Scale = Vector2.One, Trans; public float RotDeg;
        public int MapMode; public int CamRef = -1, LightRef = -1; public bool IdentityEffect = true;
        public float[] Effect = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };   // 3x4 row-major
        // a model without a .brres (GxJson): its UV matrix as is (column-major mat3, replaces the Maya transform), and a
        // steady UV scroll per second (Retro's scrolling glow layers) -- added at draw time
        public float[]? UvMtx; public Vector2 ScrollPerSec;
    }

    public sealed class GxMaterial
    {
        public string Name = "";
        public int NumTexGen, NumTevStage, CullMode;
        public GxTevStage[] Stages = Array.Empty<GxTevStage>();
        public Vector4[] Reg = new Vector4[4];    // TEV register colours reg0(PREV)..reg3 (rgba, s10; may exceed 1)
        public Vector4[] Konst = new Vector4[4];  // konst K0..K3 (rgba, 0..1)
        public int NumChans;
        public GxChannel[] Chan = new GxChannel[2];
        public GxLayer?[] Layers = new GxLayer?[8];
        public int TexMtxMode;                             // 0 Maya, 1 XSI, 2 3ds Max
        public int[] TexGenSrc = new int[8];               // XF TEXMTXINFO source row: 0 position, 1 normal, 5+n = UV n
        public bool[] TexGenStq = new bool[8];             // projection STQ (divide by q)
        public int[,] Swap = { { 0, 1, 2, 3 }, { 0, 1, 2, 3 }, { 0, 1, 2, 3 }, { 0, 1, 2, 3 } };   // [table, rgba] -> source channel
        public int AlphaComp1 = 7, AlphaRef1, AlphaLogic;  // alpha test: (a comp0 ref0) logic (a comp1 ref1); 0 and 1 or 2 xor 3 xnor
        public bool BlendSubtract;
        public bool DepthTest = true; public int DepthFunc = 3;   // z compare: 0 never 1 < 2 = 3 <= 4 > 5 != 6 >= 7 always
        public Vector3 MatColor = Vector3.One, AmbColor;   // light channel 0 -> RAS
        public float MatColorA = 1f, AmbColorA = 1f;       // matColor/ambColor alpha (RASA source when unlit)
        public uint ColorControl;
        public bool Lit => (ColorControl & 0x2) != 0;
        public string?[] TexNames = new string?[8];        // texture bound to each texmap, by TEX0 name
        // per texcoord (0..7): texgen source -- 0 = UV attribute, 1 = normal (env/reflection sphere-map).
        // Read from the material's XF TEXMTXINFO sourcerow, NOT guessed from the texture name.
        public int[] TexCoordSrc = new int[8];
        // a tangent-space normal map (GxJson "nmap" layer; GX itself has none): its texmap unit (-1 none) and texcoord.
        // With one, light channel 0 and the reflection texcoords are worked out per pixel from the perturbed normal.
        public int NormalMap = -1, NormalMapCoord;
        // pixel-engine mode
        public bool BlendEnable, DepthWrite = true, AlphaTest;
        public int BlendSrc = 4, BlendDst = 5, AlphaComp0 = 7, AlphaRef0;
        public bool Additive => BlendEnable && BlendDst == 1;      // dst = ONE
        public bool Translucent => BlendEnable && BlendDst == 5;   // dst = INVSRCA
    }

    public static class Mdl0Gx
    {
        static float F32(byte[] b, int o) => BitConverter.Int32BitsToSingle((int)U32(b, o));
        static Vector4 Rgba(byte[] b, int o) => new Vector4(b[o] / 255f, b[o + 1] / 255f, b[o + 2] / 255f, b[o + 3] / 255f);
        static uint U32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        static string Str(byte[] b, int o) { var sb = new StringBuilder(); for (int i = o; i < b.Length && b[i] >= 32 && b[i] < 127 && sb.Length < 512; i++) sb.Append((char)b[i]); return sb.ToString(); }
        static int Find(byte[] b, string s) { var p = Encoding.ASCII.GetBytes(s); for (int i = 0; i <= b.Length - p.Length; i++) { bool ok = true; for (int j = 0; j < p.Length; j++) if (b[i + j] != p[j]) { ok = false; break; } if (ok) return i; } return -1; }
        static int Section(byte[] b, int mdl0, int idx) { if (mdl0 + 0x10 + idx * 4 + 4 > b.Length) return -1; uint rel = U32(b, mdl0 + 0x10 + idx * 4); return rel == 0 ? -1 : mdl0 + (int)rel; }

        // Each texture's GX format (TEX0 header: +0x14 name offset, +0x1C width/height, +0x20 format): 0 I4, 1 I8, 2 IA4,
        // 3 IA8, 4 RGB565, 5 RGB5A3, 6 RGBA8, 8 C4, 9 C8, 10 C14X2, 14 CMPR. Matters because an intensity texture (I4/I8)
        // is its intensity in ALL FOUR channels -- a PNG export keeps the grey but drops that alpha.
        public static Dictionary<string, int> TextureFormats(string brresPath)
        {
            byte[] b = File.ReadAllBytes(brresPath);
            var res = new Dictionary<string, int>();
            for (int i = Find(b, "TEX0"); i >= 0 && i + 0x24 <= b.Length; )
            {
                int no = (int)U32(b, i + 0x14);
                if (no > 0 && i + no < b.Length)
                {
                    string name = Str(b, i + no);
                    if (name.Length > 0) res[name] = (int)U32(b, i + 0x20);
                }
                int next = -1;
                for (int k = i + 4; k + 4 <= b.Length; k++) if (b[k] == 'T' && b[k + 1] == 'E' && b[k + 2] == 'X' && b[k + 3] == '0') { next = k; break; }
                i = next;
            }
            return res;
        }
        public static bool IsIntensity(int format) => format == 0 || format == 1;

        public static List<GxMaterial> Read(string brresPath)
        {
            byte[] b = File.ReadAllBytes(brresPath);
            var result = new List<GxMaterial>();
            int mdl0 = Find(b, "MDL0"); if (mdl0 < 0) return result;
            int matG = Section(b, mdl0, 6), shdG = Section(b, mdl0, 7);
            if (matG < 0) return result;
            uint count = U32(b, matG + 4);
            for (int i = 1; i <= count && i < 200; i++)
            {
                int me = matG + 8 + i * 0x10;
                int st = matG + (int)U32(b, me + 0xC);
                if (st <= mdl0 || st + 0x3FC >= b.Length) continue;
                var m = new GxMaterial { Name = Str(b, matG + (int)U32(b, me + 8)) };
                m.NumTexGen = b[st + 0x14];
                m.NumTevStage = b[st + 0x16];
                m.CullMode = (int)U32(b, st + 0x18);
                int numTex = (int)U32(b, st + 0x2C);
                for (int t = 0; t < numTex && t < 8; t++)   // texmap i -> layer i's texture name (layer-rel offset)
                {
                    int lay = st + 0x414 + t * 0x34;
                    if (lay + 4 <= b.Length) { int no = (int)U32(b, lay); if (no > 0 && lay + no < b.Length) m.TexNames[t] = Str(b, lay + no); }
                }
                int lc = st + 0x3F0;                          // light channel 0
                m.MatColor = new Vector3(b[lc] / 255f, b[lc + 1] / 255f, b[lc + 2] / 255f);
                m.MatColorA = b[lc + 3] / 255f;
                m.AmbColor = new Vector3(b[lc + 4] / 255f, b[lc + 5] / 255f, b[lc + 6] / 255f);
                m.AmbColorA = b[lc + 7] / 255f;
                m.ColorControl = U32(b, lc + 8);
                m.NumChans = b[st + 0x15];
                for (int c = 0; c < 2; c++)
                {
                    int co = st + 0x3EC + c * 0x14;   // flags, mat, amb, colour ctrl, alpha ctrl
                    m.Chan[c] = new GxChannel { Mat = Rgba(b, co + 4), Amb = Rgba(b, co + 8), ColorCtrl = U32(b, co + 12), AlphaCtrl = U32(b, co + 16) };
                }
                // texture transforms: +0x1A4 per-texture flags, +0x1A8 matrix mode, +0x1AC 8 x (scale xy, rotation, translation xy),
                // +0x24C 8 x effect (camera ref, light ref, map mode, identity flag, 3x4 matrix); layers +0x414, 0x34 each
                m.TexMtxMode = (int)U32(b, st + 0x1A8);
                for (int t = 0; t < numTex && t < 8; t++)
                {
                    int lay = st + 0x414 + t * 0x34, srt = st + 0x1AC + t * 0x14, eff = st + 0x24C + t * 0x34;
                    if (lay + 0x34 > b.Length) break;
                    var L = new GxLayer
                    {
                        Name = m.TexNames[t], TexMapId = (int)U32(b, lay + 0x10), WrapS = (int)U32(b, lay + 0x18), WrapT = (int)U32(b, lay + 0x1C),
                        MinFilter = (int)U32(b, lay + 0x20), MagFilter = (int)U32(b, lay + 0x24), LodBias = F32(b, lay + 0x28),
                        Scale = new Vector2(F32(b, srt), F32(b, srt + 4)), RotDeg = F32(b, srt + 8), Trans = new Vector2(F32(b, srt + 12), F32(b, srt + 16)),
                        CamRef = (sbyte)b[eff], LightRef = (sbyte)b[eff + 1], MapMode = b[eff + 2], IdentityEffect = b[eff + 3] != 0,
                    };
                    for (int k = 0; k < 12; k++) L.Effect[k] = F32(b, eff + 4 + 4 * k);
                    m.Layers[t] = L;
                }
                int hi = (int)Math.Min(b.Length, st + U32(b, st));
                for (int r = 0; r < 4; r++) m.Reg[r] = ColorRA(b, st, hi, 0xE0 + r * 2, konst: false) ?? Vector4.Zero;
                for (int k = 0; k < 4; k++) m.Konst[k] = ColorRA(b, st, hi, 0xE0 + k * 2, konst: true) ?? Vector4.Zero;
                ReadPe(b, st, hi, m);
                ParseTexGen(b, st, hi, m);
                if (shdG >= 0)
                {
                    int se = shdG + 8 + i * 0x10;
                    int shSt = shdG + (int)U32(b, se + 0xC);
                    if (shSt > mdl0 && shSt + 0x200 <= b.Length) m.Stages = ParseTev(b, shSt, shSt + 0x200, m.NumTevStage, m.Swap);
                }
                result.Add(m);
            }
            return result;
        }

        // Read a TEV register (or konst) colour as RGBA from its BP RA/BG s10 word pair (even reg = RA:
        // r bits0-10, a bits12-22; odd reg = BG: b bits0-10, g bits12-22). bit23 of the low word selects
        // konst (u8) vs register (s10); match the requested kind so the two don't collide at one address.
        static Vector4? ColorRA(byte[] b, int lo, int hi, int loReg, bool konst)
        {
            for (int i = lo; i < hi - 9 && i < b.Length - 9; i++)
            {
                if (b[i] != 0x61 || b[i + 1] != loReg) continue;
                uint dLo = (uint)((b[i + 2] << 16) | (b[i + 3] << 8) | b[i + 4]);
                if (((dLo & 0x800000) != 0) != konst) continue;
                for (int j = i + 5; j < Math.Min(hi - 4, i + 0x30); j++)
                {
                    if (b[j] != 0x61 || b[j + 1] != loReg + 1) continue;
                    uint dHi = (uint)((b[j + 2] << 16) | (b[j + 3] << 8) | b[j + 4]);
                    float r = (dLo & 0x7FF) / 255f, a = ((dLo >> 12) & 0x7FF) / 255f;
                    float bl = (dHi & 0x7FF) / 255f, g = ((dHi >> 12) & 0x7FF) / 255f;
                    return new Vector4(r, g, bl, a);
                }
            }
            return null;
        }

        static void ReadPe(byte[] b, int st, int hi, GxMaterial m)
        {
            for (int k = st; k < hi - 4 && k < b.Length - 4; k++)
            {
                if (b[k] != 0x61) continue;
                int reg = b[k + 1];
                uint d = (uint)((b[k + 2] << 16) | (b[k + 3] << 8) | b[k + 4]);
                if (reg == 0x41) { m.BlendEnable = (d & 1) != 0; m.BlendDst = (int)((d >> 5) & 7); m.BlendSrc = (int)((d >> 8) & 7); m.BlendSubtract = ((d >> 11) & 1) != 0; }
                else if (reg == 0xF3)
                {
                    m.AlphaRef0 = (int)(d & 0xFF); m.AlphaRef1 = (int)((d >> 8) & 0xFF);
                    m.AlphaComp0 = (int)((d >> 16) & 7); m.AlphaComp1 = (int)((d >> 19) & 7); m.AlphaLogic = (int)((d >> 22) & 3);
                    m.AlphaTest = m.AlphaComp0 != 7 || m.AlphaComp1 != 7;
                }
                else if (reg == 0x40) { m.DepthTest = (d & 1) != 0; m.DepthFunc = (int)((d >> 1) & 7); m.DepthWrite = ((d >> 4) & 1) != 0; }
            }
        }

        // Read each texcoord's generation source from the material's GX XF writes. GXSetTexCoordGen writes
        // XF register TEXMTXINFO (0x1040 + texcoord); its "sourcerow" (bits 7-11) is 1 for NORMAL (an
        // env/reflection sphere-map) or >= 5 for a TEXn UV attribute. This is the DATA-DRIVEN texgen source
        // -- it replaces guessing "reflection" from the texture name (a *reflectivity* MASK is UV, not env).
        static void ParseTexGen(byte[] b, int st, int hi, GxMaterial m)
        {
            for (int k = st; k + 5 <= hi && k + 5 <= b.Length; k++)
            {
                if (b[k] != 0x10) continue;                       // GX XF-write opcode
                int cnt = ((b[k + 1] << 8) | b[k + 2]) + 1;       // value count (big-endian u16, stored as N-1)
                int addr = (b[k + 3] << 8) | b[k + 4];            // XF register address
                if (addr < 0x1040 || addr > 0x1047 || cnt < 1 || cnt > 8) continue;
                for (int i = 0; i < cnt; i++)
                {
                    int tc = addr - 0x1040 + i, vo = k + 5 + i * 4;
                    if (tc >= 8 || vo + 4 > b.Length) break;
                    uint info = U32(b, vo);
                    int sourcerow = (int)((info >> 7) & 0x1F);
                    m.TexCoordSrc[tc] = sourcerow == 1 ? 1 : 0;   // 1 = NORMAL (env), else a TEXn UV coord
                    m.TexGenSrc[tc] = sourcerow; m.TexGenStq[tc] = ((info >> 1) & 1) != 0;
                }
            }
        }

        // Parse the shader block's GX display list into per-stage combine + texture-order + konst-select.
        static GxTevStage[] ParseTev(byte[] b, int st, int hi, int numStages, int[,]? swap = null)
        {
            int n = Math.Clamp(numStages, 1, 16);
            var s = new GxTevStage[n];
            for (int i = 0; i < n; i++) s[i] = new GxTevStage();
            uint bpMask = 0xFFFFFF;
            for (int k = st; k + 5 <= hi && k + 5 <= b.Length; k++)
            {
                if (b[k] != 0x61) continue;
                int reg = b[k + 1];
                uint d = (uint)((b[k + 2] << 16) | (b[k + 3] << 8) | b[k + 4]);
                // BP write mask (0xFE): the NEXT write only changes the masked bits -- the konst selects are written
                // with the swap-table bits masked off, so ignoring it wiped swap table 0
                if (reg == 0xFE) { bpMask = d; continue; }
                uint mask = bpMask; bpMask = 0xFFFFFF;
                if (reg >= 0xC0 && reg <= 0xDF && (reg & 1) == 0)          // colour env
                {
                    int si = (reg - 0xC0) / 2; if (si >= n) continue; var t = s[si];
                    t.Cd = (int)(d & 0xF); t.Cc = (int)((d >> 4) & 0xF); t.Cb = (int)((d >> 8) & 0xF); t.Ca = (int)((d >> 12) & 0xF);
                    t.Cbias = (int)((d >> 16) & 3); t.Cop = (int)((d >> 18) & 1); t.Cclamp = (int)((d >> 19) & 1); t.Cscale = (int)((d >> 20) & 3); t.Cdest = (int)((d >> 22) & 3);
                }
                else if (reg >= 0xC1 && reg <= 0xDF && (reg & 1) == 1)     // alpha env
                {
                    int si = (reg - 0xC1) / 2; if (si >= n) continue; var t = s[si];
                    t.Ad = (int)((d >> 4) & 7); t.Ac = (int)((d >> 7) & 7); t.Ab = (int)((d >> 10) & 7); t.Aa = (int)((d >> 13) & 7);
                    t.Abias = (int)((d >> 16) & 3); t.Aop = (int)((d >> 18) & 1); t.Aclamp = (int)((d >> 19) & 1); t.Ascale = (int)((d >> 20) & 3); t.Adest = (int)((d >> 22) & 3);
                    t.RasSwap = (int)(d & 3); t.TexSwap = (int)((d >> 2) & 3);
                }
                else if (reg >= 0x28 && reg <= 0x2F)                       // TREF (2 stages)
                {
                    int s0 = (reg - 0x28) * 2;
                    if (s0 < n) { var t = s[s0]; int en = (int)((d >> 6) & 1); t.TexMap = en == 1 ? (int)(d & 7) : -1; t.TexCoord = (int)((d >> 3) & 7); t.RasChan = (int)((d >> 7) & 7); }
                    if (s0 + 1 < n) { var t = s[s0 + 1]; int en = (int)((d >> 18) & 1); t.TexMap = en == 1 ? (int)((d >> 12) & 7) : -1; t.TexCoord = (int)((d >> 15) & 7); t.RasChan = (int)((d >> 19) & 7); }
                }
                else if (reg >= 0xF6 && reg <= 0xFD)                       // KSEL (konst select, 2 stages)
                {
                    int s0 = (0xF6 - reg) * -2;   // 0xF6->0, 0xF7->2 ... (reg-0xF6)*2 ? handle below
                    s0 = (reg - 0xF6) * 2;
                    int kc0 = (int)((d >> 4) & 0x1F), ka0 = (int)((d >> 9) & 0x1F), kc1 = (int)((d >> 14) & 0x1F), ka1 = (int)((d >> 19) & 0x1F);
                    if ((mask & 0xFFFFF0) != 0)
                    {
                        if (s0 < n) { s[s0].KcSel = kc0; s[s0].KaSel = ka0; }
                        if (s0 + 1 < n) { s[s0 + 1].KcSel = kc1; s[s0 + 1].KaSel = ka1; }
                    }
                    // swap tables: KSEL 2t holds table t's red (bits 0-1) and green (2-3), KSEL 2t+1 its blue and alpha
                    int tb = (reg - 0xF6) / 2, half = (reg - 0xF6) & 1;
                    if (swap != null && (mask & 0xF) != 0) { swap[tb, half * 2] = (int)(d & 3); swap[tb, half * 2 + 1] = (int)((d >> 2) & 3); }
                }
            }
            return s;
        }
    }
}
