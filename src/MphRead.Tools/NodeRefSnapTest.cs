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
            // sim=<arena> [steps=N] [bots=N]: a bots match on the PC, the same puppet checks with no replay file
            if (args.Skip(1).FirstOrDefault(a => a.StartsWith("sim=")) is string sim)
            {
                int steps = args.Skip(1).Where(a => a.StartsWith("steps=")).Select(a => Int32.Parse(a[6..])).DefaultIfEmpty(36000).First();
                int bots = args.Skip(1).Where(a => a.StartsWith("bots=")).Select(a => Int32.Parse(a[5..])).DefaultIfEmpty(3).First();
                RunReplay(null, sim[4..], steps, bots);
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
                        + $" containing {scene.GetNodeRefContaining(at).PartIndex}, parts with geometry there "
                        + String.Join(" ", Enumerable.Range(0, 32).Where(i => scene.PartHasGeometryAt(i, at))));
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
        private static void RunReplay(string? path, string? simArena = null, int simSteps = 0, int simBots = 0)
        {
            var file = path == null ? null : MphRecomp.Replay.ReplayFile.Load(path);
            if (file != null)
            {
                Console.WriteLine($"  {Path.GetFileName(path)}: {file.Settings.Mode} {file.Settings.Arena}, {file.Steps.Count} steps; seats "
                    + String.Join(", ", file.Settings.Players.Select((p, i) => $"{i} {p.Character ?? p.Hunter.ToString()}{(p.Bot ? " (bot)" : "")}")));
            }
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
            var chainGood = new Dictionary<int, NodeRef>();
            var chainBad = new Dictionary<int, NodeRef>();
            int chainFrames = 0, chainGoodWrong = 0, chainBadWrong = 0;
            var goodMisses = new Dictionary<int, int>();
            var badMisses = new Dictionary<int, int>();
            var puppets = new List<Puppet>();
            foreach (bool old in new[] { true, false })
            {
                foreach (int k in new[] { 1, 3, 6 }) puppets.Add(new Puppet(old, k));
            }
            void OnStep(int step, Scene scene)
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
                        foreach (Puppet pu in puppets) pu.Lives.Remove(i);
                        chainGood.Remove(i);
                        chainBad.Remove(i);
                        goodMisses.Remove(i);
                        badMisses.Remove(i);
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
                    // a puppet's part carried step by step through NetSnapNodeRef, as a guest poses another seat: one
                    // chain started on the true part (should never leave it), one started on a wrong part (should recover)
                    if (h.Count > 0)
                    {
                        var (lr, lp) = h[^1];
                        chainFrames++;
                        NodeRef good = chainGood.TryGetValue(i, out NodeRef g) ? g : lr;
                        int gm = goodMisses.GetValueOrDefault(i);
                        chainGood[i] = good = PlayerEntity.NetSnapNodeRef(scene, good, lp, p.Position, ref gm);
                        goodMisses[i] = gm;
                        if (good.PartIndex != truth) chainGoodWrong++;
                        if (!chainBad.TryGetValue(i, out NodeRef bad))
                        {
                            bad = lr;
                            foreach (PlayerSpawnEntity e in scene.GetPlayerSpawnEntities())
                            {
                                if (e.NodeRef.PartIndex != -1 && e.NodeRef.PartIndex != lr.PartIndex) { bad = e.NodeRef; break; }
                            }
                        }
                        int bm = badMisses.GetValueOrDefault(i);
                        chainBad[i] = bad = PlayerEntity.NetSnapNodeRef(scene, bad, lp, p.Position, ref bm);
                        badMisses[i] = bm;
                        if (bad.PartIndex != truth) chainBadWrong++;
                    }
                    if (p != PlayerEntity.Main)
                    {
                        foreach (Puppet pu in puppets) pu.Step(scene, step, i, p.NodeRef, p.Position);
                    }
                    h.Add((p.NodeRef, p.Position));
                    if (h.Count > 100) h.RemoveAt(0);
                }
            }
            string verdict;
            if (file != null)
            {
                verdict = MphRecomp.Replay.ReplayVerify.Run(file, OnStep).Verdict.ToString();
            }
            else
            {
                RoomMetadata arena = MatchArenas.Find(simArena!) ?? throw new ArgumentException("no arena " + simArena);
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, simBots);
                settings.TimeLimitSeconds = simSteps / 60 + 60;
                using CampaignHost host = CampaignHost.StartMatch(settings);
                var idle = new CampaignInput { SelectWeapon = BeamType.None };
                for (int step = 0; step < simSteps; step++)
                {
                    host.Step(idle);
                    OnStep(step, host.Scene);
                }
                verdict = $"sim {arena.InGameName} ({arena.Name}), {simBots} bots, {simSteps} steps";
            }
            Console.WriteLine($"  replay {verdict}: {samples} live player frames; lookup alone wrong {lookupWrong}"
                + $" ({100.0 * lookupWrong / Math.Max(1, samples):0.0}%)");
            for (int k = 0; k < ks.Length; k++)
            {
                Console.WriteLine($"    NetSnapNodeRef from {ks[k]} frames back: wrong {jumpWrong[k]}/{jumps[k]}"
                    + $" ({100.0 * jumpWrong[k] / Math.Max(1, jumps[k]):0.00}%)");
            }
            Console.WriteLine($"    puppet chain (NetSnapNodeRef each frame): from the true part wrong {chainGoodWrong}/{chainFrames};"
                + $" started on a wrong part, wrong {chainBadWrong}/{chainFrames} (one per life until it recovers)");
            foreach (var kv in wrongSpots.OrderByDescending(k => k.Value).Take(12))
            {
                Console.WriteLine($"    true part {kv.Key.Item1} -> lookup {kv.Key.Item2}: {kv.Value} frames");
            }
            Console.WriteLine("  puppets as the viewer's guest would pose them (snaps every k frames, blended, first part from the"
                + " spawn lookup); hidden = its true part is drawn for the viewer but the puppet's part isn't; any-part = also draw"
                + " when a part holding the spot is drawn; extra = drawn by that rule while its true part isn't drawn:");
            foreach (Puppet pu in puppets)
            {
                Console.WriteLine($"    {(pu.Old ? "old" : "new")} code, k {pu.K}: {pu.Frames} frames, wrong part {pu.Wrong},"
                    + $" stranded (no geometry at the spot) {pu.Stranded}; hidden {pu.Hidden} (true part drawn {pu.TrueDrawn}),"
                    + $" any-part hidden {pu.AnyHidden}, any-part extra {pu.AnyExtra}; longest hidden run {pu.LongestHidden / 60.0:0.0} s");
                foreach (string e in pu.Events.Take(6)) Console.WriteLine("      " + e);
                if (pu.Events.Count > 6) Console.WriteLine($"      (+{pu.Events.Count - 6} more strandings)");
            }
            Console.WriteLine($"  viewer (old HostView lookup at a seat's camera): wrong {viewWrong}/{viewFrames}"
                + $" ({100.0 * viewWrong / Math.Max(1, viewFrames):0.0}%); runs of 30+ frames:");
            foreach (var v in viewRuns.Where(v => v.Len >= 30).OrderBy(v => v.Start))
            {
                Console.WriteLine($"    seat {v.Seat} {v.Start / 3600}:{v.Start / 60 % 60:00} ({v.Start}) {v.Len / 60.0:0.0} s: camera in part {v.Truth}, lookup {v.Look}");
            }
        }

        // one way a guest could carry another seat's room part: NetSnapNodeRef (or the old walk-only step) fed that seat's
        // positions as snapshots every K frames, blended between the last two, K frames late
        private sealed class Puppet(bool old, int k)
        {
            public readonly bool Old = old;
            public readonly int K = k;
            public readonly Dictionary<int, Life> Lives = [];
            public int Frames, Wrong, Stranded, Hidden, TrueDrawn, AnyHidden, AnyExtra, LongestHidden;
            public readonly List<string> Events = [];

            public sealed class Life
            {
                public readonly List<(Vector3 Pos, NodeRef Truth)> Path = [];
                public NodeRef Part = NodeRef.None;
                public Vector3 Last;
                public int Misses, HiddenRun;
                public bool WasStranded;
            }

            public void Step(Scene scene, int step, int seat, NodeRef truth, Vector3 pos)
            {
                if (!Lives.TryGetValue(seat, out Life? life)) Lives[seat] = life = new Life();
                life.Path.Add((pos, truth));
                int n = life.Path.Count - 1; // frames into this life
                float d = n - K; // the blended time, K frames late
                if (d < 0) return;
                int s0 = (int)(d / K) * K, s1 = Math.Min(s0 + K, n);
                float t = s1 == s0 ? 0 : (d - s0) / (s1 - s0);
                Vector3 at = Vector3.Lerp(life.Path[s0].Pos, life.Path[s1].Pos, t);
                NodeRef trueRef = life.Path[(int)MathF.Round(d)].Truth;
                NodeRef before = life.Part;
                if (life.Part.PartIndex == -1)
                {
                    life.Part = PlayerEntity.NetSpawnNodeRef(scene, at);
                }
                else if (Old)
                {
                    life.Part = (at - life.Last).LengthSquared <= 1 ? scene.UpdateNodeRef(life.Part, life.Last, at)
                        : PlayerEntity.NetSnapNodeRef(scene, life.Part, life.Last, at);
                }
                else
                {
                    life.Part = PlayerEntity.NetSnapNodeRef(scene, life.Part, life.Last, at, ref life.Misses);
                }
                float moved = (at - life.Last).Length;
                life.Last = at;
                Frames++;
                if (life.Part.PartIndex != trueRef.PartIndex) Wrong++;
                bool stranded = !scene.PartHasGeometryAt(life.Part.PartIndex, at);
                if (stranded)
                {
                    Stranded++;
                    if (!life.WasStranded)
                    {
                        string geo = String.Join(" ", Enumerable.Range(0, 32).Where(i => scene.PartHasGeometryAt(i, at)));
                        Events.Add($"seat {seat} step {step} ({n} frames into the life): part {before.PartIndex} -> {life.Part.PartIndex}"
                            + $" after a {(before.PartIndex == -1 ? "spawn lookup" : $"{moved:0.00}-unit move")} at {at}, true part"
                            + $" {trueRef.PartIndex}, geometry there: {geo}");
                    }
                }
                life.WasStranded = stranded;
                bool trueDrawn = scene.IsNodeRefStepVisible(trueRef);
                bool drawn = scene.IsNodeRefStepVisible(life.Part);
                bool anyDrawn = drawn || Enumerable.Range(0, 32).Any(i => scene.PartHoldsSpot(i, at, slack: 1)
                    && scene.IsNodeRefStepVisible(new NodeRef(i, 0, 0)));
                if (trueDrawn) TrueDrawn++;
                if (trueDrawn && !drawn)
                {
                    Hidden++;
                    LongestHidden = Math.Max(LongestHidden, ++life.HiddenRun);
                }
                else
                {
                    life.HiddenRun = 0;
                }
                if (trueDrawn && !anyDrawn) AnyHidden++;
                if (!trueDrawn && anyDrawn && !drawn) AnyExtra++;
            }
        }
    }
}
