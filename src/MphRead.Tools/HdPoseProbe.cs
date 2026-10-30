using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRead
{
    // MEASUREMENT for rigging the user's Brawl trophy meshes to the DS hunter skeletons: how far is each
    // trophy's sculpted pose from the poses the game's own animations reach?
    //
    // Both meshes are normalised the same way (feet on y=0, XZ centroid at the origin, scaled to the same
    // height). The trophy is then compared against EVERY frame of EVERY DS clip, facing both ways, by a
    // symmetric Chamfer distance (mean nearest-point distance trophy->DS and DS->trophy, in units of the
    // body height). Output per trophy: the idle-pose score, the best-matching game pose, and point dumps
    // (for overlay plots) of the trophy vs idle and vs the best match. Data only -- nothing is changed.
    // Run: MphRead.Tools.dll -hdpose [TrophyDir ...]   (default: every folder in brawl_extract/Converted)
    internal static class HdPoseProbe
    {
        static readonly (string Trophy, Hunter Rig)[] Pairs =
        {
            ("Kanden", Hunter.Kanden), ("Sylux", Hunter.Sylux), ("Weavel", Hunter.Weavel), ("Trace", Hunter.Trace),
            ("Noxus", Hunter.Noxus), ("Spire", Hunter.Spire), ("Samus", Hunter.Samus), ("SamusR1", Hunter.Samus),
            ("Barriersuit", Hunter.Samus), ("Gravitysuit", Hunter.Samus), ("Normalsuit", Hunter.Samus),
            ("Darksuit", Hunter.Samus), ("Darksamus", Hunter.Samus), ("SzerosuitR1", Hunter.Samus),
        };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string conv = Path.Combine(repo, "brawl_extract", "Converted");
            string outDir = Path.Combine(Path.GetTempPath(), "mph_hdpose");
            Directory.CreateDirectory(outDir);
            var only = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"trophies from {conv}; point dumps -> {outDir}");
            Console.WriteLine($"{"trophy",-12} {"rig",-7} {"verts",6} {"idle f0",8} {"best game pose",-26} {"best",6}  facing   (Chamfer, fraction of body height)");
            foreach (var (trophy, rig) in Pairs)
            {
                if (only.Count > 0 && !only.Contains(trophy)) continue;
                string dir = Path.Combine(conv, trophy);
                string? dae = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dae").FirstOrDefault() : null;
                if (dae == null) { Console.WriteLine($"{trophy,-12} (no .dae)"); continue; }
                Probe(trophy, rig, dae, outDir);
            }
        }

        static void Probe(string trophy, Hunter rig, string daePath, string outDir)
        {
            DaeModel hd = DaeModel.Load(daePath);
            var hdPts = Dedup(hd.Meshes.SelectMany(m => m.Positions).Select(p => new Vector3(p.X, p.Y, p.Z)));
            var hdN = Normalize(hdPts, out float hdHeight);
            var hdSample = Subsample(hdN, 900);

            Model model = Read.GetModelInstance(Metadata.HunterModels[rig][0]).Model;
            var a = new BipedAnimator(model);
            var pal = new float[16 * 32];
            a.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(a, 0f, pal);
            var skin = GeometryBaker.Bake(model, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var local = new List<(Vector3 P, int S)>();
            foreach (var b in skin)
                for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                    local.Add((new Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), (int)b.Verts[i + 11]));

            List<Vector3> PoseDs(int clip, int frame)
            {
                a.Legs.SetAnimation(clip); a.Legs.AnimInfo.Frame[0] = frame;
                a.Torso.SetAnimation(clip); a.Torso.AnimInfo.Frame[0] = frame;
                HunterRig.Pose(a, 0f, pal);
                var pts = local.Select(v => Vector3.TransformPosition(v.P, Pal(pal, v.S)) * model.Scale.X);
                // DS rigs face -Z; turn them to face +Z (the trophy convention is tested both ways below)
                return Normalize(Dedup(pts.Select(p => new Vector3(-p.X, p.Y, -p.Z))), out _);
            }

            var idle = PoseDs((int)PlayerAnimation.Idle, 0);
            float idleFront = Chamfer(hdSample, Subsample(idle, 900));
            float idleBack = Chamfer(hdSample, Subsample(Flip(idle), 900));
            bool trophyFacesPlusZ = idleFront <= idleBack;

            float best = float.MaxValue; int bc = 0, bf = 0; bool bflip = false;
            int clips = model.AnimationGroups.Node.Count;
            for (int c = 0; c < clips; c++)
            {
                if (c is (int)PlayerAnimation.Morph or (int)PlayerAnimation.Unmorph) continue; // mid-transformation
                int n = model.AnimationGroups.Node[c].FrameCount;
                for (int f = 0; f < n; f++)
                {
                    var ds = Subsample(PoseDs(c, f), 900);
                    foreach (bool flip in new[] { false, true })
                    {
                        float s = Chamfer(hdSample, flip ? Flip(ds) : ds);
                        if (s < best) { best = s; bc = c; bf = f; bflip = flip; }
                    }
                }
            }
            Console.WriteLine($"{trophy,-12} {rig,-7} {hdPts.Count,6} {Math.Min(idleFront, idleBack),8:0.000} {((PlayerAnimation)bc) + " f" + bf,-26} {best,6:0.000}  {(bflip ? "-Z" : "+Z")}" +
                $"   (trophy height {hdHeight:0.00} units; idle facing test: +Z {idleFront:0.000} vs -Z {idleBack:0.000})");
            Dump(Path.Combine(outDir, $"{trophy}_hd.txt"), hdN);
            Dump(Path.Combine(outDir, $"{trophy}_idle.txt"), trophyFacesPlusZ ? idle : Flip(idle));
            var bestPts = PoseDs(bc, bf);
            Dump(Path.Combine(outDir, $"{trophy}_best.txt"), bflip ? Flip(bestPts) : bestPts);
        }

        static Matrix4 Pal(float[] p, int s)
        {
            int o = s * 16;
            return new Matrix4(p[o], p[o + 1], p[o + 2], p[o + 3], p[o + 4], p[o + 5], p[o + 6], p[o + 7],
                p[o + 8], p[o + 9], p[o + 10], p[o + 11], p[o + 12], p[o + 13], p[o + 14], p[o + 15]);
        }

        static List<Vector3> Dedup(IEnumerable<Vector3> pts) =>
            pts.GroupBy(p => (MathF.Round(p.X, 4), MathF.Round(p.Y, 4), MathF.Round(p.Z, 4))).Select(g => g.First()).ToList();

        // feet at y=0, XZ centroid at 0, height 1
        static List<Vector3> Normalize(List<Vector3> pts, out float height)
        {
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            height = maxY - minY;
            float cx = pts.Average(p => p.X), cz = pts.Average(p => p.Z);
            float h = height;
            return pts.Select(p => new Vector3((p.X - cx) / h, (p.Y - minY) / h, (p.Z - cz) / h)).ToList();
        }

        static List<Vector3> Flip(List<Vector3> pts) => pts.Select(p => new Vector3(-p.X, p.Y, -p.Z)).ToList();

        static List<Vector3> Subsample(List<Vector3> pts, int n)
        {
            if (pts.Count <= n) return pts;
            var r = new List<Vector3>(n);
            double step = pts.Count / (double)n;
            for (int i = 0; i < n; i++) r.Add(pts[(int)(i * step)]);
            return r;
        }

        static float Chamfer(List<Vector3> a, List<Vector3> b) => (Directed(a, b) + Directed(b, a)) / 2f;
        static float Directed(List<Vector3> from, List<Vector3> to)
        {
            double sum = 0;
            foreach (var p in from)
            {
                float best = float.MaxValue;
                foreach (var q in to) { float d = (p - q).LengthSquared; if (d < best) best = d; }
                sum += MathF.Sqrt(best);
            }
            return (float)(sum / from.Count);
        }

        static void Dump(string path, List<Vector3> pts) =>
            File.WriteAllLines(path, pts.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z}")));
    }
}
