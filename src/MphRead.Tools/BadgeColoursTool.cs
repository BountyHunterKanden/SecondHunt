using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRecomp.Frontend;
using MphRecomp.Import;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // Every hunter and stand-in in each of the lobby's 8 colours, drawn on the PC the way CharacterPictureRender.ColourModel
    // picks the recolour, with the colour badge's automatic pair (MultiplayerMenu.DominantColours) for each:
    //   -badgecolours <outDir>   writes <who>_c<colour>.png and badges.json (for the badge eyedropper page)
    //   -badgecolours png <cache/characters dir pulled from a device> <outDir>   the same from the device's drawn pictures
    //     (the HD skins; a dev build with a bake_colours marker in that folder draws every look in all 8 colours)
    internal static class BadgeColoursTool
    {
        static readonly string[] Skins = { "DarkSamusStandin", "MP1PowerTrooper", "MP1IceTrooper", "MP1WaveTrooper", "MP1PlasmaTrooper" };
        static readonly Hunter[] Hunters = { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };

        public static void Run(string[] args)
        {
            if (args.Length > 3 && args[1] == "png")
            {
                FromPictures(Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
                return;
            }
            string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "badgecolours");
            if (!ToolPaths.UseDesktopExtraction()) return;
            Directory.CreateDirectory(outDir);
            Type menu = typeof(MultiplayerMenu);
            MethodInfo bins = menu.GetMethod("ColourBins", BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo dominant = menu.GetMethod("DominantColours", BindingFlags.NonPublic | BindingFlags.Static)!;
            var rows = new List<string>();
            var whos = Hunters.Select(h => (Who: h.ToString(), Model: Metadata.HunterModels[h][0], Rig: h, Look: LookAlikes.Look.None, Float: false))
                .Concat(Skins.Select(s => StandinPictures.Of(s) is { } of && (!of.Float || DarkSamusStandin.Available)
                    ? (Who: s, of.Model, of.Rig, of.Look, of.Float) : (Who: s, Model: "", Rig: Hunter.Samus, Look: LookAlikes.Look.None, Float: false))
                    .Where(w => w.Model != ""));
            foreach (var w in whos)
            {
                float[]? baseShare = null;
                for (int copy = 0; copy < LookAlikes.Colours; copy++)
                {
                    LookAlikes.Variant v = CharacterPictures.VariantOf(copy);
                    if (w.Look != LookAlikes.Look.None) LookAlikes.Register(new[] { (w.Look, v) });
                    else HunterColours.Install();
                    Model m = Read.GetModelInstance(w.Model, false, MetaDir.Models, noCache: true).Model;
                    int recolor = w.Look != LookAlikes.Look.None ? LookAlikes.RecolorFor(w.Look, v, w.Model)
                        : copy < HunterColours.Copies ? copy : HunterColours.RecolorFor(w.Model, copy - HunterColours.Copies);
                    (byte[] full, _) = StandinPictures.DrawColour(m, w.Rig, w.Float, recolor);
                    string name = $"{w.Who}_c{copy}.png";
                    PngOut.Write(Path.Combine(outDir, name), full, CharacterPictures.FullW, CharacterPictures.FullH);
                    object pair = dominant.Invoke(null, new object?[] { full, copy == 0 ? null : baseShare })!;
                    var (a, b) = ((ValueTuple<(byte R, byte G, byte B), (byte R, byte G, byte B)>)pair);
                    if (copy == 0)
                    {
                        object res = bins.Invoke(null, new object[] { full })!;
                        baseShare = (float[])res.GetType().GetField("Item2")!.GetValue(res)!;
                    }
                    rows.Add($"{{\"who\":\"{w.Who}\",\"colour\":{copy},\"recolor\":{recolor},\"file\":\"{name}\","
                        + $"\"autoA\":[{a.R},{a.G},{a.B}],\"autoB\":[{b.R},{b.G},{b.B}]}}");
                    Console.WriteLine($"{w.Who} c{copy}: recolor {recolor}, auto {a} / {b}");
                }
            }
            File.WriteAllText(Path.Combine(outDir, "badges.json"), "[\n" + string.Join(",\n", rows) + "\n]\n");
            Console.WriteLine($"{rows.Count} pictures in {outDir} ({CharacterPictures.FullW}x{CharacterPictures.FullH})");
        }

        // c<n>~orig-<hunter>_..._v3<ColourLook>_full.png (hunters, colours 0-7), c<n>~<skin>_<stamp>_v3<ColourLook>_full.png (skins, 1-7) and
        // <skin>_<stamp>_v3_full.png / _v2_full.png (a skin's own colour, copy 0)
        static void FromPictures(string inDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            Type menu = typeof(MultiplayerMenu);
            MethodInfo bins = menu.GetMethod("ColourBins", BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo dominant = menu.GetMethod("DominantColours", BindingFlags.NonPublic | BindingFlags.Static)!;
            var found = new SortedDictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(inDir, "*_full.png"))
            {
                string f = Path.GetFileName(path);
                int copy = 0;
                string rest = f;
                if (f.Length > 3 && f[0] == 'c' && f[2] == '~')
                {
                    if (!f.EndsWith($"_v3{CharacterPictures.ColourLook}_full.png")) continue; // (an older tint)
                    copy = f[1] - '0';
                    rest = f[3..];
                }
                else if (!f.EndsWith("_v3_full.png") && !f.EndsWith("_v2_full.png")) continue;
                string who = rest[..rest.IndexOf('_')];
                if (who.StartsWith("orig-")) who = char.ToUpperInvariant(who[5]) + who[6..];
                if (!found.TryGetValue(who, out var colours)) found[who] = colours = new SortedDictionary<int, string>();
                colours[copy] = path;
            }
            var rows = new List<string>();
            foreach ((string who, var colours) in found)
            {
                float[]? baseShare = null;
                foreach ((int copy, string path) in colours)
                {
                    byte[] full = PngOut.Read(path, out int w, out int h);
                    if (w != CharacterPictures.FullW || h != CharacterPictures.FullH) continue;
                    if (copy == 0)
                    {
                        object res = bins.Invoke(null, new object[] { full })!;
                        baseShare = (float[])res.GetType().GetField("Item2")!.GetValue(res)!;
                    }
                    object pair = dominant.Invoke(null, new object?[] { full, copy == 0 ? null : baseShare })!;
                    var (a, b) = ((ValueTuple<(byte R, byte G, byte B), (byte R, byte G, byte B)>)pair);
                    string name = $"{who}_c{copy}.png";
                    File.Copy(path, Path.Combine(outDir, name), true);
                    rows.Add($"{{\"who\":\"{who}\",\"colour\":{copy},\"recolor\":-1,\"file\":\"{name}\","
                        + $"\"autoA\":[{a.R},{a.G},{a.B}],\"autoB\":[{b.R},{b.G},{b.B}]}}");
                }
            }
            File.WriteAllText(Path.Combine(outDir, "badges.json"), "[\n" + string.Join(",\n", rows) + "\n]\n");
            Console.WriteLine($"{rows.Count} pictures from {found.Count} looks in {outDir}");
        }
    }
}
