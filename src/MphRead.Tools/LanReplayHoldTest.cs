using System;
using System.Linq;
using System.Net;
using System.Threading;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Replay;

namespace MphRead
{
    // -lanreplayholdtest (owner 2026-11-22, check 4: "i never got the replay on guest for lan match just now"): both
    // quit a LAN match at once (Odin host, Samsung guest); the host sent its recording so far, but the guest's menus came
    // back first and entering the lobby page ended its LanSession (MultiplayerMenu.OnPageEntered -> EndLan), so the transfer
    // stopped at 43 of 60 pieces. Real LAN sessions on this PC, a real ReplayLink, the real menus:
    //   BEFORE: with nothing holding the session (as before the fix) the guest's menus end it and the recording never arrives
    //   (the reported bug).
    //   QUIT: the match screen not let go yet (its hold, KeepOpen), the guest's menus enter the lobby: the session is only
    //   let go of (Current = null), the whole recording arrives, identical, and the menus are free to host or browse again.
    //   DONE: once the hold says nothing is in flight, the menus end the session again as before (the host hears BYE).
    internal static class LanReplayHoldTest
    {
        public static void Run()
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find("Combat Hall") ?? throw new InvalidOperationException("no Combat Hall");
            MatchSettings template = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 1, 1);
            var blob = new byte[66 * 1024]; // (about the device's 63 KB cut-short recording: 60 pieces)
            new Random(5).NextBytes(blob);
            FrontendSession menus = FrontendSession.Load(Paths.FileSystem);
            Console.WriteLine($"multiplayer menu bound: {menus.Multiplayer.Bound}");
            int fails = 0;

            // BEFORE
            {
                (LanSession host, LanSession guest)? pair = Pair(template);
                if (pair is not var (host, guest)) { Console.WriteLine("LANREPLAYHOLD FAIL (no LAN pair)"); return; }
                using var hostLink = new ReplayLink(host);
                using var guestLink = new ReplayLink(guest);
                byte[]? got = null;
                guestLink.ReplayReceived += b => got = b;
                LanSession.Current = guest;
                menus.Menu.Enter(MultiplayerMenu.LobbyPage); // (the guest's menus back from the match)
                hostLink.SendReplay(1, blob);
                Wait(() => got != null, 3000);
                bool bug = got == null;
                Console.WriteLine($"BEFORE {(bug ? "PASS" : "FAIL")}: with no hold the guest's menus ended its session and the "
                    + $"recording {(bug ? "never arrived (the reported bug)" : "arrived anyway")}");
                if (!bug) fails++;
                End(host, guest);
            }

            // QUIT
            {
                (LanSession host, LanSession guest)? pair = Pair(template);
                if (pair is not var (host, guest)) { Console.WriteLine("LANREPLAYHOLD FAIL (no LAN pair)"); return; }
                using var hostLink = new ReplayLink(host);
                using var guestLink = new ReplayLink(guest);
                byte[]? got = null;
                guestLink.ReplayReceived += b => got = b;
                bool screenDisposed = false;
                guest.KeepOpen = () => !screenDisposed || got == null; // (as ReplayLinkHold's: not let go yet, or still busy)
                LanSession.Current = guest;
                menus.Menu.Enter(MultiplayerMenu.LobbyPage);
                bool letGo = LanSession.Current == null;
                hostLink.SendReplay(1, blob);
                Wait(() => got != null && !hostLink.Sending, 10000);
                bool ok = letGo && got != null && got.AsSpan().SequenceEqual(blob) && !hostLink.Sending
                    && !host.Joiners.Any(j => j.Left);
                Console.WriteLine($"QUIT {(ok ? "PASS" : "FAIL")}: the guest's menus {(letGo ? "let go of" : "kept")} the held session, "
                    + $"the recording {(got == null ? "NEVER ARRIVED" : got.AsSpan().SequenceEqual(blob) ? $"arrived whole ({blob.Length / 1024} KB, identical)" : "ARRIVED DIFFERENT")}, "
                    + $"host still sending {hostLink.Sending}");
                if (!ok) fails++;
                screenDisposed = true; // (the hold's Close: the link and then the session)
                guest.KeepOpen = null;
                guest.Dispose();
                Wait(() => host.Joiners.Any(j => j.Left), 2000);
                bool bye = host.Joiners.Any(j => j.Left);
                Console.WriteLine($"QUIT {(bye ? "PASS" : "FAIL")}: the hold ended the session after the replay (the host heard BYE)");
                if (!bye) fails++;
                End(host, guest);
                if (LanSession.Current != null) { LanSession.Current.Dispose(); LanSession.Current = null; }
            }

            // DONE
            {
                (LanSession host, LanSession guest)? pair = Pair(template);
                if (pair is not var (host, guest)) { Console.WriteLine("LANREPLAYHOLD FAIL (no LAN pair)"); return; }
                guest.KeepOpen = () => false; // (the hold disposed, nothing in flight)
                LanSession.Current = guest;
                menus.Menu.Enter(MultiplayerMenu.LobbyPage);
                Wait(() => host.Joiners.Any(j => j.Left), 2000);
                bool ended = host.Joiners.Any(j => j.Left);
                Console.WriteLine($"DONE {(ended ? "PASS" : "FAIL")}: with nothing in flight the menus {(ended ? "ended" : "kept")} the session (BYE)");
                if (!ended) fails++;
                End(host, guest);
                if (LanSession.Current != null) { LanSession.Current.Dispose(); LanSession.Current = null; }
            }

            Console.WriteLine(fails == 0 ? "LANREPLAYHOLD PASS" : $"LANREPLAYHOLD FAIL ({fails})");
            Environment.ExitCode = fails == 0 ? 0 : 1;
        }

        // a host on any port and a guest joined to it at seat 1, the match started (seats known for the side channel)
        private static (LanSession, LanSession)? Pair(MatchSettings template)
        {
            LanSession host = LanSession.Host(template, Paths.MphKey, "PC host", 2, port: 0);
            LanSession guest = LanSession.Browse(Paths.MphKey);
            if (!guest.Join(new IPEndPoint(IPAddress.Loopback, host.Port), Hunter.Noxus, "Samsung", timeoutMs: 3000))
            {
                Console.WriteLine("join failed: " + guest.Status);
                End(host, guest);
                return null;
            }
            Wait(() => host.Joiners.Count > 0, 2000);
            host.SendStart();
            Wait(() => guest.Settings != null, 3000);
            if (guest.Settings == null)
            {
                Console.WriteLine("no START at the guest");
                End(host, guest);
                return null;
            }
            return (host, guest);
        }

        private static void End(LanSession host, LanSession guest)
        {
            guest.KeepOpen = null;
            guest.Dispose();
            host.Dispose();
            if (LanSession.Current == host || LanSession.Current == guest) LanSession.Current = null;
        }

        private static void Wait(Func<bool> done, int ms)
        {
            long end = Environment.TickCount64 + ms;
            while (!done() && Environment.TickCount64 < end) Thread.Sleep(10);
        }
    }
}
