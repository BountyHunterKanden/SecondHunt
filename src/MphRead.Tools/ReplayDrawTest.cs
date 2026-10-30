using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // FAST SEEK (10-27): a prepared keyframe comes from a headless process that draws nothing; loaded into a viewer that
    // draws, the picture after the seek must be the one a straight playthrough draws (the other replay tests only check
    // the simulation, so a seek could be exact and still draw the wrong textures).
    //   -replaydrawtest [<file>|<golden name>|all] [at=<step>] [steps=120]
    //       writes a whole keyframe at `at` (default the middle) in a non-drawing host, then a drawing process plays
    //       straight to at+steps recording each step's draw list and HUD (textures by their pixels, meshes by model and
    //       mesh), loads the keyframe, plays the same steps again and compares. Exit code 0 = the same picture.
    internal static partial class ReplayTest
    {
        public static void DrawTest(string[] args)
        {
            string? child = args.FirstOrDefault(a => a.StartsWith("child="))?[6..];
            string target = args.Length > 1 && !args[1].Contains('=') ? args[1] : "all";
            int at = int.TryParse(args.FirstOrDefault(a => a.StartsWith("at="))?[3..], out int a) ? a : -1;
            int steps = int.TryParse(args.FirstOrDefault(a => a.StartsWith("steps="))?[6..], out int s) ? s : 120;
            if (!Setup())
            {
                return;
            }
            var files = new List<string>();
            if (target == "all")
            {
                files.AddRange(Goldens.Select(g => Path.Combine(GoldenDir(), g.Name + ReplayLibrary.Extension)));
            }
            else if (File.Exists(target))
            {
                files.Add(target);
            }
            else
            {
                files.AddRange(target.Split(',').Select(t => Path.Combine(GoldenDir(), t + ReplayLibrary.Extension)));
            }
            if (child != null)
            {
                string? r = DrawChild(Load(files[0]), child, at, steps);
                Console.WriteLine(r == null ? "CHILD PASS" : "CHILD FAIL: " + r);
                Environment.ExitCode = r == null ? 0 : 1;
                return;
            }
            int pass = 0, fail = 0;
            foreach (string path in files)
            {
                ReplayFile file = Load(path);
                if (MatchArenas.Find(file.Settings.Arena) == null)
                {
                    Console.WriteLine($"SKIP   {Path.GetFileNameWithoutExtension(path)}: arena not installed");
                    continue;
                }
                string? result = DrawOne(path, file, at, steps);
                Console.WriteLine($"{(result == null ? "PASS" : "FAIL")}   {Path.GetFileNameWithoutExtension(path)}"
                    + (result != null ? ": " + result : ""));
                if (result == null) pass++; else fail++;
            }
            Console.WriteLine($"{(fail == 0 ? "PASS" : "FAIL")}: {pass}/{pass + fail} replays draw the same after a prepared keyframe");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // both runs are this build, so a golden recorded under an older game logic version still compares like with like
        private static ReplayFile Load(string path)
        {
            ReplayFile file = ReplayFile.Load(path);
            file.SimVersion = ReplayFile.CurrentSimVersion;
            return file;
        }

        private static string? DrawOne(string path, ReplayFile file, int at, int steps)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            if (at < 0) at = file.Steps.Count / 2;
            string kf = Path.Combine(Path.GetTempPath(), "mph-drawtest-" + Path.GetFileNameWithoutExtension(path) + ".kf");
            var player = new ReplayPlayer(file);
            // as the Odin's :replayprep process: no drawing, a DS-sized view
            CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 256, viewHeight: 192);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                var writer = new SimSnapshot.KeyframeWriter(kbase);
                while (player.Position < at && !host.Ended && player.TryNext(out CampaignInput input))
                {
                    host.Step(input);
                    player.Check(host.Scene); // (advances Position; the picture is what this test compares)
                }
                if (player.Position != at) return $"the replay ended at {player.Position} (wanted {at} of {file.Steps.Count}, ended {host.Ended})";
                File.WriteAllBytes(kf, writer.Write(SimSnapshot.Take(at, ReplayCheckpoints.Roots(host), kbase.Snap)));
            }
            finally
            {
                host.Dispose();
            }
            (int code, string output) = RunChild("-replaydrawtest", path, "child=" + kf, "at=" + at, "steps=" + steps);
            foreach (string line in output.Split('\n').Where(l => l.TrimStart().StartsWith("child", StringComparison.OrdinalIgnoreCase)
                || l.Contains("CHILD") || l.StartsWith("   ")))
            {
                Console.WriteLine("       " + line.TrimEnd());
            }
            return code == 0 ? null : "the drawing process: " + output.Split('\n').LastOrDefault(l => l.Contains("CHILD"))?.Trim();
        }

        private static string? DrawChild(ReplayFile file, string kf, int at, int steps)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null) return "refused: " + why;
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: true, viewWidth: 1280, viewHeight: 720);
            try
            {
                if (player.CheckAfterStart() is string after) return "refused: " + after;
                ReplayCheckpoints.SetUpOnce();
                var kbase = ReplayCheckpoints.KeyframeBase(host);
                var loader = new SimSnapshot.KeyframeLoader(kbase);
                int until = Math.Min(file.Steps.Count, at + steps);
                var straight = new List<List<string>>();
                while (player.Position < until && !host.Ended && player.TryNext(out CampaignInput input))
                {
                    host.Step(input);
                    player.Check(host.Scene);
                    if (player.Position > at) straight.Add(Picture(host.Scene));
                }
                string? r = loader.Load(File.ReadAllBytes(kf));
                if (r != null) return "keyframe refused: " + r;
                player.Rewind(at);
                // as MatchReplay.AfterRestore: the HUD pieces painted again from the restored frames
                foreach (MphRead.Entities.PlayerEntity p in MphRead.Entities.PlayerEntity.Players) p.HostAfterRestore();
                int shown = 0, bad = 0;
                for (int i = 0; i < straight.Count && !host.Ended && player.TryNext(out CampaignInput input); i++)
                {
                    host.Step(input);
                    player.Check(host.Scene);
                    List<string> seek = Picture(host.Scene);
                    if (seek.SequenceEqual(straight[i])) continue;
                    bad++;
                    if (shown++ < 3)
                    {
                        Console.WriteLine($"child: step {at + 1 + i}: {straight[i].Count} straight vs {seek.Count} after the keyframe");
                        var a = new HashSet<string>(straight[i]);
                        var b = new HashSet<string>(seek);
                        foreach (string x in straight[i].Where(x => !b.Contains(x)).Distinct().OrderBy(x => x.StartsWith("SPIN") ? 0 : 1).Take(8)) Console.WriteLine("   only straight: " + x);
                        foreach (string x in seek.Where(x => !a.Contains(x)).Distinct().OrderBy(x => x.StartsWith("SPIN") ? 0 : 1).Take(8)) Console.WriteLine("   only seek:     " + x);
                        int first = Enumerable.Range(0, Math.Min(seek.Count, straight[i].Count)).FirstOrDefault(k => seek[k] != straight[i][k], -1);
                        if (first >= 0)
                        {
                            Console.WriteLine($"   first difference at item {first}:");
                            Console.WriteLine("   straight: " + straight[i][first]);
                            Console.WriteLine("   seek:     " + seek[first]);
                        }
                    }
                }
                Console.WriteLine($"child: {straight.Count} steps after the keyframe at {at}, {bad} drawn differently");
                return bad == 0 ? null : $"{bad} of {straight.Count} steps drawn differently";
            }
            finally
            {
                host.Dispose();
            }
        }

        // one step's picture, by content: ids differ between runs only when the picture would
        private static List<string> Picture(Scene scene)
        {
            var list = new List<string>();
            string Tex(int id) => scene.HostTextures.TryGetValue(id, out Scene.HostTexture? t)
                ? $"{t.Width}x{t.Height}:{PixelHash(t.Pixels):x8}" : $"missing#{id}";
            foreach ((IReadOnlyList<RenderItem> items, string pass) in new[] { (scene.OpaqueItems, "O"), (scene.DecalItems, "D"),
                (scene.TranslucentItems, "T") })
            {
                foreach (RenderItem item in items)
                {
                    string mesh = "";
                    if (item.Type == RenderItemType.Mesh)
                    {
                        mesh = item.ListId >= 1 && item.ListId <= scene.HostMeshes.Count
                            ? $"{scene.HostMeshes[item.ListId - 1].Model.Name}/{scene.HostMeshes[item.ListId - 1].Model.Meshes.ToList().IndexOf(scene.HostMeshes[item.ListId - 1].Mesh)}"
                            : $"badlist#{item.ListId}";
                    }
                    string tex = item.HasTexture ? Tex(item.TextureBindingId) : "-";
                    Vector3 p = item.Transform.ExtractTranslation();
                    list.Add($"{pass} {item.Type} {mesh} tex {tex} at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} a {item.Alpha:0.00}");
                }
            }
            for (int i = 0; i < scene.HudItemCount; i++)
            {
                Scene.HostHudItem h = scene.GetHudItem(i);
                string tex = h.SpriteIndex >= 0 ? $"sprite {PixelHash(scene.HudSprites[h.SpriteIndex].Pixels):x8}"
                    : h.BindingId >= 0 ? Tex(h.BindingId) : "-";
                list.Add($"HUD {tex} {h.Left:0.000},{h.Top:0.000}-{h.Right:0.000},{h.Bottom:0.000} a {h.Alpha:0.00}");
            }
            // each player's HUD pieces by name, so a wrong sprite above names the piece that painted it
            foreach (MphRead.Entities.PlayerEntity p in MphRead.Entities.PlayerEntity.Players.Where(p => p.LoadFlags.TestFlag(MphRead.Entities.LoadFlags.Active)))
            {
                foreach (System.Reflection.FieldInfo f in HudFields)
                {
                    object? v = f.GetValue(p);
                    var insts = new List<(string, MphRead.Hud.HudObjectInstance?)>();
                    if (v is MphRead.Hud.HudObjectInstance one) insts.Add((f.Name, one));
                    else if (v is MphRead.Hud.HudObjectInstance?[] arr) insts.AddRange(arr.Select((x, k) => ($"{f.Name}[{k}]", x)));
                    // meters left out: their Bar/Tank frame is draw-time scratch, set per tile while drawing
                    foreach ((string name, MphRead.Hud.HudObjectInstance? i) in insts)
                    {
                        if (i?.CharacterData == null) continue;
                        list.Add($"PIECE p{p.SlotIndex} {name} {i.Width}x{i.Height} f{i.CurrentFrame} pal{i.PaletteIndex} {PixelHash(i.Texture):x8}");
                    }
                }
            }
            foreach (MphRead.Entities.EntityBase e in scene.Entities)
            {
                if (e is not MphRead.Entities.SpinningEntityBase) continue;
                var fl = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                list.Add($"SPIN {e.GetType().Name} {e.Id} init {e.Initialized} {typeof(MphRead.Entities.SpinningEntityBase).GetField("_spin", fl)!.GetValue(e)} spd {typeof(MphRead.Entities.SpinningEntityBase).GetField("_spinSpeed", fl)!.GetValue(e)} ft {scene.FrameTime}");
            }
            return list;
        }

        private static readonly System.Reflection.FieldInfo[] HudFields = typeof(MphRead.Entities.PlayerEntity)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(f => f.FieldType == typeof(MphRead.Hud.HudObjectInstance) || f.FieldType == typeof(MphRead.Hud.HudObjectInstance[])
                ).ToArray();

        private static uint PixelHash(ColorRgba[] pixels)
        {
            uint h = 2166136261;
            foreach (ColorRgba c in pixels)
            {
                h = (h ^ c.Red) * 16777619;
                h = (h ^ c.Green) * 16777619;
                h = (h ^ c.Blue) * 16777619;
                h = (h ^ c.Alpha) * 16777619;
            }
            return h;
        }
    }
}
