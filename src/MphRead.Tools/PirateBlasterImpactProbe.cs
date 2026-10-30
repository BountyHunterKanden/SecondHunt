using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Campaign;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Effects;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRead
{
    // -pirateblastertest impact [fx=<bodies/MP1PowerTrooper dir>] [out=<png>] (release test T4 N7, owner 2026-10-10: "is
    // there an impact image ... like power beam"): the Pirate Blaster's Prime hit effect (fx.cfg impact, the CRSC's
    // default) and its two shot systems run on their own; per system: frames with particles, most particles, how far
    // they reach (world units, at the app's scales) and how bright they get; then a strip picture of each (side view,
    // additive, like -dronefxtest --strip). Read-only.
    internal static class PirateBlasterImpactProbe
    {
        // -pirateblastertest clipfx [disc=<mp1.iso>] [match=shoot,fire,attack]: (owner 2026-10-10: "is there an effect from
        // the mp1 rom we're missing" for the shot's flash at the gun) the Power Trooper's (ANCS 8169653a) clips, and the
        // EVNT particle effects of those whose name matches, as BodyFx would import them (to a temp folder). Read-only
        public static void ClipFx(string disc, string[] match)
        {
            const uint ancsId = 0x8169653a;
            using IFileSource src = DiscImage.Open(disc);
            var pak = new Pak(src.ReadFile("Metroid1.pak"), "Metroid1.pak");
            Ancs a = Ancs.Parse(pak.Get(ancsId));
            Ancs.AnimSet set = a.Set ?? throw new InvalidDataException("no animation set");
            Console.WriteLine($"{set.Anims.Count} clips: {string.Join(", ", set.Anims.Select(x => x.Name))}");
            string[] clips = set.Anims.Select(x => x.Name).Where(n => match.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase))).ToArray();
            Console.WriteLine($"matching: {string.Join(", ", clips)}");
            string outDir = Path.Combine(Path.GetTempPath(), "mph-clipfx");
            BodyFx.Export(id => pak.Get(id), new BodyFx.Spec(clips, 0), ancsId, outDir);
            foreach (string line in File.ReadAllLines(Path.Combine(outDir, "fx", "fx.cfg"))) Console.WriteLine("  " + line);
            Console.WriteLine($"  files: {string.Join(" ", Directory.GetFiles(Path.Combine(outDir, "fx")).Select(Path.GetFileName))}");
        }

        public static void Run(string fxBody, string outPng, float impactScale, int impactEmit)
        {
            BodyFx.Config cfg = BodyFx.Config.Load(fxBody) ?? throw new InvalidDataException($"no fx.cfg in {fxBody}");
            string fx = Path.Combine(fxBody, "fx");
            RetroParticles lib = RetroParticles.Load(fx);
            var rows = new List<(string Title, uint Id, float Scale, int Emit)>
            {
                ($"impact {cfg.Impact:x8} x{impactScale} emit {impactEmit}", cfg.Impact, impactScale, impactEmit),
            };
            for (int i = 0; i < cfg.Parts.Count; i++) rows.Add(($"shot part {cfg.Parts[i]:x8} x0.5", cfg.Parts[i], 0.5f, 30));
            const int W = 220, H = 220, Cols = 8;
            const float Px = 120; // pixels a world unit
            var sheet = new float[rows.Count * H, Cols * W, 3];
            var tex = new Dictionary<uint, (byte[] Rgba, int W, int H)>();
            for (int r = 0; r < rows.Count; r++)
            {
                (string title, uint id, float scale, int emit) = rows[r];
                if (lib.Get(id) == null)
                {
                    Console.WriteLine($"  {title}: not in the library");
                    continue;
                }
                var sys = new RetroParticleSystem(lib, id, Matrix4.CreateScale(scale), follow: false, seed: 1234);
                var bb = new List<RetroBillboard>();
                int last = -1, most = 0;
                float reach = 0, bright = 0;
                var shots = new List<List<RetroBillboard>>();
                int[] pick = { 0, 1, 2, 3, 5, 8, 12, 18 };
                for (int f = 0; f < 300 && !(f > emit && sys.Done); f++)
                {
                    if (f == emit) sys.Emitting = false;
                    sys.Step();
                    bb.Clear();
                    sys.Collect(bb);
                    if (bb.Count > 0) last = f;
                    most = Math.Max(most, bb.Count);
                    foreach (RetroBillboard b in bb)
                    {
                        reach = MathF.Max(reach, b.Position.Length + b.Size / 2);
                        bright = MathF.Max(bright, MathF.Max(b.Color.X, MathF.Max(b.Color.Y, b.Color.Z)) * b.Color.W);
                    }
                    if (pick.Contains(f)) shots.Add(bb.ToList());
                }
                Console.WriteLine(FormattableString.Invariant(
                    $"  {title}: {last + 1} frames with particles, up to {most}, reach {reach:0.00} units, brightest {bright:0.00}"));
                for (int c = 0; c < shots.Count && c < Cols; c++)
                {
                    foreach (RetroBillboard b in shots[c])
                    {
                        if (!tex.TryGetValue(b.Texture, out var t))
                        {
                            string png = Path.Combine(fx, $"{b.Texture:x8}.png");
                            t = File.Exists(png) ? (PngOut.Read(png, out int tw0, out int th0), tw0, th0) : (new byte[] { 255, 255, 255, 255 }, 1, 1);
                            tex[b.Texture] = t;
                        }
                        float half = b.Size * Px / 2;
                        float cx = W / 2f + b.Position.X * Px, cy = H / 2f - b.Position.Z * Px;
                        for (int y = (int)(cy - half); y <= (int)(cy + half); y++)
                        for (int x = (int)(cx - half); x <= (int)(cx + half); x++)
                        {
                            if (x < 0 || y < 0 || x >= W || y >= H || half < 0.5f) continue;
                            float u = (x - cx) / (2 * half) + 0.5f, v = (y - cy) / (2 * half) + 0.5f;
                            if (u < 0 || v < 0 || u >= 1 || v >= 1) continue;
                            int ti = ((int)(v * t.H) * t.W + (int)(u * t.W)) * 4;
                            float a = t.Rgba[ti + 3] / 255f * b.Color.W;
                            for (int k = 0; k < 3; k++) sheet[r * H + y, c * W + x, k] += t.Rgba[ti + k] / 255f * b.Color[k] * a;
                        }
                    }
                }
            }
            // a 1-unit tick (about half a hunter's height is 0.85) in each cell's corner
            int sw = Cols * W, sh = rows.Count * H;
            var rgba = new byte[sw * sh * 4];
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int o = (y * sw + x) * 4;
                bool tick = y % H == H - 6 && x % W >= 6 && x % W < 6 + Px;
                for (int k = 0; k < 3; k++) rgba[o + k] = tick ? (byte)200 : (byte)(Math.Clamp(sheet[y, x, k] + 0.08f, 0, 1) * 255);
                rgba[o + 3] = 255;
            }
            PngOut.Write(outPng, rgba, sw, sh);
            Console.WriteLine($"  strip: {outPng} (rows: {string.Join(" / ", rows.Select(r => r.Title))}; frames {string.Join(",", new[] { 0, 1, 2, 3, 5, 8, 12, 18 })}; tick = 1 unit)");
        }
    }
}
