using System;
using System.Threading.Tasks;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // O5b host migration (HubGames.MigrateHost): the host of a started match leaving or dropping hands the game to the lowest
    // slot still online; a lobby still closes
    public static partial class SelfTest
    {
        private static async Task MigrateTests(Uri uri)
        {
            const string version = "rom1/sim3";
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Mighost");
            OnlineClient a = await Connect(uri, OnlineIdentity.Create(), "Migone");
            OnlineClient b = await Connect(uri, OnlineIdentity.Create(), "Migtwo");
            OnlineClient c = await Connect(uri, OnlineIdentity.Create(), "Migthree");

            // the host leaves a started match: slot 1 hosts it now, everyone hears it, the new host can take it to the lobby
            GameMsg g = await h.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            string id = g.Game.Id;
            GameMsg ja = await a.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version });
            GameMsg jb = await b.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version });
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            var movedA = Next<MemberEvent>(a, e => e.GameId == id && e.HostSlot != null);
            var movedB = Next<MemberEvent>(b, e => e.GameId == id && e.HostSlot != null);
            var closed = Next<GameClosedEvent>(b, e => e.GameId == id, ms: 1000);
            await h.RequestAsync<OkMsg>(new LeaveGameReq());
            MemberEvent? ma = await movedA, mb = await movedB;
            Check(ma != null && mb != null && ma.HostSlot == ja.Relay.Slot && mb.HostSlot == ja.Relay.Slot && ma.Slot == 0
                && !ma.Joined && await closed == null,
                $"migrate: the host leaves a started match -> slot {ma?.HostSlot} hosts it, the game stays open");
            Check(await ErrorOf(() => b.RequestAsync<OkMsg>(new StartGameReq { Started = false })) == "not_host"
                && await ErrorOf(() => a.RequestAsync<OkMsg>(new StartGameReq { Started = false })) == null,
                "migrate: only the new host takes the game back to the lobby");
            GamesMsg list = await c.RequestAsync<GamesMsg>(new ListGamesReq());
            GameCard? card = list.Games.Find(x => x.Id == id);
            Check(card != null && card.Host.Code == a.Me.Code && card.Players == 2,
                "migrate: the game's card names the new host, the old one is out");
            // the new host's lobby takes a joiner on a free slot (slot 0 stays free)
            GameMsg jc = await c.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = version });
            Check(jc.Relay.Slot != 0 && jc.Relay.Slot != ja.Relay.Slot && jc.Relay.Slot != jb.Relay.Slot,
                $"migrate: a joiner after it gets a free slot ({jc.Relay.Slot})");
            await a.RequestAsync<OkMsg>(new StartGameReq { Started = true });

            // the new host's connection drops mid-match: the next slot still online takes over
            var dropB = Next<MemberEvent>(b, e => e.GameId == id && e.HostSlot != null);
            var dropC = Next<MemberEvent>(c, e => e.GameId == id && e.HostSlot != null);
            await a.DisposeAsync();
            MemberEvent? db = await dropB, dc = await dropC;
            Check(db != null && dc != null && db.HostSlot == jb.Relay.Slot && dc.HostSlot == jb.Relay.Slot
                && db.Slot == ja.Relay.Slot, "migrate: the host's connection drops -> the next slot online hosts");

            // an offline slot is skipped: c (lower slot) dropped mid-match, so d takes over
            OnlineClient d = await Connect(uri, OnlineIdentity.Create(), "Migfour");
            GameMsg g2 = await b.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            string id2 = g2.Game.Id;
            GameMsg c2 = await c.RequestAsync<GameMsg>(new JoinGameReq { GameId = id2, Version = version });
            GameMsg d2 = await d.RequestAsync<GameMsg>(new JoinGameReq { GameId = id2, Version = version });
            Check(c2.Relay.Slot < d2.Relay.Slot, "migrate: second game joined (c below d)");
            await b.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            await c.DisposeAsync(); // (offline: keeps its seat in the started match)
            var skip = Next<MemberEvent>(d, e => e.GameId == id2 && e.HostSlot != null);
            await b.RequestAsync<OkMsg>(new LeaveGameReq());
            MemberEvent? sd = await skip;
            Check(sd != null && sd.HostSlot == d2.Relay.Slot, "migrate: an offline lower slot is skipped");
            await b.DisposeAsync();
            await d.DisposeAsync();

            // a lobby (not started) still closes when its host leaves
            OnlineClient l1 = await Connect(uri, OnlineIdentity.Create(), "Lobbyhost");
            OnlineClient l2 = await Connect(uri, OnlineIdentity.Create(), "Lobbyguest");
            GameMsg g3 = await l1.RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: true) });
            await l2.RequestAsync<GameMsg>(new JoinGameReq { GameId = g3.Game.Id, Version = version });
            var lobbyClosed = Next<GameClosedEvent>(l2, e => e.GameId == g3.Game.Id);
            await l1.RequestAsync<OkMsg>(new LeaveGameReq());
            Check(await lobbyClosed != null, "migrate: a lobby still closes when its host leaves");
            await l1.DisposeAsync();
            await l2.DisposeAsync();
            await h.DisposeAsync();
        }
    }
}
