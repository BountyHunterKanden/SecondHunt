using System;
using System.IO;
using System.Linq;
using MphRecomp.Assets;

namespace MphRead
{
    // Verifies the MDL0 material colour reader against Kanden's Brawl trophy (the owner's own
    // extracted .brres). Skips cleanly when that user-supplied file isn't present, so it never
    // fails on machines without it. Run: MphRead.Tools.dll -mdl0test.
    internal static class MdlColorTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        public static void Run(string[] args)
        {
            string brres = args.Length > 1 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "toyfig", "DATA", "files", "toy", "fig", "Kanden.brres"));
            if (!File.Exists(brres)) { Console.WriteLine("  [SKIP] user's Kanden.brres not present."); return; }

            var mats = Mdl0Colors.Read(brres);
            Check("found 3 materials", mats.Count == 3);
            var green = mats.FirstOrDefault(m => m.Name.Contains("green"));
            var yellow = mats.FirstOrDefault(m => m.Name.Contains("yellow"));
            Check("green material present", green != null);
            Check("yellow material present", yellow != null);

            // green C0 = RGB(83,255,26) -- the verified base register colour
            bool greenOk = green?.Register[0] is { } c
                && Math.Abs(c.X * 255 - 83) < 2 && Math.Abs(c.Y * 255 - 255) < 2 && Math.Abs(c.Z * 255 - 26) < 2;
            Check("green C0 == RGB(83,255,26)", greenOk);

            // yellow material's registers are all black -- its colour lives in the MATERIAL COLOUR
            bool yellowBlack = yellow != null && yellow.Register.All(r => r == null || (r.Value.X + r.Value.Y + r.Value.Z) < 0.01f);
            Check("yellow material registers all black/none", yellowBlack);
            bool yellowMat = yellow?.MaterialColor is { } y
                && Math.Abs(y.X * 255 - 246) < 2 && Math.Abs(y.Y * 255 - 255) < 2 && y.Z * 255 < 2;
            Check("yellow material colour == RGB(246,255,0)", yellowMat);

            // BaseColor resolves each part's true colour: register when coloured, else material colour
            Check("green BaseColor == green (from register)", green != null && Math.Abs(green.BaseColor.Y * 255 - 255) < 2 && green.BaseColor.Z * 255 < 30);
            Check("yellow BaseColor == yellow (from material colour)", yellow != null && yellow.BaseColor.X * 255 > 240 && yellow.BaseColor.Z * 255 < 2);

            // Per-material LIGHTING mode (the "individually varied" difference): muscle/armour are LIT,
            // the ports are UNLIT (chan-ctrl 0x703 vs 0x700) -> ports render flat, no diffuse/reflection.
            Check("green material is LIT (chan-ctrl 0x703)", green != null && green.Lit && green.ColorControl == 0x703);
            Check("yellow ports are UNLIT (chan-ctrl 0x700)", yellow != null && !yellow.Lit && yellow.ColorControl == 0x700);

            foreach (var m in mats)
            {
                Console.WriteLine($"    {m.Name,-16} base={Fmt(m.BaseColor)}  lit={(m.Lit ? "Y" : "n")}  ctrl=0x{m.ColorControl:X}  matColor={Fmt(m.MaterialColor)}  amb={Fmt(m.AmbientColor)}");
                Console.WriteLine($"        C0={Fmt(m.Register[0])} C1={Fmt(m.Register[1])} C2={Fmt(m.Register[2])}  K0={Fmt(m.Konst[0])} K1={Fmt(m.Konst[1])} K2={Fmt(m.Konst[2])} K3={Fmt(m.Konst[3])}");
            }
            Console.WriteLine($"\nMDL0 COLOR TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
        }

        static string Fmt(System.Numerics.Vector3? v) => v is { } c ? $"({c.X * 255:0},{c.Y * 255:0},{c.Z * 255:0})" : "(--)";
    }
}
