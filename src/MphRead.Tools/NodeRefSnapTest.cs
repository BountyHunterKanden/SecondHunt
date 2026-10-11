using System;
using System.Collections.Generic;
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
    //   -noderefsnaptest [arena=Council Chamber] [arena=...] [at=x,y,z]
    // Truth = each player spawn's own NodeRef (authored, what the host spawns with). Checks the part lookups at every
    // spawn and that a spawn-to-spawn snap through PlayerEntity.NetSnapNodeRef lands in the spawn's own part.
    internal static class NodeRefSnapTest
    {
        public static void Run(string[] args)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            var arenas = args.Skip(1).Where(a => a.StartsWith("arena=")).Select(a => a[6..]).ToList();
            if (arenas.Count == 0) arenas = ["Council Chamber", "Sanctorus", "Data Shrine", "Combat Hall", "Alinos Perch"];
            var spots = args.Skip(1).Where(a => a.StartsWith("at=")).Select(a => a[3..].Split(',').Select(float.Parse).ToArray())
                .Select(f => new Vector3(f[0], f[1], f[2])).ToList();
            var idle = new CampaignInput { SelectWeapon = BeamType.None };
            bool pass = true;
            foreach (string name in arenas)
            {
                RoomMetadata arena = MatchArenas.Find(name) ?? throw new ArgumentException("no arena " + name);
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 0);
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
    }
}
