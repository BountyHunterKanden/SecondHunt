using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Frontend;

namespace MphRead
{
    // -endingtest [bad|chain|good|all=all]: owner queue #36's campaign half, headless. Gorea's fights run through the dev
    // "boss down" skip (Core Campaign/GoreaSkip.cs via BossSkip), the movies are logged and skipped as a host would, and
    // once the ending movie's EndGame closes the session: ShipNavigation.Classify must say Ending, the room picks the
    // ending, the Gorea timers (GameState.ModeStateAdventure) have run, and GameEnding.From gives the results page's
    // numbers (what CampaignShip.OnHostEnded hands the menus).
    //   bad    Gorea_b1 without the seal targets (trigger state bit 12): 1A/1B x3 -> BadEndingPart1+2 -> room 91
    //   chain  Gorea_b1 with them: -> Gorea2Intro -> Gorea_b2 -> GoodEnding -> room 92
    //   good   Gorea_b2 on its own -> GoodEnding -> room 92
    internal static partial class CampaignSim
    {
        public static void EndingTest(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string which = args.Length >= 2 ? args[1] : "all";
            bool trace = args.Contains("trace");
            var cases = new List<(string Name, string Room, bool Seals, Movie[] Movies, int EndRoom)>
            {
                ("bad", "Gorea_b1", false, new[] { Movie.BadEndingPart1, Movie.BadEndingPart2 }, 91),
                ("chain", "Gorea_b1", true, new[] { Movie.Gorea2Intro, Movie.GoodEnding }, 92),
                ("good", "Gorea_b2", true, new[] { Movie.GoodEnding }, 92),
            };
            var results = new List<string>();
            int pass = 0, fail = 0;
            foreach (var c in cases.Where(c => which == "all" || c.Name == which))
            {
                (bool ok, string summary) = EndingCase(c.Name, c.Room, c.Seals, c.Movies, c.EndRoom, trace);
                if (ok) pass++; else fail++;
                results.Add($"  [{(ok ? "PASS" : "FAIL")}] {c.Name}: {summary}");
            }
            Console.WriteLine();
            foreach (string line in results)
            {
                Console.WriteLine(line);
            }
            Console.WriteLine($"  {pass} passed, {fail} failed");
        }

        private static ItemInstanceEntity? OmegaItem(Scene scene)
        {
            foreach (EntityBase e in scene.Entities)
            {
                if (e is ItemSpawnEntity spawn && spawn.Item is ItemInstanceEntity item && item.ItemType == ItemType.OmegaCannon)
                {
                    return item;
                }
            }
            return null;
        }

        private static bool LastGorea1BPhase(Scene scene)
        {
            foreach (EntityBase e in scene.Entities)
            {
                if (e is MphRead.Entities.Enemies.Enemy28Entity b && b.PhasesLeft == 1)
                {
                    return true;
                }
            }
            return false;
        }

