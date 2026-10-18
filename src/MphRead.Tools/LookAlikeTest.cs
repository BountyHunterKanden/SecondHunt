using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // No-import look-alikes (MphRecomp.Core LookAlikes.cs, docs/CHARACTERS_AND_SKINS.md D5), headless:
    //   -lookalikes [out=<dir>]
    //   -lookalikes replay=<file>   (pictures: the steps where each drone seat's laser and burst start)
    // registers the looks, loads each model they go on and checks every look's palette set is there at the index the
    // seats use (the same index on all of a hunter's body/alt models); with out=, one texture sheet per model: a row per
    // recolor (MPH's own recolor 0, then each look), every material's texture at 2x.
    internal static class LookAlikeTest
    {
        public static void Run(string[] args)
        {
            string? outDir = null, replayPath = null;
            bool results = false;
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("out=")) outDir = arg[4..];
                else if (arg.StartsWith("replay=")) replayPath = arg[7..];
                else if (arg == "results") results = true;
                else throw new ArgumentException($"unknown option {arg}");
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (replayPath != null)
            {
                DroneMoments(replayPath);
                return;
            }
            if (results)
            {
                ResultsShots(outDir ?? ".");
                return;
            }
            LookAlikes.Register();
            if (outDir != null) Directory.CreateDirectory(outDir);
            var groups = new (string[] Models, LookAlikes.Look[] Looks)[]
            {
                (new[] { "Weavel_lod0", "Weavel_lod1", "WeavelAlt_lod0", "WeavelAlt_Turret_lod0", "WeavelGun" },
                    new[] { LookAlikes.Look.PowerTrooper, LookAlikes.Look.IceTrooper, LookAlikes.Look.WaveTrooper, LookAlikes.Look.PlasmaTrooper }),
                (new[] { "PsychoBit" },
                    new[] { LookAlikes.Look.PowerTrooper, LookAlikes.Look.IceTrooper, LookAlikes.Look.WaveTrooper, LookAlikes.Look.PlasmaTrooper }),
                (new[] { "Samus_lod0", "Samus_lod1", "SamusAlt_lod0", "SamusGun" }, new[] { LookAlikes.Look.DarkSamus }),
            };
            int fails = 0;
            foreach (var (models, looks) in groups)
            {
                var index = new Dictionary<(LookAlikes.Look, LookAlikes.Variant), int>();
                foreach (string name in models)
                {
                    Model model = Read.GetModelInstance(name).Model;
                    if (name == "Samus_lod0") Console.WriteLine("  Samus_lod0 nodes: " + String.Join(" ", model.Nodes.Select(x => x.Name)));
                    foreach (LookAlikes.Look look in looks)
                    foreach (LookAlikes.Variant variant in Enum.GetValues<LookAlikes.Variant>())
                    {
                        // (the same index on every model of the group: one per look and variant, team and duplicates included)
                        int rc = LookAlikes.RecolorFor(look, variant, name);
                        string want = variant == LookAlikes.Variant.First ? $"look_{look}" : $"look_{look}_{variant}";
                        bool ok = rc >= 0 && rc < model.Recolors.Count && model.Recolors[rc].Name == want;
                        if (index.TryGetValue((look, variant), out int first) && first != rc) ok = false;
                        index.TryAdd((look, variant), rc);
                        if (!ok) fails++;
                        Console.WriteLine($"  {name}: {look} {variant} -> recolor {rc} of {model.Recolors.Count} {(ok ? "OK" : "FAIL")}");
                    }
                    if (outDir != null) Sheet(model, name, looks, Path.Combine(outDir, $"sheet_{name}.png"));
                }
            }
            // the hunters' 5th-8th colours (HunterColours): on every hunter model, the tail bomb, the turret and the Lockjaw
            // trail, at one index per hunter for its body / lod1 / alt (PlayerEntity.HostLookRecolor is one number)
            HunterColours.Install();
            foreach (var (hunter, names) in Metadata.HunterModels)
            {
                if (hunter == Hunter.Guardian) continue;
                var models = names.ToList();
                if (hunter == Hunter.Kanden) models.Add("KandenAlt_TailBomb");
                if (hunter == Hunter.Weavel) models.Add("WeavelAlt_Turret_lod0");
                if (hunter == Hunter.Sylux) models.Add(HunterColours.TrailModel);
                for (int k = 0; k < HunterColours.Copies; k++)
                {
                    int body = -1;
                    foreach (string name in models)
                    {
                        Model model = Read.GetModelInstance(name).Model;
                        int rc = HunterColours.RecolorFor(name, k);
                        string want = name == HunterColours.TrailModel ? $"hc_trail_{k}" : $"hc_{k}";
                        bool ok = rc >= 0 && rc < model.Recolors.Count && model.Recolors[rc].Name == want;
                        if (name.EndsWith("_lod0") || name.EndsWith("_lod1"))
                        {
                            if (body >= 0 && rc != body) ok = false;
                            body = rc;
                        }
                        if (!ok) fails++;
                        Console.WriteLine($"  {name}: copy {k + 5} -> recolor {rc} of {model.Recolors.Count} {(ok ? "OK" : "FAIL")}");
                    }
                }
            }
            fails += RecountCheck();
            Console.WriteLine(fails == 0 ? "LOOKALIKES PASS" : $"LOOKALIKES FAIL ({fails})");
        }

        // same-hunter copies count per character + skin (MatchSettings.RecolorPerSkin): each seat's MPH
        // recolor and drawn variant, new rule and the old one (replays before format 43), from the seats' strings alone
        static int RecountCheck()
        {
            static MatchPlayer P(Hunter h, string? character, string? skin) => new() { Hunter = h, Character = character, Skin = skin };
            static List<MatchPlayer> Seats() => new()
            {
                P(Hunter.Samus, "darksamus", "MP2DarkSamus"), P(Hunter.Samus, "darksamus", "MP3DarkSamus"),
                P(Hunter.Samus, "darksamus", "MP2DarkSamus"), P(Hunter.Samus, "darksamus", "MP3DarkSamus"),
                P(Hunter.Samus, null, null), P(Hunter.Samus, null, "MP1VariaSuit"), P(Hunter.Samus, "zerosuit", "MP3ZeroSuit"),
                P(Hunter.Samus, null, "original"),
            };
            static List<MatchPlayer> Seats2() => new()
            {
                P(Hunter.Sylux, null, null), P(Hunter.Sylux, null, "MP4Sylux"), P(Hunter.Sylux, null, "mp4sylux"),
                P(Hunter.Weavel, "trooper", null), P(Hunter.Weavel, "trooper", "MP1PowerTrooper"), P(Hunter.Weavel, "trooper", "MP1IceTrooper"),
                P(Hunter.Weavel, null, null), P(Hunter.Weavel, "trooper", "MP1IceTrooper"),
            };
            int fails = 0;
            void Check(string name, List<MatchPlayer> seats, bool? perSkin, int[] want)
            {
                var s = new MatchSettings { RecolorPerSkin = perSkin };
                s.Players.AddRange(seats);
                s.AssignRecolors();
                int[] got = s.Players.Select(p => p.Recolor).ToArray();
                int[] slots = Enumerable.Range(0, seats.Count).Select(s.RecolorSlot).ToArray();
                int[] variants = Enumerable.Range(0, seats.Count).Select(i => (int)LookAlikes.VariantFor(s, i)).ToArray();
                bool ok = got.SequenceEqual(want) && slots.SequenceEqual(want) && variants.SequenceEqual(want);
                if (!ok) fails++;
                Console.WriteLine($"  recount {name}: recolors {String.Join(",", got)} slots {String.Join(",", slots)} variants "
                    + $"{String.Join(",", variants)} (want {String.Join(",", want)}) {(ok ? "OK" : "FAIL")}");
            }
            // new rule: Echoes + Corruption Dark Samus both default, the 2nd of each its 1st recolour; Samus DS model / Varia /
            // Zero Suit apart ("original" = the DS model); Prime 4 Sylux apart from his DS model (skin case doesn't matter);
            // troopers per skin as before (no skin = Power), Weavel apart from them
            Check("per skin, Samus", Seats(), null, new[] { 0, 0, 1, 1, 0, 0, 0, 1 });
            Check("per skin, Sylux/Weavel", Seats2(), null, new[] { 0, 0, 1, 0, 1, 0, 0, 1 });
            // the old rule (replays before format 43): Dark Samus one look, every other Samus seat Samus
            Check("old, Samus", Seats(), false, new[] { 0, 1, 2, 3, 0, 1, 2, 3 });
            Check("old, Sylux/Weavel", Seats2(), false, new[] { 0, 1, 2, 0, 1, 0, 0, 1 });
            return fails;
        }

        // D7 picture: the RESULTS page of a Battle with a TROOPER bot and a DARK SAMUS bot, without (the ROM's bot names, as
        // before) and with their character names
        static void ResultsShots(string outDir)
        {
            Directory.CreateDirectory(outDir);
            foreach (bool names in new[] { false, true })
            {
                MphRecomp.Frontend.MatchResults.Player Row(int seat, Hunter hunter, string nick, bool bot, int place, int points, string? character)
                    => new(seat, hunter, nick, bot, 0, place, points, 0, points, 3 - place, 0, 400 - 80 * place, 1, 0, 0, 0, 0, 0, 60 - 10 * place, 0,
                        names ? character : null);
                MphRecomp.Frontend.MatchResults.Last = new MphRecomp.Frontend.MatchResults
                {
                    Mode = GameMode.Battle, LocalSeat = 0,
                    Rows = new[]
                    {
                        Row(1, Hunter.Weavel, "TROOPER", true, 0, 7, "TROOPER"),
                        Row(0, Hunter.Samus, "HUNTER", false, 1, 5, null),
                        Row(2, Hunter.Samus, "DARK SAMUS", true, 2, 4, "DARK SAMUS"),
                        Row(3, Hunter.Kanden, "KANBOT", true, 3, 2, null),
                    },
                };
                MphRecomp.Frontend.RecompMenus.OnlineNoticeSkipped = true;
                var menus = MphRecomp.Frontend.FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(outDir, "recomp_settings.json"));
                menus.Start(MphRecomp.Frontend.MultiplayerMenu.ResultsPage);
                for (int f = 0; f < 120; f++) menus.Tick();
                menus.Build(1920, 1080);
                using Image<Rgba32> img = FrontendTool.RasterCanvas(menus.DrawList, 1920, 1080);
                string path = Path.Combine(outDir, names ? "results_names.png" : "results_before.png");
                img.SaveAsPng(path);
                Console.WriteLine($"results (page {menus.Menu.Page?.Index}) -> {path}");
            }
        }

        static void DroneMoments(string path)
        {
            ReplayFile file = ReplayFile.Load(path);
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start();
            int step = 0;
            var lastLaser = new int[16];
            var lastState = new int[16];
            Array.Fill(lastLaser, -1);
            while (!host.Ended && player.TryNext(out CampaignInput input))
            {
                host.Step(input);
                step++;
                foreach (PlayerEntity p in PlayerEntity.Players)
                {
                    if (p == null || !p.HostScoutDrone || p.SlotIndex < 0 || p.SlotIndex >= 16) continue;
                    int i = p.SlotIndex, lf = p.DroneLaserFrame, st = p.DroneFireState;
                    if (lf == 0 && lastLaser[i] != 0) Console.WriteLine($"  step {step}: seat {i} laser starts (beam on at step {step + 57})");
                    if (st == 1 && lastState[i] != 1) Console.WriteLine($"  step {step}: seat {i} burst unfold starts");
                    lastLaser[i] = lf;
                    lastState[i] = st;
                }
            }
        }

        static void Sheet(Model model, string name, LookAlikes.Look[] looks, string png)
        {
            var mats = model.Materials.Where(m => m.TextureId >= 0).Select(m => (m.TextureId, m.PaletteId)).Distinct().ToList();
            // MPH's own six (pal_01-04, the two team palettes), then each look in each variant (same order)
            var recolors = Enumerable.Range(0, Math.Min(6, model.Recolors.Count)).ToList();
            recolors.AddRange(looks.SelectMany(l => Enum.GetValues<LookAlikes.Variant>().Select(v => LookAlikes.RecolorFor(l, v, name))));
            int scale = 2;
            int rowH = mats.Max(m => model.Recolors[0].Textures[m.TextureId].Height) * scale + 4;
            int width = mats.Sum(m => model.Recolors[0].Textures[m.TextureId].Width * scale + 4);
            using var img = new Image<Rgba32>(Math.Max(1, width), rowH * recolors.Count, new Rgba32(40, 44, 52));
            for (int r = 0; r < recolors.Count; r++)
            {
                int x0 = 0;
                foreach ((int tex, int pal) in mats)
                {
                    Texture t = model.Recolors[0].Textures[tex];
                    IReadOnlyList<ColorRgba> px = model.GetPixels(tex, pal, recolors[r]);
                    for (int i = 0; i < px.Count; i++)
                    {
                        ColorRgba c = px[i];
                        var o = new Rgba32(c.Red, c.Green, c.Blue, 255);
                        for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            img[x0 + i % t.Width * scale + dx, r * rowH + i / t.Width * scale + dy] = o;
                        }
                    }
                    x0 += t.Width * scale + 4;
                }
            }
            img.SaveAsPng(png);
        }
    }
}
