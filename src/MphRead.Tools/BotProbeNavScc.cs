using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRead
{
    // Bot move solver P3 (docs/BOT_MOVE_SOLVER.md), measure only, no game change: what ArenaNav.Build keeps for a First Hunt
    // arena with the import's own inputs (the spawns as anchors, the import's pad landings), against what it keeps when every
    // floor sample is an anchor (its biggest strongly connected part).
    //
    //   -botprobe fhroot=<root> navscc=<FH arena key, e.g. MP5>
    internal static partial class BotProbe
    {
        private static void NavScc(string key)
        {
            Paths.UpdatePaths(); Paths.ChooseMphPath(); Paths.ChooseFhPath();
            _ = MphRecomp.Multiplayer.MatchArenas.All.Count(); // registers the First Hunt rooms from ImportRoot
            FirstHuntArena.Def d = FirstHuntArena.Find(key) ?? throw new ArgumentException($"no First Hunt arena {key}");
            RoomMetadata meta = Metadata.GetRoomByName(d.Room).Item1!;
            var col = Formats.Collision.Collision.GetCollision(meta, roomLayerMask: -1);
            var cinfo = (Formats.Collision.MphCollisionInfo)col.Info;
            var tris = new List<(Vector3, Vector3, Vector3)>();
            foreach (Formats.Collision.CollisionData data in cinfo.Data)
            {
                var pts = Enumerable.Range(0, data.PointIndexCount).Select(i => cinfo.Points[cinfo.PointIndices[data.PointStartIndex + i]]).ToList();
                for (int j = 1; j + 1 < pts.Count; j++) tris.Add((pts[0], pts[j], pts[j + 1]));
            }
            var ray = new ArenaRaycaster(tris);
            FirstHuntArena.Info info = System.Text.Json.JsonSerializer.Deserialize<FirstHuntArena.Info>(
                File.ReadAllText(Path.Combine(FirstHuntArena.ItemDir(EchoesArena.ImportRoot!, d), "arena.json")))!;
            var jumps = info.PadLandings.Where(v => v.Length == 6)
                .Select(v => (new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]))).ToList();
            List<Vector3> spawns = Read.GetEntities(meta.EntityPath!, -1, firstHunt: false)
                .Where(e => e.Type == EntityType.PlayerSpawn).Select(e => e.Position).ToList();
            Vector2? grid = d.CentreGrid ? Vector2.Zero : null;
            ArenaNav.Graph a = ArenaNav.Build(ray, spawns, info.KillHeight, jumps, gridOrigin: grid, echoesMoves: false, padReach: 3.2f);
            Console.WriteLine($"{d.Key}: {spawns.Count} spawns {String.Join(" ", spawns.Select(Fmt))}, {jumps.Count} pad flights");
            Console.WriteLine($"  spawns as anchors (the import): {a.Nodes.Count} nodes: " + String.Join(" ", a.Nodes.Take(20).Select(Fmt)));
            var all = new List<Vector3>();
            for (float x = ray.Min.X + 1; x < ray.Max.X; x += 1.5f)
            {
                for (float z = ray.Min.Z + 1; z < ray.Max.Z; z += 1.5f)
                {
                    float top = ray.Max.Y + 1;
                    while (top > info.KillHeight && ray.FloorBelow(new Vector3(x, top, z), top - info.KillHeight) is Vector3 f)
                    {
                        all.Add(f);
                        top = f.Y - 0.3f;
                    }
                }
            }
            ArenaNav.Graph b = ArenaNav.Build(ray, all, info.KillHeight, jumps, gridOrigin: grid, echoesMoves: false, padReach: 3.2f);
            Console.WriteLine($"  {all.Count} floor samples as anchors: {b.Nodes.Count} nodes, y {b.Nodes.Min(n => n.Y):0.0}..{b.Nodes.Max(n => n.Y):0.0}");
            foreach (Vector3 s in spawns)
            {
                Vector3 near = b.Nodes.MinBy(n => (n - s).LengthSquared);
                Console.WriteLine($"  spawn {Fmt(s)}: nearest node of that part {Fmt(near)}, {(near - s).Length:0.0} away");
            }
        }
    }
}
