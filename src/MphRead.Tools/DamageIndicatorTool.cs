using System;
using System.IO;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRead
{
    // -dmgind: the HUD's damage direction indicator (hud "damage" model, nodes north..nw).
    //   -dmgind dump              its nodes: bounds, mesh, texture, decoded vertices (DS 256x192 space)
    //   -dmgind render [8way|4way|off] the campaign player shot from 8 directions (front, front-right, ... front-left), the HUD
    //                             recorded after each: one flash per shot. Default the true-direction flash
    //                             (Features.DamageTrueDirection), then a turn test: shot from the right, she turns, the flash
    //                             must stay aimed at the shooter. "8way" = the 8 parts, "4way" = vanilla's 4-way pick, "off" = none drawn.
    //                             PNGs at 16:9 and 4:3 in extract_out/damage_indicator
    internal static class DamageIndicatorTool
    {
        private static readonly string[] Names = { "front", "frontright", "right", "backright", "back", "backleft", "left", "frontleft" };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (args.Length >= 2 && args[1] == "render")
            {
                string mode = args.Length >= 3 ? args[2] : "true";
                Features.DamageTrueDirection = mode == "true";
                Features.DamageDiagonals = mode != "4way";
                Features.DamageIndicator = mode != "off";
                Render(1920, 1080, mode);
                Render(1440, 1080, mode);
                return;
            }
            Dump();
        }

        private static void Render(int width, int height, string mode)
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "damage_indicator"));
            Directory.CreateDirectory(outDir);
            using CampaignHost host = CampaignHost.Start("UNIT2_LAND", hunter: Hunter.Samus, collectDrawItems: true,
                viewWidth: width, viewHeight: height);
            int landing = 0;
            for (; landing < 3000 && (landing < 300 || CameraSequence.Current != null); landing++) host.Step(default);
            for (int i = 0; i < 60; i++) host.Step(default);
            PlayerEntity player = host.Player;
            int baseItems = host.Scene.HudItemCount;
            Console.WriteLine($"{width}x{height} {mode}: {baseItems} HUD items unhit");
            int fails = 0;
            string tag = $"{width}x{height}_{mode}";
            void Hit(int dir)
            {
                Vector3 facing = player.FacingVector.WithY(0).Normalized();
                Vector3 right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
                float angle = MathHelper.DegreesToRadians(dir * 45);
                Vector3 toShooter = facing * MathF.Cos(angle) + right * MathF.Sin(angle);
                // the shot's travel (shooter -> player), small so the push barely moves her
                player.TakeDamage(1, DamageFlags.None, -toShooter * 0.01f, null);
            }
            for (int dir = 0; dir < 8; dir++)
            {
                for (int i = 0; i < 140; i++) host.Step(default); // the last part's 126 steps run out
                Hit(dir);
                host.Step(default); // timer 125: lit (the blink's bit 8)
                int parts = host.Scene.HudItemCount - baseItems, want = mode == "off" ? 0 : 1;
                Console.WriteLine($"  shot from {Names[dir],-10}: {parts} part(s) drawn{(parts == want ? "" : $"  <-- expected {want}")}");
                if (parts != want)
                {
                    fails++;
                }
                CampaignSim.RenderHud(host.Scene, width, height, Path.Combine(outDir, $"dmg_{tag}_{dir}_{Names[dir]}.png"));
            }
            for (int i = 0; i < 140; i++) host.Step(default);
            for (int dir = 0; dir < 8; dir++)
            {
                Hit(dir);
            }
            host.Step(default);
            Console.WriteLine($"  all 8 at once: {host.Scene.HudItemCount - baseItems} part(s) drawn");
            CampaignSim.RenderHud(host.Scene, width, height, Path.Combine(outDir, $"dmg_{tag}_all.png"));
            if (mode == "true")
            {
                // shot from the right (the shooter 10 units off that way), then she turns right: the flash has to
                // swing toward the top of the view and keep pointing at that spot
                for (int i = 0; i < 140; i++) host.Step(default);
                Vector3 shooter = player.Position + Vector3.Cross(player.FacingVector.WithY(0).Normalized(), Vector3.UnitY) * 10;
                Hit(2);
                int shots = 0;
                for (int i = 0; i < 120; i++)
                {
                    host.Step(new CampaignInput { AimDelta = new System.Numerics.Vector2(6, 0) });
                    if (host.Scene.HudItemCount - baseItems != 1)
                    {
                        continue; // the blink's off half
                    }
                    if (host.Scene.DamageArrowItem < 0)
                    {
                        Console.WriteLine($"  turning, step {i,3}: no flash item  <-- off");
                        fails++;
                        continue;
                    }
                    var item = host.Scene.GetHudItem(host.Scene.DamageArrowItem);
                    float cx = (item.Left + item.Right) / 2 / (2 * height / 192f / width), cy = (item.Top + item.Bottom) / 2 / (2 / 192f);
                    Vector3 facing = player.FacingVector.WithY(0).Normalized();
                    Vector3 to = (shooter - player.Position).WithY(0);
                    float want = MathF.Atan2(Vector3.Dot(to, Vector3.Cross(facing, Vector3.UnitY)), Vector3.Dot(to, facing));
                    float got = MathF.Atan2(cx, cy);
                    float off = MathF.Abs(MathHelper.RadiansToDegrees(MathF.IEEERemainder(got - want, MathF.Tau)));
                    if (i % 16 == 0 || off > 2)
                    {
                        Console.WriteLine($"  turning, step {i,3}: shooter at {MathHelper.RadiansToDegrees(want),7:0.0} deg, flash at {MathHelper.RadiansToDegrees(got),7:0.0}{(off > 2 ? "  <-- off" : "")}");
                    }
                    if (off > 2)
                    {
                        fails++;
                    }
                    if (shots < 4 && i % 24 == 0)
                    {
                        CampaignSim.RenderHud(host.Scene, width, height, Path.Combine(outDir, $"dmg_{tag}_turn{shots++}.png"));
                    }
                }
            }
            Console.WriteLine(fails == 0 ? "  PASS" : $"  FAIL ({fails})");
        }

        private static void Dump()
        {
            Model model = Read.GetModelInstance("damage", dir: MetaDir.Hud).Model;
            Console.WriteLine($"damage: scale {model.Scale}, {model.Nodes.Count} nodes, {model.Meshes.Count} meshes, {model.Materials.Count} materials");
            for (int m = 0; m < model.Materials.Count; m++)
            {
                Material mat = model.Materials[m];
                string tex = mat.TextureId == -1 ? "none" : $"{model.Recolors[0].Textures[mat.TextureId].Width}x{model.Recolors[0].Textures[mat.TextureId].Height}";
                Console.WriteLine($"  material {m} '{mat.Name}': texture {mat.TextureId} ({tex}) palette {mat.PaletteId} alpha {mat.Alpha} poly {mat.PolygonMode} wrap {mat.XRepeat}/{mat.YRepeat}");
            }
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                Node node = model.Nodes[i];
                Console.WriteLine($"  node {i} '{node.Name}': mesh {node.MeshId} count {node.MeshCount} min {node.MinBounds} max {node.MaxBounds} enabled {node.Enabled}");
                if (node.MeshCount == 0)
                {
                    continue;
                }
                Mesh mesh = model.Meshes[node.MeshId / 2];
                float[] verts = DsDisplayList.Decode(model, mesh, isRoom: false);
                Console.Write($"    mesh {node.MeshId / 2} material {mesh.MaterialId}, {verts.Length / DsDisplayList.Stride} verts:");
                for (int v = 0; v < verts.Length; v += DsDisplayList.Stride)
                {
                    Console.Write($" ({verts[v]:0.##},{verts[v + 1]:0.##} uv {verts[v + 10]:0.###},{verts[v + 11]:0.###})");
                }
                Console.WriteLine();
            }
        }
    }
}
