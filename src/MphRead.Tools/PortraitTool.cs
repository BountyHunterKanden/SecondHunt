using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Formats;
using MphRead.Hud;
using MphRecomp.Frontend;
using MphRecomp.Import;

namespace MphRead
{
    // -portraits [outDir] [hunter=head.png ...]: the HUD hunter portraits (enemy_<hunter>.bin, the hit-target box and
    //   scoreboard picture): Samus's palette-index map printed, PNGs of each one in outDir (default extract_out/portraits),
    //   and each head picture (CharacterPictures' _head.png) composed into that hunter's portrait (HudPortraits), as the
    //   custom characters' HUD shows them
    internal static class PortraitTool
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string outDir = args.Length >= 2 ? args[1] : Path.Combine("extract_out", "portraits");
            Directory.CreateDirectory(outDir);
            for (int i = 0; i < 7; i++)
            {
                HudObject obj = HudInfo.GetHudObject(HudElements.Hunters[i]);
                byte[] idx = HudPortraits.Linear(obj.CharacterData, obj.Width, obj.Height);
                Console.WriteLine($"{HudElements.Hunters[i]}: {obj.Width}x{obj.Height}, palette {obj.PaletteData.Count}");
                if (i == 0)
                {
                    for (int y = 0; y < obj.Height; y++)
                    {
                        var line = new System.Text.StringBuilder();
                        for (int x = 0; x < obj.Width; x++)
                        {
                            line.Append(idx[y * obj.Width + x].ToString("x"));
                        }
                        Console.WriteLine("  " + line);
                    }
                }
                var rgba = new byte[obj.Width * obj.Height * 4];
                for (int p = 0; p < idx.Length; p++)
                {
                    if (idx[p] == 0)
                    {
                        continue;
                    }
                    ColorRgba c = obj.PaletteData[idx[p]];
                    rgba[p * 4] = c.Red;
                    rgba[p * 4 + 1] = c.Green;
                    rgba[p * 4 + 2] = c.Blue;
                    rgba[p * 4 + 3] = 255;
                }
                PngOut.Write(Path.Combine(outDir, $"vanilla_{i}.png"), rgba, obj.Width, obj.Height);
            }
            for (int a = 2; a < args.Length; a++)
            {
                string[] f = args[a].Split('=', 2);
                if (f.Length != 2 || !Enum.TryParse(f[0], ignoreCase: true, out Hunter hunter))
                {
                    continue;
                }
                byte[] head = PngOut.Read(f[1], out int hw, out int hh);
                WriteSprite(PortraitPath(outDir, f[1]), HudPortraits.Compose(hunter, head, hw, hh));
            }
        }

        private static string PortraitPath(string outDir, string head) => Path.Combine(outDir, "custom_" + Path.GetFileNameWithoutExtension(head) + ".png");

        private static void WriteSprite(string path, Scene.HostHudSprite sprite)
        {
            var rgba = new byte[sprite.Width * sprite.Height * 4];
            for (int p = 0; p < sprite.Pixels.Length; p++)
            {
                ColorRgba c = sprite.Pixels[p];
                rgba[p * 4] = c.Red; rgba[p * 4 + 1] = c.Green; rgba[p * 4 + 2] = c.Blue; rgba[p * 4 + 3] = c.Alpha;
            }
            PngOut.Write(path, rgba, sprite.Width, sprite.Height);
            Console.WriteLine($"  {path}");
        }
    }
}
