using System;
using System.IO;
using System.Linq;
using System.Numerics;
using MphRecomp.Assets;

namespace MphRead
{
    // Validates the Phase-2 TEV EVALUATOR (GxTev) -- the CPU reference the GLSL interpreter mirrors.
    // Two layers: (A/B) execute Kanden's REAL parsed materials and assert the known-correct duochrome
    // outputs with ZERO tuning (the Phase-3 "must match" proof, done headlessly); (C) synthetic stages
    // that pin the combine primitives (op/bias/scale/clamp, input routing, alpha) independent of assets.
    // Run: MphRead.Tools.dll -gxevaltest [Kanden.brres]
    internal static class GxEvalTest
    {
        static int _pass, _fail;
        static void Chk(string n, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {n}"); }

        const float Tol = 0.006f; // ~1.5/255
        static bool Near(Vector3 v, float r, float g, float b) =>
            MathF.Abs(v.X - r) < Tol && MathF.Abs(v.Y - g) < Tol && MathF.Abs(v.Z - b) < Tol;
        static string S(Vector3 v) => $"({v.X * 255:0.0},{v.Y * 255:0.0},{v.Z * 255:0.0})";

        // A fixed reflection/texture sample so the combine is deterministic (mid-grey metal reflection).
        static Vector4 Grey(int _) => new Vector4(0.5f, 0.5f, 0.5f, 1f);

        public static void Run(string[] args)
        {
            string brres = args.Length > 1 ? args[1]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "toyfig", "DATA", "files", "toy", "fig", "Kanden.brres"));

            // ---- (C) synthetic combine primitives: always run (no assets needed) ----
            Console.WriteLine("=== (C) combine primitives (synthetic) ===");
            {
                // a=C0, +0.5 bias, *1: out = C0 + 0.5
                var m = OneStage(ca: 2, cb: 15, cc: 15, cd: 15, op: 0, bias: 1, scale: 0);
                m.Reg[1] = new Vector4(0.1f, 0.2f, 0.3f, 1f);
                var o = Eval(m);
                Chk($"bias +0.5 on C0 -> {S(o)}≈(153,178,204)", Near(o, 0.6f, 0.7f, 0.8f));
            }
            {
                // d=ONE, op=subtract, lerp=C0: out = ONE - C0
                var m = OneStage(ca: 2, cb: 15, cc: 15, cd: 12, op: 1, bias: 0, scale: 0);
                m.Reg[1] = new Vector4(0.1f, 0.2f, 0.3f, 1f);
                var o = Eval(m);
                Chk($"subtract ONE-C0 -> {S(o)}≈(230,204,178)", Near(o, 0.9f, 0.8f, 0.7f));
            }
            {
                // a=HALF, /2 scale: out = 0.5/2 = 0.25
                var m = OneStage(ca: 13, cb: 15, cc: 15, cd: 15, op: 0, bias: 0, scale: 3);
                var o = Eval(m);
                Chk($"scale /2 on HALF -> {S(o)}≈(64,64,64)", Near(o, 0.25f, 0.25f, 0.25f));
            }
            {
                // c-lerp: mix(C0,C1,HALF) -- exercises the (1-c)*a + c*b interpolation
                var m = OneStage(ca: 2, cb: 4, cc: 13, cd: 15, op: 0, bias: 0, scale: 0);
                m.Reg[1] = new Vector4(0.2f, 0.2f, 0.2f, 1f);
                m.Reg[2] = new Vector4(0.8f, 0.8f, 0.8f, 1f);
                var o = Eval(m);
                Chk($"mix(C0,C1,HALF) -> {S(o)}≈(128,128,128)", Near(o, 0.5f, 0.5f, 0.5f));
            }
            {
                // alpha path: Ad=RASA, others ZERO -> alpha = RASA
                var m = OneStage(ca: 15, cb: 15, cc: 15, cd: 15, op: 0, bias: 0, scale: 0);
                m.Stages[0].Ad = 5; // RASA
                var o = GxTev.Evaluate(m, new GxTev.Inputs { Rasc = Vector3.Zero, Rasa = 0.4f, Tex = Grey });
                Chk($"alpha = RASA(0.4) -> {o.W * 255:0.0}≈102", MathF.Abs(o.W - 0.4f) < Tol);
            }
            {
                // unclamped color stays > 1 (linear range) so a later stage can pull it back
                var m = OneStage(ca: 12, cb: 15, cc: 15, cd: 12, op: 0, bias: 0, scale: 1); // (ONE+ONE)*2 unclamped
                m.Stages[0].Cclamp = 0;
                var o = Eval(m);
                Chk($"unclamped >1 preserved -> {o.X:0.0}≈4.0", o.X > 3.9f);
            }

