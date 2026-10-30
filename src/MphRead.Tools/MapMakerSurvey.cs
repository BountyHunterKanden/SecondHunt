using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Formats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // MAP MAKER E1 survey, headless (docs/MAP_EDITOR.md sections 4b and 5):
    //   -mmsurvey tex out=<dir>     every distinct texture the multiplayer rooms use: a numbered contact sheet per room + a table
    internal static class MapMakerSurvey
    {
        public static void Run(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "tex";
            string outDir = "mmsurvey";
            foreach (string arg in args.Skip(2))
            {
                if (arg.StartsWith("out=")) outDir = arg[4..];
                else throw new ArgumentException($"unknown option {arg}");
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            Directory.CreateDirectory(outDir);
            if (mode == "tex") Textures(outDir);
            else if (mode == "anim") Anims();
            else if (mode == "dims") MapMakerDims.Run();
            else throw new ArgumentException($"unknown mode {mode}");
        }

        // dump a room's texcoord animation groups (how the game scrolls its lava / water)
        static void Anims()
        {
            foreach (string room in new[] { "MP7 PROCESSOR CORE", "MP3 PROVING GROUND", "MP9 CRYOCHASM" })
            {
                Model model = Read.GetRoomModelInstance(room).Model;
                Console.WriteLine($"{room}: {model.AnimationGroups.Texcoord.Count} texcoord groups");
                for (int g = 0; g < model.AnimationGroups.Texcoord.Count; g++)
                {
                    TexcoordAnimationGroup grp = model.AnimationGroups.Texcoord[g];
                    Console.WriteLine($" group {g}: frames {grp.FrameCount} anims {grp.Count} scales {grp.Scales.Count} rots {grp.Rotations.Count} trans {grp.Translations.Count}");
                    foreach ((string name, TexcoordAnimation a) in grp.Animations)
                    {
                        Console.WriteLine($"  {name}: scaleBlend {a.ScaleBlendS},{a.ScaleBlendT} len {a.ScaleLutLengthS},{a.ScaleLutLengthT} idx {a.ScaleLutIndexS},{a.ScaleLutIndexT}" +
                            $" rot {a.RotateBlendZ} len {a.RotateLutLengthZ} idx {a.RotateLutIndexZ} unused2B {a.Unused2B}" +
                            $" trans blend {a.TranslateBlendS},{a.TranslateBlendT} len {a.TranslateLutLengthS},{a.TranslateLutLengthT} idx {a.TranslateLutIndexS},{a.TranslateLutIndexT}");
                        Console.WriteLine($"    S: {string.Join(' ', grp.Translations.Skip(a.TranslateLutIndexS).Take(Math.Min(6, (int)a.TranslateLutLengthS)))}" +
                            $" ... last {(a.TranslateLutLengthS > 0 ? grp.Translations[a.TranslateLutIndexS + a.TranslateLutLengthS - 1] : 0)}");
                        Console.WriteLine($"    T: {string.Join(' ', grp.Translations.Skip(a.TranslateLutIndexT).Take(Math.Min(6, (int)a.TranslateLutLengthT)))}" +
                            $" ... last {(a.TranslateLutLengthT > 0 ? grp.Translations[a.TranslateLutIndexT + a.TranslateLutLengthT - 1] : 0)}");
                        Console.WriteLine($"    scale S0 {(a.ScaleLutLengthS > 0 ? grp.Scales[a.ScaleLutIndexS] : 0)} T0 {(a.ScaleLutLengthT > 0 ? grp.Scales[a.ScaleLutIndexT] : 0)}");
                    }
                }
            }
        }

        static void Textures(string outDir)
        {
            var seen = new HashSet<string>();
            var table = new List<string>();
            foreach (RoomMetadata meta in Metadata.RoomList.Where(r => r.Multiplayer && !r.FirstHunt && r.Name.StartsWith("MP")))
            {
                Model model;
                try { model = Read.GetRoomModelInstance(meta.Name).Model; }
                catch (Exception e) { Console.WriteLine($"{meta.Name}: {e.Message}"); continue; }
                var items = new List<(int Tex, int Pal, string Mat, IReadOnlyList<ColorRgba> Px, Texture T)>();
                foreach (Material m in model.Materials)
                {
                    if (m.TextureId < 0) continue;
                    Texture t = model.Recolors[0].Textures[m.TextureId];
                    if (items.Any(i => i.Tex == m.TextureId && i.Pal == m.PaletteId)) continue;
                    IReadOnlyList<ColorRgba> px = model.GetPixels(m.TextureId, m.PaletteId, 0);
                    string key = $"{t.Width}x{t.Height}:{string.Join(',', px.Take(64).Select(p => p.Red ^ p.Green ^ p.Blue))}:{px.Count}:{px.Sum(p => p.Red + 3 * p.Green + 7 * p.Blue)}";
                    if (!seen.Add(key)) continue;
                    items.Add((m.TextureId, m.PaletteId, m.Name, px, t));
                }
                if (items.Count == 0) continue;
                const int cell = 136, cols = 8;
                int rows = (items.Count + cols - 1) / cols;
                using var img = new Image<Rgba32>(cols * cell, rows * cell, new Rgba32(40, 0, 40));
                for (int n = 0; n < items.Count; n++)
                {
                    (int tex, int pal, string mat, IReadOnlyList<ColorRgba> px, Texture t) = items[n];
                    table.Add($"{meta.Name}\t#{n}\ttex {tex}\tpal {pal}\t{t.Width}x{t.Height}\t{t.Format}\tmat {mat}");
                    int ox = n % cols * cell + 4, oy = n / cols * cell + 4;
                    int step = Math.Max(1, Math.Max(t.Width, t.Height) / 128);
                    for (int y = 0; y < t.Height / step && y < 128; y++)
                    for (int x = 0; x < t.Width / step && x < 128; x++)
                    {
                        ColorRgba c = px[y * step * t.Width + x * step];
                        img[ox + x, oy + y] = new Rgba32(c.Red, c.Green, c.Blue, 255);
                    }
                }
                img.SaveAsPng(Path.Combine(outDir, $"tex_{meta.Name.Replace(' ', '_')}.png"));
                Console.WriteLine($"{meta.Name}: {items.Count} new textures");
            }
            File.WriteAllLines(Path.Combine(outDir, "textures.tsv"), table);
        }
    }
}
