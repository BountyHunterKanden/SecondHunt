using System;
using System.Globalization;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;

// -culturetest: the match setup line round-trips under locales whose numbers differ from invariant (fi-FI writes -1 with
// U+2212 and decimals with a comma; a Finnish phone crashed match start on "−1", 2026-10-16). The tools themselves run
// invariant (Utility/Console.cs), so this sets each culture on purpose.
namespace MphRead
{
    public static class CultureTest
    {
        public static void Run()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            int failed = 0;
            foreach (string name in new[] { "fi-FI", "sv-SE", "nb-NO", "de-DE", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                var s = new MatchSettings { Mode = GameMode.Survival, Arena = "MP3 PROVING GROUND", LocalSeat = 0,
                    TimeLimitSeconds = 300.5f, TimeGoalSeconds = 60f, PointGoal = 7, DamageLevel = 2 };
                s.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Recolor = 0, Team = -1, Nickname = "HUNTER" });
                s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Recolor = 1, Team = -1, Bot = true, BotLevel = 2 });
                string line = MatchSetupText.Write(s);
                string? problem = null;
                try
                {
                    MatchSettings back = MatchSetupText.Read(line);
                    MatchSetupText.LaunchArgs(line);
                    if (back.Players.Count != 2 || back.Players[0].Team != -1 || back.Players[1].Team != -1
                        || back.Players[1].BotLevel != 2 || back.TimeLimitSeconds != 300.5f || back.PointGoal != 7)
                    {
                        problem = "values changed";
                    }
                }
                catch (Exception ex)
                {
                    problem = ex.GetType().Name + ": " + ex.Message;
                }
                Console.WriteLine($"{(problem == null ? "PASS" : "FAIL")} {name}: {line}{(problem == null ? "" : " -- " + problem)}");
                if (problem != null) failed++;
            }
            CultureInfo.CurrentCulture = before;
            Console.WriteLine(failed == 0 ? "culturetest: ALL PASS" : $"culturetest: {failed} FAILED");
        }
    }
}