        private static (bool Ok, string Summary) EndingCase(string name, string room, bool seals, Movie[] expectMovies, int expectRoom,
            bool trace)
        {
            Console.WriteLine($"=== {name}: {room}, seal targets {(seals ? "shot" : "not shot")}");
            Scene.HostPlaysMovies = true;
            MusicPlayer.Host = new LoggingMusicHost();
            int frame = 0;
            CampaignHost host = CampaignHost.Start(room, arriving: false, setupSave: s =>
            {
                GiveAll(s, 0);
                // trigger state bit 12 = room 91's seal targets shot in order (28_Gorea1B Behavior02 reads it). Set during
                // the fight below, as a player sets it: set at load, the room's StateBits triggers (47-52, 42) fire from
                // the first frame and 1A never opens its arms. Gorea_b2 on its own has it from the save.
                s.TriggerState[1] &= 0xEF;
                if (seals && room == "Gorea_b2") s.TriggerState[1] |= 0x10;
            });
            try
            {
                Scene scene = host.Scene;
                var skip = new BossSkip();
                if (trace)
                {
                    skip.Trace = msg => Console.WriteLine($"  f{frame,6} {msg}");
                }
                int movieSerial = scene.HostMovieSerial, lastRoom = scene.RoomId;
                var movies = new List<Movie>();
                bool fighting = false, omegaSeen = false, omegaTaken = false, gorea2Called = false;
                while (frame < (trace ? 60 * 60 * 2 : 60 * 60 * 15) && !host.Ended)
                {
                    host.Player.Health = host.Player.HealthMax;
                    // Gorea_b2: Gorea 2 comes out when the player picks up the Omega Cannon (item spawn 1 notifies 41, which
                    // arms the volumes that send Gorea2Trigger), as a player does: stand on it, then back to the Power Beam
                    // (holding the cannon while the seal sphere is hit makes Gorea 2 flee, 31_Gorea2 Behavior13)
                    BeamType select = BeamType.None;
                    if (scene.RoomId == ShipNavigation.GoreaSecondRoomId && !omegaTaken && frame % 30 == 0)
                    {
                        ItemInstanceEntity? omega = OmegaItem(scene);
                        if (omega != null)
                        {
                            host.PlacePlayer(omega.Position, -OpenTK.Mathematics.Vector3.UnitZ);
                            omegaSeen = true;
                        }
                        else if (omegaSeen)
                        {
                            omegaTaken = true;
                            select = BeamType.PowerBeam;
                            Console.WriteLine($"  f{frame,6} Omega Cannon picked up (holding {host.Player.CurrentWeapon})");
                        }
                    }
                    // then she walks into the arena: the volumes 41 armed (14, 39) start the fight
                    if (omegaTaken && !gorea2Called && frame % 30 == 15 && scene.TryGetEntity(14, out EntityBase? v14)
                        && v14 is TriggerVolumeEntity tv)
                    {
                        if (tv.Active)
                        {
                            host.PlacePlayer(tv.Volume.GetCenter(), -OpenTK.Mathematics.Vector3.UnitZ);
                            gorea2Called = true;
                            Console.WriteLine($"  f{frame,6} into volume #14 at {tv.Volume.GetCenter()}");
                        }
                    }
                    // a player answers prompts (the seal targets' prompt 52 pauses the fight until OK)
                    host.Step(new CampaignInput
                    {
                        SelectWeapon = select,
                        DialogButton = !host.DialogPaused || frame % 10 != 0 ? HostDialogButton.None
                            : host.Dialog == DialogType.YesNo ? HostDialogButton.No
                            : HostDialogButton.Advance
                    });
                    frame++;
                    if (host.DialogPaused && frame % 10 == 1)
                    {
                        Console.WriteLine($"  f{frame,6} dialog {host.Dialog} (answered)");
                    }
                    if (host.Ended)
                    {
                        break;
                    }
                    skip.Step(scene);
                    if (seals && (GameState.StorySave.TriggerState[1] & 0x10) == 0 && scene.RoomId == ShipNavigation.GoreaFirstRoomId
                        && LastGorea1BPhase(scene))
                    {
                        GameState.StorySave.TriggerState[1] |= 0x10;
                        Console.WriteLine($"  f{frame,6} seal targets shot (trigger state bit 12), 1B's last phase");
                    }
                    if (skip.Fighting != fighting)
                    {
                        fighting = skip.Fighting;
                        Console.WriteLine($"  f{frame,6} room {scene.RoomId}: {(fighting ? "fight on (intro over)" : "fight paused")}");
                    }
                    if (trace && frame % 600 == 0)
                    {
                        var parts = new List<string>();
                        foreach (EntityBase e in scene.Entities)
                        {
                            if (e is EnemyInstanceEntity en && en.EnemyType is >= EnemyType.Gorea1A and <= EnemyType.GoreaSealSphere2
                                and not EnemyType.Trocra and not EnemyType.GoreaLeg)
                            {
                                string f = (en.Flags.TestFlag(EnemyFlags.Visible) ? "V" : "-")
                                    + (en.Flags.TestFlag(EnemyFlags.CollideBeam) ? "B" : "-")
                                    + (en.Flags.TestFlag(EnemyFlags.Invincible) ? "I" : "-");
                                string extra = en switch
                                {
                                    MphRead.Entities.Enemies.Enemy29Entity s1 => $" dmg {s1.Damage}",
                                    MphRead.Entities.Enemies.Enemy32Entity s2 => $" dmg {s2.Damage}",
                                    MphRead.Entities.Enemies.Enemy26Entity a => $" dmg {a.Damage} {a.ArmFlags}",
                                    MphRead.Entities.Enemies.Enemy28Entity b => $" phases {b.PhasesLeft}",
                                    _ => ""
                                };
                                parts.Add($"{en.EnemyType} {f}{extra}");
                            }
                        }
                        Console.WriteLine($"  f{frame,6} status: {String.Join("; ", parts)}");
                    }
                    if (scene.HostMovieSerial != movieSerial)
                    {
                        movieSerial = scene.HostMovieSerial;
                        movies.Add(scene.HostMovie);
                        Console.WriteLine($"  f{frame,6} movie {scene.HostMovie} (skipped), {skip.Report}");
                    }
                    if (scene.MoviePlaying)
                    {
                        scene.SkipMovie();
                    }
                    if (scene.RoomId != lastRoom)
                    {
                        Console.WriteLine($"  f{frame,6} room {lastRoom} -> {scene.RoomId}");
                        lastRoom = scene.RoomId;
                    }
                }
                if (!host.Ended)
                {
                    return (false, $"no ending after {frame} frames (room {scene.RoomId}, {skip.Report}, movies {String.Join(", ", movies)})");
                }
                int roomId = host.RoomId;
                CampaignEnd end = ShipNavigation.Classify(host);
                StorySave story = GameState.StorySave;
                EndingResults r = GameEnding.From(story, roomId, playSeconds: frame / 60);
                Console.WriteLine($"  f{frame,6} session ended in room {roomId}: {end}");
                Console.WriteLine($"         gorea 1 {story.Stats.Gorea1Time:0.0} s, gorea 2 {story.Stats.Gorea2Time:0.0} s; results {r}");
                var bad = new List<string>();
                if (end != CampaignEnd.Ending) bad.Add($"classified {end}, not Ending");
                if (roomId != expectRoom) bad.Add($"ended in room {roomId}, not {expectRoom}");
                if (!movies.SequenceEqual(expectMovies)) bad.Add($"movies {String.Join(", ", movies)}, expected {String.Join(", ", expectMovies)}");
                if (r.Good != (expectRoom == ShipNavigation.GoreaSecondRoomId)) bad.Add($"Good = {r.Good}");
                if (room == "Gorea_b1" && story.Stats.Gorea1Time <= 0) bad.Add("no Gorea 1 time");
                if (expectRoom == ShipNavigation.GoreaSecondRoomId && story.Stats.Gorea2Time <= 0) bad.Add("no Gorea 2 time");
                if (expectRoom == ShipNavigation.GoreaFirstRoomId && story.Stats.Gorea2Time != 0) bad.Add("Gorea 2 time without Gorea 2");
                string summary = $"{String.Join(" -> ", movies)} -> room {roomId} {end}, {(r.Good ? "good" : "bad")}, "
                    + $"gorea {r.GoreaSeconds} s ({story.Stats.Gorea1Time:0}+{story.Stats.Gorea2Time:0}), {skip.Report}, {frame} frames";
                return (bad.Count == 0, bad.Count == 0 ? summary : summary + " -- " + String.Join("; ", bad));
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
            finally
            {
                host.Dispose();
                Scene.HostPlaysMovies = false;
            }
        }
    }
}
