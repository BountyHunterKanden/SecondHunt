using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

namespace SecondHunt.MatchHost
{
    // A dedicated match host (docs/DEDICATED_SERVERS.md): hosts an online game's match with no seat of its own
    // (MatchSettings.ServedHost), so every human is a joiner and nobody plays at zero ping. Two ways to run it:
    //   standalone (D2, tests and measuring): logs in to a server like a player, opens a public game, starts it once --min
    //     players are in and --countdown seconds pass with them, plays it, closes it and opens the next one;
    //   --control (D3, ControlMode.cs): started by the server for one served game, on the relay slot it was given; the
    //     lobby's owner runs the lobby, the server talks to it over stdin/stdout.
    //   --version: prints the game version this host's game files give (the server only serves games of that version).
    //
    //   SecondHuntMatchHost [--data <dir>] [--server <ws(s) url>] [--name <NAME>] [--identity <file>] [--mode Battle]
    //       [--arena "Combat Hall"] [--time <s>] [--players <n>] [--bots <n>] [--level <0-2>] [--min <n>] [--countdown <s>]
    //       [--matches <n>] [--stats <s>]
    //   SecondHuntMatchHost --control --relay-port <port> --token <relay token> [--relay-host 127.0.0.1] [--owner <slot>]
    //       [--seats <n>] [--data <dir>] [--mode ...] [--arena ...] [--time <s>] [--stats <s>]
    //       [--find --expect <n> [--no-extra-pickups] [--no-extra-characters]]
    //   SecondHuntMatchHost --version [--data <dir>]
    //   SecondHuntMatchHost --arenas [--data <dir>]   the arenas this host can run, one a line (the server serves Find Game
    //       matches, and games in an imported arena, only in these)
    //   any of them: [--imports <dir>]
    //
    // Needs paths.txt (the extracted game files, as the Tools use them) in --data's folder or the working directory. The
    // imported arenas (Echoes: the in-app importer's arenas/<Key>/ + import/mp2.json, as IMPORT GAMES writes them on a
    // device, from Tools -importcheck arenas all --out <dir>) come from --imports, or an imports folder next to paths.txt.
    internal static class Program
    {
        internal sealed class Options
        {
            public Uri Server = new("wss://play.secondhuntonline.com/v1/ws");
            public string Name = "SH SERVER";
            public string Identity = "matchhost_identity.txt";
            public string? Data;
            public string Mode = "Battle";
            public string Arena = "Combat Hall";
            public float Time = 300;
            public bool TimeSet; // (--time given: a Find Game match plays it instead of the mode's own, tests)
            public int Players = 4;
            public int Bots;
            public int Level = 1;
            public int Min = 2;
            public float Countdown = 15;
            public int Matches; // 0 = keep going
            public float Stats = 30;
            public bool Version;
            public bool Control;
            public string RelayHost = "127.0.0.1";
            public int RelayPort;
            public string Token = "";
            public int Owner = -1;
            public int Seats = 4;
            // a Find Game match (the server opened it, HubServedFind.cs): Find's own rules, started by this host once the
            // Expect matched players are in (ControlMode.cs), the searches' ONLINE SETTINGS filters
            public bool Find;
            public int Expect;
            public bool NoExtraPickups;
            public bool NoExtraCharacters;
            public bool Arenas;
            public string? Imports;
        }

        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Options o;
            try
            {
                o = Parse(args);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                Console.Error.WriteLine($"matchhost: {ex.Message}");
                return 2;
            }
            if (o.Data != null)
            {
                o.Identity = Path.GetFullPath(o.Identity);
                Directory.SetCurrentDirectory(o.Data);
            }
            if (!File.Exists("paths.txt"))
            {
                Console.Error.WriteLine("matchhost: no paths.txt here: a match host runs the match itself, so it needs the "
                    + "extracted game files (docs/DEDICATED_SERVERS.md section 3)");
                return 3;
            }
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            Extract.NoCodeFiles = true; // (the server keeps no cart code files: no sound tables or font, never needed here)
            MphRecomp.Arenas.EchoesArena.ImportRoot = o.Imports ?? (Directory.Exists("imports") ? Path.GetFullPath("imports") : null);
            string romKey = LanSession.RomKeyFor(Paths.MphKey);
            if (o.Version)
            {
                Console.WriteLine(OnlineMatch.VersionKey(romKey));
                return 0;
            }
            if (o.Arenas)
            {
                foreach (string arena in MatchArenas.All.Select(r => r.Name).Where(n => !ExtraArenas.IsExtra(n) || ExtraArenas.Owned().Contains(n)).Distinct())
                {
                    Console.WriteLine(arena);
                }
                return 0;
            }
            if (o.Control)
            {
                return ControlMode.Run(o, romKey);
            }
            Log($"game files {Paths.MphKey} ({romKey}), version {OnlineMatch.VersionKey(romKey)}, server {o.Server}");

