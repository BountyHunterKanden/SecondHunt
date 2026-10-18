using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // The Trooper's Scout Drone (docs/CHARACTERS_AND_SKINS.md D10/D10a, MphRead Players/PlayerScoutDrone.cs), headless:
    //   -dronetest [arena=Combat Hall] [time=120] [bots=2] [out=<dir>] [lookback=<frames>] [force=1: PlayerEntity.HostDroneTest]
    // seat 0 is a local Trooper (Weavel + Character "trooper") driven by a script, over and over: split (Morph), hold
    // Jump and fly forward firing the blasters, let go and sink, wander a little, merge (Morph). Seat 1 is a Trooper bot.
    // Checks:
    //   - a split leaves half his health in the standing body; a merge gives it back (capped at his max)
    //   - while hovering the drone is never more than his head height above the floor under it, and it does rise near it
    //   - letting go of Jump brings it back down to the floor
    //   - the blasters fire (shots spawned, owned by seat 0) and no trooper's standing body ever fires
    //   - the hold-to-fire burst (every drone seat): no shot before the unfold has finished (32 frames) or outside the
    //     firing state, at most 38 shots a burst, a full burst's shots worth 94 (x2 with Double Damage), no new unfold
    //     sooner than 120 frames after a fold ends (the wait ticks from the next frame: a held trigger unfolds on the
    //     120th), both guns used
    //   - the cutting laser (every other cycle the script presses Zoom, L2, instead of fire; bots pick it themselves):
    //     134 frames, damage only in frames 57..116, at most 150 a laser, never a burst during a laser, no laser sooner
    //     than 600 frames after a laser ends (its own wait; a burst may follow at once); prints what each laser dealt and what one would deal a body standing still
    // then records the match, saves it, loads the file and replays it: every step's checksum must match.
    internal static class DroneTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            float time = 120;
            int bots = 2;
            string? outDir = null;
            long traceFrom = -1; // print seat 0 every frame from this frame for 60 frames
            int lookBack = 0; // pictures: after the split, back away for this many frames and hover (the camera faces the body)
            // rules=off: the release-test T5 rules (wall cling jump rule, drone pad arc, hover walk) off; rules=cling / pad: only
            // that one on (A/B; the replay check then fails: these settings aren't written)
            string t5Rules = "on";
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
                case "out": outDir = value; break;
                case "trace": traceFrom = Int64.Parse(value); break;
                case "lookback": lookBack = Int32.Parse(value); break;
                case "rules": t5Rules = value; break;
                case "force": PlayerEntity.HostDroneTest = value == "1"; break; // the launcher's Drone kill test (--es dronetest 1)
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Characters.ScoutDroneOn = true;
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = time,
                WallClingJumpRule = t5Rules is "on" or "cling", DronePadArc = t5Rules is "on" or "pad", HoverWalk = t5Rules == "on" };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            Hunter[] others = { Hunter.Samus, Hunter.Kanden, Hunter.Spire };
            for (int seat = 1; seat <= Math.Max(1, bots); seat++)
            {
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

            var rec = new ReplayRecorder(settings, "dronetest");
            CampaignHost host = CampaignHost.StartMatch(settings);
            rec.Started(host.Scene);
            long maxFrames = (long)(time * 60) + 600;
            var errors = new List<string>();
            void Fail(string what)
            {
                if (errors.Count < 20) errors.Add($"frame {host.Frame}: {what}");
            }

            int count = settings.Players.Count;
            var wasSplit = new bool[count];
            var bodyAtSplit = new int[count];
            var lastDrone = new int[count];
            var lastBody = new int[count];
            int phase = 0, phaseFrames = 0, cycles = 0;
            float maxGap = 0, capSeen = 0;
            int hoverFrames = 0, landedAfterSink = 0, sinks = 0, bodyShots = 0, merges = 0, splitsChecked = 0;
            var seenBeams = new HashSet<BeamProjectileEntity>();
            int overFrames = 0, overMax = 0;
            bool resting = false;
            float restY = Single.MaxValue;
            // the burst, per seat
            var prevState = new int[count];
            var prevShots = new int[count];
            var prevBursts = new int[count];
            var burstStart = new long[count];
            var burstShots = new int[count];
            var burstDamage = new int[count];
            var burstSeen = new int[count]; // its shots seen in the scene (one that hits on its spawn frame isn't)
            var foldEnd = Enumerable.Repeat(-1L, count).ToArray();
            var fullBursts = new int[count];
            var bursts = new int[count];
            int shortestWait = Int32.MaxValue;
            int fullDamage = -1;
            var shotDamage = new SortedDictionary<int, int>(); // drone shots seen, by damage
            var seenDrone = new HashSet<BeamProjectileEntity>();
            // the laser, per seat
            var prevLaserFrame = new int[count];
            var prevLasers = new int[count];
            var prevDealt = new int[count];
            var laserStart = new long[count];
            var laserEnd = Enumerable.Repeat(-1L, count).ToArray();
            var lasers = new int[count];
            var fullLasers = new int[count];
            var laserDealt = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                laserDealt[i] = new List<int>();
            }
            int shortestLaserWait = Int32.MaxValue;
            while (!host.Ended && host.Frame < maxFrames)
            {
                PlayerEntity me = PlayerEntity.Players[0];
                var input = new CampaignInput();
                bool even = host.Frame % 2 == 0;
                phaseFrames++;
                if (me.Health == 0)
                {
                    if (even) input.Buttons |= CampaignButtons.Shoot; // respawn
                    phase = 0;
                    phaseFrames = 0;
                }
                else if (phase == 0)
                {
                    // as a biped: walk a bit, then split
                    input.Move.Y = 1;
                    if (phaseFrames == 50)
                    {
                        input.Buttons |= CampaignButtons.Morph;
                    }
                    if (me.IsAltForm && phaseFrames > 50)
                    {
                        phase = 1;
                        phaseFrames = 0;
                    }
                    else if (phaseFrames > 120)
                    {
                        phaseFrames = 0; // the morph didn't take (mid-air?): again
                    }
                }
                else if (phase == 1)
                {
                    // rise, fly forward, fire (every other cycle the laser)
                    input.Buttons |= CampaignButtons.Jump | (cycles % 2 == 1 ? CampaignButtons.Zoom : CampaignButtons.AltAttack);
                    input.Move.Y = 1;
                    input.AimDelta = new System.Numerics.Vector2(phaseFrames % 90 < 45 ? 6 : -6, 0);
                    if (lookBack > 0)
                    {
                        input.Move.Y = phaseFrames < lookBack ? -1 : 0; // back away: the camera faces the body
                        input.AimDelta = default;
                    }
                    if (phaseFrames >= 150)
                    {
                        phase = 2;
                        phaseFrames = 0;
                        sinks++;
                    }
                }
                else if (phase == 2)
                {
                    // let go: sink back to the floor, still firing
                    input.Buttons |= CampaignButtons.AltAttack;
                    // down: on the floor, or resting on a ledge's edge (MphRead calls an edge contact lateral, not standing)
                    if (phaseFrames % 30 == 0)
                    {
                        resting = MathF.Abs(me.Position.Y - restY) < 0.01f && phaseFrames > 0;
                        restY = me.Position.Y;
                    }
                    if (me.Flags1.TestFlag(PlayerFlags1.Standing) || resting)
                    {
                        resting = false;
                        restY = Single.MaxValue;
                        landedAfterSink++;
                        phase = 3;
                        phaseFrames = 0;
                    }
                    else if (phaseFrames > 400)
                    {
                        Fail($"the drone didn't come down within 400 frames of letting go (gap {me.DroneFloorGap:0.00})");
                        phase = 3;
                        phaseFrames = 0;
                    }
                }
                else if (phase == 3)
                {
                    // walk on the floor, then merge
                    input.Move.Y = 1;
                    if (phaseFrames == 40)
                    {
                        input.Buttons |= CampaignButtons.Morph;
                    }
                    if (!me.IsAltForm && !me.IsMorphing && phaseFrames > 40)
                    {
                        phase = 0;
                        phaseFrames = 0;
                        cycles++;
                    }
                    else if (phaseFrames > 140)
                    {
                        phaseFrames = 30;
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    lastDrone[i] = p.Health;
                    lastBody[i] = p.Halfturret?.Health ?? 0;
                    prevState[i] = p.DroneFireState;
                    prevShots[i] = p.DroneShots;
                    prevBursts[i] = p.DroneBursts;
                    prevLaserFrame[i] = p.DroneLaserFrame;
                    prevLasers[i] = p.DroneLasers;
                    prevDealt[i] = p.DroneLaserDealt;
                }
                host.Step(input);
                rec.Step(input, host.Scene);

                // the burst
                foreach (EntityBase e in host.Scene.Entities)
                {
                    // first seen while its owner flies the drone (a biped's shot still in flight after the split isn't)
                    if (e is BeamProjectileEntity b && seenDrone.Add(b) && b.Owner is PlayerEntity o && o.HostScoutDrone && o.IsAltForm)
                    {
                        burstDamage[o.SlotIndex] += (int)b.Damage;
                        burstSeen[o.SlotIndex]++;
                        shotDamage[(int)b.Damage] = shotDamage.GetValueOrDefault((int)b.Damage) + 1;
                        // 2 / 3 by turns; doubled 4 / 6; Prime Hunter 3 / 4
                        if ((int)b.Damage is not (2 or 3 or 4 or 6))
                        {
                            Fail($"seat {o.SlotIndex} drone shot worth {b.Damage}");
                        }
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (!p.HostScoutDrone)
                    {
                        continue;
                    }
                    if (p.Health == 0)
                    {
                        foldEnd[i] = -1; // a respawn starts with no wait
                        laserEnd[i] = -1;
                        laserStart[i] = -1; // died mid-laser: cut short, not timed
                    }
                    if (p.DroneLaserFrame >= 0 && p.DroneFireState != 0)
                    {
                        Fail($"seat {i} burst state {p.DroneFireState} during a laser");
                    }
                    if (p.DroneLasers > prevLasers[i])
                    {
                        if (prevState[i] != 0)
                        {
                            Fail($"seat {i} laser started in burst state {prevState[i]}");
                        }
                        if (laserEnd[i] >= 0)
                        {
                            int wait = (int)(host.Frame - laserEnd[i]);
                            shortestLaserWait = Math.Min(shortestLaserWait, wait);
                            if (wait < 600)
                            {
                                Fail($"seat {i} laser again {wait} frames after a laser ended");
                            }
                        }
                        lasers[i]++;
                        laserStart[i] = host.Frame;
                    }
                    if (p.DroneLaserDealt > prevDealt[i] && p.DroneLasers == prevLasers[i]
                        && (prevLaserFrame[i] < 57 || prevLaserFrame[i] > 116))
                    {
                        Fail($"seat {i} laser damage at laser frame {prevLaserFrame[i]}");
                    }
                    if (prevLaserFrame[i] >= 0 && p.DroneLaserFrame < 0)
                    {
                        int length = (int)(host.Frame - laserStart[i]);
                        if (laserStart[i] >= 0 && length > 134)
                        {
                            Fail($"seat {i} laser ran {length} frames");
                        }
                        if (laserStart[i] >= 0 && length == 134)
                        {
                            fullLasers[i]++;
                        }
                        if (p.DroneLaserDealt > 150)
                        {
                            Fail($"seat {i} laser dealt {p.DroneLaserDealt}");
                        }
                        laserDealt[i].Add(p.DroneLaserDealt);
                        laserEnd[i] = p.Health > 0 ? host.Frame : -1;
                    }
                    if (p.DroneBursts > prevBursts[i])
                    {
                        if (foldEnd[i] >= 0)
                        {
                            int wait = (int)(host.Frame - foldEnd[i]);
                            shortestWait = Math.Min(shortestWait, wait);
                            if (wait < 120)
                            {
                                Fail($"seat {i} unfolded again {wait} frames after a fold ended");
                            }
                        }
                        bursts[i]++;
                        burstStart[i] = host.Frame;
                        burstShots[i] = 0;
                        burstDamage[i] = 0;
                        burstSeen[i] = 0;
                    }
                    int shots = p.DroneShots - prevShots[i];
                    if (shots > 0)
                    {
                        if (prevState[i] != 2)
                        {
                            Fail($"seat {i} shot in burst state {prevState[i]}");
                        }
                        if (host.Frame - burstStart[i] <= 32)
                        {
                            Fail($"seat {i} shot {host.Frame - burstStart[i]} frames into a burst (unfold 32)");
                        }
                        burstShots[i] += shots;
                    }
                    if (prevState[i] != 0 && p.DroneFireState == 0)
                    {
                        if (burstShots[i] > 38)
                        {
                            Fail($"seat {i} burst of {burstShots[i]} shots");
                        }
                        if (burstShots[i] == 38)
                        {
                            fullBursts[i]++;
                            fullDamage = burstSeen[i] == 38 ? burstDamage[i] : fullDamage;
                            // 94; 188 with Double Damage; 132 as the Prime Hunter (150%, rounded down per shot)
                            if (burstSeen[i] == 38 && burstDamage[i] != 94 && burstDamage[i] != 188 && burstDamage[i] != 132)
                            {
                                Fail($"seat {i} full burst worth {burstDamage[i]} (want 94; 188 doubled, 132 Prime Hunter)");
                            }
                        }
                        foldEnd[i] = p.Health > 0 ? host.Frame : -1;
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    bool split = p.Flags2.TestFlag(PlayerFlags2.Halfturret);
                    if (split && !wasSplit[i] && p.HostScoutDrone)
                    {
                        int before = lastDrone[i];
                        int body = p.Halfturret.Health;
                        bodyAtSplit[i] = body;
                        int want = before > 1 ? before / 2 : 1;
                        // the split frame may also take damage: the body's share must be half of what he had, the drone the rest
                        if (Math.Abs(body - want) > 0 || p.Health > before - (before > 1 ? body : 0))
                        {
                            Fail($"seat {i} split: had {before}, body {body} (want {want}), drone {p.Health}");
                        }
                        splitsChecked++;
                    }
                    else if (!split && wasSplit[i] && p.Health > 0 && !p.IsAltForm)
                    {
                        // merged (not the body dying): health = drone + body, capped
                        int want = Math.Min(lastDrone[i] + lastBody[i], p.HealthMax);
                        // (a hit on the merge frame takes its share: those frames aren't checked)
                        if (lastBody[i] > 0 && Math.Abs(p.Health - want) > 2 && !p.HostDamageFlash)
                        {
                            Fail($"seat {i} merge: drone {lastDrone[i]} + body {lastBody[i]} -> {p.Health} (want {want})");
                        }
                        merges++;
                    }
                    wasSplit[i] = split;
                }
                if (traceFrom >= 0 && host.Frame >= traceFrom && host.Frame < traceFrom + 60)
                {
                    Console.WriteLine($"  trace {host.Frame}: phase {phase} pos {me.Position} speed {me.Speed} gap {me.DroneFloorGap:0.000} alt {me.IsAltForm} "
                        + $"standing {me.Flags1.TestFlag(PlayerFlags1.Standing)} lateral {me.Flags1.TestFlag(PlayerFlags1.CollidingLateral)} buttons {input.Buttons}");
                }
                if (me.IsAltForm && me.Health > 0 && !me.Flags1.TestFlag(PlayerFlags1.Standing) && me.DroneFloorGap >= 0)
                {
                    hoverFrames++;
                    capSeen = me.DroneCap;
                    maxGap = MathF.Max(maxGap, me.DroneFloorGap);
                    // over the cap it never rises (the probe is from before this frame's move: one frame's rise allowed),
                    // and it's back under it soon (flown off a ledge: the floor dropped away under it)
                    if (me.DroneFloorGap > me.DroneCap + 0.06f)
                    {
                        overFrames++;
                        if (me.Speed.Y > 0.005f && overFrames > 2)
                        {
                            Fail($"the drone rises at {me.DroneFloorGap:0.00} above the floor (cap {me.DroneCap:0.00})");
                        }
                        if (overFrames == 60)
                        {
                            Fail($"the drone has been over the cap for 60 frames ({me.DroneFloorGap:0.00}, cap {me.DroneCap:0.00})");
                        }
                        overMax = Math.Max(overMax, overFrames);
                    }
                    else
                    {
                        overFrames = 0;
                    }
                }
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is BeamProjectileEntity b && b.Owner is HalfturretEntity t && t.Owner.HostScoutDrone && seenBeams.Add(b))
                    {
                        bodyShots++;
                        Fail($"seat {t.Owner.SlotIndex}'s standing body fired");
                    }
                }
            }
            rec.End(host.Ended);
            PlayerEntity p0 = PlayerEntity.Players[0];
            Console.WriteLine($"recorded {rec.File.Steps.Count} steps (ended {host.Ended}); seat 0: splits {p0.DroneSplits}, cycles {cycles}, "
                + $"drone shots {p0.DroneShots}, kills {GameState.Kills[0]}, deaths {GameState.Deaths[0]}; hovering frames {hoverFrames}, "
                + $"highest {maxGap:0.00} of cap {capSeen:0.00}; sinks {sinks}, landed after a sink {landedAfterSink}; splits checked {splitsChecked}, merges {merges}; "
                + $"standing-body shots {bodyShots}; longest over the cap {overMax} frames");
            Console.WriteLine($"  burst seat 0: bursts {bursts[0]} (full {fullBursts[0]}), shots left/right {p0.DroneShotsBySide[0]}/{p0.DroneShotsBySide[1]}, "
                + $"muzzles pulled back from a wall {p0.DroneMuzzleClamps}, shortest wait after a fold {(shortestWait == Int32.MaxValue ? "-" : shortestWait.ToString())} frames, last full burst worth {fullDamage}; shots seen by damage "
                + string.Join(", ", shotDamage.Select(kv => $"{kv.Key}: {kv.Value}")));
            for (int i = 1; i < count; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                Console.WriteLine($"  seat {i} {p.Hunter,-7} scout drone {(p.HostScoutDrone ? "yes" : "no "),-3} splits {p.DroneSplits} shots {p.DroneShots} "
                    + $"bursts {bursts[i]} (full {fullBursts[i]}) lasers {lasers[i]} (full {fullLasers[i]}) kills {GameState.Kills[i]}");
            }
            for (int i = 0; i < count; i++)
            {
                if (lasers[i] > 0)
                {
                    Console.WriteLine($"  laser seat {i}: {lasers[i]} (full {fullLasers[i]}), dealt per laser [{string.Join(", ", laserDealt[i])}], "
                        + $"shortest wait after a laser {(shortestLaserWait == Int32.MaxValue ? "-" : shortestLaserWait.ToString())} frames");
                }
            }
            // what one laser deals a body standing still (Prime's rule: aimed at it), by distance and sweep
            PlayerEntity? still = PlayerEntity.Players.Take(count).FirstOrDefault(p => !p.HostScoutDrone && p.Health > 0 && !p.IsAltForm);
            if (still != null)
            {
                foreach (float d in new[] { 4f, 8f, 12f, 20f, 30f })
                {
                    Vector3 from = still.Position + new Vector3(d, 1.2f, 0);
                    int[] dealt = Enumerable.Range(0, 4).Select(s => PlayerEntity.DroneLaserStillTarget(still, from, s)).ToArray();
                    Console.WriteLine($"  laser on a still {still.Hunter} {d} units away: sweeps 0-3 deal {string.Join(" / ", dealt)}");
                }
            }

            string dir = outDir ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "characters_mockups", "dronetest"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "dronetest.mphreplay");
            rec.File.Save(path);
            ReplayFile file = ReplayFile.Load(path);
            string? why = ReplayPlayer.CheckBeforeStart(file);
            bool exact = false;
            int checkedSteps = 0;
            if (why == null)
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
            Console.WriteLine($"replay file {path}: format {ReplayFile.FormatVersion}; " + (exact ? $"EXACT ({checkedSteps} steps)" : $"NOT EXACT: {why}"));
            foreach (string e in errors)
            {
                Console.WriteLine($"  WRONG {e}");
            }
            bool pass = errors.Count == 0 && p0.HostScoutDrone && p0.DroneSplits >= 2 && merges >= 2 && p0.DroneShots > 0
                && maxGap >= capSeen * 0.6f && landedAfterSink >= 1 && exact
                && fullBursts[0] >= 1 && p0.DroneShotsBySide[0] > 0 && p0.DroneShotsBySide[1] > 0
                && fullLasers[0] >= 1 && lasers[1] >= 1;
            Console.WriteLine(pass ? "dronetest PASS" : "dronetest FAIL");
            Environment.ExitCode = pass ? 0 : 1;
        }

        //   -dronekilltest: the kill message (owner 2026-10-17: "(sentry drone)"). Seat 0 a local Samus, seat 1
        // a Trooper bot with the drone; each case starts a fresh match, kills seat 0 one way and reads its HUD's "(...)" line,
        // and what its snapshot sends a match client (LastBeam, through Snapshot's bytes):
        //   - a drone blaster shot (its own pool) -> (SENTRY DRONE), LastBeam 0x40 (Power Beam + the drone bit)
        //   - the cutting laser (source = the drone's player) -> (SENTRY DRONE), LastBeam 0x49 (no beam + the drone bit)
        //   - a Power Beam shot of his own -> (POWER BEAM), LastBeam 0 (unchanged)
        //   - Shooting Gallery's turret (owner 2026-10-17: "killed by [player] (heavy turret)"): a turret shot -> (HEAVY TURRET),
        //     LastBeam 0x20 (Power Beam + the turret bit); the entry kill -> (HEAVY TURRET), LastBeam 0x29 (no beam + the turret bit)
        //   - a match client given those snapshots -> the same lines
        public static void RunKillMessage()
        {
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Characters.ScoutDroneOn = true;
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, TimeLimitSeconds = 60 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Character = Characters.Trooper.Id, Bot = true, BotLevel = 0 });
            var queueField = typeof(PlayerEntity).GetField("_hudMessageQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var laserField = typeof(PlayerEntity).GetField("_droneLaserApplying",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            IEnumerable<object> Queue() => ((System.Collections.IEnumerable)queueField.GetValue(null)!).Cast<object>();
            string? KilledBy()
            {
                foreach (object m in Queue())
                {
                    float life = (float)m.GetType().GetProperty("Lifetime")!.GetValue(m)!;
                    string text = new string(((char[])m.GetType().GetProperty("Text")!.GetValue(m)!).TakeWhile(c => c != '\0').ToArray());
                    if (life > 0 && text.StartsWith('('))
                    {
                        return text;
                    }
                }
                return null;
            }
            var errors = new List<string>();
            var turretShot = typeof(BeamProjectileEntity).GetProperty(nameof(BeamProjectileEntity.HostTurretShot))!;
            sbyte[] sent = new sbyte[5];
            string[] names = { "drone blaster", "cutting laser", "own Power Beam", "turret shot", "turret entry kill" };
            string[] wantText = { "(SENTRY DRONE)", "(SENTRY DRONE)", "(POWER BEAM)", "(HEAVY TURRET)", "(HEAVY TURRET)" };
            sbyte[] wantBeam = { 0x40, 0x49, 0, 0x20, 0x29 };
            for (int pass = 0; pass < 2; pass++)
            {
                bool client = pass == 1;
                string side = client ? "match client" : "host";
                for (int k = 0; k < names.Length; k++)
                {
                    CampaignHost host = CampaignHost.StartMatch(settings);
                    PlayerEntity p0 = PlayerEntity.Players[0];
                    PlayerEntity p1 = PlayerEntity.Players[1];
                    for (int f = 0; f < 600 && (p0.Health == 0 || p1.Health == 0); f++)
                    {
                        var input = new CampaignInput();
                        if (f % 2 == 0) input.Buttons |= CampaignButtons.Shoot; // spawn
                        host.Step(input);
                    }
                    if (p0.Health == 0 || p1.Health == 0 || p1.DroneBeams == null)
                    {
                        errors.Add($"{side} {names[k]}: seats not spawned / no drone pool");
                        continue;
                    }
                    foreach (object m in Queue())
                    {
                        m.GetType().GetProperty("Lifetime")!.SetValue(m, 0f);
                    }
                    if (client)
                    {
                        PlayerNetState s = p0.NetCapture();
                        s.Health = 0;
                        s.LastAttacker = 1;
                        s.LastBeam = sent[k];
                        s.LastFlags = 0;
                        PlayerEntity.NetClient = true;
                        try
                        {
                            p0.NetApply(s, pose: false);
                        }
                        finally
                        {
                            PlayerEntity.NetClient = false;
                        }
                    }
                    else
                    {
                        BeamProjectileEntity beam = k == 0 ? p1.DroneBeams[0] : p1.EquipInfo.Beams![0];
                        beam.Owner = p1;
                        beam.Beam = BeamType.PowerBeam;
                        if (k == 4)
                        {
                            PlayerEntity.HostTurretKillApplying = true;
                            try
                            {
                                p0.TakeDamage(999u, DamageFlags.NoDmgInvuln | DamageFlags.IgnoreInvuln, Vector3.UnitX, p1);
                            }
                            finally
                            {
                                PlayerEntity.HostTurretKillApplying = false;
                            }
                        }
                        else if (k == 3)
                        {
                            turretShot.SetValue(beam, true);
                            try
                            {
                                p0.TakeDamage(999u, DamageFlags.IgnoreInvuln, Vector3.UnitX, beam);
                            }
                            finally
                            {
                                turretShot.SetValue(beam, false);
                            }
                        }
                        else if (k == 1)
                        {
                            laserField.SetValue(null, true);
                            try
                            {
                                p0.TakeDamage(999u, DamageFlags.NoDmgInvuln | DamageFlags.IgnoreInvuln, Vector3.UnitX, p1); // (past the spawn invulnerability)
                            }
                            finally
                            {
                                laserField.SetValue(null, false);
                            }
                        }
                        else
                        {
                            p0.TakeDamage(999u, DamageFlags.IgnoreInvuln, Vector3.UnitX, beam);
                        }
                        byte[] bytes = new byte[Snapshot.SeatSize];
                        Snapshot.WriteSeat(bytes, p0.NetCapture(), 0, 0, 0);
                        sent[k] = Snapshot.ReadSeat(bytes, out _, out _, out _).LastBeam;
                        if (sent[k] != wantBeam[k])
                        {
                            errors.Add($"{names[k]}: snapshot LastBeam 0x{sent[k]:x2}, want 0x{wantBeam[k]:x2}");
                        }
                    }
                    string? text = KilledBy();
                    Console.WriteLine($"{side}, {names[k]}: health {p0.Health}, message {text ?? "(none)"}"
                        + (client ? $" (from LastBeam 0x{sent[k]:x2})" : $", snapshot LastBeam 0x{sent[k]:x2}"));
                    if (p0.Health != 0)
                    {
                        errors.Add($"{side} {names[k]}: not killed");
                    }
                    if (text != wantText[k])
                    {
                        errors.Add($"{side} {names[k]}: {text ?? "no message"}, want {wantText[k]}");
                    }
                }
            }
            foreach (string e in errors)
            {
                Console.WriteLine($"  WRONG {e}");
            }
            Console.WriteLine(errors.Count == 0 ? "dronekilltest PASS" : "dronekilltest FAIL");
            Environment.ExitCode = errors.Count == 0 ? 0 : 1;
        }
    }
}
