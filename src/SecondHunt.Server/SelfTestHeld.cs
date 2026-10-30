using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    public static partial class SelfTest
    {
        // 0.7.0 (release test T1): a started match's seat whose device went quiet on the relay is HELD: a fresh login hears of
        // it (WelcomeMsg.Held, the hub's REJOIN), FIND GAME releases it instead of "leave your game first", the player's
        // presence is plain online, and a seat quiet for SeatHoldMs is dropped (the timings shortened here)
        private static async Task HeldSeats(Uri uri)
        {
            long quiet = Hub.HeldQuietMs, hold = Hub.SeatHoldMs;
            Hub.HeldQuietMs = 300;
            Hub.SeatHoldMs = 60_000; // shortened only after the "kept" check below (a slow run passed 2.5 s before it)
            try
            {
                OnlineIdentity jid = OnlineIdentity.Create(), kid = OnlineIdentity.Create();
                OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "HeldHost");
                OnlineClient j = await Connect(uri, jid, "HeldJoin");
                OnlineClient k = await Connect(uri, kid, "HeldKeep");
                GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
                await j.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
                await k.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
                // a fresh login while the seat is still live (its relay slot just got its token): nothing held
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
                await j.DisposeAsync();
                OnlineClient j2 = await Connect(uri, jid, "HeldJoin");
                bool liveNotHeld = j2.Held == null;
                await j2.DisposeAsync();
                await Task.Delay(600);
                // quiet past HeldQuietMs: the login offers REJOIN for that game
                OnlineClient j3 = await Connect(uri, jid, "HeldJoin");
                bool offered = j3.Held?.Id == g.Game.Id && j3.Held.Started;
                // FIND GAME from a held seat: allowed (the seat is released), not "in_game"
                string? find = await ErrorOf(() => j3.RequestAsync<OkMsg>(new FindStartReq
                {
                    Version = "rom1/sim3", Modes = { "Battle" }, Nearby = false, PingMs = 40,
                }));
                string? backAfterFind = await ErrorOf(() => j3.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = "rom1/sim3" }));
                Check(liveNotHeld && offered && find == null && backAfterFind == "not_found",
                    "held seats: a quiet seat is offered at login (REJOIN), FIND releases it instead of in_game");
                await ErrorOf(() => j3.RequestAsync<OkMsg>(new FindCancelReq()));
                // the other joiner (still online, its relay slot quiet) is dropped after SeatHoldMs
                OnlineClient k2 = await Connect(uri, kid, "HeldKeep");
                bool keptBefore = k2.Held != null;
                Hub.SeatHoldMs = 1_000;
                await Task.Delay(3_500);
                string? gone = await ErrorOf(() => k2.RequestAsync<GameMsg>(new JoinGameReq { GameId = "", Version = "rom1/sim3" }));
                Check(keptBefore && gone == "not_found", "held seats: a seat quiet for the seat hold is dropped");
                await j3.DisposeAsync();
                await h.DisposeAsync();
                await k.DisposeAsync();
                await k2.DisposeAsync();
            }
            finally
            {
                Hub.HeldQuietMs = quiet;
                Hub.SeatHoldMs = hold;
            }
        }
            // 0.7.2 (release 0.2.0 F1): a seat dropped for its silence whose device sends again (only its sends were lost: it
        // heard the match all along) gets its relay slot back -- the host's packets reach it again, the others hear it
        // joined again. Before, the host still heard it and nothing reached it: it came back invisible, then ended its match.
        private static async Task SeatReturn(Uri uri)
        {
            long hold = Hub.SeatHoldMs;
            try
            {
                OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "BackHost");
                OnlineClient j = await Connect(uri, OnlineIdentity.Create(), "BackJoin");
                GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
                GameMsg jg = await j.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
                var at = new IPEndPoint(IPAddress.Loopback, g.Relay.Port);
                using var hu = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var ju = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                async Task Send(UdpClient u, RelayInfo info, byte to, string text)
                {
                    byte[] packet = new byte[RelayWire.SendHeader + text.Length];
                    RelayWire.WriteHeader(packet, RelayWire.ParseToken(info.Token), to);
                    System.Text.Encoding.ASCII.GetBytes(text, 0, text.Length, packet, RelayWire.SendHeader);
                    await u.SendAsync(packet, packet.Length, at);
                }
                static async Task<string?> Receive(UdpClient u, int ms = 1000)
                {
                    using var cts = new CancellationTokenSource(ms);
                    try
                    {
                        while (true)
                        {
                            byte[] p = (await u.ReceiveAsync(cts.Token)).Buffer;
                            if (p.Length > RelayWire.ReceiveHeader)
                            {
                                return System.Text.Encoding.ASCII.GetString(p, RelayWire.ReceiveHeader, p.Length - RelayWire.ReceiveHeader);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
                await Send(hu, g.Relay, RelayWire.KeepAlive, "");
                await Send(ju, jg.Relay, RelayWire.KeepAlive, "");
                await Task.Delay(200);
                // the joiner goes quiet past the hold (the host keeps sending): its seat is dropped
                var left = Next<MemberEvent>(h, e => !e.Joined && e.Slot == jg.Relay.Slot);
                Hub.SeatHoldMs = 1_000;
                for (int i = 0; i < 12; i++)
                {
                    await Send(hu, g.Relay, RelayWire.KeepAlive, "");
                    await Task.Delay(300);
                }
                bool dropped = await left != null;
                // its sends come again: the host hears it and it hears the host
                var joined = Next<MemberEvent>(h, e => e.Joined && e.Slot == jg.Relay.Slot);
                await Send(ju, jg.Relay, 0, "input");
                string? hostHeard = await Receive(hu);
                await Task.Delay(200);
                await Send(hu, g.Relay, RelayWire.ToAll, "snap");
                string? joinerHeard = await Receive(ju);
                Check(dropped && hostHeard == "input" && joinerHeard == "snap" && await joined != null,
                    $"held seats: a dropped seat whose device sends again gets its slot back (dropped {dropped}, host heard "
                    + $"{hostHeard ?? "nothing"}, joiner heard {joinerHeard ?? "nothing"})");
                await j.DisposeAsync();
                await h.DisposeAsync();
            }
            finally
            {
                Hub.SeatHoldMs = hold;
            }
        }
    }
}