            OnlineIdentity identity = LoadIdentity(o.Identity);
            OnlineClient client = Wait(OnlineClient.ConnectAsync(o.Server, identity, o.Name, "matchhost"));
            Log($"logged in as {client.Me}");
            int played = 0;
            while (o.Matches == 0 || played < o.Matches)
            {
                try
                {
                    if (HostOne(o, client, romKey))
                    {
                        played++;
                    }
                }
                catch (Exception ex) when (ex is OnlineException or IOException or InvalidOperationException)
                {
                    Log($"game failed: {ex.Message}; opening a new one in 5 s");
                    Thread.Sleep(5000);
                }
            }
            Wait(client.DisposeAsync().AsTask().ContinueWith(_ => 0));
            return 0;
        }

        // the match a game starts with: its mode, arena and time, with no human seat (every human joins) and o.Bots bots
        internal static MatchSettings Template(Options o)
        {
            if (o.Find)
            {
                // Find Game: the rules a player host's device made (BuildFindMatch: the mode's defaults in the voted arena)
                MatchSettings found = MphRecomp.Frontend.MultiplayerMenu.BuildFindMatch(o.Mode, o.Arena);
                found.Players.RemoveAll(p => !p.Bot);
                found.ServedHost = true;
                if (o.NoExtraPickups) found.PirateBlaster = false;
                if (o.NoExtraCharacters) found.ExtraCharacters = false;
                if (o.TimeSet) found.TimeLimitSeconds = o.Time;
                Characters.ApplyExtraRules(found);
                return found;
            }
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings template = MatchSettings.Quick(mode, arena.Name, Hunter.Samus, o.Bots, o.Level);
            template.Players.RemoveAll(p => !p.Bot);
            template.ServedHost = true;
            template.TimeLimitSeconds = o.Time;
            return template;
        }

        // standalone: one game: lobby, match, close. False when no match was played (nobody came, or everyone left the lobby)
        private static bool HostOne(Options o, OnlineClient client, string romKey)
        {
            MatchSettings template = Template(o);
            GameMsg game = Wait(client.RequestAsync<GameMsg>(new HostGameReq
            {
                Info = new GameInfo
                {
                    Mode = template.Mode.ToString(), Arena = template.Arena, Public = true, Version = OnlineMatch.VersionKey(romKey),
                    // (a standalone host is a player to the server: it counts this host's relay slot as a player)
                    MaxPlayers = o.Players + 1,
                    Rules = $"{o.Time:0} s", ExtraArena = ExtraArenas.IsExtra(template.Arena),
                },
            }));
            using RelayTransport relay = RelayTransport.For(game.Relay, o.Server);
            using NetMatchSession net = NetMatchSession.Host(relay, template, romKey, o.Players + o.Bots);
            Log($"game {game.Game.Id}: {template.Mode} in {template.Arena}, {o.Time:0} s, up to {o.Players} players (relay {relay.Relay})");
            try
            {
                if (!Lobby(o, net))
                {
                    return false;
                }
                net.SendStart();
                Wait(client.RequestAsync<OkMsg>(new StartGameReq { Started = true }));
                Play(net, o.Stats, CancellationToken.None);
                return true;
            }
            finally
            {
                try
                {
                    Wait(client.RequestAsync<OkMsg>(new LeaveGameReq()));
                }
                catch (Exception ex) when (ex is OnlineException or InvalidOperationException)
                {
                }
            }
        }

