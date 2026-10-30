using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaignencounters [list] [roomId|a-b] [load|door] [hunter=X]: hunter encounters ON (the room sweeps run with
    // Cheats.NoRandomEncounters). Every story room with a hunter spawn, on every entity layer the spawn is on, with every
    // hunter the spawn can roll -- a random spawn takes one of the six hunters from the planet's roster, or the Guardian
    // when the roster is empty (always the case before the planet is cleared) -- forced to fire
    // (SceneSetup.HostForceEncounters). Two ways in:
    //   load  start the session in the room (continue / exit-ship path; the roster comes from the save's defeated hunters)
    //   door  walk in through a door from a neighbouring room: the background room load (RoomEntity.ProcessTransition
    //         on a worker thread) where a missing encounter node file left the owner stuck at a dark door in High Ground
    // Each case checks that the room load threw nothing, that the bot is there as the expected hunter, that the
    // encounter's node file replaced the room's (Metadata.EncounterNodeDataOverrides), then runs 10 s of the fight,
    // kills the bot and runs 10 s more. `list` prints the spawns only. Writes extract_out/campaign/encounter_sweep.txt.
    internal static partial class CampaignSim
    {
        private sealed class HunterSpawn
        {
            public int EntityId;
            public ushort LayerMask;
            public uint HunterId;
            public uint EncounterType;
            public byte Chance;
        }

        private static readonly Hunter[] _rosterHunters =
            { Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };

        public static void Encounters(string[] args)
        {
            args = TakeHunterArg(args);
            // effect pool double releases (Renderer.cs guard, 2026-10-01): print who did it
            Scene.HostEffectPoolWarning ??= msg => Console.WriteLine($"  WARNING {msg}");
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            bool listOnly = args.Contains("list");
            bool doLoad = !args.Contains("door");
            bool doDoor = !args.Contains("load");
            int fromRoom = 27, toRoom = 92;
            string? range = args.Skip(1).FirstOrDefault(a => a.Length > 0 && Char.IsDigit(a[0]));
            if (range != null)
            {
                string[] parts = range.Split('-');
                fromRoom = Int32.Parse(parts[0]);
                toRoom = parts.Length > 1 ? Int32.Parse(parts[1]) : fromRoom;
            }
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "campaign"));
            Directory.CreateDirectory(outDir);
            var lines = new List<string>();
            void Log(string line)
            {
                Console.WriteLine(line);
                lines.Add(line);
            }

            // the encounter node files must exist under exactly these names: Android's file system is case-sensitive
            string nodeDir = Paths.Combine(Paths.FileSystem, "levels", "nodeData");
            var nodeFiles = new HashSet<string>(Directory.GetFiles(nodeDir).Select(f => Path.GetFileName(f)!), StringComparer.Ordinal);
            foreach ((int roomId, string path) in Metadata.EncounterNodeDataOverrides.OrderBy(kv => kv.Key))
            {
                string name = Path.GetFileName(path.Replace('\\', '/'));
                bool exact = nodeFiles.Contains(name);
                Log($"  {(exact ? "ok  " : "CASE")} node override {roomId} {Metadata.GetRoomById(roomId)?.InGameName}: {name}"
                    + (exact ? "" : $" (on disk: {nodeFiles.FirstOrDefault(f => String.Equals(f, name, StringComparison.OrdinalIgnoreCase)) ?? "missing"})"));
            }

            var total = Stopwatch.StartNew();
            int ok = 0, bad = 0;
            // one working door per (room, layer), found on the first hunter and reused for the rest
            var doorFor = new Dictionary<(int, int), DoorCase>();
            List<DoorCase>? allDoors = null;
            for (int id = fromRoom; id <= toRoom; id++)
            {
                RoomMetadata meta = Metadata.GetRoomById(id)!;
                var spawns = new List<HunterSpawn>();
                foreach (Entity e in Read.GetEntities(meta.EntityPath!, -1, meta.FirstHunt))
                {
                    if (e is Entity<EnemySpawnEntityData> sp && sp.Data.EnemyType == EnemyType.Hunter)
                    {
                        EnemySpawnFields09 f = sp.Data.Fields.S09;
                        spawns.Add(new HunterSpawn
                        {
                            EntityId = sp.EntityId, LayerMask = sp.LayerMask, HunterId = f.HunterId,
                            EncounterType = f.EncounterType, Chance = f.HunterChance
                        });
                    }
                }
                if (spawns.Count == 0)
                {
                    continue;
                }
                string over = Metadata.EncounterNodeDataOverrides.TryGetValue(id, out string? o) ? $", encounter nodes {Path.GetFileName(o.Replace('\\', '/'))}" : "";
                Log($"  {id} {meta.Name} ({meta.InGameName}){over}");
                foreach (HunterSpawn s in spawns)
                {
                    Log($"      spawn #{s.EntityId}: {(s.HunterId == 8 ? "random" : ((Hunter)s.HunterId).ToString())}, chance {s.Chance}%, "
                        + $"encounter type {s.EncounterType}, layers {String.Join("/", Enumerable.Range(0, 3).Where(l => (s.LayerMask & (1 << l)) != 0).Select(l => _layerNames[l]))}"
                        + (s.LayerMask == 0xFFFF ? " (all)" : ""));
                }
                if (listOnly)
                {
                    continue;
                }
                for (int layer = 0; layer < 3; layer++)
                {
                    List<HunterSpawn> active = spawns.Where(s => (s.LayerMask & (1 << layer)) != 0).ToList();
                    if (active.Count == 0)
                    {
                        continue;
                    }
                    // the hunters this layer can put in the room: a random spawn draws from the planet's roster, which
                    // only cleared planets get (SceneSetup.UpdateAreaHunters); an empty roster rolls the Guardian
                    var choices = new List<Hunter>();
                    if (active.Any(s => s.HunterId == 8))
                    {
                        if (layer == (int)AreaState.Clear)
                        {
                            choices.AddRange(_rosterHunters);
                        }
                        choices.Add(Hunter.Guardian);
                    }
                    else
                    {
                        choices.Add((Hunter)active[0].HunterId);
                    }
                    foreach (Hunter h in choices)
                    {
                        string label = $"{id} {meta.InGameName} [{_layerNames[layer]}] {h}";
                        if (doLoad)
                        {
                            (bool good, string summary) = RunEncounterLoad(meta, layer, h, random: active.Any(s => s.HunterId == 8));
                            if (good) ok++; else bad++;
                            Log($"  {(good ? "ok  " : "FAIL")} {label} load: {summary}");
                        }
                        if (doDoor)
                        {
                            allDoors ??= Enumerable.Range(27, 66).SelectMany(r => DoorCases(Metadata.GetRoomById(r)!, everyLayer: true)).ToList();
                            (bool good, string summary) = RunEncounterDoor(meta, layer, h, allDoors, doorFor);
                            if (good) ok++; else bad++;
                            Log($"  {(good ? "ok  " : "FAIL")} {label} door: {summary}");
                        }
                    }
                }
            }
            if (!listOnly)
            {
                Log($"  {ok} ok, {bad} not ok, {total.Elapsed.TotalSeconds:0} s");
                File.WriteAllLines(Path.Combine(outDir, "encounter_sweep.txt"), lines);
            }
        }

        // the room's planet (both its areas) on the layer, every other area on the first-visit layer; on a cleared planet
        // UpdateAreaHunters then deals every defeated hunter to this planet's roster
        private static BossFlags FlagsForPlanet(int roomId, int layer)
        {
            int planet = Metadata.GetAreaInfo(roomId) / 2;
            return (BossFlags)((layer << (4 * planet)) | (layer << (4 * planet + 2)));
        }

        private static (bool, string) RunEncounterLoad(RoomMetadata meta, int layer, Hunter h, bool random)
        {
            RoomEntity.TransitionFailure = null;
            Cheats.NoRandomEncounters = false;
            SceneSetup.HostForceEncounters = true;
            try
            {
                using CampaignHost host = CampaignHost.Start(meta.Name, hunter: _hunter, arriving: false, setupSave: s =>
                {
                    GiveAll(s, 0);
                    s.BossFlags = FlagsForPlanet(meta.Id, layer);
                    s.DefeatedHunters = !random || h == Hunter.Guardian ? (byte)0 : (byte)(1 << (int)h);
                });
                return CheckEncounter(host, meta.Id, h);
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name}: {ex.Message} @ {StackHead(ex)}");
            }
            finally
            {
                SceneSetup.HostForceEncounters = false;
            }
        }

        private static (bool, string) RunEncounterDoor(RoomMetadata meta, int layer, Hunter h, List<DoorCase> allDoors,
            Dictionary<(int, int), DoorCase> doorFor)
        {
            // doors into this room that load it on this layer: open, walkable gates first, landing rooms (long intro) last
            List<DoorCase> candidates = doorFor.TryGetValue((meta.Id, layer), out DoorCase? known) ? new List<DoorCase> { known }
                : allDoors.Where(c => c.TargetRoom == meta.Id && c.TargetLayer == layer && !c.ExpectBlocked)
                    .OrderBy(c => c.Morph).ThenBy(c => c.Weapon != BeamType.PowerBeam).ThenBy(c => IsLandingRoom(c.Room.Id))
                    .Take(4).ToList();
            if (candidates.Count == 0)
            {
                return (true, "no walkable door loads it on this layer (skipped)");
            }
            int planet = Metadata.GetAreaInfo(meta.Id) / 2;
            byte roster = h == Hunter.Guardian ? (byte)0 : (byte)(1 << (int)h);
            string last = "";
            foreach (DoorCase c in candidates)
            {
                DoorResult r;
                bool reached = false;
                try
                {
                    // the start room loads with encounters off (a bot there would only get in the way of the walk);
                    // they're switched on, with this planet's roster, right before the walk
                    Cheats.NoRandomEncounters = true;
                    r = RunDoor(c, verbose: false, beforeWalk: host =>
                    {
                        GameState.StorySave.AreaHunters[planet] = roster;
                        Cheats.NoRandomEncounters = false;
                        SceneSetup.HostForceEncounters = true;
                    }, onArrival: host =>
                    {
                        reached = true;
                        return CheckEncounter(host, meta.Id, h);
                    });
                }
                catch (Exception ex)
                {
                    r = new DoorResult { Summary = $"{ex.GetType().Name}: {ex.Message} @ {StackHead(ex)}" };
                }
                finally
                {
                    SceneSetup.HostForceEncounters = false;
                }
                last = $"via {c.Room.Id} {c.Room.InGameName} door#{c.DoorId}: {r.Summary}";
                if (reached || r.Summary.StartsWith("room load failed"))
                {
                    doorFor[(meta.Id, layer)] = c;
                    return (r.Ok, last);
                }
            }
            // no door got her there: a walk problem (the plain door sweep's territory), not an encounter result
            return (false, "never arrived: " + last);
        }

        // the top frames of an exception's stack, innermost first, without file paths
        private static string StackHead(Exception ex) => String.Join(" < ", (ex.StackTrace ?? "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith("at ")).Take(16)
            .Select(l => l[3..].Split(" in ")[0] + (l.Contains(":line ") ? ":" + l[(l.LastIndexOf(":line ") + 6)..] : "")));

        private static string NodePrint(NodeData? nd) => nd == null ? "none"
            : $"{nd.Data.Count}x{String.Join("/", nd.Data.Select(a => a.Sum(b => b.Count)))}";

        // after the room is in: the bots, the node file, a fight, the kill
        private static (bool, string) CheckEncounter(CampaignHost host, int roomId, Hunter expected)
        {
            var problems = new List<string>();
            string Bots() => String.Join(", ", Enumerable.Range(1, Math.Max(0, PlayerEntity.PlayerCount - 1))
                .Select(i => PlayerEntity.Players[i]).Where(p => p.IsBot)
                .Select(p => $"{p.Hunter} hp {p.Health} at {Fmt(p.Position)}"));
            List<PlayerEntity> bots = Enumerable.Range(1, Math.Max(0, PlayerEntity.PlayerCount - 1))
                .Select(i => PlayerEntity.Players[i]).Where(p => p.IsBot).ToList();
            if (!bots.Any(b => b.Hunter == expected))
            {
                problems.Add($"no {expected} bot (bots: {(bots.Count == 0 ? "none" : Bots())})");
            }
            string enc = String.Join("/", GameState.EncounterState);
            bool encounter = Enumerable.Range(1, 3).Any(i => GameState.EncounterState[i] is >= 1 and <= 4);
            string node = NodePrint(host.Scene.Room?.NodeData);
            if (encounter && Metadata.EncounterNodeDataOverrides.TryGetValue(roomId, out string? overPath))
            {
                string want = NodePrint(ReadNodeData.ReadData(Paths.Combine(@"", overPath)));
                if (node != want)
                {
                    problems.Add($"room nodes {node}, encounter file has {want}");
                }
                else
                {
                    node += " (encounter file)";
                }
            }
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            string? ended = null; // why the scene closed under the test (MphRead's QuitGame cleans the scene up)
            int minHp = host.Player.Health;
            bool trace = Environment.GetEnvironmentVariable("MPHENCTRACE") == "1";
            if (trace)
            {
                NodeData? nd = host.Scene.Room?.NodeData;
                Console.WriteLine($"      at f{host.Frame}: bots {Bots()}; enc {enc}; nodes {node}, "
                    + $"sets [{(nd == null ? "" : String.Join(",", Enumerable.Range(0, 16).Where(i => nd.SetSelector[i])))}]; "
                    + String.Join("; ", bots.Select(b => $"{b.Hunter} spawner #{b.EnemySpawner?.Id} level {b.BotLevel}")));
            }
            void Run(int frames, Func<bool>? until = null)
            {
                for (int i = 0; i < frames && !host.Ended && RoomEntity.TransitionFailure == null && until?.Invoke() != true; i++)
                {
                    input.DialogButton = !host.DialogPaused || i % 10 != 0 ? null
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance;
                    DialogType dialog = host.DialogPaused ? host.Dialog : DialogType.None;
                    host.Step(input);
                    minHp = Math.Min(minHp, host.Player.Health);
                    // she never shoots back, and a hunter can kill a Samus who stands still (game over quits the scene):
                    // keep her topped up -- the test is about the encounter running, not the fight's balance
                    if (host.Player.Health is > 0 and < 400)
                    {
                        host.Player.Health = 799;
                    }
                    if (trace && host.Frame % 30 == 0)
                    {
                        Console.WriteLine($"      f{host.Frame}: Samus {Fmt(host.Player.Position)} hp {host.Player.Health} "
                            + $"standing {host.Player.Flags1.TestFlag(PlayerFlags1.Standing)}; bots {Bots()}");
                    }
                    if (host.Ended)
                    {
                        ended = $"scene ended at frame {host.Frame} (room {host.RoomId}, Samus hp {host.Player.Health}, lowest {minHp}, "
                            + $"pos {Fmt(host.Player.Position)}, last dialog {dialog}, answered {input.DialogButton?.ToString() ?? "nothing"})";
                    }
                }
            }
            // scripted encounters wait for their event (a trigger volume activates the spawner, which then spawns the
            // hunter once Samus is in range). Only while no hunter is out: activate the first waiting spawner ourselves
            // and stand her in front of it; the room's own script releases the rest (activating several at once stages
            // fights the game never does -- both Weapons Complex Sylux phases together crash the bots' node pathing)
            var triggered = new List<string>();
            PlayerEntity? first = bots.Any(b => b.Health > 0) ? null : bots.FirstOrDefault(b => b.EnemySpawner != null);
            if (first != null)
            {
                PlayerEntity b = first;
                EnemySpawnEntity sp = b.EnemySpawner!;
                host.Scene.SendMessage(Message.Activate, sp, sp, 0, 0);
                Run(60, () => b.Health > 0);
                // stand where there is floor near the spawner (some face a drop: a fixed offset can be mid-air, and a
                // fall kills her and quits the game). Not ON the spawner: the hunter would appear inside her.
                foreach (float dist in new[] { 2f, -2f, 4f, -4f })
                {
                    if (b.Health > 0 || ended != null)
                    {
                        break;
                    }
                    host.PlacePlayer(sp.Position + sp.FacingVector * dist + sp.UpVector * 0.5f, dist >= 0 ? -sp.FacingVector : sp.FacingVector);
                    Run(30);
                    float drop = Vector3.Dot(sp.Position - host.Player.Position, sp.UpVector);
                    if (trace)
                    {
                        Console.WriteLine($"      place {dist} u from spawner #{sp.Id} at {Fmt(sp.Position)}: Samus {Fmt(host.Player.Position)}, "
                            + $"drop {drop:0.0}, standing {host.Player.Flags1.TestFlag(PlayerFlags1.Standing)}");
                    }
                    if (host.Player.Health > 0 && drop < 1.5f && drop > -1.5f && host.Player.Flags1.TestFlag(PlayerFlags1.Standing))
                    {
                        Run(180, () => b.Health > 0);
                    }
                }
                if (b.Health > 0)
                {
                    triggered.Add($"{b.Hunter} via spawner #{sp.Id}");
                }
                else
                {
                    problems.Add($"{b.Hunter} never spawned (spawner #{sp.Id} activated, Samus placed in front of it)");
                }
            }
            string waiting = String.Join(", ", bots.Where(b => b.Health == 0 && b != first).Select(b => $"{b.Hunter} (spawner #{b.EnemySpawner?.Id})"));
            string staged = (triggered.Count > 0 ? $"; triggered {String.Join(", ", triggered)}" : "")
                + (waiting.Length > 0 ? $"; waiting on the room's script: {waiting}" : "");
            var spawnPos = bots.ToDictionary(b => b, b => b.Position);
            try
            {
                Run(600);
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name} in the fight: {ex.Message} @ {StackHead(ex)}"
                    + (triggered.Count > 0 ? " [after the test's own spawner activation]" : " [no test activation]")
                    + $" | bots [{String.Join(", ", bots.Select(b => b.Hunter))}], enc {enc}, nodes {node}{staged}");
            }
            if (ended != null)
            {
                problems.Add(ended);
                return (false, String.Join("; ", problems) + $" | bots [{String.Join(", ", bots.Select(b => b.Hunter))}], enc {enc}, nodes {node}{staged}");
            }
            float moved = bots.Count == 0 ? 0 : bots.Max(b => (b.Position - spawnPos[b]).Length);
            string fight = $"fight 600 f: bots {Bots()}, max move {moved:0.0} u, Samus hp {host.Player.Health}";
            foreach (PlayerEntity b in bots)
            {
                if (!Single.IsFinite(b.Position.X) || !Single.IsFinite(b.Position.Y) || !Single.IsFinite(b.Position.Z))
                {
                    problems.Add($"{b.Hunter} position not finite");
                }
            }
            // the kill: shot-sized hits (40 every 10 frames, like a charged Power Beam) on every hunter that is out,
            // incl. any the room's script releases meanwhile -- one huge hit would skip the health thresholds a
            // fight's script reacts to (a hunter retreating, the next phase spawning)
            int hits = 0;
            try
            {
                for (int t = 0; t < 120 && ended == null && RoomEntity.TransitionFailure == null; t++)
                {
                    foreach (PlayerEntity b in bots.Where(b => b.Health > 0))
                    {
                        b.TakeDamage(40, DamageFlags.None, null, host.Player);
                        hits++;
                    }
                    Run(10);
                }
                Run(300);
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name} during the kill ({hits} hits): {ex.Message} @ {StackHead(ex)}"
                    + $" | bots [{String.Join(", ", bots.Select(b => b.Hunter))}], enc {enc}, nodes {node}{staged}; {fight}");
            }
            string after = $"killed ({hits} hits of 40) -> bots {Bots()}, encounter state {String.Join("/", GameState.EncounterState)}, room {host.RoomId}"
                + (ended != null ? ", " + ended : "");
            if (ended != null)
            {
                problems.Add("after the kill: " + ended);
            }
            if (RoomEntity.TransitionFailure is Exception fail)
            {
                problems.Add($"room load failed: {fail.GetType().Name}: {fail.Message}");
            }
            string summary = $"bots [{String.Join(", ", bots.Select(b => b.Hunter))}], enc {enc}, nodes {node}{staged}; {fight}; {after}";
            return problems.Count == 0 ? (true, summary) : (false, String.Join("; ", problems) + " | " + summary);
        }
    }
}
