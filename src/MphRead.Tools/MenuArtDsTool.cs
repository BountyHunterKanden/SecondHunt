using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Entities;
using MphRecomp.Frontend;
using MphRecomp.Import;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MphRead
{
    // The menus' backdrop from the DS bodies (StandinPictures.MenuArt) drawn on the PC from the AMHE1 data:
    //   -menuartds <outDir> [width]   writes <id>.png and <id>_cmp.png (the DS art | ours | ours under the menus' green
    //                                 tint) for the low-poly Dark Samus and every hunter, as the app draws them
    internal static class MenuArtDsTool
    {
        public static void Run(string[] args)
        {
            string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "menuartds");
            int w = args.Length > 2 ? int.Parse(args[2]) : 512;
            int h = (int)MathF.Round(w / MenuSamus.Aspect);
            // tuning: [clip (-1 = the model as built)] [span share] [head at]
            int clip = args.Length > 3 ? int.Parse(args[3]) : (int)PlayerAnimation.Idle;
            float span = args.Length > 4 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : StandinPictures.MenuArtSpan;
            float headAt = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : StandinPictures.MenuArtHeadAt;
            if (!ToolPaths.UseDesktopExtraction()) return;
            Directory.CreateDirectory(outDir);
            var ids = new List<string> { MphRecomp.Multiplayer.Characters.DarkSamusStandinSkin };
            foreach (Hunter hunter in StandinPictures.MenuArtHunters) ids.Add(StandinPictures.MenuArtId(hunter));
            using Image<Rgba32> ds = DsArt();
            ds.Mutate(c => c.Resize(w, h, KnownResamplers.NearestNeighbor));
            foreach (string id in ids)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Model? model = StandinPictures.LoadMenuArt(id);
                if (model == null)
                {
                    Console.WriteLine($"{id}: no model");
                    continue;
                }
                byte[] px = StandinPictures.MenuArt(model, id, w, h, 2, clip, span, headAt);
                string name = id.Replace(':', '_');
                PngOut.Write(Path.Combine(outDir, name + ".png"), px, w, h);
                using var ours = Image.LoadPixelData<Rgba32>(px, w, h);
                using var cmp = new Image<Rgba32>(w * 3 + 8, h, new Rgba32(40, 40, 40, 255));
                cmp.Mutate(c => c.DrawImage(ds, new Point(0, 0), 1f).DrawImage(ours, new Point(w + 4, 0), 1f));
                using (Image<Rgba32> tinted = ours.Clone())
                {
                    tinted.ProcessPixelRows(a =>
                    {
                        for (int y = 0; y < a.Height; y++)
                            foreach (ref Rgba32 p in a.GetRowSpan(y))
                                p = new Rgba32((byte)(p.R * 11 / 32), (byte)((p.G * 11 + 255 * 21) / 32), (byte)(p.B * 11 / 32), 255);
                    });
                    cmp.Mutate(c => c.DrawImage(tinted, new Point(2 * (w + 4), 0), 1f));
                }
                cmp.SaveAsPng(Path.Combine(outDir, name + "_cmp.png"));
                var hd = StandinPictures.LastHead;
                Console.WriteLine($"{id}: {sw.ElapsedMilliseconds} ms; head {hd.Center} r {hd.Radius:0.000}; body {hd.Min} .. {hd.Max}");
            }
            Console.WriteLine($"PNGs in {outDir}");
        }

        // sourceimages/bg main1 (top) over main2 (touch screen), as FrontendSession draws them
        private static Image<Rgba32> DsArt()
        {
            var img = new Image<Rgba32>(256, 384);
            for (int s = 0; s < 2; s++)
            {
                (int bw, int bh, byte[] rgba) = FrontendSession.LoadBitmap(Path.Combine(Paths.FileSystem, "sourceimages", "bg", s == 0 ? "main1.bin" : "main2.bin"));
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int o = (y * bw + x) * 4;
                        img[x, y + s * 192] = new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], 255);
                    }
            }
            return img;
        }
    }
}