        // wait for --min players, then --countdown s with at least that many (restarts if one leaves); false after 30 min
        // with nobody, so a quiet server still opens a fresh game now and then
        private static bool Lobby(Options o, NetMatchSession net)
        {
            var idle = Stopwatch.StartNew();
            Stopwatch? countdown = null;
            int seen = -1;
            while (true)
            {
                int joined = net.Joined;
                if (joined != seen)
                {
                    seen = joined;
                    Log($"lobby: {joined}/{o.Players} players ({net.Status})");
                    idle.Restart();
                }
                if (joined >= o.Players || joined >= o.Min && countdown != null && countdown.Elapsed.TotalSeconds >= o.Countdown)
                {
                    Log($"lobby: starting with {joined} players");
                    return true;
                }
                if (joined >= o.Min && countdown == null)
                {
                    countdown = Stopwatch.StartNew();
                    Log($"lobby: {o.Countdown:0} s to start");
                }
                else if (joined < o.Min)
                {
                    countdown = null;
                }
                if (joined == 0 && idle.Elapsed.TotalMinutes >= 30)
                {
                    return false;
                }
                Thread.Sleep(50);
            }
        }

        // the started match in real time (60 Hz), as long as anyone is still in it; prints the step cost and memory as it
        // goes; the session is back in its lobby after (EndMatch). over: called first with whether the match ended itself
        // (not everyone gone or stopped), while the seats are still the match's
        internal static void Play(NetMatchSession net, float statsEvery, CancellationToken stop, Action<bool>? over = null)
        {
            MatchSettings settings = net.Settings!;
            // no Hyper Ball for Dark Samus in served matches (owner 2026-10-18: not public yet); this host isn't a public build,
            // so MatchStart would turn it on (settings.HyperBall ?? !PublicBuild) while the players' public apps have bombs
            settings.HyperBall = false;
            Log("seats " + string.Join(", ", settings.Players.Select((p, i) => $"{i}:{p.Hunter}{(p.Character != null ? "/" + p.Character : "")}{(p.Bot ? " bot" : $" {p.Nickname}")}")));
            var load = Stopwatch.StartNew();
            CampaignHost host = CampaignHost.StartMatch(settings);
            var driver = new MatchNetDriver(net, host);
            Log($"match loaded in {load.ElapsedMilliseconds} ms, {Memory()}");

            var clock = Stopwatch.StartNew();
            var step = new Stopwatch();
            long frames = 0, statFrames = 0;
            double statMs = 0, statMax = 0, allMs = 0;
            double nextStats = statsEvery;
            // (the match ends itself; this only stops one that can't: its time limit and RESULTS, or 30 min with none)
            double limit = (settings.TimeLimitSeconds is float t && t > 0 ? t : 1680) + 120;
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            while (!host.Ended && clock.Elapsed.TotalSeconds < limit && !stop.IsCancellationRequested)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step.Restart();
                    driver.Step(idle);
                    double ms = step.Elapsed.TotalMilliseconds;
                    frames++;
                    statFrames++;
                    statMs += ms;
                    allMs += ms;
                    statMax = Math.Max(statMax, ms);
                }
                if (clock.Elapsed.TotalSeconds >= nextStats)
                {
                    nextStats += statsEvery;
                    Log($"frame {host.Frame}: step mean {statMs / Math.Max(statFrames, 1):0.000} ms, max {statMax:0.00} ms, "
                        + $"players {net.Joined}, {Memory()}");
                    statFrames = 0;
                    statMs = statMax = 0;
                }
                if (net.Joined == 0 && GameState.MatchState == MatchState.InProgress)
                {
                    Log("everyone left: the match ends");
                    break;
                }
                Thread.Sleep(1);
            }
            Log($"match over: {frames} frames, step mean {allMs / Math.Max(frames, 1):0.000} ms, {Memory()}, {driver.StatusLine()}");
            Log("scores " + string.Join(", ", Enumerable.Range(0, settings.Players.Count).Select(i =>
                $"{settings.Players[i].Nickname ?? settings.Players[i].Hunter.ToString()} {GameState.Points[i]}")));
            over?.Invoke(host.Ended);
            net.EndMatch();
        }

        internal static string Memory()
        {
            using Process me = Process.GetCurrentProcess();
            return $"memory {me.WorkingSet64 / (1024 * 1024)} MB (peak {me.PeakWorkingSet64 / (1024 * 1024)} MB, "
                + $"managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB)";
        }

        // the host's own player identity (a name and friend code like anyone's), kept across restarts by its backup code
        private static OnlineIdentity LoadIdentity(string path)
        {
            if (File.Exists(path) && OnlineIdentity.FromBackupCode(File.ReadAllText(path).Trim()) is OnlineIdentity kept)
            {
                return kept;
            }
            OnlineIdentity made = OnlineIdentity.Create();
            File.WriteAllText(path, made.BackupCode);
            return made;
        }

        private static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                string Next() => args[++i];
                switch (key)
                {
                case "--server": o.Server = new Uri(Next()); break;
                case "--name": o.Name = Next(); break;
                case "--identity": o.Identity = Next(); break;
                case "--data": o.Data = Next(); break;
                case "--mode": o.Mode = Next(); break;
                case "--arena": o.Arena = Next(); break;
                case "--time": o.Time = Single.Parse(Next(), CultureInfo.InvariantCulture); o.TimeSet = true; break;
                case "--players": o.Players = Int32.Parse(Next()); break;
                case "--bots": o.Bots = Int32.Parse(Next()); break;
                case "--level": o.Level = Int32.Parse(Next()); break;
                case "--min": o.Min = Int32.Parse(Next()); break;
                case "--countdown": o.Countdown = Single.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--matches": o.Matches = Int32.Parse(Next()); break;
                case "--stats": o.Stats = Single.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--version": o.Version = true; break;
                case "--control": o.Control = true; break;
                case "--relay-host": o.RelayHost = Next(); break;
                case "--relay-port": o.RelayPort = Int32.Parse(Next()); break;
                case "--token": o.Token = Next(); break;
                case "--owner": o.Owner = Int32.Parse(Next()); break;
                case "--seats": o.Seats = Int32.Parse(Next()); break;
                case "--find": o.Find = true; break;
                case "--expect": o.Expect = Int32.Parse(Next()); break;
                case "--no-extra-pickups": o.NoExtraPickups = true; break;
                case "--no-extra-characters": o.NoExtraCharacters = true; break;
                case "--arenas": o.Arenas = true; break;
                case "--imports": o.Imports = Path.GetFullPath(Next()); break;
                default: throw new ArgumentException($"unknown option {key}");
                }
            }
            if (o.Players < 1 || o.Players + o.Bots > MatchSettings.MaxSeats || o.Min < 1)
            {
                throw new ArgumentException($"players + bots must be 1-{MatchSettings.MaxSeats}, min at least 1");
            }
            if (o.Control && (o.RelayPort <= 0 || o.Token.Length == 0))
            {
                throw new ArgumentException("--control needs --relay-port and --token");
            }
            return o;
        }

        // (a log line: the server keeps these in its own log; control lines start with '!' instead, ControlMode.Say)
        internal static void Log(string line)
        {
            lock (Console.Out)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");
            }
        }

        private static T Wait<T>(Task<T> task)
        {
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
