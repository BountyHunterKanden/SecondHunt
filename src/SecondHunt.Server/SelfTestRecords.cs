using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // T3 (0.7.0): friend OR rival like vanilla, and head to head counted once on the server
    public static partial class SelfTest
    {
        private static async Task Records(string dir)
        {
            // the one-time migration: rival links + rival requests between friends go, the friendship stays
            string migrateDb = Path.Combine(dir, "records-migrate.db");
            using (var store = new Store(migrateDb))
            {
                var p = store.Login(OnlineIdentity.Create().PublicKey, "Pair1");
                var q = store.Login(OnlineIdentity.Create().PublicKey, "Pair2");
                var r = store.Login(OnlineIdentity.Create().PublicKey, "Other");
                foreach ((string a, string b) in new[] { (p.Code, q.Code), (q.Code, p.Code) })
                {
                    store.AddLink(a, b, LinkKind.Friend);
                    store.AddLink(a, b, LinkKind.Rival);
                }
                store.AddLink(p.Code, q.Code, "req:" + LinkKind.Rival);
                store.AddLink(p.Code, r.Code, LinkKind.Rival);
                store.AddLink(r.Code, p.Code, LinkKind.Rival);
                int gone = store.SeparateFriendsAndRivals();
                Check(gone == 3 && store.HasLink(p.Code, q.Code, LinkKind.Friend) && store.HasLink(q.Code, p.Code, LinkKind.Friend)
                    && !store.HasLink(p.Code, q.Code, LinkKind.Rival) && !store.HasLink(q.Code, p.Code, LinkKind.Rival)
                    && store.HasLink(p.Code, r.Code, LinkKind.Rival), $"migration: friend+rival doubles become friends ({gone} rows went)");
            }

            (WebApplication app, Uri uri) = await Start(Path.Combine(dir, "records.db"));
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Host");
            OnlineClient g1 = await Connect(uri, OnlineIdentity.Create(), "Guest1");
            OnlineClient g2 = await Connect(uri, OnlineIdentity.Create(), "Guest2");
            OnlineClient s = await Connect(uri, OnlineIdentity.Create(), "Stranger");

            // rivals first, then friend codes swapped: upgraded to friends (vanilla), both sides told the rival link went
            await h.RequestAsync<OkMsg>(new AddReq { Code = g1.Me.Code, Kind = LinkKind.Rival });
            await g1.RequestAsync<OkMsg>(new AddReq { Code = h.Me.Code, Kind = LinkKind.Rival });
            var roster = await h.RequestAsync<RosterMsg>(new RosterReq());
            Check(roster.Rivals.Any(x => x.Code == g1.Me.Code), "mutual ADD RIVAL: rivals");
            var goneH = Next<RemovedEvent>(h, e => e.Kind == LinkKind.Rival);
            var goneG = Next<RemovedEvent>(g1, e => e.Kind == LinkKind.Rival);
            await h.RequestAsync<OkMsg>(new AddReq { Code = g1.Me.Code, Kind = LinkKind.Friend });
            var done = await g1.RequestAsync<OkMsg>(new AddReq { Code = h.Me.Code, Kind = LinkKind.Friend });
            roster = await h.RequestAsync<RosterMsg>(new RosterReq());
            PlayerCard card = (await h.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = g1.Me.Code })).Player;
            Check(done.Done && roster.Friends.Any(x => x.Code == g1.Me.Code) && roster.Rivals.All(x => x.Code != g1.Me.Code)
                && card.Friend && !card.Rival && await goneH != null && await goneG != null,
                "a rival who swaps friend codes is upgraded to a friend (rival link gone, both told)");
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(new AddReq { Code = g1.Me.Code, Kind = LinkKind.Rival })) == "already_friends",
                "ADD RIVAL on a friend is refused");
            // a rival request waiting when the two become friends: accepting it does nothing
            await s.RequestAsync<OkMsg>(new AddReq { Code = g2.Me.Code, Kind = LinkKind.Rival });
            await s.RequestAsync<OkMsg>(new AddReq { Code = g2.Me.Code, Kind = LinkKind.Friend });
            await g2.RequestAsync<OkMsg>(new AddReq { Code = s.Me.Code, Kind = LinkKind.Friend });
            string? late = await ErrorOf(() => g2.RequestAsync<OkMsg>(new AnswerReq { Code = s.Me.Code, Kind = LinkKind.Rival, Accept = true }));
            card = (await g2.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = s.Me.Code })).Player;
            Check(late == "no_request" && card.Friend && !card.Rival, "becoming friends drops a waiting rival request");

            // an online match: host + two guests; the host reports the places once
            var info = new GameInfo { Mode = "Battle", Arena = "Combat Hall", MaxPlayers = 4, Public = true, Version = "rom1/sim3" };
            GameMsg game = await h.RequestAsync<GameMsg>(new HostGameReq { Info = info });
            string id = game.Game.Id;
            await g1.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = "rom1/sim3" });
            await g2.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = "rom1/sim3" });
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(Result(id, (h, 0), (g1, 1)))) == "not_in_game",
                "a result before the match started is refused");
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(Result(id, (h, 0), (s, 1)))) == "bad_result",
                "a result naming someone outside the match is refused");
            Check(await ErrorOf(() => s.RequestAsync<OkMsg>(Result(id, (s, 0), (h, 1)))) == "not_in_game",
                "a player outside the match can't report it");
            Check(await ErrorOf(() => h.RequestAsync<OkMsg>(Result(id, (h, 0)))) == "bad_result", "one row isn't a result");
            var first = await h.RequestAsync<OkMsg>(Result(id, (h, 0), (g1, 1), (g2, 1)));
            var again = await g1.RequestAsync<OkMsg>(Result(id, (h, 0), (g1, 1), (g2, 1)));
            HeadToHeadInfo? hg1 = (await h.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = g1.Me.Code })).Player.Record;
            HeadToHeadInfo? g1h = (await g1.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = h.Me.Code })).Player.Record;
            HeadToHeadInfo? g1g2 = (await g1.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = g2.Me.Code })).Player.Record;
            Check(first.Done && !again.Done, "counted once per match (a second report of it is ignored)");
            Check(hg1 is { Rounds: 1, Victories: 1, Last: HeadToHeadInfo.Victory } && g1h is { Rounds: 1, Victories: 0, Last: HeadToHeadInfo.Defeat }
                && g1g2 is { Rounds: 1, Victories: 0, Last: HeadToHeadInfo.Tie },
                "both sides read the same record (host 1-0 over guest 1, guest 1 and guest 2 tied)");
            // the next match of the same game (PLAY AGAIN), teams: teammates aren't counted
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = false });
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            var teams = Result(id, (h, 0), (g1, 0), (g2, 1));
            teams.Teams = true;
            teams.Rows[0].Team = 0;
            teams.Rows[1].Team = 0;
            teams.Rows[2].Team = 1;
            await h.RequestAsync<OkMsg>(teams);
            hg1 = (await h.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = g1.Me.Code })).Player.Record;
            HeadToHeadInfo? g2h = (await g2.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = h.Me.Code })).Player.Record;
            Check(hg1 is { Rounds: 1 } && g2h is { Rounds: 2, Victories: 0, Last: HeadToHeadInfo.Defeat },
                "a team match counts across teams only (teammates' record unchanged)");
            Check((await h.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = s.Me.Code })).Player.Record == null,
                "no record with someone never played");

            // LAN (the server never saw it): the reporter must be in it, each match once
            var lan = Result("", (s, 0), (h, 1));
            lan.Lan = true;
            var lanFirst = await s.RequestAsync<OkMsg>(lan);
            var lanAgain = await s.RequestAsync<OkMsg>(lan);
            var notMine = Result("", (h, 0), (g1, 1));
            notMine.Lan = true;
            HeadToHeadInfo? hs = (await h.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = s.Me.Code })).Player.Record;
            Check(lanFirst.Done && !lanAgain.Done && await ErrorOf(() => s.RequestAsync<OkMsg>(notMine)) == "bad_result"
                && hs is { Rounds: 1, Victories: 0, Last: HeadToHeadInfo.Defeat }, "a LAN result: counted once, only from a player in it");

            foreach (OnlineClient c in new[] { h, g1, g2, s })
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private static MatchResultReq Result(string gameId, params (OnlineClient Who, int Place)[] rows) => new()
        {
            GameId = gameId,
            MatchKey = Guid.NewGuid().ToString("N"),
            Rows = rows.Select(r => new ResultRow { Code = r.Who.Me.Code, Place = r.Place }).ToList(),
        };
    }
}
