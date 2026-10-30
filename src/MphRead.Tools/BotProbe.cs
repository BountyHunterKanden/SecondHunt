using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Bots batch 10-23 (thread A): headless bot-behaviour measurements per arena, the repros for the owner's reports.
    //
    //   -botprobe [arena=<name>|all|echoes] [mode=Battle] [bots=3] [level=2] [time=120] [char=trooper|darksamus]
    //             [dropoff=on|off] [steady=on|off] [switch=on|off] [verbose]   (gates default to a live match's: on, steady off)
//             [hunters=samus,kanden,spire] [csv=<path with {arena} {level}>]
//   -botprobe replay=<a.csv,b.csv> arena=<name>   (scores CSVs from csv= or BizHawk vlog.lua the same way; see Replay)
    //
    // Per bot seat, while alive in biped form:
    //   snaps    aim reversals: a yaw step of 8+ degrees in one frame, then 8+ the other way within 10 frames (A2 jitter)
    //   hops     jumps taken while the bot moved under 1.5 units in the 2 s before (A2 jumping in place)
    //   holds    stretches of 4+ s alive in which it moved under 2 units with its next node 2+ away (stuck)
    //   walls    shots fired with room/entity collision between its muzzle and the target's middle (A3); shots = all
    //   pitch    mean and 90th-percentile look pitch while it has no target or can't see it (A6; up = positive)
    // The local seat (seat 0) wanders and fires now and then so the bots have a moving target.
    internal static partial class BotProbe
    {
        private sealed class Seat
        {
            public float LastYaw = Single.NaN;
            public readonly Queue<(long Frame, float Delta)> Steps = new();
            public int Snaps;
            public int Hops;
            public int Holds;
            public int Shots;
            public int Walls;
            public readonly List<float> RoamPitch = new();
            public readonly List<float> AimSeen = new(), AimHidden = new(); // degrees off the target at each real shot (target seen / not seen)
            public readonly Queue<Vector3> Trail = new(); // last 120 frames' positions
            public Vector3 HoldStart;
            public long HoldFrom = -1;
            public bool WasJump;
            public PlayerEntity? LastTarget;
            public int LastShotTimer = -1;
            public long TargetChanged = -100;
            public long Frames;
            // A4 (Echoes arenas), any form: jump pads taken, air (double) jumps, steps under water, Gravity Boosts,
            // pickups (health / ammo up while alive, or a new weapon)
            public int Pads, AirJumps, WetFrames, Boosts, Pickups, Deaths;
            public bool WasDead;
            public bool WasPad, WasAirJump, WasBoost;
            public int LastHealth = -1, LastAmmo0, LastAmmo1, LastWeapons;
        }

        // A4: nav nodes some bot came within 1.5 units of, this arena
        private static readonly HashSet<NodeData3> Visited = new();

        // A5: the online host lobby's bot card / ADD BOT picks (OnlineMenus.NextBot, private: by reflection) with the
        // desktop HD folder as this device's: TROOPER + DARK SAMUS only with EXTRA PICKUPS + EXTRA CHARACTERS on; LEVEL and
        // a new host's remake keep the character
        private static void PickCheck()
        {
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
                if (!ok) fails++;
            }
            string? hd = null;
            for (string? d = AppContext.BaseDirectory; d != null && hd == null; d = System.IO.Path.GetDirectoryName(d))
            {
                string c = System.IO.Path.Combine(d, "brawl_extract", "Converted");
                if (System.IO.Directory.Exists(c)) hd = c;
            }
            hd ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MPH Recomp", "brawl_extract", "Converted");
            MphRecomp.Frontend.MultiplayerMenu.CharacterHdRoot = hd;
            var next = typeof(MphRecomp.Frontend.RecompMenus).GetMethod("NextBot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            List<MatchPlayer> Cycle(MatchSettings s)
            {
                var seen = new List<MatchPlayer>();
                MatchPlayer? from = null;
                for (int i = 0; i < 12; i++)
                {
                    from = (MatchPlayer)next.Invoke(null, new object?[] { s, from, 1 })!;
                    seen.Add(from);
                }
                return seen;
            }
            string Show(MatchPlayer p) => $"{p.Hunter}/{p.Character ?? "-"}/{p.Skin ?? "-"}/{p.Nickname}";
            var on = new MatchSettings();
            on.Players.Add(new MatchPlayer { Hunter = Hunter.Samus });
            List<MatchPlayer> picks = Cycle(on);
            Console.WriteLine("extras on:  " + String.Join(", ", picks.Take(9).Select(Show)));
            MatchPlayer? trooper = picks.FirstOrDefault(p => p.Character == "trooper");
            MatchPlayer? dark = picks.FirstOrDefault(p => p.Character == "darksamus");
            Check(trooper is { Hunter: Hunter.Weavel, Nickname: "TROOPER", Skin: not null }, "rules on: a tap reaches TROOPER (Weavel, a skin, named TROOPER)");
            Check(dark is { Hunter: Hunter.Samus, Nickname: "DARK SAMUS", Skin: not null }, "rules on: a tap reaches DARK SAMUS");
            Check(picks.Take(8).Select(p => p.Character ?? p.Hunter.ToString()).Distinct().Count() == 8 && picks[8].Hunter == Hunter.Kanden,
                "rules on: eight picks (the host plays Samus), then round again");
            var off = new MatchSettings { ExtraCharacters = false };
            off.Players.Add(new MatchPlayer { Hunter = Hunter.Samus });
            Check(Cycle(off).All(p => p.Character == null), "EXTRA CHARACTERS off: no TROOPER / DARK SAMUS");
            var noPickups = new MatchSettings { PirateBlaster = false };
            noPickups.Players.Add(new MatchPlayer { Hunter = Hunter.Samus });
            Check(Cycle(noPickups).All(p => p.Character == null), "EXTRA PICKUPS off: no TROOPER / DARK SAMUS");
            // a human Weavel doesn't take the TROOPER pick, a TROOPER bot doesn't take Weavel's
            var taken = new MatchSettings();
            taken.Players.Add(new MatchPlayer { Hunter = Hunter.Samus });
            taken.Players.Add(MphRecomp.Frontend.MultiplayerMenu.OnlineBot(Characters.Trooper, 1));
            MatchPlayer add = (MatchPlayer)next.Invoke(null, new object?[] { taken, MphRecomp.Frontend.MultiplayerMenu.OnlineBot(Hunter.Weavel, 1), 1 })!;
            Check(add.Character != "trooper", $"a tap after Weavel skips the TROOPER already on a seat ({Show(add)})");
            if (trooper != null)
            {
                MatchPlayer like = MphRecomp.Frontend.MultiplayerMenu.OnlineBotLike(trooper, 2);
                Check(like.Character == "trooper" && like.Skin == trooper.Skin && like.BotLevel == 2 && like.Nickname == "TROOPER", "LEVEL keeps the TROOPER");
                var match = new MatchSettings();
                match.Players.Add(new MatchPlayer { Hunter = Hunter.Samus });
                match.Players.Add(like);
                Characters.ApplyExtraRules(match);
                Check(match.Players[1].Character == "trooper", "the match keeps a TROOPER bot (rules on)");
                match.ExtraCharacters = false;
                Characters.ApplyExtraRules(match);
                Check(match.Players[1].Character == null, "rules off at START: the bot plays Weavel");
            }
            Console.WriteLine(fails == 0 ? "picks: all PASS" : $"picks: {fails} FAIL");
            if (fails > 0) Environment.ExitCode = 1;
        }

        // aitree=<ids>: the Battle personality (offset 32896) nodes whose Func24Id is listed, each with its parent and
        // children, in AiPersonality.PrintNode's format (Switch lines = the node's exits: func3, weight, target)
        private static void AiTree(string ids)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var tokens = ids.Split(',').ToHashSet(); // Func24 ids or node labels (R, BY, ...)
            var load = typeof(AiPersonality).GetMethod("LoadData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var root = (AiPersonalityData1)load.Invoke(null, [32896])!;
            var queue = new Queue<AiPersonalityData1>([root]);
            while (queue.Count > 0)
            {
                AiPersonalityData1 node = queue.Dequeue();
                foreach (AiPersonalityData1 child in node.Data1)
                {
                    queue.Enqueue(child);
                }
                if (!tokens.Contains(node.Func24Id.ToString()) && !tokens.Contains(node.Label))
                {
                    continue;
                }
                var sb = new System.Text.StringBuilder();
                string path = node.Label;
                for (AiPersonalityData1? p = node.Parent; p != null; p = p.Parent)
                {
                    path = p.Label + "(" + p.Func24Id + ")/" + path;
                }
                sb.AppendLine("==== " + path + " id " + node.Func24Id);
                AiPersonalityData1.PrintNode(node, sb);
                sb.AppendLine("  children: " + String.Join(", ", node.Data1.Select(c => c.Label + "(" + c.Func24Id + ")")));
                if (node.Parent != null)
                {
                    sb.AppendLine("  siblings: " + String.Join(", ", node.Parent.Data1.Select(c => c.Label + "(" + c.Func24Id + ")")));
                }
                Console.Write(sb.ToString());
            }
        }

        public static void Run(string[] args)
        {
            string arenaArg = "all";
            string mode = "Battle";
            int bots = 3, level = 2;
            float time = 120;
            int? goal = null;
            string? character = null, humanChar = null;
            bool? dropOff = null, steady = null, switchFirst = null, sight = null, roam = null, cling = null, crates = null, vparity = null, stuckfix = null, switchorder = null, nodeflat = null, stucksight = null, breakwalls = null, stuckvertical = null, edgejump = null, padjump = null, liftguard = null, uplifts = null, arrivetick = null, edgehold = null, release = null, extras = null;
            bool verbose = false, links = false, linkForce = false, linkSolve = false, padSolve = false;
            Hunter human = Hunter.Samus;
            string? csvPath = null, replay = null, onlyArena = null;
            string[]? hunters = null;
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaArg = value; break;
                case "only": onlyArena = value; break; // with arena=vanilla/echoes/all: just the arenas whose in-game name has this text (comma list)
                case "mode": mode = value; break;
                case "bots": bots = Int32.Parse(value); break;
                case "level": level = Int32.Parse(value); break;
                case "time": time = Single.Parse(value); break;
                case "goal": goal = Int32.Parse(value); break; // point goal (vanilla probe lobbies ran the full time: goal=99)
                case "char": character = value.ToLowerInvariant(); break;
                case "dropoff": dropOff = value == "on"; break;
                case "steady": steady = value == "on"; break;
                case "switch": switchFirst = value == "on"; break;
                case "sight": sight = value == "on"; break;
                case "roam": roam = value == "on"; break;
                case "cling": cling = value == "on"; break;
                case "crates": crates = value == "on"; break;
                case "vparity": vparity = value == "on"; break;
                case "stuckfix": stuckfix = value == "on"; break;
                case "switchorder": switchorder = value == "on"; break;
                case "switchlatch": PlayerEntity.HostBotSwitchLatch = value == "on"; break; // prototype (PlayerAi.cs UpdatePathWeights)
                case "nodeflat": nodeflat = value == "on"; break;
                case "stucksight": stucksight = value == "on"; break;
                case "breakwalls": breakwalls = value == "on"; break;
                case "stuckvertical": stuckvertical = value == "on"; break;
                case "edgejump": edgejump = value == "on"; break;
                case "release": release = value == "on"; break; // a bot lets go of stale controls (PlayerAi.cs)
                case "padjump": padjump = value == "on"; break; // the edge jump's pad-launch jump (PlayerAiEdgeJump.cs)
                case "arrivetick": arrivetick = value == "on"; break; // node arrival per 30 Hz tick (PlayerAiEdgeJump.cs)
                case "edgehold": edgehold = value == "on"; break; // an edge jump's flight keeps its node (PlayerAiEdgeJump.cs)
                case "uplifts": uplifts = value == "on"; break; // only gravity pushing up is a lift (PlayerAiEdgeJump.cs IsLift)
                case "liftguard": liftguard = value == "on"; break; // no climb into a Gravity lift off the route (PlayerAiEdgeJump.cs)
                case "humanmove": HumanMove = value; break;
                case "humanchar": humanChar = value.ToLowerInvariant(); break;
                case "wallsdown": WallsDown = value == "on"; break; // control: the arena's breakable walls kept broken all match (an open arena)
                case "extras": extras = value == "on"; break; // off = no EXTRA PICKUPS (Pirate Blaster + placed pickups), as vanilla
                case "verbose": verbose = true; break;
                case "linkdebug": LinkDebugSet = value.Split(',').Select(Int32.Parse).Where(v => v > 0).ToHashSet(); LinkDebug = Int32.Parse(value.Split(',')[0]); break; // links: trace this link's run (its number, from 1)
                case "linkall": LinkAll = value == "on"; break;
                case "linkdied": LinkDied = value == "on"; break;
                case "linkshow": LinkShow = value.Split(',').Select(v => (Int32.Parse(v.Split('-')[0]), Int32.Parse(v.Split('-')[1]))).ToHashSet(); break; // links: always list these links (from-to node ids)
                case "fullhp": LinkFullHp = value == "on"; break;
                case "morph": LinkMorph = value == "on"; break;
                case "clockreset": LinkClockReset = value == "on"; break;
                case "runin": LinkRunIn = value == "on"; break; // runin=off: every link is a standing (cold) start
                case "links": links = true; break; // link log: every route link bots take in a bots-only match, and how each run ended
                case "linkforce": linkForce = true; break; // a bot put on every route link, one at a time, its node look-ups locked to the link's end (bots=1; hunters=X picks it)
                case "linksolve": linkSolve = true; break; // bot move solver P1 (BotProbeLinkSolve.cs): the cheapest move that gets along every route link (bots=1; hunter=X is the player driven)
                case "padsolve": padSolve = true; break; // pad + jump survey (BotProbePadSolve.cs): higher floors a jump during a pad flight reaches, off the route graph (bots=1; hunter=X)
                case "padfine": PadFine = Int32.Parse(value); break; // padsolve: only pad N, steer every 10deg and jump every 2 frames
                case "linklist": SolveList = value; break; // linksolve: only these links (a links_on.tsv: arena, link#, .., verdict "failing")
                case "fhroot": MphRecomp.Arenas.EchoesArena.ImportRoot = value; break; // First Hunt arenas from an -fharena convert root (arena=fh)
                case "hunter": human = Enum.Parse<Hunter>(value, ignoreCase: true); break; // the human seat's hunter
                case "csv": csvPath = value; break; // per-frame dump in routes/vlog.lua's columns ({arena} {level} substituted)
                case "hunters": hunters = value.Split(','); break; // bot seats' hunters in order (vanilla matches: samus,kanden,spire)
                case "replay": replay = value; break;
                case "seed": Seed = UInt32.Parse(value); break; // varies the match RNG (0 = as before) // analyze CSV(s) (comma list; ours or vanilla's) on arena=<name>, no match played
                case "navscc": NavScc(value); return; // P3 measure: ArenaNav nodes kept for a First Hunt arena, spawn anchors vs every floor sample (fhroot= first)
                case "aitree": AiTree(value); return; // prints the Battle AI tree's nodes with these Func24 ids (comma list) + parent/children
                case "picks": PickCheck(); return; // A5: the online host lobby's bot picks (no server)
                case "demolog": DemoLog = true; break;
                case "selftrace": SelfLog = true; SelfTraceSeat = Int32.Parse(value.Split('@')[0]); SelfTraceFrom = Int64.Parse(value.Split('@')[1].Split('-')[0]); SelfTraceTo = Int64.Parse(value.Split('-')[1]); break; // seat@from-to: her moves every 4 frames
                case "selflog": SelfLog = true; break; // 10-28: one line per self-KO (where, the drop, what hit it)
                case "giveups": PlayerEntity.HostUnstickGiveUp = (p, n) => Console.WriteLine( // the stuck fix's give-ups: where, which node
                    $"giveup {p.Hunter} at {p.Position.X:0.0} {p.Position.Y:0.0} {p.Position.Z:0.0} node {n.Id} {n.Position.X:0.0} {n.Position.Y:0.0} {n.Position.Z:0.0}"); break;
                case "demo": DemoKind = Int32.Parse(value.Split('@')[0]); DemoTimes = new Queue<long>(value.Split('@')[1].Split(',').Select(Int64.Parse)); break; // demo=1@600[,1500..]: PlayerAiDemo.cs at those frames, then the bot's escape time
                case "matchreplay": MatchReplayTrace(value, args); return; // matchreplay=<file.mphreplay> seat=N steps=a-b: a bot's AI state each step of a recorded match
                case "trace": TraceSeat = Int32.Parse(value.Split('@')[0]); TraceFrom = Int64.Parse(value.Split('@')[1]); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // (Release-like: asserts don't end the run)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (replay != null)
            {
                Replay(replay.Split(','), arenaArg, level);
                return;
            }
            GameMode gameMode = Enum.Parse<GameMode>(mode.Replace(" ", ""), ignoreCase: true);
            IEnumerable<RoomMetadata> arenas = arenaArg switch
            {
                "all" => MatchArenas.All,
                "echoes" => MatchArenas.All.Where(a => MphRecomp.Arenas.EchoesArena.IsEchoesRoom(a.Name)),
                "vanilla" => MatchArenas.All.Where(a => !MphRecomp.Arenas.EchoesArena.IsEchoesRoom(a.Name)
                    && !MphRecomp.Arenas.FirstHuntArena.IsFirstHuntRoom(a.Name)),
                "fh" => MatchArenas.All.Where(a => MphRecomp.Arenas.FirstHuntArena.IsFirstHuntRoom(a.Name)),
                _ => new[] { MatchArenas.Find(arenaArg) ?? throw new ArgumentException($"no arena {arenaArg}") },
            };
            if (onlyArena != null)
            {
                string[] only = onlyArena.Split(',');
                arenas = arenas.Where(a => only.Any(o => a.InGameName.Contains(o, StringComparison.OrdinalIgnoreCase)));
            }
            var totals = new int[6];
            int arenasRun = 0;
            Console.WriteLine("arena                 | bot             snaps/min hops holds shots walls  roam pitch mean/p90");
            var echoesLines = new List<string>();
            foreach (RoomMetadata arena in arenas)
            {
                // only what the game's own arena table offers: the census alone passes e.g. the expanded Transfer Lock's
                // Battle layer (its menu table: Bounty/Capture/Defender; bots fall out into space every few seconds)
                if (!MatchArenas.Supports(arena, gameMode, bots + 1)
                    || MphRecomp.Frontend.MultiplayerMenu.VanillaOffers(arena.Id, gameMode) == false)
                {
                    continue;
                }
                MatchSettings settings = MatchSettings.Quick(gameMode, arena.Name, human, bots, level);
                settings.TimeLimitSeconds = time;
                settings.PointGoal = goal ?? settings.PointGoal;
                settings.BotDropOff = dropOff;
                settings.BotSteadyAim = steady;
                settings.BotSwitchFirst = switchFirst;
                settings.BotFireSight = sight;
                settings.BotRoamLook = roam;
                settings.BotWallCling = cling;
                settings.BotCrates = crates;
                settings.BotVanillaParity = vparity;
                settings.BotStuckFix = stuckfix;
                settings.BotSwitchOrder = switchorder;
                settings.BotNodeFlat = nodeflat;
                settings.BotStuckSight = stucksight;
                settings.BotBreakWalls = breakwalls;
                settings.BotStuckVertical = stuckvertical;
                settings.BotEdgeJump = edgejump;
                settings.BotEdgePadJump = padjump;
                settings.BotLiftGuard = liftguard;
                settings.BotUpLifts = uplifts;
                settings.BotArriveTick = arrivetick;
                settings.BotEdgeHold = edgehold;
                settings.BotReleaseControls = release;
                if (extras != null) { settings.PirateBlaster = extras; settings.PlacedPickups = extras; settings.CrateBlaster = extras; }
                for (int h = 0; hunters != null && h < hunters.Length && h + 1 < settings.Players.Count; h++)
                {
                    settings.Players[h + 1].Hunter = Enum.Parse<Hunter>(hunters[h], ignoreCase: true);
                }
                if (character == "trooper" || character == "darksamus")
                {
                    MatchPlayer seat = settings.Players[1];
                    seat.Hunter = character == "trooper" ? Hunter.Weavel : Hunter.Samus;
                    seat.Character = character;
                }
                if (humanChar == "darksamus")
                {
                    settings.Players[0].Hunter = Hunter.Samus;
                    settings.Players[0].Character = humanChar;
                }
                if (padSolve)
                {
                    settings.TimeLimitSeconds = 99999;
                    settings.PointGoal = 999;
                    PadSolve(settings, arena, echoesLines);
                    arenasRun++;
                    continue;
                }
                if (linkSolve)
                {
                    settings.TimeLimitSeconds = 99999;
                    settings.PointGoal = 999;
                    LinkSolve(settings, arena, echoesLines);
                    arenasRun++;
                    continue;
                }
                if (linkForce)
                {
                    settings.TimeLimitSeconds = 99999;
                    settings.PointGoal = 999;
                    LinkAudit(settings, arena, echoesLines);
                    arenasRun++;
                    continue;
                }
                if (links)
                {
                    settings.PointGoal = 999;
                    LinkLog(settings, arena, echoesLines);
                    arenasRun++;
                    continue;
                }
                Seat[] seats = Play(settings, verbose, out int navNodes, out int crateBreaks,
                    csvPath?.Replace("{arena}", arena.Name).Replace("{level}", level.ToString()));
                arenasRun++;
                // the game's own score counters (GameState), not a guess from the dump: per seat kills/deaths/points
                Console.WriteLine("score: " + String.Join(" | ", Enumerable.Range(0, seats.Length).Select(i =>
                    $"s{i} {(i == 0 ? "human" : settings.Players[i].Character ?? settings.Players[i].Hunter.ToString())} "
                    + $"kills {GameState.Kills[i]} deaths {GameState.Deaths[i]} self {GameState.Suicides[i]} points {GameState.Points[i]}")));
                Console.WriteLine("hurt: " + String.Join(" ", HumanHurt.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
                // nav coverage for every arena (vanilla ones are the yardstick for the Echoes ones)
                echoesLines.Add($"{arena.InGameName,-21} | nav {Visited.Count}/{navNodes} nodes reached ({(navNodes == 0 ? 0 : 100f * Visited.Count / navNodes):0}%), crates/grates broken {crateBreaks}");
                int[] uc = PlayerEntity.HostUnstickCounts;
                echoesLines.Add($"{"",-21} | unstick: unmorphs {uc[0]}, climbs {uc[1]}, give-ups {uc[2]}, drifts {uc[3]}, link jumps {uc[4]}, items skipped {uc[5]}");
                Array.Clear(uc);
                int[] ec = PlayerEntity.HostEdgeCounts;
                echoesLines.Add($"{"",-21} | edge jump: jumps {ec[0]}, step-asides {ec[1]}, run-ups {ec[2]}, nodes marked {ec[3]}, pad jumps {ec[4]}, lift guard {PlayerEntity.HostLiftGuardCount}, lift escapes {PlayerEntity.HostLiftEscapeCount}, edge holds {PlayerEntity.HostEdgeHoldCount}");
                PlayerEntity.HostLiftGuardCount = 0;
                PlayerEntity.HostLiftEscapeCount = 0;
                PlayerEntity.HostEdgeHoldCount = 0;
                Array.Clear(ec);
                if (MphRecomp.Arenas.EchoesArena.IsEchoesRoom(arena.Name))
                {
                    for (int i = 1; i < seats.Length; i++)
                    {
                        Seat s = seats[i];
                        echoesLines.Add($"{"",-21} |   {settings.Players[i].Character ?? settings.Players[i].Hunter.ToString(),-10} pads {s.Pads,3}  air jumps {s.AirJumps,3}  "
                            + $"under water {s.WetFrames / 60f,5:0.0} s  boosts {s.Boosts,3}  pickups {s.Pickups,3}  holds {s.Holds,2}");
                    }
                }
                for (int i = 1; i < seats.Length; i++)
                {
                    Seat s = seats[i];
                    MatchPlayer mp = settings.Players[i];
                    float minutes = Math.Max(1, s.Frames) / 3600f;
                    string pitch = s.RoamPitch.Count == 0 ? "-" : $"{s.RoamPitch.Average(),5:0.0}/{Percentile(s.RoamPitch, 0.9f),5:0.0}";
                    string who = mp.Character ?? mp.Hunter.ToString();
                    Console.WriteLine($"{arena.InGameName,-21} | {who,-15} {s.Snaps / minutes,9:0.0} {s.Hops,4} {s.Holds,5} "
                        + $"{s.Shots,5} {s.Walls,5}  {pitch}");
                    totals[0] += s.Snaps;
                    totals[1] += s.Hops;
                    totals[2] += s.Holds;
                    totals[3] += s.Shots;
                    totals[4] += s.Walls;
                    totals[5] += s.Deaths;
                    AimAll.Add(s.AimSeen, s.AimHidden);
                }
            }
            Console.WriteLine($"TOTAL {arenasRun} arenas: snaps {totals[0]} hops {totals[1]} holds {totals[2]} "
                + $"shots {totals[3]} through-wall {totals[4]} ({(totals[3] == 0 ? 0 : 100f * totals[4] / totals[3]):0.0}%) deaths {totals[5]}");
            Console.WriteLine($"aim error at real shots (deg off the target): seen n {AimAll.Count(0)} median {AimAll.Pct(0, 0.5f):0.0} p90 {AimAll.Pct(0, 0.9f):0.0} over30 {AimAll.Over(0, 30):0}% | hidden n {AimAll.Count(1)} median {AimAll.Pct(1, 0.5f):0.0} p90 {AimAll.Pct(1, 0.9f):0.0} over30 {AimAll.Over(1, 30):0}%");
            Console.WriteLine("causes: " + String.Join(" ", WhyCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
            int[] cc = PlayerEntity.PlayerAiData.HostCrateCounts, wc = PlayerEntity.PlayerAiData.HostWallClingCounts;
            Console.WriteLine($"bot crates: picked {cc[0]} shots {cc[1]} seen break {cc[2]} drops reached {cc[3]}, given up: time {cc[4]} out of sight {cc[5]} fight {cc[6]}; drops too far {cc[7]} no floor {cc[8]} timed out {cc[9]} | "
                + $"bot wall cling: perch jumps {wc[0]} clings {wc[1]} wall jumps {wc[2]}");
            int[] bw = PlayerEntity.PlayerAiData.HostWallCounts;
            Console.WriteLine($"bot breakable walls: picked {bw[0]} shots {bw[1]} seen break {bw[2]}, given up: time {bw[3]} way changed {bw[4]} fight/other {bw[5]}");
            foreach (string line in echoesLines)
            {
                Console.WriteLine(line);
            }
        }

        private static class AimAll
        {
            private static readonly List<float>[] Lists = { new(), new() };
            public static void Add(List<float> seen, List<float> hidden) { Lists[0].AddRange(seen); Lists[1].AddRange(hidden); }
            public static int Count(int i) => Lists[i].Count;
            public static float Pct(int i, float p) => Lists[i].Count == 0 ? 0 : Percentile(Lists[i], p);
            public static float Over(int i, float deg) => Lists[i].Count == 0 ? 0 : 100f * Lists[i].Count(v => v > deg) / Lists[i].Count;
        }

        private static readonly Dictionary<string, int> WhyCounts = new();
        // matchreplay=<file> seat=N steps=a-b: replays a recorded match (ReplayTest -replayplay) and prints that seat's AI
        // state every 10 steps in the range (where, alt form, tree path, the stuck fix's clock, its node and target)
        private static void MatchReplayTrace(string file, string[] args)
        {
            if (args.Contains("selflog=on")) // 10-28: each self-KO in a match replay (the owner's Odin edge-jump test)
            {
                ReplayTest.StepProbe = (step, host) => LogSelfKos(host, PlayerEntity.PlayerCount);
                ReplayTest.PlayFile(new[] { "-replayplay", file });
                return;
            }
            int seat = Int32.Parse(args.First(a => a.StartsWith("seat="))[5..]);
            string[] range = args.First(a => a.StartsWith("steps="))[6..].Split('-');
            int from = Int32.Parse(range[0]), to = Int32.Parse(range[1]);
            // stucksight=on|off: forces that switch over what the file recorded (the match plays on from where it changes)
            string? force = args.FirstOrDefault(a => a.StartsWith("stucksight="));
            ReplayTest.StepProbe = (step, host) =>
            {
                if (force != null) PlayerEntity.HostBotStuckSight = force.EndsWith("=on");
                if (step < from || step > to || step % 10 != 0) return;
                PlayerEntity p = PlayerEntity.Players[seat];
                Console.WriteLine($"step {step} {p.Hunter} hp {p.Health} alt {p.IsAltForm} pos {Fmt(p.Position)} spd {Fmt(p.Speed)} "
                    + $"stuckfix {PlayerEntity.HostBotStuckFix} sight {PlayerEntity.HostBotStuckSight} parity {PlayerEntity.HostBotVanillaParity} tree {p.AiData.HostDebugState()} "
                    + $"travel:clock {p.AiData.HostUnstickState} seesfoe {p.AiData.HostUnstickSeesFoe} {p.AiData.DemoState()} "
                    + string.Join(" ", Enumerable.Range(0, PlayerEntity.PlayerCount).Select(i => $"[{i} {PlayerEntity.Players[i].Hunter} {(PlayerEntity.Players[i].IsBot ? "bot" : "human")} {Fmt(PlayerEntity.Players[i].Position)}]")));
            };
            ReplayTest.PlayFile(new[] { "-replayplay", file }.Concat(args.Where(a => a.StartsWith("dump="))).ToArray()); // (dump= plays on past a difference)
        }

        private static int TraceSeat = -1;
        private static long TraceFrom, TraceLength = 120;

        private static float Percentile(List<float> values, float p)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[Math.Clamp((int)(p * sorted.Count), 0, sorted.Count - 1)];
        }

        private static uint Seed;
        private static bool WallsDown;
        // humanmove=still: the human seat gives no input all match (default walk: forward + turning, firing); humanchar=darksamus:
        // the human seat is the DARK SAMUS character (Samus base, MatchStart's Dark Samus flags). Prints a "hurt:" line: the
        // human's health lost, by attacker seat and how (ball = attacker in alt form, i.e. bombs; else its weapon)
        private static string HumanMove = "walk";
        private static readonly Dictionary<string, int> HumanHurt = new();

        private static int Demo, DemoKind;
        private static bool DemoLog;
        private static bool SelfLog;
        private static readonly HashSet<object> _execDumped = new HashSet<object>(); // 10-28: each leaf group's exits are printed once per run
        private static long DemoAt;
        private static Queue<long> DemoTimes = new();
        private static PlayerEntity? DemoBot;
        // demo=: put the bot in place at DemoAt, then print where it is every half second until it's 1.5 away
        // (across the floor) or 15 s pass
        private static void DemoStep(CampaignHost host)
        {
            if (Demo == 0 && DemoTimes.Count > 0 && host.Frame >= DemoTimes.Peek())
            {
                Demo = DemoKind;
                DemoAt = DemoTimes.Dequeue();
            }
            if (Demo == 0 || host.Frame < DemoAt)
            {
                return;
            }
            if (host.Frame == DemoAt)
            {
                string result = PlayerEntity.HostBotDemo(host.Scene, Demo);
                Console.WriteLine($"demo {Demo} at frame {host.Frame}: {result}");
                DemoBot = PlayerEntity.Players.FirstOrDefault(p => p.IsBot && p.Health > 0);
                if (!result.Contains(" PUT "))
                {
                    Demo = 0;
                }
                return;
            }
            if (DemoBot == null)
            {
                return;
            }
            float away = (DemoBot.Position - PlayerEntity.DemoSpot).Xz.Length;
            long t = host.Frame - DemoAt;
            if (DemoLog && (t % 30 == 0 || t < 40 && t % 4 == 0))
            {
                Console.WriteLine($"  +{t / 60f,4:0.00} s pos {DemoBot.Position.X,6:0.00} {DemoBot.Position.Y,6:0.00} {DemoBot.Position.Z,6:0.00} away {away:0.00} {DemoBot.AiData.DemoState()}");
            }
            if (away > 1.5f || t >= 15 * 60)
            {
                Console.WriteLine($"demo {Demo}: left the spot after {t / 60f:0.00} s" + (away > 1.5f ? "" : " (never)"));
                DemoBot = null;
                Demo = 0;
            }
        }

        // linkforce: for every node and each of its links (the AI's own route table), the bot is put at the node and held to
        // the link's other end (PlayerAiLinkAudit.cs: every node look-up comes back as that end) with nobody to fight (the
        // human seat is taken out of play) until it gets there (the AI's own in-range check) or LinkSeconds pass. Not ok
        // by kind: died, fell (well below both ends), walled (pressed on a wall half its last 2 s), pinned (moved under 0.5
        // in its last 2 s), wander (still moving), untested (the AI left the held link over a tenth of the run). Reached
        // runs that spent 0.5 s+ pressed on a wall are listed as rubbed.
        private const int LinkSeconds = 12;
        private const int BackSeconds = 30; // linkforce: a failed run's way back to the link's end
        private static int LinkDebug;
        private static HashSet<int> LinkDebugSet = new HashSet<int>();
        private static bool LinkDied; // linkdied=on: links also lists every link a bot died on
        private static HashSet<(int, int)> LinkShow = new HashSet<(int, int)>();
        private static bool LinkAll; // linkall=on: with linkdebug, trace the listed links but still run every link (state as in the full audit)
        private static bool LinkFullHp = true; // fullhp=off: the step-2 baseline test (a graded run starts with whatever health the bot had); on (default): each graded run starts at full health
        private static bool LinkClockReset = true; // clockreset=off: the step-3 (a) test (the stuck fix's clock carried across a teleport); on (default): each Put starts the clock again
        private static bool LinkRunIn = true; // runin=off: no run-in, every link starts cold (step-3 (b): is a failure the link or how it was arrived at?)
        private static bool LinkMorph = true; // morph=off: the step-2 test (a start at an alt-form node left the bot as it was); on (default): morphed first
        private sealed class LinkStat
        {
            public int Tries, Reached, Dropped, Died, Pinned, PinnedFighting, GiveUps;
            public long ReachedFrames, MaxFrames, PinnedFrames, WallFrames, WallFightFrames, WallTravelFrames, WallSeeFrames;
            public int WallSpells;
        }

        private sealed class LinkRun
        {
            public NodeData3? From, To;
            public long Start;
            public Vector3 Anchor;
            public int Still;
            public bool Pinned, PinnedFighting;
            public int Wall;
        }

        // the link log: a bots-only match (the human seat taken out, so bots fight each other as in a vanilla bot lobby),
        // every route link each bot takes (its _node40 moving on), and how each run ended: reached (it moved on from the
        // node it was heading for), dropped (it changed course first: a new goal, a give-up), died. Pinned = 2 s without
        // moving 0.5 short of the node (split by whether it was after a player). Game counters only.
        private static void LinkLog(MatchSettings settings, RoomMetadata arena, List<string> lines)
        {
            CampaignHost host = CampaignHost.StartMatch(settings);
            host.Scene.HostDrawFreeSim = true;
            if (Seed != 0)
            {
                Rng.SetRng1(Rng.Rng1 ^ (Seed * 2654435761u));
                Rng.SetRng2(Rng.Rng2 ^ (Seed * 40503u));
            }
            var stats = new Dictionary<(int, int), LinkStat>();
            var reachedNodes = new HashSet<int>();
            int count = settings.Players.Count;
            var runs = new LinkRun?[count];
            var giveUps = new int[count];
            Action<PlayerEntity, NodeData3>? oldGiveUp = PlayerEntity.HostUnstickGiveUp;
            PlayerEntity.HostUnstickGiveUp = (p, n) => giveUps[p.SlotIndex]++;
            LinkStat Stat((int, int) key) => stats.TryGetValue(key, out LinkStat? v) ? v : stats[key] = new LinkStat();
            try
            {
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                long maxFrames = (long)((settings.TimeLimitSeconds ?? 900) * 60);
                PlayerEntity human = PlayerEntity.Players[0];
                var onHuman = new long[count];
                var fighting = new long[count];
                void Close(int i, LinkRun r, string how)
                {
                    if (r.From == null || r.To == null)
                    {
                        return;
                    }
                    LinkStat st = Stat((r.From.Id, r.To.Id));
                    long frames = host.Frame - r.Start;
                    st.Tries++;
                    st.MaxFrames = Math.Max(st.MaxFrames, frames);
                    if (how == "reached")
                    {
                        st.Reached++;
                        st.ReachedFrames += frames;
                        reachedNodes.Add(r.To.Id);
                    }
                    else if (how == "died")
                    {
                        st.Died++;
                    }
                    else
                    {
                        st.Dropped++;
                    }
                    if (r.Pinned)
                    {
                        st.Pinned++;
                    }
                    if (r.PinnedFighting)
                    {
                        st.PinnedFighting++;
                    }
                    st.GiveUps += giveUps[i];
                    giveUps[i] = 0;
                }
                while (!host.Ended && host.Frame < maxFrames)
                {
                    if (host.Frame >= 60)
                    {
                        human.LoadFlags &= ~LoadFlags.Active;
                    }
                    host.Step(input);
                    for (int i = 1; i < count; i++)
                    {
                        PlayerEntity bot = PlayerEntity.Players[i];
                        LinkRun? run = runs[i];
                        if (bot.AiData.HostTargetPlayer == human)
                        {
                            onHuman[i]++;
                        }
                        // linkdebug=-N: every N seconds, where each bot is and what it's doing
                        if (LinkDebug < 0 && host.Frame % (-LinkDebug * 60) == 0)
                        {
                            Console.WriteLine($"  t {host.Frame / 60,4} bot {i} {bot.Hunter,-7} hp {bot.Health,3} at {Fmt(bot.Position)} {bot.AiData.AuditState()} sees {bot.AiData.AuditSees}");
                        }
                        if (bot.AiData.AuditFighting)
                        {
                            fighting[i]++;
                        }
                        if (bot.Health == 0)
                        {
                            if (run != null)
                            {
                                Close(i, run, "died");
                                runs[i] = null;
                            }
                            continue;
                        }
                        NodeData3? to = bot.AiData.AuditTarget;
                        if (run == null || to != run.To)
                        {
                            if (run != null)
                            {
                                Close(i, run, bot.AiData.AuditPrev == run.To ? "reached" : "dropped");
                            }
                            // linkdebug=-N: bot 1's every change of course, for the first 30 s after 100 s
                            if (LinkDebug < 0 && i == 1 && host.Frame >= 6000 && host.Frame < 7800)
                            {
                                Console.WriteLine($"  f {host.Frame,6} bot 1 at {Fmt(bot.Position)} {(run == null ? "" : bot.AiData.AuditPrev == run.To ? "reached" : "dropped")} "
                                    + $"{run?.To?.Id} -> {to?.Id} {(to == null ? "" : Fmt(to.Position))} prev {bot.AiData.AuditPrev?.Id} {bot.AiData.AuditState()}");
                            }
                            runs[i] = new LinkRun { From = bot.AiData.AuditPrev, To = to, Start = host.Frame, Anchor = bot.Position };
                            continue;
                        }
                        // walking into a wall: held against one (the game's own wall-hit timer) without getting 0.5 anywhere
                        if (run.Still >= 30 && bot.AiData.AuditWall && to != null && run.From != null)
                        {
                            LinkStat ws = Stat((run.From.Id, to.Id));
                            ws.WallFrames++;
                            if (bot.AiData.AuditFighting)
                            {
                                ws.WallFightFrames++;
                            }
                            if (bot.AiData.AuditTravel)
                            {
                                ws.WallTravelFrames++;
                            }
                            if (bot.AiData.AuditSees)
                            {
                                ws.WallSeeFrames++;
                            }
                            if (++run.Wall == 120)
                            {
                                ws.WallSpells++;
                            }
                        }
                        if ((bot.Position - run.Anchor).Length > 0.5f)
                        {
                            run.Wall = 0;
                            run.Anchor = bot.Position;
                            run.Still = 0;
                        }
                        else if (++run.Still >= 120 && to != null && !bot.AiData.AuditInRange(to))
                        {
                            if (bot.AiData.AuditFighting)
                            {
                                run.PinnedFighting = true;
                            }
                            else
                            {
                                run.Pinned = true;
                                if (run.From != null)
                                {
                                    Stat((run.From.Id, to.Id)).PinnedFrames++;
                                }
                            }
                        }
                    }
                }
                PlayerEntity p1 = PlayerEntity.Players[1];
                IReadOnlyList<NodeData3> nodes = p1.AiData.AuditNodes;
                var graph = new HashSet<(int, int)>();
                foreach (NodeData3 n in nodes)
                {
                    foreach (NodeData3 m in p1.AiData.AuditLinks(n))
                    {
                        graph.Add((n.Id, m.Id));
                    }
                }
                var byId = new Dictionary<int, NodeData3>();
                foreach (NodeData3 n in nodes)
                {
                    byId.TryAdd(n.Id, n);
                }
                int used = stats.Keys.Count(graph.Contains);
                int tries = stats.Values.Sum(s => s.Tries), reached = stats.Values.Sum(s => s.Reached);
                int pinned = stats.Values.Sum(s => s.Pinned), pinnedF = stats.Values.Sum(s => s.PinnedFighting);
                int gaveUp = stats.Values.Sum(s => s.GiveUps);
                long pinnedFrames = stats.Values.Sum(s => s.PinnedFrames), wallFrames = stats.Values.Sum(s => s.WallFrames),
                    wallFight = stats.Values.Sum(s => s.WallFightFrames), wallTravel = stats.Values.Sum(s => s.WallTravelFrames),
                    wallSee = stats.Values.Sum(s => s.WallSeeFrames);
                int wallSpells = stats.Values.Sum(s => s.WallSpells);
                int offGraph = stats.Keys.Count(k => !graph.Contains(k));
                int first = lines.Count;
                lines.Add($"{arena.InGameName,-21} | links used {used}/{graph.Count} ({(graph.Count == 0 ? 0 : 100f * used / graph.Count):0}%), nodes reached {reachedNodes.Count}/{nodes.Count}; "
                    + $"runs {tries}: reached {(tries == 0 ? 0 : 100f * reached / tries):0.0}%, pinned {pinned} ({pinnedFrames / 60f:0} s), pinned fighting {pinnedF}, walled {wallFrames / 60f:0.0} s (target {wallFight / 60f:0.0} s, following nodes {wallTravel / 60f:0.0} s, foe in sight {wallSee / 60f:0.0} s; {wallSpells} spells 2 s+), give-ups {gaveUp}, off-graph pairs {offGraph}");
                string Pos(int id) => byId.TryGetValue(id, out NodeData3? n) ? $"{n.NodeType,-10} {Fmt(n.Position)}" : "?";
                for (int i = 1; i < count; i++)
                {
                    PlayerEntity bot = PlayerEntity.Players[i];
                    lines.Add($"   bot {i} {bot.Hunter,-8} kills {GameState.Kills[i]} deaths {GameState.Deaths[i]}, fighting {fighting[i] / 60f:0} s, on the taken-out seat {onHuman[i] / 60f:0} s");
                }
                foreach (KeyValuePair<(int, int), LinkStat> kv in stats.Where(k => k.Value.Pinned > 0 || k.Value.GiveUps > 0 || k.Value.WallSpells > 0)
                    .OrderByDescending(k => k.Value.PinnedFrames + k.Value.WallFrames).Take(15).Concat(stats.Where(k => LinkShow.Contains(k.Key) || LinkDied && k.Value.Died > 0)))
                {
                    LinkStat st = kv.Value;
                    lines.Add($"   {kv.Key.Item1,3} {Pos(kv.Key.Item1)} -> {kv.Key.Item2,3} {Pos(kv.Key.Item2)}  runs {st.Tries} reached {st.Reached} dropped {st.Dropped} died {st.Died} "
                        + $"pinned {st.Pinned} ({st.PinnedFrames / 60f:0.0} s) walled {st.WallFrames / 60f:0.0} s (nodes {st.WallTravelFrames / 60f:0.0}, sight {st.WallSeeFrames / 60f:0.0}; {st.WallSpells}) give-ups {st.GiveUps} longest {st.MaxFrames / 60f:0.0} s");
                }
                for (int l = first; l < lines.Count; l++)
                {
                    Console.WriteLine(lines[l]);
                }
            }
            finally
            {
                PlayerEntity.HostUnstickGiveUp = oldGiveUp;
                host.Dispose();
            }
        }

        private static void LinkAudit(MatchSettings settings, RoomMetadata arena, List<string> lines)
        {
            CampaignHost host = CampaignHost.StartMatch(settings);
            host.Scene.HostDrawFreeSim = true;
            PlayerEntity bot = PlayerEntity.Players[1];
            try
            {
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                PlayerEntity human = PlayerEntity.Players[0];
                for (int i = 0; i < 120; i++)
                {
                    host.Step(input);
                }
                if (Seed != 0)
                {
                    Rng.SetRng1(Rng.Rng1 ^ (Seed * 2654435761u)); // (seed 0 = as the step-2 audit ran; before 10-26 the seed was ignored here)
                    Rng.SetRng2(Rng.Rng2 ^ (Seed * 40503u));
                }
                human.LoadFlags &= ~LoadFlags.Active;
                IReadOnlyList<NodeData3> nodes = bot.AiData.AuditNodes;
                // the links into each node (a run starts as the bot arrives along one of them)
                var into = new Dictionary<NodeData3, List<NodeData3>>();
                foreach (NodeData3 node in nodes)
                {
                    foreach (NodeData3 next in bot.AiData.AuditLinks(node))
                    {
                        if (!into.TryGetValue(next, out List<NodeData3>? list))
                        {
                            into[next] = list = new List<NodeData3>();
                        }
                        list.Add(node);
                    }
                }
                void WaitAlive()
                {
                    bot.AiData.AuditHold(null);
                    for (int w = 0; w < 600 && bot.Health == 0; w++)
                    {
                        host.Step(input);
                    }
                }
                // the start of a run: at an alt-form node the bot is morphed first (morph=off: the step-2 test, which left it as it was)
                void Put(NodeData3 a, NodeData3 b)
                {
                    if (LinkMorph && bot.AuditNeedsMorph(a))
                    {
                        bot.AuditMorph();
                        for (int m = 0; m < 180 && (bot.AuditMorphing || !bot.IsAltForm); m++)
                        {
                            host.Step(input);
                        }
                    }
                    bot.AuditPut(a, b, LinkClockReset);
                }
                // held to a node until it gets there (true), dies or the time's up
                int leadDebug = -1; // (linkdebug: the run-in of a traced link is traced too, as "lead <link> r<frame>")
                bool RunTo(NodeData3 node, int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        human.LoadFlags &= ~LoadFlags.Active;
                        bot.AiData.AuditHold(node);
                        host.Step(input);
                        if (bot.Health == 0)
                        {
                            return false;
                        }
                        if (leadDebug >= 0)
                        {
                            Console.WriteLine($"  lead {leadDebug} ->{node.Id} r{i} pos {Fmt(bot.Position)} {bot.AiData.AuditState()} {bot.AiData.AuditMove()}");
                        }
                        if (bot.AiData.AuditInRange(node))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                int total = 0, ok = 0, cold = 0;
                long okFrames = 0, okWall = 0;
                var kinds = new Dictionary<string, int>();
                var fails = new List<string>();
                var rubbed = new List<(long, string)>();
                foreach (NodeData3 from in nodes)
                {
                    foreach (NodeData3 to in bot.AiData.AuditLinks(from))
                    {
                        if (LinkDebugSet.Count > 0 && !LinkAll && total >= LinkDebugSet.Max())
                        {
                            break;
                        }
                        total++;
                        if (LinkDebugSet.Count > 0 && !LinkAll && !LinkDebugSet.Contains(total))
                        {
                            continue; // (linkdebug: only the listed links run; the rest of the arena is skipped for speed)
                        }
                        // the run-in: from a node with a link into this one (on foot first, nearest first), held to this
                        // one until it gets there, so it starts the link as it would arrive (moving, in the air off a
                        // jump pad, in alt form); the run-in isn't graded. None gets there: a standing start (cold).
                        string lead = "cold";
                        leadDebug = LinkDebugSet.Contains(total) ? total : -1;
                        List<NodeData3> leads = into.GetValueOrDefault(from) ?? new List<NodeData3>();
                        foreach (NodeData3 p in leads.Where(_ => LinkRunIn).OrderBy(p => p.NodeType == NodeType.Aerial ? 1 : 0)
                            .ThenBy(p => (p.Position - from.Position).LengthSquared).Take(3))
                        {
                            WaitAlive();
                            Put(p, from);
                            if (RunTo(from, LinkSeconds * 60))
                            {
                                lead = p.Id.ToString();
                                break;
                            }
                        }
                        leadDebug = -1;
                        if (lead == "cold")
                        {
                            cold++;
                            WaitAlive();
                            Put(from, to);
                        }
                        if (LinkDebugSet.Contains(total))
                        {
                            Console.WriteLine($"  link {total} {from.Id}->{to.Id} lead {lead} at {Fmt(bot.Position)}");
                            // floor under the link: a ray straight down every unit along it (y of the lower end + 3 -> 20 below)
                            for (int s = 0; s <= 20; s++)
                            {
                                Vector3 p = Vector3.Lerp(from.Position, to.Position, s / 20f);
                                var hit = new CollisionResult();
                                bool got = CollisionDetection.CheckBetweenPoints(p.AddY(3), p.AddY(-20), TestFlags.None, host.Scene, ref hit);
                                Console.WriteLine($"  floor {s}/20 at ({p.X:0.0} {p.Z:0.0}) from y {p.Y + 3:0.0}: {(got ? $"y {hit.Position.Y:0.0} terrain {hit.Terrain}" : "none")}");
                            }
                            foreach (EntityBase e in host.Scene.Entities)
                            {
                                if (e.Type != EntityType.ItemSpawn && e.Type != EntityType.ItemInstance && e.Type != EntityType.PlayerSpawn && e.Type != EntityType.Player)
                                {
                                    Console.WriteLine($"  any {e.Type} {Fmt(e.Position)} {e.GetType().Name}" + (e is JumpPadEntity jp ? $" closestnode {jp.ClosestNode?.Id}" : "")
                                        + (e is AreaVolumeEntity av ? $" inside {av.Data.InsideMessage} {av.Data.InsideMsgParam1} exit {av.Data.ExitMessage} vol {av.Data.Volume.Type} active {av.Active} cyl pos {Fmt(av.Data.Volume.CylinderPosition.ToFloatVector())} vec {Fmt(av.Data.Volume.CylinderVector.ToFloatVector())} r {av.Data.Volume.CylinderRadius.FloatValue:0.0} box pos {Fmt(av.Data.Volume.BoxPosition.ToFloatVector())} v1 {Fmt(av.Data.Volume.BoxVector1.ToFloatVector())} v2 {Fmt(av.Data.Volume.BoxVector2.ToFloatVector())} v3 {Fmt(av.Data.Volume.BoxVector3.ToFloatVector())} dots {av.Data.Volume.BoxDot1.FloatValue:0.0},{av.Data.Volume.BoxDot2.FloatValue:0.0},{av.Data.Volume.BoxDot3.FloatValue:0.0} entpos {Fmt(av.Position)}" : ""));
                                }
                                if ((e.Position - to.Position).Length < 12 || (e.Position - from.Position).Length < 12)
                                {
                                    Console.WriteLine($"  near {e.Type} {Fmt(e.Position)}");
                                }
                            }
                        }
                        if (LinkFullHp)
                        {
                            bot.Health = bot.HealthMax; // (a run is graded on the link, not on health left over from the run-in)
                        }
                        var trail = new Queue<Vector3>();
                        var walls = new Queue<bool>();
                        float best = Single.MaxValue;
                        string kind = "";
                        int f = 0, held = 0, wallFrames = 0;
                        for (; f < LinkSeconds * 60; f++)
                        {
                            human.LoadFlags &= ~LoadFlags.Active;
                            bot.AiData.AuditHold(to);
                            host.Step(input);
                            if (bot.AiData.AuditOnHold)
                            {
                                held++;
                            }
                            if (LinkDebugSet.Contains(total))
                            {
                                Console.WriteLine($"  link {total} {from.Id}->{to.Id} +{f} pos {Fmt(bot.Position)} hp {bot.Health} alt {bot.IsAltForm} {bot.AiData.AuditState()} {bot.AiData.AuditMove()}");
                            }
                            if (bot.Health == 0)
                            {
                                kind = "died";
                                break;
                            }
                            if (bot.AiData.AuditInRange(to))
                            {
                                break;
                            }
                            bool wall = bot.AiData.AuditWall;
                            if (wall)
                            {
                                wallFrames++;
                            }
                            best = Math.Min(best, (to.Position - bot.Position).Length);
                            trail.Enqueue(bot.Position);
                            walls.Enqueue(wall);
                            if (trail.Count > 120)
                            {
                                trail.Dequeue();
                                walls.Dequeue();
                            }
                        }
                        int steps = Math.Min(f + 1, LinkSeconds * 60);
                        if (kind == "" && held < steps * 9 / 10)
                        {
                            // the AI left the held link for more than a tenth of the run: this run didn't test it
                            kind = "untested";
                        }
                        else if (kind == "" && f == LinkSeconds * 60)
                        {
                            bool still = (trail.Peek() - bot.Position).Length < 0.5f;
                            bool walled = walls.Count(w => w) * 2 >= walls.Count;
                            kind = bot.Position.Y < Math.Min(from.Position.Y, to.Position.Y) - 3 ? "fell"
                                : walled ? "walled" : still ? "pinned" : "wander";
                        }
                        if (kind == "")
                        {
                            ok++;
                            okFrames += f;
                            okWall += wallFrames;
                            // (every link gets a line: the table covers working ones too)
                            fails.Add($"  {"ok",-15} #{total,-4} {from.Id,3} {from.NodeType,-10} {Fmt(from.Position)} -> {to.Id,3} {to.NodeType,-10} {Fmt(to.Position)}"
                                + $"  len {(to.Position - from.Position).Length:0.0} dy {to.Position.Y - from.Position.Y:0.0} took {f / 60f:0.0} s on a wall {wallFrames / 60f:0.0} s lead {lead}");
                            if (wallFrames >= 30)
                            {
                                rubbed.Add((wallFrames, $"  rubbed          #{total,-4} {from.Id,3} {from.NodeType,-10} {Fmt(from.Position)} -> {to.Id,3} {to.NodeType,-10} {Fmt(to.Position)}"
                                    + $"  len {(to.Position - from.Position).Length:0.0} took {f / 60f:0.0} s, on a wall {wallFrames / 60f:0.0} s, lead {lead}"));
                            }
                            continue;
                        }
                        Vector3 end = bot.Position;
                        string line = $" #{total,-4} {from.Id,3} {from.NodeType,-10} {Fmt(from.Position)} -> {to.Id,3} {to.NodeType,-10} {Fmt(to.Position)}"
                            + $"  len {(to.Position - from.Position).Length:0.0} dy {to.Position.Y - from.Position.Y:0.0} end {Fmt(end)} closest {best:0.0}"
                            + $" on a wall {wallFrames / 60f:0.0} s held {100f * held / steps:0}% lead {lead}";
                        // a run that didn't get there and didn't die: let go, the AI plays on by its own goals and route (no
                        // foe) from where it ended up. back = it reached 2 nodes by itself in BackSeconds (the link failed,
                        // the bot isn't stuck); stuck = it didn't; died = on the way
                        if (kind == "fell" || kind == "walled" || kind == "pinned" || kind == "wander")
                        {
                            bot.AiData.AuditHold(null);
                            NodeData3? last = bot.AiData.AuditPrev;
                            int r = 0, reached = 0;
                            string after = "stuck";
                            for (; r < BackSeconds * 60; r++)
                            {
                                human.LoadFlags &= ~LoadFlags.Active;
                                host.Step(input);
                                if (bot.Health == 0)
                                {
                                    after = "died";
                                    break;
                                }
                                NodeData3? prev = bot.AiData.AuditPrev;
                                if (prev != null && prev != last)
                                {
                                    last = prev;
                                    if (++reached == 2)
                                    {
                                        after = "back";
                                        break;
                                    }
                                }
                            }
                            kind += "/" + after;
                            line += $" then {r / 60f:0.0} s";
                        }
                        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                        fails.Add($"  {kind,-15}" + line);
                    }
                }
                bot.AiData.AuditHold(null);
                lines.Add($"{arena.InGameName,-21} | {bot.Hunter,-8} links {total}: ok {ok} ({(total == 0 ? 0 : 100f * ok / total):0.0}%, mean {(ok == 0 ? 0 : okFrames / 60f / ok):0.0} s, "
                    + $"on a wall {okWall / 60f:0.0} s in all, {rubbed.Count} rubbed 0.5 s+), "
                    + "not ok: " + String.Join(" ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) + $" ({nodes.Count} nodes, {cold} cold starts)");
                int first = lines.Count - 1;
                lines.AddRange(fails);
                lines.AddRange(rubbed.OrderByDescending(r => r.Item1).Select(r => r.Item2));
                Console.WriteLine(lines[first]);
            }
            finally
            {
                bot.AiData.AuditHold(null);
                host.Dispose();
            }
        }

        private static readonly OpenTK.Mathematics.Vector3[,] SelfTrail = new OpenTK.Mathematics.Vector3[8, 180];
        private static readonly int[] SelfSeen = new int[8];

        private static int SelfEdgeCount;
        private static long SelfEdgeFrame = -1; // the last edge jump (any bot)
        private static readonly long[] SelfClimbFrame = new long[8]; // each bot's last jump start (_unstickClimb 0 -> 1)
        private static readonly int[] SelfClimbLast = new int[8];
        private static readonly long[] SelfStuckFrame = new long[8]; // each bot's last stuck-fix climb (_unstickClimbs up)
        private static readonly int[] SelfStuckLast = new int[8];
        private static readonly string[] SelfNode = new string[8];

        private static int SelfTraceSeat = -1;
        private static long SelfTraceFrom, SelfTraceTo;

        private static string AiButtonsDown(PlayerEntity p) // selftrace: the move buttons held (X fwd, B back, Y left, A right) + L jump
        {
            object? b = p.AiData.GetType().GetField("_buttons", NonPub)?.GetValue(p.AiData);
            if (b == null) return "?";
            string s = "";
            foreach (string n in new[] { "X", "B", "Y", "A", "L" })
            {
                object? btn = b.GetType().GetProperty(n)?.GetValue(b) ?? b.GetType().GetField(n)?.GetValue(b);
                bool down = btn != null && (bool)(btn.GetType().GetProperty("IsDown")!.GetValue(btn)!);
                s += down ? n : "-";
            }
            return s;
        }

        private static string AiLeafCtx(PlayerEntity p) // selftrace: the leaf context's Field2C/Field20/Field30 (path-follow mode)
        {
            if (p.AiData.GetType().GetField("_executionTree", NonPub)?.GetValue(p.AiData) is not Array tree) return "?";
            object? leaf = null;
            foreach (object? c in tree)
            {
                if (c == null || c.GetType().GetProperty("Data1")?.GetValue(c) == null) break;
                leaf = c;
                object d = c.GetType().GetProperty("Data1")!.GetValue(c)!;
                object? kids = d.GetType().GetProperty("Data1")?.GetValue(d) ?? d.GetType().GetField("Data1")?.GetValue(d);
                if (kids is System.Collections.ICollection k && k.Count == 0) break;
            }
            if (leaf == null) return "?";
            int F(string n) => leaf.GetType().GetProperty(n)?.GetValue(leaf) is int v ? v : -1;
            return $"{F("Field2C")}/{F("Field20")}/{F("Field30")}";
        }

        private static int AiInt(PlayerEntity p, string name)
        {
            return p.AiData.GetType().GetField(name, NonPub)?.GetValue(p.AiData) is int v ? v : 0;
        }

        private static void LogSelfKos(CampaignHost host, int count)
        {
            long f = host.Frame;
            if (f <= 1)
            {
                Array.Clear(SelfSeen);
                Array.Fill(SelfClimbFrame, -100000);
                Array.Fill(SelfStuckFrame, -100000);
            }
            if (PlayerEntity.HostEdgeCounts[0] != SelfEdgeCount)
            {
                SelfEdgeCount = PlayerEntity.HostEdgeCounts[0];
                SelfEdgeFrame = f;
            }
            for (int i = 0; i < count; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p.Health > 0)
                {
                    SelfTrail[i, f % 180] = p.Position;
                    int climb = AiInt(p, "_unstickClimb"), climbs = AiInt(p, "_unstickClimbs");
                    if (climb > 0 && SelfClimbLast[i] == 0)
                    {
                        SelfClimbFrame[i] = f;
                    }
                    if (climbs > SelfStuckLast[i])
                    {
                        SelfStuckFrame[i] = f;
                    }
                    SelfClimbLast[i] = climb;
                    SelfStuckLast[i] = climbs;
                    object? n40 = p.AiData.GetType().GetField("_node40", NonPub)?.GetValue(p.AiData);
                    SelfNode[i] = n40 is NodeData3 nd ? $"{nd.Id}@{nd.Position.X:0.0},{nd.Position.Y:0.0},{nd.Position.Z:0.0}" : "-";
                    if (i == SelfTraceSeat && f == SelfTraceFrom)
                    {
                        foreach (AreaVolumeEntity v in host.Scene.GetAreaVolumeEntities())
                        {
                            if (v.Data.InsideMessage == Message.Gravity)
                            {
                                CollisionVolume cv = CollisionVolume.Move(v.Data.Volume, v.Position);
                                Console.WriteLine($"gravvol #{v.Id} {cv.Type} cyl pos {cv.CylinderPosition} vec {cv.CylinderVector} r {cv.CylinderRadius} dot {cv.CylinderDot}"
                                    + $" box pos {cv.BoxPosition} v1 {cv.BoxVector1} v2 {cv.BoxVector2} v3 {cv.BoxVector3} d {cv.BoxDot1} {cv.BoxDot2} {cv.BoxDot3}");
                            }
                        }
                    }
                    if (i == SelfTraceSeat && f >= SelfTraceFrom && f <= SelfTraceTo && (f % 4 == 0 || SelfTraceTo - SelfTraceFrom <= 24))
                    {
                        string grav = "";
                        foreach (AreaVolumeEntity v in host.Scene.GetAreaVolumeEntities())
                        {
                            if (v.Data.InsideMessage == Message.Gravity && CollisionVolume.Move(v.Data.Volume, v.Position).TestPoint(p.Position))
                            {
                                grav += $" grav#{v.Id}";
                            }
                        }
                        Console.WriteLine($"st f{f} pos {p.Position.X:0.0} {p.Position.Y:0.0} {p.Position.Z:0.0} spd {p.Speed.X:0.00} {p.Speed.Y:0.00} {p.Speed.Z:0.00}"
                            + $" gnd {p.Flags1.TestFlag(PlayerFlags1.Grounded)} node {SelfNode[i]} climb {climb} climbs {climbs} clock {AiInt(p, "_unstickFrames")} drift {AiInt(p, "_unstickDrift")}{grav}"
                            + $" btn {AiButtonsDown(p)} f2 {(uint)p.AiData.Flags2:X} st {p.AiData.HostState} c {AiLeafCtx(p)} uc {string.Join(",", PlayerEntity.HostUnstickCounts)} ec {string.Join(",", PlayerEntity.HostEdgeCounts)}");
                    }
                }
                if (GameState.Suicides[i] > SelfSeen[i])
                {
                    SelfSeen[i] = GameState.Suicides[i];
                    OpenTK.Mathematics.Vector3 at = SelfTrail[i, (f - 1) % 180], before = SelfTrail[i, (f + 1) % 180];
                    float maxY = Single.MinValue;
                    for (int k = 0; k < 180; k++)
                    {
                        maxY = MathF.Max(maxY, SelfTrail[i, k].Y);
                    }
                    Console.WriteLine($"selfko f {f} s{i} {p.Hunter} at {at.X:0.0} {at.Y:0.0} {at.Z:0.0} 3s-ago {before.X:0.0} {before.Y:0.0} {before.Z:0.0}"
                        + $" maxY {maxY:0.0} edge-jump-ago {(SelfEdgeFrame < 0 ? -1 : f - SelfEdgeFrame)} jump-ago {f - SelfClimbFrame[i]} stuckclimb-ago {f - SelfStuckFrame[i]} node {SelfNode[i]} by {p.NetLastAttacker} beam {p.NetLastBeam} flags {p.NetLastFlags} alt {p.IsAltForm}"
                        + VolumesAt(host, at));
                }
            }
        }

        private static string VolumesAt(CampaignHost host, OpenTK.Mathematics.Vector3 at)
        {
            string text = "";
            foreach (AreaVolumeEntity v in host.Scene.GetAreaVolumeEntities())
            {
                if ((v.Data.InsideMessage == Message.Death || v.Data.InsideMessage == Message.Damage)
                    && CollisionVolume.Move(v.Data.Volume, v.Position).TestPoint(at))
                {
                    text += $" vol#{v.Id} {v.Data.InsideMessage} {v.Data.InsideMsgParam1} @{v.Position.X:0.0},{v.Position.Y:0.0},{v.Position.Z:0.0}";
                }
            }
            return text;
        }

        private static Seat[] Play(MatchSettings settings, bool verbose, out int navNodes, out int crateBreaks, string? csv = null)
        {
            Visited.Clear();
            navNodes = 0;
            crateBreaks = 0;
            int count = settings.Players.Count;
            var seats = new Seat[count];
            for (int i = 0; i < count; i++)
            {
                seats[i] = new Seat();
            }
            CampaignHost host = CampaignHost.StartMatch(settings);
            host.Scene.HostDrawFreeSim = true; // as a real match (ReplaySession.Started): Spire's rocks move without a draw (before 10-25 they sat at the start point)
            if (Seed != 0)
            {
                Rng.SetRng1(Rng.Rng1 ^ (Seed * 2654435761u));
                Rng.SetRng2(Rng.Rng2 ^ (Seed * 40503u));
            }
            using System.IO.StreamWriter? dump = csv == null ? null : new System.IO.StreamWriter(csv);
            // diagnostic 10-28: one line per seat-1 missile (spawn, lock, closest pass to seat 0, end) next to the CSV
            using System.IO.StreamWriter? missLog = csv == null ? null : new System.IO.StreamWriter(csv + ".missiles.txt");
            var missiles = new Dictionary<BeamProjectileEntity, MissileTrace>();
            dump?.WriteLine(CsvHeader);
            try
            {
                long maxFrames = (long)((settings.TimeLimitSeconds ?? 900) * 60) + 600;
                var input = new CampaignInput { SelectWeapon = BeamType.None };
                HumanHurt.Clear();
                int humanHp = -1;
                long stillDeath = -1; int stillJitter = 0; var stillRng = new Random((int)Seed + 17);
                while (!host.Ended && host.Frame < maxFrames)
                {
                    if (HumanMove == "still")
                    {
                        // 10-28: a still human respawned on the timer, always on a frame that's a multiple of 4, and the
                        // respawn pick is FrameCount % (free spawns): 36 of 38 respawns on one spot (vanilla spreads them).
                        // Press FIRE 0-15 frames before the timer runs out so the pick spreads as a player's would.
                        PlayerEntity hm = PlayerEntity.Players[0];
                        if (hm.Health > 0) { stillDeath = -1; }
                        else if (stillDeath < 0) { stillDeath = host.Frame; stillJitter = stillRng.Next(16); }
                        input.Buttons = stillDeath >= 0 && host.Frame >= stillDeath + 584 + stillJitter
                            ? CampaignButtons.Shoot : CampaignButtons.None;
                    }
                    else
                    {
                        input.Move = new System.Numerics.Vector2(0, 1);
                        input.AimDelta = new System.Numerics.Vector2(host.Frame % 240 < 120 ? 2 : -2, 0);
                        input.Buttons = host.Frame % 30 < 10 ? CampaignButtons.Shoot : CampaignButtons.None;
                    }
                    host.Step(input);
                    PlayerEntity me = PlayerEntity.Players[0];
                    if (humanHp >= 0 && me.Health < humanHp && me.NetLastAttacker > 0)
                    {
                        PlayerEntity by = PlayerEntity.Players[me.NetLastAttacker];
                        string key = $"s{me.NetLastAttacker}:{(by.IsAltForm ? "ball" : by.CurrentWeapon.ToString())}";
                        HumanHurt[key] = HumanHurt.GetValueOrDefault(key) + humanHp - me.Health;
                        HumanHurt["hits"] = HumanHurt.GetValueOrDefault("hits") + 1;
                    }
                    humanHp = me.Health;
                    DemoStep(host);
                    if (WallsDown && PlayerEntity.HostWallList != null)
                    {
                        foreach (IBotBreakable w in PlayerEntity.HostWallList)
                        {
                            if (w is MphRecomp.Arenas.EchoesBreakableEntity b && b.Up)
                            {
                                b.Hit(b.FullHp);
                            }
                        }
                    }
                    if (dump != null)
                    {
                        WriteRow(dump, host.Frame, count);
                    }
                    if (missLog != null)
                    {
                        TraceMissiles(host, missLog, missiles);
                    }
                    if (SelfLog)
                    {
                        LogSelfKos(host, count);
                    }
                    for (int i = 1; i < count; i++)
                    {
                        Measure(host, seats[i], PlayerEntity.Players[i], i, verbose);
                        MeasureEchoes(host, seats[i], PlayerEntity.Players[i]);
                    }
                }
                var nodes = new HashSet<NodeData3>();
                foreach (var a in host.Scene.Room.NodeData?.Data ?? new List<IReadOnlyList<IReadOnlyList<NodeData3>>>())
                {
                    foreach (var b in a)
                    {
                        foreach (NodeData3 n in b)
                        {
                            nodes.Add(n);
                        }
                    }
                }
                navNodes = nodes.Count;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is MphRecomp.Arenas.EchoesBreakableEntity grate)
                    {
                        crateBreaks += grate.Breaks;
                    }
                    else if (e is MphRecomp.Arenas.EchoesCrateEntity crate)
                    {
                        crateBreaks += crate.Breaks;
                    }
                }
            }
            finally
            {
                host.Dispose();
            }
            return seats;
        }

        private sealed class MissileTrace
        {
            public long Frame;
            public Vector3 Start, Dir, Last, Human;
            public float AimErr, MinDist = Single.MaxValue, Dist;
            public string Lock = "-";
            public int HumanHp;
            public bool Homing;
        }

        private static void TraceMissiles(CampaignHost host, System.IO.StreamWriter log, Dictionary<BeamProjectileEntity, MissileTrace> missiles)
        {
            PlayerEntity human = PlayerEntity.Players[0];
            PlayerEntity bot = PlayerEntity.Players[1];
            Vector3 hp = human.Position + new Vector3(0, 0.5f, 0);
            var seen = new HashSet<BeamProjectileEntity>();
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is not BeamProjectileEntity beam || beam.Owner != bot || beam.Beam != BeamType.Missile || beam.Lifespan <= 0)
                {
                    continue;
                }
                seen.Add(beam);
                if (!missiles.TryGetValue(beam, out MissileTrace? t))
                {
                    t = new MissileTrace { Frame = host.Frame, Start = beam.Position, Dir = beam.Velocity.Normalized(), Human = hp,
                        HumanHp = human.Health, Homing = beam.Flags.TestFlag(BeamFlags.Homing) };
                    Vector3 to = hp - beam.Position;
                    t.Dist = to.Length;
                    t.AimErr = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(to.Normalized(), t.Dir), -1, 1)));
                    t.Lock = beam.Target == null ? "-" : beam.Target == human ? "human" : beam.Target.Type.ToString();
                    missiles[beam] = t;
                }
                t.Last = beam.Position;
                t.MinDist = MathF.Min(t.MinDist, (beam.Position - hp).Length);
                if (beam.Target == null && t.Lock != "-")
                {
                    t.Lock += "(lost)";
                }
            }
            foreach (BeamProjectileEntity beam in missiles.Keys.Where(b => !seen.Contains(b)).ToList())
            {
                MissileTrace t = missiles[beam];
                log.WriteLine($"f {t.Frame} dist {t.Dist:0.0} aimerr {t.AimErr:0.0} homing {(t.Homing ? 1 : 0)} lock {t.Lock} "
                    + $"min {t.MinDist:0.00} end ({t.Last.X:0.0} {t.Last.Y:0.0} {t.Last.Z:0.0}) human ({t.Human.X:0.0} {t.Human.Y:0.0} {t.Human.Z:0.0}) "
                    + $"hpdrop {t.HumanHp - human.Health} frames {host.Frame - t.Frame}");
                missiles.Remove(beam);
            }
        }

        private static void Measure(CampaignHost host, Seat s, PlayerEntity p, int index, bool verbose)
        {
            long frame = host.Frame;
            bool dead = p.LoadFlags.TestFlag(LoadFlags.Active) && p.Health == 0;
            if (dead && !s.WasDead)
            {
                s.Deaths++;
                if (verbose) Console.WriteLine($"  f {frame,5} seat {index} died at ({p.Position.X:0.0} {p.Position.Y:0.0} {p.Position.Z:0.0}) by seat {p.NetLastAttacker}");
            }
            s.WasDead = dead;
            if (!p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0 || p.IsAltForm || p.IsMorphing || p.IsUnmorphing)
            {
                s.LastYaw = Single.NaN;
                s.Trail.Clear();
                s.HoldFrom = -1;
                s.WasJump = false;
                return;
            }
            s.Frames++;
            Vector3 facing = p.CameraInfo.Facing;
            float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(facing.X, facing.Z));
            if (!Single.IsNaN(s.LastYaw))
            {
                float delta = yaw - s.LastYaw;
                if (delta > 180) delta -= 360;
                if (delta < -180) delta += 360;
                if (MathF.Abs(delta) >= 8)
                {
                    while (s.Steps.Count > 0 && frame - s.Steps.Peek().Frame > 10)
                    {
                        s.Steps.Dequeue();
                    }
                    if (s.Steps.Any(st => MathF.Sign(st.Delta) != MathF.Sign(delta)))
                    {
                        s.Snaps++;
                        PlayerEntity? tgt = p.AiData.HostTargetPlayer;
                        string cause = p.CameraInfo.Shake > 0 ? "snap:shake" : p.AiData.HostDisrupted > 0 ? "snap:disrupted" : frame - s.TargetChanged <= 12 ? "snap:switch" : tgt == null ? "snap:roam" : "snap:aiming";
                        WhyCounts[cause] = WhyCounts.GetValueOrDefault(cause) + 1;
                        s.Steps.Clear();
                        if (verbose)
                        {
                            Console.WriteLine($"  f{frame,6} seat {index} {p.Hunter} snap {delta:0} deg {cause} at {Fmt(p.Position)}");
                        }
                    }
                    else
                    {
                        s.Steps.Enqueue((frame, delta));
                    }
                }
            }
            s.LastYaw = yaw;
            if (TraceSeat == index && frame >= TraceFrom && frame < TraceFrom + TraceLength)
            {
                PlayerEntity? t = p.AiData.HostTargetPlayer;
                Vector3 ap = p.AiData.HostAimPoint;
                float apYaw = MathHelper.RadiansToDegrees(MathF.Atan2(ap.X - p.Position.X, ap.Z - p.Position.Z));
                float tYaw = t == null ? 0 : MathHelper.RadiansToDegrees(MathF.Atan2(t.Position.X - p.Position.X, t.Position.Z - p.Position.Z));
                Console.WriteLine($"  f{frame} yaw {yaw,7:0.0} aimpt-yaw {apYaw,7:0.0} target-yaw {tYaw,7:0.0} timer {p.AiData.HostAimTimer,2} dis {p.AiData.HostDisrupted,3} tgt {t?.Hunter} path {p.AiData.HostFirePath} aimpt {Fmt(ap)} tpos {Fmt(t?.Position ?? Vector3.Zero)}");
            }
            PlayerEntity? pressTarget = s.LastTarget; // the AI pressed fire last frame, at the target it had then
            if (p.AiData.HostTargetPlayer != s.LastTarget)
            {
                s.LastTarget = p.AiData.HostTargetPlayer;
                s.TargetChanged = frame;
            }
            // hops: a new jump with little travel in the 2 s before
            s.Trail.Enqueue(p.Position);
            if (s.Trail.Count > 120)
            {
                s.Trail.Dequeue();
            }
            bool jump = p.Flags1.TestFlag(PlayerFlags1.UsedJump);
            if (jump && !s.WasJump && s.Trail.Count == 120 && (p.Position.Xz - s.Trail.Peek().Xz).Length < 1.5f)
            {
                s.Hops++;
                if (verbose)
                {
                    Console.WriteLine($"  f{frame,6} seat {index} {p.Hunter} hop in place at {Fmt(p.Position)}");
                }
            }
            s.WasJump = jump;
            // holds: 4 s within 2 units
            if (s.HoldFrom < 0 || p.Health == 0 || p.AiData.HostNodePos is not Vector3 holdNode
                || (holdNode - p.Position).Length < 2 || (p.Position - s.HoldStart).Length > 2)
            {
                s.HoldFrom = frame;
                s.HoldStart = p.Position;
            }
            else if (frame - s.HoldFrom == 240)
            {
                s.Holds++;
                if (verbose)
                {
                    Console.WriteLine($"  f{frame,6} seat {index} {p.Hunter} hold at {Fmt(p.Position)} node {Fmt(p.AiData.HostNodePos ?? Vector3.Zero)}");
                }
            }
            PlayerEntity? target = pressTarget ?? p.AiData.HostTargetPlayer;
            bool newShot = p.TimeSinceShot == 0 && s.LastShotTimer != 0; // Shock Coil fires every frame: count bursts
            s.LastShotTimer = p.TimeSinceShot;
            if (newShot)
            {
                s.Shots++;
                if (p.AiData.HostCrateAim)
                {
                    // a shot at a crate (PlayerAiCrates.cs), not at a player
                }
                else if (target != null && target.Health > 0)
                {
                    Vector3 toMid = target.Position.AddY(target.IsAltForm ? 0.5f : 1) - p.CameraInfo.Position;
                    float aimErr = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(p.CameraInfo.Facing.Normalized(), toMid.Normalized()), -1, 1)));
                    (p.AiData.HostSees(target) ? s.AimSeen : s.AimHidden).Add(aimErr);
                    if (target.SlotIndex == 0)
                    {
                        string k = $"shot{index}:{(p.IsAltForm ? "ball" : p.CurrentWeapon.ToString())}";
                        HumanHurt[k] = HumanHurt.GetValueOrDefault(k) + 1;
                        if (aimErr < 5) HumanHurt[k + "<5deg"] = HumanHurt.GetValueOrDefault(k + "<5deg") + 1;
                        float dist = (target.Position - p.Position).Length;
                        string d = dist < 5 ? "d<5" : dist < 10 ? "d5-10" : dist < 15 ? "d10-15" : "d15+";
                        HumanHurt[d] = HumanHurt.GetValueOrDefault(d) + 1;
                    }
                    if (verbose && !p.AiData.HostSees(target) && aimErr > 30)
                    {
                        CollisionResult gunRay = default;
                        bool gunBlocked = CollisionDetection.CheckBetweenPoints(p.GunMuzzlePosition, target.Position.AddY(1), TestFlags.Beams, host.Scene, ref gunRay);
                        Console.WriteLine($"  f{frame,6} seat {index} WILD shot {p.CurrentWeapon} err {aimErr:0} path {p.AiData.HostFirePath}@{p.AiData.HostFirePathFrame} gunRayBlocked {gunBlocked} dist {(target.Position - p.Position).Length:0.0} lookframe {frame - p.AiData.HostLookFrame}");
                    }
                    Vector3 to = target.Position.AddY(target.IsAltForm ? 0.5f : 1);
                    CollisionResult discard = default;
                    bool blocked = CollisionDetection.CheckBetweenPoints(p.GunMuzzlePosition, to, TestFlags.None, host.Scene, ref discard);
                    if (blocked && discard.EntityCollision?.Entity is EntityBase hitShell && hitShell == target.HostSeatShell)
                    {
                        // the turret's shell around its occupant: the shot damages the turret (EchoesTurretEntity.Hit),
                        // which is how bots fight an occupied turret (owner D8), so not a wasted shot
                        WhyCounts["turret shell"] = WhyCounts.GetValueOrDefault("turret shell") + 1;
                    }
                    else if (blocked)
                    {
                        s.Walls++;
                        // why the fire rule let it through: under sqrt(10) / Shock Coil / visibility flag stale (eyes
                        // blocked now) / eyes clear but the muzzle line blocked (corner) / flag false (another path)
                        CollisionResult eyes = default;
                        bool eyesBlocked = CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, target.CameraInfo.Position,
                            TestFlags.None, host.Scene, ref eyes);
                        string why = (target.Position - p.Position).LengthSquared < 10 ? "close"
                            : p.AiData.HostWeapon == 7 ? "coil"
                            : !p.AiData.HostSees(target) ? "noflag"
                            : eyesBlocked ? "stale"
                            : "muzzle";
                        WhyCounts[why] = WhyCounts.GetValueOrDefault(why) + 1;
                        string path = $"path {why}:{p.AiData.HostFirePath}";
                        WhyCounts[path] = WhyCounts.GetValueOrDefault(path) + 1;
                        string weapon = $"{why}:{p.CurrentWeapon}";
                        WhyCounts[weapon] = WhyCounts.GetValueOrDefault(weapon) + 1;
                        if (discard.EntityCollision?.Entity is EntityBase hitEntity)
                        {
                            string what = $"entity {hitEntity.GetType().Name}";
                            WhyCounts[what] = WhyCounts.GetValueOrDefault(what) + 1;
                        }
                        if (verbose)
                        {
                            Console.WriteLine($"  f{frame,6} seat {index} {p.Hunter} shot {p.CurrentWeapon} into a wall [{why} {p.AiData.HostFirePath}@{p.AiData.HostFirePathFrame}] at "
                                + $"{Fmt(p.Position)} -> {target.Hunter} {Fmt(target.Position)}"
                                + $"{(discard.EntityCollision?.Entity is EntityBase e ? $" (entity {e.GetType().Name})" : "")}");
                        }
                    }
                }
            }
            if (p.AiData.HostTargetPlayer is not PlayerEntity roamT || !p.AiData.HostSees(roamT))
            {
                float pitch = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(facing.Y, -1, 1)));
                s.RoamPitch.Add(pitch);
                if (MathF.Abs(pitch) >= 25)
                {
                    string why = frame - p.AiData.HostLookFrame <= 2 ? $"tilt:look{p.AiData.HostLookB}" : "tilt:held";
                    WhyCounts[why] = WhyCounts.GetValueOrDefault(why) + 1;
                    if (verbose && frame % 60 == 0)
                    {
                        Console.WriteLine($"  f{frame,6} seat {index} {p.Hunter} roams tilted {pitch:0} deg [{why}] at {Fmt(p.Position)}");
                    }
                }
            }
        }

        private static void MeasureEchoes(CampaignHost host, Seat s, PlayerEntity p)
        {
            if (!p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0)
            {
                s.LastHealth = -1;
                return;
            }
            bool pad = p.Flags1.TestFlag(PlayerFlags1.UsedJumpPad);
            if (pad && !s.WasPad) s.Pads++;
            s.WasPad = pad;
            // an Echoes ground jump leaves UsedJump clear (SpaceJump), so its rise is the air jump
            bool air = !p.IsAltForm && !p.IsMorphing && !p.IsUnmorphing // alt forms set UsedJump every frame (ProcessAlt)
                && p.Flags1.TestFlag(PlayerFlags1.UsedJump) && !p.Flags1.TestFlag(PlayerFlags1.Standing);
            if (air && !s.WasAirJump) s.AirJumps++;
            s.WasAirJump = air;
            if (p.EchoesSubmerged) s.WetFrames++;
            bool boost = p.EchoesBoosting;
            if (boost && !s.WasBoost) s.Boosts++;
            s.WasBoost = boost;
            int weapons = 0;
            for (int i = 0; i < 14; i++)
            {
                if (p.AvailableWeapons[i]) weapons++;
            }
            int ammo0 = p.HostAmmo(0), ammo1 = p.HostAmmo(1);
            if (s.LastHealth >= 0 && (p.Health > s.LastHealth || ammo0 > s.LastAmmo0 || ammo1 > s.LastAmmo1 || weapons > s.LastWeapons))
            {
                s.Pickups++;
            }
            s.LastHealth = p.Health;
            s.LastAmmo0 = ammo0;
            s.LastAmmo1 = ammo1;
            s.LastWeapons = weapons;
            if (host.Scene.Room.NodeData is NodeData data && host.Frame % 10 == 0)
            {
                foreach (var a in data.Data)
                {
                    foreach (var b in a)
                    {
                        foreach (NodeData3 n in b)
                        {
                            if ((n.Position - p.Position).LengthSquared < 1.5f * 1.5f) Visited.Add(n);
                        }
                    }
                }
            }
        }

        // Vanilla parity (10-24): one per-frame row for all four seats, the same columns BizHawk's vlog.lua writes from
        // the DS game's RAM, so replay= scores vanilla and ours with one analyzer
        private const string CsvFields = "x,y,z,cx,cy,cz,ax,ay,az,hp,tss,wpn,chg,f1,f2,tgt,aif2";

        private static string CsvHeader => "f," + String.Join(",", Enumerable.Range(0, 4)
            .Select(i => String.Join(",", CsvFields.Split(',').Select(n => n + i)))) + ",vis,"
            // the bots' next-node positions after vis (our side only; replay= reads columns by index, vis = c[69])
            + String.Join(",", Enumerable.Range(0, 4).Select(i => $"nx{i},ny{i},nz{i},st{i}"))
            + "," + String.Join(",", Enumerable.Range(0, 4).Select(i => $"q{i}")) // the AI's stuck counter (Field118)
            + "," + String.Join(",", Enumerable.Range(0, 4).Select(i => $"u{i}")) // following nodes (1/0) : the unstick clock
            + "," + String.Join(",", Enumerable.Range(0, 4).Select(i => $"ua{i},ms{i}")) // ammo: UA, missiles
            + ",dbg1"; // seat 1's fire state: Flags4 | _shotDelay | R FramesDown | R FramesUp | sight blocked | Shoot down

        private static readonly System.Reflection.BindingFlags NonPub = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        private static string FireDebug(PlayerEntity p)
        {
            object ai = p.AiData;
            Type t = ai.GetType();
            int delay = (int)t.GetField("_shotDelay", NonPub)!.GetValue(ai)!;
            object buttons = t.GetField("_buttons", NonPub)!.GetValue(ai)!;
            object r = buttons.GetType().GetProperty("R")!.GetValue(buttons)!;
            int fd = (int)r.GetType().GetProperty("FramesDown")!.GetValue(r)!;
            int fu = (int)r.GetType().GetProperty("FramesUp")!.GetValue(r)!;
            int w1 = (int)t.GetField("_weapon1", NonPub)!.GetValue(ai)!;
            // 10-28 bombs: | bomb ammo | bomb cooldown | L FramesDown | L FramesUp | _field102E (ball-attack L delay)
            object l = buttons.GetType().GetProperty("L")!.GetValue(buttons)!;
            Type pt = typeof(PlayerEntity);
            int ammo = Convert.ToInt32(pt.GetField("_bombAmmo", NonPub)!.GetValue(p));
            int bcd = Convert.ToInt32(pt.GetField("_bombCooldown", NonPub)!.GetValue(p));
            int lfd = (int)l.GetType().GetProperty("FramesDown")!.GetValue(l)!;
            int lfu = (int)l.GetType().GetProperty("FramesUp")!.GetValue(l)!;
            int f102e = Convert.ToInt32(t.GetField("_field102E", NonPub)!.GetValue(ai));
            // 10-28: | execution path as Func24:FieldC:FieldF per depth, root to leaf (which ball-attack context is live)
            var path = new System.Text.StringBuilder();
            object? leaf = null;
            foreach (object? c in (Array)t.GetField("_executionTree", NonPub)!.GetValue(ai)!)
            {
                if (c == null) break;
                leaf = c;
                Type ct = c.GetType();
                object? d1 = ct.GetProperty("Data1")!.GetValue(c);
                if (d1 == null) break;
                if (path.Length > 0) path.Append('/');
                path.Append($"{ct.GetProperty("Func24Id")!.GetValue(c)}:{ct.GetProperty("FieldC")!.GetValue(c)}:{ct.GetProperty("FieldF")!.GetValue(c)}");
                var n1 = (AiPersonalityData1)d1;
                if (n1.Data1.Count == 0)
                {
                    // 10-28: once, print the exits of the ball-attack siblings (30/31/32) and what each one checks
                    if (n1.Parent != null && n1.Parent.Data1.Any(s => s.Func24Id == 30 || s.Func24Id == 60) && _execDumped.Add(n1.Parent))
                    {
                        var sib = n1.Parent.Data1;
                        for (int k = 0; k < sib.Count; k++)
                        {
                            Console.WriteLine($"[exits] child {k}: Func24 {sib[k].Func24Id} d3a [{string.Join(",", sib[k].Data3a)}] d3b [{string.Join(",", sib[k].Data3b)}]");
                            foreach (AiPersonalityData2 e in sib[k].Data2)
                            {
                                string gates = string.Join(" & ", e.Data4.Select(g => $"f3_{g.Func3Id}({g.Parameters.Param1},{g.Parameters.Param2})"));
                                Console.WriteLine($"[exits]   -> {(e.Data1SelectIndex >= 20 ? "reset" : $"child {e.Data1SelectIndex} (Func24 {sib[e.Data1SelectIndex].Func24Id})")}"
                                    + $" w {e.Weight} x f3_{e.Func3Id}({e.Parameters.Param1},{e.Parameters.Param2}){(gates.Length > 0 ? " if " + gates : "")}");
                            }
                        }
                    }
                    break;
                }
            }
            return $"{(int)p.AiData.Flags4}|{delay}|{fd}|{fu}|{(p.AiData.SightBlocked ? 1 : 0)}|{(p.Controls.Shoot.IsDown ? 1 : 0)}|{p.AiData.HostFirePath}@{p.AiData.HostFirePathFrame}|{w1}|{p.EquipInfo.ChargeLevel}|{(int)p.AiData.Flags2}"
                + $"|{ammo}|{bcd}|{lfd}|{lfu}|{f102e}|{path}|{ItemTag(t.GetField("_itemC8", NonPub)!.GetValue(ai) as ItemInstanceEntity)}|{MoveTag(p, buttons, leaf)}";
        }

        // 10-28: | move buttons down (X B Y A) : turn amount : facing x,z : leaf context Field20,2C,30,40,44
        private static string MoveTag(PlayerEntity p, object buttons, object? leaf)
        {
            string b = "";
            foreach (string n in new[] { "X", "B", "Y", "A" })
            {
                object btn = buttons.GetType().GetProperty(n)!.GetValue(buttons)!;
                if ((bool)btn.GetType().GetProperty("IsDown")!.GetValue(btn)!) b += n;
            }
            float aimX = (float)p.AiData.GetType().GetField("_buttonAimX", NonPub)!.GetValue(p.AiData)!;
            string ctx = "";
            if (leaf != null)
            {
                Type ct = leaf.GetType();
                ctx = string.Join(";", new[] { "Field20", "Field2C", "Field30", "Field40", "Field44", "Field18", "Field1C", "Field28" }.Select(f => ct.GetProperty(f)!.GetValue(leaf)));
            }
            object? n40 = p.AiData.GetType().GetField("_node40", NonPub)!.GetValue(p.AiData);
            string np = "";
            if (n40 != null)
            {
                object pos = n40.GetType().GetProperty("Position")?.GetValue(n40) ?? n40.GetType().GetField("Position")!.GetValue(n40)!;
                var v = (OpenTK.Mathematics.Vector3)pos;
                np = $"{v.X:0.00};{v.Y:0.00};{v.Z:0.00};{n40.GetType().GetProperty("NodeType")?.GetValue(n40) ?? n40.GetType().GetField("NodeType")?.GetValue(n40)}";
            }
            var a48 = (OpenTK.Mathematics.Vector3)p.AiData.GetType().GetField("_field1048", NonPub)!.GetValue(p.AiData)!;
            return $"{(b.Length > 0 ? b : "-")}:{aimX:0.###}:{p.Field70:0.00};{p.Field74:0.00}:{ctx}:{np}:{p.Speed.X:0.000};{p.Speed.Z:0.000}:{a48.X:0.0};{a48.Y:0.0};{a48.Z:0.0}";
        }

        // 10-28: | item the bot is heading for: type@x:y:z (despawn timer), or - for none
        private static string ItemTag(ItemInstanceEntity? item)
        {
            if (item == null) return "-";
            Vector3 q = item.Position;
            return $"{item.ItemType}@{q.X:0.0}:{q.Y:0.0}:{q.Z:0.0}({item.DespawnTimer})";
        }

        private static void WriteRow(System.IO.StreamWriter w, long frame, int count)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(frame.ToString(ic));
            for (int i = 0; i < 4; i++)
            {
                if (i >= count)
                {
                    sb.Append(",0,0,0,0,0,0,0,0,1,0,255,0,0,0,0,-1,0");
                    continue;
                }
                PlayerEntity p = PlayerEntity.Players[i];
                Vector3 c = p.CameraInfo.Position, a = p.CameraInfo.Facing;
                int tgt = p.IsBot ? p.AiData.HostTargetPlayer?.SlotIndex ?? -1 : -1;
                sb.Append(String.Format(ic, ",{0:0.0000},{1:0.0000},{2:0.0000},{3:0.0000},{4:0.0000},{5:0.0000},{6:0.00000},{7:0.00000},{8:0.00000},{9},{10},{11},{12},{13},{14},{15},{16}",
                    p.Position.X, p.Position.Y, p.Position.Z, c.X, c.Y, c.Z, a.X, a.Y, a.Z, p.Health, Math.Min((int)p.TimeSinceShot, 255),
                    (int)p.CurrentWeapon, p.EquipInfo.ChargeLevel, (uint)p.Flags1, (uint)p.Flags2, tgt, p.IsBot ? (uint)p.AiData.Flags2 : 0u));
            }
            sb.Append(',');
            for (int k = 0; k < 16; k++)
            {
                int a = k % 4, b = k / 4;
                bool sees = a < count && b < count && a != b && PlayerEntity.Players[b].AiData is { } ai && ai.HostSees(PlayerEntity.Players[a]);
                sb.Append(sees ? "01" : "00");
            }
            for (int i = 0; i < 4; i++)
            {
                Vector3? n = i < count && PlayerEntity.Players[i].IsBot ? PlayerEntity.Players[i].AiData.HostNodePos : null;
                sb.Append(n is Vector3 v ? String.Format(ic, ",{0:0.00},{1:0.00},{2:0.00}", v.X, v.Y, v.Z) : ",,,");
                sb.Append(',').Append(i < count && PlayerEntity.Players[i].IsBot ? PlayerEntity.Players[i].AiData.HostState : "");
            }
            for (int i = 0; i < 4; i++)
            {
                sb.Append(',').Append(i < count && PlayerEntity.Players[i].IsBot ? PlayerEntity.Players[i].AiData.Field118.ToString(ic) : "");
            }
            for (int i = 0; i < 4; i++)
            {
                sb.Append(',').Append(i < count && PlayerEntity.Players[i].IsBot ? PlayerEntity.Players[i].AiData.HostUnstickState : "");
            }
            for (int i = 0; i < 4; i++)
            {
                sb.Append(',').Append(i < count ? PlayerEntity.Players[i].HostAmmo(0).ToString(ic) : "");
                sb.Append(',').Append(i < count ? PlayerEntity.Players[i].HostAmmo(1).ToString(ic) : "");
            }
            sb.Append(',').Append(count > 1 && PlayerEntity.Players[1].IsBot ? FireDebug(PlayerEntity.Players[1]) : "");
            w.WriteLine(sb.ToString());
        }

        private sealed class RSeat
        {
            public long Frames, SeenFrames;
            public int Shots, OnTarget, Hits, Walls, WallsSeen, Snaps, Hops, Holds, Deaths;
            public readonly List<float> AimSeen = new(), AimHidden = new(), RoamPitch = new();
            public float LastYaw = Single.NaN;
            public readonly Queue<(long Frame, float Delta)> Steps = new();
            public readonly Queue<Vector2> Trail = new();
            public Vector3 HoldStart;
            public long HoldFrom = -1;
            public bool WasJump, WasAlive;
            public int LastTss = -1;
            public readonly List<(long Frame, int Target, int Hp)> Pending = new();

            public void Add(RSeat o)
            {
                Frames += o.Frames; SeenFrames += o.SeenFrames; Shots += o.Shots; OnTarget += o.OnTarget; Hits += o.Hits;
                Walls += o.Walls; WallsSeen += o.WallsSeen; Snaps += o.Snaps; Hops += o.Hops; Holds += o.Holds; Deaths += o.Deaths;
                AimSeen.AddRange(o.AimSeen); AimHidden.AddRange(o.AimHidden); RoamPitch.AddRange(o.RoamPitch);
            }
        }

        private readonly struct RRow
        {
            public readonly long F;
            public readonly Vector3[] Pos, Cam, Face;
            public readonly int[] Hp, Tss, Wpn, Tgt;
            public readonly uint[] F1;
            public readonly byte[] Vis;

            public RRow(string line)
            {
                string[] c = line.Split(',');
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                float F(int i) => Single.Parse(c[i], ic);
                this.F = Int64.Parse(c[0], ic);
                Pos = new Vector3[4]; Cam = new Vector3[4]; Face = new Vector3[4];
                Hp = new int[4]; Tss = new int[4]; Wpn = new int[4]; Tgt = new int[4]; F1 = new uint[4];
                for (int i = 0; i < 4; i++)
                {
                    int b = 1 + i * 17;
                    Pos[i] = new Vector3(F(b), F(b + 1), F(b + 2));
                    Cam[i] = new Vector3(F(b + 3), F(b + 4), F(b + 5));
                    Face[i] = new Vector3(F(b + 6), F(b + 7), F(b + 8));
                    Hp[i] = Int32.Parse(c[b + 9], ic);
                    Tss[i] = Int32.Parse(c[b + 10], ic);
                    Wpn[i] = Int32.Parse(c[b + 11], ic);
                    F1[i] = (uint)Int64.Parse(c[b + 13], ic);
                    Tgt[i] = Int32.Parse(c[b + 15], ic);
                }
                string v = c[69];
                Vis = new byte[16];
                for (int k = 0; k < 16; k++)
                {
                    Vis[k] = Convert.ToByte(v.Substring(k * 2, 2), 16);
                }
            }

            public bool Biped(int i) => Hp[i] > 0 && (F1[i] & 0x1A00) == 0; // alive, not alt form / morphing / unmorphing
            public Vector3 Mid(int i) => Pos[i] + new Vector3(0, (F1[i] & 0x200) != 0 ? 0.5f : 1, 0);
            public bool Sees(int viewer, int target) => Vis[target + viewer * 4] != 0;
        }

        // replay=<a.csv,b.csv> arena=<name>: scores bot seats 1-3 from CSV rows (vlog.lua's vanilla dump or csv=), with
        // the room's collision for the wall test. Shots: TimeSinceShot back to 0, alive in biped form; aim error from the
        // camera to the AI target's middle (target and its seen flag from the frame before); on target = within the
        // target's angular half-width (0.6 u); hit = the target's health drops within 45 frames; walls = room/entity
        // collision between the camera and the target's middle. snaps / hops / holds as in the live probe (holds without
        // the node condition, which vanilla's RAM dump doesn't have).
        private static void Replay(string[] files, string arenaArg, int level)
        {
            RoomMetadata arena = MatchArenas.Find(arenaArg) ?? throw new ArgumentException($"no arena {arenaArg}");
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 3, level);
            CampaignHost host = CampaignHost.StartMatch(settings);
            try
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None });
                var totals = new RSeat[4];
                for (int i = 0; i < 4; i++)
                {
                    totals[i] = new RSeat();
                }
                long humanHurt = 0, totalFrames = 0;
                int humanDeaths = 0;
                Console.WriteLine("file                       seat  min shots/m seen n med  p90 >30% | hidden n med >30% | ontgt% hit% walls(seen) | snaps/m hops holds deaths tgtseen% | pitch mean/p90");
                foreach (string file in files)
                {
                    var seats = new RSeat[4];
                    for (int i = 0; i < 4; i++)
                    {
                        seats[i] = new RSeat();
                    }
                    RRow? prev = null;
                    long hurt = 0, frames = 0;
                    int hDeaths = 0;
                    foreach (string line in System.IO.File.ReadLines(file).Skip(1))
                    {
                        if (line.Length < 10) continue;
                        var row = new RRow(line);
                        if (row.F > 120 && prev is RRow pr)
                        {
                            frames++;
                            if (row.Hp[0] < pr.Hp[0]) hurt += pr.Hp[0] - row.Hp[0];
                            if (row.Hp[0] == 0 && pr.Hp[0] > 0) hDeaths++;
                            for (int i = 1; i < 4; i++)
                            {
                                Score(seats[i], i, row, pr, host.Scene);
                            }
                        }
                        prev = row;
                    }
                    totalFrames += frames;
                    humanHurt += hurt;
                    humanDeaths += hDeaths;
                    for (int i = 1; i < 4; i++)
                    {
                        Console.WriteLine(RLine(System.IO.Path.GetFileNameWithoutExtension(file), i, seats[i]));
                        totals[i].Add(seats[i]);
                    }
                    Console.WriteLine($"{"",-26} human: damage taken {hurt / Math.Max(1, frames / 3600f):0}/min, deaths {hDeaths}");
                }
                if (files.Length > 1)
                {
                    var all = new RSeat();
                    for (int i = 1; i < 4; i++)
                    {
                        Console.WriteLine(RLine("ALL", i, totals[i]));
                        all.Add(totals[i]);
                    }
                    Console.WriteLine(RLine("ALL", 0, all) + "   (seat 0 = all bot seats)");
                    Console.WriteLine($"{"",-26} human: damage taken {humanHurt / Math.Max(1, totalFrames / 3600f):0}/min, deaths {humanDeaths}");
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        private static string RLine(string name, int seat, RSeat s)
        {
            float min = Math.Max(1, s.Frames) / 3600f;
            float Pct(List<float> l, float p) => l.Count == 0 ? 0 : Percentile(l, p);
            float Over(List<float> l) => l.Count == 0 ? 0 : 100f * l.Count(v => v > 30) / l.Count;
            int aimed = s.AimSeen.Count + s.AimHidden.Count;
            string pitch = s.RoamPitch.Count == 0 ? "-" : $"{s.RoamPitch.Average(),5:0.0}/{Percentile(s.RoamPitch, 0.9f),5:0.0}";
            return $"{name,-26} {seat,4} {min,4:0.0} {s.Shots / min,7:0.0} {s.AimSeen.Count,6} {Pct(s.AimSeen, 0.5f),4:0} {Pct(s.AimSeen, 0.9f),4:0} {Over(s.AimSeen),4:0}% | "
                + $"{s.AimHidden.Count,8} {Pct(s.AimHidden, 0.5f),3:0} {Over(s.AimHidden),4:0}% | {(aimed == 0 ? 0 : 100f * s.OnTarget / aimed),6:0} {(aimed == 0 ? 0 : 100f * s.Hits / aimed),4:0} {s.Walls,5}({s.WallsSeen,3}) | "
                + $"{s.Snaps / min,7:0.0} {s.Hops,4} {s.Holds,5} {s.Deaths,6} {100f * s.SeenFrames / Math.Max(1, s.Frames),8:0} | {pitch}";
        }

        private static void Score(RSeat s, int i, RRow row, RRow pr, Scene scene)
        {
            for (int k = s.Pending.Count - 1; k >= 0; k--)
            {
                (long f, int t, int hp) = s.Pending[k];
                if (row.Hp[t] < hp)
                {
                    s.Hits++;
                    s.Pending.RemoveAt(k);
                }
                else if (row.F - f > 45)
                {
                    s.Pending.RemoveAt(k);
                }
            }
            bool alive = row.Hp[i] > 0;
            if (!alive && s.WasAlive) s.Deaths++;
            s.WasAlive = alive;
            int lastTss = s.LastTss;
            s.LastTss = row.Tss[i];
            if (!row.Biped(i))
            {
                s.LastYaw = Single.NaN;
                s.Trail.Clear();
                s.HoldFrom = -1;
                s.WasJump = false;
                return;
            }
            s.Frames++;
            Vector3 face = row.Face[i];
            int tp = pr.Tgt[i];
            bool hasTarget = tp >= 0 && tp < 4 && tp != i;
            if (hasTarget && pr.Sees(i, tp)) s.SeenFrames++;
            else s.RoamPitch.Add(MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(face.Y / Math.Max(face.Length, 1e-4f), -1, 1))));
            float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(face.X, face.Z));
            // vanilla turns once per 30 Hz tick (the facing holds still every other frame), so steps are taken over
            // 2 frames on both sides: 16+ degrees per 2 frames = the live probe's 8+ per frame
            if ((row.F & 1) == 0 && !Single.IsNaN(s.LastYaw))
            {
                float delta = yaw - s.LastYaw;
                if (delta > 180) delta -= 360;
                if (delta < -180) delta += 360;
                if (MathF.Abs(delta) >= 16)
                {
                    while (s.Steps.Count > 0 && row.F - s.Steps.Peek().Frame > 10) s.Steps.Dequeue();
                    if (s.Steps.Any(st => MathF.Sign(st.Delta) != MathF.Sign(delta)))
                    {
                        s.Snaps++;
                        s.Steps.Clear();
                    }
                    else
                    {
                        s.Steps.Enqueue((row.F, delta));
                    }
                }
            }
            if ((row.F & 1) == 0) s.LastYaw = yaw;
            Vector3 pos = row.Pos[i];
            s.Trail.Enqueue(pos.Xz);
            if (s.Trail.Count > 120) s.Trail.Dequeue();
            bool jump = (row.F1[i] & 0x100) != 0;
            if (jump && !s.WasJump && s.Trail.Count == 120 && (pos.Xz - s.Trail.Peek()).Length < 1.5f) s.Hops++;
            s.WasJump = jump;
            if (s.HoldFrom < 0 || (pos - s.HoldStart).Length > 2)
            {
                s.HoldFrom = row.F;
                s.HoldStart = pos;
            }
            else if (row.F - s.HoldFrom == 240)
            {
                s.Holds++;
            }
            if (row.Tss[i] == 0 && lastTss != 0 && lastTss >= 0)
            {
                s.Shots++;
                if (hasTarget && row.Hp[tp] > 0)
                {
                    Vector3 mid = row.Mid(tp), eye = row.Cam[i];
                    Vector3 to = mid - eye;
                    float dist = to.Length;
                    float err = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(face.Normalized(), to.Normalized()), -1, 1)));
                    bool seen = pr.Sees(i, tp);
                    (seen ? s.AimSeen : s.AimHidden).Add(err);
                    if (err <= MathHelper.RadiansToDegrees(MathF.Atan2(0.6f, dist))) s.OnTarget++;
                    s.Pending.Add((row.F, tp, row.Hp[tp]));
                    CollisionResult res = default;
                    if (CollisionDetection.CheckBetweenPoints(eye, mid, TestFlags.None, scene, ref res))
                    {
                        s.Walls++;
                        if (seen) s.WallsSeen++;
                    }
                }
            }
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.0} {v.Y:0.0} {v.Z:0.0})";
    }
}
