using System;
using System.Linq;
using System.Reflection;
using MphRecomp.Frontend;
using MphRecomp.Online;

namespace MphRead
{
    // -hubselftest (docs/GAMING_HUB.md): the server's copy of the rank rule (Core Online/ContentKeys.cs HubRules, the
    // server can't use Frontend) = the license's (HunterLicense), and every game mode and kill beam has a content key
    public static class HubSelfTest
    {
        public static void Run()
        {
            int fail = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
                fail += ok ? 0 : 1;
            }
            var table = (ValueTuple<int, int>[,])typeof(HunterLicense)
                .GetField("RankTable", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            bool same = table.GetLength(0) == HubRules.RankTable.GetLength(0) && table.GetLength(1) == HubRules.RankTable.GetLength(1);
            for (int i = 0; same && i < table.GetLength(0); i++)
            {
                for (int j = 0; j < table.GetLength(1); j++)
                {
                    same &= table[i, j] == HubRules.RankTable[i, j];
                }
            }
            Check(same, "rank table: HubRules = HunterLicense");
            Check(HubRules.RankThresholds.SequenceEqual(HunterLicense.RankThresholds)
                && HubRules.MaxPoints == HunterLicense.RankThresholds[4] - 1, "rank thresholds + 850 cap: HubRules = HunterLicense");
            var modes = Enum.GetValues<GameMode>().Where(m => m >= GameMode.Battle)
                .Select(m => ContentKeys.ModeKey(HunterLicense.ModeIndex(m))).ToList();
            Check(modes.All(k => ContentKeys.Find(ContentKeys.Mode, k) != null),
                "every multiplayer mode has a known mode key: " + string.Join(" ", modes.Distinct()));
            var beams = Enum.GetValues<BeamType>().Where(b => b is not (BeamType.Platform or BeamType.Enemy) && (int)b >= 0 && (int)b < 16)
                .Select(b => ContentKeys.WeaponKey((int)b)).ToList();
            Check(beams.All(k => k != null && ContentKeys.Find(ContentKeys.Weapon, k) != null),
                "every beam has a known weapon key: " + string.Join(" ", beams));
            Check(Enum.GetValues<Hunter>().Where(h => h != Hunter.Random)
                .All(h => ContentKeys.Find(ContentKeys.Hunter, ContentKeys.HunterKey(null, h.ToString())) != null), "every hunter has a known key");
            var picks = MphRecomp.Multiplayer.Characters.All.Where(c => !c.HasOriginal)
                .Select(c => ContentKeys.HunterKey(c.Id, c.Base.ToString())).ToList();
            Check(picks.All(k => ContentKeys.Find(ContentKeys.Hunter, k) != null),
                "every picked character (TROOPER, DARK SAMUS...) has a known key: " + string.Join(" ", picks));
            Console.WriteLine(fail == 0 ? "hubselftest: PASS" : $"hubselftest: {fail} FAILED");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }
    }
}
