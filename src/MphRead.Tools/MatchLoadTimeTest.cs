using System;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -matchloadtime [runs=5] [arenas=Combat Hall,Alinos Perch,Data Shrine] [draw=1] [keep=1: Read.HostKeepAcrossScenes] [prewarm=1: SceneSetup.HostPrewarmHunterResources first]: how long a match start takes
    // (CampaignHost.StartMatch, as the App's: draw items collected, 4 seats of mixed hunters), each run after the last
    // one's scene, with CampaignHost.MatchLoadTimes' phases. The first run of a process includes the JIT and the file
    // reads' cold cache.
    internal static class MatchLoadTimeTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int runs = 5;
            string[] arenas = ["Combat Hall", "Alinos Perch", "Data Shrine"];
            bool draw = true;
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("runs=")) runs = Int32.Parse(arg[5..]);
                else if (arg.StartsWith("arenas=")) arenas = arg[7..].Split(',');
                else if (arg.StartsWith("draw=")) draw = arg[5..] != "0";
                else if (arg.StartsWith("keep=")) Read.HostKeepAcrossScenes = arg[5..] != "0";
            }
            if (args.Contains("prewarm=1"))
            {
                // as the menus would before the first match: the hunters parsed and decoded with no scene
                var pw = Stopwatch.StartNew();
                SceneSetup.HostPrewarmHunterResources();
                Console.WriteLine($"prewarm {pw.Elapsed.TotalMilliseconds:0} ms");
            }
            Hunter[] hunters = [Hunter.Samus, Hunter.Kanden, Hunter.Sylux, Hunter.Noxus];
            if (args.Contains("split=1"))
            {
                Split(runs);
                return;
            }
            for (int run = 0; run < runs; run++)
            {
                foreach (string arena in arenas)
                {
                    var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find(arena)!.Name };
                    for (int i = 0; i < hunters.Length; i++)
                    {
                        settings.Players.Add(new MatchPlayer { Hunter = hunters[i], Recolor = i, Bot = i > 0,
                            BotLevel = 1, Nickname = $"Player{i + 1}" });
                    }
                    var sw = Stopwatch.StartNew();
                    using CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: draw);
                    double start = sw.Elapsed.TotalMilliseconds;
                    for (int f = 0; f < 60; f++)
                    {
                        host.Step(default);
                    }
                    Console.WriteLine($"run {run + 1} {arena,-16} start {start,6:0} ms [{CampaignHost.MatchLoadTimes}],"
                        + $" 60 frames {sw.Elapsed.TotalMilliseconds - start,5:0} ms");
                }
            }
            // what keeping the hunters costs: the managed heap with no match running (the kept models + their pixels)
            Read.ClearCache();
            Console.WriteLine($"heap between matches {GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:0.0} MB");
        }

        // where the hunters' share goes: the model files' parse vs their textures' decode (as BindTexture's)
        private static void Split(int runs)
        {
            for (int run = 0; run < runs; run++)
            {
                Read.ClearCache();
                var sw = Stopwatch.StartNew();
                var models = Metadata.HunterModels.Values.SelectMany(m => m).Select(n => Read.GetModelInstance(n).Model).ToList();
                double parse = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                int textures = 0;
                foreach (Model model in models)
                {
                    foreach (Material material in model.Materials)
                    {
                        if (material.TextureId == -1) continue;
                        for (int r = 0; r < model.Recolors.Count; r++)
                        {
                            model.GetPixels(material.TextureId, material.PaletteId, r);
                            textures++;
                        }
                    }
                }
                Console.WriteLine($"run {run + 1}: {models.Count} hunter models parse {parse:0} ms, {textures} textures decode"
                    + $" {sw.Elapsed.TotalMilliseconds:0} ms");
            }
        }
    }
}
