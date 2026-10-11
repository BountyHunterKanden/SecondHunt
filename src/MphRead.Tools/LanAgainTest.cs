using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -lanagaintest (release 0.2.0, owner 2026-10-11: "now i cant even join from odin on lan to windows ... keeps saying
    // wait for players to join ... then i keep getting kicked", cured by restarting the Odin app): one process hosts and
    // joins twice, as the menus do after a LAN match (the host's lobby again, the joiner's list again). Each case on its own
    // free port (never GamePort: a real host on this PC may listen there).
    //   CLEAN: host + join, both ended (Dispose, as EndLan), then a new host on the same port + a new joiner
    //   STALE GUEST: the first joiner's session never ended (still listening), then the same as CLEAN
    //   STALE HOST: the first host's session never ended when the second hosts on its port (ReuseAddress): who answers
    internal static class LanAgainTest
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
            fails += Case("CLEAN", template, endGuest: true, endHost: true);
            fails += Case("STALE GUEST", template, endGuest: false, endHost: true);
            fails += Case("STALE HOST", template, endGuest: true, endHost: false);
            Console.WriteLine(fails == 0 ? "LANAGAIN PASS" : $"LANAGAIN FAIL ({fails})");
        }

        private static int FreePort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        private static int Case(string name, MatchSettings template, bool endGuest, bool endHost)
        {
            int port = FreePort();
            var address = new IPEndPoint(IPAddress.Loopback, port);
            LanSession host1 = LanSession.Host(template, Paths.MphKey, "PC host", 2, port);
            LanSession guest1 = LanSession.Browse(Paths.MphKey);
            bool first = guest1.Join(address, Hunter.Noxus, "Odin", timeoutMs: 3000);
            if (endGuest) guest1.Dispose();
            if (endHost) host1.Dispose();
            LanSession host2 = LanSession.Host(template, Paths.MphKey, "PC host", 2, port);
            LanSession guest2 = LanSession.Browse(Paths.MphKey);
            // (as the menus: the address its beacons give: its own port, or another one when the first host still has it)
            bool second = guest2.Join(new IPEndPoint(IPAddress.Loopback, host2.Port), Hunter.Noxus, "Odin", timeoutMs: 3000);
            int seen1 = host1.Joiners.Count(j => !j.Left), seen2 = host2.Joiners.Count(j => !j.Left);
            bool ok = first && second && (host2.Port == port || !endHost) && seen2 >= 1;
            Console.WriteLine($"{name} {(ok ? "PASS" : "FAIL")}: first join {first}, second join {second} ({guest2.Status}); "
                + $"host 2 on port {host2.Port} (asked {port}) sees {seen2} joiner(s), host 1 sees {seen1}");
            guest2.Dispose();
            host2.Dispose();
            guest1.Dispose();
            host1.Dispose();
            return ok ? 0 : 1;
        }
    }
}
