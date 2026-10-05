using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Formats;
using MphRecomp.Render;

namespace MphRead
{
    // Front-end (menu) research: what a ROM menu widget model holds, so the recomp's own menus can place it.
    //   -feprobe <model path> [anim path]   e.g. -feprobe "main menu\options_Model.bin" "main menu\options_Idle_Anim.bin"
    //   prints nodes (transform, bounds), materials (texture size), animation groups, and each mesh's vertex bounds
    internal static class FrontendProbe
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string modelPath = args[1];
            string? animPath = args.Length >= 3 ? args[2] : null;
            Model model = Read.ReadModelFile("probe", modelPath, animPath);
            Console.WriteLine($"{modelPath}  scale {model.Scale.X}  nodes {model.Nodes.Count}  meshes {model.Meshes.Count}  " +
                $"materials {model.Materials.Count}  textures {model.Recolors[0].Textures.Count}");
            AnimationGroups ag = model.AnimationGroups;
            Console.WriteLine($"anim groups: node {ag.Node.Count} material {ag.Material.Count} texcoord {ag.Texcoord.Count} texture {ag.Texture.Count}");
            foreach (NodeAnimationGroup g in ag.Node)
            {
                Console.WriteLine($"  node anim: frames {g.FrameCount}, animated nodes {g.Animations.Count} ({String.Join(", ", g.Animations.Keys)})");
            }
            foreach (MaterialAnimationGroup g in ag.Material)
            {
                Console.WriteLine($"  material anim: frames {g.FrameCount}, materials {String.Join(", ", g.Animations.Keys)}");
            }
            foreach (TexcoordAnimationGroup g in ag.Texcoord)
            {
                Console.WriteLine($"  texcoord anim: frames {g.FrameCount}, materials {String.Join(", ", g.Animations.Keys)}");
            }
            foreach (TextureAnimationGroup g in ag.Texture)
            {
                Console.WriteLine($"  texture anim: frames {g.FrameCount}, materials {String.Join(", ", g.Animations.Keys)}");
            }
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                Node n = model.Nodes[i];
                Console.WriteLine($"node {i} '{n.Name}' parent {n.ParentIndex} child {n.ChildIndex} next {n.NextIndex} " +
                    $"enabled {n.Enabled} meshes {n.MeshCount}@{n.MeshId / 2} pos {n.Position} scale {n.Scale} " +
                    $"angle {n.Angle} bb {n.MinBounds}..{n.MaxBounds} billboard {n.BillboardMode}");
            }
            for (int i = 0; i < model.Materials.Count; i++)
            {
                Material m = model.Materials[i];
                string tex = m.TextureId == -1 ? "none"
                    : $"{m.TextureId} {model.Recolors[0].Textures[m.TextureId].Width}x{model.Recolors[0].Textures[m.TextureId].Height} " +
                      $"{model.Recolors[0].Textures[m.TextureId].Format}";
                Console.WriteLine($"material {i} '{m.Name}' tex {tex} pal {m.PaletteId} alpha {m.Alpha} poly {m.PolygonMode} " +
                    $"render {m.RenderMode} diffuse {m.Diffuse} lighting {m.Lighting} texgen {m.TexgenMode} scale {m.ScaleS},{m.ScaleT}");
            }
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh mesh = model.Meshes[i];
                float[] v = DsDisplayList.Decode(model, mesh, isRoom: false);
                int count = v.Length / DsDisplayList.Stride;
                float minX = Single.MaxValue, minY = Single.MaxValue, minZ = Single.MaxValue;
                float maxX = Single.MinValue, maxY = Single.MinValue, maxZ = Single.MinValue;
                float minS = Single.MaxValue, minT = Single.MaxValue, maxS = Single.MinValue, maxT = Single.MinValue;
                var mtx = new HashSet<float>();
                for (int k = 0; k < count; k++)
                {
                    int o = k * DsDisplayList.Stride;
                    minX = Math.Min(minX, v[o]); maxX = Math.Max(maxX, v[o]);
                    minY = Math.Min(minY, v[o + 1]); maxY = Math.Max(maxY, v[o + 1]);
                    minZ = Math.Min(minZ, v[o + 2]); maxZ = Math.Max(maxZ, v[o + 2]);
                    minS = Math.Min(minS, v[o + 10]); maxS = Math.Max(maxS, v[o + 10]);
                    minT = Math.Min(minT, v[o + 11]); maxT = Math.Max(maxT, v[o + 11]);
                    mtx.Add(v[o + 12]);
                }
                float sc = model.Scale.X;
                Console.WriteLine($"mesh {i} material {mesh.MaterialId} tris {count / 3} " +
                    $"x {minX * sc:0.###}..{maxX * sc:0.###} y {minY * sc:0.###}..{maxY * sc:0.###} z {minZ * sc:0.###}..{maxZ * sc:0.###} " +
                    $"st {minS:0.##},{minT:0.##}..{maxS:0.##},{maxT:0.##} mtx [{String.Join(",", mtx.OrderBy(m => m))}]");
            }
        }
    }
}
