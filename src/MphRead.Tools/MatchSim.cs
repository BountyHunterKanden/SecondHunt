using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Headless multiplayer tests: MphRead's own match logic + bot AI hosted by CampaignHost.StartMatch, the way the
    // Android app runs a match. The local seat gets no input unless asked (it stands and respawns; bots hunt it).
    //
    //   -matchsim [mode=Battle] [arena=<name|in-game name>] [bots=3] [level=1] [time=60] [hunter=Samus] [move]
    //       one match, verbose: state changes, every kill/death, final scoreboard, sim cost.
    //   -matchsweep [mode=<one>|all] [arena=<one>] [bots=3] [level=2] [time=40]
    //       every arena x every mode: loads, plays to the end, checks the match ended by itself and that the bots
    //       scored. Results also go to extract_out/multiplayer/match_sweep.txt
    //   -matcharenas    list the arenas (room name, in-game name, id)
    internal static class MatchSim
    {
        private sealed class Options
        {
            public string Mode = "Battle";
            public string? Arena;
            public int Bots = 3;
            public int Level = 1;
            public float Time = 60;
            public Hunter Hunter = Hunter.Samus;
            public bool Move;
            public bool Remote; // seat 1 is a remote human driven through NetInput by a scripted client
        }

        private static Options Parse(string[] args, Options o)
        {
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
                case "time": o.Time = Single.Parse(value); break;
                case "hunter": o.Hunter = Enum.Parse<Hunter>(value, ignoreCase: true); break;
                case "move": o.Move = true; break;
                case "remote": o.Remote = true; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            return o;
        }

        private static GameMode ParseMode(string mode) => Enum.Parse<GameMode>(mode.Replace(" ", ""), ignoreCase: true);

        // per arena: spawn points / octolith flags / bases / nodes on each mode's entity layer (4 players), and which
        // modes that data supports
        public static void Arenas(string[] args)
        {
            bool census = ToolPaths.UseDesktopExtraction();
            foreach (RoomMetadata r in MatchArenas.All)
            {
                Console.WriteLine($"{r.Id,4}  {r.Name,-28} {r.InGameName}");
                if (!census)
                {
                    continue;
                }
                foreach (GameMode mode in MatchSettings.Modes)
                {
                    ArenaCensus c = MatchArenas.Census(r, mode);
                    Console.WriteLine($"        {mode,-13} spawns {c.Spawns,2} flags {c.Flags} bases {c.Bases} nodes {c.Nodes}"
                        + (c.Supports(mode) ? "" : "   -- not playable"));
                }
            }
        }

        public static void Run(string[] args)
        {
            Options o = Parse(args, new Options());
            CountAsserts();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            GameMode mode = ParseMode(o.Mode);
            RoomMetadata arena = MatchArenas.Find(o.Arena ?? "Combat Hall")
                ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings settings = MatchSettings.Quick(mode, arena.Name, o.Hunter, o.Bots, o.Level);
            settings.TimeLimitSeconds = o.Time;
            MakeRemote(settings, o);
            Console.WriteLine($"{mode} in {arena.InGameName} ({arena.Name}), {settings.Players.Count} players, "
                + $"bot level {o.Level}, time {o.Time} s");
            MatchResult r = Play(settings, verbose: true, o.Move);
            Console.WriteLine(r.Summary());
            Console.WriteLine(MatchStatus.Scoreboard());
        }

        public static void Sweep(string[] args)
        {
            Options o = Parse(args, new Options { Mode = "all", Level = 2, Time = 40 });
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            IReadOnlyList<GameMode> modes = o.Mode == "all" ? MatchSettings.Modes : new[] { ParseMode(o.Mode) };
            IEnumerable<RoomMetadata> arenas = o.Arena == null ? MatchArenas.All
                : new[] { MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}") };
            CountAsserts();
            var report = new StringBuilder();
            report.AppendLine($"# match sweep {DateTime.Now:yyyy-MM-dd HH:mm}: {o.Bots} bots level {o.Level}, time {o.Time} s");
            report.AppendLine("# result mode arena | frames end-state kills deaths points | sim ms/frame | notes");
            int pass = 0, fail = 0, skip = 0;
            var total = Stopwatch.StartNew();
            foreach (RoomMetadata arena in arenas)
            {
                foreach (GameMode mode in modes)
                {
                    if (!MatchArenas.Supports(arena, mode, o.Bots + 1))
                    {
                        skip++;
                        string na = $"n/a  {mode,-13} {arena.InGameName,-20} | {MatchArenas.Census(arena, mode, o.Bots + 1)}";
                        Console.WriteLine(na);
                        report.AppendLine(na);
                        continue;
                    }
                    MatchSettings settings = MatchSettings.Quick(mode, arena.Name, o.Hunter, o.Bots, o.Level);
                    settings.TimeLimitSeconds = o.Time;
                    MakeRemote(settings, o);
                    MatchResult r = Play(settings, verbose: false, o.Move);
                    // something happened: a kill, a death (incl. suicides) or a mode point
                    bool ok = r.Error == null && r.Ended && (r.Kills + r.Deaths > 0 || r.Points != 0) && r.NeverSpawned == 0;
                    if (ok) pass++; else fail++;
                    string line = $"{(ok ? "PASS" : "FAIL")} {mode,-13} {arena.InGameName,-20} | {r.Summary()}";
                    Console.WriteLine(line);
                    report.AppendLine(line);
                }
            }
            string summary = $"{pass}/{pass + fail} pass, {skip} not playable (entity data), {_asserts.Count} distinct asserts, "
                + $"in {total.Elapsed.TotalSeconds:0} s";
            foreach ((string message, int count) in _asserts)
            {
                summary += $"\n  assert x{count}: {message}";
            }
            Console.WriteLine(summary);
            report.AppendLine(summary);
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "extract_out", "multiplayer"));
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "match_sweep.txt"), report.ToString());
            Console.WriteLine($"written {Path.Combine(outDir, "match_sweep.txt")}");
        }

        // MphRead's Debug.Asserts would end the process in this Debug build; the app is a Release build (asserts
        // compiled out), so count them and carry on like the device does, and report them.
        private static readonly Dictionary<string, int> _asserts = new();
        private static string _lastCamSeq = "";
        private static int _assertsThisMatch;

        private sealed class AssertCounter : TraceListener
        {
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void Fail(string? message, string? detailMessage)
            {
                string where = new StackTrace(4, false).GetFrames().Select(f => f.GetMethod())
                    .FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("MphRead") == true
                        && m.DeclaringType != typeof(System.Diagnostics.Debug)) is System.Reflection.MethodBase m
                    ? $"{m.DeclaringType?.Name}.{m.Name}" : "?";
                string key = $"{message} at {where}";
                _asserts[key] = _asserts.TryGetValue(key, out int n) ? n + 1 : 1;
                _assertsThisMatch++;
            }
        }

        private static void CountAsserts()
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new AssertCounter());
        }

        private static void MakeRemote(MatchSettings settings, Options o)
        {
            if (o.Remote && settings.Players.Count > 1)
            {
                settings.Players[1].Bot = false;
                settings.Players[1].Remote = true;
                settings.Players[1].Nickname = "Remote";
            }
        }

        // A stand-in for a client device: aims at the nearest opponent with an ABSOLUTE view, walks forward, fires
        // when lined up. Goes through the real wire format (NetInput.Write/Read) every frame.
        private sealed class ScriptedClient
        {
            private readonly int _seat;
            private ushort _seq;
            private readonly byte[] _wire = new byte[MphRecomp.Multiplayer.Net.NetInput.Size];
            public float TargetYaw, TargetPitch;
            public int AimFrames, AimOffFrames;
            public float WorstAimError;

            public ScriptedClient(int seat) { _seat = seat; }

            public void Send(CampaignHost host)
            {
                PlayerEntity me = PlayerEntity.Players[_seat];
                PlayerEntity? target = null;
                float best = Single.MaxValue;
                for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (i == _seat || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0
                        || (GameState.Teams && p.TeamIndex == me.TeamIndex))
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
                var input = new MphRecomp.Multiplayer.Net.NetInput
                {
                    Seq = _seq++,
                    SelectWeapon = BeamType.None,
                    AimEpoch = (byte)me.NetAimEpoch, // a real client learns the epoch from the host snapshot
                    Yaw = me.NetYaw,
                    Pitch = me.NetPitch
                };
                if (target != null && me.Health > 0)
                {
                    Vector3 to = target.Position - me.Position;
                    input.Yaw = MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
                    float flat = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
                    input.Pitch = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(to.Y, flat)), -80, 80);
                    input.MoveY = 1;
                    float err = MathF.Abs(Wrap(input.Yaw - me.NetYaw));
                    if (err < 10 && host.Frame % 20 < 12)
                    {
                        input.Buttons = CampaignButtons.Shoot;
                    }
                }
                input.Write(_wire);
                var received = MphRecomp.Multiplayer.Net.NetInput.Read(_wire);
                TargetYaw = received.Yaw;
                TargetPitch = received.Pitch;
                host.SetRemoteInput(_seat, received);
            }

            // after the host step: did the seat view land on the target it was sent?
            public void Check()
            {
                PlayerEntity me = PlayerEntity.Players[_seat];
                if (me.Health == 0 || !me.LoadFlags.TestFlag(LoadFlags.Active) || me.IsAltForm
                    || MphRead.Formats.CameraSequence.Current?.Flags.TestFlag(MphRead.Formats.CamSeqFlags.BlockInput) == true)
                {
                    return;
                }
                AimFrames++;
                float err = MathF.Max(MathF.Abs(Wrap(TargetYaw - me.NetYaw)), MathF.Abs(TargetPitch - me.NetPitch));
                if (err > 0.1f)
                {
                    AimOffFrames++;
                }
                WorstAimError = MathF.Max(WorstAimError, err);
            }

            private static float Wrap(float d) => d - 360 * MathF.Floor((d + 180) / 360);
        }

        private sealed class MatchResult
        {
            public string? Error;
            public bool Ended;
            public long Frames;
            public MatchState LastState;
            public int Kills, Deaths, Points;
            public double LoadMs, SimMs;
            public int NeverSpawned; // seats that never became active
            public int StuckBots; // bots that barely moved all match
            public int Asserts;
            public string? RemoteNote;

            public string Summary()
            {
                if (Error != null)
                {
                    return $"ERROR {Error}";
                }
                string notes = "";
                if (!Ended) notes += " did-not-end";
                if (NeverSpawned > 0) notes += $" never-spawned={NeverSpawned}";
                if (StuckBots > 0) notes += $" stuck-bots={StuckBots}";
                if (Asserts > 0) notes += $" asserts={Asserts}";
                if (RemoteNote != null) notes += " " + RemoteNote;
                return $"{Frames} f {(Ended ? "ended" : LastState.ToString())} k {Kills} d {Deaths} p {Points} | "
                    + $"load {LoadMs:0} ms, {SimMs:0.00} ms/f |{notes}";
            }
        }

        private static MatchResult Play(MatchSettings settings, bool verbose, bool move)
        {
            var r = new MatchResult();
            _assertsThisMatch = 0;
            CampaignHost? host = null;
            try
            {
                var load = Stopwatch.StartNew();
                host = CampaignHost.StartMatch(settings);
                r.LoadMs = load.Elapsed.TotalMilliseconds;
                // time limit + game over (90 f) + ending (150 f) + fade, with slack
                long maxFrames = (long)((settings.TimeLimitSeconds ?? 900) * 60) + 600;
                int seats = settings.Players.Count;
                var spawned = new bool[seats];
                var start = new Vector3?[seats];
                var travel = new float[seats];
                var last = new Vector3[seats];
                int[] kills = new int[seats], deaths = new int[seats];
                MatchState state = GameState.MatchState;
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                ScriptedClient? remote = settings.Players.Count > 1 && settings.Players[1].Remote ? new ScriptedClient(1) : null;
                var sim = Stopwatch.StartNew();
                while (!host.Ended && host.Frame < maxFrames)
                {
                    remote?.Send(host);
                    if (move)
                    {
                        // wander: walk forward, turn slowly, fire now and then
                        input.Move = new System.Numerics.Vector2(0, 1);
                        input.AimDelta = new System.Numerics.Vector2(host.Frame % 240 < 120 ? 2 : -2, 0);
                        input.Buttons = host.Frame % 30 < 10 ? CampaignButtons.Shoot : CampaignButtons.None;
                    }
                    host.Step(input);
                    remote?.Check();
                    for (int i = 0; i < seats; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        if (p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health > 0)
                        {
                            if (!spawned[i])
                            {
                                spawned[i] = true;
                                start[i] = p.Position;
                                last[i] = p.Position;
                            }
                            travel[i] += (p.Position - last[i]).Length;
                            last[i] = p.Position;
                        }
                        if (verbose && (GameState.Kills[i] != kills[i] || GameState.Deaths[i] != deaths[i]))
                        {
                            Console.WriteLine($"  f{host.Frame,6} seat {i} {p.Hunter}: kills {GameState.Kills[i]} "
                                + $"deaths {GameState.Deaths[i]} points {GameState.Points[i]}");
                        }
                        kills[i] = GameState.Kills[i];
                        deaths[i] = GameState.Deaths[i];
                    }
                    if (verbose)
                    {
                        var cs = MphRead.Formats.CameraSequence.Current;
                        string csNow = cs == null ? "none" : $"{cs.SequenceId}{(cs.BlockInput ? " BlockInput" : "")}";
                        if (csNow != _lastCamSeq)
                        {
                            Console.WriteLine($"  f{host.Frame,6} camera sequence -> {csNow}");
                            _lastCamSeq = csNow;
                        }
                    }
                    if (GameState.MatchState != state)
                    {
                        state = GameState.MatchState;
                        if (verbose)
                        {
                            Console.WriteLine($"  f{host.Frame,6} match state -> {state}");
                        }
                    }
                }
                r.SimMs = sim.Elapsed.TotalMilliseconds / Math.Max(1, host.Frame);
                r.Asserts = _assertsThisMatch;
                if (remote != null)
                {
                    r.RemoteNote = $"remote: kills {GameState.Kills[1]} deaths {GameState.Deaths[1]}, aim off >0.1deg "
                        + $"{remote.AimOffFrames}/{remote.AimFrames} f, worst {remote.WorstAimError:0.00} deg";
                }
                r.Ended = host.Ended;
                r.Frames = host.Frame;
                r.LastState = GameState.MatchState;
                for (int i = 0; i < seats; i++)
                {
                    r.Kills += GameState.Kills[i];
                    r.Deaths += GameState.Deaths[i];
                    r.Points += GameState.Points[i];
                    if (!spawned[i])
                    {
                        r.NeverSpawned++;
                    }
                    else if (settings.Players[i].Bot && travel[i] < 10)
                    {
                        r.StuckBots++;
                    }
                }
                if (verbose)
                {
                    Console.WriteLine($"  travel: {string.Join(", ", travel.Select((t, i) => $"{settings.Players[i].Hunter} {t:0}"))}");
                }
            }
            catch (Exception ex)
            {
                r.Error = $"{ex.GetType().Name}: {ex.Message}";
                if (verbose)
                {
                    Console.WriteLine(ex);
                }
            }
            finally
            {
                try
                {
                    host?.Dispose();
                }
                catch (Exception ex)
                {
                    r.Error ??= $"dispose {ex.GetType().Name}: {ex.Message}";
                }
            }
            return r;
        }
    }
}
