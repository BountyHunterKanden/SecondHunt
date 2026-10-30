using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // The Trooper's Wall Cling (docs/CHARACTERS_AND_SKINS.md D17, MphRead Players/PlayerWallCling.cs), headless:
    //   -wallclingtest [arena=Combat Hall] [time=150] [bots=1] [out=<dir>]
    // seat 0 is a local Trooper (Weavel + Character "trooper") driven by a script: walk forward until a wall stops him,
    // jump into it, then on alternate clings either hang on until he lets go (and keep pushing into the same wall while
    // he falls) or wall-jump after ~0.6 s (still holding forward, so he drifts back at the wall he left); after landing,
    // turn and look for another wall. Checks:
    //   - clings happen, at least one ends by timing out and one by a wall jump
    //   - nothing moves him while he clings; a timed-out cling lasted WallClingTime frames and he falls after it
    //   - a wall jump goes up and off the wall
    //   - he never clings to the same wall again before landing, nor more than WallClingChain times
    //   - every wall he clings to stands within the allowed angles (a face, -1 to +5 degrees off vertical)
    //   - the Trooper bot clings by the bots' wall cling of format 58 (PlayerAiWallCling.cs; its clings counted); botcling=off:
    //     bots never cling, as before format 58 (no replay check then: a format 58 file always plays with the bots' cling on)
    // then records the match, saves it, loads the file and replays it: every step's checksum must match.
    internal static class WallClingTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            float time = 150;
            int bots = 1;
            bool botCling = true;
            string? outDir = null;
            int trace = 0; // a cling number: print seat 0 every frame from its start to 60 frames after it ends
            int traceLeft = 0;
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                case "time": time = Single.Parse(value); break;
                case "bots": bots = Int32.Parse(value); break;
                case "botcling": botCling = value != "off"; break;
                case "out": outDir = value; break;
                case "trace": trace = Int32.Parse(value); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = time, BotWallCling = botCling };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            Hunter[] others = { Hunter.Samus, Hunter.Kanden, Hunter.Spire };
            for (int seat = 1; seat <= Math.Max(1, bots); seat++)
            {
                // seat 1 is a Trooper bot too (bots must never cling)
                bool trooper = seat == 1;
                settings.Players.Add(new MatchPlayer
                {
                    Hunter = trooper ? Characters.Trooper.Base : others[seat - 1],
                    Recolor = trooper ? 1 : 0,
                    Bot = true,
                    BotLevel = 0,
                    Character = trooper ? Characters.Trooper.Id : null
                });
            }
            Console.WriteLine($"Battle in {arena.InGameName}, {time} s: "
                + string.Join(", ", settings.Players.Select((p, i) => $"{i} {p.Hunter}{(p.Character != null ? " (TROOPER)" : "")}{(p.Bot ? " bot" : " local")}")));

            var rec = new ReplayRecorder(settings, "wallclingtest");
            CampaignHost host = CampaignHost.StartMatch(settings);
            rec.Started(host.Scene);
            long maxFrames = (long)(time * 60) + 600;
            var errors = new List<string>();
            void Fail(string what)
            {
                if (errors.Count < 20) errors.Add($"frame {host.Frame}: {what}");
            }

            // script state
            int pushFrames = 0; // walking into a wall while standing
            int walkFrames = 0; // walking without meeting a wall
            int turnLeft = 0; // frames of turning left to do
            int clingFrames = 0; // frames of the current cling
            int clingIndex = 0;
            bool wasClinging = false;
            bool landedSinceCling = true;
            Vector3 lastNormal = Vector3.Zero;
            Vector3 clingPos = Vector3.Zero, clingNormal = Vector3.Zero;
            int fallCheck = -1; // frames left to see him fall after a timeout
            float fallFromY = 0;
            int jumpCheck = -1; // frames left before checking a wall jump took him off the wall
            float jumpTop = 0;
            bool jumpCeiling = false; // that wall jump's rise was stopped by a ceiling on its first ticks
            float jumpSpeedY = 0;
            int ceilingJumps = 0;
            int timeouts = 0, wallJumps = 0, shotsWhile = 0, bumped = 0;
            Vector3 clingStart = Vector3.Zero;
            float settleMax = 0; // the most the push-out off the face moved him over a cling's first frames
            float driftMax = 0; // the most he drifted from where a cling started (other players' pushes left out)
            string driftWhere = "";
            bool driftFailed = false;
            var clingSteps = new List<string>();
            int chain = 0, maxChain = 0;
            float minNy = Single.MaxValue, maxNy = Single.MinValue;
            while (!host.Ended && host.Frame < maxFrames)
            {
                PlayerEntity me = PlayerEntity.Players[0];
                var input = new CampaignInput();
                bool even = host.Frame % 2 == 0;
                if (me.Health == 0)
                {
                    if (even) input.Buttons |= CampaignButtons.Shoot; // respawn
                    pushFrames = walkFrames = 0;
                }
                else if (me.WallClinging)
                {
                    bool jumpOff = clingIndex % 2 == 0;
                    input.Move.Y = 1; // keeps pushing into the wall
                    if (jumpOff && clingFrames >= 36 && even)
                    {
                        input.Buttons |= CampaignButtons.Jump;
                    }
                    if (clingFrames % 12 == 6)
                    {
                        input.Buttons |= CampaignButtons.Shoot;
                        shotsWhile++;
                    }
                }
                else if (turnLeft > 0)
                {
                    input.AimDelta = new System.Numerics.Vector2(40, 0); // ~10 degrees a frame
                    turnLeft--;
                }
                else if (fallCheck > 0 && fallCheck <= 20)
                {
                    // after timing out: pushing forward for 10 frames (the same wall must not take him again), then let go
                    // (holding into a leaning face, MphRead's own wall response slows any hunter's fall to a slide)
                }
                else
                {
                    input.Move.Y = 1;
                    bool standing = me.Flags1.TestFlag(PlayerFlags1.Standing);
                    if (standing && me.Flags1.TestFlag(PlayerFlags1.CollidingLateral))
                    {
                        pushFrames++;
                        walkFrames = 0;
                    }
                    else if (standing)
                    {
                        pushFrames = 0;
                        if (++walkFrames > 240)
                        {
                            turnLeft = 6;
                            walkFrames = 0;
                        }
                    }
                    if (pushFrames >= 6 && even)
                    {
                        input.Buttons |= CampaignButtons.Jump; // jump at the wall he's walking into
                    }
                    if (pushFrames > 90)
                    {
                        turnLeft = 9; // stuck on something he can't cling to (a low step, a corner): turn
                        pushFrames = 0;
                    }
                }

                Vector3 before = me.Position;
                host.Step(input);
                rec.Step(input, host.Scene);

                // checks
                bool clinging = me.WallClinging;
                if (me.Flags1.TestFlag(PlayerFlags1.Standing))
                {
                    if (!landedSinceCling && !clinging && !wasClinging && clingIndex > 0)
                    {
                        turnLeft = 10 + clingIndex * 3 % 9; // landed after a cling: look somewhere else
                    }
                    landedSinceCling = true;
                }
                if (clinging && !wasClinging)
                {
                    clingIndex++;
                    clingFrames = 1;
                    clingPos = me.Position;
                    clingStart = me.Position;
                    driftFailed = false;
                    clingNormal = me.WallClingNormal;
                    clingSteps.Add($"{rec.File.Steps.Count}");
                    if (!landedSinceCling && Vector3.Dot(clingNormal, lastNormal) > 0.7f)
                    {
                        Fail($"cling {clingIndex}: the same wall again before landing (dot {Vector3.Dot(clingNormal, lastNormal):0.00})");
                    }
                    if (MathF.Abs(clingNormal.Y) > 1e-4f || MathF.Abs(clingNormal.Length - 1) > 1e-3f)
                    {
                        Fail($"cling {clingIndex}: wall normal {clingNormal} isn't a horizontal unit vector");
                    }
                    lastNormal = clingNormal;
                    if (landedSinceCling)
                    {
                        chain = 0;
                    }
                    chain++;
                    maxChain = Math.Max(maxChain, chain);
                    if (chain > me.WallClingChainLimit)
                    {
                        Fail($"cling {clingIndex}: {chain} clings without landing (limit {me.WallClingChainLimit})");
                    }
                    float ny = me.WallClingNormalY;
                    minNy = MathF.Min(minNy, ny);
                    maxNy = MathF.Max(maxNy, ny);
                    if (ny < PlayerEntity.WallClingMinNormalY || ny > PlayerEntity.WallClingMaxNormalY)
                    {
                        Fail($"cling {clingIndex}: wall normal.Y {ny:0.0000} outside the allowed angles");
                    }
                    landedSinceCling = false;
                }
                else if (clinging)
                {
                    clingFrames++;
                    // nothing moves him on the wall, except the room collision easing him out of the face over his first
                    // frames (MphRead's push-out against a sloped edge: ~1 cm in all) and other players' bodies pushing him
                    float near = Single.MaxValue;
                    for (int i = 1; i < settings.Players.Count; i++)
                    {
                        near = MathF.Min(near, (PlayerEntity.Players[i].Position - me.Position).Length);
                    }
                    if (near < 2.5f)
                    {
                        if ((me.Position - clingPos).Length > 1e-3f)
                        {
                            bumped++;
                        }
                        clingStart += me.Position - clingPos; // a push doesn't count as drift
                    }
                    if (clingFrames <= 4)
                    {
                        // the push-out off the face settles over the first frames (up to a few cm against a sloped edge)
                        settleMax = MathF.Max(settleMax, (me.Position - clingStart).Length);
                        clingStart = me.Position;
                    }
                    float drift = (me.Position - clingStart).Length;
                    if (drift > driftMax)
                    {
                        driftMax = drift;
                        driftWhere = $"cling {clingIndex} frame {clingFrames}";
                    }
                    if (drift > 0.03f && !driftFailed)
                    {
                        Fail($"cling {clingIndex}: drifted {drift:0.000} while clinging (nearest player {near:0.00} away)");
                        driftFailed = true;
                    }
                    clingPos = me.Position;
                }
                else if (wasClinging)
                {
                    if (me.WallJumps > wallJumps)
                    {
                        wallJumps = me.WallJumps;
                        jumpCheck = 12;
                        jumpTop = me.Position.Y;
                        // gravity is held on the wall jump's first tick, so a rise gone at once is a ceiling over the wall (the hold
                        // rule's later clings can land him under a ledge): the push off is still checked, the rise isn't
                        jumpCeiling = MathF.Abs(me.Speed.Y) < 0.01f;
                        jumpSpeedY = me.Speed.Y;
                        if (jumpCeiling)
                        {
                            ceilingJumps++;
                        }
                        else if (me.Speed.Y <= 0)
                        {
                            Fail($"cling {clingIndex}: wall jump without upward speed ({me.Speed.Y:0.000})");
                        }
                    }
                    else if (me.WallClingTimeouts > timeouts)
                    {
                        timeouts = me.WallClingTimeouts;
                        if (Math.Abs(clingFrames - PlayerEntity.WallClingTime) > 1)
                        {
                            Fail($"cling {clingIndex}: timed out after {clingFrames} frames (want {PlayerEntity.WallClingTime})");
                        }
                        fallCheck = 30;
                        fallFromY = me.Position.Y;
                    }
                    clingSteps[^1] += $"-{rec.File.Steps.Count - 1}({(me.WallJumps == wallJumps && jumpCheck == 12 ? "jump" : me.Health == 0 ? "died" : clingFrames >= PlayerEntity.WallClingTime - 1 ? "timeout" : "drop")})";
                }
                if (fallCheck > 0 && me.Flags1.TestFlag(PlayerFlags1.Grounded))
                {
                    fallCheck = -1; // landed
                }
                // let go: he falls, or (pushing on into a low block's top edge) MphRead lifts him over it; either way he's
                // no longer held where the cling was
                if (fallCheck > 0 && --fallCheck == 0 && me.Position.Y >= fallFromY - 0.15f && !me.WallClinging && me.Health > 0
                    && (me.Position - clingPos).Length < 0.15f)
                {
                    Fail($"cling {clingIndex}: didn't fall after timing out (y {fallFromY:0.00} -> {me.Position.Y:0.00}, speed {me.Speed}, "
                        + $"health {me.Health}, grounded {me.Flags1.TestFlag(PlayerFlags1.Grounded)}, lateral {me.Flags1.TestFlag(PlayerFlags1.CollidingLateral)})");
                }
                if (jumpCheck > 0)
                {
                    jumpTop = MathF.Max(jumpTop, me.Position.Y);
                    // a rise cut by far more than gravity a tick, in the jump's first ticks: a ceiling over the wall too
                    if (!jumpCeiling && jumpCheck >= 9 && jumpSpeedY - me.Speed.Y > 0.1f)
                    {
                        jumpCeiling = true;
                        ceilingJumps++;
                    }
                    jumpSpeedY = me.Speed.Y;
                }
                if (jumpCheck > 0 && --jumpCheck == 0 && me.Health > 0)
                {
                    // off the wall 12 frames later; up: his highest point since (a low ceiling can stop the rise early)
                    float off = Vector3.Dot(me.Position - clingPos, clingNormal);
                    float up = jumpTop - clingPos.Y;
                    // (another Trooper's standing body (Weavel's turret sphere) next to the wall blocks the jump: not the cling's fault)
                    bool bodyInTheWay = PlayerEntity.Players.Any(p => p != me && p.IsAltForm && p.Halfturret is HalfturretEntity t
                        && Vector3.Distance(t.Position, clingPos) < 1.5f);
                    if ((off < 0.2f || up < 0.2f && !jumpCeiling) && !bodyInTheWay)
                    {
                        Fail($"cling {clingIndex}: 12 frames after the wall jump he's {off:0.00} off the wall, at most {up:0.00} up");
                    }
                }
                if (trace > 0 && clingIndex == trace && (clinging || wasClinging))
                {
                    traceLeft = 60;
                }
                if (traceLeft > 0)
                {
                    traceLeft--;
                    Console.WriteLine($"  trace {host.Frame}: pos {me.Position} speed {me.Speed} cling {clinging} standing {me.Flags1.TestFlag(PlayerFlags1.Standing)} "
                        + $"grounded {me.Flags1.TestFlag(PlayerFlags1.Grounded)} lateral {me.Flags1.TestFlag(PlayerFlags1.CollidingLateral)} move {input.Move.Y} buttons {input.Buttons}"
                        + (PlayerEntity.Players.Count > 1 ? $" | seat 1 {PlayerEntity.Players[1].Position} alt {PlayerEntity.Players[1].IsAltForm} body {(PlayerEntity.Players[1].Halfturret is HalfturretEntity t && PlayerEntity.Players[1].IsAltForm ? t.Position.ToString() : "-")}" : ""));
                }
                wasClinging = clinging;
                for (int i = 1; i < settings.Players.Count; i++)
                {
                    if (PlayerEntity.Players[i].WallClinging && !botCling)
                    {
                        Fail($"bot seat {i} clings");
                    }
                }
            }
            rec.End(host.Ended);
            PlayerEntity p0 = PlayerEntity.Players[0];
            Console.WriteLine($"recorded {rec.File.Steps.Count} steps (ended {host.Ended}); seat 0: clings {p0.WallClings}, wall jumps {p0.WallJumps}, "
                + $"timeouts {p0.WallClingTimeouts}, drops {p0.WallClingDrops}, longest {p0.WallClingLongest} frames, shots pressed while clinging {shotsWhile}, "
                + $"deaths {GameState.Deaths[0]}; frames pushed by another player while clinging {bumped}, most drift on a wall {driftMax * 1000:0.0} mm ({driftWhere}), "
                + $"most push-out off the face on a cling's first frames {settleMax * 1000:0.0} mm");
            Console.WriteLine($"cling steps: {string.Join(" ", clingSteps.Take(24))}");
            Console.WriteLine($"clings refused for a wall shorter than him: {p0.WallClingTooShort}");
            Console.WriteLine($"wall jumps whose rise a ceiling stopped at once: {ceilingJumps}");
            Console.WriteLine($"most clings in one airtime {maxChain} (limit {PlayerEntity.WallClingChain}); wall normal.Y {minNy:0.0000} .. {maxNy:0.0000}"
                + $" ({MathF.Asin(Math.Clamp(minNy, -1, 1)) * 180 / MathF.PI:0.0} .. {MathF.Asin(Math.Clamp(maxNy, -1, 1)) * 180 / MathF.PI:0.0} degrees)");
            for (int i = 1; i < settings.Players.Count; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                Console.WriteLine($"  seat {i} {p.Hunter,-7} wall cling {(p.HostWallCling ? "yes" : "no "),-3} clings {p.WallClings}");
            }

            string dir = outDir ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "characters_mockups", "wallclingtest"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "wallclingtest.mphreplay");
            rec.File.Save(path);
            ReplayFile file = ReplayFile.Load(path);
            string? why = botCling ? ReplayPlayer.CheckBeforeStart(file) : "not checked (botcling=off: a format 58 file plays with the bots' cling on)";
            bool exact = !botCling;
            int checkedSteps = 0;
            if (botCling && why == null)
            {
                var player = new ReplayPlayer(file);
                CampaignHost replay = player.Start();
                why = player.CheckAfterStart();
                while (why == null && !replay.Ended && player.TryNext(out CampaignInput input))
                {
                    replay.Step(input);
                    if (!player.Check(replay.Scene))
                    {
                        why = player.Failure ?? $"failed at step {player.FailedAt}";
                        break;
                    }
                    checkedSteps++;
                }
                exact = why == null && checkedSteps == file.Steps.Count;
                if (why == null && !exact)
                {
                    why = $"stopped after {checkedSteps} of {file.Steps.Count} steps";
                }
            }
            Console.WriteLine($"replay file {path}: format {ReplayFile.FormatVersion}; " + (!botCling ? why : exact ? $"EXACT ({checkedSteps} steps)" : $"NOT EXACT: {why}"));
            foreach (string e in errors)
            {
                Console.WriteLine($"  WRONG {e}");
            }
            bool pass = errors.Count == 0 && p0.HostWallCling && p0.WallClings >= 2 && p0.WallJumps >= 1 && p0.WallClingTimeouts >= 1 && exact;
            Console.WriteLine(pass ? "wallclingtest PASS" : "wallclingtest FAIL");
            Environment.ExitCode = pass ? 0 : 1;
        }
    }
}
