using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Accidental Wall Clings (release test notes N10, owner: the Trooper "walks" into a cling where the floor/wall geometry
    // is busy, e.g. Fault Line), headless:
    //   -wallclingprobe [arena=Fault Line] [time=600] [mode=walk|jump|both] [seed=1] [check=1]
    // walk: a lone local Trooper wanders (forward/strafe, random turns, never presses Jump) and every cling is accidental;
    // jump: he jumps at each wall he walks into (as -wallclingtest) and every cling is wanted. Prints each cling's air frames
    // (frames since he last stood) and whether he went up (jumped) before it, so the rule against accidental clings can be
    // picked from data. check=1: FAIL if a walk cling happens or a jump run gets no clings.
    internal static class WallClingProbe
    {
        public static void Run(string[] args)
        {
            string arenaName = "Fault Line";
            float time = 600;
            string mode = "both";
            int seed = 1;
            bool check = false;
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                case "time": time = Single.Parse(value); break;
                case "mode": mode = value.ToLowerInvariant(); break;
                case "seed": seed = Int32.Parse(value); break;
                case "check": check = value != "0"; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var arenas = arenaName.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? MatchArenas.All.Where(a => MatchArenas.Supports(a, GameMode.Battle)).Select(a => a.Name).Distinct().ToList()
                : new List<string> { (MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}")).Name };
            int fails = 0;
            var walkAir = new List<int>();
            var jumpAir = new List<int>();
            int walkClings = 0, jumpClings = 0, jumpRose = 0, walkRose = 0;
            foreach (string name in arenas)
            {
                foreach (string m in mode == "both" ? new[] { "walk", "jump" } : new[] { mode })
                {
                    List<(int Air, bool Rose, Vector3 Pos, float Drop)> clings = RunOne(name, time, m == "jump", seed);
                    var air = clings.Select(c => c.Air).ToList();
                    Console.WriteLine($"{name} {m}: {clings.Count} clings"
                        + (clings.Count > 0 ? $", air frames min {air.Min()} median {air.OrderBy(a => a).ElementAt(air.Count / 2)} max {air.Max()},"
                        + $" rose {clings.Count(c => c.Rose)}/{clings.Count}" : ""));
                    foreach ((int a, bool rose, Vector3 pos, float drop) in clings.Where(c => !c.Rose).Take(40))
                    {
                        Console.WriteLine($"    air {a,4} {(rose ? "jumped" : "fell  ")} drop {drop:0.00} at ({pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00})");
                    }
                    if (m == "walk")
                    {
                        walkClings += clings.Count;
                        walkRose += clings.Count(c => c.Rose);
                        walkAir.AddRange(air);
                        // the rule (T5 N10): a cling needs a jump, or a real fall (WallClingMinDrop) like walking off a ledge
                        int shortFalls = clings.Count(c => !c.Rose && c.Drop < PlayerEntity.WallClingMinDrop - 0.001f);
                        if (check && shortFalls > 0)
                        {
                            Console.WriteLine($"FAIL: {name}: {shortFalls} clings without a jump on a fall under {PlayerEntity.WallClingMinDrop:0.0}");
                            fails++;
                        }
                    }
                    else
                    {
                        jumpClings += clings.Count;
                        jumpRose += clings.Count(c => c.Rose);
                        jumpAir.AddRange(air);
                        if (check && clings.Count == 0)
                        {
                            Console.WriteLine($"FAIL: {name}: jumping at walls never clung");
                            fails++;
                        }
                    }
                }
            }
            string Hist(List<int> v)
            {
                int[] edges = { 0, 16, 20, 24, 30, 40, 60, 90, 10001 };
                return string.Join(" ", Enumerable.Range(0, edges.Length - 1)
                    .Select(i => $"[{edges[i]}-{edges[i + 1] - 1}]:{v.Count(a => a >= edges[i] && a < edges[i + 1])}"));
            }
            Console.WriteLine($"ALL walk: {walkClings} clings (rose {walkRose}) air {Hist(walkAir)}");
            Console.WriteLine($"ALL jump: {jumpClings} clings (rose {jumpRose}) air {Hist(jumpAir)}");
            Console.WriteLine(check ? (fails == 0 ? "PASS" : $"FAIL ({fails})") : "done");
        }

        private static List<(int, bool, Vector3, float)> RunOne(string arenaName, float time, bool jump, int seed)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arenaName, TimeLimitSeconds = time, Solo = true };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            CampaignHost host = CampaignHost.StartMatch(settings);
            var rng = new Random(seed);
            var clings = new List<(int, bool, Vector3, float)>();
            long maxFrames = (long)(time * 60);
            int pushFrames = 0, turnLeft = 0, turnDir = 1, walkFrames = 0, strafe = 0, strafeFrames = 0;
            bool was = false;
            while (!host.Ended && host.Frame < maxFrames)
            {
                PlayerEntity me = PlayerEntity.Players[0];
                var input = new CampaignInput();
                bool even = host.Frame % 2 == 0;
                if (me.Health == 0)
                {
                    if (even) input.Buttons |= CampaignButtons.Shoot;
                }
                else if (me.WallClinging)
                {
                    // let go at once (drop by morphing): only the start matters here
                    if (even) input.Buttons |= CampaignButtons.Morph;
                }
                else if (me.IsAltForm)
                {
                    if (even) input.Buttons |= CampaignButtons.Morph;
                }
                else if (turnLeft > 0)
                {
                    input.AimDelta = new System.Numerics.Vector2(40 * turnDir, 0);
                    input.Move.Y = 1;
                    turnLeft--;
                }
                else
                {
                    input.Move.Y = 1;
                    if (strafeFrames > 0)
                    {
                        input.Move.X = strafe;
                        strafeFrames--;
                    }
                    else if (rng.Next(90) == 0)
                    {
                        strafe = rng.Next(2) == 0 ? -1 : 1;
                        strafeFrames = 20 + rng.Next(60);
                    }
                    bool standing = me.Flags1.TestFlag(PlayerFlags1.Standing);
                    if (me.Flags1.TestFlag(PlayerFlags1.CollidingLateral))
                    {
                        pushFrames++;
                    }
                    else
                    {
                        pushFrames = 0;
                    }
                    if (jump && standing && pushFrames >= 6 && even)
                    {
                        input.Buttons |= CampaignButtons.Jump;
                    }
                    if (pushFrames > (jump ? 90 : 30) || ++walkFrames > 120 + rng.Next(240))
                    {
                        turnLeft = 3 + rng.Next(14);
                        turnDir = rng.Next(2) == 0 ? -1 : 1;
                        pushFrames = 0;
                        walkFrames = 0;
                    }
                }
                host.Step(input);
                bool now = me.WallClinging;
                if (now && !was)
                {
                    clings.Add((me.WallClingStartAir, me.WallClingStartRose, me.Position, me.WallClingStartDrop));
                }
                was = now;
            }
            host.Dispose();
            return clings;
        }
    }
}
