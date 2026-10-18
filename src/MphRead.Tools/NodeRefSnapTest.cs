using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats.Culling;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // An online joiner snapped onto the host's pose (PlayerEntity.NetApply / NetCorrect) took its room part from
    // Scene.GetNodeRefByPosition, whose portal planes run on forever: in Council Chamber a spot can pass a far part's
    // test, and the room then draws only what that wrong part sees (the Samsung's black Council Chamber, 10-10).
    //   -noderefsnaptest [arena=Council Chamber] [arena=...] [at=x,y,z] [mode=Bounty] (default Battle; modes load different spawns)
    // Truth = each player spawn's own NodeRef (authored, what the host spawns with). Checks the part lookups at every
    // spawn and that a spawn-to-spawn snap through PlayerEntity.NetSnapNodeRef lands in the spawn's own part.
    internal static class NodeRefSnapTest
    {
        public static void Run(string[] args)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            if (args.Skip(1).FirstOrDefault(a => a.StartsWith("replay=")) is string rep)
            {
                RunReplay(rep[7..]);
                return;
            }
            var arenas = args.Skip(1).Where(a => a.StartsWith("arena=")).Select(a => a[6..]).ToList();
            if (arenas.Count == 0) arenas = ["Council Chamber", "Sanctorus", "Data Shrine", "Combat Hall", "Alinos Perch"];
            var spots = args.Skip(1).Where(a => a.StartsWith("at=")).Select(a => a[3..].Split(',').Select(float.Parse).ToArray())
                .Select(f => new Vector3(f[0], f[1], f[2])).ToList();
            GameMode mode = args.Skip(1).Where(a => a.StartsWith("mode=")).Select(a => Enum.Parse<GameMode>(a[5..], true))
                .DefaultIfEmpty(GameMode.Battle).First();
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            bool pass = true;
            foreach (string name in arenas)
            {
                RoomMetadata arena = MatchArenas.Find(name) ?? throw new ArgumentException("no arena " + name);
                MatchSettings settings = MatchSettings.Quick(mode, arena.Name, Hunter.Samus, 0);
                settings.Solo = true;
                using CampaignHost host = CampaignHost.StartMatch(settings);
                for (int i = 0; i < 2; i++) host.Step(idle);
                Scene scene = host.Scene;
                var spawns = new List<PlayerSpawnEntity>();
                foreach (PlayerSpawnEntity s in scene.GetPlayerSpawnEntities()) if (s.NodeRef.PartIndex != -1) spawns.Add(s);
                int planesWrong = 0, containWrong = 0, spawnWrong = 0, pairs = 0, snapWrong = 0;
                foreach (PlayerSpawnEntity s in spawns)
                {
                    Vector3 p = s.Position + new Vector3(0, 0.5f, 0);
                    int planes = scene.GetNodeRefByPosition(p).PartIndex, contain = scene.GetNodeRefContaining(p).PartIndex;
                    if (PlayerEntity.NetSpawnNodeRef(scene, p).PartIndex != s.NodeRef.PartIndex) spawnWrong++;
                    if (planes != s.NodeRef.PartIndex) planesWrong++;
                    if (contain != s.NodeRef.PartIndex) containWrong++;
                    if (planes != s.NodeRef.PartIndex || contain != s.NodeRef.PartIndex)
                    {
                        Console.WriteLine($"    spawn {s.Id} at {p}: own part {s.NodeRef.PartIndex}, planes {planes}, containing {contain}");
                    }
                    foreach (PlayerSpawnEntity t in spawns)
                    {
                        if (t == s) continue;
                        pairs++;
                        if (PlayerEntity.NetSnapNodeRef(scene, s.NodeRef, p, t.Position + new Vector3(0, 0.5f, 0)).PartIndex
                            != t.NodeRef.PartIndex) snapWrong++;
                    }
                }
                foreach (Vector3 at in spots)
                {
                    Console.WriteLine($"    at {at}: planes {scene.GetNodeRefByPosition(at).PartIndex},"
                        + $" containing {scene.GetNodeRefContaining(at).PartIndex}");
                }
                Console.WriteLine($"  {arena.InGameName} ({arena.Name}): {spawns.Count} spawns; wrong part: planes lookup"
                    + $" {planesWrong}, containing lookup {containWrong} (info), NetSpawnNodeRef {spawnWrong}; {pairs} snaps, NetSnapNodeRef wrong {snapWrong}");
                if (spawnWrong != 0 || snapWrong != 0) pass = false;
            }
            Console.WriteLine(pass ? "PASS" : "FAIL");
        }

        // -noderefsnaptest replay=<file.mphreplay>: real play positions. Truth = each live player's own part (walked
        // through the portals frame by frame); counts how often a jump's lookup (NetSpawnNodeRef, what NetSnapNodeRef
        // takes on a > 1 unit move) and a 1-frame step from the true part (NetSnapNodeRef) would land elsewhere
        private static void RunReplay(string path)
        {
            var file = MphRecomp.Replay.ReplayFile.Load(path);
            Console.WriteLine($"  {Path.GetFileName(path)}: {file.Settings.Mode} {file.Settings.Arena}, {file.Steps.Count} steps; seats "
                + String.Join(", ", file.Settings.Players.Select((p, i) => $"{i} {p.Character ?? p.Hunter.ToString()}{(p.Bot ? " (bot)" : "")}")));
            var hist = new Dictionary<int, List<(NodeRef Ref, Vector3 Pos)>>();
            int[] ks = [1, 10, 30, 90];
            int samples = 0, lookupWrong = 0;
            var jumps = new int[ks.Length];
            var jumpWrong = new int[ks.Length];
            var wrongSpots = new Dictionary<(int, int), int>();
            // the replay viewer watching a seat (the old HostView part = the lookup at that seat's camera): runs of frames
            // where it isn't the camera's own part = the room drawn black / cut away in that view
            var viewRun = new Dictionary<int, (int Start, int Look, int Truth)>();
            var viewRuns = new List<(int Seat, int Start, int Len, int Truth, int Look)>();
            int viewFrames = 0, viewWrong = 0;
            var r = MphRecomp.Replay.ReplayVerify.Run(file, (step, scene) =>
            {
                for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (p.LoadFlags.TestFlag(LoadFlags.Active) && p.CameraInfo.NodeRef.PartIndex != -1)
                    {
                        viewFrames++;
                        int camTruth = p.CameraInfo.NodeRef.PartIndex;
                        int camLook = scene.GetNodeRefContaining(p.CameraInfo.Position).PartIndex;
                        bool wrong = camLook != camTruth;
                        if (wrong) viewWrong++;
                        if (viewRun.TryGetValue(i, out var run) && (!wrong || run.Look != camLook || run.Truth != camTruth))
                        {
                            viewRuns.Add((i, run.Start, step - run.Start, run.Truth, run.Look));
                            viewRun.Remove(i);
                        }
                        if (wrong && !viewRun.ContainsKey(i)) viewRun[i] = (step, camLook, camTruth);
                    }
                    if (!p.LoadFlags.TestFlag(LoadFlags.Active) || !p.LoadFlags.TestFlag(LoadFlags.Spawned) || p.Health == 0
                        || p.NodeRef.PartIndex == -1)
                    {
                        hist.Remove(i);
                        continue;
                    }
                    samples++;
                    int truth = p.NodeRef.PartIndex;
                    int look = PlayerEntity.NetSpawnNodeRef(scene, p.Position).PartIndex;
                    if (look != truth)
                    {
                        lookupWrong++;
                        wrongSpots[(truth, look)] = wrongSpots.GetValueOrDefault((truth, look)) + 1;
                    }
                    if (!hist.TryGetValue(i, out var h)) hist[i] = h = new List<(NodeRef, Vector3)>();
                    for (int k = 0; k < ks.Length; k++)
                    {
                        if (h.Count < ks[k]) continue;
                        var (pr, pp) = h[h.Count - ks[k]];
                        jumps[k]++;
                        if (PlayerEntity.NetSnapNodeRef(scene, pr, pp, p.Position).PartIndex != truth) jumpWrong[k]++;
                    }
                    h.Add((p.NodeRef, p.Position));
                    if (h.Count > 100) h.RemoveAt(0);
                }
            });
            Console.WriteLine($"  replay {r.Verdict}: {samples} live player frames; lookup alone wrong {lookupWrong}"
                + $" ({100.0 * lookupWrong / Math.Max(1, samples):0.0}%)");
            for (int k = 0; k < ks.Length; k++)
            {
                Console.WriteLine($"    NetSnapNodeRef from {ks[k]} frames back: wrong {jumpWrong[k]}/{jumps[k]}"
                    + $" ({100.0 * jumpWrong[k] / Math.Max(1, jumps[k]):0.00}%)");
            }
            foreach (var kv in wrongSpots.OrderByDescending(k => k.Value).Take(12))
            {
                Console.WriteLine($"    true part {kv.Key.Item1} -> lookup {kv.Key.Item2}: {kv.Value} frames");
            }
            Console.WriteLine($"  viewer (old HostView lookup at a seat's camera): wrong {viewWrong}/{viewFrames}"
                + $" ({100.0 * viewWrong / Math.Max(1, viewFrames):0.0}%); runs of 30+ frames:");
            foreach (var v in viewRuns.Where(v => v.Len >= 30).OrderBy(v => v.Start))
            {
                Console.WriteLine($"    seat {v.Seat} {v.Start / 3600}:{v.Start / 60 % 60:00} ({v.Start}) {v.Len / 60.0:0.0} s: camera in part {v.Truth}, lookup {v.Look}");
            }
        }
    }
}
