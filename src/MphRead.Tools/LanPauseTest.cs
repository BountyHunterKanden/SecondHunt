using System;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -lanpausetest (owner 2026-10-11: "in lan if the host pauses then the guest is kicked to results"): the in-match pause
    // menu is its own FrontendSession, and its MultiplayerMenu used to end LanSession.Current on entering the pause page
    // (BYE to the joiner). PAUSE: opening that pause menu keeps the match's session. MENUS: the menus themselves still end
    // it on a page that isn't the LAN's (as before).
    internal static class LanPauseTest
    {
        public static void Run()
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find("Combat Hall") ?? throw new InvalidOperationException("no Combat Hall");
            MatchSettings template = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 1, 1);
            int fails = 0;

            LanSession net = LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            LanSession.Current = net;
            FrontendSession pause = FrontendSession.Load(Paths.FileSystem);
            Console.WriteLine($"multiplayer menu bound: {pause.Multiplayer.Bound}");
            pause.OpenPause();
            pause.Menu.Enter(FrontendSession.OptionsPage); // (RECOMP SETTINGS and the like: any page under the pause menu)
            bool kept = ReferenceEquals(LanSession.Current, net);
            Console.WriteLine($"PAUSE {(kept ? "PASS" : "FAIL")}: the match's LAN session {(kept ? "kept" : "ended")}");
            if (!kept) fails++;
            net.Dispose();

            net = LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            LanSession.Current = net;
            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            menus.Menu.Enter(FrontendSession.OptionsPage);
            bool ended = LanSession.Current == null;
            Console.WriteLine($"MENUS {(ended ? "PASS" : "FAIL")}: the menus {(ended ? "ended" : "kept")} the LAN session off its pages");
            if (!ended) fails++;
            net.Dispose();

            // the check itself: without the guard (OverGame unset) the pause menu ends the session, as on the devices before
            net = LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            LanSession.Current = net;
            FrontendSession unguarded = FrontendSession.Load(Paths.FileSystem);
            unguarded.Multiplayer.OverGame = null;
            unguarded.OpenPause();
            bool bug = LanSession.Current == null;
            Console.WriteLine($"BEFORE {(bug ? "PASS" : "FAIL")}: without the guard the pause menu {(bug ? "ends" : "keeps")} it (the reported bug)");
            if (!bug) fails++;
            net.Dispose();
            LanSession.Current = null;

            Console.WriteLine(fails == 0 ? "LANPAUSE PASS" : $"LANPAUSE FAIL ({fails})");
        }
    }
}
