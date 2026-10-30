using System;
using System.IO;
using MphRecomp.Frontend;
using MphRecomp.Import;

namespace MphRead
{
    // The stand-ins' character pictures (StandinPictures) drawn on the PC from the AMHE1 data:
    //   -standinpics <outDir>   writes <skin>_full.png / <skin>_head.png for every stand-in, as the app draws them
    internal static class StandinPicturesTool
    {
        static readonly string[] Skins = { "DarkSamusStandin", "MP1PowerTrooper", "MP1IceTrooper", "MP1WaveTrooper", "MP1PlasmaTrooper" };

        public static void Run(string[] args)
        {
            string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "standinpics");
            if (!ToolPaths.UseDesktopExtraction()) return;
            Directory.CreateDirectory(outDir);
            foreach (string skin in Skins)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Model? model = StandinPictures.Load(skin);
                if (model == null)
                {
                    Console.WriteLine($"{skin}: no stand-in model");
                    continue;
                }
                (byte[] full, byte[] head) = StandinPictures.Draw(model, skin);
                PngOut.Write(Path.Combine(outDir, skin + "_full.png"), full, CharacterPictures.FullW, CharacterPictures.FullH);
                PngOut.Write(Path.Combine(outDir, skin + "_head.png"), head, CharacterPictures.HeadW, CharacterPictures.HeadH);
                int solid = 0;
                for (int i = 3; i < full.Length; i += 4) if (full[i] > 24) solid++;
                Console.WriteLine($"{skin}: {sw.ElapsedMilliseconds} ms (load + draw), body pixels {solid}");
            }
            Console.WriteLine($"PNGs in {outDir}");
        }
    }
}
