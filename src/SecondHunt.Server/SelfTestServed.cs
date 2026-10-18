using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // Served games (0.8.0, HubServed.cs): the hub's side, with this binary as a stand-in match host (--fake-matchhost: it
    // plays nothing; the test says its !started / !result / !lobby for it). The real match host's side is MphRead.Tools
    // -onlinetest served=hub.
    public static partial class SelfTest
    {
        private const string ServedVersion = "rom1/sim3";

        // --fake-matchhost [--version | --control ...]: says the version, or ready and then waits for stop (or its stdin closing)
        public static int FakeMatchHost(string[] args)
        {
            if (args.Contains("--version"))
            {
                Console.WriteLine(ServedVersion);
                return 0;
            }
            Console.WriteLine($"!ready {ServedVersion}");
            while (Console.In.ReadLine() is string line && line != "stop")
            {
            }
            Console.WriteLine("!bye stopped");
            return 0;
        }

        private static async Task ServedTests(string dir)
        {
            (WebApplication app, Uri uri) = await Start(Path.Combine(dir, "served.db"));
            Hub hub = app.Services.GetRequiredService<Hub>();
            // (this binary again: the apphost, or dotnet + this dll)
            string exe = Environment.ProcessPath!;
            bool viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            hub.Served = new ServedConfig
            {
                Exe = exe, MaxMatches = 1,
                Prefix = viaDotnet ? [typeof(SelfTest).Assembly.Location, "--fake-matchhost"] : ["--fake-matchhost"],
            };
            hub.StartServed();
            OnlineClient o = await Connect(uri, OnlineIdentity.Create(), "Owner");
            OnlineClient a = await Connect(uri, OnlineIdentity.Create(), "Servedone");
            OnlineClient old = await Connect(uri, OnlineIdentity.Create(), "Oldclient");
            OnlineClient p = await Connect(uri, OnlineIdentity.Create(), "Selfhost");
            await Friends(o, a);

            // asked + able: served, the asker on slot 1 (slot 0 = the match host), its process running
            GameInfo info = Info(pub: true);
            info.Served = true;
            GameMsg g = await o.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            string id = g.Game.Id;
            Process? mh = hub.MatchHostOf(id);
            Check(g.Game.Info.Served && g.Relay.Slot == 1 && mh != null && !mh.HasExited,
                $"served: a served game's owner sits on slot {g.Relay.Slot}, its match host runs");
            Check(await ErrorOf(() => old.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = ServedVersion })) == "version",
                "served: a client that can't play a served game is told to update");
            GameMsg ja = await a.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = ServedVersion, Served = true });
            Check(ja.Game.Info.Served && ja.Relay.Slot is not 0 and not 1, $"served: a joiner gets its own slot ({ja.Relay.Slot})");

            // at the cap (1 here): the next ask is player-hosted as before
            GameMsg pg = await p.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            Check(!pg.Game.Info.Served && pg.Relay.Slot == 0 && hub.MatchHostOf(pg.Game.Id) == null,
                "served: past the cap a new game is player-hosted (the asker hosts on slot 0)");
            await p.RequestAsync<OkMsg>(new LeaveGameReq());

            // the match host says when a match starts; the owner's own START does nothing; only the match host's result counts
            Check(!(await o.RequestAsync<OkMsg>(new StartGameReq { Started = true })).Done, "served: the owner's StartGame is not a start");
            hub.MatchHostSays(id, "!started");
            Check(!(await o.RequestAsync<OkMsg>(Result(id, (o, 1), (a, 0)))).Done, "served: a player's own result of a served match is ignored");
            hub.MatchHostSays(id, $"!result {{\"teams\":false,\"rows\":[{{\"slot\":{g.Relay.Slot},\"place\":0}},{{\"slot\":{ja.Relay.Slot},\"place\":1}}]}}");
            HeadToHeadInfo? oa = (await o.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = a.Me.Code })).Player.Record;
            Check(oa is { Rounds: 1, Victories: 1, Last: HeadToHeadInfo.Victory }, "served: the match host's result is the record (owner won)");
            hub.MatchHostSays(id, "!lobby");

            // draining (before an update): no new served games; the running one plays on
            hub.Draining = true;
            GameMsg dg = await p.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            Check(!dg.Game.Info.Served && hub.MatchHostOf(id) is { HasExited: false }, "served: draining -> new games player-hosted, the running one stays");
            await p.RequestAsync<OkMsg>(new LeaveGameReq());
            hub.Draining = false;

            // the owner leaves: the next player owns the lobby, the game stays; the last one leaves: closed, the match host goes
            var owner = Next<MemberEvent>(a, e => e.GameId == id && e.Owner != null);
            var closed = Next<GameClosedEvent>(a, e => e.GameId == id, ms: 1000);
            await o.RequestAsync<OkMsg>(new LeaveGameReq());
            MemberEvent? moved = await owner;
            Check(moved?.Owner == a.Me.Code && await closed == null, "served: the owner leaves -> the next player owns the lobby");
            await a.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(mh != null && mh.WaitForExit(5000) && hub.MatchHostOf(id) == null, "served: everyone gone -> the game closes, its match host leaves");

            // a match host that dies closes its game for the players in it
            GameMsg cg = await a.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            Process? crash = hub.MatchHostOf(cg.Game.Id);
            var crashed = Next<GameClosedEvent>(a, e => e.GameId == cg.Game.Id, ms: 5000);
            crash?.Kill();
            Check(cg.Game.Info.Served && (await crashed)?.Reason == "the server's match ended", "served: its match host dying closes the game");

            foreach (OnlineClient c in new[] { o, a, old, p })
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
