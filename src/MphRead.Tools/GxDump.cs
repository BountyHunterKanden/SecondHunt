using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Assets;

namespace MphRead
{
    // Print every material exactly as OUR parser (Mdl0Gx) reads it, in the same terms BrawlLib's viewer uses, so the
    // two can be compared field by field (gx_compare.py against a BrawlLib dump). Run: MphRead.Tools.dll -gxdump <.brres> [out]
    internal static class GxDump
    {
        static readonly string[] Comp = { "Never", "Less", "Equal", "LessOrEqual", "Greater", "NotEqual", "GreaterOrEqual", "Always" };
        static readonly string[] Logic = { "And", "Or", "Xor", "InverseXor" };
        static readonly string[] Dst = { "Zero", "One", "SourceColor", "InverseSourceColor", "SourceAlpha", "InverseSourceAlpha", "DestinationAlpha", "InverseDestinationAlpha" };
        static readonly string[] Src = { "Zero", "One", "DestinationColor", "InverseDestinationColor", "SourceAlpha", "InverseSourceAlpha", "DestinationAlpha", "InverseDestinationAlpha" };
        static readonly string[] Cull = { "Cull_None", "Cull_Outside", "Cull_Inside", "Cull_All" };
        static readonly string[] Map = { "TexCoord", "EnvCamera", "Projection", "EnvLight", "EnvSpec" };
        static readonly string[] Wrap = { "Clamp", "Repeat", "Mirror" };
        static readonly string[] Filt = { "Nearest", "Linear", "Nearest_Mipmap_Nearest", "Linear_Mipmap_Nearest", "Nearest_Mipmap_Linear", "Linear_Mipmap_Linear" };
        static readonly string[] Dif = { "Disabled", "Enabled", "Clamped", "?" };
        static readonly string[] Att = { "None", "Specular", "None", "Spotlight" };
        static readonly string[] Ras = { "LightChannel0", "LightChannel1", "?", "?", "?", "BumpAlpha", "NormalizedBumpAlpha", "Zero" };

        static string C(System.Numerics.Vector4 v) => $"R:{Math.Round(v.X * 255)} G:{Math.Round(v.Y * 255)} B:{Math.Round(v.Z * 255)} A:{Math.Round(v.W * 255)}";
        static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        public static void Run(string[] args)
        {
            var sb = new StringBuilder();
            foreach (var m in Mdl0Gx.Read(args[1]))
            {
                sb.AppendLine($"=== MATERIAL {m.Name}");
                sb.AppendLine($"  stages = {m.Stages.Length}");
                for (int c = 0; c < 2; c++)
                {
                    var ch = m.Chan[c]; string k = $"C{c + 1}";
                    sb.AppendLine($"  {k}MaterialColor = {C(ch.Mat)}");
                    sb.AppendLine($"  {k}AmbientColor = {C(ch.Amb)}");
                    sb.AppendLine($"  {k}Color = mat={(GxChannel.VtxMat(ch.ColorCtrl) ? "Vertex" : "Register")} amb={(GxChannel.VtxAmb(ch.ColorCtrl) ? "Vertex" : "Register")} lit={GxChannel.Lit(ch.ColorCtrl)} dif={Dif[GxChannel.DiffFn(ch.ColorCtrl)]} att={Att[GxChannel.Attn(ch.ColorCtrl)]}");
                    sb.AppendLine($"  {k}Alpha = mat={(GxChannel.VtxMat(ch.AlphaCtrl) ? "Vertex" : "Register")} lit={GxChannel.Lit(ch.AlphaCtrl)}");
                }
                for (int r = 1; r < 4; r++) sb.AppendLine($"  Color{r - 1} = {C(m.Reg[r])}");
                for (int r = 0; r < 4; r++) sb.AppendLine($"  ConstantColor{r} = {C(m.Konst[r])}");
                sb.AppendLine($"  alpha = {Comp[m.AlphaComp0]} {m.AlphaRef0} {Logic[m.AlphaLogic]} {Comp[m.AlphaComp1]} {m.AlphaRef1}");
                sb.AppendLine($"  blend = en={m.BlendEnable} {Src[m.BlendSrc]}/{Dst[m.BlendDst]} sub={m.BlendSubtract} zupd={m.DepthWrite} cull={Cull[m.CullMode & 3]}");
                for (int t = 0; t < 8; t++)
                {
                    var L = m.Layers[t]; if (L == null) continue;
                    sb.AppendLine($"  layer {t} = {L.Name} map={Map[Math.Min(L.MapMode, 4)]} srt=S({F(L.Scale.X)},{F(L.Scale.Y)}) R{F(L.RotDeg)} T({F(L.Trans.X)},{F(L.Trans.Y)}) wrap={Wrap[L.WrapS % 3]}/{Wrap[L.WrapT % 3]} filt={Filt[L.MinFilter % 6]}/{Filt[L.MagFilter % 6]} lod={F(L.LodBias)} light={L.LightRef} cam={L.CamRef}");
                }
                for (int i = 0; i < m.Stages.Length; i++)
                {
                    var s = m.Stages[i];
                    sb.AppendLine($"  stage {i} = ras={Ras[s.RasChan & 7]} tex={(s.TexMap >= 0 ? "TexMap" + s.TexMap : "off")} coord=TexCoord{s.TexCoord} swap=ras{s.RasSwap}/tex{s.TexSwap} kc={s.KcSel} ka={s.KaSel}");
                }
                sb.AppendLine("  swaptables = " + string.Join(" ", Enumerable.Range(0, 4).Select(t => string.Concat(Enumerable.Range(0, 4).Select(c => "RGBA"[m.Swap[t, c]])))));
            }
            if (args.Length > 2) File.WriteAllText(args[2], sb.ToString()); else Console.Write(sb);
        }
    }
}
