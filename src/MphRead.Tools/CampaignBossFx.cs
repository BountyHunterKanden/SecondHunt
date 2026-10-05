using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MphRead.Effects;
using MphRead.Entities;
using MphRead.Entities.Enemies;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -bossfx [room=UNIT3_B2] [seconds=120]: the owner's VDO 2 Slench fight (2026-10-03, room 76 Biodefense Chamber 08)
    // ran at 120 fps except waves every ~7 s in which frames came 20 -> 60 ms apart while the GL thread's own work stayed
    // 3-5 ms (the GPU behind). Runs a boss room headless with Samus kept alive at her spawn and, per frame, counts what
    // the draw lists hold and how much of a 1920x1080 screen the particles / trails cover (their quads projected from
    // Samus's eye looking at the boss, with the game's projection). Prints a line per second, then the worst frames
    // with what covered the screen.
    internal static partial class CampaignSim
    {
        public static void BossFx(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT3_B2";
            int seconds = args.Length >= 3 ? Int32.Parse(args[2]) : 120;
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                arriving: false);
            Scene scene = host.Scene;
            const double screen = 1920.0 * 1080.0;
            var frames = new List<FxFrame>();
            int lastSecond = -1;
            FxFrame? secMax = null;
            var secStates = new SortedSet<string>();
            for (int f = 0; f < seconds * 60; f++)
            {
                host.Player.Health = host.Player.HealthMax;
                host.Step(default);
                if (host.Ended)
                {
                    Console.WriteLine($"  f{f}: host ended");
                    break;
                }
                EntityBase? boss = FindBoss(scene);
                Vector3 eye = host.Player.Position + new Vector3(0, 1.3f, 0);
                Vector3 target = boss?.Position ?? eye + Vector3.UnitZ;
                Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
                Matrix4 vp = view * scene.PerspectiveMatrix;
                Matrix4 inv = view.ClearTranslation().Inverted();
                var fr = new FxFrame { Frame = f, State = boss != null ? BossState(boss) : "-" };
                foreach ((IReadOnlyList<RenderItem> list, int pass) in new[] { (scene.OpaqueItems, 0), (scene.DecalItems, 1), (scene.TranslucentItems, 2) })
                {
                    foreach (RenderItem it in list)
                    {
                        fr.Items[pass]++;
                        if (it.Type == RenderItemType.Particle || it.Type == RenderItemType.TrailSingle)
                        {
                            fr.Particles++;
                            double a = QuadArea(it, vp, inv) / screen;
                            fr.Coverage += a;
                            string key = $"{it.Type} tex {it.TextureBindingId} {it.PolygonMode}";
                            fr.ByTex[key] = fr.ByTex.GetValueOrDefault(key) + a;
                            fr.CountByTex[key] = fr.CountByTex.GetValueOrDefault(key) + 1;
                        }
                        else if (pass == 2 && it.Type == RenderItemType.Mesh && it.ListId >= 1 && it.ListId <= scene.HostMeshes.Count)
                        {
                            string name = scene.HostMeshes[it.ListId - 1].Model.Name;
                            fr.Meshes[name] = fr.Meshes.GetValueOrDefault(name) + 1;
                        }
                    }
                }
                fr.Elements = PoolField<List<EffectElementEntry>>(scene, "_activeElements").Count;
                frames.Add(fr);
                secStates.Add(fr.State);
                if (secMax == null || fr.Coverage > secMax.Coverage) secMax = fr;
                int second = f / 60;
                if (second != lastSecond && f % 60 == 59)
                {
                    var sec = frames.Skip(frames.Count - 60).ToList();
                    Console.WriteLine($"  {second,3}s: particles max {sec.Max(x => x.Particles),4}, coverage max {sec.Max(x => x.Coverage),6:0.00} screens, "
                        + $"translucent items max {sec.Max(x => x.Items[2]),4}, opaque {sec.Max(x => x.Items[0]),4}, active fx elements max "
                        + $"{sec.Max(x => x.Elements),3}; boss {string.Join("/", secStates)}");
                    lastSecond = second;
                    secStates.Clear();
                    secMax = null;
                }
            }
            Console.WriteLine("worst frames by particle coverage:");
            foreach (FxFrame fr in frames.OrderByDescending(x => x.Coverage).Take(12).OrderBy(x => x.Frame))
            {
                Console.WriteLine($"  f{fr.Frame,5} ({fr.Frame / 60.0,6:0.00} s) boss {fr.State}: {fr.Particles} particles cover {fr.Coverage:0.00} screens, "
                    + $"{fr.Items[2]} translucent items, {fr.Elements} fx elements");
                foreach (var kv in fr.ByTex.OrderByDescending(kv => kv.Value).Take(4))
                {
                    Console.WriteLine($"      {kv.Key}: {fr.CountByTex[kv.Key]} quads, {kv.Value:0.00} screens");
                }
                if (fr.Meshes.Count > 0)
                {
                    Console.WriteLine("      translucent meshes: " + string.Join(", ", fr.Meshes.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")));
                }
            }
            Console.WriteLine("by boss state (frames, mean / max coverage, mean particles):");
            foreach (var g in frames.GroupBy(x => x.State).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  {g.Key,-14} {g.Count(),5}  {g.Average(x => x.Coverage),6:0.00} / {g.Max(x => x.Coverage),6:0.00}  {g.Average(x => x.Particles),6:0.0}");
            }
        }

        private sealed class FxFrame
        {
            public int Frame;
            public string State = "";
            public readonly int[] Items = new int[3];
            public int Particles;
            public double Coverage;
            public int Elements;
            public readonly Dictionary<string, double> ByTex = new();
            public readonly Dictionary<string, int> CountByTex = new();
            public readonly Dictionary<string, int> Meshes = new();
        }

        private static EntityBase? FindBoss(Scene scene)
        {
            foreach (EntityBase e in scene.Entities)
            {
                if (e is Enemy41Entity || e.GetType().Name is "Enemy45Entity" or "Enemy46Entity" or "Enemy28Entity")
                {
                    return e;
                }
            }
            return null;
        }

        private static string BossState(EntityBase boss)
        {
            for (Type? t = boss.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField("_state1", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                {
                    object? v = f.GetValue(boss);
                    return boss is Enemy41Entity && v is byte b ? ((SlenchState)b).ToString() : $"{boss.GetType().Name}:{v}";
                }
            }
            return boss.GetType().Name;
        }

        // the quad as CampaignActivity draws it (DrawQuad: Points[v * 2 + 1], stack * view_inv), in pixels, each corner
        // clamped to the screen (a rough clip); 0 when a corner is behind the eye
        private static double QuadArea(RenderItem it, Matrix4 vp, Matrix4 viewInvRot)
        {
            if (it.Points == null || it.Points.Length < 8)
            {
                return 0;
            }
            Matrix4 bill = it.BillboardMode == BillboardMode.Sphere ? viewInvRot
                : it.BillboardMode == BillboardMode.Cylinder ? viewInvRot : Matrix4.Identity;
            Matrix4 m = bill * it.Transform * vp;
            Span<Vector2> p = stackalloc Vector2[4];
            // a particle is a triangle fan (corners in order), a trail a strip (0 1 3 2 around)
            ReadOnlySpan<int> order = it.Type == RenderItemType.TrailSingle ? stackalloc int[] { 0, 1, 3, 2 } : stackalloc int[] { 0, 1, 2, 3 };
            for (int v = 0; v < 4; v++)
            {
                Vector4 c = new Vector4(it.Points[order[v] * 2 + 1], 1) * m;
                if (c.W <= 0.01f)
                {
                    return 0;
                }
                float x = (c.X / c.W * 0.5f + 0.5f) * 1920, y = (c.Y / c.W * 0.5f + 0.5f) * 1080;
                p[v] = new Vector2(Math.Clamp(x, 0, 1920), Math.Clamp(y, 0, 1080));
            }
            double area = 0;
            for (int v = 0; v < 4; v++)
            {
                Vector2 a = p[v], b = p[(v + 1) % 4];
                area += (double)a.X * b.Y - (double)b.X * a.Y;
            }
            return Math.Abs(area) / 2;
        }
    }
}
