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
    // -dronefxtest [--mp1 disc.iso] [--trilogy disc.rvz] [--out dir] [--strip <png>]
    // The Scout Drone's Prime 1 shot effects (Core Import/Effects/BodyFx.cs + Campaign/RetroParticles.cs): imports the
    // drone's fx (its firing clips' EVNT effects + the hit effect of its CRSC) from the owner's Prime disc and from the
    // Trilogy disc (the two folders must be byte-identical), loads them the way the app does, and runs the effects:
    // the gun flash (06296852, 2 frames of emission) must draw for about 6-10 frames, the hit (eb8b9043) about 8-14, the
    // firing glows must draw while emitting and die out after. --strip: a frame strip of each (side view, additive) to
    // compare with a capture of Prime's own drone.
    // Prime's drone sounds (Core Import/Retro/MusyX.cs, from AudioGrp.pak): the four sound ids its clips and its CRSC play
    // must come out as sfx_<id>.wav through the right MusyX sounds, at a sane rate, about as long as their macros make them,
    // and not silent; each one's macro steps are printed.
    internal static class DroneFxTest
    {
        public const uint Ancs = 0x5e058399;
        static BodyFx.Spec Spec = null!;

        public static void Run(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-dronefxtest")));
            string mp1 = Arg("--mp1", Path.Combine(repo, "brawl_extract", "prime", "mp1.iso"));
            string tri = Arg("--trilogy", Path.Combine(repo, "brawl_extract", "prime", "trilogy.rvz"));
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) fails++;
            }
            // the drones' recipes ask for the same effects
            RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
            BodyFx.Spec?[] specs = store.All.Where(r => r.Kind == "bodies" && r.Name is "MP1AlphaDrone" or "MP1GammaDrone").Select(BodyFx.From).ToArray();
            Check(specs.Length == 2 && specs.All(s => s != null && s.Crsc == 0xc59e2b05 && s.Clips.SequenceEqual(specs[0]!.Clips)),
                $"both drone recipes ask for fx ({String.Join(", ", specs[0]?.Clips ?? Array.Empty<string>())})");
            if (specs.Length == 0 || specs[0] == null) Environment.Exit(1);
            Spec = specs[0]!;
            string gc = Path.Combine(outRoot, "gc");
            Import(mp1, "", gc);
            string[] files = Directory.GetFiles(Path.Combine(gc, "fx")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;
            Console.WriteLine($"  dronefxtest: {files.Length} files in {Path.Combine(gc, "fx")}");
            Console.Write(File.ReadAllText(Path.Combine(gc, "fx", "fx.cfg")));
            if (File.Exists(tri))
            {
                string wii = Path.Combine(outRoot, "trilogy");
                Import(tri, "MP1/", wii);
                string[] tfiles = Directory.GetFiles(Path.Combine(wii, "fx")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;
                bool same = files.SequenceEqual(tfiles) && files.All(f =>
                    File.ReadAllBytes(Path.Combine(gc, "fx", f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(wii, "fx", f))));
                Check(same, $"Trilogy's fx folder byte-identical to the GameCube disc's ({tfiles.Length} files)");
            }
            else
            {
                Console.WriteLine($"  [SKIP] no Trilogy disc at {tri}");
            }
            BodyFx.Config? cfg = BodyFx.Config.Load(gc);
            Check(cfg != null && cfg.Impact == 0xeb8b9043, "fx.cfg: impact eb8b9043");
            Check(cfg != null && cfg.Events.Any(e => e.Part == 0x06296852 && e.Bone == "L_GUN_BOTTOM_LCTR")
                && cfg.Events.Any(e => e.Part == 0x06296852 && e.Bone == "R_GUN_BOTTOM_LCTR"), "fx.cfg: gun flash 06296852 on both L/R_GUN_BOTTOM_LCTR");
            RetroParticles lib = RetroParticles.Load(Path.Combine(gc, "fx"));
            Console.WriteLine($"  {lib.Count} particle systems loaded");
            (int flash, int flashMax) = Life(lib, 0x06296852, 2);
            Check(flash is >= 6 and <= 10, $"gun flash draws {flash} frames (6..10), up to {flashMax} billboards");
            (int hit, int hitMax) = Life(lib, 0xeb8b9043, 2);
            Check(hit is >= 8 and <= 14, $"hit effect draws {hit} frames (8..14), up to {hitMax} billboards");
            (int panel, int panelMax) = Life(lib, 0x19eecc2f, 31);
            Check(panel is >= 31 and <= 31 + 26 && panelMax > 3, $"panel glow draws {panel} frames for 31 of emission (31..57), up to {panelMax}");
            (int hand, int handMax) = Life(lib, 0xa3b5243a, 20);
            Check(hand is >= 20 and <= 34 && handMax > 3, $"hand charge glow draws {hand} frames for 20 of emission, up to {handMax}");
            // the cutting laser's burn (the drone's CDroneLaser particle): it draws through the child (6f198391) while emitting
            Check(cfg != null && cfg.Laser == 0x5cf355af && lib.Get(0x6f198391) != null, "fx.cfg: laser 5cf355af (+ its sparks 6f198391)");
            (int burn, int burnMax) = Life(lib, 0x5cf355af, 60);
            Check(burn is >= 60 and <= 60 + 46 && burnMax > 3, $"laser burn draws {burn} frames for 60 of emission (60..106), up to {burnMax}");
            Check(cfg != null && cfg.Events.Any(e => e.Clip == "A_cuttinglaser_drone" && e.Part == 0x63058345 && e.Bone == "Beacon_LCTR"),
                "fx.cfg: the laser clip's flare 63058345 on Beacon_LCTR");
            // the flare's fire puffs (fba6f455) are a flipbook (ATEX, 42aa343c: 4 x 4 tiles of 32): each draws one tile, the
            // first (age 0) the bottom-left one
            var puff = new RetroParticleSystem(lib, 0xfba6f455, Matrix4.Identity, follow: false);
            var puffs = new List<RetroBillboard>();
            puff.Step();
            puff.Collect(puffs);
            Vector4 uv0 = puffs.Count > 0 ? puffs[0].Uv : default;
            Check(puffs.Count > 0 && MathF.Abs(uv0.Z - uv0.X - 0.25f) < 1e-4f && MathF.Abs(uv0.W - uv0.Y - 0.25f) < 1e-4f
                && uv0.X == 0 && MathF.Abs(uv0.Y - 0.75f) < 1e-4f, $"fire puff flipbook: a new puff shows the bottom-left tile ({uv0})");
            // the sounds: game id -> (MusyX sfx id, shortest / longest seconds)
            var want = new (uint Id, int Musyx, float Min, float Max, string What)[]
            {
                (3078, 247, 0.9f, 1.1f, "unfold charge (sample at +3 notes, 0.7 s + 0.3 s fade)"),
                (703, 227, 0.25f, 0.45f, "blast per volley (one-shot sample)"),
                (738, 233, 2.3f, 2.5f, "cutting laser (looped sample, 1.9 s + 0.5 s fade)"),
                (3097, 226, 0.25f, 0.45f, "shot impact (one-shot sample)"),
            };
            foreach ((uint id, int musyx, float min, float max, string what) in want)
            {
                string wav = Path.Combine(gc, "fx", BodyFx.SfxFile(id));
                int rate = 0;
                short[]? pcm = File.Exists(wav) ? MusyX.ReadWav(wav, out rate) : null;
                float secs = pcm != null && rate > 0 ? pcm.Length / (float)rate : 0;
                int peak = pcm != null && pcm.Length > 0 ? pcm.Max(x => Math.Abs((int)x)) : 0;
                double rms = pcm != null && pcm.Length > 0 ? Math.Sqrt(pcm.Average(x => (double)x * x)) : 0;
                Check(cfg != null && cfg.Sfx.TryGetValue(id, out int m) && m == musyx && pcm != null && rate is >= 8000 and <= 48000
                    && secs >= min && secs <= max && peak > 1000,
                    $"sound {id} -> MusyX {musyx}, {what}: {secs:0.000} s @ {rate} Hz ({min}..{max} s), peak {peak}, rms {rms:0}");
            }
            using (IFileSource src = DiscImage.Open(mp1))
            {
                var audio = new Pak(src.ReadFile("AudioGrp.pak"), "AudioGrp.pak");
                Agsc drones = Agsc.Parse(audio.Get(0x98b283bf));
                foreach ((uint id, int musyx, _, _, _) in want)
                {
                    Agsc.Rendered? r = drones.Render((ushort)musyx);
                    Console.WriteLine($"  {id} -> {musyx}: {String.Join("; ", r?.Steps ?? new List<string>())}"
                        + (r?.Skipped.Count > 0 ? $" (skipped ops {String.Join(" ", r.Skipped.Select(x => x.ToString("x2")))})" : ""));
                }
            }
            string strip = Arg("--strip", "");
            if (strip.Length > 0) Strip(lib, Path.Combine(gc, "fx"), strip);
            Console.WriteLine(fails == 0 ? "  SUMMARY: PASS" : $"  SUMMARY: FAIL ({fails})");
            if (fails > 0) Environment.Exit(1);
        }

        // the recipe's paks (Metroid3.pak, AudioGrp.pak) from the disc, under `folder` (Trilogy: "MP1/")
        static void Import(string disc, string folder, string outDir)
        {
            using IFileSource src = DiscImage.Open(disc);
            var paks = new List<Pak>();
            foreach (string name in new[] { "Metroid3.pak", "AudioGrp.pak" })
            {
                byte[] b = src.ReadFile(folder + name);
                Console.WriteLine($"  {folder}{name}: sha1 {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant()}");
                paks.Add(new Pak(b, name));
            }
            Directory.CreateDirectory(outDir);
            BodyFx.Export(id => paks.First(p => p.Contains(id)).Get(id), Spec, Ancs, outDir);
        }

        // frames an effect draws anything when it emits for `emit` frames, and its most billboards in one frame
        static (int Frames, int Max) Life(RetroParticles lib, uint id, int emit)
        {
            var sys = new RetroParticleSystem(lib, id, Matrix4.Identity, follow: false);
            var bb = new List<RetroBillboard>();
            int last = -1, max = 0;
            for (int f = 0; f < 600 && !(f > emit && sys.Done); f++)
            {
                if (f == emit) sys.Emitting = false;
                sys.Step();
                bb.Clear();
                sys.Collect(bb);
                if (bb.Count > 0) last = f;
                max = Math.Max(max, bb.Count);
            }
            return (last + 1, max);
        }

        // side view (Prime x right, z up), 90 px a metre, additive, 8 frames of each effect
        static void Strip(RetroParticles lib, string fxDir, string path)
        {
            const int W = 220, H = 220;
            const float Sc = 90;
            var tex = new Dictionary<uint, (byte[] Rgba, int W, int H)>();
            (byte[], int, int) Tex(uint t)
            {
                if (!tex.TryGetValue(t, out var v))
                {
                    byte[] rgba = PngOut.Read(Path.Combine(fxDir, $"{t:x8}.png"), out int w, out int h);
                    tex[t] = v = (rgba, w, h);
                }
                return v;
            }
            var rows = new (string Title, (uint Id, Vector3 At, int Emit)[] Systems, int[] Frames)[]
            {
                ("gun flash 06296852", new[] { (0x06296852u, Vector3.Zero, 2) }, new[] { 0, 1, 2, 3, 4, 5, 6, 7 }),
                ("hit eb8b9043", new[] { (0xeb8b9043u, Vector3.Zero, 2) }, new[] { 0, 1, 2, 3, 4, 6, 8, 11 }),
                ("panels 19eecc2f + beacon d3792285", new[] { (0x19eecc2fu, new Vector3(-0.3f, 0, 0), 31), (0x19eecc2fu, new Vector3(0.3f, 0, 0), 31),
                    (0xd3792285u, new Vector3(0, 0, 0.45f), 31) }, new[] { 0, 4, 8, 16, 24, 31, 40, 50 }),
                ("hands a3b5243a", new[] { (0xa3b5243au, new Vector3(-0.35f, 0, 0), 20), (0xa3b5243au, new Vector3(0.35f, 0, 0), 20) },
                    new[] { 0, 3, 6, 10, 14, 19, 24, 30 }),
            };
            var sheet = new float[rows.Length * H, 8 * W, 3];
            for (int r = 0; r < rows.Length; r++)
            {
                var systems = rows[r].Systems.Select(s => (Sys: new RetroParticleSystem(lib, s.Id, Matrix4.CreateTranslation(s.At), false), s.Emit)).ToList();
                var bb = new List<RetroBillboard>();
                int col = 0;
                for (int f = 0; f <= rows[r].Frames.Max(); f++)
                {
                    foreach (var s in systems)
                    {
                        if (f == s.Emit) s.Sys.Emitting = false;
                        s.Sys.Step();
                    }
                    if (!rows[r].Frames.Contains(f)) continue;
                    bb.Clear();
                    foreach (var s in systems) s.Sys.Collect(bb);
                    foreach (RetroBillboard b in bb)
                    {
                        (byte[] t, int tw, int th) = Tex(b.Texture);
                        float half = b.Size * Sc / 2;
                        float cx = W / 2f + b.Position.X * Sc, cy = H * 0.62f - b.Position.Z * Sc;
                        float cos = MathF.Cos(MathHelper.DegreesToRadians(b.Rotation)), sin = MathF.Sin(MathHelper.DegreesToRadians(b.Rotation));
                        for (int y = (int)(cy - half * 1.5f); y <= (int)(cy + half * 1.5f); y++)
                        for (int x = (int)(cx - half * 1.5f); x <= (int)(cx + half * 1.5f); x++)
                        {
                            if (x < 0 || y < 0 || x >= W || y >= H || half < 0.5f) continue;
                            float dx = x - cx, dy = y - cy;
                            float u = (dx * cos + dy * sin) / (2 * half) + 0.5f, v = (-dx * sin + dy * cos) / (2 * half) + 0.5f;
                            if (u < 0 || v < 0 || u >= 1 || v >= 1) continue;
                            int ti = ((int)(v * th) * tw + (int)(u * tw)) * 4;
                            float a = t[ti + 3] / 255f * b.Color.W;
                            for (int k = 0; k < 3; k++) sheet[r * H + y, col * W + x, k] += t[ti + k] / 255f * b.Color[k] * a;
                        }
                    }
                    col++;
                }
            }
            int sw = 8 * W, sh = rows.Length * H;
            var rgba = new byte[sw * sh * 4];
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int o = (y * sw + x) * 4;
                for (int k = 0; k < 3; k++) rgba[o + k] = (byte)(Math.Clamp(sheet[y, x, k] + (k == 2 ? 0.1f : 0.075f), 0, 1) * 255);
                rgba[o + 3] = 255;
            }
            PngOut.Write(path, rgba, sw, sh);
            Console.WriteLine($"  strip: {path}");
        }
    }
}
