using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRead
{
    // -latearenatest (2026-10-21 hotfix, no ROM): Stasis Bunker ("E3 FIRST HUNT", room 119) is a retail arena, and a LAN host
    // takes a joiner for it only when the joiner's JOIN names it (ExtraArenas.IsLate): an older build can't play it.
    //   RULES: retail Has() for anyone; Stasis Bunker not for an older JOIN (no list, or a list without it), yes when named;
    //          the refusal says to update; this build's JOIN field names it (Write -> Read)
    //   LOOPBACK: a host in Stasis Bunker takes this build's joiner
    internal static class LateArenaTest
    {
        private const string Stasis = "E3 FIRST HUNT";
        private static int s_fails;

        private static void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok) s_fails++;
        }

        public static void Run()
        {
            Check("Stasis Bunker is a match arena", MatchArenas.IsArena(Stasis));
            Check("Stasis Bunker isn't an extra (imported) arena", !ExtraArenas.IsExtra(Stasis));
            Check("Combat Hall: an older joiner (no list) has it", ExtraArenas.Has(null, "Combat Hall"));
            Check("Stasis Bunker: an older joiner (no list) doesn't", !ExtraArenas.Has(null, Stasis));
            Check("Stasis Bunker: an older joiner (a list without it) doesn't", !ExtraArenas.Has(new HashSet<string> { "ECHOES SPIRES" }, Stasis));
            Check("Stasis Bunker: a joiner that names it has it", ExtraArenas.Has(new HashSet<string> { Stasis }, Stasis));
            string? refusal = ExtraArenas.Refusal(null, Stasis);
            Console.WriteLine($"      refusal: {refusal}");
            Check("the refusal says to update", refusal?.Contains("update the game") == true && refusal.Contains("stasis bunker"));
            var ms = new MemoryStream();
            ExtraArenas.Write(new BinaryWriter(ms));
            ms.Position = 0;
            HashSet<string>? sent = ExtraArenas.Read(new BinaryReader(ms));
            Check("this build's JOIN names Stasis Bunker", sent?.Contains(Stasis) == true);

            MatchSettings template = MatchSettings.Quick(GameMode.Battle, Stasis, Hunter.Samus, 1, 1);
            int port;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            LanSession host = LanSession.Host(template, "test", "PC host", 2, port);
            LanSession guest = LanSession.Browse("test");
            bool joined = guest.Join(new IPEndPoint(IPAddress.Loopback, host.Port), Hunter.Noxus, "Odin", timeoutMs: 3000);
            Check($"a host in Stasis Bunker takes this build's joiner ({guest.Status})", joined && host.Joiners.Count(j => !j.Left) == 1);
            guest.Dispose();
            host.Dispose();

            Console.WriteLine(s_fails == 0 ? "LATEARENA PASS" : $"LATEARENA FAIL ({s_fails})");
            if (s_fails > 0) Environment.Exit(1);
        }
    }
}
