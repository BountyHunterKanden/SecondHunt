using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // The first online match, all on one PC (docs/ONLINE_PLAY.md O3): a real SecondHunt server (spawned here unless
    // server= names one), a host that opens a public game on it, and players-1 more copies of this tool that find the
    // game in the open games list, join it and play their seats. Every match packet goes through the server's UDP relay
    // (RelayTransport + NetMatchSession), each device on its own 60 Hz clock (no lockstep), as phones on two networks
    // would. delay=<ms> holds every relayed packet that long (one way). leave=<s>: the last joiner quits mid-match (the
    // host must drop its seat and play on). PASS = every device ends with the same scoreboard. hostleave=<s>: the host
    // quits at that time instead (BYE + leave the game): O5b host migration (doc section 9b): the server names the lowest
    // slot still online (joiner 1) the new host, it takes over from its own copy of the match, the others follow it; PASS =
    // the joiner on the lowest relay slot took over, every other joiner followed, none ended early, the old host's seat is out, its bots are bots
    // again on the new host, and every joiner ends with the same scores. hostcrash=<s>: the same, but the host vanishes
    // without a BYE and its server connection just drops (the server's websocket close decides).
    //
    //   -onlinetest [players=3] [bots=1] [mode=Battle] [arena=Combat Hall] [time=30] [level=1] [delay=0] [leave=0]
    //               [hostleave=0] [server=ws://host:port/v1/ws] [looks=1] [loss=<percent>] [jitter=<ms>] [lagcomp=0]
    //               [maxrewind=<frames>] [strafe=1] [rejoin=<s>] [rejoinwait=<s>]
    // rejoin=<s> (O5): the last joiner vanishes at that time (no BYE, as a crash), waits rejoinwait= s (default 2; past 10 s
    // the host drops its seat first) and comes back as a restarted app (a new process with the same identity, JoinGameReq
    // with no id = "my started game", a new relay token); PASS = the host saw it rejoin, its seat is up at the end, and
    // every device agrees on the scores
    // wifi=<s> [wifiwait=<s>] (release test T1): the last joiner's network goes away at that time (its relay sends and
    // receives stop, its server connection drops) for wifiwait= s (default 15: past the host's 10 s seat drop), then comes back
    // in the same process; PASS = it didn't end its match, got back in (NetMatchSession.Reconnects), the host took its seat
    // back, and every device agrees on the scores
    // uplink=1 with wifi= (release 0.2.0 F1, the owner's Wi-Fi rejoin): only the joiner's sends are lost for wifiwait= s; it
    // still hears the host (so it never ends its match) and its server connection stays. Past the server's 90 s seat hold
    // the seat is dropped there; PASS needs the seat to play again all the same (the host hears it, it hears the host)
    // turn=local (O5, decision 18): a TURN server on this PC (TurnTestServer, delay= on its hops too) and every device on a
    // HybridTransport (TURN first, the relay as fallback); PASS also needs most match packets on every device to go over TURN
    // claims=0: the host takes no hit claims (Net/HitClaims.cs); the host's "claims" line says what it did with each joiner's claims
    // pos=0 (0.3.6): the host takes no client-reported positions (MatchSettings.ClientPosition, Net/ClientPosition.cs);
    // poslimit=<units> its limit (4); the host's "positions" line says how many of each joiner's reports it took and why it
    // refused the rest. hz=30|20 (0.3.6): the host's snapshot rate (NetMatchSession.SnapshotIntervalNormal; 60 by default).
    // With served=hub both go to the server as --served-client-position / --served-position-limit / --served-snapshot-hz.
    // lagcomp=0 / maxrewind= / strafe=1 (N4): the host's player strafes left and right (a moving target for the joiners);
    // the host prints how many of each joiner's beam hits counted there against the hits that joiner saw on its screen
    // loss= / jitter= (O5): every device's own sends are dropped that often and wait 0..jitter ms more (SimulatedLink), on
    // top of the relay's delay=; each device prints its link (rate, round trip, loss, KB, inputs merged/starved).
    // external=1 lag=<ms> (the device test): the host spawns no joiners; it waits up to 10 min for players-1 outside
    // devices (a phone's dev menu ONLINE TEST: JOIN takes the first open game of its version on that server), plays them with
    // its bots, and its own player hunts like a joiner's (strafe=1: strafes instead); lag= adds that round trip on the
    // host's link (SimulatedLink), so every outside device sees it on top of its real ping (rev 0 and rev 1 share one
    // game version, LanSession.RomKeyFor, so the shared Tools can host the phones). PASS = the match ran to its end here;
    // compare the host's scoreboard with the device's RESULTS by eye.
    // looks=1: seat characters + skins (SeatLooksTest: a Varia Samus host, joiner 1 a Wave Trooper, the last bot a Plasma
    // Trooper); every device must show the same looks with the Trooper's abilities on (each Trooper seat), and joiner 1 first sends a JOIN
    // from the previous protocol, which must be refused.
    internal static partial class OnlineMatchTool
    {
        private sealed class Options
        {
            public int Players = 3;
            public int Bots = 1;
            public string Mode = "Battle";
            public string Arena = "Combat Hall";
            public float Time = 30;
            public int Level = 1;
            public int Delay;
            public float Loss; // 0..1
            public int Jitter;
            public int Burst, BurstSeat = -1; // (burst=<ms>, burstdevice=<index>: only that device's link is bursty)
            public int Lag; // lag=<ms>: a round trip added on this device's link (half each way)
            public bool External; // external=1: the joiners are outside devices (phones), not processes started here
            public bool Served; // served=1 (docs/DEDICATED_SERVERS.md D1): the host has no seat; players= joiners play every human seat
            public bool ServedHub; // served=hub (D3, OnlineServedTest.cs): the server runs the match (SecondHuntMatchHost); joiner 1 owns the lobby
            public int Owner; // -onlinejoin owner=<players>: open a served game and run its lobby (OnlineServedTest.cs)
            public int Matches = 1; // served=hub matches=<n>: PLAY AGAIN after each match, n in all
            public bool OwnerLeave; // served=hub ownerleave=1: the owner leaves after the first match (the next slot owns it)
            public int? Goal; // goal=<points>: the match's point goal (0 = none, ends on time only); default = the mode's
            public float Leave;
            public float HostLeave;
            public bool HostCrash; // hostcrash=<s>: hostleave without a BYE or LeaveGame (a crash: the socket drops)
            public string? Server;
            public bool Looks;
            public bool LagComp = true; // lagcomp=0: the host doesn't rewind (N4), to compare the joiners' hits
            public bool Claims = true; // claims=0: the host takes no hit claims (MatchSettings.HitClaims, Net/HitClaims.cs), to compare
            public bool Positions = true; // pos=0: the host takes no client-reported positions (Net/ClientPosition.cs), to compare
            public float? PosLimit; // poslimit=<units>: ClientPosition.Limit (4)
            public int Hz = 60; // hz=30|20: the host's snapshot rate (60)
            public bool Strafe;
            public float Rejoin; // rejoin=<s>: the last joiner vanishes then (no BYE) and comes back as a restarted app
            public float RejoinWait = 2;
            public float Wifi; // wifi=<s>: the last joiner's network goes away then, for WifiWait s, and comes back
            public float WifiWait = 15;
            public bool Uplink; // uplink=1: wifi= loses only the joiner's sends
            public bool Stale; // stale=1 (with wifi=): when the network is back, the joiner's relay socket still hears but its sends are lost (Android: a socket left on the network that went away) until it makes a new one
            public bool TurnDead; // turndead=1 (with turn= and wifi=): the joiner's TURN sends stay lost after the network is back
            public bool Again; // -onlinejoin: this process is that restarted app
            public string? Code; // its identity (backup code)
            public string? Turn; // turn=local: a TURN server here (TurnTestServer) + our server's --turn-static; turn=hub
            public float TurnKill; // turnkill=<s>: the local TURN server goes away then (every device must fall back to the relay)
            // -onlinejoin
            public string Game = "";
            public int Index = 1;
            public Hunter Hunter = Hunter.Sylux;
            public string? Character, Skin;
            public int Colour = -1; // looks=1: the joiner's colour pick
            public bool Chars; // served=hub chars=1: a Trooper and a Dark Samus seat (OnlineServedTest.cs)
            public bool Duel; // duel=1 (2026-10-19, point-blank hits not landing): joiner 1 strafes in place, the others close to
                              // 4 units and shoot it; the host's shot check and HITS vs each joiner's SAWHITS say what landed
            public string? DuelRole; // -onlinejoin duel=runner|shooter
            public bool Push; // duel=1 push=1 (2026-10-19, knockback shake online): the runner stands still, the shooter fires
                              // missiles (they push); both joiners log the runner's motion for 2 s after each hit (PushWatch)
            public string Fwd = ""; // client switches passed on to each joiner process (smoothfp=, ...)
            public bool SmoothOff; // smooth=0: guests snap the other players to each snapshot as before (Net/RemoteSmoothing.cs)
            public bool AltFixOff; // altfix=0: guests correct a ball's height as before (ClientPrediction.AltHeightFix)
            public bool Ball; // ball=1 (2026-10-19, alt form bouncing online): the joiners play in alt form (morph, then roll at their target)
            public bool? Replays; // served=hub replays=0|1 (default on): each joiner gets the match host's recording, played back exact (OnlineServedReplay.cs)
            public bool Gap; // served=hub gap=1 (docs/FRUITY_PORT.md Step 2a): the path-gap check (OnlineGapTest.cs)
            public bool FruityP1; // served=hub fruity=1 (docs/FRUITY_PORT.md P1): the match host runs Fruity Prime's movement sync
            public string? Seek; // seek=<ItemType> (0.2.0 pickups bake): joiner 1 walks to that item and logs its weapons + UA
        }

        private static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "players": o.Players = Int32.Parse(value); break;
                case "bots": o.Bots = Int32.Parse(value); break;
                case "mode": o.Mode = value; break;
                case "arena": o.Arena = value; break;
                case "time": o.Time = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "level": o.Level = Int32.Parse(value); break;
                case "delay": o.Delay = Int32.Parse(value); break;
                case "loss": o.Loss = Single.Parse(value, CultureInfo.InvariantCulture) / 100f; break;
                case "jitter": o.Jitter = Int32.Parse(value); break;
                case "burst": o.Burst = Int32.Parse(value); break; // (sends let go in bunches up to this many ms apart: SimulatedLink)
                case "burstdevice": o.BurstSeat = Int32.Parse(value); break;
                case "p2": HitClaims.OutboxOff = value == "0"; break; // 0 = the old repeat-4 claims (A/B; fruity=1)
                case "burstbuf": NetMatchSession.BurstBuffer = value != "0"; break; // 0 = a bursty link's spare trimmed as a steady one's (A/B; served=1)
                case "lag": o.Lag = Int32.Parse(value); break;
                case "external": o.External = value != "0"; break;
                case "served": o.Served = value != "0"; o.ServedHub = value == "hub"; break;
                case "owner": o.Owner = Int32.Parse(value); break;
                case "matches": o.Matches = Int32.Parse(value); break;
                case "ownerleave": o.OwnerLeave = value != "0"; break;
                case "goal": o.Goal = Int32.Parse(value); break;
                case "leave": o.Leave = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "hostleave": o.HostLeave = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "hostcrash": o.HostLeave = Single.Parse(value, CultureInfo.InvariantCulture); o.HostCrash = true; break;
                case "server": o.Server = value; break;
                case "game": o.Game = value; break;
                case "index": o.Index = Int32.Parse(value); break;
                case "hunter": o.Hunter = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "looks": o.Looks = value != "0"; break;
                case "chars": o.Chars = value != "0"; break;
                case "replays": o.Replays = value != "0"; break;
                case "ball": o.Ball = value != "0"; break;
                case "duel": if (value is "runner" or "shooter") o.DuelRole = value; else o.Duel = value != "0"; break;
                case "push": o.Push = value != "0"; break;
                case "gap": o.Gap = value != "0"; break;
                case "smooth": RemoteSmoothing.Enabled = value != "0"; o.SmoothOff = value == "0"; break;
                case "fruitypin": ClientPosition.Pin = value != "0"; break; // 0 = no pin (A/B)
                case "fruityhold": ClientPosition.Hold = value != "0"; break;
                case "fruitypinafter": ClientPosition.PinAfterMode = Int32.Parse(value, CultureInfo.InvariantCulture); break;
                case "fruity": o.FruityP1 = value != "0"; break; // Fruity Prime's P1 movement sync, served=hub only (Net/FruitySync.cs)
                case "fruitylatest": NetMatchSession.LatestInputOnly = value != "0"; break;
                case "fruitycorrect": ClientPrediction.FruityCorrect = value != "0"; o.Fwd += " fruitycorrect=" + value; break;
                case "smoothfp": RemoteSmoothing.FruityRead = value != "0"; o.Fwd += " smoothfp=" + value; break; // 0 = the arrival-clock read point (A/B)
                case "respawnhold": RemoteSmoothing.RespawnHold = value != "0"; o.Fwd += " respawnhold=" + value; break; // 0 = a respawned seat drawn on its pre-spawn poses (A/B)
                case "steady": RemoteSmoothing.SteadyOn = value != "0"; o.Fwd += " steady=" + value; break; // 0 = no steady walk on the drawn others (A/B)
                case "altfix": ClientPrediction.AltHeightFix = value != "0"; o.AltFixOff = value == "0"; break;
                case "speedfix": ClientPrediction.SpeedFix = value != "0"; break; // 0 = the earlier speed correction (A/B)
                case "clientpush": ClientPrediction.ClientPushes = value != "0"; break; // 0 = the host pushes its copy (the hold fix, A/B)
                case "pace": NetMatchSession.PaceInputs = value != "0"; break; // 0 = the cloud's fold past the jitter buffer, no pace asks (E A/B; reaches an in-process host only: served=1)
                case "snapfix": ClientPrediction.SnapFix = value != "0"; break; // 0 = the earlier raw-pose snap (A/B)
                case "lagcomp": o.LagComp = value != "0"; break;
                case "claims": o.Claims = value != "0"; break;
                case "pos": o.Positions = value != "0"; break;
                case "poslimit": o.PosLimit = Single.Parse(value, CultureInfo.InvariantCulture); ClientPosition.Limit = o.PosLimit.Value; break;
                case "hz": o.Hz = Int32.Parse(value); if (o.Hz is not (60 or 30 or 20)) throw new ArgumentException("hz= is 60, 30 or 20"); break;
                case "strafe": o.Strafe = value != "0"; break;
                case "rejoin": o.Rejoin = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "rejoinwait": o.RejoinWait = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "wifi": o.Wifi = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "wifiwait": o.WifiWait = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "uplink": o.Uplink = value != "0"; break;
                case "turndead": o.TurnDead = value != "0"; break;
                case "stale": o.Stale = value != "0"; break;
                case "again": o.Again = value != "0"; break;
                case "code": o.Code = value; break;
                case "turn": o.Turn = value; break;
                case "turnkill": o.TurnKill = Single.Parse(value, CultureInfo.InvariantCulture); break;
                case "maxrewind": LagCompensation.MaxRewind = Int32.Parse(value); break;
                case "character": o.Character = value; break;
                case "skin": o.Skin = value; break;
                case "colour": o.Colour = Int32.Parse(value); break;
                case "seek": o.Seek = value; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            return o;
        }

        private static readonly Hunter[] JoinerHunters = { Hunter.Sylux, Hunter.Weavel, Hunter.Noxus, Hunter.Trace,
            Hunter.Spire, Hunter.Kanden, Hunter.Samus };

        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);
        // what must match to join (GameInfo.Version): the ROM and the game logic
        private static string VersionKey => $"{RomKey}/{ReplayFile.CurrentSimVersion}";

        private static T Wait<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();

        // ---------------------------------------------------------------- parent: server + host + joiners

        public static void Test(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            UseImports();
            if (o.Gap)
            {
                // (the server, its match host and the joiners all inherit it)
                Environment.SetEnvironmentVariable("GAPTRACE", Path.Combine(Path.GetTempPath(), "secondhunt-gap-" + Guid.NewGuid().ToString("N")[..8]));
            }
            Process? server = null;
            var serverOut = new List<string>();
            string? dbDir = null;
            // turn=local: a TURN server here, and our server hands its fixed credentials out (--turn-static) as it would
            // Cloudflare's; turn=hub: the devices ask the server named by server= (the live one: Cloudflare, once its key is in)
            TurnTestServer? turnServer = null;
            if (o.Turn == "local")
            {
                if (o.Server != null || TurnTestServer.CheckVectors() is string)
                {
                    Console.WriteLine("FAIL onlinetest: " + (o.Server != null ? "turn=local needs the local server (no server=)"
                        : TurnTestServer.CheckVectors()));
                    return;
                }
                turnServer = new TurnTestServer(o.Delay);
                Console.WriteLine($"TURN server: {turnServer.Url}");
                o.Turn = "hub";
                if (o.TurnKill > 0)
                {
                    TurnTestServer doomed = turnServer;
                    _turnKill = new Timer(_ =>
                    {
                        doomed.Dispose();
                        Console.WriteLine($"TURN server: gone at {o.TurnKill:0.#} s (turnkill=)");
                    }, null, (int)(o.TurnKill * 1000), Timeout.Infinite);
                }
            }
            Uri uri;
            if (o.Server != null)
            {
                uri = new Uri(o.Server);
            }
            else
            {
                string dll = ServerDll();
                if (!File.Exists(dll))
                {
                    Console.WriteLine($"FAIL onlinetest: no server build at {dll} (build src/SecondHunt.Server first)");
                    return;
                }
                int http = FreeTcpPort();
                int relayPort = FreeUdpPort();
                dbDir = Path.Combine(Path.GetTempPath(), "secondhunt-onlinetest-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dbDir);
                server = Start(Environment.ProcessPath!, $"\"{dll}\" --urls http://127.0.0.1:{http} --db \"{Path.Combine(dbDir, "test.db")}\" "
                    + $"--relay-port {relayPort} --relay-delay {o.Delay} --words none.txt"
                    + (o.ServedHub ? $" --matchhost \"{MatchHostExe()}\" --matchhost-data \"{MatchHostData()}\"" + ImportsArg() : "")
                    + (o.ServedHub && !o.Positions ? " --served-client-position off" : "")
                    + (o.ServedHub && o.PosLimit is float pl ? $" --served-position-limit {pl.ToString(CultureInfo.InvariantCulture)}" : "")
                    + (o.ServedHub && o.Hz != 60 ? $" --served-snapshot-hz {o.Hz}" : "")
                    + (o.ServedHub ? $" --served-fruity-pin {OnOff(ClientPosition.Pin)} --served-fruity-hold {OnOff(ClientPosition.Hold)}"
                        + $" --served-latest-input {OnOff(NetMatchSession.LatestInputOnly)} --served-fruity-p1 {OnOff(o.FruityP1)}" : "")
                    + (turnServer != null ? $" --turn-static \"{turnServer.Url}|{TurnTestServer.User}|{TurnTestServer.Password}\"" : ""),
                    serverOut);
                uri = new Uri($"ws://127.0.0.1:{http}/v1/ws");
                if (!WaitHealthy(http))
                {
                    Console.WriteLine("FAIL onlinetest: the server didn't start");
                    Dump("server", server, serverOut);
                    return;
                }
            }
            Console.WriteLine($"server: {uri}" + (o.Delay > 0 ? $", relay delay {o.Delay} ms one way" : "")
                + (o.Loss > 0 || o.Jitter > 0 ? $", every device loses {o.Loss * 100:0.#}% of its sends, jitter 0-{o.Jitter} ms" : "")
                + (o.Lag > 0 ? $", host link lag {o.Lag} ms round trip" : ""));

            var joiners = new List<(Process P, List<string> Out)>();
            var hostLines = new List<string>();
            try
            {
                if (o.ServedHub)
                {
                    RunServedHub(o, uri, joiners);
                }
                else
                {
                    RunHost(o, uri, joiners, hostLines);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("host: " + ex);
            }
            finally
            {
                // (hostleave: the joiners play the rest of the match after the host is gone)
                int waitMs = o.HostLeave > 0 ? (int)Math.Max(40000, (o.Time - o.HostLeave + 40) * 1000)
                    : o.ServedHub ? (int)((o.Time + 90) * 1000 * o.Matches) : 40000; // (served: nobody here played the match first)
                foreach ((Process p, List<string> output) in joiners)
                {
                    if (!p.WaitForExit(waitMs))
                    {
                        p.Kill();
                    }
                    p.WaitForExit();
                }
                for (int i = 0; i < joiners.Count; i++)
                {
                    Dump($"joiner {i + 1}", joiners[i].P, joiners[i].Out);
                }
                if (turnServer != null)
                {
                    Console.WriteLine($"TURN server: {turnServer.Allocations} allocations, {turnServer.Egress / 1024} KB to clients, "
                        + $"{turnServer.Rejected} rejected");
                    turnServer.Dispose();
                }
                if (server != null)
                {
                    server.Kill();
                    server.WaitForExit();
                    if (serverOut.Any(l => l.Contains("stderr") || l.Contains("fail", StringComparison.OrdinalIgnoreCase)))
                    {
                        Dump("server", server, serverOut);
                    }
                }
                if (dbDir != null)
                {
                    try
                    {
                        Directory.Delete(dbDir, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            if (o.ServedHub)
            {
                ServedVerdict(o, serverOut, joiners.Select(j => j.Out).ToList());
                if (o.Gap)
                {
                    Console.WriteLine($"gap traces: {GapTrace.Folder}");
                    GapCheck(GapTrace.Folder!);
                }
                return;
            }
            Verdict(o, hostLines, joiners.Select(j => j.Out).ToList());
        }

        private static void RunHost(Options o, Uri uri, List<(Process, List<string>)> joiners, List<string> lines)
        {
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings template = MatchSettings.Quick(mode, arena.Name, Hunter.Samus, o.Bots, o.Level);
            template.Players[0].Nickname = "PC host";
            template.TimeLimitSeconds = o.Time;
            if (o.Goal is int goal)
            {
                template.PointGoal = goal;
            }
            if (o.Looks)
            {
                SeatLooksTest.Dress(template);
            }
            if (o.Served)
            {
                // a served match host: no player of its own (its template keeps only the bots), every human joins
                template.Players.RemoveAll(p => !p.Bot);
                template.ServedHost = true;
            }
            int want = o.Served ? o.Players : o.Players - 1; // the joiners this host waits for

            OnlineClient online = Wait(OnlineClient.ConnectAsync(uri, OnlineIdentity.Create(), "PC host", "onlinetest"));
            GameMsg game = Wait(online.RequestAsync<GameMsg>(new HostGameReq
            {
                Info = new GameInfo
                {
                    Mode = mode.ToString(), Arena = arena.Name, MaxPlayers = o.Players + (o.Served ? 1 : 0), Public = true, Version = VersionKey,
                    Rules = $"{o.Time:0} s",
                },
            }));
            using RelayTransport relay = RelayTransport.For(game.Relay, uri);
            using NetMatchSession net = NetMatchSession.Host(Link(o, relay, 0, online), template, RomKey, o.Players);
            net.SnapshotIntervalNormal = 60 / o.Hz; // (0.3.6: 60 Hz unless hz=)
            Console.WriteLine($"host: {online.Me} hosts game {game.Game.Id}, relay {relay.Relay} slot {relay.LocalSlot}");

            string dll = typeof(OnlineMatchTool).Assembly.Location;
            if (o.External)
            {
                Console.WriteLine($"host: waiting up to 10 min for {want} outside player(s) (game version {VersionKey}); "
                    + "on the device: dev menu -> ONLINE TEST: JOIN");
            }
            for (int i = 1; i <= want && !o.External; i++)
            {
                var output = new List<string>();
                float leave = i == want ? o.Leave : 0;
                float rejoin = i == want ? o.Rejoin : 0;
                float wifi = i == want ? o.Wifi : 0;
                string a = $"\"{dll}\" -onlinejoin server={uri} game={game.Game.Id} index={i} "
                    + (o.Looks && i == 1 ? $"hunter={SeatLooksTest.JoinerHunter} character={SeatLooksTest.JoinerCharacter} skin={SeatLooksTest.JoinerSkin} colour={SeatLooksTest.JoinerColour} "
                        : $"hunter={JoinerHunters[(i - 1) % JoinerHunters.Length]} ")
                    + $"time={o.Time.ToString(CultureInfo.InvariantCulture)} leave={leave.ToString(CultureInfo.InvariantCulture)}"
                    + (o.Looks ? " looks=1" : "")
                    + (o.Ball ? " ball=1" : "") + (o.AltFixOff ? " altfix=0" : "") + (o.SmoothOff ? " smooth=0" : "") + o.Fwd
                    + (o.Duel ? (i == 1 ? " duel=runner" : " duel=shooter") + (o.Push ? " push=1" : "") : "")
                    + (o.Seek != null && i == 1 ? $" seek={o.Seek}" : "")
                    + (o.Loss > 0 ? $" loss={(o.Loss * 100).ToString(CultureInfo.InvariantCulture)}" : "")
                    + (o.Jitter > 0 ? $" jitter={o.Jitter}" : "")
                    + (HitClaims.OutboxOff ? " p2=0" : "")
                    + (o.Turn != null ? $" turn={o.Turn}" : "")
                    + (rejoin > 0 ? $" rejoin={rejoin.ToString(CultureInfo.InvariantCulture)} rejoinwait="
                        + o.RejoinWait.ToString(CultureInfo.InvariantCulture) : "")
                    + (wifi > 0 ? $" wifi={wifi.ToString(CultureInfo.InvariantCulture)} wifiwait="
                        + o.WifiWait.ToString(CultureInfo.InvariantCulture) + (o.Uplink ? " uplink=1" : "") + (o.TurnDead ? " turndead=1" : "") + (o.Stale ? " stale=1" : "") : "");
                joiners.Add((Start(Environment.ProcessPath!, a, output), output));
            }

            var clock = Stopwatch.StartNew();
            int seen = 0;
            while (net.Joined < want && clock.ElapsedMilliseconds < (o.External ? 600000 : 60000))
            {
                if (o.External && net.Joined != seen)
                {
                    seen = net.Joined;
                    Console.WriteLine($"host: {seen}/{want} joined ({net.Status})");
                }
                Thread.Sleep(20);
            }
            Console.WriteLine($"host: {net.Joined}/{want} joined after {clock.ElapsedMilliseconds} ms ({net.Status})");
            if (net.Joined < want)
            {
                lines.Add("FAIL not everyone joined");
                Wait(online.RequestAsync<OkMsg>(new LeaveGameReq()));
                Wait(online.DisposeAsync().AsTask().ContinueWith(_ => 0));
                return;
            }
            net.SendStart();
            Wait(online.RequestAsync<OkMsg>(new StartGameReq { Started = true }));
            MatchSettings settings = net.Settings!;
            settings.HitClaims = o.Claims ? null : false;
            settings.ClientPosition = o.Positions ? null : false;
            Console.WriteLine("host: seats " + string.Join(", ", settings.Players.Select((p, i) =>
                $"{i}:{p.Hunter}{(p.Recolor > 0 ? $"/{p.Recolor}" : "")}{(p.Bot ? " bot" : p.Remote ? $" {p.Nickname}" : " local")}")));
            CampaignHost host = CampaignHost.StartMatch(settings);
            if (o.Looks)
            {
                lines.Add(SeatLooksTest.Looks(settings));
                lines.Add(SeatLooksTest.Abilities(settings.Players.Count));
                Console.WriteLine($"host: {lines[^2]}, {lines[^1]}");
            }
            var driver = new MatchNetDriver(net, host);
            host.LagCompOn = o.LagComp;
            // wifi= (release 0.2.0 F1): the seat that came back must play again here -- spawned and drawn, hit by others
            // and hitting them, and still in at the end
            int backSeat = net.RemoteSeats.Count > 0 ? net.RemoteSeats[^1] : -1;
            bool backWatch = false, backDrawn = false;
            int backTaken = 0, backDealt0 = 0, backHealth = 0;
            RealTime(host, () =>
            {
                driver.Step(o.Strafe ? Strafe(host) : o.External ? Brain(host) : new CampaignInput { SelectWeapon = BeamType.None });
                SampleExtras(settings.Players.Count);
                if (o.Seek != null && settings.Players.Count > 1)
                {
                    SeekLog("host seat 1", host, PlayerEntity.Players[1]);
                    // the item put down where the joiner stands (every 10 s), so it walks into it
                    PlayerEntity j = PlayerEntity.Players[1];
                    if (host.Frame % 600 == 300 && j.Health > 0)
                    {
                        foreach (ItemInstanceEntity it in host.Scene.GetItemInstanceEntities())
                        {
                            if (!String.Equals(it.ItemType.ToString(), o.Seek, StringComparison.OrdinalIgnoreCase)) continue;
                            it.Position = j.Position + new Vector3(0, 0.65f, 0);
                            Console.WriteLine($"SEEK host frame {host.Frame}: {it.ItemType} put at seat 1 {j.Position}");
                            break;
                        }
                    }
                }
                if ((o.Wifi > 0 || o.Rejoin > 0) && backSeat >= 0 && GameState.MatchState == MatchState.InProgress)
                {
                    PlayerEntity b = PlayerEntity.Players[backSeat];
                    if (!backWatch && driver.SeatsBack > 0)
                    {
                        backWatch = true;
                        backDealt0 = GameState.BeamDamageDealt[backSeat];
                        Console.WriteLine($"host: seat {backSeat} back at frame {host.Frame}");
                    }
                    if (backWatch)
                    {
                        bool up = b.LoadFlags.TestFlag(LoadFlags.Active) && b.LoadFlags.TestFlag(LoadFlags.Spawned) && b.Health > 0;
                        if (up && !b.Flags2.TestFlag(PlayerFlags2.HideModel) && !backDrawn)
                        {
                            backDrawn = true;
                            Console.WriteLine($"host: seat {backSeat} spawned and drawn at frame {host.Frame}");
                        }
                        if (up && b.Health < backHealth)
                        {
                            backTaken++;
                        }
                        backHealth = up ? b.Health : 0;
                    }
                }
            },
                o.HostLeave > 0 ? o.HostLeave : o.Time + 15);
            if (o.Wifi > 0 || o.Rejoin > 0)
            {
                PlayerEntity b = PlayerEntity.Players[Math.Max(backSeat, 0)];
                lines.Add($"BACKSEAT watched={backWatch} drawn={backDrawn} taken={backTaken} "
                    + $"dealt={GameState.BeamDamageDealt[Math.Max(backSeat, 0)] - backDealt0} active={b.LoadFlags.TestFlag(LoadFlags.Active)} "
                    + $"gone={driver.SeatsGone} back={driver.SeatsBack}");
                Console.WriteLine("host: " + lines[^1]);
            }
            if (host.Ended)
            {
                lines.Add(EndedAt());
            }
            if (o.HostLeave > 0)
            {
                // the host quits mid-match: BYE to every device, then out of the game on the server (hostcrash: neither, the
                // relay goes quiet and the server connection drops)
                if (o.HostCrash)
                {
                    net.Vanish();
                    relay.Dispose();
                }
                else
                {
                    net.Dispose();
                }
                lines.Add("HOSTLEFT " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Console.WriteLine($"host: {(o.HostCrash ? "crashes" : "quits")} at frame {host.Frame}, match time "
                    + $"{GameState.MatchTime:0.00}, seats {string.Join(",", settings.Players.Select((p, i) => $"{i}:{(p.Bot ? "bot" : p.Remote ? "remote" : "host")}"))}");
                if (!o.HostCrash)
                {
                    Wait(online.RequestAsync<OkMsg>(new LeaveGameReq()));
                }
                host.Dispose();
                Wait(online.DisposeAsync().AsTask().ContinueWith(_ => 0));
                return;
            }
            Console.WriteLine($"host: {host.Frame} frames, ended {host.Ended}, {driver.StatusLine()}, relay ping {relay.PingMs} ms");
            Console.WriteLine("host: link " + net.NetStats());
            if (_hybrid != null)
            {
                Console.WriteLine("host: " + _hybrid.Summary());
                lines.Add($"TURNPACKETS {_hybrid.TurnPackets} {_hybrid.RelayPackets} {_hybrid.Fallbacks}");
            }
            // N4: the joiners' beam hits that counted here (each joiner prints the hits it saw), the rewind of each, and how
            // often a beam was tested against an older pose
            Console.WriteLine($"host: lag comp {(o.LagComp ? "on" : "off")}, " + string.Join(", ", net.RemoteSeats.Select(s =>
                $"seat {s} hits {PlayerEntity.NetBeamHits[s]} rewind {PlayerEntity.Players[s].HostLagRewind}"))
                + $", shifted tests {host.Lag?.Shifted ?? 0}");
            Console.WriteLine("host: shots " + ShotReport.HostLine(settings.Players.Count));
            Console.WriteLine("host: shotcheck " + ShotCheck.HostLine());
            Console.WriteLine($"host: claims ({(o.Claims ? "on" : "off")}, {host.ClaimsApplied} applied) " + HitClaims.HostLine());
            Console.WriteLine($"host: positions ({(o.Positions ? "on" : "off")}, {o.Hz} Hz, {host.PositionsApplied} taken, client-run pushes: {host.PushSpeedsTaken} speeds taken, {host.PushHeldFrames} held frames kept) " + ClientPosition.HostLine());
            lines.Add($"POSITIONS {host.PositionsApplied}");
            Console.WriteLine("host: lag " + host.LagStatsLine(reset: false));
            lines.Add("HITS " + string.Join(",", net.RemoteSeats.Select(s => $"{s}:{PlayerEntity.NetBeamHits[s]}")));
            Console.WriteLine("host: inputs " + string.Join(", ", net.RemoteSeats.Select(s => $"seat {s} {net.InputsFrom(s)}"))
                + (driver.SeatsGone > 0 ? $", {driver.SeatsGone} seat(s) dropped" : ""));
            Console.WriteLine($"host: rejoins {net.Rejoins}, dropped seats back {driver.SeatsBack}");
            lines.Add("SEATSGONE " + driver.SeatsGone);
            lines.Add("REJOINS " + net.Rejoins);
            lines.Add("SEATSBACK " + driver.SeatsBack);
            lines.Add("INPUTS " + string.Join(",", net.RemoteSeats.Select(s => net.InputsFrom(s))));
            lines.Add(Scores(settings.Players.Count, host.Scene));
            lines.Add(Board());
            lines.Add(Results(settings.Players.Count));
            if (ResultsProblem(settings.Players.Count) is string bad)
            {
                lines.Add("FAIL host results: " + bad);
            }
            lines.Add(ObjectiveEvents());
            lines.Add(Detail(settings.Players.Count));
            Console.WriteLine($"host: {Afflictions()}");
            Console.WriteLine("host scoreboard:\n" + MatchStatus.Scoreboard());
            host.Dispose();
            Wait(online.RequestAsync<OkMsg>(new StartGameReq { Started = false }));
            Wait(online.RequestAsync<OkMsg>(new LeaveGameReq()));
            Wait(online.DisposeAsync().AsTask().ContinueWith(_ => 0));
        }

        private static void Verdict(Options o, List<string> host, List<List<string>> joiners)
        {
            string? hostScores = host.FirstOrDefault(l => l.StartsWith("SCORES "));
            var problems = new List<string>(host.Where(l => l.StartsWith("FAIL")));
            if (hostScores == null && problems.Count == 0 && o.HostLeave == 0)
            {
                problems.Add("the host has no scoreboard");
            }
            long hostLeft = Int64.Parse(host.FirstOrDefault(l => l.StartsWith("HOSTLEFT "))?[9..] ?? "0");
            if (o.HostLeave > 0 && hostLeft == 0 && problems.Count == 0)
            {
                problems.Add("the host never quit");
            }
            int expectedGone = o.Leave > 0 ? 1 : 0;
            string? gone = host.FirstOrDefault(l => l.StartsWith("SEATSGONE "));
            if (gone != null && gone != $"SEATSGONE {expectedGone}")
            {
                problems.Add($"host dropped {gone[10..]} seat(s), expected {expectedGone}");
            }
            string? hostLooks = host.FirstOrDefault(l => l.StartsWith("LOOKS "));
            if (o.Looks && (hostLooks == null || !host.Contains(SeatLooksTest.AbilitiesExpected(hostLooks))) && problems.Count == 0)
            {
                problems.Add("host " + (hostLooks == null ? "has no LOOKS line" : host.FirstOrDefault(l => l.StartsWith("ABILITIES "))
                    + $" (expected {SeatLooksTest.AbilitiesExpected(hostLooks)}: abilities on for each Trooper seat)"));
            }
            string? rejoins = host.FirstOrDefault(l => l.StartsWith("REJOINS "));
            if (o.Rejoin > 0 && rejoins == "REJOINS 0")
            {
                problems.Add("the host never saw the joiner rejoin");
            }
            // a restarted app whose seat the host dropped (rejoinwait past 10 s) plays on too (release 0.2.0 F1)
            string? backRejoin = host.FirstOrDefault(l => l.StartsWith("BACKSEAT "));
            if (o.Rejoin > 0 && o.RejoinWait > 10 && backRejoin != null)
            {
                var f = backRejoin[9..].Split(' ').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
                if (f["watched"] != "True" || f["drawn"] != "True" || f["taken"] == "0" || f["dealt"] == "0"
                    || f["active"] != "True" || f["gone"] != "0" || f["back"] != "1")
                {
                    problems.Add($"the restarted joiner's seat doesn't play on at the host: {backRejoin} (expected drawn, taken > 0, "
                        + "dealt > 0, active, gone 0, back 1)");
                }
            }
            if (o.Wifi > 0)
            {
                List<string> last = joiners.Count > 0 ? joiners[^1] : new List<string>();
                string? reconnects, ended;
                lock (last)
                {
                    reconnects = last.LastOrDefault(l => l.StartsWith("RECONNECTS "));
                    ended = last.LastOrDefault(l => l.StartsWith("ENDED "));
                }
                // (uplink=1: it never lost the host, so there is nothing to reconnect)
                if (!o.Uplink && (reconnects == null || reconnects == "RECONNECTS 0"))
                {
                    problems.Add($"the joiner whose Wi-Fi went away didn't get back in ({reconnects ?? "no RECONNECTS line"})");
                }
                // (after GAME OVER it's the test host quitting at its own end: a joiner a few frames behind it, as one back from
                // a cut is, hears it leave in its last frames; in the app the host stays on RESULTS)
                bool overFirst;
                lock (last)
                {
                    overFirst = last.Any(l => l.Contains("state GameOver at frame"));
                }
                if (ended != null && ended.EndsWith("gone=True") && !overFirst)
                {
                    problems.Add("the joiner whose Wi-Fi went away ended its match (lost the host)");
                }
                if (o.WifiWait > 10 && host.Contains("SEATSBACK 0"))
                {
                    problems.Add("the host never took the dropped seat back");
                }
                // release 0.2.0 F1 ("X killed you" on a rejoin): the seat set up afresh is no death here, and it spawns again
                string? reset;
                lock (last)
                {
                    reset = last.LastOrDefault(l => l.StartsWith("REJOINRESET "));
                }
                // (uplink=1: it still hears the match, so its first drop to no health may be a real death there)
                if (o.WifiWait > 10 && reset != null && reset.StartsWith("REJOINRESET played") && !o.Uplink)
                {
                    problems.Add($"the joiner whose Wi-Fi went away: {reset} (expected a quiet reset, no death)");
                }
                else if (o.WifiWait > 10 && reset != null && reset.EndsWith("spawned=False"))
                {
                    problems.Add($"the joiner whose Wi-Fi went away never spawned again ({reset})");
                }
                // ... and plays on: the host has the seat up and drawn, it's hit and hits back, it stays in to the end, and its
                // connection doesn't drop again (the owner, 10-11: invisible, unhittable, then "reconnecting" with Wi-Fi on)
                string? back = host.FirstOrDefault(l => l.StartsWith("BACKSEAT "));
                if (o.WifiWait > 10 && back != null)
                {
                    var f = back[9..].Split(' ').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
                    if (f["watched"] != "True" || f["drawn"] != "True" || f["taken"] == "0" || f["dealt"] == "0"
                        || f["active"] != "True" || f["gone"] != "0" || f["back"] != "1")
                    {
                        problems.Add($"the seat whose Wi-Fi went away doesn't play on at the host: {back} (expected drawn, "
                            + "taken > 0, dealt > 0, active, gone 0, back 1)");
                    }
                }
                if (!o.Uplink && reconnects != null && reconnects != "RECONNECTS 1")
                {
                    problems.Add($"the joiner whose Wi-Fi went away reconnected more than once ({reconnects})");
                }
            }
            if (o.Turn != null)
            {
                foreach ((string who, List<string> l) in new[] { ("host", host) }.Concat(joiners.Select((j, i) => ($"joiner {i + 1}", j))))
                {
                    string? tp;
                    lock (l)
                    {
                        tp = l.LastOrDefault(x => x.StartsWith("TURNPACKETS "));
                    }
                    long[] n = tp?.Split(' ').Skip(1).Select(Int64.Parse).ToArray() ?? new long[] { 0, 1, 0 };
                    if (o.TurnKill > 0 || o.Wifi > 0)
                    {
                        // TURN first, then the relay after it went away (wifi=: the joiner's TURN sends went with its network)
                        if (n[0] == 0 || n.Length < 3 || n[2] == 0)
                        {
                            problems.Add($"{who} didn't use TURN then fall back to the relay ({tp ?? "no TURNPACKETS"})");
                        }
                    }
                    else if (n[0] <= n[1] * 4 && !(o.HostLeave > 0))
                    {
                        problems.Add($"{who} sent {n[0]} packets over TURN, {n[1]} over the relay");
                    }
                }
            }
            string? inputs = host.FirstOrDefault(l => l.StartsWith("INPUTS "));
            if (inputs != null && inputs[7..].Split(',').Any(n => Int32.Parse(n) == 0))
            {
                problems.Add("a seat sent no inputs: " + inputs);
            }
            string? newHostScores = null;
            // O5b: the successor is the lowest relay slot still online (= the joiner whose JOIN the server took first)
            int successor = -1, lowestSlot = int.MaxValue;
            for (int i = 0; i < joiners.Count; i++)
            {
                lock (joiners[i])
                {
                    string? slotLine = joiners[i].FirstOrDefault(l => l.Contains(": relay slot "));
                    if (slotLine != null && int.TryParse(slotLine.Split(": relay slot ")[1].Split(',')[0], out int slot)
                        && slot < lowestSlot)
                    {
                        lowestSlot = slot;
                        successor = i;
                    }
                }
            }
            for (int i = 0; i < joiners.Count; i++)
            {
                List<string> lines;
                lock (joiners[i])
                {
                    lines = joiners[i].ToList();
                }
                if (o.HostLeave > 0)
                {
                    // O5b: the successor (lowest relay slot) takes over; the rest follow it to the match's end
                    string? ended = lines.FirstOrDefault(l => l.StartsWith("ENDED "));
                    string? migration = lines.FirstOrDefault(l => l.StartsWith("MIGRATION "));
                    string? endScores = lines.FirstOrDefault(l => l.StartsWith("SCORES "));
                    string? hostSeat = lines.FirstOrDefault(l => l.StartsWith("OLDHOST "));
                    string want = i == successor ? "MIGRATION took=True" : "MIGRATION took=False";
                    if (ended == null || !ended.EndsWith("gone=False"))
                    {
                        problems.Add($"joiner {i + 1} didn't finish the match ({ended ?? "no ENDED line"})");
                    }
                    if (migration == null || !migration.StartsWith(want) || migration.Contains("moves=0"))
                    {
                        problems.Add($"joiner {i + 1}: {migration ?? "no MIGRATION line"} (want {want[10..]})");
                    }
                    if (hostSeat == null || !hostSeat.Contains("active=False"))
                    {
                        problems.Add($"joiner {i + 1}: the old host's seat {hostSeat ?? "(no OLDHOST line)"}");
                    }
                    string? bots = lines.FirstOrDefault(l => l.StartsWith("BOTS "));
                    if (i == successor && o.Bots > 0 && (bots == null || bots.Contains("=False")))
                    {
                        problems.Add($"the new host's bots aren't bots: {bots ?? "no BOTS line"}");
                    }
                    if (endScores == null)
                    {
                        problems.Add($"joiner {i + 1} has no scoreboard");
                    }
                    else if (newHostScores == null)
                    {
                        newHostScores = endScores;
                    }
                    else if (endScores != newHostScores)
                    {
                        problems.Add($"joiner {i + 1} {endScores} vs the new host {newHostScores}");
                    }
                    continue;
                }
                if (o.Looks && hostLooks != null && SeatLooksTest.Problem($"joiner {i + 1}", hostLooks, lines.ToArray(), oldJoin: i == 0)
                    is string looksProblem)
                {
                    problems.Add(looksProblem);
                }
                bool left = o.Leave > 0 && i == joiners.Count - 1;
                string? scores = lines.FirstOrDefault(l => l.StartsWith("SCORES "));
                string? events = lines.FirstOrDefault(l => l.StartsWith("OBJEVENTS "));
                string? hostEvents = host.FirstOrDefault(l => l.StartsWith("OBJEVENTS "));
                if (!left && o.HostLeave == 0 && events != hostEvents)
                {
                    problems.Add($"joiner {i + 1} {events ?? "no OBJEVENTS"} vs host {hostEvents ?? "no OBJEVENTS"}");
                }
                string? detail = lines.FirstOrDefault(l => l.StartsWith("DETAIL "));
                string? hostDetail = host.FirstOrDefault(l => l.StartsWith("DETAIL "));
                if (!left && o.HostLeave == 0 && detail != hostDetail)
                {
                    problems.Add($"joiner {i + 1} {detail ?? "no DETAIL"} vs host {hostDetail ?? "no DETAIL"}");
                }
                if (scores == null)
                {
                    problems.Add($"joiner {i + 1} has no scoreboard");
                }
                else if (!left && scores != hostScores)
                {
                    problems.Add($"joiner {i + 1} {scores} vs host {hostScores}");
                }
                // the held-Select live scoreboard as the joiner draws it
                string? board = lines.FirstOrDefault(l => l.StartsWith("BOARD "));
                string? hostBoard = host.FirstOrDefault(l => l.StartsWith("BOARD "));
                if (!left && o.HostLeave == 0 && board != hostBoard)
                {
                    problems.Add($"joiner {i + 1} {board ?? "no BOARD"} vs host {hostBoard ?? "no BOARD"}");
                }
                // release 0.2.0 F1: the same RESULTS places, at about the same time (a match that ran to its end)
                string? results = lines.FirstOrDefault(l => l.StartsWith("RESULTS "));
                string? hostResults = host.FirstOrDefault(l => l.StartsWith("RESULTS "));
                bool rejoiner = o.Rejoin > 0 && i == joiners.Count - 1;
                if (!left && !o.External && results != hostResults)
                {
                    problems.Add($"joiner {i + 1} {results ?? "no RESULTS"} vs host {hostResults ?? "no RESULTS"}");
                }
                if (!left && !rejoiner && o.Wifi == 0 && host.Any(l => l.StartsWith("ENDAT "))
                    && EndLagProblem($"joiner {i + 1}", host, lines, 2500 + 2 * o.Delay + o.Jitter) is string lag)
                {
                    problems.Add(lag);
                }
            }
            if (o.HostLeave > 0)
            {
                Console.WriteLine(problems.Count == 0
                    ? $"PASS onlinetest: the host {(o.HostCrash ? "crashed" : "quit")} at {o.HostLeave:0.#} s, joiner {successor + 1} (relay slot {lowestSlot}) took over, "
                        + $"all {joiners.Count} joiner(s) finished the match with the same scores ({newHostScores})"
                    : "FAIL onlinetest: " + String.Join("; ", problems));
                return;
            }
            Console.WriteLine(problems.Count == 0
                ? o.External ? $"PASS onlinetest: the match with {o.Players - 1} outside device(s) ran to its end; host scores "
                    + $"{hostScores} (compare with the device's RESULTS)"
                : $"PASS onlinetest: {joiners.Count + 1} devices through the relay agree on the final scores ({hostScores})"
                    + (o.Looks ? $" and the seat looks ({hostLooks}), Trooper abilities on, a protocol 1 JOIN refused" : "")
                : "FAIL onlinetest: " + String.Join("; ", problems));
        }

        // ---------------------------------------------------------------- joiner (a separate process)

        public static void Join(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            UseImports();
            var uri = new Uri(o.Server!);
            string name = $"PC {o.Index}";
            _ballBrain = o.Ball;
            _duelRole = o.DuelRole;
            _pushDuel = o.Push;
            OnlineIdentity me = (o.Code != null ? OnlineIdentity.FromBackupCode(o.Code) : null) ?? OnlineIdentity.Create();
            OnlineClient online = Wait(OnlineClient.ConnectAsync(uri, me, name, "onlinetest"));
            if (!o.Again && o.Owner == 0)
            {
                GamesMsg list = Wait(online.RequestAsync<GamesMsg>(new ListGamesReq()));
                GameCard? card = list.Games.FirstOrDefault(g => g.Id == o.Game);
                Console.WriteLine($"joiner {o.Index}: {online.Me}, {list.Games.Count} open game(s), "
                    + (card == null ? $"game {o.Game} NOT listed" : $"{card.Id} by {card.Host} ({card.Players}/{card.Info.MaxPlayers})"));
            }
            var closed = new ManualResetEventSlim();
            online.EventReceived += m =>
            {
                if (m is GameClosedEvent c)
                {
                    Console.WriteLine($"CLOSED {c.Reason}");
                    closed.Set();
                }
            };
            // a restarted app asks for "my started game" (no id): the server gives it its slot back with a new token
            GameMsg game = o.Owner > 0 && !o.Again ? OwnServedGame(o, online)
                : Wait(online.RequestAsync<GameMsg>(new JoinGameReq { GameId = o.Again ? "" : o.Game, Version = VersionKey, Served = true }));
            if (o.Again)
            {
                Console.WriteLine($"joiner {o.Index}: back as {online.Me} in game {game.Game.Id}");
            }
            using RelayTransport relay = RelayTransport.For(game.Relay, uri);
            if (o.Looks && o.Index == 1)
            {
                Console.WriteLine(SeatLooksTest.OldOnlineJoin(relay, RomKey));
            }
            NetMatchSession net = NetMatchSession.Client(Link(o, relay, o.Index, online), RomKey);
            net.IsOwner = o.Owner > 0; // (a served game's lobby owner: OnlineServedTest.cs)
            net.LinkAlive = () => relay.RelayAlive; // (as OnlineMatch: reconnect tells our own network from the host's)
            // O5b: as OnlineMatch does in the app: the server names a new host when this one leaves or drops
            net.CanMigrate = online.ServerAtLeast(OnlineMatch.MigrateSinceServer);
            online.EventReceived += m =>
            {
                if (m is MemberEvent { HostSlot: int slot } moved && moved.GameId == game.Game.Id)
                {
                    Console.WriteLine($"joiner {o.Index}: server: slot {slot} hosts now (slot {moved.Slot} left)");
                    net.HostMoved(slot);
                }
                else if (m is MemberEvent { Owner: string owner } ownerMoved && ownerMoved.GameId == game.Game.Id && owner == online.Me.Code)
                {
                    Console.WriteLine($"joiner {o.Index}: server: this device owns the lobby now (slot {ownerMoved.Slot} left)");
                    net.IsOwner = true;
                }
                else if (m is GameClosedEvent closedGame && closedGame.GameId == game.Game.Id)
                {
                    net.CanMigrate = false;
                }
            };
            bool rejoined = false;
            try
            {
                if (!net.Join(o.Hunter, name, timeoutMs: 15000, character: o.Character, skin: o.Skin, colourPick: o.Colour))
                {
                    Console.WriteLine($"joiner {o.Index}: {net.Status}");
                    return;
                }
                Console.WriteLine($"joiner {o.Index}: relay slot {relay.LocalSlot}, {net.Status}");
                if (net.IsOwner && !o.Again && !OwnerSetup(o, net))
                {
                    return;
                }
                if (!net.WaitForStart(o.Again ? 15000 : 90000))
                {
                    Console.WriteLine($"joiner {o.Index}: no START ({net.Status})");
                    return;
                }
                int seat = net.Settings!.LocalSeat;
                Console.WriteLine($"joiner {o.Index}: {net.Status}");
                CampaignHost host = CampaignHost.StartMatch(net.Settings);
                // replays=1: the app guest's side of the replay link (OnlineServedReplay.cs; not a rejoin; the leaver asks for
                // the recording so far as it leaves, as the app does)
                using JoinerReplay? replay = o.Replays == true && o.Rejoin == 0 && o.Wifi == 0 && !o.Again
                    ? JoinerReplay.Begin(net, o.Index, seat, me, name) : null;
                if (o.Looks)
                {
                    Console.WriteLine(SeatLooksTest.Looks(net.Settings));
                    Console.WriteLine(SeatLooksTest.Abilities(net.Settings.Players.Count));
                }
                var driver = new MatchNetDriver(net, host);
                using GapTrace? gap = o.Again ? null : GapTrace.Open("j" + o.Index); // (gap test: GAPTRACE set)
                bool wifiOff = false;
                long wifiOnFrame = -1;
                using Timer? wifiOffTimer = o.Wifi > 0 ? new Timer(_ =>
                {
                    wifiOff = true;
                    if (_hybrid?.Turn is TurnClient turnOff)
                    {
                        turnOff.SendOffline = true; // (TURN sends go with the network too; packets in still arrive)
                    }
                    if (o.Uplink)
                    {
                        // only the sends are lost: the host's snapshots still come, the server connection stays
                        relay.SendOffline = true;
                        Console.WriteLine($"joiner {o.Index}: UPLINK OFF at frame {host.Frame} for {o.WifiWait:0.#} s");
                        return;
                    }
                    // the network goes away: nothing in or out on the relay, the server connection drops (as Wi-Fi off)
                    relay.Offline = true;
                    Console.WriteLine($"joiner {o.Index}: WIFI OFF at frame {host.Frame} for {o.WifiWait:0.#} s");
                    _ = online.DisposeAsync().AsTask();
                }, null, (int)(o.Wifi * 1000), Timeout.Infinite) : null;
                using Timer? wifiOnTimer = o.Wifi > 0 ? new Timer(_ =>
                {
                    relay.Offline = false;
                    relay.SendOffline = false;
                    if (o.Stale)
                    {
                        relay.KillSocketSends();
                    }
                    if (_hybrid?.Turn is TurnClient turnOn && !o.TurnDead)
                    {
                        turnOn.SendOffline = false;
                    }
                    wifiOnFrame = host.Frame;
                    Console.WriteLine($"joiner {o.Index}: WIFI ON at frame {host.Frame} (reconnecting {net.Reconnecting})");
                }, null, (int)((o.Wifi + o.WifiWait) * 1000), Timeout.Infinite) : null;
                // wifi= (release 0.2.0 F1): the host sets the dropped seat up afresh (no health, not spawned); here the
                // own player lives on, so its first drop to no health after the network went is that reset -- it must be
                // quiet (Spawned cleared, as the host's seat), not a death the game plays (Spawned kept) credited to
                // somebody; then the seat must spawn again
                string? rejoinReset = null;
                bool rejoinSpawned = false;
                int lastHealth = host.Player.Health;
                RealTime(host, () =>
                {
                    driver.Step(o.Seek != null ? SeekBrain(host, o.Seek) : Brain(host));
                    gap?.Step(host.Frame, seat);
                    if (_pushDuel) PushWatch(host, o.Index);
                    SampleExtras(net.Settings.Players.Count);
                    if (o.Seek != null) SeekLog($"joiner {o.Index}", host, host.Player);
                    int health = host.Player.Health;
                    // (only around the return: a drop to no health later on is play, a real death; a seat the host spawns
                    // again at once, a held FIRE, never shows no health here at all)
                    if (wifiOff && rejoinReset == null && lastHealth > 0 && health == 0
                        && (wifiOnFrame < 0 || host.Frame - wifiOnFrame < 300))
                    {
                        bool played = host.Player.LoadFlags.TestFlag(LoadFlags.Spawned);
                        rejoinReset = played ? "played" : "quiet";
                        Console.WriteLine($"joiner {o.Index}: own seat reset at frame {host.Frame}: {rejoinReset}");
                    }
                    // (spawned and drawn after the return; a seat the host spawned again at once shows no reset at all)
                    else if ((rejoinReset != null || wifiOnFrame >= 0 && host.Frame - wifiOnFrame > 300) && !rejoinSpawned && health > 0
                        && host.Player.LoadFlags.TestFlag(LoadFlags.Spawned) && !host.Player.Flags2.TestFlag(PlayerFlags2.HideModel))
                    {
                        rejoinSpawned = true; // (spawned here too: drawn, in play)
                        Console.WriteLine($"joiner {o.Index}: own seat spawned again at frame {host.Frame}");
                    }
                    lastHealth = health;
                }, o.Leave > 0 ? o.Leave : o.Rejoin > 0 && !o.Again ? o.Rejoin : o.Time + 25);
                if (o.Leave > 0)
                {
                    replay?.AskCutShort(host.Frame);
                }
                if (o.Wifi > 0)
                {
                    Console.WriteLine($"REJOINRESET {rejoinReset ?? "none"} spawned={rejoinSpawned}");
                }
                if (host.Ended)
                {
                    Console.WriteLine(EndedAt());
                }
                if (o.Rejoin > 0 && !o.Again)
                {
                    // rejoin=: gone without a word (no BYE, no LeaveGame) as a crash, then the app starts again
                    net.Vanish();
                    Console.WriteLine($"joiner {o.Index}: VANISHED at frame {host.Frame}, back in {o.RejoinWait:0.#} s");
                    host.Dispose();
                    Wait(online.DisposeAsync().AsTask().ContinueWith(_ => 0));
                    relay.Dispose();
                    Thread.Sleep((int)(o.RejoinWait * 1000));
                    var again = new List<string>();
                    string a = $"\"{typeof(OnlineMatchTool).Assembly.Location}\" -onlinejoin " + string.Join(" ", args.Skip(1)
                        .Where(x => !x.StartsWith("rejoin") && !x.StartsWith("code="))) + $" again=1 code={me.BackupCode}";
                    Process p = Start(Environment.ProcessPath!, a, again);
                    p.WaitForExit();
                    p.WaitForExit(); // (the end of its output)
                    lock (again)
                    {
                        foreach (string line in again)
                        {
                            Console.WriteLine(line);
                        }
                    }
                    rejoined = true;
                    return;
                }
                if (o.Again)
                {
                    Console.WriteLine($"joiner {o.Index}: own player {(host.Player.Health > 0 ? "up" : "down")}, health "
                        + host.Player.Health);
                }
                Console.WriteLine($"ENDED {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} gone={driver.PeerGone}");
                if (o.Wifi > 0)
                {
                    Console.WriteLine($"joiner {o.Index}: back in {net.Reconnects} time(s), relay sockets made again {relay.Rebuilds}");
                    Console.WriteLine("RECONNECTS " + net.Reconnects);
                }
                // O5b: whether this device took over / followed, the old host's seat (seat 0) and the bots as this device has them
                Console.WriteLine($"MIGRATION took={driver.TookOver} moves={driver.HostMoves} hostslot={net.HostSlot} ({net.Status})");
                Console.WriteLine($"OLDHOST seat 0 active={PlayerEntity.Players[0].LoadFlags.TestFlag(LoadFlags.Active)}");
                if (driver.TookOver)
                {
                    Console.WriteLine("BOTS " + string.Join(",", net.Settings.Players.Select((p, i) => (p, i)).Where(x => x.p.Bot)
                        .Select(x => $"{x.i}={PlayerEntity.Players[x.i].IsBot}")));
                }
                if (driver.PeerGone)
                {
                    closed.Wait(3000); // the server's ev_game_closed
                }
                Console.WriteLine($"joiner {o.Index}: {host.Frame} frames as seat {seat}, {driver.StatusLine()}, own player pulled "
                    + $"back {host.LocalCorrections} times, ended {host.Ended}, relay ping {relay.PingMs} ms");
                Console.WriteLine($"joiner {o.Index}: link {net.NetStats()}; {driver.Prediction?.Summary()}");
                if (driver.Smoothing != null) Console.WriteLine($"joiner {o.Index}: {driver.Smoothing.Summary()}");
                Console.WriteLine($"joiner {o.Index}: paced: skipped {driver.PaceSkipped}, ran twice {driver.PaceDoubled}");
                if (_hybrid != null)
                {
                    Console.WriteLine($"joiner {o.Index}: {_hybrid.Summary()}");
                    Console.WriteLine($"TURNPACKETS {_hybrid.TurnPackets} {_hybrid.RelayPackets} {_hybrid.Fallbacks}");
                }
                Console.WriteLine($"joiner {o.Index}: own beam hits seen here {PlayerEntity.NetBeamHits[seat]}");
                Console.WriteLine($"SAWHITS {seat}:{PlayerEntity.NetBeamHits[seat]}");
                Console.WriteLine($"joiner {o.Index}: " + HitClaims.GuestLine());
                Console.WriteLine($"CLAIMSUNHEARD {seat}:{HitClaims.Unheard}");
                Console.WriteLine(Scores(net.Settings.Players.Count, host.Scene));
                // (the order from the points as they stand, as the next frame's board and MatchResults.Capture have it: the
                // last snapshot can land after this device's last UpdateState, two tied seats then left in their old order)
                GameState.UpdateState(voices: false);
                Console.WriteLine(Board());
                Console.WriteLine(Results(net.Settings.Players.Count));
                Console.WriteLine(ObjectiveEvents());
                Console.WriteLine(Detail(net.Settings.Players.Count));
                Console.WriteLine($"joiner {o.Index}: {Afflictions()}");
                Console.WriteLine($"joiner {o.Index} scoreboard:\n" + MatchStatus.Scoreboard());
                host.Dispose();
                replay?.Finish();
                for (int match = 2; match <= o.Matches && o.Leave == 0 && !(o.OwnerLeave && o.Owner > 0); match++)
                {
                    if (!PlayAgain(o, net, match))
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (!rejoined)
                {
                    net.Dispose();
                    if (online.Connected) // (wifi=: that connection went with the network)
                    {
                        Wait(online.RequestAsync<OkMsg>(new LeaveGameReq()));
                    }
                    Wait(online.DisposeAsync().AsTask().ContinueWith(_ => 0));
                }
            }
        }

        // ---------------------------------------------------------------- shared

        // this device's side of the relay (turn=: TURN first, attached when the server's credentials + the allocation come
        // through, as OnlineMatch does in the app), with loss= / jitter= when asked
        private static HybridTransport? _hybrid;
        private static Timer? _turnKill;

        private static IDatagramTransport Link(Options o, RelayTransport relay, int index, OnlineClient online)
        {
            IDatagramTransport link = relay;
            if (o.Turn != null)
            {
                var hybrid = new HybridTransport(relay);
                link = _hybrid = hybrid;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    (TurnClient? turn, string? note) = await OnlineMatch.RequestTurnAsync(online);
                    if (turn == null || !hybrid.Attach(turn))
                    {
                        Console.WriteLine($"device {index}: no TURN ({note ?? "closed"}): relay only");
                    }
                    else
                    {
                        Console.WriteLine($"device {index}: TURN {turn.Relayed} on {turn.Server}");
                    }
                });
            }
            return o.Loss > 0 || o.Jitter > 0 || o.Lag > 0 || (o.Burst > 0 && (o.BurstSeat < 0 || o.BurstSeat == index))
                ? new SimulatedLink(link, o.Loss, o.Jitter, seed: 1000 + index, o.Lag, o.BurstSeat < 0 || o.BurstSeat == index ? o.Burst : 0) : link;
        }

        // (with the mode's state: prime hunter + mode stats, NetObjective)
        internal static string Scores(int seats, Scene? scene = null) => "SCORES " + string.Join("|", Enumerable.Range(0, seats)
            .Select(i => $"{GameState.Points[i]},{GameState.Kills[i]},{GameState.Deaths[i]}")) + " OBJ " + NetObjective.Summary() + World(scene);

        // what the held-Select live scoreboard draws (PlayerHud.DrawScoreboard: ResultSlots order, team rows, the mode's two
        // columns): every device must agree, joiners included (live scoreboard thread, 2026-10-11)
        internal static string Board()
        {
            GameMode mode = GameState.Mode;
            bool timeColumn = mode is GameMode.Survival or GameMode.SurvivalTeams or GameMode.Defender or GameMode.DefenderTeams
                or GameMode.PrimeHunter;
            bool deathsColumn = mode is GameMode.Battle or GameMode.BattleTeams or GameMode.Survival or GameMode.SurvivalTeams;
            string Value1(float time, int points) => timeColumn ? (time < 0 ? "MAX" : ((int)time).ToString()) : points.ToString();
            string Value2(int deaths, int kills) => deathsColumn ? deaths.ToString() : kills.ToString();
            var parts = new List<string>();
            int curTeam = 4;
            for (int i = 0; i < GameState.ActivePlayers; i++)
            {
                int slot = GameState.ResultSlots[i];
                PlayerEntity player = PlayerEntity.Players[slot];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                if (GameState.Teams && player.TeamIndex != curTeam)
                {
                    curTeam = player.TeamIndex;
                    parts.Add($"T{curTeam}:{Value1(GameState.TeamTime[curTeam], GameState.TeamPoints[curTeam])},"
                        + Value2(GameState.TeamDeaths[curTeam], GameState.TeamKills[curTeam]));
                }
                parts.Add($"{slot}:{Value1(GameState.Time[slot], GameState.Points[slot])},"
                    + $"{Value2(GameState.Deaths[slot], GameState.Kills[slot])},{GameState.Stars[slot]}");
            }
            return "BOARD " + String.Join(" ", parts);
        }

        // the Echoes crates + breakables: how many and how often each broke (every device must agree: Net/NetWorld.cs)
        internal static string World(Scene? scene)
        {
            if (scene == null) return "";
            var list = NetWorld.Elements(scene);
            // (+ Shooting Gallery's turret: how often it blew up; Arenas/EchoesTurret.cs)
            string turret = NetWorld.TurretOf(scene) is { } t ? $" TURRET {t.Destructions}" : "";
            return (list.Count == 0 ? "" : " WORLD " + string.Join(",", list.Select(e => e switch
            {
                MphRecomp.Arenas.EchoesCrateEntity c => $"c{c.Breaks}",
                MphRecomp.Arenas.EchoesBreakableEntity b => $"b{b.Breaks}",
                _ => "?"
            }))) + turret;
        }

        // each seat's place as the RESULTS page shows it (GameState.Standings, team:place), the result order, and the table
        // itself (every row's columns + the place line it would show that seat); every device must agree (release 0.2.0 F1:
        // a team match read "tied for 2nd" on one device, "last place" on the other; then the guest's "tied for 1st place"
        // beside the host's "2nd place", and its efficiency off). Taken the way the page takes it (MatchResults.Capture first)
        internal static string Results(int seats)
        {
            var r = MphRecomp.Frontend.MatchResults.Capture(PlayerEntity.MainPlayerIndex);
            return "RESULTS " + string.Join("|", Enumerable.Range(0, seats)
                .Select(i => $"{PlayerEntity.Players[i].TeamIndex}:{GameState.Standings[i]}"))
                + " order " + string.Join(",", Enumerable.Range(0, Math.Min(seats, GameState.ActivePlayers)).Select(i => GameState.ResultSlots[i]))
                + " " + ResultsShownLine() + " table " + ResultsTable(r);
        }

        // per row: seat, place (t = tied: the place line's "tied for"), points, deaths, kills, efficiency, best weapon, damage,
        // headshot / alt-form / friendly kills, suicides, the mode's two counters
        internal static string ResultsTable(MphRecomp.Frontend.MatchResults r) => string.Join("|", r.Rows.Select(p =>
        {
            bool tied = r.Rows.Any(q => q.Seat != p.Seat && q.Place == p.Place && (!r.Teams || q.Team != p.Team));
            return $"{p.Seat}:{p.Place}{(tied ? "t" : "")}:{p.Points},{p.Deaths},{p.Kills},{p.Efficiency}%,{p.BestWeapon},"
                + $"{p.DamageDealt},{p.HeadshotKills},{p.AltFormKills},{p.FriendKills},{p.SelfDestructs},{p.Field260},{p.Field268}";
        }));

        // the RESULTS page's table as it would be filled (every seat, bots included, must have a row; release 0.2.0 F1: a
        // tied team match's 4th player fell off it)
        internal static string ResultsShownLine()
        {
            var r = MphRecomp.Frontend.MatchResults.Capture(PlayerEntity.MainPlayerIndex);
            int shown = MphRecomp.Frontend.MultiplayerMenu.ResultsShown(r).Where(x => x != null).Select(x => x!.Seat).Distinct().Count();
            return $"shown {shown}/{r.Rows.Count}";
        }

        // a team match's places that can't be: teammates placed apart, or a seat with no place (3 = never set)
        internal static string? ResultsProblem(int seats)
        {
            string shownLine = ResultsShownLine();
            if (shownLine.Split(' ')[1] is string n && n.Split('/') is [string a, string b] && a != b)
            {
                return $"the RESULTS table leaves players out ({shownLine})";
            }
            if (!MatchSettings.IsTeamMode(GameState.Mode))
            {
                return null;
            }
            var byTeam = new Dictionary<int, int>();
            for (int i = 0; i < seats; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                int place = GameState.Standings[i];
                if (place > 1)
                {
                    return $"seat {i} has team place {place}";
                }
                if (byTeam.TryGetValue(p.TeamIndex, out int mate) && mate != place)
                {
                    return $"team {p.TeamIndex} placed apart ({mate} and {place})";
                }
                byTeam[p.TeamIndex] = place;
            }
            return null;
        }

        // when this device's match ended (wall clock), to compare with the host's: a joiner hanging on GAME OVER after the
        // host went to RESULTS (release 0.2.0 F1) ends seconds later
        internal static string EndedAt() => "ENDAT " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // a joiner's end against the host's: fine within allowedMs
        internal static string? EndLagProblem(string who, IEnumerable<string> host, IEnumerable<string> joiner, int allowedMs)
        {
            string? h = host.LastOrDefault(l => l.StartsWith("ENDAT ")), j = joiner.LastOrDefault(l => l.StartsWith("ENDAT "));
            if (h == null || j == null)
            {
                return $"{who}: no ENDAT line ({(h == null ? "host" : who)})";
            }
            long lag = Int64.Parse(j[6..]) - Int64.Parse(h[6..]);
            return lag > allowedMs ? $"{who} ended its match {lag} ms after the host (allowed {allowedMs})" : null;
        }

        // the detail stats (O5 extras: the host's on every device): per seat streak, suicides, friendly kills, headshot kills,
        // beam damage dealt, beam kills
        internal static string Detail(int seats) => "DETAIL " + string.Join("|", Enumerable.Range(0, seats)
            .Select(i => $"{GameState.KillStreak[i]},{GameState.Suicides[i]},{GameState.FriendlyKills[i]},"
                + $"{GameState.HeadshotKills[i]},{GameState.BeamDamageDealt[i]},"
                + Enumerable.Range(0, 9).Sum(b => GameState.BeamKills[i, b])));

        // seat-frames with each affliction / power-up running on this device (O5 extras: about the same on every device)
        private static readonly int[] _extraFrames = new int[6];

        private static void SampleExtras(int seats)
        {
            for (int i = 0; i < seats && i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerNetExtras e = PlayerEntity.Players[i].NetCaptureExtras();
                if (e.Frozen > 0) _extraFrames[0]++;
                if (e.Disrupted > 0) _extraFrames[1]++;
                if (e.Burn > 0) _extraFrames[2]++;
                if (e.Cloak > 0) _extraFrames[3]++;
                if (e.DoubleDamage > 0) _extraFrames[4]++;
                if (e.Turret > 0) _extraFrames[5]++;
            }
        }

        private static string Afflictions() => $"seat-frames frozen {_extraFrames[0]}, disrupted {_extraFrames[1]}, burning "
            + $"{_extraFrames[2]}, cloaked {_extraFrames[3]}, double damage {_extraFrames[4]}, turret up {_extraFrames[5]}";

        // the octolith events and node captures this device decided (host) or played (joiner): equal on every device
        internal static string ObjectiveEvents() => $"OBJEVENTS {(PlayerEntity.NetClient ? OctolithFlagEntity.NetEventsPlayed
            : OctolithFlagEntity.NetEventsRecorded)} nodes {NodeDefenseEntity.NetCompletes}";

        // SECONDHUNT_SERVER_DLL picks another server build (e.g. a session's own bin/Net output) over the shared Debug one.
        private static string OnOff(bool on) => on ? "on" : "off";

        private static string ServerDll() => Environment.GetEnvironmentVariable("SECONDHUNT_SERVER_DLL") is { Length: > 0 } dll
            ? Path.GetFullPath(dll)
            : Path.GetFullPath(Path.Combine(typeof(OnlineMatchTool).Assembly.Location, "..", "..",
                "..", "..", "..", "SecondHunt.Server", "bin", "Debug", "net9.0", "SecondHuntServer.dll"));

        private static Process Start(string exe, string arguments, List<string> output)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(typeof(OnlineMatchTool).Assembly.Location)!
            };
            Process p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("stderr: " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private static void Dump(string who, Process p, List<string> output)
        {
            lock (output)
            {
                foreach (string line in output)
                {
                    Console.WriteLine($"  [{who}] {line}");
                }
            }
        }

        private static bool WaitHealthy(int port)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 15000)
            {
                try
                {
                    if (Wait(http.GetStringAsync($"http://127.0.0.1:{port}/v1/health")).StartsWith("ok"))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Threading.Tasks.TaskCanceledException)
                {
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private static int FreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static int FreeUdpPort()
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }

        // steps at 60 Hz of wall-clock time until the match ends or the time runs out (as MatchNetLoop's)
        // (prints each match state change: where devices' matches part ways)
        private static void RealTime(CampaignHost host, Action step, float seconds)
        {
            var clock = Stopwatch.StartNew();
            long frames = 0;
            MatchState state = GameState.MatchState;
            while (!host.Ended && clock.Elapsed.TotalSeconds < seconds)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step();
                    frames++;
                    if (GameState.MatchState != state)
                    {
                        state = GameState.MatchState;
                        Console.WriteLine($"  state {state} at frame {host.Frame}, match time {GameState.MatchTime:0.00}");
                    }
                }
                Thread.Sleep(1);
            }
        }

        // strafe=1: the host's player runs left and right, 1.5 s each way (and presses FIRE to respawn)
        private static CampaignInput Strafe(CampaignHost host)
        {
            return new CampaignInput
            {
                SelectWeapon = BeamType.None,
                Move = new System.Numerics.Vector2(host.Frame / 90 % 2 == 0 ? 1 : -1, 0),
                Buttons = host.Player.Health == 0 ? CampaignButtons.Shoot : CampaignButtons.None
            };
        }

        // a joiner's player: hunt the nearest opponent it can see in its own scene (as MatchNetLoop's)
        // seek=: the nearest item of that type, walked to (no shooting); and each change of a seat's Pirate Blaster, weapon
        // and UA logged (the guest's blaster pickup: release 0.2.0)
        private static CampaignInput SeekBrain(CampaignHost host, string seek)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot;
                return input;
            }
            ItemInstanceEntity? target = null;
            float best = Single.MaxValue;
            foreach (ItemInstanceEntity it in host.Scene.GetItemInstanceEntities())
            {
                if (!String.Equals(it.ItemType.ToString(), seek, StringComparison.OrdinalIgnoreCase)) continue;
                float d = (it.Position - me.Position).LengthSquared;
                if (d < best)
                {
                    best = d;
                    target = it;
                }
            }
            if (host.Frame % 120 == 0)
            {
                Console.WriteLine($"SEEK at {me.Position} -> {(target == null ? "no " + seek : $"{target.Position} ({MathF.Sqrt(best):0.0})")}");
            }
            if (target != null)
            {
                Vector3 to = target.Position - me.Position;
                float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
                float dyaw = yaw - me.NetYaw;
                dyaw -= 360 * MathF.Floor((dyaw + 180) / 360);
                input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw, -12, 12), 0);
                if (MathF.Abs(dyaw) < 30) input.Move = new System.Numerics.Vector2(0, 1);
            }
            return input;
        }

        private static readonly Dictionary<string, string> _seekLast = new();
        private static int _seekLines;

        private static void SeekLog(string who, CampaignHost host, PlayerEntity p)
        {
            string state = $"blaster={(p.AvailableWeapons[BeamType.PirateBlaster] ? 1 : 0)} weapon={p.CurrentWeapon} ua={p.HostAmmo(0)} missiles={p.HostAmmo(1)}";
            if (_seekLast.TryGetValue(who, out string? last) && last == state) return;
            _seekLast[who] = state;
            if (++_seekLines <= 200) Console.WriteLine($"SEEK {who} frame {host.Frame}: {state}");
        }

        // ball=1: the joiner morphs once it's up and stays in alt form (the prediction summary then measures a rolling ball)
        private static bool _ballBrain;
        // duel=: the runner strafes left and right where it stands, never shooting; a shooter closes to 4 units of the
        // nearest player, stops and taps fire whenever its aim is on them
        private static string? _duelRole;

        private static bool _pushDuel;
        private static readonly bool _pushTrace = Environment.GetEnvironmentVariable("PUSHTRACE") == "1"; // the first hit, frame by frame
        private sealed class PushWindow
        {
            public int Hits, Frames, Reversals, TotalReversals, Windows;
            public float Path, YPath, TotalPath, TotalNet;
            public Vector3 Start, Last, LastDelta;
            public int Health = -1;
            public long HitFrame = -1;
        }
        private static readonly PushWindow _pushWatch = new();

        // push=1: the runner's motion for 120 frames after each hit -- its own (joiner 1, predicted) or as the shooter sees
        // it (the puppet): the XZ path walked against the net move, and how often the XZ motion turned back (a shake turns
        // back again and again; one knockback slides one way and stops)
        private static void PushWatch(CampaignHost host, int index)
        {
            PlayerEntity? victim = null;
            if (_duelRole == "runner")
            {
                victim = host.Player;
            }
            else
            {
                for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (p != host.Player && p.LoadFlags.TestFlag(LoadFlags.Active)) victim = p;
                }
            }
            if (victim == null) return;
            PushWindow w = _pushWatch;
            string who = _duelRole == "runner" ? "self" : "seen";
            void Close()
            {
                if (w.HitFrame < 0) return;
                float net = new Vector3(w.Last.X - w.Start.X, 0, w.Last.Z - w.Start.Z).Length;
                w.Windows++;
                w.TotalPath += w.Path;
                w.TotalNet += net;
                w.TotalReversals += w.Reversals;
                Console.WriteLine($"PUSH {who} hit at {w.HitFrame}: {w.Frames} frames, path {w.Path:0.00} net {net:0.00} "
                    + $"turns {w.Reversals} ypath {w.YPath:0.00} | total {w.Windows} hits: path {w.TotalPath:0.00} net {w.TotalNet:0.00} "
                    + $"turns {w.TotalReversals}");
                w.HitFrame = -1;
            }
            int health = victim.Health;
            bool hit = w.Health > 0 && health > 0 && health < w.Health;
            if (health == 0 || !victim.LoadFlags.TestFlag(LoadFlags.Spawned))
            {
                w.HitFrame = -1; // (a death: no window)
            }
            else if (hit)
            {
                Close();
                w.HitFrame = host.Frame;
                w.Frames = w.Reversals = 0;
                w.Path = w.YPath = 0;
                w.Start = w.Last = victim.Position;
                w.LastDelta = Vector3.Zero;
            }
            else if (w.HitFrame >= 0)
            {
                Vector3 d = victim.Position - w.Last;
                var xz = new Vector3(d.X, 0, d.Z);
                w.Path += xz.Length;
                w.YPath += MathF.Abs(d.Y);
                if (xz.Length > 0.005f && w.LastDelta.Length > 0.005f && Vector3.Dot(xz, w.LastDelta) < 0) w.Reversals++;
                if (xz.Length > 0.005f) w.LastDelta = xz;
                if (_pushTrace && w.Windows == 0) Console.WriteLine($"PTRACE {who} {host.Frame - w.HitFrame} d {d.X:0.000} {d.Y:0.000} {d.Z:0.000}");
                w.Last = victim.Position;
                if (++w.Frames >= 120) Close();
            }
            w.Health = health;
        }

        private static CampaignInput Brain(CampaignHost host)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot; // press FIRE to (re)spawn
                return input;
            }
            if (_ballBrain && !me.IsAltForm && !me.IsMorphing && host.Frame % 60 == 0)
            {
                input.Buttons = CampaignButtons.Morph;
                return input;
            }
            if (_duelRole == "runner" && _pushDuel)
            {
                return input; // stands still: any motion is the knockback (and its corrections)
            }
            if (_duelRole == "runner")
            {
                input.Move = new System.Numerics.Vector2(host.Frame / 45 % 2 == 0 ? 1 : -1, 0);
                return input;
            }
            PlayerEntity? target = null;
            float best = Single.MaxValue;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p == me || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0)
                {
                    continue;
                }
                float d = (p.Position - me.Position).LengthSquared;
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }
            if (target != null)
            {
                Vector3 to = target.Position - me.Position;
                float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
                float pitch = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z))), -80, 80);
                float dyaw = yaw - me.NetYaw;
                dyaw -= 360 * MathF.Floor((dyaw + 180) / 360);
                float dpitch = pitch - me.NetPitch;
                input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw, -12, 12), -4 * Math.Clamp(dpitch, -6, 6));
                input.Move = new System.Numerics.Vector2(0, 1);
                if (_duelRole == "shooter")
                {
                    if (_pushDuel) input.SelectWeapon = BeamType.Missile;
                    if (to.Length < 4) input.Move = System.Numerics.Vector2.Zero;
                    input.Buttons = MathF.Abs(dyaw) < 4 && host.Frame % (_pushDuel ? 120 : 20) < 10 ? CampaignButtons.Shoot : CampaignButtons.None;                }
                else if (MathF.Abs(dyaw) < 10 && host.Frame % 20 < 12)
                {
                    input.Buttons = CampaignButtons.Shoot;
                }
            }
            return input;
        }
    }
}
