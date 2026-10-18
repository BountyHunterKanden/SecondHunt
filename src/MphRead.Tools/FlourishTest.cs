using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -flourishtest [--root <dir with bodies/>] [--sheet <png>]
    // The native bodies' idle flourish candidates (Core Campaign/BodyFlourish.cs, owner 2026-10-15) on imported bodies (run
    // -bodytest MP1PowerTrooper / -darksamustest all with the same --out first): each candidate's clip is there (chains
    // made at load), how far its first and last pose sit from the map's idle at t=0 (the largest bone move, model units:
    // what the cross-fades into and out of it have to cover) and its largest key-to-key bone jump against its median one
    // (a pop at a chain's join). --sheet: 6 evenly spaced front views per candidate, a row each.
    internal static class FlourishTest
    {
        public static void Run(string[] args)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string root = Path.GetFullPath(Arg("--root", Path.Combine(Path.GetTempPath(), "mph-bodytest")));
            string sheet = Arg("--sheet", "");
            var tiles = new List<(string Label, Rgba32[] Px)>();
            int fails = 0;
            foreach ((string body, BodyFlourish.Set set) in new[] { ("MP1PowerTrooper", BodyFlourish.Trooper),
                ("MP2DarkSamus", BodyFlourish.Echoes), ("MP3DarkSamus", BodyFlourish.Corruption) })
            {
                string dir = Path.Combine(root, "bodies", body);
                if (!NativeBody.Exists(dir))
                {
                    Console.WriteLine($"  [SKIP] {body}: no body at {dir}");
                    continue;
                }
                NativeBody b = NativeBody.Load(dir);
                BodyClips? map = BodyClips.For(b);
                if (map?.FlourishSet != set)
                {
                    Console.WriteLine($"  FAIL {body}: map's flourish set is {map?.FlourishSet?.Name ?? "none"}, want {set.Name}");
                    fails++;
                    continue;
                }
                HdGun.Anim idle = b.Clip(map.ClipFor(PlayerAnimation.Idle))!;
                Vector3[] idlePose = Positions(b, idle, 0);
                Console.WriteLine($"  {set.Name} on {body} (idle {idle.Name}):");
                int keep = set.Index;
                for (int i = 0; i < set.List.Length; i++)
                {
                    set.Index = i;
                    BodyFlourish.Candidate c = set.List[i];
                    string? clip = BodyFlourish.Clip(b, set);
                    if (c.Parts.Length == 0)
                    {
                        Console.WriteLine($"    {i}: {c.Label}: (none) {(clip == null ? "ok" : "FAIL: got " + clip)}");
                        if (clip != null) fails++;
                        continue;
                    }
                    if (clip == null || b.Clip(clip) is not HdGun.Anim a)
                    {
                        Console.WriteLine($"    {i}: {c.Label}: FAIL, no clip ({string.Join("+", c.Parts)})");
                        fails++;
                        continue;
                    }
                    float start = Dist(Positions(b, a, 0), idlePose), end = Dist(Positions(b, a, a.Duration), idlePose);
                    var jumps = new List<float>();
                    Vector3[] last = Positions(b, a, 0);
                    for (int k = 1; k < a.Keys; k++)
                    {
                        Vector3[] now = Positions(b, a, k * a.Tick);
                        jumps.Add(Dist(now, last));
                        last = now;
                    }
                    jumps.Sort();
                    float median = jumps.Count > 0 ? jumps[jumps.Count / 2] : 0, max = jumps.Count > 0 ? jumps[^1] : 0;
                    Console.WriteLine($"    {i}: {c.Label,-34} {a.Duration,5:F2} s  start {start,5:F2} / end {end,5:F2} from idle  "
                        + $"largest key jump {max:F3} (median {median:F3}, x{(median > 0 ? max / median : 0):F1})  [{clip}]");
                    if (sheet.Length > 0)
                    {
                        for (int f = 0; f < 6; f++)
                        {
                            b.Pose(a, a.Duration * f / 5);
                            tiles.Add(($"{set.Name}: {c.Label} t={a.Duration * f / 5:F2}", BodyTest.Splat(b, front: true)));
                        }
                    }
                }
                set.Index = keep;
            }
            if (sheet.Length > 0 && tiles.Count > 0) BodyTest.SaveSheet(tiles, sheet);
            Console.WriteLine(fails == 0 ? "  flourishtest PASS" : $"  flourishtest FAIL ({fails})");
        }

        static Vector3[] Positions(NativeBody b, HdGun.Anim a, float t)
        {
            b.Pose(a, t);
            var p = new Vector3[b.Data.BoneCount];
            for (int i = 0; i < p.Length; i++) p[i] = b.World[i].ExtractTranslation();
            return p;
        }

        static float Dist(Vector3[] a, Vector3[] b)
        {
            float m = 0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, (a[i] - b[i]).Length);
            return m;
        }
    }
}
