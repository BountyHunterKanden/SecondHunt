using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace MphRead
{
    // -onlinetest served=hub (docs/DEDICATED_SERVERS.md D3): the whole served path. The local server runs with --matchhost
    // (SecondHuntMatchHost, built to SecondHunt.MatchHost/bin/Served/net9.0, or SECONDHUNT_MATCHHOST); joiner 1 opens a
    // served game and owns its lobby (-onlinejoin owner=: rules + bots by OWNER SETTINGS, then START once everyone's in),
    // joiners 2..players= join it; the match runs in the match host process. PASS = every device played to the end with
    // the same scoreboard, the server says the game was served and took the match host's result.
    internal static partial class OnlineMatchTool
    {
        internal static string MatchHostExe() => Environment.GetEnvironmentVariable("SECONDHUNT_MATCHHOST") is { Length: > 0 } exe
            ? Path.GetFullPath(exe)
            : Path.GetFullPath(Path.Combine(typeof(OnlineMatchTool).Assembly.Location, "..", "..", "..", "..", "..",
                "SecondHunt.MatchHost", "bin", "Served", "net9.0", OperatingSystem.IsWindows() ? "SecondHuntMatchHost.exe" : "SecondHuntMatchHost"));

        // the match host's game files: this folder's paths.txt, or SECONDHUNT_MATCHHOST_DATA (a folder with its own paths.txt,
        // e.g. the pruned copy deploy.ps1 -GameData uploads)
        internal static string MatchHostData() => Environment.GetEnvironmentVariable("SECONDHUNT_MATCHHOST_DATA") is { Length: > 0 } dir
            ? Path.GetFullPath(dir) : Directory.GetCurrentDirectory();

        // SECONDHUNT_IMPORTS=<dir> (an in-app import's folder: arenas/<Key>/ + import/mp2.json, e.g. -importcheck arenas all
        // --out <dir>): this test, its joiners and the local server's match host all play the imported Echoes arenas from it,
        // as devices and the live server do, instead of the PC's staged copies
        private static string? Imports() => Environment.GetEnvironmentVariable("SECONDHUNT_IMPORTS") is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;

        internal static void UseImports()
        {
            if (Imports() is string dir)
            {
                MphRecomp.Arenas.EchoesArena.ImportRoot = dir;
            }
        }

        internal static string ImportsArg() => Imports() is string dir ? $" --matchhost-args \"--imports {dir}\"" : "";

        private static void RunServedHub(Options o, Uri uri, List<(Process, List<string>)> joiners)
        {
            string dll = typeof(OnlineMatchTool).Assembly.Location;
            string Args(int i, string game) => $"\"{dll}\" -onlinejoin server={uri} index={i} hunter={JoinerHunters[(i - 1) % JoinerHunters.Length]} "
                + $"time={o.Time.ToString(CultureInfo.InvariantCulture)} leave={(i == o.Players ? o.Leave : 0).ToString(CultureInfo.InvariantCulture)}"
                + (i == 1 ? $" owner={o.Players} bots={o.Bots} level={o.Level} mode=\"{o.Mode}\" arena=\"{o.Arena}\"" : $" game={game}")
                + (o.Loss > 0 ? $" loss={(o.Loss * 100).ToString(CultureInfo.InvariantCulture)}" : "")
                + (o.Jitter > 0 ? $" jitter={o.Jitter}" : "")
                + (o.Replays != false ? " replays=1" : "")
                + (o.Ball ? " ball=1" : "") + (o.AltFixOff ? " altfix=0" : "") + (o.SmoothOff ? " smooth=0" : "") + o.Fwd
                + (!MphRecomp.Multiplayer.Net.ClientPrediction.ClientPushes ? " clientpush=0" : "")
                + (o.Duel ? (i == 1 ? " duel=runner" : " duel=shooter") + (o.Push ? " push=1" : "") : "") // (the match host's recording to each joiner, OnlineServedReplay.cs)
                + (o.Matches > 1 ? $" matches={o.Matches}" + (o.OwnerLeave ? " ownerleave=1" : "") : "")
                // chars=1: joiners 2-5 play the extra characters (Trooper, Dark Samus, Zero Suit, Sylux) on the server's match host
                + (o.Chars && i >= 2 && i <= Characters.All.Count ? $" hunter={Characters.All[i - 1].Base} character={Characters.All[i - 1].Id}" : "")
                // rejoin=: the last joiner vanishes mid-match and comes back as a restarted app (a held seat on the server)
                + (o.Rejoin > 0 && i == o.Players ? $" rejoin={o.Rejoin.ToString(CultureInfo.InvariantCulture)} rejoinwait="
                    + o.RejoinWait.ToString(CultureInfo.InvariantCulture) : "");
            var ownerOut = new List<string>();
            joiners.Add((Start(Environment.ProcessPath!, Args(1, ""), ownerOut), ownerOut));
            string? game = null;
            var clock = Stopwatch.StartNew();
            while (game == null && clock.ElapsedMilliseconds < 30000)
            {
                lock (ownerOut)
                {
                    game = ownerOut.FirstOrDefault(l => l.StartsWith("OWNERGAME ", StringComparison.Ordinal))?[10..].Trim();
                }
                Thread.Sleep(50);
            }
            if (game == null)
            {
                Console.WriteLine("served: the owner opened no served game");
                return;
            }
            Console.WriteLine($"served: game {game}, owned by joiner 1");
            for (int i = 2; i <= o.Players; i++)
            {
                var output = new List<string>();
                joiners.Add((Start(Environment.ProcessPath!, Args(i, game), output), output));
            }
        }

        // -onlinejoin owner=: open a served game (the server must serve it) and say its id
        private static GameMsg OwnServedGame(Options o, OnlineClient online)
        {
            GameMsg game = Wait(online.RequestAsync<GameMsg>(new HostGameReq
            {
                Info = new GameInfo
                {
                    Mode = o.Mode.Replace(" ", ""), Arena = o.Arena, MaxPlayers = o.Owner, Public = true, Version = VersionKey,
                    Rules = $"{o.Time:0} s", Served = true,
                },
            }));
            Console.WriteLine($"joiner {o.Index}: {online.Me} opened game {game.Game.Id}, served {game.Game.Info.Served}, relay slot {game.Relay.Slot}");
            if (!game.Game.Info.Served)
            {
                throw new InvalidOperationException("the server didn't serve the game");
            }
            Console.WriteLine($"OWNERGAME {game.Game.Id}");
            return game;
        }

        // the owner's lobby: its rules and bots (one SETTINGS), then START once owner= players are in (90 s at most)
        private static bool OwnerSetup(Options o, NetMatchSession net)
        {
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            MatchSettings rules = MatchSettings.Quick(mode, MatchArenas.Find(o.Arena)!.Name, Hunter.Samus, o.Bots, o.Level);
            rules.TimeLimitSeconds = o.Time;
            var clock = Stopwatch.StartNew();
            bool sent = false;
            while (clock.ElapsedMilliseconds < 90000)
            {
                MatchSettings? lobby = net.OwnerLobby();
                if (lobby != null && !sent)
                {
                    net.OwnerRules(rules, bots: true);
                    sent = true;
                }
                if (lobby != null && sent && lobby.Players.Count(p => !p.Bot) >= o.Owner && lobby.Players.Count(p => p.Bot) == o.Bots
                    && lobby.Mode == mode && lobby.TimeLimitSeconds == o.Time)
                {
                    Console.WriteLine($"joiner {o.Index}: lobby ready ({lobby.Players.Count} seats: "
                        + string.Join(", ", lobby.Players.Select(p => $"{p.Hunter}{(p.Bot ? $" bot{p.BotLevel}" : "")}")) + "), START");
                    net.SendStart();
                    return true;
                }
                if (lobby != null && sent && clock.ElapsedMilliseconds % 3000 < 60)
                {
                    net.OwnerRules(rules, bots: true); // (UDP: again until the LOBBY shows it)
                }
                Thread.Sleep(50);
            }
            Console.WriteLine($"joiner {o.Index}: FAIL the lobby never filled ({net.Status})");
            return false;
        }

        // matches= (PLAY AGAIN): the lobby again (this player's LOOK says it's back), then the next match; whoever owns the
        // lobby now STARTs it once the others have had a few seconds to come back
        private static bool PlayAgain(Options o, NetMatchSession net, int match)
        {
            net.EndMatch();
            net.SetLook(o.Hunter, o.Character, o.Skin, choosing: false);
            var clock = Stopwatch.StartNew();
            long startAt = -1;
            while (!net.Started && !net.HostLeft && clock.ElapsedMilliseconds < 90000)
            {
                net.LobbyTick(inLobby: true);
                if (net.IsOwner && startAt < 0)
                {
                    startAt = clock.ElapsedMilliseconds + 8000;
                }
                if (net.IsOwner && clock.ElapsedMilliseconds >= startAt)
                {
                    Console.WriteLine($"joiner {o.Index}: match {match}: START ({net.OwnerLobby()?.Players.Count ?? 0} seats)");
                    net.SendStart();
                    startAt = clock.ElapsedMilliseconds + 5000;
                }
                Thread.Sleep(20);
            }
            if (!net.Started)
            {
                Console.WriteLine($"joiner {o.Index}: match {match}: no START ({net.Status})");
                return false;
            }
            CampaignHost host = CampaignHost.StartMatch(net.Settings!);
            var driver = new MatchNetDriver(net, host);
            RealTime(host, () => driver.Step(Brain(host)), o.Time + 25);
            Console.WriteLine($"joiner {o.Index}: match {match}: {host.Frame} frames as seat {net.Settings!.LocalSeat}, ended {host.Ended}");
            Console.WriteLine($"joiner {o.Index} match {match} scoreboard:\n" + MatchStatus.Scoreboard());
            host.Dispose();
            return true;
        }

        private static void ServedVerdict(Options o, List<string> serverOut, List<List<string>> outputs)
        {
            var lines = new List<string>();
            // each match's board on each device that played it: leave= (the last joiner) quit mid-match 1, its board is from
            // then; ownerleave=1: joiner 1 left after match 1
            string Board(List<string> output, string header)
            {
                lock (output)
                {
                    int at = output.FindIndex(l => l.Contains(header, StringComparison.Ordinal));
                    return at < 0 ? "" : string.Join("\n", output.Skip(at + 1).TakeWhile(l => l.StartsWith('#') || l.StartsWith("Battle")
                        || l.StartsWith("Capture") || l.StartsWith("Bounty") || l.StartsWith("Defender") || l.StartsWith("Nodes")
                        || l.StartsWith("Survival") || l.StartsWith("Prime")));
                }
            }
            List<string> boards = [];
            for (int match = 1; match <= o.Matches; match++)
            {
                List<string> these = outputs.Where((_, i) => !(o.Leave > 0 && i == outputs.Count - 1) && !(match > 1 && o.OwnerLeave && i == 0))
                    .Select(output => Board(output, match == 1 ? " scoreboard:" : $" match {match} scoreboard:")).ToList();
                if (these.Any(b => b.Length == 0))
                {
                    lines.Add($"FAIL match {match}: {these.Count(b => b.Length == 0)} device(s) printed no scoreboard");
                }
                else if (these.Distinct().Count() != 1)
                {
                    lines.Add($"FAIL match {match}: the devices' scoreboards differ");
                }
                boards.Add(these.FirstOrDefault() ?? "");
            }
            List<string> server;
            lock (serverOut)
            {
                server = serverOut.ToList();
            }
            // (server=: the live server; its side is in its journal, so only the devices are checked here)
            if (o.Server == null && !server.Any(l => l.Contains("served games: on", StringComparison.Ordinal)))
            {
                lines.Add("FAIL served games are off on the server: " + (server.FirstOrDefault(l => l.Contains("served games")) ?? "?"));
            }
            if (o.Server == null && !server.Any(l => l.Contains(": match host ready", StringComparison.Ordinal)))
            {
                lines.Add("FAIL no match host came up");
            }
            int results = server.Count(l => l.Contains(": result for", StringComparison.Ordinal));
            if (o.Server == null && o.Leave == 0 && results != o.Matches)
            {
                lines.Add($"FAIL the server took {results} result(s) from the match host, {o.Matches} match(es) played");
            }
            if (o.OwnerLeave && !outputs.Skip(1).Any(output => { lock (output) { return output.Any(l => l.Contains("owns the lobby now")); } }))
            {
                lines.Add("FAIL nobody heard they own the lobby after the owner left");
            }
            // replays (default on): every joiner but a rejoin got the match host's recording of match 1, exact (the leaver its
            // cut-short copy, asked for as it left)
            if (o.Replays != false)
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    if (o.Rejoin > 0 && i == outputs.Count - 1) continue;
                    string? line;
                    lock (outputs[i])
                    {
                        line = outputs[i].FirstOrDefault(l => l.StartsWith("REPLAY joiner ", StringComparison.Ordinal));
                    }
                    if (line == null || !line.Contains(": exact,", StringComparison.Ordinal))
                    {
                        lines.Add($"FAIL joiner {i + 1}'s replay: {line ?? "no REPLAY line"}");
                    }
                    else
                    {
                        Console.WriteLine(line);
                    }
                }
            }
            Console.WriteLine("server (served lines):");
            foreach (string l in server.Where(l => l.Contains("served") || l.Contains("[match ")).TakeLast(40))
            {
                Console.WriteLine("  " + l);
            }
            for (int match = 1; match <= boards.Count; match++)
            {
                Console.WriteLine($"match {match} scoreboard:\n" + boards[match - 1]);
            }
            Console.WriteLine(lines.Count == 0 ? $"PASS served onlinetest: {o.Players} players + {o.Bots} bots, {o.Matches} match(es) ran on the server"
                + (o.OwnerLeave ? ", the owner left after the first and the next player ran the lobby" : "")
                : string.Join("\n", lines));
        }
    }
}
