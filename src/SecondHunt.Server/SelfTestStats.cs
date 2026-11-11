using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // 0.10.0: the Gaming Hub (HubStats.cs, StoreStats.cs, docs/GAMING_HUB.md): counted from the host's report, the three pages
    public static partial class SelfTest
    {
        private static async Task HubTests(string dir)
        {
            // the rank rule = vanilla's (two level-0 players: the winner +2, the loser -1, never under 0)
            Check(HubRules.After(0, 0, -1, false, new[] { (0, 1, -1) }) == 2 && HubRules.After(0, 1, -1, false, new[] { (0, 0, -1) }) == 0
                && HubRules.After(100, 1, -1, false, new[] { (0, 0, -1) }) == 98 && HubRules.After(849, 0, -1, false, new[] { (0, 1, -1) }) == 850
                && HubRules.After(0, 0, 0, true, new[] { (0, 1, 0) }) == 0, "hub: rank rule (gain, loss, floor, cap, teammates)");
            // Overall Points: games x4, wins x4, kills /10, headshots /20, win rate 0-10, each part capped (21,010 at most)
            Check(HubRules.Overall(10, 10, 90, 30, 1000) == 100 && HubRules.Overall(0, 0, 0, 0, 0) == 0
                && HubRules.Overall(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1000) == HubRules.MaxOverall && HubRules.MaxOverall == 21010
                && HubRules.Overall(-5, 0, -9, 0, 0) == 0, "hub: Overall Points (parts, caps, never negative)");
            // a match's kills go by how long the server saw it run: 20 + 12 a minute, 999 at most
            Check(Hub.MatchKillCap(0) == 20 && Hub.MatchKillCap(10) == 140 && Hub.MatchKillCap(-3) == 20 && Hub.MatchKillCap(600) == 999,
                "hub: the kill cap from the match's length");
            Check(ContentKeys.Key("MP1 SANCTORUS") == "mp1_sanctorus" && ContentKeys.Key("Dark Samus") == "dark_samus"
                && !ContentKeys.Valid("BAD KEY!") && ContentKeys.Display(ContentKeys.Hunter, "new_hunter") == "NEW HUNTER"
                && ContentKeys.WeaponKey(11) == "pirate_blaster", "hub: content keys (names, unknown keys still show)");

            (WebApplication app, System.Uri uri) = await Start(Path.Combine(dir, "hub.db"));
            OnlineClient h = await Connect(uri, OnlineIdentity.Create(), "Host");
            OnlineClient g1 = await Connect(uri, OnlineIdentity.Create(), "Guest1");
            OnlineClient g2 = await Connect(uri, OnlineIdentity.Create(), "Guest2");
            OnlineClient s = await Connect(uri, OnlineIdentity.Create(), "Stranger");
            Check(h.ServerAtLeast(HubReq.SinceServer), $"hub: the server has the Gaming Hub ({h.ServerVersion})");
            HubMsg empty = await s.RequestAsync<HubMsg>(new HubReq());
            Check(empty.Leaders.Count == 0 && empty.Hunters.Count == 0 && empty.Matches == 0, "hub: an empty hub before any match");

            var info = new GameInfo { Mode = "Battle", Arena = "Combat Hall", MaxPlayers = 4, Public = true, Version = "rom1/sim3" };
            string id = (await h.RequestAsync<GameMsg>(new HostGameReq { Info = info })).Game.Id;
            await g1.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = "rom1/sim3" });
            await g2.RequestAsync<GameMsg>(new JoinGameReq { GameId = id, Version = "rom1/sim3" });
            await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
            MatchResultReq first = Result(id, (h, 0), (g1, 1), (g2, 2));
            first.Mode = "battle";
            first.Arena = "mp1_sanctorus";
            first.Seconds = 300;
            first.Rows[0].Stats = new ResultStats
            {
                Hunter = "trooper", Kills = 5, Deaths = 1, Headshots = 2, AltKills = 1, BestStreak = 3, Shots = 120,
                Weapons = new() { ["power_beam"] = 3, [ContentKeys.AltForm] = 1, ["pirate_blaster"] = 1 },
            };
            first.Rows[1].Stats = new ResultStats { Hunter = "kanden", Kills = 2, Deaths = 3, Weapons = new() { ["volt_driver"] = 2 } };
            // a doctored row: clamped (kills to the 20 an instant match allows), a bad key dropped
            first.Rows[2].Stats = new ResultStats { Hunter = "samus", Kills = 5000, Headshots = 9000, Shots = int.MaxValue, Weapons = new() { ["BAD KEY!"] = 7 } };
            await h.RequestAsync<OkMsg>(first);

            HubMsg hub = await s.RequestAsync<HubMsg>(new HubReq());
            // the host leads Overall Points (4 + 4 = 8) over the clamped doctored row (4 + 20/10 + 20/20 = 7)
            Check(hub.Leaders.TryGetValue(HubCategory.Points, out LeaderRow? top) && top.Code == h.Me.Code && top.Value == 8 && top.Rank == 0
                && hub.Leaders[HubCategory.KillStreak].Code == h.Me.Code && hub.Leaders[HubCategory.KillStreak].Value == 3
                && !hub.Leaders.ContainsKey(HubCategory.WinRate), "hub: each board's leader (Overall Points, no win rate before 10 games)");
            Check(hub.Hunters.Count == 3 && hub.Hunters.All(x => x.Share == 333) && hub.Modes.Single() is { Key: "battle", Share: 1000 }
                && hub.Arenas.Single().Key == "mp1_sanctorus" && hub.Weapons.All(x => x.Key != "BAD KEY!"),
                "hub: favourites in tenths of a percent (bad keys dropped)");
            Check(hub.Players == 3 && hub.Matches == 1 && hub.Recent.Any(x => x.Code == h.Me.Code) && hub.Recent.All(x => x.Code != s.Me.Code),
                $"hub: counts + online lately, not the asker ({hub.Recent.Count} recent)");

            StatsMsg card = await g1.RequestAsync<StatsMsg>(new StatsReq { Code = h.Me.Code });
            Check(card.Totals[Store.Kills] == 5 && card.Totals[Store.Wins] == 1 && card.Totals[Store.Seconds] == 300 && card.Totals[Store.Shots] == 120
                && card.Totals[HubCategory.BipedKills] == 4 && card.Hunters["trooper"] == 1 && card.Weapons["pirate_blaster"] == 1
                && card.Arenas["mp1_sanctorus"] == 1 && card.Places[HubCategory.Points] == 1 && card.Totals[HubCategory.Points] == 8
                && card.Totals[HubCategory.RankPoints] == 4, "hub: a player's stats card (Overall 8 = 1st, star points 4 = +2 over two)");
            StatsMsg doctored = await g2.RequestAsync<StatsMsg>(new StatsReq());
            Check(doctored.Code == g2.Me.Code && doctored.Totals[Store.Kills] == 20 && doctored.Totals[HubCategory.Headshots] == 20
                && doctored.Totals[HubCategory.Points] == 7
                && doctored.Totals[Store.Shots] == 4 * 3600 * 60
                && doctored.Weapons.Count == 0, "hub: a doctored report is clamped (own card, Code empty)");
            Check(await ErrorOf(() => s.RequestAsync<StatsMsg>(new StatsReq { Code = "000000000000" })) == "not_found",
                "hub: no card for an unknown code");

            // nine more matches, the host winning every one: win rate from 10 games, the run of wins
            for (int i = 0; i < 9; i++)
            {
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = false });
                await h.RequestAsync<OkMsg>(new StartGameReq { Started = true });
                await h.RequestAsync<OkMsg>(Result(id, (h, 0), (g1, 1), (g2, 1)));
            }
            LeadersMsg rate = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.WinRate });
            LeadersMsg run = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.WinStreak });
            LeadersMsg games = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Games });
            Check(rate.Rows.First() is { Place: 1, Value: 1000 } && rate.Rows.First().Code == h.Me.Code
                && run.Rows.Single().Value == 10 && games.Rows.Count == 3 && games.Rows.All(x => x is { Place: 1, Value: 10 })
                && rate.Mine == null, "hub: boards (win rate 100.0%, 10 wins in a row, a tie shares 1st)");
            // the host after 10 wins: 40 + 40 + 0 + 0 + 10 Overall, 40 star points = super hunter (rank 1) on the row
            LeadersMsg overall = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Points });
            Check(overall.Rows.FirstOrDefault(x => x.Code == h.Me.Code) is { Value: 90, Rank: 1 }, "hub: Overall Points + rank on a row");

            // friends only: the stranger and a friend; the asker's own place under the board
            await s.RequestAsync<OkMsg>(new AddReq { Code = g2.Me.Code, Kind = LinkKind.Friend });
            await g2.RequestAsync<OkMsg>(new AddReq { Code = s.Me.Code, Kind = LinkKind.Friend });
            LeadersMsg friends = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Games, Scope = LeadersReq.Friends });
            LeadersMsg rivals = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Games, Scope = LeadersReq.Rivals });
            LeadersMsg mine = await g2.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Points, Scope = LeadersReq.Friends });
            Check(friends.Rows.Single().Code == g2.Me.Code && rivals.Rows.Count == 0 && mine.Mine?.Code == g2.Me.Code,
                "hub: friends / rivals boards");
            Check(await ErrorOf(() => s.RequestAsync<LeadersMsg>(new LeadersReq { Category = "nope" })) == "bad_board",
                "hub: no such board");

            // LAN never counts
            var lan = Result("", (s, 0), (h, 1));
            lan.Lan = true;
            await s.RequestAsync<OkMsg>(lan);
            Check((await s.RequestAsync<StatsMsg>(new StatsReq())).Totals.Count == 0, "hub: a LAN match isn't counted");

            // a deleted profile leaves the boards and the hunter count
            await g1.RequestAsync<OkMsg>(new DeleteProfileReq());
            LeadersMsg after = await s.RequestAsync<LeadersMsg>(new LeadersReq { Category = HubCategory.Games });
            Check(after.Rows.Count == 2 && after.Rows.All(x => x.Code != g1.Me.Code)
                && (await s.RequestAsync<HubMsg>(new HubReq())).Players == 2, "hub: a deleted profile's stats go with it");

            foreach (OnlineClient c in new[] { h, g1, g2, s })
            {
                await c.DisposeAsync();
            }
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
