using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // -licensequit: the Hunter License quit penalty (HunterLicense.ArmQuitPenalty, MultiplayerMenu.ArmMatch / CountMatch /
    // ChargeQuitPenalty; owner 2026-11-07: vanilla's rank loss for quitting, without its bot-match cheese). No ROM needed
    internal static class LicenseQuitTest
    {
        public static void Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"licensequit_{Environment.ProcessId}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            try
            {
                new HunterLicense { Name = "TEST", RankPoints = 150, RankLevel = 2, Disconnects = 2 }.Save(dir);
                var mp = new MultiplayerMenu { ProfileDir = dir };
                mp.Log = m => Console.WriteLine("  " + m);
                MethodInfo arm = typeof(MultiplayerMenu).GetMethod("ArmMatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo count = typeof(MultiplayerMenu).GetMethod("CountMatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Arm(MatchSettings s, bool rejoin = false, bool wifi = true) => arm.Invoke(mp, new object[] { s, wifi, rejoin });
                HunterLicense Disk() => HunterLicense.Load(dir);
                // seat 0 = this device; humans at levels 3 (master) and 2 (elite), a bot
                MatchSettings Humans() => Settings(new MatchPlayer { RankLevel = 2 }, new MatchPlayer { Remote = true, RankLevel = 3 },
                    new MatchPlayer { Bot = true }, new MatchPlayer { Remote = true, RankLevel = 2, Team = 0 });
                MatchSettings Bots() => Settings(new MatchPlayer { RankLevel = 2 }, new MatchPlayer { Bot = true }, new MatchPlayer { Bot = true });

                // 1. a match start marks the humans (not the bot) and saves at once (closing the app keeps it)
                Arm(Humans());
                Check(Disk().PendingLosses.SequenceEqual(new[] { 3, 2 }), $"armed and saved: [{String.Join(",", Disk().PendingLosses)}]");
                Check(Disk().RankPoints == 150, "nothing charged at the start");

                // 2. QUIT (no RESULTS), then a bot match before going online: charged at its start, not undone
                Arm(Bots());
                // elite (2) vs master (3) loses 3, vs elite (2) loses 4
                Check(Disk().RankPoints == 143, $"quit then a bot match: 150 -> {Disk().RankPoints} (want 143)");
                Check(Disk().PendingLosses.Length == 0, "the bot match marks no one");
                Check(Disk().Disconnects == 2, "Disconnects untouched");
                count.Invoke(mp, null); // (no RESULTS of it: nothing)

                // 3. a match that reaches RESULTS (also how a lost connection ends) clears its marks: nothing charged later
                Arm(Humans());
                MatchResults.Last = Results(place: 1);
                count.Invoke(mp, null);
                int afterCount = Disk().RankPoints;
                Check(Disk().PendingLosses.Length == 0, $"RESULTS clear the marks (points {afterCount} from the result itself)");
                Arm(Bots());
                Check(Disk().RankPoints == afterCount, "no charge after a match with RESULTS");

                // 4. a rejoin of the held match: re-armed, not charged
                Arm(Humans());
                int before = Disk().RankPoints;
                Arm(Humans(), rejoin: true);
                Check(Disk().RankPoints == before && Disk().PendingLosses.Length == 2, "a rejoin keeps the marks, charges nothing");

                // 5. the online login / a declined rejoin: MultiplayerMenu.ChargeQuitPenalty, and only once
                int lost = mp.ChargeQuitPenalty("test login");
                Check(lost == 7 && Disk().RankPoints == before - 7, $"login charge: lost {lost}");
                Check(mp.ChargeQuitPenalty("test login again") == 0, "charged once");

                // 6. the level follows the points down, and points stop at 0
                new HunterLicense { Name = "TEST", RankPoints = 142, RankLevel = 2 }.Save(dir);
                mp.ProfileDir = dir;
                Arm(Humans());
                mp.ChargeQuitPenalty("test level");
                Check(Disk().RankPoints == 135 && Disk().RankLevel == 1, $"142 -> {Disk().RankPoints}, level {Disk().RankLevel} (want 135, 1)");
                new HunterLicense { Name = "TEST", RankPoints = 3, RankLevel = 0 }.Save(dir);
                mp.ProfileDir = dir;
                Arm(Settings(new MatchPlayer { RankLevel = 0 }, new MatchPlayer { Remote = true, RankLevel = 0 },
                    new MatchPlayer { Remote = true, RankLevel = 1 }, new MatchPlayer { Remote = true, RankLevel = 2 }, new MatchPlayer { Remote = true, RankLevel = 4 }));
                mp.ChargeQuitPenalty("test floor");
                Check(Disk().RankPoints == 0 && Disk().RankLevel == 0, $"3 -> {Disk().RankPoints} (bounty losses 1+1+1+0: floor 0)");

                // 7. internet matches only (owner 2026-11-08): a LAN match with humans marks no one, but still charges an
                // internet match left before it (no LAN-match cheese)
                new HunterLicense { Name = "TEST", RankPoints = 150, RankLevel = 2 }.Save(dir);
                mp.ProfileDir = dir;
                Arm(Humans(), wifi: false);
                Check(Disk().PendingLosses.Length == 0 && Disk().RankPoints == 150, "a LAN match marks no one");
                Arm(Bots());
                Check(Disk().RankPoints == 150, "a LAN match left: nothing charged");
                Arm(Humans());
                Arm(Humans(), wifi: false);
                Check(Disk().RankPoints == 143 && Disk().PendingLosses.Length == 0, $"an internet match left, then LAN: 150 -> {Disk().RankPoints} (want 143)");

                // 8. an old license file (no marks) loads with none; a bad one is cleaned
                File.WriteAllText(Path.Combine(dir, HunterLicense.FileName), "{\"RankPoints\":50,\"RankLevel\":1,\"PendingLosses\":[1,9,-1,3]}");
                Check(Disk().PendingLosses.SequenceEqual(new[] { 1, 3 }), $"bad marks cleaned: [{String.Join(",", Disk().PendingLosses)}]");
            }
            finally
            {
                MatchResults.Last = null;
                Directory.Delete(dir, recursive: true);
            }
            Console.WriteLine(fails == 0 ? "licensequit: all passed" : $"licensequit: {fails} FAILED");
            Environment.ExitCode = fails == 0 ? 0 : 1;
        }

        private static MatchSettings Settings(params MatchPlayer[] players)
        {
            var s = new MatchSettings { Mode = GameMode.Battle, LocalSeat = 0 };
            s.Players.AddRange(players);
            return s;
        }

        private static MatchResults Results(int place)
        {
            static MatchResults.Player Row(int seat, bool bot, int place) => new(seat, Hunter.Samus, $"P{seat}", bot, -1, place,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, MatchResults.None);
            return new MatchResults
            {
                Mode = GameMode.Battle, LocalSeat = 0,
                Rows = new[] { Row(0, false, place), Row(1, false, 0), Row(2, true, 2), Row(3, false, 3) },
            };
        }
    }
}
