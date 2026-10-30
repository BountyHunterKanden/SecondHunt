using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Frontend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MphRead
{
    // -fegrooves [frame,...]: the two models the front end's code draws behind every menu page (ov0 0x212622c loads
    // frontend2d/slots + lines with their Idle animations; 0x2125cd4 draws them, owner queue #33: the "grooves" with
    // lights travelling through them). Prints their bounds and draws them alone in DS menu space (both screens,
    // 256x384, seam at y 0) at each frame -> extract_out/frontend/grooves.png.
    internal static class FrontendGrooves
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int[] frames = args.Length >= 2 ? args[1].Split(',').Select(Int32.Parse).ToArray() : new[] { 0, 15, 30, 45, 60, 90 };
            FrontendSession session = FrontendSession.Load(Paths.FileSystem);
            MenuWidgets widgets = session.Menu.Widgets;
            var models = new[] { ("slots", @"_archives\frontend2d\slots_Model.bin", @"_archives\frontend2d\slots_Idle_Anim.bin"),
                ("lines", @"_archives\frontend2d\lines_Model.bin", @"_archives\frontend2d\lines_Idle_Anim.bin") };
            var tris = new List<WidgetTri>();
            using var sheet = new Image<Rgba32>((256 + 4) * frames.Length, 384 * 2 + 4, new Rgba32(40, 40, 40, 255));
            for (int m = 0; m < models.Length; m++)
            {
                (string name, string model, string anim) = models[m];
                ModelInstance inst = widgets.Instance(model, anim);
                Console.WriteLine($"{name}: {inst.Model.Nodes.Count} nodes, {inst.Model.Meshes.Count} meshes, {inst.Model.Materials.Count} materials, "
                    + $"{MenuWidgets.FrameCount(inst)} frames, scale {inst.Model.Scale}");
                foreach (var mat in inst.Model.Materials)
                    Console.WriteLine($"  material {mat.Name}: alpha {mat.Alpha}, render {mat.RenderMode}, polygon {mat.PolygonMode}, texture {mat.TextureId}");
                for (int i = 0; i < frames.Length; i++)
                {
                    tris.Clear();
                    widgets.Evaluate(inst, frames[i], loop: true, alpha: 1, tris);
                    float x0 = tris.Min(t => Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))), x1 = tris.Max(t => Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
                    float y0 = tris.Min(t => Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))), y1 = tris.Max(t => Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
                    float a0 = tris.Min(t => t.A.A), a1 = tris.Max(t => t.A.A);
                    Console.WriteLine($"  frame {frames[i]}: {tris.Count} tris, x {x0:0.#}..{x1:0.#}, y {y0:0.#}..{y1:0.#}, alpha {a0:0.##}..{a1:0.##}");
                    var list = new UiDrawList(session.Textures);
                    list.Quad(-1, 0, 0, 256, 384, 0, 0, 1, 1, 0.1f, 0.25f, 0.1f, 1);
                    foreach (WidgetTri t in tris.OrderBy(t => t.Z))
                    {
                        UiVertex V(UiVertex v) { v.Y = 192 - v.Y; return v; }
                        list.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, V(t.A), V(t.B), V(t.C));
                    }
                    using Image<Rgba32> img = FrontendTool.RasterCanvas(list, 256, 384);
                    int px = i * 260, py = m * 388;
                    sheet.Mutate(c => c.DrawImage(img, new Point(px, py), 1f));
                }
            }
            string path = Path.Combine(FrontendTool.OutDir(), "grooves.png");
            sheet.SaveAsPng(path);
            Console.WriteLine("-> " + path);
        }
    }
}
