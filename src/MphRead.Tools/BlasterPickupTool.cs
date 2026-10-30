using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Formats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // Pirate Blaster pickup art, headless:
    //   -blasterpickup dump [out=<dir>]
    // prints each MPH weapon pickup model's nodes, meshes, materials and textures (with out=, one texture sheet per model)
    internal static class BlasterPickupTool
    {
        static readonly string[] PickupModels =
        {
            "pick_wpn_all", "pick_wpn_electro", "pick_wpn_jackhammer", "pick_wpn_snipergun", "pick_wpn_shotgun",
            "pick_wpn_mortar", "pick_wpn_ghostbuster", "pick_wpn_gorea", "pick_wpn_missile",
        };

        public static void Run(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "dump";
            string? outDir = null;
            foreach (string arg in args.Skip(2))
            {
                if (arg.StartsWith("out=")) outDir = arg[4..];
                else throw new ArgumentException($"unknown option {arg}");
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (outDir != null) Directory.CreateDirectory(outDir);
            if (mode == "dump")
            {
                foreach (string name in PickupModels) Dump(name, outDir);
                return;
            }
            if (mode.StartsWith("dump:"))
            {
                Dump(mode[5..], outDir);
                return;
            }
            if (mode.StartsWith("verts:"))
            {
                Model model = Read.GetModelInstance(mode[6..]).Model;
                foreach (Mesh mesh in model.Meshes)
                {
                    float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom: false);
                    Console.WriteLine($"mesh material {mesh.MaterialId} '{model.Materials[mesh.MaterialId].Name}': {v.Length / 13} verts");
                    for (int i = 0; i < v.Length; i += 13)
                    {
                        Console.WriteLine($"  p {v[i]:0.000},{v[i + 1]:0.000},{v[i + 2]:0.000} n {v[i + 3]:0.00},{v[i + 4]:0.00},{v[i + 5]:0.00} c {v[i + 6]:0.00},{v[i + 7]:0.00},{v[i + 8]:0.00},{v[i + 9]:0} uv {v[i + 10]:0.000},{v[i + 11]:0.000}");
                    }
                }
                return;
            }
            throw new ArgumentException($"unknown mode {mode}");
        }

        static void Dump(string name, string? outDir)
        {
            Model model = Read.GetModelInstance(name).Model;
            Console.WriteLine($"== {name}: scale {model.Scale} nodes {model.Nodes.Count} meshes {model.Meshes.Count} materials {model.Materials.Count} recolors {model.Recolors.Count} textures {model.Recolors[0].Textures.Count} palettes {model.Recolors[0].Palettes.Count}");
            foreach (Node n in model.Nodes)
            {
                Console.WriteLine($"  node '{n.Name}' parent {n.ParentIndex} meshes {n.MeshCount}@{n.MeshId} pos {n.Position} angle {n.Angle} scale {n.Scale} bb {n.MinBounds}..{n.MaxBounds} bill {n.BillboardMode}");
            }
            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Mesh m = model.Meshes[i];
                DisplayList dl = model.DisplayLists[m.DlistId];
                IReadOnlyList<RenderInstruction> list = model.RenderInstructionLists[m.DlistId];
                string codes = String.Join(" ", list.GroupBy(x => x.Code).Select(g => $"{g.Key}x{g.Count()}"));
                string colors = String.Join(" ", list.Where(x => x.Code == InstructionCode.COLOR || x.Code == InstructionCode.DIF_AMB)
                    .Select(x => Rgb15(x.Arguments[0])).Distinct());
                Console.WriteLine($"  mesh {i}: material {m.MaterialId} dlist {m.DlistId} size {dl.Size} instr {list.Count}: {codes}; colors {colors}");
            }
            for (int i = 0; i < model.Materials.Count; i++)
            {
                Material m = model.Materials[i];
                string tex = m.TextureId >= 0 ? $"{model.Recolors[0].Textures[m.TextureId].Format} {model.Recolors[0].Textures[m.TextureId].Width}x{model.Recolors[0].Textures[m.TextureId].Height}" : "none";
                Console.WriteLine($"  mat {i} '{m.Name}': tex {m.TextureId} ({tex}) pal {m.PaletteId} light {m.Lighting} alpha {m.Alpha} cull {m.Culling} poly {m.PolygonMode} render {m.RenderMode} texgen {m.TexgenMode} rep {m.XRepeat}/{m.YRepeat} diff {m.Diffuse.Red},{m.Diffuse.Green},{m.Diffuse.Blue} amb {m.Ambient.Red},{m.Ambient.Green},{m.Ambient.Blue} spec {m.Specular.Red},{m.Specular.Green},{m.Specular.Blue} anim {m.AnimationFlags} st {m.ScaleS},{m.ScaleT}/{m.TranslateS},{m.TranslateT}");
            }
            Console.WriteLine($"  anims: node {model.AnimationGroups.Node.Count} mat {model.AnimationGroups.Material.Count} texcoord {model.AnimationGroups.Texcoord.Count} tex {model.AnimationGroups.Texture.Count}");
            if (outDir != null) Sheet(model, Path.Combine(outDir, $"tex_{name}.png"));
        }

        static string Rgb15(uint v) => $"{(v & 31) * 255 / 31},{(v >> 5 & 31) * 255 / 31},{(v >> 10 & 31) * 255 / 31}";

        static void Sheet(Model model, string png)
        {
            var mats = model.Materials.Where(m => m.TextureId >= 0).Select(m => (m.TextureId, m.PaletteId)).Distinct().ToList();
            if (mats.Count == 0) return;
            int scale = 4;
            int h = mats.Max(m => model.Recolors[0].Textures[m.TextureId].Height) * scale;
            int w = mats.Sum(m => model.Recolors[0].Textures[m.TextureId].Width * scale + 4);
            using var img = new Image<Rgba32>(w, h, new Rgba32(255, 0, 255));
            int x0 = 0;
            foreach ((int tex, int pal) in mats)
            {
                Texture t = model.Recolors[0].Textures[tex];
                IReadOnlyList<ColorRgba> px = model.GetPixels(tex, pal, 0);
                for (int i = 0; i < px.Count; i++)
                {
                    ColorRgba c = px[i];
                    var o = new Rgba32(c.Red, c.Green, c.Blue, c.Alpha);
                    for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        img[x0 + i % t.Width * scale + dx, i / t.Width * scale + dy] = o;
                    }
                }
                x0 += t.Width * scale + 4;
            }
            img.SaveAsPng(png);
        }
    }
}
