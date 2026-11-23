using System;
using MphRecomp.Frontend;

namespace MphRead
{
    // -restorelicensetest: after RESTORE (docs/PROFILE_SYNC.md Phase 0) the hunter's online card can come onto this device's
    // license (HunterLicense.TakeOnlineCard): a blank device takes it all; nothing goes down; the asking pass (apply: false)
    // changes nothing; a license already ahead says so; favorites come only onto a license with no matches.
    // No ROM needed.
    internal static class RestoreLicenseTest
    {
        public static void Run()
        {
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            static HunterLicense Card() => new()
            {
                RankPoints = 400, RankLevel = 3, WifiWins = 30, OnlineMatches = 90, LocalMatches = 5, BipedKills = 800,
                HeadshotKills = 40, WifiSeconds = 7200, TotalSeconds = 9000, WifiBestKillStreak = 12, BeatGame = true,
                FavoriteHunter = 2, FavoriteWeapon = 4, FavoriteMode = 1, LuckyArena = HunterLicense.FirstArenaRoom + 3,
            };

            var blank = new HunterLicense();
            Check(blank.TakeOnlineCard(Card(), apply: false), "a blank device's license is behind the card");
            Check(blank.RankPoints == 0 && blank.OnlineMatches == 0 && !blank.BeatGame, "the asking pass changes nothing");
            Check(blank.TakeOnlineCard(Card()), "a blank device takes the card");
            Check(blank.RankPoints == 400 && blank.RankLevel == 3 && blank.OnlineMatches == 90 && blank.BipedKills == 800
                && blank.WifiSeconds == 7200 && blank.WifiBestKillStreak == 12 && blank.BeatGame, "its numbers, rank and flags");
            Check(blank.FavoriteHunter == 2 && blank.FavoriteWeapon == 4 && blank.FavoriteMode == 1
                && blank.LuckyArena == HunterLicense.FirstArenaRoom + 3, "a license with no matches takes the favorites");
            Check(!blank.TakeOnlineCard(Card()), "taken once, it's no longer behind");

            var mixed = new HunterLicense
            {
                RankPoints = 500, RankLevel = 3, OnlineMatches = 10, LocalMatches = 60, BipedKills = 100, HeadshotKills = 90,
                Completed = true, FavoriteHunter = 5, FavoriteWeapon = 1, FavoriteMode = 4,
            };
            Check(mixed.TakeOnlineCard(Card()), "a license ahead in some numbers and behind in others takes the card");
            Check(mixed.RankPoints == 500 && mixed.LocalMatches == 60 && mixed.HeadshotKills == 90 && mixed.Completed,
                "nothing goes down");
            Check(mixed.OnlineMatches == 90 && mixed.BipedKills == 800 && mixed.BeatGame, "the higher numbers come in");
            Check(mixed.FavoriteHunter == 5 && mixed.FavoriteWeapon == 1 && mixed.FavoriteMode == 4,
                "a license with matches keeps its favorites");

            var ahead = new HunterLicense
            {
                RankPoints = 850, RankLevel = 4, WifiWins = 99, WirelessWins = 1, OnlineMatches = 200, LocalMatches = 50,
                BipedKills = 900, AltFormKills = 1, HeadshotKills = 50, Disconnects = 1, WirelessSeconds = 1, WifiSeconds = 8000,
                TotalSeconds = 9999, WifiBestWinStreak = 1, WifiBestKillStreak = 20, WirelessBestWinStreak = 1,
                WirelessBestKillStreak = 1, BeatGame = true, Completed = true,
            };
            Check(!ahead.TakeOnlineCard(Card(), apply: false), "a license ahead everywhere says it's not behind");

            var huge = new HunterLicense();
            huge.TakeOnlineCard(new HunterLicense { RankPoints = 99999, WifiSeconds = double.NaN, OnlineMatches = 1 });
            Check(huge.RankPoints == HunterLicense.RankThresholds[^1] - 1 && huge.RankLevel == 4 && huge.WifiSeconds == 0,
                "a card out of range is clamped (points under the top, a NaN clock skipped)");

            Console.WriteLine(fails == 0 ? "restorelicensetest: all passed" : $"restorelicensetest: {fails} FAILED");
            Environment.ExitCode = fails == 0 ? 0 : 1;
        }
    }
}
