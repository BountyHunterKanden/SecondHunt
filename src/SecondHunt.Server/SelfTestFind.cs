using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // O6 Find Game: every queue rule as a plain function (FindRules, made-up clocks and tickets), then real clients on a real
    // server at test speed (FindConfig.Fast): the 4-player match with a party, the vote, the host, REGION widening, the fill
    // wait, mode / version / block separation, cancel, a player leaving, a host that never opens the game, joining an open game.
    public static partial class SelfTest
    {
        private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        // a search that has waited `waited` seconds at T0; members are id_0, id_1, ...
        private static FindTicket Tk(string id, int ping = 50, double waited = 0, int size = 1, bool nearby = true, string[]? modes = null,
            string version = "v", Dictionary<string, string[]>? arenas = null) => new()
        {
            Id = id, Members = Enumerable.Range(0, size).Select(i => $"{id}_{i}").ToList(), Modes = (modes ?? FindMode.All).ToHashSet(),
            Nearby = nearby, Version = version, Since = T0.AddSeconds(-waited), PingMs = ping,
            Arenas = (arenas ?? new()).ToDictionary(a => a.Key, a => a.Value.ToHashSet()),
        };

        private static void FindRulesTests()
        {
            var c = new FindConfig { MaxPlayers = 4 }; // (the rules at vanilla's 4; P8's 8 below)
            static bool NoBlocks(string a, string b) => false;

            Check(FindRules.Limit(c, true, 0) == 120 && FindRules.Limit(c, true, 9.9) == 120 && FindRules.Limit(c, true, 10) == 160
                && FindRules.Limit(c, true, 25) == 200 && FindRules.Limit(c, true, 59) == 320 && FindRules.Limit(c, true, 60) == int.MaxValue
                && FindRules.Limit(c, false, 0) == int.MaxValue, "find: NEARBY limit 120 ms, +40 every 10 s, none after 60 s; WORLDWIDE none");
            Check(FindRules.PingOk(c, Tk("a", 50), Tk("b", 70), T0) && !FindRules.PingOk(c, Tk("a", 50), Tk("b", 80), T0)
                && FindRules.PingOk(c, Tk("a", 80, 10), Tk("b", 80, 10), T0), "find: two pings fit when their sum is within the limit, which widens with the wait");
            Check(!FindRules.PingOk(c, Tk("a", 50), Tk("b", 200, 60), T0) && FindRules.PingOk(c, Tk("a", 50, 60), Tk("b", 200, 60), T0)
                && !FindRules.PingOk(c, Tk("a", 50), Tk("b", 500, nearby: false), T0) && FindRules.PingOk(c, Tk("a", 400, nearby: false),
                Tk("b", 500, nearby: false), T0), "find: the stricter search's limit counts; two WORLDWIDE searches always fit");

            var both = FindRules.CommonModes(new[] { Tk("a", modes: new[] { "Battle", "Bounty" }), Tk("b", modes: new[] { "Bounty", "Capture" }) });
            var any = FindRules.CommonModes(new[] { Tk("a"), Tk("b", modes: new[] { "Battle" }) });
            var none = FindRules.CommonModes(new[] { Tk("a", modes: new[] { "Battle" }), Tk("b", modes: new[] { "Bounty" }) });
            Check(both.Keys.SequenceEqual(new[] { "Bounty" }) && any.Keys.SequenceEqual(new[] { "Battle" }) && none.Count == 0
                && FindRules.CommonModes(new[] { Tk("a"), Tk("b") }).Count == 7, "find: the modes a group shares (ANY = all seven)");
            var arenaA = new Dictionary<string, string[]> { ["Battle"] = new[] { "x", "y" } };
            var arenaB = new Dictionary<string, string[]> { ["Battle"] = new[] { "y", "z" } };
            var arenaC = new Dictionary<string, string[]> { ["Battle"] = new[] { "z" } };
            var shared = FindRules.CommonModes(new[] { Tk("a", arenas: arenaA), Tk("b", arenas: arenaB) });
            var open = FindRules.CommonModes(new[] { Tk("a", arenas: arenaA), Tk("b") });
            var disjoint = FindRules.CommonModes(new[] { Tk("a", arenas: arenaA), Tk("c", arenas: arenaC) });
            Check(shared["Battle"]!.SequenceEqual(new[] { "y" }) && open["Battle"]!.SequenceEqual(new[] { "x", "y" }) && open["Bounty"] == null
                && !disjoint.ContainsKey("Battle") && disjoint.ContainsKey("Bounty"),
                "find: arenas are the ones every device has; none in common takes the mode out; unlisted = any arena");

            // P8 (release 0.2.0 F3): a match holds 8 by default
            var c8 = new FindConfig();
            FindTicket[] nine = Enumerable.Range(0, 9).Select(i => Tk("n" + i, waited: 9 - i)).ToArray();
            List<FindTicket>? g8 = FindRules.Form(c8, nine, T0, NoBlocks);
            Check(c8.MaxPlayers == 8 && g8?.Count == 8 && g8.All(t => t.Id != "n8")
                && FindRules.Form(c8, nine.Skip(5).ToList(), T0, NoBlocks) == null
                && FindRules.Form(c8, new[] { Tk("a", waited: 15), Tk("b"), Tk("c"), Tk("d") }, T0, NoBlocks)?.Count == 4,
                "find (8): eight searches match at once (nine: the eight longest waiting); four wait until 15 s, then play");
            Check(FindRules.Form(c8, new[] { Tk("p", size: 4) }, T0, NoBlocks) == null
                && FindRules.Form(c8, new[] { Tk("p", size: 4, waited: 15) }, T0, NoBlocks)?.Count == 1
                && FindRules.Form(c8, new[] { Tk("p", size: 3, waited: 100) }, T0, NoBlocks) == null
                && FindRules.Form(c8, new[] { Tk("p", size: 4, waited: 5), Tk("q", size: 4) }, T0, NoBlocks)?.Count == 2,
                "find (8): a full party of four plays alone after 15 s, a short one never; two parties of four fill a match at once");

            // forming a match
            var four = new[] { Tk("a", waited: 3), Tk("b", waited: 2), Tk("c", waited: 1), Tk("d") };
            Check(FindRules.Form(c, four, T0, NoBlocks)?.Count == 4, "find: four fitting searches match at once");
            Check(FindRules.Form(c, four.Take(3).ToList(), T0, NoBlocks) == null
                && FindRules.Form(c, new[] { Tk("a", waited: 14.9), Tk("b"), Tk("c") }, T0, NoBlocks) == null
                && FindRules.Form(c, new[] { Tk("a", waited: 15), Tk("b"), Tk("c") }, T0, NoBlocks)?.Count == 3,
                "find: fewer than four wait until the oldest search has waited 15 s");
            Check(FindRules.Form(c, new[] { Tk("a", waited: 100) }, T0, NoBlocks) == null
                && FindRules.Form(c, new[] { Tk("p", size: 3, waited: 100) }, T0, NoBlocks) == null
                && FindRules.Form(c, new[] { Tk("p", size: 4) }, T0, NoBlocks)?.Count == 1
                && FindRules.Form(c, new[] { Tk("p", size: 2, waited: 20), Tk("q", size: 2, waited: 20) }, T0, NoBlocks)?.Count == 2,
                "find: one player or a short party alone never matches; a full party does; two parties of two do");
            FindTicket[] five = { Tk("a", waited: 5), Tk("b", waited: 4), Tk("c", waited: 3), Tk("d", waited: 2), Tk("e", waited: 1) };
            List<FindTicket>? g5 = FindRules.Form(c, five, T0, NoBlocks);
            Check(g5 != null && g5.Count == 4 && g5.Select(t => t.Id).SequenceEqual(new[] { "a", "b", "c", "d" }),
                "find: five searches make one match of the four longest waiting");
            List<FindTicket>? party = FindRules.Form(c, new[] { Tk("p", size: 2, waited: 5), Tk("s1", waited: 4), Tk("s2", waited: 3) }, T0, NoBlocks);
            Check(party?.Count == 3 && party.Sum(t => t.Size) == 4, "find: a party of two counts as two players");
            List<FindTicket>? party3 = FindRules.Form(c, new[] { Tk("p", size: 3, waited: 5), Tk("s1", waited: 4), Tk("s2", waited: 3) }, T0, NoBlocks);
            Check(party3 != null && party3.Sum(t => t.Size) == 4 && party3.Any(t => t.Id == "s1") && party3.All(t => t.Id != "s2"),
                "find: a party of three and two singles make four, the longer waiting single first");
            // a search is never split: a party of three can't join three others (6 > 4 never) and waits for one
            Check(FindRules.Form(c, new[] { Tk("p", size: 3, waited: 20), Tk("q", size: 3, waited: 19) }, T0, NoBlocks) == null,
                "find: two parties of three don't fit one match of four");

            bool Blocked(string a, string b) => (a == "a_0" && b == "b_0") || (a == "b_0" && b == "a_0");
            List<FindTicket>? blocked = FindRules.Form(c, new[] { Tk("a", waited: 20), Tk("b", waited: 19), Tk("c", waited: 18) }, T0, Blocked);
            Check(blocked != null && blocked.Count == 2 && !(blocked.Any(t => t.Id == "a") && blocked.Any(t => t.Id == "b")),
                "find: two players who blocked each other are never in one match");
            List<FindTicket>? versions = FindRules.Form(c, new[] { Tk("a", waited: 20), Tk("b", waited: 19, version: "w"), Tk("c", waited: 18),
                Tk("d", waited: 17, version: "w") }, T0, NoBlocks);
            Check(versions != null && versions.Select(t => t.Id).SequenceEqual(new[] { "a", "c" }), "find: only the same game version match");
            var far = new[] { Tk("a", 150, 20), Tk("b", 150, 20) };
            Check(FindRules.Form(c, far, T0, NoBlocks) == null && FindRules.Form(c, far.Select(t => Tk(t.Id, 150, 60)).ToList(), T0, NoBlocks)?.Count == 2
                && FindRules.Form(c, far.Select(t => Tk(t.Id, 150, 20, nearby: false)).ToList(), T0, NoBlocks)?.Count == 2,
                "find: two distant NEARBY searches wait for the limit to widen; WORLDWIDE ones don't");
            Check(FindRules.Form(c, new[] { Tk("a", waited: 20, modes: new[] { "Battle" }), Tk("b", waited: 20, modes: new[] { "Bounty" }) }, T0, NoBlocks) == null
                && FindRules.Form(c, new[] { Tk("a", waited: 20, modes: new[] { "Battle" }), Tk("b", waited: 20) }, T0, NoBlocks)?.Count == 2,
                "find: different modes don't match, ANY matches a mode");

            // host and vote
            var pings = new Dictionary<string, int> { ["a"] = 90, ["b"] = 30, ["c"] = 30 };
            Check(FindRules.PickHost(new[] { "a", "b", "c" }, p => pings[p]) == "b" && FindRules.PickHost(new[] { "c", "b", "a" }, p => pings[p]) == "c",
                "find: the host is the lowest round trip, a tie goes to the longer-waiting player");
            string[] ballot = { "x", "y", "z" };
            var rng = new Random(7);
            bool plurality = Enumerable.Range(0, 40).All(_ => FindRules.Tally(ballot, new Dictionary<string, string> { ["a"] = "x", ["b"] = "x", ["c"] = "y" }, rng) == "x");
            var tie = Enumerable.Range(0, 60).Select(_ => FindRules.Tally(ballot, new Dictionary<string, string> { ["a"] = "x", ["b"] = "y" }, rng)).ToHashSet();
            var nobody = Enumerable.Range(0, 90).Select(_ => FindRules.Tally(ballot, new Dictionary<string, string>(), rng)).ToHashSet();
            Check(plurality && tie.SetEquals(new[] { "x", "y" }) && nobody.SetEquals(ballot) && FindRules.Tally(Array.Empty<string>(),
                new Dictionary<string, string>(), rng) == "", "find: the vote: most votes wins, a tie is drawn between the tied, no votes = any, no options = the host picks");

            // joining an open game instead
            FindTicket two = Tk("t", 50, size: 2, modes: new[] { "Battle" });
            Check(FindRules.GameFits(c, two, "Battle", "v", GameInfo.NoMods, 2, 40, T0) && FindRules.GameFits(c, two, "BattleTeams", "v", GameInfo.NoMods, 2, 40, T0)
                && !FindRules.GameFits(c, two, "Battle", "v", GameInfo.NoMods, 1, 40, T0) && !FindRules.GameFits(c, two, "Battle", "v", "abc", 2, 40, T0)
                && !FindRules.GameFits(c, two, "Battle", "w", GameInfo.NoMods, 2, 40, T0) && !FindRules.GameFits(c, two, "Bounty", "v", GameInfo.NoMods, 2, 40, T0)
                && !FindRules.GameFits(c, two, "Battle", "v", GameInfo.NoMods, 2, 100, T0), "find: an open game fits a search of that mode, version, size and route, without mods");

            // ONLINE SETTINGS' extra filters (0.6.0)
            GameInfo all = new(), pickupsOnly = new() { ExtraCharacters = false }, vanilla = new() { ExtraPickups = false, ExtraCharacters = false };
            FindTicket noPickups = new() { Id = "np", NoExtraPickups = true }, noChars = new() { Id = "nc", NoExtraCharacters = true };
            Check(FindRules.ExtrasFit(Tk("any"), all) && !FindRules.ExtrasFit(noPickups, all) && !FindRules.ExtrasFit(noPickups, pickupsOnly)
                && FindRules.ExtrasFit(noPickups, vanilla) && !FindRules.ExtrasFit(noChars, all) && FindRules.ExtrasFit(noChars, pickupsOnly)
                && FindRules.ExtrasFit(noChars, vanilla), "find: a search filtering extra pickups / characters skips open games that allow them");

            // the QUICK PLAY playlist (N17): any number of modes ticked, a group plays one they all ticked
            var playlist = FindRules.CommonModes(new[] { Tk("a", modes: new[] { "Battle", "Survival" }), Tk("b", modes: new[] { "Survival", "Nodes" }) });
            Check(playlist.Keys.SequenceEqual(new[] { "Survival" }) && FindRules.Form(c, new[] { Tk("a", waited: 20, modes: new[] { "Battle", "Survival" }),
                Tk("b", waited: 20, modes: new[] { "Survival", "Nodes" }) }, T0, NoBlocks)?.Count == 2,
                "find: two playlists match on the modes both ticked");
            // an open game in an imported arena (Echoes, N28) takes a search only when the search has that arena for the mode
            GameInfo echoes = new() { Mode = "Battle", Arena = "ECHOES SPIRES", ExtraArena = true };
            var hasIt = new Dictionary<string, string[]> { ["Battle"] = new[] { "Combat Hall", "ECHOES SPIRES" } };
            var lacksIt = new Dictionary<string, string[]> { ["Battle"] = new[] { "Combat Hall" } };
            Check(FindRules.ExtrasFit(Tk("h", arenas: hasIt), echoes) && !FindRules.ExtrasFit(Tk("l", arenas: lacksIt), echoes)
                && !FindRules.ExtrasFit(Tk("u"), echoes) && FindRules.ExtrasFit(Tk("l", arenas: lacksIt), new GameInfo { Mode = "Battle", Arena = "Combat Hall" }),
                "find: only searches with the imported arena go into a game in it (no list = none)");
        }

        private static FindStartReq Req(string version, string[] modes, bool nearby, int ping, Dictionary<string, List<string>>? arenas = null) => new()
        {
            Version = version, Modes = modes.ToList(), Nearby = nearby, PingMs = ping, Arenas = arenas ?? new(),
        };

        private static async Task<List<OnlineClient>> Players(Uri uri, params string[] names)
        {
            var list = new List<OnlineClient>();
            foreach (string n in names)
            {
                list.Add(await Connect(uri, OnlineIdentity.Create(), n));
            }
            return list;
        }

        private static async Task Close(IEnumerable<OnlineClient> clients)
        {
            foreach (OnlineClient c in clients)
            {
                await c.DisposeAsync();
            }
        }

        private static async Task FindGame(string db)
        {
            FindConfig cfg = FindConfig.Fast;
            (WebApplication app, Uri uri) = await Start(db, cfg);
            const string v = "rom1/sim3";
            string[] battle = { "Battle" };

            // ---- the main case: a party of two and two singles, four players, the vote, the host ----
            {
                List<OnlineClient> p = await Players(uri, "Lead", "Mate", "Solo1", "Solo2", "Outsider");
                OnlineClient lead = p[0], mate = p[1], c = p[2], d = p[3], outsider = p[4];
                await Friends(lead, mate);
                var invite = Next<PartyInviteEvent>(mate);
                await lead.RequestAsync<OkMsg>(new PartyInviteReq { Code = mate.Me.Code });
                await mate.RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = (await invite)!.PartyId });
                await lead.RequestAsync<OkMsg>(new PingReq { Ms = 60 });
                await mate.RequestAsync<OkMsg>(new PingReq { Ms = 30 }); // the party member has the best connection
                var arenas = (params string[] a) => new Dictionary<string, List<string>> { ["Battle"] = a.ToList() };

                Check(await ErrorOf(() => mate.RequestAsync<OkMsg>(Req(v, battle, false, 30))) == "not_leader", "find: a party member can't start the party's search");
                Check(await ErrorOf(() => lead.RequestAsync<OkMsg>(Req("", battle, false, 60))) == "version"
                    && await ErrorOf(() => lead.RequestAsync<OkMsg>(Req(v, new[] { "Chess" }, false, 60))) == "bad_mode", "find: a search needs a version and real modes");

                var votes = new[] { lead, mate, c, d }.Select(x => Next<FindEvent>(x, e => e.State == FindState.Vote, 5000)).ToList();
                await c.RequestAsync<OkMsg>(Req(v, battle, false, 45, arenas("Shrine", "Fiend", "Vault", "Rally", "Sanctuary")));
                Check(await ErrorOf(() => c.RequestAsync<OkMsg>(Req(v, battle, false, 45))) == "searching", "find: already searching");
                await d.RequestAsync<OkMsg>(Req(v, battle, false, 90, arenas("Fiend", "Vault", "Rally", "Lab")));
                await lead.RequestAsync<OkMsg>(Req(v, battle, false, 60, arenas("Hall", "Shrine", "Fiend", "Vault", "Rally")));
                FindEvent?[] ev = await Task.WhenAll(votes);
                Check(ev.All(e => e != null) && ev.Select(e => e!.FindId).Distinct().Count() == 1 && ev.All(e => e!.Players.Count == 4 && e.Mode == "Battle"),
                    "find: the party and two singles became one match of four, all told");
                Check(ev[0]!.Arenas.ToHashSet().SetEquals(new[] { "Fiend", "Vault", "Rally" }) && ev.All(e => e!.Arenas.SequenceEqual(ev[0]!.Arenas)),
                    "find: the ballot holds the arenas every device (the party leader's list for the party) has");
                Check(ev[2]!.Players.Any(x => x.Code == lead.Me.Code) && await ErrorOf(() => c.RequestAsync<OkMsg>(Req(v, battle, false, 45))) == "searching",
                    "find: the match lists everyone; matched players aren't searching twice");
                Check(await ErrorOf(() => c.RequestAsync<OkMsg>(new FindVoteReq { Arena = "Hall" })) == "bad_arena", "find: only a ballot arena can be voted for");

                string findId = ev[0]!.FindId;
                var hostEv = Next<FindEvent>(mate, e => e.State == FindState.Host);
                var joinEvs = new[] { lead, c, d }.Select(x => Next<FindEvent>(x, e => e.State == FindState.Join)).ToList();
                await lead.RequestAsync<OkMsg>(new FindVoteReq { Arena = "Fiend" });
                await mate.RequestAsync<OkMsg>(new FindVoteReq { Arena = "Fiend" });
                await c.RequestAsync<OkMsg>(new FindVoteReq { Arena = "Vault" });
                await d.RequestAsync<OkMsg>(new FindVoteReq { Arena = "Vault" });
                FindEvent? hosting = await hostEv;
                FindEvent?[] joining = await Task.WhenAll(joinEvs);
                string arena = hosting?.Arena ?? "";
                Check(hosting != null && (arena is "Fiend" or "Vault") && joining.All(e => e != null && e.Arena == arena && e.HostCard?.Code == mate.Me.Code),
                    $"find: all four voted (a 2-2 tie drawn: {arena}); the player with the best connection hosts, not the party leader");
                Check(await ErrorOf(() => c.RequestAsync<OkMsg>(new FindVoteReq { Arena = arena })) == "no_vote", "find: no voting after the vote");

                // the host opens the game
                GameInfo Asked(string a) => new() { Mode = "Battle", Arena = a, MaxPlayers = 4, Public = true, Version = v };
                Check(await ErrorOf(() => mate.RequestAsync<GameMsg>(new HostGameReq { Info = Asked("Hall") })) == "bad_arena"
                    && await ErrorOf(() => c.RequestAsync<GameMsg>(new HostGameReq { Info = Asked(arena), Find = findId })) == "not_found"
                    && await ErrorOf(() => mate.RequestAsync<GameMsg>(new HostGameReq { Info = new GameInfo { Mode = "Bounty", Arena = arena, Version = v }, Find = findId })) == "bad_mode"
                    && await ErrorOf(() => mate.RequestAsync<GameMsg>(new HostGameReq { Info = new GameInfo { Mode = "Battle", Arena = arena, Version = "x" }, Find = findId })) == "version",
                    "find: only the chosen host opens the match's game, with its mode, arena and version");
                var games = new[] { lead, c, d }.Select(x => Next<FindEvent>(x, e => e.State == FindState.Game)).ToList();
                GameMsg game = await mate.RequestAsync<GameMsg>(new HostGameReq { Info = Asked(arena) }); // (no Find: the chosen host's host_game is the match's)
                FindEvent?[] told = await Task.WhenAll(games);
                Check(game.Game.Info is { Public: false, MaxPlayers: 4 } && told.All(e => e != null && e.GameId == game.Game.Id && e.HostCard?.Code == mate.Me.Code),
                    "find: the game is the match's (private, four seats); the other three are told where to join");
                bool outsiderSees = (await outsider.RequestAsync<GamesMsg>(new ListGamesReq())).Games.Any(g => g.Id == game.Game.Id);
                Check(!outsiderSees && await ErrorOf(() => outsider.RequestAsync<GameMsg>(new JoinGameReq { GameId = game.Game.Id, Version = v })) == "not_found",
                    "find: nobody outside the match sees or joins its game");
                var slots = new List<int>();
                foreach (OnlineClient x in new[] { lead, c, d })
                {
                    slots.Add((await x.RequestAsync<GameMsg>(new JoinGameReq { GameId = game.Game.Id, Version = v })).Relay.Slot);
                }
                Check(slots.OrderBy(s => s).SequenceEqual(new[] { 1, 2, 3 }), "find: the three join the host's game on relay slots 1-3");
                Check(await ErrorOf(() => c.RequestAsync<OkMsg>(Req(v, battle, false, 45))) == "in_game", "find: a player in a game can't search");
                await Close(p);
            }

            // ---- REGION: NEARBY waits for the limit to widen; the fill wait; separation ----
            {
                List<OnlineClient> p = await Players(uri, "FarA", "FarB");
                var ballot = new Dictionary<string, List<string>> { ["Battle"] = new() { "Fiend", "Vault" } };
                var early = Next<FindEvent>(p[0], e => e.State == FindState.Vote, 3500);
                var first = Next<FindEvent>(p[0], e => e.State == FindState.Searching);
                await p[0].RequestAsync<OkMsg>(Req(v, battle, true, 150, ballot));
                await p[1].RequestAsync<OkMsg>(Req(v, battle, true, 150, ballot));
                FindEvent? s1 = await first;
                Check(await early == null, "find: NEARBY: two players 300 ms apart through the server aren't matched at first");
                var later = Next<FindEvent>(p[0], e => e.State == FindState.Searching && e.LimitMs > (s1?.LimitMs ?? 0), 4000);
                var vote = Next<FindEvent>(p[1], e => e.State == FindState.Vote, 6000);
                Check(s1 is { Nearby: true, LimitMs: > 0 } && (await later)?.LimitMs > s1.LimitMs && await vote != null,
                    $"find: NEARBY: the limit widens ({s1?.LimitMs} ms and up) until they fit; progress says so");
                var cancelled = Next<FindEvent>(p[0], e => e.State == FindState.Cancelled);
                var again = Next<FindEvent>(p[1], e => e.State == FindState.Searching && e.Reason == "match");
                await p[0].RequestAsync<OkMsg>(new FindCancelReq());
                FindEvent? c0 = await cancelled;
                Check(c0?.Reason == "you" && (await again) != null, "find: cancelling in a match: you're out, the other player searches again");
                Check((await p[1].RequestAsync<OkMsg>(new FindCancelReq())).Done, "find: cancel ends a search");
                await Close(p);
            }
            {
                List<OnlineClient> p = await Players(uri, "NearA", "NearB");
                var early = Next<FindEvent>(p[0], e => e.State == FindState.Vote, 1000);
                var ballot = new Dictionary<string, List<string>> { ["Battle"] = new() { "Fiend", "Vault" } };
                await p[0].RequestAsync<OkMsg>(Req(v, battle, true, 40, ballot));
                await p[1].RequestAsync<OkMsg>(Req(v, battle, true, 40, ballot));
                Check(await early == null, "find: a pair waits for more players first (the fill wait)");
                var vote = Next<FindEvent>(p[0], e => e.State == FindState.Vote, 3000);
                Check(await vote != null, "find: a nearby pair matches once the fill wait is over");
                await Close(p);
            }
            {
                List<OnlineClient> p = await Players(uri, "Battler", "Bounty", "OtherVersion", "Blocker", "Blocked");
                await p[3].RequestAsync<OkMsg>(new BlockReq { Code = p[4].Me.Code });
                var any = p.Select(x => Next<FindEvent>(x, e => e.State == FindState.Vote, 3800)).ToList();
                await p[0].RequestAsync<OkMsg>(Req(v, battle, false, 40));
                await p[1].RequestAsync<OkMsg>(Req(v, new[] { "Bounty" }, false, 40));
                await p[2].RequestAsync<OkMsg>(Req("rom9/sim9", battle, false, 40));
                await p[3].RequestAsync<OkMsg>(Req(v, new[] { "Survival" }, false, 40));
                await p[4].RequestAsync<OkMsg>(Req(v, new[] { "Survival" }, false, 40));
                Check((await Task.WhenAll(any)).All(e => e == null), "find: other modes, another game version and a block keep players apart, however long they wait");
                Check((await p[3].RequestAsync<OkMsg>(new FindCancelReq())).Done && (await p[0].RequestAsync<OkMsg>(new FindCancelReq())).Done
                    && !(await p[0].RequestAsync<OkMsg>(new FindCancelReq())).Done, "find: cancel says whether there was a search");
                await Close(p);
            }

            // ---- a party's search ends with the party; a party member in a game blocks it ----
            {
                List<OnlineClient> p = await Players(uri, "PLead", "PMate");
                await Friends(p[0], p[1]);
                var invite = Next<PartyInviteEvent>(p[1]);
                await p[0].RequestAsync<OkMsg>(new PartyInviteReq { Code = p[1].Me.Code });
                await p[1].RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = (await invite)!.PartyId });
                var mateSearching = Next<FindEvent>(p[1], e => e.State == FindState.Searching);
                await p[0].RequestAsync<OkMsg>(Req(v, battle, false, 40));
                Check(await mateSearching != null, "find: a party's search is the whole party's: the member hears the progress");
                var leadCancelled = Next<FindEvent>(p[0], e => e.State == FindState.Cancelled);
                var mateCancelled = Next<FindEvent>(p[1], e => e.State == FindState.Cancelled);
                await p[1].RequestAsync<OkMsg>(new PartyLeaveReq());
                Check((await leadCancelled)?.Reason == "party" && (await mateCancelled)?.Reason == "party", "find: a member leaving the party ends the party's search for both");
                var invite2 = Next<PartyInviteEvent>(p[1]);
                await p[0].RequestAsync<OkMsg>(new PartyInviteReq { Code = p[1].Me.Code });
                await p[1].RequestAsync<PartyMsg>(new PartyJoinReq { PartyId = (await invite2)!.PartyId });
                await p[1].RequestAsync<GameMsg>(new HostGameReq { Info = Info(pub: false) });
                Check(await ErrorOf(() => p[0].RequestAsync<OkMsg>(Req(v, battle, false, 40))) == "party_busy", "find: a party can't search while a member is in a game");
                await Close(p);
            }

            // ---- a player leaves the vote; the chosen host never opens the game ----
            {
                List<OnlineClient> p = await Players(uri, "Fast", "Mid", "Slow");
                await p[0].RequestAsync<OkMsg>(new PingReq { Ms = 20 });
                await p[1].RequestAsync<OkMsg>(new PingReq { Ms = 40 });
                await p[2].RequestAsync<OkMsg>(new PingReq { Ms = 60 });
                var arenas = new Dictionary<string, List<string>> { ["Battle"] = new() { "Fiend", "Vault" } };
                var votes = p.Select(x => Next<FindEvent>(x, e => e.State == FindState.Vote, 5000)).ToList();
                foreach (OnlineClient x in p)
                {
                    await x.RequestAsync<OkMsg>(Req(v, battle, false, 0, arenas));
                }
                FindEvent?[] ev = await Task.WhenAll(votes);
                Check(ev.All(e => e != null && e.Players.Count == 3), "find: three players match after the fill wait");
                var hostEv = Next<FindEvent>(p[0], e => e.State == FindState.Host);
                var joinEv = Next<FindEvent>(p[1], e => e.State == FindState.Join);
                await p[2].DisposeAsync(); // the slowest player's connection drops during the vote
                await p[0].RequestAsync<OkMsg>(new FindVoteReq { Arena = "Fiend" });
                await p[1].RequestAsync<OkMsg>(new FindVoteReq { Arena = "Fiend" });
                FindEvent? h = await hostEv;
                Check(h is { Arena: "Fiend" } && h.Players.Count == 2 && (await joinEv)?.HostCard?.Code == p[0].Me.Code,
                    "find: a player who drops out of the vote is gone; the others vote and the best connection hosts");
                // nobody opens the game: the host's search ends, the other one is back in the queue where they were
                var hostGone = Next<FindEvent>(p[0], e => e.State == FindState.Cancelled, 7000);
                var back = Next<FindEvent>(p[1], e => e.State == FindState.Searching && e.Reason == "match", 7000);
                Check((await hostGone)?.Reason == "host" && await back != null, "find: a host who never opens the game is dropped; the others search again");
                await p[1].RequestAsync<OkMsg>(new FindCancelReq());
                await Close(new[] { p[0], p[1] });
            }

            // ---- an open public game is joined instead of queueing ----
            {
                List<OnlineClient> p = await Players(uri, "Lobby", "Quick", "BountyFan", "Late");
                await p[0].RequestAsync<GameMsg>(new HostGameReq { Info = new GameInfo { Mode = "Battle", Arena = "Fiend", MaxPlayers = 2, Public = true, Version = v } });
                var fan = Next<FindEvent>(p[2], e => true, 1500);
                var quick = Next<FindEvent>(p[1], e => true, 2000);
                await p[1].RequestAsync<OkMsg>(Req(v, battle, false, 40));
                await p[2].RequestAsync<OkMsg>(Req(v, new[] { "Bounty" }, false, 40));
                FindEvent? q = await quick;
                GameMsg joined = await p[1].RequestAsync<GameMsg>(new JoinGameReq { GameId = q?.GameId ?? "", Version = v });
                Check(q is { State: FindState.Game } && joined.Relay.Slot == 1, "find: a search that fits an open public game is sent straight into it");
                Check((await fan)?.State is not FindState.Game, "find: another mode isn't");
                var late = Next<FindEvent>(p[3], e => e.State == FindState.Game, 1200);
                await p[3].RequestAsync<OkMsg>(Req(v, battle, false, 40));
                Check(await late == null, "find: a full game isn't offered");
                await p[2].RequestAsync<OkMsg>(new FindCancelReq());
                await p[3].RequestAsync<OkMsg>(new FindCancelReq());
                await Close(p);
            }

            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
