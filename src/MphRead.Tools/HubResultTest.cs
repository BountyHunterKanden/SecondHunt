using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Online;

namespace MphRead
{
    // -hubresulttest: the Hunter Hub stats a served match host sends (owner 2026-11-11: the licence's FAVOURITES said SAMUS
    // for Dark Samus, and ALT FORM for every hunter's alt form). A headless match like the match host's (no shell sets
    // PlayerEntity.HostCharacterName): a Dark Samus seat and a Trooper bot, alt-form kills given to both, then
    // MatchResults.Capture with the match's settings as ControlMode.Result takes it
    internal static class HubResultTest
    {
        public static void Run()
        {
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find("Combat Hall") ?? throw new InvalidOperationException("no Combat Hall");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = 60 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = "darksamus", Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Weavel, Character = "trooper", Bot = true, BotLevel = 0 });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 0 });
            CampaignHost host = CampaignHost.StartMatch(settings);
            for (int i = 0; i < 120; i++) host.Step(default);
            for (int s = 0; s < 3; s++) GameState.AltDamageCount[s] = s + 2;
            var errors = new List<string>();
            void Expect(string what, string got, string want)
            {
                Console.WriteLine($"  {what}: {got}");
                if (got != want) errors.Add($"{what}: {got}, want {want}");
            }
            foreach ((MatchSettings? given, string label) in new[] { ((MatchSettings?)settings, "with settings"), (null, "without") })
            {
                MatchResults r = MatchResults.Capture(0, settings.Arena, given);
                foreach (MatchResults.Player p in r.Rows)
                {
                    ResultStats stats = MatchResults.HubStats(p);
                    string weapons = string.Join(",", stats.Weapons);
                    if (given == null)
                    {
                        Console.WriteLine($"  {label} seat {p.Seat}: {stats.Hunter} {weapons}"); // (as before: base hunters)
                        continue;
                    }
                    (string hunter, string alt) = p.Seat switch { 0 => ("dark_samus", "morph_ball"), 1 => ("trooper", "scout_drone"), _ => ("kanden", "stinglarva") };
                    Expect($"seat {p.Seat} hunter", stats.Hunter, hunter);
                    Expect($"seat {p.Seat} alt-form kills", stats.Weapons.GetValueOrDefault(alt).ToString(), (p.Seat + 2).ToString());
                    if (stats.Weapons.ContainsKey(ContentKeys.AltForm)) errors.Add($"seat {p.Seat}: still counts {ContentKeys.AltForm}");
                }
            }
            foreach (string error in errors) Console.WriteLine($"FAIL {error}");
            Console.WriteLine(errors.Count == 0 ? "hubresulttest PASS" : $"hubresulttest FAIL ({errors.Count})");
            Environment.ExitCode = errors.Count == 0 ? 0 : 1;
        }
    }
}