            // ---- (A/B) real Kanden materials ----
            if (!File.Exists(brres)) { Console.WriteLine("  [SKIP] Kanden.brres absent -> ran synthetic only: " + brres); Summary(); return; }
            var mats = Mdl0Gx.Read(brres);
            var green = mats.FirstOrDefault(m => m.Name.Contains("green"));
            var vio = mats.FirstOrDefault(m => m.Name.Contains("vioret"));
            var yel = mats.FirstOrDefault(m => m.Name.Contains("yellow"));

            Console.WriteLine("\n=== (A) Kanden combine (RASC=0.25, TEXC=0.5) -- zero-tuning duochrome match ===");
            var rasc = new Vector3(0.25f, 0.25f, 0.25f);
            var inp = new GxTev.Inputs { Rasc = rasc, Rasa = 1f, Tex = Grey };
            if (green != null)
            {
                var o = V3(GxTev.Evaluate(green, inp));
                // 2*mix(C0=(83,255,26),C1=(255,0,255),0.25)=..; then 2*TEXC(0.5)*CPREV -> gold-green
                Chk($"green muscle = gold {S(o)}≈(252,255,167)", Near(o, 0.988f, 1.0f, 0.653f));
                Chk("green reads its own C0/C1 (gold, not magenta/green)", o.X > 0.9f && o.Y > 0.9f && o.Z < 0.8f);
            }
            if (vio != null)
            {
                var o = V3(GxTev.Evaluate(vio, inp));
                // swapped lerp order -> muted violet-blue chrome
                Chk($"vioret armour = violet {S(o)}≈(142,142,183)", Near(o, 0.557f, 0.559f, 0.718f));
                if (green != null)
                {
                    var g = V3(GxTev.Evaluate(green, inp));
                    Chk("armour != muscle for SAME inputs (swap matters)", !Near(o, g.X, g.Y, g.Z));
                    Chk("armour is bluer than muscle (b>r)", o.Z > o.X + 0.1f);
                }
            }
            if (yel != null)
            {
                // ports are UNLIT: flat RASC = matColor, ignores lighting entirely
                var rY = GxTev.Rasterize(yel, sceneLight: new Vector3(0.9f, 0.9f, 0.9f));
                var o = V3(GxTev.Evaluate(yel, new GxTev.Inputs { Rasc = rY, Rasa = 1f, Tex = Grey }));
                Chk($"yellow ports = flat matColor {S(o)}≈(246,255,0)", Near(o, 0.9647f, 1.0f, 0.0f));
            }

            Console.WriteLine("\n=== (B) Rasterize (lit vs unlit) ===");
            if (green != null)
            {
                // green is lit + grey matColor(128) + white ambient -> RASC saturates to matColor
                var r = GxTev.Rasterize(green, Vector3.Zero);
                Chk($"lit green RASC = matColor*clamp(amb) {S(r)}≈(128,128,128)", Near(r, 0.502f, 0.502f, 0.502f));
            }
            if (yel != null)
            {
                var r = GxTev.Rasterize(yel, new Vector3(1, 1, 1));
                Chk("unlit yellow RASC ignores light = matColor", Near(r, 0.9647f, 1.0f, 0.0f));
            }
            {
                // synthetic lit material with NON-saturating ambient proves light adds + clamps + modulates
                var m = new GxMaterial { ColorControl = 0x2, MatColor = Vector3.One, AmbColor = new Vector3(0.2f) };
                var r = GxTev.Rasterize(m, new Vector3(0.5f, 0.5f, 0.5f)); // 1*clamp(0.2+0.5)=0.7
                Chk($"lit RASC = mat*(amb+light) {S(r)}≈(179,179,179)", Near(r, 0.7f, 0.7f, 0.7f));
            }

            Summary();
        }

        static void Summary() =>
            Console.WriteLine($"\nGX EVALUATOR: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");

        static Vector3 V3(Vector4 v) => new Vector3(v.X, v.Y, v.Z);
        static Vector3 Eval(GxMaterial m) =>
            V3(GxTev.Evaluate(m, new GxTev.Inputs { Rasc = new Vector3(0.5f), Rasa = 1f, Tex = Grey }));

        // Build a 1-stage material; colour combine selectors given, alpha defaults to ZERO/flat.
        static GxMaterial OneStage(int ca, int cb, int cc, int cd, int op, int bias, int scale)
        {
            var s = new GxTevStage
            {
                Ca = ca, Cb = cb, Cc = cc, Cd = cd, Cop = op, Cbias = bias, Cscale = scale, Cclamp = 1, Cdest = 0,
                Aa = 7, Ab = 7, Ac = 7, Ad = 7, Aop = 0, Abias = 0, Ascale = 0, Aclamp = 1, Adest = 0,
                TexMap = -1,
            };
            return new GxMaterial { Stages = new[] { s }, NumTevStage = 1 };
        }
    }
}
