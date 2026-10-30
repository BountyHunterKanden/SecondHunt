using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Framing;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // Replay mode R0, the determinism harness (docs/REPLAY_MODE.md). A scripted human plays seat 0 against bots; the
    // match is recorded (MphRecomp.Core Replay/) and played back through the same CampaignHost.Step, and every step's
    // state checksum must match.
    //
    //   -replaytest [mode=Battle] [arena=<name>] [bots=3] [level=2] [time=60] [hunter=Samus] [seed=1]
    //       records two matches (the first since launch, then a second), replays both in this process (after other
    //       matches) and each in a fresh process (-replayplay). Files: extract_out/replay/
    //   -replayplay <file> [view=nodraw|draw|wide|third|spectate] [trace=a-b] [dump=a-b]
    //       plays a recording, checking every step; exit code 0 = exact to the end. dump=a-b: the raw state of steps
    //       a..b (ReplayState.Dump, as the app logs it with --es replaydump a-b) to extract_out/replay/dump_<name>_pc.txt
    //   -replaysweep [mode=<one>|all] [arena=<one>] [bots=3] [level=2] [time=30] [view=nodraw] [record=nodraw]
    //       every arena x playable mode: record, then replay in this process -> extract_out/replay/replay_sweep.txt
    //   -drawinvariance [mode] [arena] [bots] [level] [time=60] [hunter] [seed=1]
    //       one recorded match (no drawing) played again five ways -- no draw, draw at 256x192, draw at 1920x1080 with
    //       DS framing, draw at 1920x1080 in third person, draw at 1920x1080 from a spectator camera chasing the other
    //       seats (Scene.HostView) -- reporting the first step where each part of the state differs (rng2, match, a
    //       seat, gameplay entities by type; rng1 / cosmetic entities for information)
    //   -replaygolden [view=nodraw] [only=<name>]
    //       R2c: plays every golden replay in tests/replays/ (checked in), checking every step; exit code 0 = all exact.
    //       A failure means the game logic changed: if that was intended, bump ReplayFile.CurrentSimVersion and
    //       re-record. -replaygolden record [only=<name>] [force]: re-records them from their specs (Goldens below);
    //       refused while a golden of the current sim version still fails (bump the version first) unless force;
    //       seed=/time=/... override the spec, to try one out (copy the keeper into Goldens)
    //   detmath=off: the platform's MathF instead of DetMath once the match is set up (R2b timing comparisons)
    //   every command also takes drawfree=off: play without Scene.HostDrawFreeSim (R0's behaviour, to see the coupling),
    //   or drawfree=recordoff: record without it (the app today), replay with it
    internal static partial class ReplayTest
    {
        private enum View { NoDraw, Draw, Wide, Third, Spectate }

        private sealed class Options
        {
            public string Mode = "Battle";
            public string? Arena;
            public int Bots = 3;
            public int Level = 2;
            public float Time = 60;
            public Hunter Hunter = Hunter.Samus;
            public int Seed = 1;
            public int? Goal; // goal=99: the point goal (a long match runs to its time limit)
            public Hunter[]? Opponents; // opponents=Spire,Spire,Spire: the bots' hunters (repeats allowed)
            public View RecordView = View.NoDraw; // -drawinvariance record=draw: the reference is drawn at 256x192
            public View View = View.NoDraw;
            public int TraceFrom = -1, TraceTo = -1; // -replayplay: print seat 0 per step in this range
            public int DumpFrom = -1, DumpTo = -1; // -replayplay dump=a-b: ReplayState.Dump per step -> a file (R2b)
            // drawfree=off / recordoff: Scene.HostDrawFreeSim off (after the replay code turned it on) while recording,
            // and with off, while replaying too
            public bool DrawFreeRecord = true, DrawFreeReplay = true;
            public bool Pov; // -replayplay pov=1: each seat's first-person gun vs its own view (ReplayPovProbe, B3)
            public bool HudSeat; // -replayplay hudseat=1: drawn with each seat's HUD in turn, as watching it in the app (B3)
            // -replayplay skipdraw=N (with view=draw): runs of N steps alternately skip the draw list (HostSkipDrawList, as a
            // seek's unseen steps in the app) and build it; the match must play the same (B4)
            public int SkipDraw;
        }

        private static Options Parse(IEnumerable<string> args, Options o)
        {
            foreach (string arg in args)
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
                case "seed": o.Seed = Int32.Parse(value); break;
                case "goal": o.Goal = Int32.Parse(value); break;
                case "opponents":
                    o.Opponents = value.Split(',').Select(h => Enum.Parse<Hunter>(h, ignoreCase: true)).ToArray();
                    break;
                case "trace":
                    o.TraceFrom = Int32.Parse(value.Split('-')[0]);
                    o.TraceTo = Int32.Parse(value.Split('-')[^1]);
                    break;
                case "dump":
                    o.DumpFrom = Int32.Parse(value.Split('-')[0]);
                    o.DumpTo = Int32.Parse(value.Split('-')[^1]);
                    break;
                case "record": o.RecordView = value == "draw" ? View.Draw : View.NoDraw; break;
                case "detmath": _detMath = value != "off"; break;
                case "drawfree":
                    o.DrawFreeRecord = value != "off" && value != "recordoff";
                    o.DrawFreeReplay = value != "off";
                    break;
                case "pov": o.Pov = value != "0"; break;
                case "hudseat": o.HudSeat = value != "0"; break;
                case "skipdraw": o.SkipDraw = Int32.Parse(value); break;
                case "view": o.View = Enum.Parse<View>(value == "nodraw" ? "NoDraw" : value, ignoreCase: true); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            return o;
        }

        private static GameMode ParseMode(string mode) => Enum.Parse<GameMode>(mode.Replace(" ", ""), ignoreCase: true);

        private static string OutDir()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "extract_out", "replay"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static MatchSettings Settings(Options o, GameMode mode, RoomMetadata arena)
        {
            MatchSettings settings = MatchSettings.Quick(mode, arena.Name, o.Hunter, o.Opponents == null ? o.Bots : 0, o.Level);
            if (o.Opponents != null)
            {
                foreach (Hunter h in o.Opponents)
                {
                    settings.Players.Add(new MatchPlayer { Hunter = h, Bot = true, BotLevel = o.Level });
                }
            }
            settings.TimeLimitSeconds = o.Time;
            if (o.Goal != null) settings.PointGoal = o.Goal;
            return settings;
        }

        // ---- the commands ----

        public static void Test(string[] args)
        {
            Options o = Parse(args.Skip(1), new Options());
            (_drawFreeRecord, _drawFreeReplay) = (o.DrawFreeRecord, o.DrawFreeReplay);
            if (!Setup())
            {
                return;
            }
            GameMode mode = ParseMode(o.Mode);
            RoomMetadata arena = MatchArenas.Find(o.Arena ?? "Combat Hall") ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings settings = Settings(o, mode, arena);
            Console.WriteLine($"{mode} in {arena.InGameName}, {settings.Players.Count} players, bot level {o.Level}, "
                + $"time {o.Time} s, seed {o.Seed}");
            string dir = OutDir();
            string fileA = Path.Combine(dir, "replaytest_a.mphreplay");
            string fileB = Path.Combine(dir, "replaytest_b.mphreplay");
            int failures = 0;

            Run a = Record(settings, o.Seed, View.NoDraw, types: false);
            Console.WriteLine($"record A (first match since launch): {a.Summary()}");
            Run b = Record(settings, o.Seed + 1, View.NoDraw, types: false);
            Console.WriteLine($"record B (second match):             {b.Summary()}");
            if (a.Error != null || b.Error != null)
            {
                Environment.ExitCode = 1;
                return;
            }
            a.File!.Save(fileA);
            b.File!.Save(fileB);
            Console.WriteLine($"  files: {fileA} ({new FileInfo(fileA).Length / 1024} KB), {Path.GetFileName(fileB)} "
                + $"({new FileInfo(fileB).Length / 1024} KB)");

            foreach ((string name, string path) in new[] { ("A", fileA), ("B", fileB) })
            {
                ReplayFile file = ReplayFile.Load(path); // through the file, as a viewer would
                Run r = Replay(file, View.NoDraw, types: false, reference: null);
                Console.WriteLine($"replay {name} in this process:          {r.Verdict()}");
                failures += r.Exact ? 0 : 1;
            }
            failures += LibraryCheck(dir, a.File!);
            foreach ((string name, string path) in new[] { ("A", fileA), ("B", fileB) })
            {
                (int code, string output) = RunChild("-replayplay", path);
                string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                Console.WriteLine($"replay {name} in a fresh process:       {(code == 0 ? "" : $"[exit {code}] ")}{last}");
                failures += code == 0 ? 0 : 1;
            }
            // the checks must catch a change: a second of aim nudged a third of the way in, with seat 0 alive and on foot
            // there, has to fail inside that second. (The game ignores aim in some states -- Samus's morph ball (only
            // Trace, Sylux and Weavel aim in alt form, PlayerInput.cs ~1233), blocking camera sequences -- and a nudge
            // there changes nothing, so "exact" would be the right answer.)
            {
                ReplayFile tampered = ReplayFile.Load(fileA);
                int at = tampered.Steps.Count / 3;
                while (at < a.Live.Count - 2 && !(a.Live[at - 1] && a.Live[at] && a.Live[at + 1]))
                {
                    at++;
                }
                int end = Math.Min(at + 60, tampered.Steps.Count);
                for (int i = at; i < end; i++)
                {
                    ReplayStep step = tampered.Steps[i];
                    step.Input.AimDelta += new System.Numerics.Vector2(6, 0);
                    tampered.Steps[i] = step;
                }
                Run r = Replay(tampered, View.NoDraw, types: false, reference: null);
                bool caught = r.FirstFail >= at && r.FirstFail < end;
                Console.WriteLine($"replay A, aim nudged from step {at}:   {r.Verdict()} -> {(caught ? "caught" : "NOT CAUGHT")}");
                failures += caught ? 0 : 1;
            }
            // and a device whose arena files differ (ReplayData) must refuse before the first step
            {
                ReplayFile other = ReplayFile.Load(fileA);
                string recorded = other.Data;
                other.Data = System.Text.RegularExpressions.Regex.Replace(recorded, "collision=[0-9a-f]{8}", "collision=00000000");
                Run r = Replay(other, View.NoDraw, types: false, reference: null);
                bool refused = recorded.Contains("collision=") && r.Error?.StartsWith("refused: ") == true && r.Frames == 0;
                Console.WriteLine($"replay A, other collision file:     {r.Error ?? r.Verdict()} -> {(refused ? "refused" : "NOT REFUSED")}");
                Console.WriteLine($"  recorded on: {recorded}");
                failures += refused ? 0 : 1;
            }
            Console.WriteLine(failures == 0 ? "PASS: every replay exact to the end, and a changed input is caught"
                : $"FAIL: {failures} check(s) failed");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        public static void PlayFile(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayplay <file> [view=nodraw|draw|wide|third]");
                return;
            }
            Options o = Parse(args.Skip(2), new Options());
            (_drawFreeRecord, _drawFreeReplay) = (o.DrawFreeRecord, o.DrawFreeReplay);
            _skipDraw = o.SkipDraw;
            if (!Setup())
            {
                return;
            }
            ReplayFile file;
            try
            {
                file = ReplayFile.Load(args[1]);
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"REFUSED: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Settings.Players.Count} players, {file.Steps.Count} steps, recorded on "
                + $"{file.Platform} ({file.Build})");
            _traceFrom = o.TraceFrom;
            _traceTo = o.TraceTo;
            (_dumpFrom, _dumpTo) = (o.DumpFrom, o.DumpTo);
            _dump = o.DumpFrom >= 0 ? new List<string>() : null;
            if (o.Pov) ReplayPovProbe.Install();
            if (o.HudSeat)
            {
                // the watched seat's HUD, the next seat every 5 s, as MatchReplay.PlaceReplayView sets it (the recorder's own
                // seat: its own HUD, HostHudSeat null; a served match's seats are all watched, HostSeatlessHud)
                bool seatless = file.Settings.ServedHost && file.Settings.Players[file.Settings.LocalSeat].Remote;
                int seats = file.Settings.Players.Count;
                _beforeStep = host =>
                {
                    PlayerEntity p = PlayerEntity.Players[(int)(host.Frame / 300 % seats)];
                    PlayerEntity.HostSeatlessHud = seatless;
                    PlayerEntity.HostHudSeat = p == PlayerEntity.Main && !seatless ? null : p;
                };
                if (o.View == View.NoDraw) o.View = View.Draw;
            }
            Run r;
            try
            {
                r = Replay(file, o.View, types: false, reference: null, stopOnFail: _dump == null);
            }
            finally
            {
                if (o.HudSeat)
                {
                    _beforeStep = null;
                    PlayerEntity.HostSeatlessHud = false;
                    PlayerEntity.HostHudSeat = null;
                }
            }
            Console.WriteLine(r.Verdict());
            if (o.Pov) ReplayPovProbe.Report();
            if (_dump != null)
            {
                // the same lines the app logs under MPHReplayDump (--es replaydump a-b), to diff across devices
                string path = Path.Combine(OutDir(), $"dump_{Path.GetFileNameWithoutExtension(args[1])}_pc.txt");
                File.WriteAllLines(path, _dump);
                Console.WriteLine($"dump of steps {o.DumpFrom}-{o.DumpTo}: {path}");
            }
            Environment.ExitCode = r.Exact ? 0 : 1;
        }

        public static void Sweep(string[] args)
        {
            Options o = Parse(args.Skip(1), new Options { Mode = "all", Time = 30 });
            (_drawFreeRecord, _drawFreeReplay) = (o.DrawFreeRecord, o.DrawFreeReplay);
            if (!Setup())
            {
                return;
            }
            IReadOnlyList<GameMode> modes = o.Mode == "all" ? MatchSettings.Modes : new[] { ParseMode(o.Mode) };
            IEnumerable<RoomMetadata> arenas = o.Arena == null ? MatchArenas.All
                : new[] { MatchArenas.Find(o.Arena) ?? throw new ArgumentException($"no arena {o.Arena}") };
            var report = new List<string>
            {
                $"# replay sweep {DateTime.Now:yyyy-MM-dd HH:mm}: {o.Bots} bots level {o.Level}, time {o.Time} s, "
                    + $"recorded {o.RecordView}, replayed {o.View}{(o.DrawFreeRecord ? "" : o.DrawFreeReplay ? ", drawfree=recordoff" : ", drawfree=off")}",
                "# result mode arena | record | replay"
            };
            int pass = 0, fail = 0, skip = 0, seed = o.Seed;
            var total = Stopwatch.StartNew();
            foreach (RoomMetadata arena in arenas)
            {
                foreach (GameMode mode in modes)
                {
                    if (!MatchArenas.Supports(arena, mode, o.Bots + 1))
                    {
                        skip++;
                        continue;
                    }
                    MatchSettings settings = Settings(o, mode, arena);
                    Run rec = Record(settings, seed++, o.RecordView, types: false);
                    Run rep = rec.Error == null ? Replay(rec.File!, o.View, types: false, reference: rec) : rec;
                    bool ok = rec.Error == null && rep.Exact;
                    if (ok) pass++; else fail++;
                    string line = $"{(ok ? "PASS" : "FAIL")} {mode,-13} {arena.InGameName,-20} | {rec.Summary()} | {rep.Verdict()}";
                    Console.WriteLine(line);
                    report.Add(line);
                }
            }
            string summary = $"{pass}/{pass + fail} exact, {skip} not playable, in {total.Elapsed.TotalSeconds:0} s";
            Console.WriteLine(summary);
            report.Add(summary);
            string path = Path.Combine(OutDir(), "replay_sweep.txt");
            File.WriteAllLines(path, report);
            Console.WriteLine($"written {path}");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        public static void DrawInvariance(string[] args)
        {
            Options o = Parse(args.Skip(1), new Options());
            (_drawFreeRecord, _drawFreeReplay) = (o.DrawFreeRecord, o.DrawFreeReplay);
            if (!Setup())
            {
                return;
            }
            GameMode mode = ParseMode(o.Mode);
            RoomMetadata arena = MatchArenas.Find(o.Arena ?? "Combat Hall") ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings settings = Settings(o, mode, arena);
            Console.WriteLine($"{mode} in {arena.InGameName}, {settings.Players.Count} players ({string.Join(", ",
                settings.Players.Select(p => p.Hunter))}), bot level {o.Level}, time {o.Time} s, seed {o.Seed}");
            Run reference = Record(settings, o.Seed, o.RecordView, types: true);
            Console.WriteLine($"recorded with {(o.RecordView == View.Draw ? "a draw list at 256x192" : "no drawing")}: "
                + reference.Summary());
            if (reference.Error != null)
            {
                return;
            }
            bool allSame = true;
            foreach (View view in new[] { View.NoDraw, View.Draw, View.Wide, View.Third, View.Spectate })
            {
                Run r = Replay(reference.File!, view, types: true, reference: reference, stopOnFail: false);
                Console.WriteLine($"{Describe(view)}: {r.Verdict()}; Spire alt attack {r.SpireAttackSteps} steps, "
                    + $"a rock near a player {r.SpireRockNear}");
                foreach (string line in CompareRuns(reference, r))
                {
                    Console.WriteLine("    " + line);
                }
                allSame &= r.Exact;
            }
            Console.WriteLine(allSame ? "PASS: drawing never changed the match" : "FAIL: drawing changed the match (see above)");
            Environment.ExitCode = allSame ? 0 : 1;
        }

        // ---- R2c: golden replays (tests/replays/, checked in: inputs, settings and checksums only, no game data) ----

        // Each golden's spec. Recorded drawn at 256x192 as the app records, 60 s, 3 bots level 2 unless it says
        // otherwise. Between them: every mode family, seat 0 (the scripted human: moves, aims, fires, jumps, morphs,
        // boosts, bombs, alt attacks, zooms, cycles weapons, the wheel) as each of the seven hunters, an Echoes arena,
        // and a Spire-heavy match (the rocks, R1). Add new ones at the end; a new spec just needs `record only=<name>`.
        private static readonly (string Name, string[] Spec)[] Goldens =
        {
            ("battle-combat-hall-samus", new[] { "mode=Battle", "arena=Combat Hall", "hunter=Samus", "seed=101" }),
            ("survival-ice-hive-noxus", new[] { "mode=Survival", "arena=Ice Hive", "hunter=Noxus", "seed=102" }),
            ("capture-elder-passage-kanden", new[] { "mode=Capture", "arena=Elder Passage", "hunter=Kanden", "seed=103" }),
            ("bounty-fault-line-weavel", new[] { "mode=Bounty", "arena=Fault Line", "hunter=Weavel", "seed=104" }),
            ("nodes-sanctorus-trace", new[] { "mode=Nodes", "arena=Sanctorus", "hunter=Trace", "seed=105" }),
            ("defender-harvester-sylux", new[] { "mode=Defender", "arena=Harvester", "hunter=Sylux", "seed=106" }),
            ("primehunter-head-shot-spire", new[] { "mode=PrimeHunter", "arena=Head Shot", "hunter=Spire", "seed=107" }),
            ("battle-combat-hall-spire-x3", new[] { "mode=Battle", "arena=Combat Hall", "hunter=Samus",
                "opponents=Spire,Spire,Spire", "time=90", "seed=8" }),
            ("battle-pipeline-samus", new[] { "mode=Battle", "arena=Pipeline", "hunter=Samus", "seed=109" }),
            // Shooting Gallery's manned turret and bomb slot, bots using them (format 42 hashes the turret)
            ("battle-shooting-gallery-kanden", new[] { "mode=Battle", "arena=Shooting Gallery", "hunter=Kanden", "seed=110" }),
        };

        public static string GoldenDir() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "replays"));

        public static void Golden(string[] args)
        {
            bool record = args.Contains("record"), force = args.Contains("force");
            string? only = args.FirstOrDefault(a => a.StartsWith("only="))?[5..];
            Options o = Parse(args.Skip(1).Where(a => a != "record" && a != "force" && !a.StartsWith("only=")), new Options());
            (_drawFreeRecord, _drawFreeReplay) = (o.DrawFreeRecord, o.DrawFreeReplay);
            if (!Setup())
            {
                return;
            }
            (string Name, string[] Spec)[] selected = Goldens.Where(g => only == null || g.Name == only).ToArray();
            if (selected.Length == 0)
            {
                Console.WriteLine($"no golden named {only}; there are: {string.Join(", ", Goldens.Select(g => g.Name))}");
                Environment.ExitCode = 2;
                return;
            }
            string dir = GoldenDir();
            Directory.CreateDirectory(dir);
            var total = Stopwatch.StartNew();
            if (record)
            {
                // the rule this check enforces: a sim change that alters play must bump the sim version, so that players'
                // old replays are refused up front rather than stopped part-way. Re-recording a golden that no longer
                // plays under the same version would hide exactly that.
                if (!force)
                {
                    var stale = new List<string>();
                    foreach ((string name, _) in selected)
                    {
                        string path = Path.Combine(dir, name + ReplayLibrary.Extension);
                        if (File.Exists(path) && ReplayFile.Load(path) is ReplayFile old
                            && old.SimVersion == ReplayFile.CurrentSimVersion && !Replay(old, View.NoDraw, false, null).Exact)
                        {
                            stale.Add(name);
                        }
                    }
                    if (stale.Count > 0)
                    {
                        Console.WriteLine($"REFUSED: {string.Join(", ", stale)} no longer play(s) exactly under sim version "
                            + $"{ReplayFile.CurrentSimVersion}. The game logic changed: bump ReplayFile.CurrentSimVersion, then "
                            + "record again (or add force if the change is a fix to the goldens themselves).");
                        Environment.ExitCode = 1;
                        return;
                    }
                }
                int bad = 0;
                foreach ((string name, string[] spec) in selected)
                {
                    // seed=, time=, ... on the command line override the spec (to try one out: copy the keeper into Goldens)
                    Options so = Parse(spec.Concat(args.Skip(1).Where(a => a.Contains('=') && !a.StartsWith("only=")
                        && !a.StartsWith("view=") && !a.StartsWith("drawfree="))), new Options { RecordView = View.Draw });
                    RoomMetadata arena = MatchArenas.Find(so.Arena!) ?? throw new ArgumentException($"no arena {so.Arena}");
                    MatchSettings settings = Settings(so, ParseMode(so.Mode), arena);
                    // every golden starts like the first match after launch (SceneSetup resets both RNG streams), not
                    // from wherever the previous one left them, so a re-record from its spec gives the same file
                    Menu.PreviousSaveSlot = -1;
                    Run rec = Record(settings, so.Seed, so.RecordView, types: false);
                    Run check = rec.Error == null ? Replay(rec.File!, View.NoDraw, false, rec) : rec;
                    if (!check.Exact)
                    {
                        Console.WriteLine($"FAIL   {name}: {rec.Summary()}; replayed: {check.Verdict()} (not saved)");
                        bad++;
                        continue;
                    }
                    rec.File!.Build = "tools golden";
                    string path = Path.Combine(dir, name + ReplayLibrary.Extension);
                    rec.File.Save(path);
                    // how much this golden would see: played without Scene.HostDrawFreeSim (Spire's rocks never placed
                    // without drawing, R0's coupling), where does it first differ? Goldens that never differ there don't
                    // exercise a Spire rock hit.
                    _drawFreeReplay = false;
                    Run off = Replay(rec.File, View.NoDraw, false, rec);
                    _drawFreeReplay = o.DrawFreeReplay;
                    Console.WriteLine($"saved  {name}: {rec.Summary()}, {new FileInfo(path).Length / 1024} KB, "
                        + $"sim version {rec.File.SimVersion}; Spire alt attack {rec.SpireAttackSteps} steps, rock near "
                        + $"{rec.SpireRockNear}; {(off.Exact ? "no rock hit" : $"rocks matter from step {off.FirstFail}")}");
                }
                Console.WriteLine(bad == 0 ? $"recorded {selected.Length} golden replay(s) in {total.Elapsed.TotalSeconds:0} s -> {dir}"
                    : $"FAIL: {bad} golden replay(s) not recorded");
                Environment.ExitCode = bad == 0 ? 0 : 1;
                return;
            }
            int pass = 0, fail = 0, skip = 0;
            foreach ((string name, _) in selected)
            {
                string path = Path.Combine(dir, name + ReplayLibrary.Extension);
                string result;
                if (!File.Exists(path))
                {
                    result = "FAIL   missing (-replaygolden record only=" + name + ")";
                }
                else
                {
                    ReplayFile? file = null;
                    string? damaged = null;
                    try
                    {
                        file = ReplayFile.Load(path);
                    }
                    catch (InvalidDataException ex)
                    {
                        damaged = ex.Message;
                    }
                    if (file == null)
                    {
                        result = $"FAIL   damaged: {damaged}";
                    }
                    else if (MatchArenas.Find(file.Settings.Arena) == null)
                    {
                        result = $"SKIP   arena {file.Settings.Arena} isn't installed here";
                    }
                    else
                    {
                        Run r = Replay(file, o.View, types: false, reference: null);
                        result = $"{(r.Exact ? "PASS" : "FAIL")}   {r.Verdict()}";
                    }
                }
                if (result.StartsWith("PASS")) pass++; else if (result.StartsWith("SKIP")) skip++; else fail++;
                Console.WriteLine($"{result[..7]}{name,-30} {result[7..]}");
            }
            string summary = $"{pass}/{pass + fail} golden replays exact ({o.View}{(skip > 0 ? $", {skip} skipped" : "")}), "
                + $"sim version {ReplayFile.CurrentSimVersion}, {total.Elapsed.TotalSeconds:0} s";
            Console.WriteLine(fail == 0 ? "PASS: " + summary : "FAIL: " + summary
                + "\n  The game logic changed. If that was intended: bump ReplayFile.CurrentSimVersion (players' old replays"
                + " are then refused up front, not stopped part-way) and run -replaygolden record. If not: it's a bug;"
                + " -replayplay <file> trace=a-b shows seat 0 around the step.");
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        // R2b: what the app's ReplayVerifyActivity does on a device (Core ReplayVerify), with the same lines, to diff:
        //   -replayverify <file|dir> [dump=a-b]   (dir: every .mphreplay in it, by name; default tests/replays)
        public static void Verify(string[] args)
        {
            string target = args.Length > 1 && !args[1].Contains('=') ? args[1] : GoldenDir();
            Options o = Parse(args.Skip(target == GoldenDir() ? 1 : 2), new Options());
            if (!Setup())
            {
                return;
            }
            string[] files = Directory.Exists(target)
                ? Directory.GetFiles(target, "*" + ReplayLibrary.Extension).OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : new[] { target };
            int exact = 0;
            var dump = new List<string>();
            foreach (string path in files)
            {
                ReplayVerify.Result r = ReplayVerify.Run(ReplayFile.Load(path), o.DumpFrom < 0 ? null : (step, scene) =>
                {
                    if (step >= o.DumpFrom && step <= o.DumpTo)
                    {
                        dump.AddRange(ReplayState.Dump(scene, step).Split('\n'));
                    }
                });
                exact += r.Exact ? 1 : 0;
                Console.WriteLine($"verify {Path.GetFileName(path)}: {r.Verdict}");
            }
            Console.WriteLine($"verify: {exact}/{files.Length} exact on {ReplayFile.DescribePlatform()}");
            if (o.DumpFrom >= 0)
            {
                string path = Path.Combine(OutDir(), "dump_verify_pc.txt");
                File.WriteAllLines(path, dump);
                Console.WriteLine($"dump: {path}");
            }
            Environment.ExitCode = exact == files.Length ? 0 : 1;
        }

        // R2's storage (Core Replay/ReplayLibrary.cs), as the app uses it: twelve saves keep the newest ten, a pinned one
        // stays out of the count, the newest is found and plays back exactly. Returns the failures (0 or 1).
        private static int LibraryCheck(string outDir, ReplayFile file)
        {
            string data = Path.Combine(outDir, "library_test");
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
            var start = new DateTime(2026, 10, 7, 12, 0, 0);
            var saved = new List<string>();
            // format 3: the seats' places (the list's win / loss) survive the save and the header read
            file.Places.Add(new ReplayPlace(file.Settings.LocalSeat, 0, 7));
            for (int i = 0; i < 12; i++)
            {
                saved.Add(ReplayLibrary.Save(data, file, start.AddMinutes(i)));
            }
            IReadOnlyList<string> recent = ReplayLibrary.Recent(data);
            bool keptNewest = recent.Count == ReplayLibrary.KeepRecent && recent[0] == saved[^1] && recent[^1] == saved[2]
                && !File.Exists(saved[0]) && !File.Exists(saved[1]);
            string pinned = ReplayLibrary.Pin(data, saved[^1]);
            ReplayLibrary.Save(data, file, start.AddMinutes(-5)); // an older one: the pinned one stays the newest
            bool pinOk = ReplayLibrary.Pinned(data).Count == 1 && ReplayLibrary.Recent(data).Count == ReplayLibrary.KeepRecent
                && File.Exists(pinned) && ReplayLibrary.Newest(data) is string n && File.GetLastWriteTimeUtc(n) >= File.GetLastWriteTimeUtc(pinned);
            Run r = Replay(ReplayFile.Load(ReplayLibrary.Newest(data)!), View.NoDraw, types: false, reference: null);
            // R3's list: every file once (the pinned one is the newest here), read from headers; rename keeps the time;
            // delete; a damaged file is listed, refused
            IReadOnlyList<ReplayInfo> list = ReplayLibrary.List(data);
            ReplayInfo top = list[0];
            bool listOk = list.Count == ReplayLibrary.KeepRecent + 1 && top.Pinned && top.Path == pinned && top.PlaysHere
                && top.Steps == file.Steps.Count && top.Complete == file.Complete && top.Mode == file.Settings.Mode
                && top.Recorded == start.AddMinutes(11) && list.Skip(1).All(i => !i.Pinned && i.PlaysHere)
                && top.LocalPlace == 0 && top.Header!.Places.Count == 1 && top.Header.Places[0].Points == 7;
            string renamed = ReplayLibrary.Rename(list[1].Path, "My best: 3 kills!");
            ReplayInfo again = ReplayLibrary.Describe(renamed, pinned: false);
            bool renameOk = !File.Exists(list[1].Path) && again.Title == "My best_ 3 kills_" && again.Recorded == list[1].Recorded
                && ReplayLibrary.List(data)[1].Path == renamed;
            ReplayLibrary.Delete(renamed);
            // the page's RENAME also pins (a named replay outlives the last ten); renaming a pinned one keeps it there
            string kept = ReplayLibrary.RenameAndKeep(data, ReplayLibrary.List(data)[1].Path, "grate stuck");
            string keptAgain = ReplayLibrary.RenameAndKeep(data, kept, "grate stuck 2");
            renameOk &= ReplayLibrary.IsPinned(data, kept) && ReplayLibrary.IsPinned(data, keptAgain) && !File.Exists(kept)
                && ReplayLibrary.Describe(keptAgain, pinned: true).Title == "grate stuck 2";
            ReplayLibrary.Delete(keptAgain);
            File.WriteAllBytes(Path.Combine(ReplayLibrary.RecentDir(data), "20261007-130000 junk" + ReplayLibrary.Extension), new byte[] { 1, 2, 3 });
            IReadOnlyList<ReplayInfo> after = ReplayLibrary.List(data);
            bool deleteOk = after.Count == ReplayLibrary.KeepRecent && /* (two went: renamed + kept) */ after.All(i => i.Path != renamed)
                && after.Single(i => i.Title == "junk") is { Header: null, PlaysHere: false };
            bool ok = keptNewest && pinOk && r.Exact && listOk && renameOk && deleteOk;
            Console.WriteLine($"library (12 saves, keep {ReplayLibrary.KeepRecent}, pin, newest, list, rename, delete): "
                + $"{(ok ? "OK" : $"FAIL kept {keptNewest} pin {pinOk} list {listOk} rename {renameOk} delete {deleteOk}")}; "
                + $"newest played back: {r.Verdict()}\n  e.g. {Path.GetFileName(recent[0])}; list row: \"{top.Title}\", {top.Arena}, "
                + $"{top.Seconds} s, {top.Players.Count} players, {(top.Complete ? "complete" : "incomplete")}");
            Directory.Delete(data, recursive: true);
            file.Places.Clear();
            return ok ? 0 : 1;
        }

        private static string Describe(View view) => view switch
        {
            View.NoDraw => "no drawing",
            View.Draw => "draw list at 256x192",
            View.Wide => "draw list at 1920x1080, DS framing",
            View.Third => "draw list at 1920x1080, third person",
            _ => "draw list at 1920x1080, spectator camera"
        };

        // ---- playing a match ----

        private sealed class Run
        {
            public ReplayFile? File;
            public readonly List<ReplayStateParts> Parts = new();
            // seat 0 (the scripted human) spawned, alive and on foot after each step (aim turns her)
            public readonly List<bool> Live = new();
            // diagnostics: steps with a Spire alt attack on (without Scene.HostDrawFreeSim, PlayerDraw.cs updates its
            // rocks only while drawing; PlayerCollision.cs ~124 hits with them), and of those, a rock within 1.5 of
            // another seat
            public int SpireAttackSteps, SpireRockNear;
            public List<ulong[]>? Types;
            public string? Error;
            public long Frames;
            public bool Ended;
            public double SimMs;
            public int Kills, Deaths;
            // playback only
            public int FirstFail = -1;
            public bool StepsMatch = true;
            public bool Exact => Error == null && FirstFail < 0 && StepsMatch;

            public string Summary() => Error != null ? $"ERROR {Error}"
                : $"{Frames} steps, {(Ended ? "ended" : "did not end")}, k {Kills} d {Deaths}, {SimMs:0.00} ms/step";

            public string Verdict()
            {
                if (Error != null)
                {
                    return $"ERROR {Error}";
                }
                if (FirstFail >= 0)
                {
                    return $"NOT EXACT from step {FirstFail} of {File?.Steps.Count} ({SimMs:0.00} ms/step)";
                }
                if (!StepsMatch)
                {
                    return $"NOT EXACT: ended at step {Frames}, recording has {File?.Steps.Count} (ended {Ended})";
                }
                return $"exact, {Frames} steps ({SimMs:0.00} ms/step)";
            }
        }

        private static Run Record(MatchSettings settings, int seed, View view, bool types)
        {
            var run = new Run { Types = types ? new List<ulong[]>() : null };
            var script = new ScriptedHuman(seed);
            CampaignHost? host = null;
            try
            {
                var rec = new ReplayRecorder(settings, "tools");
                host = StartFor(view, () => CampaignHost.StartMatch(settings, view != View.NoDraw,
                    view == View.Draw ? 256 : 1920, view == View.Draw ? 192 : 1080));
                rec.Started(host.Scene);
                host.Scene.HostDrawFreeSim = _drawFreeRecord;
                long maxFrames = (long)((settings.TimeLimitSeconds ?? 900) * 60) + 600;
                var sim = Stopwatch.StartNew();
                while (!host.Ended && host.Frame < maxFrames)
                {
                    CampaignInput input = script.Next(host);
                    BeforeStep(view, host);
                    host.Step(input);
                    ReplayStateParts parts = ReplayState.Capture(host.Scene);
                    rec.File.Steps.Add(new ReplayStep { Input = input, Checksum = parts.Checksum });
                    Collect(run, host, parts);
                }
                run.SimMs = sim.Elapsed.TotalMilliseconds / Math.Max(1, host.Frame);
                rec.End(host.Ended);
                run.File = rec.File;
                Finish(run, host);
            }
            catch (Exception ex)
            {
                run.Error = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine(ex);
            }
            finally
            {
                EndView(view, host);
            }
            return run;
        }

        private static Run Replay(ReplayFile file, View view, bool types, Run? reference, bool stopOnFail = true)
        {
            var run = new Run { File = file, Types = types ? new List<ulong[]>() : null };
            CampaignHost? host = null;
            try
            {
                string? why = ReplayPlayer.CheckBeforeStart(file);
                if (why != null)
                {
                    run.Error = "refused: " + why;
                    return run;
                }
                var player = new ReplayPlayer(file);
                host = StartFor(view, () => player.Start(view != View.NoDraw, view == View.Draw ? 256 : 1920,
                    view == View.Draw ? 192 : 1080));
                host.Scene.HostDrawFreeSim = _drawFreeReplay;
                why = player.CheckAfterStart();
                if (why != null)
                {
                    run.Error = "refused: " + why;
                    return run;
                }
                var sim = Stopwatch.StartNew();
                while (!host.Ended && player.Position < file.Steps.Count)
                {
                    CampaignInput input = file.Steps[player.Position].Input;
                    player.BeforeStep(); // (a LAN / online host's recording: its remote seats' inputs and drops)
                    BeforeStep(view, host);
                    _beforeStep?.Invoke(host); // (-replaynettest served=1: the watched seat's HUD, ReplayTest.Net.cs)
                    host.Scene.HostSkipDrawList = _skipDraw > 0 && player.Position / _skipDraw % 2 == 0;
                    host.Step(input);
                    host.Scene.HostSkipDrawList = false;
                    if (_dump != null && run.Parts.Count >= _dumpFrom && run.Parts.Count <= _dumpTo)
                    {
                        _dump.Add(ReplayState.Dump(host.Scene, run.Parts.Count));
                    }
                    else if (_dump != null && run.Parts.Count > _dumpTo)
                    {
                        break;
                    }
                    ReplayStateParts parts = ReplayState.Capture(host.Scene);
                    bool ok = player.Check(host.Scene);
                    Collect(run, host, parts);
                    if (!ok && run.FirstFail < 0)
                    {
                        run.FirstFail = player.FailedAt;
                        if (stopOnFail)
                        {
                            break;
                        }
                    }
                }
                run.SimMs = sim.Elapsed.TotalMilliseconds / Math.Max(1, host.Frame);
                Finish(run, host);
                if (run.FirstFail < 0)
                {
                    // the recording's end: same number of steps, and ended (or not) the same way
                    run.StepsMatch = host.Frame == file.Steps.Count && host.Ended == file.Complete;
                }
            }
            catch (Exception ex)
            {
                run.Error = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine(ex);
            }
            finally
            {
                EndView(view, host);
            }
            return run;
        }

        private static bool _drawFreeRecord = true, _drawFreeReplay = true;
        private static int _skipDraw; // -replayplay skipdraw=N (B4)
        private static Action<CampaignHost>? _beforeStep;
        private static bool _detMath = true;

        private static CampaignHost StartFor(View view, Func<CampaignHost> start)
        {
            Scene.HostFramingAspect = view == View.Wide ? ViewFraming.DsAspect : 0;
            PlayerEntity.HostShowMainBiped = view == View.Third || view == View.Spectate;
            CampaignHost host = start();
            if (!_detMath)
            {
                DetMathF.HostDeterministic = false; // detmath=off: the platform's MathF after the match's setup (timing)
            }
            return host;
        }

        // the app's third-person camera sits behind and above her after each step; the next step's frustum uses it.
        // Spectate: a render-only chase camera behind another seat (the next one every 10 s), like R4's spectator view;
        // every 5 s it switches between the main player's field of view and its own 60 degrees.
        private static void BeforeStep(View view, CampaignHost host)
        {
            if (view == View.Third)
            {
                PlayerNetState s = host.Player.NetCapture();
                host.Scene.HostCameraOffset = -s.Facing * 2.5f + s.Up * 0.7f;
            }
            else if (view == View.Spectate && PlayerEntity.PlayerCount > 1)
            {
                int seat = 1 + (int)(host.Frame / 600 % (PlayerEntity.PlayerCount - 1));
                PlayerNetState s = PlayerEntity.Players[seat].NetCapture();
                var back = new Vector3(s.Facing.X, 0, s.Facing.Z);
                back = back.LengthSquared > 0.0001f ? back.Normalized() : Vector3.UnitZ;
                host.Scene.HostView = new HostView(s.Position - back * 3 + Vector3.UnitY * 1.5f,
                    s.Position + Vector3.UnitY * 0.5f, Vector3.UnitY, FovY: host.Frame / 300 % 2 == 0 ? 0 : 60);
            }
        }

        private static void EndView(View view, CampaignHost? host)
        {
            Scene.HostFramingAspect = 0;
            PlayerEntity.HostShowMainBiped = false;
            if (host != null)
            {
                host.Scene.HostCameraOffset = Vector3.Zero;
                host.Scene.HostView = null;
                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"dispose: {ex.Message}");
                }
            }
        }

        private static int _traceFrom = -1, _traceTo = -1;
        private static int _dumpFrom = -1, _dumpTo = -1;
        private static List<string>? _dump;
        private static readonly System.Reflection.FieldInfo _rockL = typeof(PlayerEntity).GetField("_spireRockPosL",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        private static readonly System.Reflection.FieldInfo _rockR = typeof(PlayerEntity).GetField("_spireRockPosR",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // a probe run each replayed step (step, host): -echoesturret crateprobe
        public static Action<int, CampaignHost>? StepProbe { get; set; }

        private static void Collect(Run run, CampaignHost host, in ReplayStateParts parts)
        {
            int step = run.Parts.Count;
            StepProbe?.Invoke(step, host);
            if (step >= _traceFrom && step <= _traceTo)
            {
                PlayerEntity t = PlayerEntity.Players[0];
                PlayerNetState n = t.NetCapture();
                var cs = MphRead.Formats.CameraSequence.Current;
                Console.WriteLine($"  step {step}: in {run.File?.Steps[step].Input.AimDelta} hp {t.Health} alt {t.IsAltForm} "
                    + $"yaw {n.Yaw:0.00} pitch {n.Pitch:0.00} pos {n.Position} f1 {t.Flags1} "
                    + $"camseq {(cs == null ? "-" : $"{cs.SequenceId}{(cs.BlockInput ? " block" : "")}")} "
                    + $"state {GameState.MatchState} dialog {GameState.DialogPause}");
            }
            run.Parts.Add(parts);
            for (int i = 0; i < PlayerEntity.PlayerCount; i++)
            {
                PlayerEntity spire = PlayerEntity.Players[i];
                if (spire.Hunter != Hunter.Spire || !spire.Flags2.TestFlag(PlayerFlags2.AltAttack))
                {
                    continue;
                }
                run.SpireAttackSteps++;
                var l = (Vector3)_rockL.GetValue(spire)!;
                var r = (Vector3)_rockR.GetValue(spire)!;
                for (int j = 0; j < PlayerEntity.PlayerCount; j++)
                {
                    PlayerEntity t = PlayerEntity.Players[j];
                    if (j != i && t.LoadFlags.TestFlag(LoadFlags.Active)
                        && ((l - t.Position).Length < 1.5f || (r - t.Position).Length < 1.5f))
                    {
                        run.SpireRockNear++;
                        break;
                    }
                }
            }
            PlayerEntity p0 = PlayerEntity.Players[0];
            run.Live.Add(p0.LoadFlags.TestFlag(LoadFlags.Active) && p0.LoadFlags.TestFlag(LoadFlags.Spawned) && p0.Health > 0
                && !p0.IsAltForm);
            if (run.Types != null)
            {
                ulong[] byType = new ulong[ReplayState.TypeSlots];
                ReplayState.CaptureByType(host.Scene, byType);
                run.Types.Add(byType);
            }
        }

        private static void Finish(Run run, CampaignHost host)
        {
            run.Frames = host.Frame;
            run.Ended = host.Ended;
            for (int i = 0; i < PlayerEntity.PlayerCount; i++)
            {
                run.Kills += GameState.Kills[i];
                run.Deaths += GameState.Deaths[i];
            }
        }

        // the first step at which each part differs, and at the first gameplay-entity difference, which entity types
        private static IEnumerable<string> CompareRuns(Run reference, Run run)
        {
            int count = Math.Min(reference.Parts.Count, run.Parts.Count);
            var first = new Dictionary<string, int>();
            void Note(string part, int step)
            {
                first.TryAdd(part, step);
            }
            for (int i = 0; i < count; i++)
            {
                ReplayStateParts a = reference.Parts[i], b = run.Parts[i];
                if (a.Rng2 != b.Rng2) Note("rng2", i);
                if (a.Match != b.Match) Note("match", i);
                for (int s = 0; s < 4; s++)
                {
                    if (a.Seat(s) != b.Seat(s)) Note($"seat{s}", i);
                }
                if (a.Entities != b.Entities) Note("entities", i);
                if (a.Rng1 != b.Rng1) Note("rng1 (cosmetic)", i);
                if (a.Cosmetic != b.Cosmetic) Note("cosmetic entities", i);
            }
            if (reference.Parts.Count != run.Parts.Count)
            {
                yield return $"steps: recorded {reference.Parts.Count}, this run {run.Parts.Count}";
            }
            foreach ((string part, int step) in first.OrderBy(p => p.Value))
            {
                string extra = "";
                if (reference.Types != null && run.Types != null && step < run.Types.Count)
                {
                    IEnumerable<string> types = Enumerable.Range(0, ReplayState.TypeSlots)
                        .Where(t => reference.Types[step][t] != run.Types[step][t])
                        .Select(t => ((EntityType)t).ToString());
                    if (part == "entities" || part == "cosmetic entities")
                    {
                        extra = $" ({string.Join(", ", types)})";
                    }
                }
                yield return $"{part,-18} first differs at step {step}{extra}";
            }
        }

        // ---- a stand-in human for seat 0: deterministic from its seed, varied enough to exercise everything a
        //      player does (move, aim, fire, jump, morph + boost + bombs, alt attack, zoom, weapon cycling, the wheel) ----

        private sealed class ScriptedHuman
        {
            private uint _state;
            private int _moveLeft, _aimLeft, _actLeft;
            private System.Numerics.Vector2 _move, _aim, _wheel;
            private CampaignButtons _held;
            private BeamType _select = BeamType.None;

            public ScriptedHuman(int seed)
            {
                _state = (uint)seed * 2654435761u + 12345;
            }

            // 0 .. n-1 from a plain LCG (the test's own numbers, never the game's RNG)
            private int Roll(int n)
            {
                _state = _state * 1664525u + 1013904223u;
                return (int)(((_state >> 8) * (ulong)n) >> 24);
            }

            public CampaignInput Next(CampaignHost host)
            {
                if (--_moveLeft <= 0)
                {
                    _moveLeft = 15 + Roll(90);
                    int x = Roll(3) - 1;
                    int y = Roll(5) switch { 0 => -1, 1 => 0, _ => 1 }; // mostly forward
                    _move = new System.Numerics.Vector2(x, y);
                }
                if (--_aimLeft <= 0)
                {
                    _aimLeft = 8 + Roll(50);
                    _aim = new System.Numerics.Vector2((Roll(17) - 8) * 0.75f, (Roll(9) - 4) * 0.25f);
                }
                CampaignButtons press = CampaignButtons.None;
                if (--_actLeft <= 0)
                {
                    _actLeft = 6 + Roll(45);
                    _select = BeamType.None;
                    _wheel = default;
                    _held = Roll(20) switch
                    {
                        < 8 => CampaignButtons.Shoot,
                        < 10 => CampaignButtons.Shoot | CampaignButtons.Jump,
                        < 12 => CampaignButtons.Jump,
                        12 => CampaignButtons.Morph,
                        13 => CampaignButtons.Boost | CampaignButtons.AltAttack,
                        14 => CampaignButtons.AltAttack,
                        15 => CampaignButtons.Zoom | CampaignButtons.Shoot,
                        16 => CampaignButtons.None,
                        17 => CampaignButtons.WeaponMenu,
                        _ => CampaignButtons.None
                    };
                    if (_held == CampaignButtons.Morph)
                    {
                        // a press toggles the form: one frame, not held
                        press = CampaignButtons.Morph;
                        _held = CampaignButtons.None;
                    }
                    else if (_held == CampaignButtons.WeaponMenu)
                    {
                        float a = Roll(6) * MathF.PI / 3; // a wheel sector
                        _wheel = new System.Numerics.Vector2(MathF.Sin(a), MathF.Cos(a));
                    }
                    else if (_held == CampaignButtons.None && press == CampaignButtons.None)
                    {
                        switch (Roll(4))
                        {
                        case 0: press = CampaignButtons.NextWeapon; break;
                        case 1: press = CampaignButtons.PrevWeapon; break;
                        case 2: _select = BeamType.Missile; break;
                        default: _select = BeamType.PowerBeam; break;
                        }
                    }
                }
                return new CampaignInput
                {
                    Move = _move,
                    AimDelta = _aim,
                    Buttons = _held | press,
                    SelectWeapon = _select,
                    WeaponWheel = _wheel
                };
            }
        }

        // ---- plumbing ----

        private static bool Setup()
        {
            // MphRead's Debug.Asserts would end this Debug process; the app is a Release build: carry on like it does
            Trace.Listeners.Clear();
            Trace.Listeners.Add(new IgnoreAsserts());
            return ToolPaths.UseDesktopExtraction();
        }

        private sealed class IgnoreAsserts : TraceListener
        {
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void Fail(string? message, string? detailMessage) { }
        }

        // this tool again in a new process (a replay with nothing left over from earlier matches)
        private static (int Code, string Output) RunChild(params string[] args)
        {
            string exe = Environment.ProcessPath ?? "dotnet";
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(typeof(ReplayTest).Assembly.Location);
            }
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using Process p = Process.Start(psi)!;
            System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output + err.Result);
        }
    }
}
