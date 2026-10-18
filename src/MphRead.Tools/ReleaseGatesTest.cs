using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRecomp.Arenas;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Import;
using MphRecomp.Multiplayer;
using MphRecomp.PrimeMusic;

namespace MphRead
{
    // -releasegatestest: what the next release leaves out of a PUBLIC build (owner 2026-10-17:
    // Zero Suit Samus + Paralyzer, map work since 0.2.1, Prime music import, First Hunt arenas), each
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
                    Check($"{b}, import offers a Prime song", ImportGames.Offered(Item(TrackCatalog.Kind, "MP1PhendranaDrifts")), !pub);
                    Check($"{b}, import offers hd/MP3ZeroSuit", ImportGames.Offered(Item("hd", "MP3ZeroSuit")), !pub);
                    Check($"{b}, import offers bodies/MP3ZeroSuit", ImportGames.Offered(Item("bodies", "MP3ZeroSuit")), !pub);
                    Check($"{b}, import offers hd/MP1PowerSuit", ImportGames.Offered(Item("hd", "MP1PowerSuit")), true);
                    Check($"{b}, import offers Echoes arena music", ImportGames.Offered(Item("music", "EchoesArenaMusic")), true);
                    Check($"{b}, import offers hd/MP4Sylux", ImportGames.Offered(Item("hd", "MP4Sylux")), true);
                    Check($"{b}, import offers guns/MP4Sylux (Beyond Sylux's arm cannon)", ImportGames.Offered(Item("guns", "MP4Sylux")), true);
                    Check($"{b}, import offers guns/MP4DefaultSuit (the Prime arm cannons stay held)", ImportGames.Offered(Item("guns", "MP4DefaultSuit")), false);
                    Check($"{b}, import offers bodies/MP4SyluxLockjaw", ImportGames.Offered(Item("bodies", "MP4SyluxLockjaw")), true);

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

                    // #18: no Prime songs to play or pick
                    Check($"{b}, Prime songs on", MusicLibrary.On, !pub);

                    // #24 / C13: First Hunt's arenas out of the arena lists, every build
                    foreach (FirstHuntArena.Def d in FirstHuntArena.All.Where(d => d.Id is >= 1101 and <= 1103))
                    {
                        Check($"{b}, {d.InGameName} out of the arena lists", FirstHuntArena.IsDevRoom(d.Room), true);
                    }

                    // #16 + #18: RECOMP SETTINGS' rows (a headless front end on the PC's ROM tree, scratch settings)
                    List<string>? labels = SettingLabels();
                    if (labels == null)
                    {
                        Console.WriteLine($"SKIP {b}, settings rows: no front end here");
                        continue;
                    }
                    // (the campaign's radar + mini map: dev only; a match's radar comes with the radar rule, owner 2026-10-17)
                    Check($"{b}, settings has the hud row", labels.Contains("hud"), !pub);
                    Check($"{b}, settings has the map mode row", labels.Contains("map\nmode"), true);
                    // high frame rate: not in the next release (owner 2026-10-18; the frame rate row is PC only anyway)
                    Check($"{b}, settings has the smooth frames row", labels.Contains("smooth\nframes"), !pub);
                    Check($"{b}, settings has the frame rate row", labels.Contains("frame\nrate"), false);
                    Check($"{b}, settings has Prime song rows", labels.Count(l => l.Contains("song", StringComparison.OrdinalIgnoreCase)
                        || l.StartsWith("match\nmusic", StringComparison.Ordinal) || l.StartsWith("menu\nmusic", StringComparison.Ordinal)) > 0, !pub);
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

        static List<string>? SettingLabels()
        {
            string dir = Path.Combine(Path.GetTempPath(), "releasegatestest_" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                Paths.UpdatePaths(); // (paths.txt next to the tool, as Program does for its viewer)
                Paths.ChooseMphPath();
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
