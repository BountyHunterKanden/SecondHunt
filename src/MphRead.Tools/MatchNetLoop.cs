using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRead
{
    // Two-process netcode test on one PC (MphRead's statics allow one Scene per process): this process hosts a match
    // (seat 0 its own idle player, seat 1 a remote human, the rest bots) and starts a second copy of the tool as the
    // client playing seat 1. They talk over UDP on localhost in lockstep: the host steps frame f with the client's
    // input f and sends its snapshot; the client applies it, plays its own frame (a scripted player hunting the nearest
    // opponent) and sends that input. delay=<frames> makes both sides use data that many frames old (RTT = 2 x delay).
    // Before each snapshot is applied the client measures how far its own simulation had drifted from the host.
    //
    //   -netloop [mode=Battle] [arena=Combat Hall] [bots=2] [level=1] [time=60] [delay=0] [hunter=Samus] [remote=Kanden]
    internal static class MatchNetLoop
    {
        private const byte MsgHello = 1, MsgInput = 2, MsgSnapshot = 3, MsgEnd = 4;
        private const int RemoteSeat = 1;

        private sealed class Options
        {
            public string Mode = "Battle";
            public string Arena = "Combat Hall";
            public int Bots = 2;
            public int Level = 1;
            public float Time = 60;
            public int Delay;
            public bool Trooper; // -netloop trooper=1: the client plays a Trooper and splits / merges its Scout Drone every 3 s
            public int Jitter; // -lantest jitter=<ms>: each LAN send also waits 0..this (reorders, bunches); delay= frames each way
            public Hunter Hunter = Hunter.Samus;
            public Hunter Remote = Hunter.Kanden;
            public int Port;
            public float Leave; // -lantest: the client quits after this many seconds (0 = stays)
            public float HostLeave; // -lantest: the host quits after this many seconds
            public bool Looks; // -lantest looks=1: seat characters + skins (SeatLooksTest)
            public int Players = 2; // -lantest: devices in the match (the host + players-1 joiners, up to 8: P8)
            public int Index = 1; // -lanjoin: which joiner this is (its hunter and name)
            public float Rejoin; // -lantest rejoin=<s>: the last joiner's app is killed then (no BYE) and starts again (release test T1)
            public float RejoinWait = 12; // rejoinwait=<s>: how long it stays away (past 10 s: the host's match drops the seat first)
            public int HostPort; // -lanjoin hostport=<n>: only this host (several sessions run -lantest on one PC at once)
            public bool Again; // -lanjoin again=1: the restarted app, rejoining its held seat from the LAN list
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
                case "mode": o.Mode = value; break;
                case "arena": o.Arena = value; break;
                case "bots": o.Bots = Int32.Parse(value); break;
                case "level": o.Level = Int32.Parse(value); break;
                case "time": o.Time = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "delay": o.Delay = Int32.Parse(value); break;
                case "jitter": o.Jitter = Int32.Parse(value); break;
                case "trooper": o.Trooper = value != "0"; break;
                case "hunter": o.Hunter = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "remote": o.Remote = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "port": o.Port = Int32.Parse(value); break;
                case "leave": o.Leave = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "hostleave": o.HostLeave = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "looks": o.Looks = value != "0"; break;
                case "players": o.Players = Math.Clamp(Int32.Parse(value), 2, MatchSettings.MaxSeats); break;
                case "index": o.Index = Int32.Parse(value); break;
                case "rejoin": o.Rejoin = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "rejoinwait": o.RejoinWait = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "again": o.Again = value != "0"; break;
                case "hostport": o.HostPort = Int32.Parse(value); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            return o;
        }

        private static string Forward(Options o)
        {
            return $"mode={o.Mode} \"arena={o.Arena}\" bots={o.Bots} level={o.Level} "
                + $"time={o.Time.ToString(System.Globalization.CultureInfo.InvariantCulture)} delay={o.Delay} "
                + $"hunter={o.Hunter} remote={o.Remote} leave={o.Leave.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + (o.Looks ? " looks=1" : "") + $" players={o.Players}" + (o.Jitter > 0 ? $" jitter={o.Jitter}" : "") + (o.Trooper ? " trooper=1" : "");
        }

        // -lantest delay=<frames each way> jitter=<ms>: this device's LAN sends leave late (Wi-Fi in a test)
        private static void SimulateLink(LanSession net, Options o)
        {
            net.TestDelayMs = o.Delay * 1000 / 60;
            net.TestJitterMs = o.Jitter;
            if (o.Delay > 0 || o.Jitter > 0)
            {
                Console.WriteLine($"link: sends {net.TestDelayMs} ms late + 0-{o.Jitter} ms jitter");
            }
        }

        private static MatchSettings HostSettings(Options o)
        {
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}");
            var settings = new MatchSettings { Mode = mode, Arena = arena.Name, TimeLimitSeconds = o.Time };
            settings.Players.Add(new MatchPlayer { Hunter = o.Hunter, Nickname = "Host" });
            settings.Players.Add(o.Trooper
                ? new MatchPlayer { Hunter = Characters.Trooper.Base, Character = Characters.Trooper.Id, Remote = true, Nickname = "Client" }
                : new MatchPlayer { Hunter = o.Remote, Remote = true, Nickname = "Client" });
            Hunter[] botHunters = { Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel };
            foreach (Hunter h in botHunters.Where(h => h != o.Hunter && h != o.Remote).Take(Math.Clamp(o.Bots, 0, 2)))
            {
                settings.Players.Add(new MatchPlayer { Hunter = h, Bot = true, BotLevel = o.Level });
            }
            return settings;
        }

        // ---------------------------------------------------------------- host

        public static void Host(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Client.ReceiveTimeout = 30000;
            int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            string dll = typeof(MatchNetLoop).Assembly.Location;
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"\"{dll}\" -netclient port={port} {Forward(o)}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!
            };
            using Process client = Process.Start(psi)!;
            var clientOut = new List<string>();
            client.OutputDataReceived += (_, e) => { if (e.Data != null) lock (clientOut) clientOut.Add(e.Data); };
            client.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (clientOut) clientOut.Add("stderr: " + e.Data); };
            client.BeginOutputReadLine();
            client.BeginErrorReadLine();
            try
            {
                RunHost(o, socket);
            }
            catch (Exception ex)
            {
                Console.WriteLine("host: " + ex);
            }
            finally
            {
                if (!client.WaitForExit(20000))
                {
                    client.Kill();
                }
                client.WaitForExit();
                lock (clientOut)
                {
                    foreach (string line in clientOut)
                    {
                        Console.WriteLine("  [client] " + line);
                    }
                }
            }
        }

        private static void RunHost(Options o, UdpClient socket)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] hello = socket.Receive(ref remote);
            if (hello[0] != MsgHello)
            {
                throw new InvalidOperationException("expected the client's hello");
            }
            socket.Client.ReceiveTimeout = 10000;
            MatchSettings settings = HostSettings(o);
            CampaignHost host = CampaignHost.StartMatch(settings);
            Console.WriteLine($"host: {settings.Mode} in {MatchArenas.Find(settings.Arena)?.InGameName}, seats "
                + string.Join(", ", settings.Players.Select((p, i) => $"{i}:{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " remote" : " local")}"))
                + $", delay {o.Delay} f (RTT {o.Delay * 2 * 1000 / 60} ms)");
            var inputs = new Queue<NetInput>();
            var snaps = new Queue<byte[]>();
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            long maxFrames = (long)(o.Time * 60) + 900;
            var buffer = new byte[2048];
            long sent = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                while (!host.Ended && host.Frame < maxFrames)
                {
                    // lockstep: the client's input for this frame
                    NetInput input;
                    while (true)
                    {
                        byte[] msg = socket.Receive(ref remote);
                        if (msg[0] == MsgInput)
                        {
                            input = NetInput.Read(msg.AsSpan(1));
                            break;
                        }
                    }
                    inputs.Enqueue(input);
                    if (inputs.Count > o.Delay)
                    {
                        host.SetRemoteInput(RemoteSeat, inputs.Dequeue());
                    }
                    host.Step(idle);
                    Snapshot snap = host.CaptureSnapshot(lanStats: true);
                    buffer[0] = MsgSnapshot;
                    int size = 1 + snap.Write(buffer.AsSpan(1));
                    socket.Send(buffer, size, remote);
                    sent += size;
                }
            }
            finally
            {
                socket.Send(new[] { MsgEnd }, 1, remote);
            }
            Console.WriteLine($"host: {host.Frame} frames in {clock.Elapsed.TotalSeconds:0.0} s, snapshots avg "
                + $"{sent / Math.Max(1, host.Frame)} B ({sent / Math.Max(1, host.Frame) * 60 / 1024.0:0.0} KB/s at 60 Hz), ended {host.Ended}");
            Console.WriteLine("host scoreboard:\n" + MatchStatus.Scoreboard());
            host.Dispose();
        }

        // ---------------------------------------------------------------- client

        public static void Client(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Client.ReceiveTimeout = 15000;
            var hostEnd = new IPEndPoint(IPAddress.Loopback, o.Port);
            MatchSettings settings = MatchClient.ForSeat(HostSettings(o), RemoteSeat);
            CampaignHost host = CampaignHost.StartMatch(settings);
            socket.Send(new[] { MsgHello }, 1, hostEnd);
            var snaps = new Queue<Snapshot>();
            var buffer = new byte[1 + NetInput.Size];
            byte epoch = 0;
            ushort seq = 0;
            // stats
            int frames = 0, puppetSamples = 0, healthMismatch = 0, localSnaps = 0, itemChecks = 0, itemDiffers = 0, itemMax = 0;
            double puppetErrSum = 0, localErrSum = 0;
            float puppetErrMax = 0, localErrMax = 0;
            Snapshot? last = null;
            int ownGains = 0; // own energy raised by this client's own step (a pickup): must stay 0, the host's state raises it
            int dipFrame = 0, dipFrom = 0, falseHurts = 0, merges = 0;
            bool wasAlt = false;
            int lastHealth = 0;
            bool hadTurret = false;
            int mergeFrame = -1000, mergeHurts = 0, lastSinceDamage = 0;
            void Play()
            {
                CampaignInput input = Brain(host, out NetInput net, o.Trooper);
                int before = host.Player.Health;
                host.Step(input);
                PlayerEntity me = host.Player;
                if (before > 0 && me.Health > before)
                {
                    ownGains++;
                }
                // trooper=1: own health down then back up within 30 frames out of drone form = a hurt the host never dealt
                if (me.Health < lastHealth && me.Health > 0) // (snapshots apply between steps: frame to frame)
                {
                    dipFrame = frames;
                    dipFrom = lastHealth;
                }
                else if (dipFrom > 0 && me.Health >= dipFrom && frames - dipFrame <= 30)
                {
                    falseHurts++;
                    dipFrom = 0;
                }
                lastHealth = me.Health;
                // trooper=1: hurt effects (TakeDamage restarts the time since damage) within 20 frames of this device's merge
                bool turret = me.Flags2.TestFlag(PlayerFlags2.Halfturret);
                if (hadTurret && !turret) mergeFrame = frames;
                hadTurret = turret;
                if (me.DroneTimeSinceDamage < lastSinceDamage && frames - mergeFrame <= 20) mergeHurts++;
                lastSinceDamage = me.DroneTimeSinceDamage;
                if (me.IsAltForm != wasAlt)
                {
                    wasAlt = me.IsAltForm;
                    if (!wasAlt) merges++;
                }
                net.Seq = seq++;
                net.Yaw = me.NetYaw;
                net.Pitch = me.NetPitch;
                net.AimEpoch = epoch;
                buffer[0] = MsgInput;
                net.Write(buffer.AsSpan(1));
                socket.Send(buffer, buffer.Length, hostEnd);
            }
            Play(); // frame 0 needs no snapshot
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] msg;
                try
                {
                    msg = socket.Receive(ref remote);
                }
                catch (SocketException)
                {
                    Console.WriteLine("client: timed out waiting for the host");
                    break;
                }
                if (msg[0] == MsgEnd)
                {
                    break;
                }
                if (msg[0] != MsgSnapshot)
                {
                    continue;
                }
                snaps.Enqueue(Snapshot.Read(msg.AsSpan(1)));
                if (snaps.Count > o.Delay)
                {
                    Snapshot snap = snaps.Dequeue();
                    last = snap;
                    // drift of this client's own simulation from the host's, just before the correction
                    for (int i = 0; i < snap.Seats.Length; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        PlayerNetState s = snap.Seats[i];
                        if (s.Health == 0 || p.Health == 0)
                        {
                            continue;
                        }
                        float err = (p.Position - s.Position).Length;
                        if (i == RemoteSeat)
                        {
                            localErrSum += err;
                            localErrMax = MathF.Max(localErrMax, err);
                            localSnaps++;
                        }
                        else
                        {
                            puppetErrSum += err;
                            puppetErrMax = MathF.Max(puppetErrMax, err);
                            puppetSamples++;
                        }
                        if (p.Health != s.Health)
                        {
                            healthMismatch++;
                        }
                    }
                    epoch = snap.Seats[RemoteSeat].AimEpoch;
                    host.ApplySnapshot(snap);
                    if (snap.Items != null)
                    {
                        // do this client's items match the host's list? (by type and place)
                        var mine = new List<(int, Vector3)>();
                        foreach (ItemInstanceEntity item in host.Scene.GetItemInstanceEntities())
                        {
                            if (item.DespawnTimer != 0)
                            {
                                mine.Add(((int)item.ItemType, item.Position));
                            }
                        }
                        bool same = mine.Count == snap.Items.Length && snap.Items.All(n => mine.Any(m => m.Item1 == n.Type
                            && (m.Item2 - n.Position).LengthSquared < 0.01f));
                        itemChecks++;
                        if (!same)
                        {
                            itemDiffers++;
                        }
                        itemMax = Math.Max(itemMax, snap.Items.Length);
                    }
                }
                if (host.Ended)
                {
                    break;
                }
                Play();
                frames++;
            }
            Console.WriteLine($"client: {frames} frames as seat {RemoteSeat}; own-player drift before correction mean "
                + $"{localErrSum / Math.Max(1, localSnaps):0.000} max {localErrMax:0.000}; puppet drift mean "
                + $"{puppetErrSum / Math.Max(1, puppetSamples):0.000} max {puppetErrMax:0.000}; health differed on "
                + $"{healthMismatch} seat-frames before correction; own player pulled back {host.LocalCorrections} times");
            Console.WriteLine($"client: items checked {itemChecks} times (up to {itemMax} in the arena), differed from the "
                + $"host's list {itemDiffers} times; this player: {GameState.Kills[RemoteSeat]} kills, "
                + $"health {PlayerEntity.Players[RemoteSeat].Health}; own energy raised locally {ownGains} times "
                + "(each one read as a hit on the next host state; should be 0)"
                + (o.Trooper ? $"; drone merges {merges}, health dipped and came back {falseHurts} times, hurt within 20 frames of a merge {mergeHurts} times (should be 0)" : ""));
            if (last != null)
            {
                Console.WriteLine("client scoreboard (host numbers):\n" + MatchStatus.Scoreboard());
            }
            host.Dispose();
        }

        // ---------------------------------------------------------------- real-time LAN test

        // -lantest [same options]: the real LanSession -- beacons, discovery, join, then host and client each run on their
        // own 60 Hz clock (no lockstep), like two devices on one Wi-Fi network. looks=1: seat characters + skins
        // (SeatLooksTest: a Varia Samus host, a Wave Trooper joiner, a Plasma Trooper bot; PASS/FAIL line at the end).
        // players=3..4: that many devices (the host's START seats them all); PASS = every device ends with the host's
        // scoreboard, mode state and octolith/node events (as -onlinetest).
        public static void LanTest(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings template = MatchSettings.Quick(mode, arena.Name, o.Hunter, o.Bots, o.Level);
            template.TimeLimitSeconds = o.Time;
            if (o.Looks)
            {
                SeatLooksTest.Dress(template);
            }
            string? hostLooks = null, hostAbilities = null;
            using LanSession net = LanSession.Host(template, Paths.MphKey, "PC host", Math.Max(o.Players, template.Players.Count),
                port: 0); // (any free port: never GamePort, where a real host on this PC may be listening)
            SimulateLink(net, o);
            Console.WriteLine("host: " + net.Status);
            string dll = typeof(MatchNetLoop).Assembly.Location;
            var clients = new List<(Process Process, List<string> Out)>();
            (Process, List<string>) Launch(int k, string extra)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"\"{dll}\" -lanjoin {Forward(o)} index={k} hostport={net.Port}{extra}")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(dll)!
                };
                Process process = Process.Start(psi)!;
                var output = new List<string>();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("stderr: " + e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return (process, output);
            }
            for (int k = 1; k < o.Players; k++)
            {
                clients.Add(Launch(k, ""));
            }
            // rejoin=: the last joiner's app dies mid-match (no BYE: Wi-Fi gone / app killed), then starts again and rejoins
            (Process Process, List<string> Out)? rejoiner = null;
            System.Threading.Timer? killTimer = null, backTimer = null;
            List<string> clientOut = clients[0].Out;
            var waited = Stopwatch.StartNew();
            while (net.Joiners.Count(j => !j.Left) < o.Players - 1 && waited.ElapsedMilliseconds < 30000)
            {
                Thread.Sleep(20);
            }
            string? hostScores = null, hostEvents = null, hostResults = null, hostResultsProblem = null, hostEnd = null;
            if (net.Joiners.Count(j => !j.Left) < o.Players - 1)
            {
                Console.WriteLine($"host: {net.Joiners.Count(j => !j.Left)} of {o.Players - 1} joined in 30 s");
            }
            else
            {
                if (o.Players > 2 || o.Rejoin > 0) // (rejoin=: as the app's menus do; a held seat is one that got its START)
                {
                    net.SendStart(); // every joiner's final seat
                }
                Console.WriteLine("host: " + net.Status + "; seats " + string.Join(", ", net.Settings!.Players.Select((p, i) =>
                    $"{i}:{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " " + p.Nickname : " local")}")));
                CampaignHost host = CampaignHost.StartMatch(net.Settings!);
                hostLooks = SeatLooksTest.Looks(net.Settings!);
                hostAbilities = SeatLooksTest.Abilities(net.Settings!.Players.Count);
                Console.WriteLine($"host: {hostLooks}, {hostAbilities}");
                var driver = new MatchNetDriver(net, host);
                if (o.Rejoin > 0)
                {
                    killTimer = new System.Threading.Timer(_ =>
                    {
                        clients[^1].Process.Kill();
                        Console.WriteLine($"host: joiner {o.Players - 1} KILLED at frame {host.Frame}, back in {o.RejoinWait:0.#} s");
                    }, null, (int)(o.Rejoin * 1000), Timeout.Infinite);
                    backTimer = new System.Threading.Timer(_ =>
                    {
                        rejoiner = Launch(o.Players - 1, " again=1");
                        Console.WriteLine($"host: joiner {o.Players - 1} STARTED AGAIN at frame {host.Frame}");
                    }, null, (int)((o.Rejoin + o.RejoinWait) * 1000), Timeout.Infinite);
                }
                RealTime(host, () => driver.Step(new CampaignInput { SelectWeapon = BeamType.None }),
                    o.HostLeave > 0 ? o.HostLeave : o.Time + 12 + (o.Rejoin > 0 ? 20 : 0));
                hostEnd = host.Ended ? OnlineMatchTool.EndedAt() : null;
                killTimer?.Dispose();
                backTimer?.Dispose();
                if (o.Rejoin > 0)
                {
                    Console.WriteLine($"host: LAN rejoins {net.Rejoins}, dropped seats back {driver.SeatsBack}");
                }
                Console.WriteLine($"host: {host.Frame} frames, inputs received {net.InputsReceived}, ended {host.Ended}, "
                    + $"{driver.StatusLine()}, seat 1 {(PlayerEntity.Players[1].LoadFlags.TestFlag(LoadFlags.Disconnected) ? "disconnected" : "in the match")}");
                Console.WriteLine("host scoreboard:\n" + MatchStatus.Scoreboard());
                hostScores = OnlineMatchTool.Scores(net.Settings!.Players.Count, host.Scene);
                hostEvents = OnlineMatchTool.ObjectiveEvents();
                hostResults = OnlineMatchTool.Results(net.Settings!.Players.Count);
                hostResultsProblem = OnlineMatchTool.ResultsProblem(net.Settings!.Players.Count);
                Console.WriteLine(hostScores);
                Console.WriteLine(hostEvents);
                Console.WriteLine(hostResults);
                host.Dispose();
            }
            net.Dispose();
            var problems = new List<string>();
            for (int k = 0; k < clients.Count; k++)
            {
                (Process process, List<string> output) = clients[k];
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                }
                process.WaitForExit();
                lock (output)
                {
                    if (k > 0)
                    {
                        foreach (string line in output)
                        {
                            Console.WriteLine($"  [client {k + 1}] " + line);
                        }
                    }
                    string? scores = output.FirstOrDefault(l => l.StartsWith("SCORES "));
                    string? events = output.FirstOrDefault(l => l.StartsWith("OBJEVENTS "));
                    if (hostScores == null)
                    {
                        problems.Add("the host's match never started");
                        break;
                    }
                    if (o.Leave == 0 && o.HostLeave == 0 && (scores != hostScores || events != hostEvents)
                        && !(o.Rejoin > 0 && k == clients.Count - 1)) // (the killed app: the restarted one's scores count)
                    {
                        problems.Add($"client {k + 1} {scores ?? "no SCORES"} / {events ?? "no OBJEVENTS"}");
                    }
                    // release 0.2.0 F1: the same RESULTS places, at about the same time
                    if (o.Leave == 0 && o.HostLeave == 0 && !(o.Rejoin > 0 && k == clients.Count - 1))
                    {
                        string? results = output.FirstOrDefault(l => l.StartsWith("RESULTS "));
                        if (results != hostResults)
                        {
                            problems.Add($"client {k + 1} {results ?? "no RESULTS"} vs host {hostResults}");
                        }
                        if (hostEnd != null && OnlineMatchTool.EndLagProblem($"client {k + 1}", new[] { hostEnd }, output, 2500) is string lag)
                        {
                            problems.Add(lag);
                        }
                    }
                }
                process.Dispose();
            }
            if (o.Rejoin > 0 && hostScores != null)
            {
                if (rejoiner is not (Process, List<string>) back)
                {
                    problems.Add("the killed joiner never started again");
                }
                else
                {
                    if (!back.Item1.WaitForExit(30000))
                    {
                        back.Item1.Kill();
                    }
                    back.Item1.WaitForExit();
                    lock (back.Item2)
                    {
                        foreach (string line in back.Item2)
                        {
                            Console.WriteLine("  [rejoined] " + line);
                        }
                        string? scores = back.Item2.FirstOrDefault(l => l.StartsWith("SCORES "));
                        string? events = back.Item2.FirstOrDefault(l => l.StartsWith("OBJEVENTS "));
                        if (scores != hostScores || events != hostEvents)
                        {
                            problems.Add($"rejoined client {scores ?? "no SCORES"} / {events ?? "no OBJEVENTS"}");
                        }
                    }
                    if (net.Rejoins == 0)
                    {
                        problems.Add("the host never re-admitted the held seat");
                    }
                }
            }
            lock (clientOut)
            {
                foreach (string line in clientOut)
                {
                    Console.WriteLine("  [client] " + line);
                }
                if (o.Looks)
                {
                    string? problem = hostLooks == null ? "the match never started"
                        : hostAbilities != SeatLooksTest.AbilitiesExpected(hostLooks)
                            ? $"host {hostAbilities} (expected {SeatLooksTest.AbilitiesExpected(hostLooks)}: abilities on for each Trooper seat)"
                        : SeatLooksTest.Problem("client", hostLooks, clientOut.ToArray(), oldJoin: true)
                            ?? SeatLooksTest.SetupLineProblem(net.Settings!);
                    Console.WriteLine(problem == null ? $"PASS lantest looks: both devices {hostLooks}, {hostAbilities} (Trooper abilities on), "
                        + "a protocol 6 JOIN refused, setup line round trip" : "FAIL lantest looks: " + problem);
                }
            }
            if (hostResultsProblem != null)
            {
                problems.Add("host results: " + hostResultsProblem);
            }
            if (o.Leave == 0 && o.HostLeave == 0)
            {
                Console.WriteLine(problems.Count == 0 ? $"PASS lantest: {o.Players} devices agree on the final scores ({hostScores}; {hostEvents})"
                    : "FAIL lantest: host " + hostScores + " / " + hostEvents + "; " + string.Join("; ", problems));
            }
        }

        public static void LanJoin(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            using LanSession net = LanSession.Browse(Paths.MphKey);
            SimulateLink(net, o);
            var found = Stopwatch.StartNew();
            LanHostInfo? target = null;
            while (target == null && found.ElapsedMilliseconds < 10000)
            {
                // (a running host only beacons for its held seats: release test T1; again=1 looks for its own)
                target = net.Hosts.FirstOrDefault(h => (o.HostPort == 0 || h.Address.Port == o.HostPort)
                    && (o.Again ? h.Running && h.HeldFor(JoinerName(o)) : !h.Running));
                Thread.Sleep(50);
            }
            if (target == null)
            {
                Console.WriteLine("client: no host found in 10 s (" + net.Status + ")");
                return;
            }
            Console.WriteLine($"client: found {target.Label} after {found.ElapsedMilliseconds} ms");
            if (o.Looks)
            {
                Console.WriteLine(SeatLooksTest.OldLanJoin(target.Address, Paths.MphKey));
            }
            Hunter[] joinerHunters = { o.Remote, Hunter.Sylux, Hunter.Weavel, Hunter.Noxus, Hunter.Trace, Hunter.Spire, Hunter.Samus };
            Hunter hunter = joinerHunters[Math.Clamp(o.Index - 1, 0, joinerHunters.Length - 1)];
            string name = JoinerName(o);
            if (!(o.Looks ? net.Join(target.Address, SeatLooksTest.JoinerHunter, "PC client",
                character: SeatLooksTest.JoinerCharacter, skin: SeatLooksTest.JoinerSkin)
                : o.Trooper ? net.Join(target.Address, Characters.Trooper.Base, name, character: Characters.Trooper.Id)
                : net.Join(target.Address, hunter, name)))
            {
                Console.WriteLine("client: " + net.Status);
                return;
            }
            Console.WriteLine("client: " + net.Status);
            if (o.Players > 2 || o.Again)
            {
                // the host's START (the final seats: the WELCOME's move as others join)
                var start = Stopwatch.StartNew();
                while (!net.Started && start.ElapsedMilliseconds < 40000)
                {
                    Thread.Sleep(5);
                }
                Console.WriteLine($"client: started {net.Started}, seat {net.Settings!.LocalSeat} of {net.Settings.Players.Count}");
            }
            CampaignHost host = CampaignHost.StartMatch(net.Settings!);
            Console.WriteLine(SeatLooksTest.Looks(net.Settings!));
            Console.WriteLine(SeatLooksTest.Abilities(net.Settings!.Players.Count));
            var driver = new MatchNetDriver(net, host);
            var watch = new MergeHurtWatch();
            RealTime(host, () =>
            {
                driver.Step(Brain(host, out _, o.Trooper));
                watch.Watch(host.Player, (int)host.Frame);
            }, o.Leave > 0 ? o.Leave : o.Time + 20);
            if (o.Trooper)
            {
                Console.WriteLine($"client: drone merges {watch.Merges}, hurt within 20 frames of a merge {watch.Hurts} times (should be 0)");
            }
            if (host.Ended)
            {
                Console.WriteLine(OnlineMatchTool.EndedAt());
            }
            Console.WriteLine("client: " + driver.StatusLine());
            Console.WriteLine($"client: {host.Frame} frames, snapshots received {net.SnapshotsReceived}, own player pulled back "
                + $"{host.LocalCorrections} times, ended {host.Ended}");
            Console.WriteLine("client scoreboard:\n" + MatchStatus.Scoreboard());
            Console.WriteLine(OnlineMatchTool.Scores(net.Settings!.Players.Count, host.Scene));
            Console.WriteLine(OnlineMatchTool.Results(net.Settings!.Players.Count));
            Console.WriteLine(OnlineMatchTool.ObjectiveEvents());
            host.Dispose();
        }

        private static string JoinerName(Options o) => o.Index > 1 ? $"PC client {o.Index}" : "PC client";

        // steps at 60 Hz of wall-clock time until the match ends or the time runs out
        // trooper=1: this device's drone merges (its Halfturret flag cleared) and hurt effects within 20 frames after one
        // (TakeDamage restarts the time since damage): the guest replaying the host's still-split snapshot as a hit
        private sealed class MergeHurtWatch
        {
            private bool _hadTurret;
            private int _mergeFrame = -1000, _lastSinceDamage;
            public int Merges, Hurts;

            public void Watch(PlayerEntity me, int frame)
            {
                bool turret = me.Flags2.TestFlag(PlayerFlags2.Halfturret);
                if (_hadTurret && !turret)
                {
                    _mergeFrame = frame;
                    Merges++;
                }
                _hadTurret = turret;
                if (me.DroneTimeSinceDamage < _lastSinceDamage && frame - _mergeFrame <= 20)
                {
                    Hurts++;
                }
                _lastSinceDamage = me.DroneTimeSinceDamage;
            }
        }

        private static void RealTime(CampaignHost host, Action step, float seconds)
        {
            var clock = Stopwatch.StartNew();
            long frames = 0;
            while (!host.Ended && clock.Elapsed.TotalSeconds < seconds)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step();
                    frames++;
                }
                Thread.Sleep(1);
            }
        }

        // the client's player: hunt the nearest opponent it can see in its own scene
        private static CampaignInput Brain(CampaignHost host, out NetInput net, bool morph = false)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            net = new NetInput { SelectWeapon = BeamType.None };
            PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot; // press FIRE to (re)spawn
                net.Buttons = input.Buttons;
                return input;
            }
            if (morph && host.Frame % 180 == 90)
            {
                input.Buttons = CampaignButtons.Morph; // trooper=1: split, then merge 3 s later
                net.Buttons = input.Buttons;
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
                // local aim is a mouse-style delta (degrees = -delta / 4), capped like a fast flick
                input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw, -12, 12), -4 * Math.Clamp(dpitch, -6, 6));
                input.Move = new System.Numerics.Vector2(0, 1);
                net.MoveY = 1;
                if (MathF.Abs(dyaw) < 10 && host.Frame % 20 < 12)
                {
                    input.Buttons = CampaignButtons.Shoot;
                }
            }
            net.Buttons = input.Buttons;
            return input;
        }
    }
}
