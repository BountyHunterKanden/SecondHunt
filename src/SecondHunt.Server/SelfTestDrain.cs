using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // 0.10.3: the restart drain before a deploy (HubDrain.cs) and the request flood limit (HubFlood.cs). The drain: everyone
    // hears it, searches end, nothing new starts (a rejoin still does), it drains once the last match ends or the cap passes,
    // calling it off puts things back, and the drain file starts, cancels and cleans up after itself. The flood limit: a
    // calm client never hears "slow_down", one firing hundreds of requests is refused, then dropped, and the calm one isn't.
    public static partial class SelfTest
    {
        private static async Task DrainTests(string dir)
        {
            await Drain(Path.Combine(dir, "drain.db"));
            await DrainFileTests(Path.Combine(dir, "drainfile.db"), dir);
            await Flood(Path.Combine(dir, "flood.db"));
        }

        private static FindStartReq DrainSearch() => new() { Version = "rom1/sim3", Modes = { "Battle" }, Nearby = false, PingMs = 40 };

        private static async Task Drain(string db)
        {
            (WebApplication app, Uri uri) = await Start(db);
            Hub hub = app.Services.GetRequiredService<Hub>();
            OnlineIdentity jid = OnlineIdentity.Create();
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "DrainHost");
            OnlineClient j = await Connect(uri, jid, "DrainJoin");
            OnlineClient o = await Connect(uri, OnlineIdentity.Create(), "DrainLook");
            OnlineClient s = await Connect(uri, OnlineIdentity.Create(), "DrainFind");
            GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            await j.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" });
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            await s.RequestAsync<OkMsg>(DrainSearch());

            var notice = Next<ServerNoticeEvent>(o, x => x.Kind == NoticeKind.Restart);
            var stopped = Next<FindEvent>(s, x => x.State == FindState.Cancelled);
            hub.BeginRestart(60);
            ServerNoticeEvent? n = await notice;
            FindEvent? f = await stopped;
            Check(n?.Seconds == 60 && o.Notice?.Kind == NoticeKind.Restart && f?.Reason == "restart",
                $"drain: everyone hears the restart ({n?.Seconds} s) and searches end ({f?.Reason})");
            OnlineClient other = await Connect(uri, OnlineIdentity.Create(), "DrainOther");
            string? host = await ErrorOf(() => other.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) }));
            string? find = await ErrorOf(() => other.RequestAsync<OkMsg>(DrainSearch()));
            string? join = await ErrorOf(() => other.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" }));
            Check(host == Hub.DrainingCode && find == Hub.DrainingCode && join == Hub.DrainingCode,
                $"drain: no new lobbies, searches or joins ({host}/{find}/{join})");
            Check(hub.DrainHealth.StartsWith(" restart ") && other.Notice?.Kind == NoticeKind.Restart && other.Notice.Seconds > 0,
                $"drain: health says{hub.DrainHealth}, a new login hears it in its welcome ({other.Notice?.Seconds} s)");
            // the joiner drops and comes back to its own started match: let in
            await j.DisposeAsync();
            OnlineClient j2 = await Connect(uri, jid, "DrainJoin");
            string? rejoin = await ErrorOf(() => j2.RequestAsync<GameMsg>(new JoinGameReq { GameId = g.Game.Id, Version = "rom1/sim3" }));
            Check(rejoin != Hub.DrainingCode, $"drain: a rejoin to your own running match is still let in ({rejoin ?? "ok"})");
            // the match ends: drained
            var drained = Next<ServerNoticeEvent>(o, x => x.Kind == NoticeKind.Drained, ms: 4000);
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = false });
            ServerNoticeEvent? d = await drained;
            Check(d != null && hub.Drained && hub.DrainHealth == " drained", $"drain: the last match ended: drained ({hub.DrainHealth})");
            // called off: back to normal
            var cancel = Next<ServerNoticeEvent>(o, x => x.Kind == NoticeKind.Cancel);
            hub.CancelRestart();
            bool cancelled = await cancel != null;
            await Task.Delay(100);
            string? again = await ErrorOf(() => other.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) }));
            Check(cancelled && o.Notice == null && !hub.Restarting && hub.DrainHealth == "" && again == null,
                $"drain: called off, lobbies open again ({again ?? "ok"})");
            await other.RequestAsync<OkMsg>(new LeaveGameReq());
            // the cap passes with a match still running: drained all the same
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            var capped = Next<ServerNoticeEvent>(o, x => x.Kind == NoticeKind.Drained, ms: 5000);
            hub.BeginRestart(2);
            bool cap = await capped != null && hub.Drained;
            Check(cap, "drain: the cap passed with a match running: drained");
            hub.CancelRestart();
            foreach (OnlineClient c in new[] { h, j2, o, s, other })
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
        }

        private static async Task DrainFileTests(string db, string dir)
        {
            (WebApplication app, Uri uri) = await Start(db);
            Hub hub = app.Services.GetRequiredService<Hub>();
            string file = Path.Combine(dir, "drain");
            hub.DrainFile = file;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await File.WriteAllTextAsync(file, Hub.RestartLine(now, 30, exit: false));
            await Task.Delay(2_500);
            bool began = hub.Restarting && hub.Drained; // (no match running: drained at once)
            File.Delete(file);
            await Task.Delay(2_500);
            bool off = !hub.Restarting;
            Check(began && off, "drain file: a restart line starts the drain, removing the file calls it off");
            await File.WriteAllTextAsync(file, Hub.RestartLine(now - 1_000, 30, exit: false));
            await Task.Delay(2_500);
            Check(!hub.Restarting && !File.Exists(file), "drain file: a restart line from before this server started is removed");
            // "drain on" (a plain time, no restart): no served games only, not a restart
            await File.WriteAllTextAsync(file, now.ToString(CultureInfo.InvariantCulture) + "\n");
            await Task.Delay(2_500);
            Check(!hub.Restarting && File.Exists(file), "drain file: a plain drain on isn't a restart");
            File.Delete(file);
            hub.DrainFile = "";
            await app.StopAsync();
        }

        private static async Task Flood(string db)
        {
            (WebApplication app, Uri uri) = await Start(db);
            OnlineClient calm = await Connect(uri, OnlineIdentity.Create(), "FloodCalm");
            OnlineClient flood = await Connect(uri, OnlineIdentity.Create(), "FloodBad");
            // a calm client: a burst of 30 at once (a login)
            int calmRefused = 0;
            for (int i = 0; i < 30; i++)
            {
                calmRefused += await ErrorOf(() => calm.RequestAsync<OkMsg>(new PingReq { Ms = 40 })) == Hub.SlowDownCode ? 1 : 0;
            }
            // the flood: hundreds at once, then more until it's cut off
            int slowDown = 0;
            for (int round = 0; round < 10 && flood.Connected; round++)
            {
                string?[] codes = await Task.WhenAll(Enumerable.Range(0, 200)
                    .Select(_ => ErrorOf(() => flood.RequestAsync<OkMsg>(new PingReq { Ms = 40 }))));
                slowDown += codes.Count(c => c == Hub.SlowDownCode);
                calmRefused += await ErrorOf(() => calm.RequestAsync<OkMsg>(new PingReq { Ms = 40 })) == Hub.SlowDownCode ? 1 : 0;
            }
            await Task.Delay(300);
            Check(slowDown > 0 && !flood.Connected && flood.CloseReason?.Contains("too many requests") == true,
                $"flood: a client firing hundreds of requests is told to slow down ({slowDown}), then dropped ({flood.CloseReason})");
            string? after = await ErrorOf(() => calm.RequestAsync<OkMsg>(new PingReq { Ms = 40 }));
            Check(calmRefused == 0 && calm.Connected && after == null, "flood: a calm client is never refused, and stays on");
            await calm.DisposeAsync();
            await flood.DisposeAsync();
            await app.StopAsync();
        }
    }
}
