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
            public Hunter Hunter = Hunter.Samus;
            public Hunter Remote = Hunter.Kanden;
            public int Port;
            public float Leave; // -lantest: the client quits after this many seconds (0 = stays)
            public float HostLeave; // -lantest: the host quits after this many seconds
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
                case "hunter": o.Hunter = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "remote": o.Remote = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "port": o.Port = Int32.Parse(value); break;
                case "leave": o.Leave = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "hostleave": o.HostLeave = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            return o;
        }

        private static string Forward(Options o)
        {
            return $"mode={o.Mode} \"arena={o.Arena}\" bots={o.Bots} level={o.Level} "
                + $"time={o.Time.ToString(System.Globalization.CultureInfo.InvariantCulture)} delay={o.Delay} "
                + $"hunter={o.Hunter} remote={o.Remote} leave={o.Leave.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static MatchSettings HostSettings(Options o)
        {
            GameMode mode = Enum.Parse<GameMode>(o.Mode.Replace(" ", ""), ignoreCase: true);
            RoomMetadata arena = MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}");
            var settings = new MatchSettings { Mode = mode, Arena = arena.Name, TimeLimitSeconds = o.Time };
            settings.Players.Add(new MatchPlayer { Hunter = o.Hunter, Nickname = "Host" });
            settings.Players.Add(new MatchPlayer { Hunter = o.Remote, Remote = true, Nickname = "Client" });
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
                    Snapshot snap = host.CaptureSnapshot();
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
            void Play()
            {
                CampaignInput input = Brain(host, out NetInput net);
                host.Step(input);
                PlayerEntity me = host.Player;
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
                + $"health {PlayerEntity.Players[RemoteSeat].Health}");
            if (last != null)
            {
                Console.WriteLine("client scoreboard (host numbers):\n" + MatchStatus.Scoreboard());
            }
            host.Dispose();
        }

        // ---------------------------------------------------------------- real-time LAN test

        // -lantest [same options]: the real LanSession -- beacons, discovery, join, then host and client each run on their
        // own 60 Hz clock (no lockstep), like two devices on one Wi-Fi network.
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
            using LanSession net = LanSession.Host(template, Paths.MphKey, "PC host");
            Console.WriteLine("host: " + net.Status);
            string dll = typeof(MatchNetLoop).Assembly.Location;
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"\"{dll}\" -lanjoin {Forward(o)}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!
            };
            using Process client = Process.Start(psi)!;
            var clientOut = new List<string>();
            client.OutputDataReceived += (_, e) => { if (e.Data != null) lock (clientOut) clientOut.Add(e.Data); };
            client.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (clientOut) clientOut.Add("stderr: " + e.Data); };
            client.BeginOutputReadLine();
            client.BeginErrorReadLine();
            var joined = new ManualResetEventSlim();
            net.Joined += joined.Set;
            if (!joined.Wait(30000))
            {
                Console.WriteLine("host: nobody joined in 30 s");
            }
            else
            {
                Console.WriteLine("host: " + net.Status);
                CampaignHost host = CampaignHost.StartMatch(net.Settings!);
                var driver = new MatchNetDriver(net, host);
                RealTime(host, () => driver.Step(new CampaignInput { SelectWeapon = BeamType.None }),
                    o.HostLeave > 0 ? o.HostLeave : o.Time + 12);
                Console.WriteLine($"host: {host.Frame} frames, inputs received {net.InputsReceived}, ended {host.Ended}, "
                    + $"{driver.StatusLine()}, seat 1 {(PlayerEntity.Players[1].LoadFlags.TestFlag(LoadFlags.Disconnected) ? "disconnected" : "in the match")}");
                Console.WriteLine("host scoreboard:\n" + MatchStatus.Scoreboard());
                host.Dispose();
            }
            net.Dispose();
            if (!client.WaitForExit(30000))
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

        public static void LanJoin(string[] args)
        {
            Options o = Parse(args);
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            using LanSession net = LanSession.Browse(Paths.MphKey);
            var found = Stopwatch.StartNew();
            LanHostInfo? target = null;
            while (target == null && found.ElapsedMilliseconds < 10000)
            {
                target = net.Hosts.FirstOrDefault();
                Thread.Sleep(50);
            }
            if (target == null)
            {
                Console.WriteLine("client: no host found in 10 s (" + net.Status + ")");
                return;
            }
            Console.WriteLine($"client: found {target.Label} after {found.ElapsedMilliseconds} ms");
            if (!net.Join(target.Address, o.Remote, "PC client"))
            {
                Console.WriteLine("client: " + net.Status);
                return;
            }
            Console.WriteLine("client: " + net.Status);
            CampaignHost host = CampaignHost.StartMatch(net.Settings!);
            var driver = new MatchNetDriver(net, host);
            RealTime(host, () => driver.Step(Brain(host, out _)), o.Leave > 0 ? o.Leave : o.Time + 20);
            Console.WriteLine("client: " + driver.StatusLine());
            Console.WriteLine($"client: {host.Frame} frames, snapshots received {net.SnapshotsReceived}, own player pulled back "
                + $"{host.LocalCorrections} times, ended {host.Ended}");
            Console.WriteLine("client scoreboard:\n" + MatchStatus.Scoreboard());
            host.Dispose();
        }

        // steps at 60 Hz of wall-clock time until the match ends or the time runs out
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
        private static CampaignInput Brain(CampaignHost host, out NetInput net)
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
