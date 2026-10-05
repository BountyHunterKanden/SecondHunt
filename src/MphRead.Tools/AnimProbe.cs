using System;
using System.IO;
using System.Linq;
using MphRead.Entities;

namespace MphRead
{
    // Read-only discovery dump of each DS hunter's REAL skeleton + animation table, straight from the
    // user's extracted ROM data: model scale, node hierarchy (names/parents), which nodes feed the DS
    // matrix stack, and the frame count of every PlayerAnimation slot. Grounds the Android per-frame
    // animation port in measured data instead of assumptions. Run: MphRead.Tools.dll -animprobe [Kanden]
    internal static class AnimProbe
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var hunters = args.Length > 1
                ? new[] { Enum.Parse<Hunter>(args[1], ignoreCase: true) }
                : new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };
            foreach (Hunter h in hunters)
            {
                string name = Metadata.HunterModels[h][0];
                Model m = Read.GetModelInstance(name).Model;
                PlayerValues pv = Metadata.PlayerValues[(int)h];
                Console.WriteLine($"=== {h} ({name}) scale={m.Scale.X} hunterScale={Metadata.HunterScales[h]:0.####} " +
                    $"minPickupH={Fixed.ToFloat(pv.MinPickupHeight):0.###} maxPickupH={Fixed.ToFloat(pv.MaxPickupHeight):0.###} " +
                    $"nodes={m.Nodes.Count} stack={m.NodeMatrixIds.Count} meshes={m.Meshes.Count}");
                for (int i = 0; i < m.Nodes.Count; i++)
                {
                    Node n = m.Nodes[i];
                    int slot = m.NodeMatrixIds.ToList().IndexOf(i);
                    Console.WriteLine($"  [{i,2}] {n.Name,-16} parent={n.ParentIndex,2} child={n.ChildIndex,2} next={n.NextIndex,2} " +
                        $"meshes={n.MeshCount} stackSlot={(slot >= 0 ? slot.ToString() : "-")} bb={n.BillboardMode}");
                }
                var groups = m.AnimationGroups.Node;
                Console.Write($"  node anim groups={groups.Count}:");
                for (int a = 0; a < groups.Count; a++)
                {
                    string label = Enum.IsDefined(typeof(PlayerAnimation), (sbyte)a) ? ((PlayerAnimation)a).ToString() : "?";
                    Console.Write($" {a}:{label}={groups[a].FrameCount}f/{groups[a].Animations.Count}n");
                }
                Console.WriteLine();
                FrontAxis(h, m);
                // joint positions in the Idle pose (model space; the rig faces -Z) -- which side is "L_"?
                var skj = new MphRecomp.Anim.DsSkeleton(m); var pj = new MphRecomp.Anim.SkeletonPose(skj.Count); var wj = new OpenTK.Mathematics.Matrix4[skj.Count];
                MphRecomp.Anim.DsSkeleton.Sample(m, (int)PlayerAnimation.Idle, 0, pj); skj.Fk(pj, wj);
                Console.WriteLine("  idle joints: " + string.Join(" ", Enumerable.Range(0, skj.Count).Select(i => { var t = wj[i].ExtractTranslation(); return $"{skj.Names[i]}({t.X:0.00},{t.Y:0.00},{t.Z:0.00})"; })));
            }
            SpawnFacings("MP1 SANCTORUS");
        }

        // Which way does the rig face? Feet extend FORWARD of the ankle joint, so the mean offset of
        // ankle-bound vertices from the ankle position (Idle pose, model space) points model-front.
        static void FrontAxis(Hunter h, Model m)
        {
            var a = new MphRecomp.Anim.BipedAnimator(m);
            a.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            var pal = new float[16 * 32];
            MphRecomp.Anim.HunterRig.Pose(a, 0f, pal);
            var skin = MphRecomp.Render.GeometryBaker.Bake(m, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var ids = m.NodeMatrixIds.ToList();
            foreach (string ankle in new[] { "L_ankle", "R_ankle" })
            {
                int node = m.GetNodeIndexByName(ankle);
                int slot = ids.IndexOf(node);
                if (slot < 0) continue;
                var joint = m.Nodes[node].Animation.ExtractTranslation();
                OpenTK.Mathematics.Vector3 sum = default; int n = 0;
                foreach (var b in skin)
                    for (int i = 0; i + 11 < b.Verts.Count; i += 12)
                    {
                        if ((int)b.Verts[i + 11] != slot) continue;
                        var w = OpenTK.Mathematics.Vector3.TransformPosition(new OpenTK.Mathematics.Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]), m.Nodes[node].Animation);
                        sum += w - joint; n++;
                    }
                if (n > 0) { var mean = sum / n; Console.WriteLine($"  {ankle}: {n} verts, mean offset from joint = ({mean.X:0.000}, {mean.Y:0.000}, {mean.Z:0.000})  => toes point model {(MathF.Abs(mean.Z) > MathF.Abs(mean.X) ? (mean.Z > 0 ? "+Z" : "-Z") : (mean.X > 0 ? "+X" : "-X"))}"); }
            }
        }

        // Do the room's spawns face its centre? (dot of facing with the direction to the centre)
        static void SpawnFacings(string room)
        {
            RoomMetadata meta = Metadata.RoomMetadata[room];
            var ents = Read.GetEntities(meta.EntityPath!, layerId: -1, meta.FirstHunt, allowHook: true);
            int toward = 0, away = 0;
            foreach (var e in ents)
            {
                if (e.Type != EntityType.PlayerSpawn) continue;
                var toC = new OpenTK.Mathematics.Vector3(-e.Position.X, 0, -e.Position.Z);
                if (toC.LengthSquared < 1e-4f) continue;
                float dot = OpenTK.Mathematics.Vector3.Dot(toC.Normalized(), new OpenTK.Mathematics.Vector3(e.FacingVector.X, 0, e.FacingVector.Z).Normalized());
                if (dot > 0) toward++; else away++;
            }
            Console.WriteLine($"{room}: spawns facing toward centre={toward}, away={away}");
            // entity layers: how many PlayerSpawns each layer holds, and how many sit on the same spot
            var all = ents.Where(e => e.Type == EntityType.PlayerSpawn).ToList();
            int dupes = all.Count - all.Select(e => (MathF.Round(e.Position.X, 2), MathF.Round(e.Position.Z, 2))).Distinct().Count();
            Console.WriteLine($"  all layers: {all.Count} PlayerSpawns, {dupes} share a position with another");
            for (int i = 0; i < all.Count; i++)
            {
                Console.WriteLine($"    spawn #{i,2} id {all[i].EntityId,3} pos=({all[i].Position.X,6:0.00},{all[i].Position.Y,5:0.00},{all[i].Position.Z,6:0.00}) layers=0x{all[i].LayerMask:X4}");
                for (int j = i + 1; j < all.Count; j++)
                    if ((all[i].Position - all[j].Position).Length < 1.2f)
                        Console.WriteLine($"      !! within 1.2 of spawn #{j} ({(all[i].Position - all[j].Position).Length:0.00} apart)");
            }
            foreach (var g in all.GroupBy(e => (MathF.Round(e.Position.X, 2), MathF.Round(e.Position.Z, 2))).Where(g => g.Count() > 1))
                Console.WriteLine($"    same XZ {g.Key}: " + string.Join(" | ", g.Select(e => $"id {e.EntityId} y={e.Position.Y:0.00} facing=({e.FacingVector.X:0.00},{e.FacingVector.Z:0.00}) layers=0x{e.LayerMask:X4}")));
            for (int layer = 0; layer < 16; layer++)
            {
                var l = Read.GetEntities(meta.EntityPath!, layerId: layer, meta.FirstHunt, allowHook: true).Where(e => e.Type == EntityType.PlayerSpawn).ToList();
                if (l.Count == 0) continue;
                int d = l.Count - l.Select(e => (MathF.Round(e.Position.X, 2), MathF.Round(e.Position.Z, 2))).Distinct().Count();
                Console.WriteLine($"  layer {layer,2}: {l.Count} PlayerSpawns, {d} duplicates");
            }
            foreach (GameMode mode in new[] { GameMode.Battle, GameMode.Survival, GameMode.Capture, GameMode.Bounty, GameMode.Nodes, GameMode.Defender, GameMode.PrimeHunter })
                Console.WriteLine($"  {mode} (2p/3p/4p) -> entity layer {Metadata.GetMultiplayerEntityLayer(mode, 2)}/{Metadata.GetMultiplayerEntityLayer(mode, 3)}/{Metadata.GetMultiplayerEntityLayer(mode, 4)}");
        }
    }

    // Shared: point the desktop data layer at the extracted game files (the desktop bin's paths.txt).
    internal static class ToolPaths
    {
        public static bool UseDesktopExtraction()
        {
            string repoBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MphRead", "bin", "Debug", "net9.0"));
            if (!File.Exists(Path.Combine(repoBin, "paths.txt")))
            {
                Console.WriteLine($"  [SKIP] extracted game files not found (expected paths.txt in {repoBin}).");
                return false;
            }
            Directory.SetCurrentDirectory(repoBin);
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            return true;
        }
    }
}
