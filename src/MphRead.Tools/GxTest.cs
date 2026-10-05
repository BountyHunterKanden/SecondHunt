using System;
using System.IO;
using System.Linq;
using MphRecomp.Assets;

namespace MphRead
{
    // Validates the Phase-1 GX material parser (Mdl0Gx) against Kanden's known program, and dumps any
    // model's parsed materials for inspection. Skips cleanly if the user's .brres isn't present.
    // Run: MphRead.Tools.dll -gxtest [brres]
    internal static class GxTest
    {
        static int _pass, _fail;
        static void Chk(string n, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {n}"); }
        static readonly string[] CC = { "CPREV","APREV","C0","A0","C1","A1","C2","A2","TEXC","TEXA","RASC","RASA","ONE","HALF","KONST","ZERO" };
        static readonly string[] AC = { "APREV","A0","A1","A2","TEXA","RASA","KONST","ZERO" };
        static readonly string[] DST = { "PREV","C0","C1","C2" };
        static readonly string[] SCL = { "*1","*2","*4","/2" };
        static readonly string[] CULL = { "None","Front","Back","All" };

        public static void Run(string[] args)
        {
            string brres = args.Length > 1 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "toyfig", "DATA", "files", "toy", "fig", "Kanden.brres"));
            if (!File.Exists(brres)) { Console.WriteLine("  [SKIP] brres not present: " + brres); return; }
            var mats = Mdl0Gx.Read(brres);
            Console.WriteLine($"=== {Path.GetFileName(brres)}: {mats.Count} materials ===");
            foreach (var m in mats)
            {
                Console.WriteLine($"\n  {m.Name}  stages={m.NumTevStage} texgen={m.NumTexGen} cull={(m.CullMode < 4 ? CULL[m.CullMode] : m.CullMode.ToString())} " +
                    $"lit={(m.Lit ? "Y" : "n")} blend={(m.BlendEnable ? (m.Additive ? "ADD" : m.Translucent ? "XLU" : "on") : "off")} zwr={(m.DepthWrite ? "Y" : "n")}");
                Console.WriteLine($"    tex=[{string.Join(", ", m.TexNames.Where(t => t != null))}]");
                Console.WriteLine($"    texgenSrc=[{string.Join(",", Enumerable.Range(0, Math.Max(1, m.NumTexGen)).Select(i => i + ":" + (m.TexCoordSrc[i] == 1 ? "ENV" : "uv")))}]");
                Console.WriteLine($"    reg1(C0)={Fmt(m.Reg[1])} reg2(C1)={Fmt(m.Reg[2])} reg3(C2)={Fmt(m.Reg[3])}  matColor={Fmt3(m.MatColor)}a={m.MatColorA * 255:0} amb={Fmt3(m.AmbColor)}a={m.AmbColorA * 255:0}");
                Console.WriteLine($"    K0={Fmt(m.Konst[0])} K1={Fmt(m.Konst[1])} K2={Fmt(m.Konst[2])} K3={Fmt(m.Konst[3])}");
                Console.WriteLine($"    PE: blendEnable={m.BlendEnable} src={m.BlendSrc} dst={m.BlendDst} depthWrite={m.DepthWrite} alphaTest={m.AlphaTest}(comp={m.AlphaComp0} ref={m.AlphaRef0})");
                for (int i = 0; i < m.Stages.Length; i++)
                {
                    var s = m.Stages[i];
                    Console.WriteLine($"    C{i}: {DST[s.Cdest]}=({CC[s.Ca]},{CC[s.Cb]},{CC[s.Cc]},{CC[s.Cd]}){(s.Cop == 1 ? "-" : "+")}{SCL[s.Cscale]}{(s.Cclamp == 1 ? " clamp" : "")}  tex={(s.TexMap < 0 ? "-" : s.TexMap.ToString())} coord={s.TexCoord} ras={s.RasChan} kc={s.KcSel:X2}");
                    Console.WriteLine($"    A{i}: {DST[s.Adest]}=({AC[s.Aa]},{AC[s.Ab]},{AC[s.Ac]},{AC[s.Ad]}){(s.Aop == 1 ? "-" : "+")}{SCL[s.Ascale]}{(s.Aclamp == 1 ? " clamp" : "")}  ka={s.KaSel:X2}");
                }
            }
            // Regression vs Kanden's known program (matches the verified -mdltev output).
            if (Path.GetFileName(brres).Equals("Kanden.brres", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("\n=== Kanden regression ===");
                Chk("3 materials", mats.Count == 3);
                var green = mats.FirstOrDefault(m => m.Name.Contains("green"));
                var vio = mats.FirstOrDefault(m => m.Name.Contains("vioret"));
                var yel = mats.FirstOrDefault(m => m.Name.Contains("yellow"));
                Chk("green present + 2 stages", green != null && green.Stages.Length == 2);
                // green s0 = mix(C0,C1,RASC): a=C0(2), b=C1(4), c=RASC(10), d=ZERO(15), *2
                Chk("green s0 = mix(C0,C1,RASC)*2", green != null && green.Stages[0].Ca == 2 && green.Stages[0].Cb == 4 && green.Stages[0].Cc == 10 && green.Stages[0].Cscale == 1);
                // green s1 = TEXC*CPREV *2 (b=TEXC(8), c=CPREV(0))
                Chk("green s1 = TEXC*CPREV*2", green != null && green.Stages[1].Cb == 8 && green.Stages[1].Cc == 0 && green.Stages[1].Cscale == 1);
                Chk("green samples Kanden_reflected", green != null && green.TexNames.Any(t => t == "Kanden_reflected"));
                Chk("green cull=Back", green != null && green.CullMode == 2);
                // armour swaps: s0 = mix(C1,C0,RASC): a=C1(4), b=C0(2)
                Chk("vioret s0 swaps (a=C1,b=C0)", vio != null && vio.Stages[0].Ca == 4 && vio.Stages[0].Cb == 2);
                // ports: unlit, 1 stage = flat RASC (d=RASC(10)), no texture
                Chk("yellow unlit + flat RASC", yel != null && !yel.Lit && yel.Stages[0].Cd == 10 && yel.Stages[0].TexMap < 0);
                Console.WriteLine($"\nGX PARSER: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            }
        }

        static string Fmt(System.Numerics.Vector4 v) => $"({v.X * 255:0},{v.Y * 255:0},{v.Z * 255:0})";
        static string Fmt3(System.Numerics.Vector3 v) => $"({v.X * 255:0},{v.Y * 255:0},{v.Z * 255:0})";
    }
}
