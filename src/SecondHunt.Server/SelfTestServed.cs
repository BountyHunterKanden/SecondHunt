using System;
using System.Collections.Generic;
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
            if (args.Contains("--arenas"))
            {
                Console.WriteLine("Fiend");
                Console.WriteLine("Vault");
                return 0;
            }
            Console.WriteLine($"!ready {ServedVersion}");
            string? told = Environment.GetEnvironmentVariable("SELFTEST_MATCHHOST_LOG");
            while (Console.In.ReadLine() is string line && line != "stop")
            {
                if (told != null) File.AppendAllLines(told, [line]); // (what the server told it, for the leave check)
            }
            Console.WriteLine("!bye stopped");
            return 0;
        }

        // the lines the fake match host was told so far (read while it may be appending: shared open, a missing file is none)
        private static List<string> ToldLines(string path)
        {
            var lines = new List<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                while (r.ReadLine() is string l) lines.Add(l);
            }
            catch (IOException) { }
            return lines;
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
            string told = Path.Combine(dir, "matchhost_told.txt");
            Environment.SetEnvironmentVariable("SELFTEST_MATCHHOST_LOG", told);
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

            // 0.9.7: a recording that didn't reach a player over the match: the match host drops it and says !replay; it
            // waits in that player's mailbox as their own; a non-player's is refused and deleted
            byte[] rec = ReplayMailRules.Magic.Concat(Enumerable.Range(0, 50_000).Select(i => (byte)(i * 3))).ToArray();
            string drop = Path.Combine(hub.ReplayDir, "drop");
            Directory.CreateDirectory(drop);
            string Drop()
            {
                string n = Guid.NewGuid().ToString("N") + ".mphreplay";
                File.WriteAllBytes(Path.Combine(drop, n), rec);
                return n;
            }
            var ownEv = Next<ReplayOfferEvent>(a, e => e.Offer.Own);
            string n1 = Drop();
            hub.MatchHostSays(id, $"!replay {a.Me.Code} {n1} 300 Battle Combat Hall");
            ReplayOfferEvent? oe = await ownEv;
            ReplaysMsg aw = await ReplayMail.WaitingAsync(a);
            byte[] got = oe != null ? await ReplayMail.DownloadAsync(a, uri, oe.Offer.Id) : [];
            Check(oe?.Offer is { Own: true, Seconds: 300, Mode: "Battle", Arena: "Combat Hall" } && oe.Offer.From.Code == a.Me.Code
                && aw.Offers.Any(x => x.Own && x.Id == oe.Offer.Id) && got.SequenceEqual(rec) && !File.Exists(Path.Combine(drop, n1)),
                "served replays: a recording the match host couldn't hand over waits in that player's mailbox as their own");
            Check(oe != null && (await a.RequestAsync<OkMsg>(new ReplayDropReq { ReplayId = oe.Offer.Id })).Done
                && (await ReplayMail.WaitingAsync(a)).Offers.Count == 0, "served replays: taken, it's off the server");
            string n2 = Drop();
            hub.MatchHostSays(id, $"!replay {p.Me.Code} {n2} 1 Battle Fiend");
            Check(!File.Exists(Path.Combine(drop, n2)) && (await ReplayMail.WaitingAsync(p)).Offers.Count == 0,
                "served replays: a recording for someone not in that game is refused (and deleted)");

            // draining (before an update): no new served games; the running one plays on
            hub.Draining = true;
            GameMsg dg = await p.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            Check(!dg.Game.Info.Served && hub.MatchHostOf(id) is { HasExited: false }, "served: draining -> new games player-hosted, the running one stays");
            await p.RequestAsync<OkMsg>(new LeaveGameReq());
            hub.Draining = false;

            // the owner leaves: the next player owns the lobby, the game stays; the last one leaves: closed, the match host goes
            var owner = Next<MemberEvent>(a, e => e.GameId == id && e.Owner != null);
            var closed = Next<GameClosedEvent>(a, e => e.GameId == id, ms: 1000);
            int ownerSlot = g.Relay.Slot;
            await o.RequestAsync<OkMsg>(new LeaveGameReq());
            MemberEvent? moved = await owner;
            // the freed relay slot is told to the match host, or it keeps the leaver's seat for whoever gets the slot next
            // (2026-10-24: the new player shown under the old one's name)
            long until = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < until && !(ToldLines(told).Contains($"left {ownerSlot}"))) await Task.Delay(20);
            Check(ToldLines(told).Contains($"left {ownerSlot}"),
                $"served: a player leaving is told to the match host (left {ownerSlot}; it was told: {(ToldLines(told).Count > 0 ? string.Join(",", ToldLines(told)) : "nothing")})");
            Check(moved?.Owner == a.Me.Code && await closed == null, "served: the owner leaves -> the next player owns the lobby");
            await a.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(mh != null && mh.WaitForExit(5000) && hub.MatchHostOf(id) == null, "served: everyone gone -> the game closes, its match host leaves");

            // a match host that dies closes its game for the players in it
            GameMsg cg = await a.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            Process? crash = hub.MatchHostOf(cg.Game.Id);
            var crashed = Next<GameClosedEvent>(a, e => e.GameId == cg.Game.Id, ms: 5000);
            crash?.Kill();
            Check(cg.Game.Info.Served && (await crashed)?.Reason == "the server's match ended", "served: its match host dying closes the game");

            // Find Game (HubServedFind.cs): the vote is over -> the server opens the match's game, nobody is told to host; the
            // players join it; the first start makes the vote's pick (the best ping) the lobby's owner
            hub.Find = FindConfig.Fast;
            OnlineClient fx = await Connect(uri, OnlineIdentity.Create(), "Findx");
            OnlineClient fy = await Connect(uri, OnlineIdentity.Create(), "Findy");
            FindStartReq Search(int ping, string arena) => new()
            {
                Version = ServedVersion, Modes = new List<string> { "Battle" }, PingMs = ping,
                Arenas = new Dictionary<string, List<string>> { ["Battle"] = new() { arena } },
            };
            var gx = Next<FindEvent>(fx, e => e.State is FindState.Game or FindState.Host, 10000);
            var gy = Next<FindEvent>(fy, e => e.State is FindState.Game or FindState.Host, 10000);
            await fx.RequestAsync<OkMsg>(Search(20, "Fiend"));
            await fy.RequestAsync<OkMsg>(Search(40, "Fiend"));
            FindEvent? ex = await gx, ey = await gy;
            string fid = ex?.GameId ?? "";
            Check(ex?.State == FindState.Game && ey?.State == FindState.Game && fid.Length > 0 && ey.GameId == fid
                && hub.MatchHostOf(fid) is { HasExited: false }, "served find: the server opens the match's game, both players are sent to it");
            GameMsg jx = await fx.RequestAsync<GameMsg>(new JoinGameReq { GameId = fid, Version = ServedVersion, Served = true });
            GameMsg jy = await fy.RequestAsync<GameMsg>(new JoinGameReq { GameId = fid, Version = ServedVersion, Served = true });
            Check(jx.Game.Info.Served && !jx.Game.Info.Public && jx.Relay.Slot >= 1 && jy.Relay.Slot >= 1 && jx.Relay.Slot != jy.Relay.Slot,
                "served find: the players join it as joiners (no one on the match host's slot)");
            var ownerEv = Next<MemberEvent>(fy, e => e.GameId == fid && e.Owner != null, ms: 3000);
            hub.MatchHostSays(fid, "!started");
            Check((await ownerEv)?.Owner == fx.Me.Code, "served find: the first match started -> the best ping owns the lobby");

            // the server is full (cap 1): the next match is player-hosted as before
            OnlineClient fz = await Connect(uri, OnlineIdentity.Create(), "Findz");
            OnlineClient fw = await Connect(uri, OnlineIdentity.Create(), "Findw");
            var hz = Next<FindEvent>(fz, e => e.State is FindState.Game or FindState.Host or FindState.Join, 10000);
            var hw = Next<FindEvent>(fw, e => e.State is FindState.Game or FindState.Host or FindState.Join, 10000);
            await fz.RequestAsync<OkMsg>(Search(20, "Vault"));
            await fw.RequestAsync<OkMsg>(Search(40, "Vault"));
            FindEvent? ez = await hz, ew = await hw;
            Check(ez?.State == FindState.Host && ew?.State == FindState.Join, "served find: at the cap a player hosts the match as before");
            Process? fmh = hub.MatchHostOf(fid);
            await fx.RequestAsync<OkMsg>(new LeaveGameReq());
            await fy.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(fmh != null && fmh.WaitForExit(5000), "served find: everyone gone -> the server is free again");

            // 0.8.2: an imported arena is served when the match host has it (its --arenas), else player-hosted as before
            GameInfo echoes = Info(pub: false);
            echoes.Served = true;
            echoes.ExtraArena = true;
            echoes.Arena = "ECHOES SPIRES";
            GameMsg eg = await p.RequestAsync<GameMsg>(new HostGameReq { Info = echoes });
            Check(!eg.Game.Info.Served && hub.MatchHostOf(eg.Game.Id) == null, "served: an imported arena the match host lacks is player-hosted");
            await p.RequestAsync<OkMsg>(new LeaveGameReq());

            // 0.8.2: a party leader's served game in an imported arena the match host has: the member is sent to it; 8 players
            // seat on slots 1-8 (slot 0 = the match host), a 9th is turned away
            OnlineClient lead = await Connect(uri, OnlineIdentity.Create(), "Partylead");
            OnlineClient mem = await Connect(uri, OnlineIdentity.Create(), "Partymem");
            await Friends(lead, mem);
            var invite = Next<PartyInviteEvent>(mem);
            await lead.RequestAsync<OkMsg>(new PartyInviteReq { Code = mem.Me.Code });
            await mem.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = (await invite)!.PartyId });
            var follow = Next<PartyGameEvent>(mem);
            echoes.Arena = "Vault";
            echoes.Public = true;
            echoes.MaxPlayers = 8;
            GameMsg vg = await lead.RequestAsync<GameMsg>(new HostGameReq { Info = echoes });
            string vid = vg.Game.Id;
            Check(vg.Game.Info.Served && vg.Game.Info.MaxPlayers == 8 && hub.MatchHostOf(vid) is { HasExited: false },
                "served: an imported arena the match host has is served, 8 seats");
            Check((await follow)?.GameId == vid, "served: the party member is sent to the leader's served game");
            var slots = new List<int> { vg.Relay.Slot };
            var seated = new List<OnlineClient> { lead, mem };
            slots.Add((await mem.RequestAsync<GameMsg>(new JoinGameReq { GameId = vid, Version = ServedVersion, Served = true })).Relay.Slot);
            for (int i = 0; i < 7; i++)
            {
                OnlineClient c = await Connect(uri, OnlineIdentity.Create(), $"Seat{i}");
                seated.Add(c);
                if (i < 6)
                {
                    slots.Add((await c.RequestAsync<GameMsg>(new JoinGameReq { GameId = vid, Version = ServedVersion, Served = true })).Relay.Slot);
                }
            }
            Check(slots.Order().SequenceEqual(Enumerable.Range(1, 8)), $"served: 8 players sit on slots 1-8 ({string.Join(",", slots)})");
            Check(await ErrorOf(() => seated[^1].RequestAsync<GameMsg>(new JoinGameReq { GameId = vid, Version = ServedVersion, Served = true }))
                == "full", "served: a 9th player is turned away (full)");

            foreach (OnlineClient c in new[] { o, a, old, p, fx, fy, fz, fw }.Concat(seated))
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
