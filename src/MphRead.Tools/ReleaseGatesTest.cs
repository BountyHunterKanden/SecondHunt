using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRecomp.Arenas;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Import;
using MphRecomp.Mods;
using MphRecomp.Multiplayer;
using MphRecomp.PrimeMusic;

namespace MphRead
{
    // -releasegatestest: what the next release leaves out of a PUBLIC build (owner 2026-10-17:
    // Zero Suit Samus + Paralyzer, map work since 0.2.1, First Hunt arenas; Prime music public since 2026-10-29), each
    // checked public and dev. The map screen's own gate (CampaignMap: PRIME mode = PauseMap's in public) is App code, not
    // reachable here. Exit code = failures.
    public static class ReleaseGatesTest
    {
        public static int Run(string[] args)
        {
            int fails = 0;
            void Check(string what, bool got, bool want)
            {
                Console.WriteLine($"{(got == want ? "PASS" : "FAIL")} {what}: {got} (want {want})");
                if (got != want) fails++;
            }
            bool saved = RecompSettings.PublicBuild;
            try
            {
                foreach (bool pub in new[] { true, false })
                {
                    RecompSettings.PublicBuild = pub;
                    string b = pub ? "public" : "dev";

                    // #18 + #14: IMPORT GAMES' items
                    ImportItemInfo Item(string kind, string name) => new() { Id = kind + "/" + name, Kind = kind, Name = name };
                    Check($"{b}, import offers a Prime song", ImportGames.Offered(Item(TrackCatalog.Kind, "MP1PhendranaDrifts")), true);
                    Check($"{b}, import offers hd/MP3ZeroSuit", ImportGames.Offered(Item("hd", "MP3ZeroSuit")), !pub);
                    Check($"{b}, import offers bodies/MP3ZeroSuit", ImportGames.Offered(Item("bodies", "MP3ZeroSuit")), !pub);
                    Check($"{b}, import offers hd/MP1PowerSuit", ImportGames.Offered(Item("hd", "MP1PowerSuit")), true);
                    Check($"{b}, import offers Echoes arena music", ImportGames.Offered(Item("music", "EchoesArenaMusic")), true);
                    Check($"{b}, import offers hd/MP4Sylux", ImportGames.Offered(Item("hd", "MP4Sylux")), true);
                    Check($"{b}, import offers guns/MP4Sylux (Beyond Sylux's arm cannon)", ImportGames.Offered(Item("guns", "MP4Sylux")), true);
                    Check($"{b}, import offers guns/MP4DefaultSuit (the Prime arm cannons stay held)", ImportGames.Offered(Item("guns", "MP4DefaultSuit")), false);
                    Check($"{b}, import offers bodies/MP4SyluxLockjaw", ImportGames.Offered(Item("bodies", "MP4SyluxLockjaw")), true);
                    Check($"{b}, import offers hd/MP2PirateCommando (no player character)", ImportGames.Offered(Item("hd", "MP2PirateCommando")), !pub);
                    foreach (string pirate in new[] { "MP1SpacePirate", "MP2PirateTrooper", "MP2DarkPirateTrooper", "MP2DarkCommando" })
                        Check($"{b}, import offers hd/{pirate} (no player character)", ImportGames.Offered(Item("hd", pirate)), !pub);
                    Check($"{b}, import offers hd/SamusBall (Brawl Samus)", ImportGames.Offered(Item("hd", "SamusBall")), !pub);
                    Check($"{b}, import offers brawl/Paralyzer", ImportGames.Offered(Item("brawl", "Paralyzer")), !pub);
                    Check($"{b}, import offers bodies/MP1AlphaDrone", ImportGames.Offered(Item("bodies", "MP1AlphaDrone")), true);

                    // #14: Zero Suit's skins (picker, TRY CONTROLS, look-alikes) and a seat a dev joiner sends
                    Check($"{b}, Zero Suit has skins", Characters.Skins(Characters.ZeroSuit, null).Count > 0, !pub);
                    Check($"{b}, Dark Samus has skins", Characters.Skins(Characters.DarkSamus, null).Count > 0, true);
                    var s = new MatchSettings();
                    s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Skin = "MP3ZeroSuit", Character = Characters.ZeroSuit.Id });
                    s.Players.Add(new MatchPlayer { Hunter = Hunter.Weavel, Skin = "MP1PowerTrooper", Character = Characters.Trooper.Id });
                    Characters.ApplyExtraRules(s);
                    Check($"{b}, a Zero Suit seat stays Zero Suit", Characters.IsZeroSuit(s.Players[0]), !pub);
                    Check($"{b}, a Trooper seat stays Trooper", s.Players[1].Character == Characters.Trooper.Id, true);
                    Check($"{b}, Zero Suit seat's affinity is the Paralyzer", Characters.AffinityBeamFor(s, s.Players[0]) == BeamType.Paralyzer, !pub);
                    // Dark Samus's Hyper Beam (owner 2026-10-24: dev only, as her Hyper Ball)
                    var dark = new MatchPlayer { Hunter = Hunter.Samus, Character = Characters.DarkSamus.Id };
                    Check($"{b}, Dark Samus seat's affinity is the Hyper Beam", Characters.AffinityBeamFor(new MatchSettings(), dark) == BeamType.HyperBeam, !pub);
                    Check($"{b}, a served match's Dark Samus has no Hyper Beam", Characters.AffinityBeamFor(new MatchSettings { HyperBeam = false }, dark) == null, true);


                    // #18: Prime songs public since the 2026-10-29 rework
                    Check($"{b}, Prime songs on", MusicLibrary.On, true);
                    // spider ball + grapple: wave 2, off in public (owner 2026-11-07)
                    {
                        GameMode mode = GameState.Mode;
                        int room = EchoesArena.All[0].Id;
                        try
                        {
                            GameState.Mode = GameMode.Battle;
                            EchoesMovement.Extra(null!, room, Array.Empty<MphRead.Entities.EntityBase>());
                            Check($"{b}, spider ball on in an Echoes arena", MphRead.Entities.PlayerEntity.HostSpiderBall, !pub);
                            if (pub)
                            {
                                EchoesGrapple.SetRoomSpots(room, new[] { new GrappleSpot() });
                                EchoesGrapple.Extra(null!, room, Array.Empty<MphRead.Entities.EntityBase>());
                                Check($"{b}, grapple points in an Echoes arena", MphRead.Entities.PlayerEntity.HostGrapplePoints != null, false);
                            }
                        }
                        finally
                        {
                            EchoesGrapple.SetRoomSpots(room, null);
                            GameState.Mode = mode;
                        }
                    }

                    // #24 / C13: First Hunt's arenas out of the arena lists, every build
                    foreach (FirstHuntArena.Def d in FirstHuntArena.All.Where(d => d.Id is >= 1101 and <= 1103))
                    {
                        Check($"{b}, {d.InGameName} out of the arena lists", FirstHuntArena.IsDevRoom(d.Room), true);
                    }

                    // MODS: hidden in public for now (owner 2026-11-07 "remove for now"), so a mods folder runs nothing there
                    int mods = ModsRunning();
                    if (mods < 0) Console.WriteLine($"SKIP {b}, mods: no example mod here");
                    else Check($"{b}, an installed mod runs", mods > 0, !pub);

                    // #16 + #18: RECOMP SETTINGS' rows (a headless front end on the PC's ROM tree, scratch settings)
                    List<string>? labels = SettingLabels();
                    if (labels == null)
                    {
                        Console.WriteLine($"SKIP {b}, settings rows: no front end here");
                        continue;
                    }
                    // (the campaign's mini map: dev only, owner 2026-10-17/26; a match's radar comes with the radar rule)
                    Check($"{b}, settings has the hud row", labels.Contains("hud"), !pub);
                    // the campaign's radar on its own, every build (owner 2026-10-26)
                    Check($"{b}, settings has the campaign radar row", labels.Contains("campaign\nradar"), true);
                    Check($"{b}, settings has the radar pickups row", labels.Contains("radar\npickups"), true);
                    Check($"{b}, settings has the map mode row", labels.Contains("map\nmode"), !pub); // (PRIME map: map work, owner 2026-11-07)
                    // high frame rate: not in the next release (owner 2026-10-18; the frame rate row is PC only anyway)
                    Check($"{b}, settings has the smooth frames row", labels.Contains("smooth\nframes"), !pub);
                    Check($"{b}, settings has the frame rate row", labels.Contains("frame\nrate"), false);
                    // DEATH: RAGDOLL every build, First Hunt's collapse dev only (owner 2026-11-09 "ragdoll in next release
                    // without first hunt at all"); FIRST HUNT is never offered in public (RecompMenus.FirstHuntOffered)
                    Check($"{b}, settings has the death row", labels.Contains("death"), true);
                    Check($"{b}, settings has the vanilla orbs row (DEATH on RAGDOLL)", labels.Contains("vanilla\norbs"), true);
                    Check($"{b}, settings has no follow cam row (RAGDOLL's own, owner 2026-11-09)", labels.Contains("follow\ncam"), false);
                    MphRecomp.Anim.DeathCollapse.Set("firsthunt", Environment.CurrentDirectory);
                    Check($"{b}, DEATH = FIRST HUNT turns the collapse on", MphRecomp.Anim.DeathCollapse.Root != null, !pub);
                    Check($"{b}, DEATH = FIRST HUNT leaves the ragdoll off", MphRecomp.Anim.DeathCollapse.Ragdoll, false);
                    MphRecomp.Anim.DeathCollapse.Set("ragdoll", Environment.CurrentDirectory);
                    Check($"{b}, DEATH = RAGDOLL turns the ragdoll on", MphRecomp.Anim.DeathCollapse.Ragdoll, true);
                    Check($"{b}, DEATH = RAGDOLL leaves the collapse off", MphRecomp.Anim.DeathCollapse.Root != null, false);
                    MphRecomp.Anim.DeathCollapse.Set(null, Environment.CurrentDirectory);
                    Check($"{b}, settings has Prime song rows", labels.Count(l => l.Contains("song", StringComparison.OrdinalIgnoreCase)
                        || l.StartsWith("match\nmusic", StringComparison.Ordinal) || l.StartsWith("menu\nmusic", StringComparison.Ordinal)) > 0, true);
                    Console.WriteLine($"  {b} rows: " + String.Join(", ", labels.Select(l => l.Replace('\n', ' '))));
                }
            }
            finally
            {
                RecompSettings.PublicBuild = saved;
            }
            Console.WriteLine(fails == 0 ? "releasegatestest: all PASS" : $"releasegatestest: {fails} FAIL");
            return fails;
        }

        // Copies docs/mods/example_stat_tracker into a scratch data folder and attaches it as a game would. -1 = no example mod.
        static int ModsRunning()
        {
            string? src = null;
            for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null && src == null; d = d.Parent)
            {
                string c = Path.Combine(d.FullName, "docs", "mods", "example_stat_tracker");
                if (Directory.Exists(c)) src = c;
            }
            if (src == null) return -1;
            string dir = Path.Combine(Path.GetTempPath(), "releasegatestest_mods_" + Environment.ProcessId);
            string to = Path.Combine(dir, "mods", "example_stat_tracker");
            Directory.CreateDirectory(to);
            try
            {
                foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
                return LiveMods.Attach(dir, null);
            }
            finally
            {
                LiveMods.Detach();
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        static List<string>? SettingLabels()
        {
            string dir = Path.Combine(Path.GetTempPath(), "releasegatestest_" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                Paths.UpdatePaths(); // (paths.txt next to the tool, as Program does for its viewer)
                Paths.ChooseMphPath();
                // (DEATH on RAGDOLL, so its VANILLA ORBS and FOLLOW CAM rows show)
                File.WriteAllText(Path.Combine(dir, "recomp_settings.json"), "{\"DeathAnimation\":\"ragdoll\"}");
                FrontendSession session = FrontendSession.Load(Paths.FileSystem, settingsPath: Path.Combine(dir, "recomp_settings.json"));
                MethodInfo rows = typeof(RecompMenus).GetMethod("SettingRows", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var list = (System.Collections.IEnumerable)rows.Invoke(session.Recomp, null)!;
                var labels = new List<string>();
                foreach (object row in list) labels.Add((string)row.GetType().GetField("Label")!.GetValue(row)!);
                return labels;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TargetInvocationException or FileNotFoundException)
            {
                Console.WriteLine("  (front end: " + ex.GetBaseException().Message + ")");
                return null;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
