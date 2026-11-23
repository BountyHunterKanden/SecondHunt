using System;
using System.Net;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -lanrulestest (owner 2026-11-15: "in LAN if the host changes rules while someone's in lobby they get booted"): the
    // host's menus ended LanSession.Current on any page but its lobby (BYE to the joiner), so the MODE / RULES / SELECT
    // HUNTER steps the lobby's tabs lead to booted every joiner. EDIT: the host walks lobby -> mode -> rules -> hunter ->
    // lobby with a joiner in, and the session and the joiner stay. LEAVE: CREATE OR JOIN still ends it (the joiner hears BYE).
    internal static class LanRulesEditTest
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

            LanSession host = LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            LanSession.Current = host;
            LanSession guest = LanSession.Browse(Paths.MphKey);
            bool joined = guest.Join(new IPEndPoint(IPAddress.Loopback, host.Port), Hunter.Noxus, "Odin", timeoutMs: 3000);
            Console.WriteLine($"JOIN {(joined ? "PASS" : "FAIL")}: {guest.Status}");
            if (!joined) fails++;

            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            Console.WriteLine($"multiplayer menu bound: {menus.Multiplayer.Bound}");
            int[] walk = { MultiplayerMenu.LobbyPage, MultiplayerMenu.ModePage, 28, MultiplayerMenu.HunterPage, MultiplayerMenu.LobbyPage };
            bool kept = true;
            foreach (int page in walk)
            {
                menus.Menu.Enter(page);
                if (!ReferenceEquals(LanSession.Current, host))
                {
                    Console.WriteLine($"  page {page} ended the host's session");
                    kept = false;
                    break;
                }
            }
            Thread.Sleep(300); // (a BYE would be in by now)
            bool stayed = kept && !guest.PeerLeft;
            Console.WriteLine($"EDIT {(stayed ? "PASS" : "FAIL")}: the host's session {(kept ? "kept" : "ended")} through "
                + $"mode/rules/hunter, the joiner {(guest.PeerLeft ? "was told the host left" : "is still in")}");
            if (!stayed) fails++;

            menus.Menu.Enter(MultiplayerMenu.CreateJoinPage);
            Thread.Sleep(300);
            bool ended = !ReferenceEquals(LanSession.Current, host) && guest.PeerLeft;
            Console.WriteLine($"LEAVE {(ended ? "PASS" : "FAIL")}: CREATE OR JOIN {(ended ? "ended" : "kept")} the host's session"
                + $" (joiner PeerLeft {guest.PeerLeft})");
            if (!ended) fails++;

            guest.Dispose();
            host.Dispose();
            LanSession.Current?.Dispose(); // (CREATE OR JOIN's own listener)
            LanSession.Current = null;
            Console.WriteLine(fails == 0 ? "LANRULES PASS" : $"LANRULES FAIL ({fails})");
        }
    }
}
