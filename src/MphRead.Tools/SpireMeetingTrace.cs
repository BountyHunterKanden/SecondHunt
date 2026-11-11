using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -spiretrace [frames=1800] [every=30] [nodes]: the Spire meeting in High Ground (UNIT1_RM1, first visit). Samus
    // stands still on TriggerVolume#37 (which spawns Spire from EnemySpawn#21); prints Spire's position, current/goal
    // node, shots fired and Samus's hp every `every` frames -- the recomp side of the BizHawk side-by-side (vanilla
    // positions at f300/600/900/1200/1500 from US rev 1). `nodes` also lists the encounter's node file. `at=x,z` moves
    // Samus to that floor spot (or x,y,z: a node's position) 60 frames after the trigger (the BizHawk script's SP_AT does the same).
    // `rng=<hex>` seeds both RNGs just before Samus steps on the trigger (Spire's first vantage is drawn at his spawn);
    // `ctx` adds his AI execution tree and Rng2 to each row.
    internal static class SpireMeetingTrace
    {
        private static readonly FieldInfo _node40 = typeof(PlayerEntity.PlayerAiData)
            .GetField("_node40", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo _node3C = typeof(PlayerEntity.PlayerAiData)
            .GetField("_node3C", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo _nodeList = typeof(PlayerEntity.PlayerAiData)
            .GetField("_nodeList", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo _tree = typeof(PlayerEntity.PlayerAiData)
            .GetField("_executionTree", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo _queued = typeof(PlayerEntity.PlayerAiData)
            .GetField("_queuedFindEntityAction", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // `ctx`: the AI execution tree (Field4/5/9 per depth, the personality's state and goal choice)
        private static string Ctx(PlayerEntity p)
        {
            var parts = new List<string>();
            foreach (object? c in (Array)_tree.GetValue(p.AiData)!)
            {
                if (c == null)
                {
                    continue;
                }
                Type t = c.GetType();
                int Get(string n) => Convert.ToInt32(t.GetProperty(n)!.GetValue(c));
                if (Get("Field4") == 0 && Get("Field9") == 0)
                {
                    continue;
                }
                parts.Add($"{Get("Func24Id")}:{Get("Field4")}/{Get("Field5")}/{Get("Field9")}/{Get("FieldB")}/{Get("FieldC")}");
            }
            return $" ctx {String.Join(" ", parts)} q {_queued.GetValue(p.AiData)}";
        }

        private static readonly PropertyInfo _legs = typeof(PlayerEntity)
            .GetProperty("Biped1Anim", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int frames = Arg(args, "frames", 1800);
            int every = Arg(args, "every", 30);
            using CampaignHost host = CampaignHost.Start("UNIT1_RM1", arriving: false, setupSave: s =>
            {
                s.BossFlags = 0; // first visit: the meeting layer
            });
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            for (int i = 0; i < 30; i++)
            {
                host.Step(input);
            }
            if (args.Contains("dump"))
            {
                Console.WriteLine($"Samus start {Fmt(host.Player.Position)}");
                foreach (TriggerVolumeEntity t in host.Scene.GetTriggerVolumeEntities())
                {
                    TriggerVolumeEntityData d = t.Data;
                    Console.WriteLine($"  trigger {t.Id} {d.Subtype} {Fmt(t.Position)} active {t.Active} flags {d.TriggerFlags} "
                        + $"threshold {d.TriggerThreshold} parent {d.ParentId}:{d.ParentMessage} child {d.ChildId}:{d.ChildMessage}");
                }
                foreach (EnemySpawnEntity e in host.Scene.GetEnemySpawnEntities())
                {
                    EnemySpawnEntityData sd = e.Data;
                    Console.WriteLine($"  enemyspawn {e.Id} {Fmt(e.Position)} {sd.EnemyType} hp {sd.SpawnerHealth} msgs {sd.EntityId1}:{sd.Message1} "
                        + $"{sd.EntityId2}:{sd.Message2} {sd.EntityId3}:{sd.Message3}");
                }
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e.Type is EntityType.CameraSequence or EntityType.AreaVolume or EntityType.Door)
                    {
                        Console.WriteLine($"  {e.Type} {e.Id} {Fmt(e.Position)}");
                    }
                    if (e is CamSeqEntity cs)
                    {
                        Console.WriteLine($"    seq {cs.Name} blockInput {cs.Data.BlockInput} end {cs.Data.EndMessage}");
                        foreach (CameraSequenceKeyframe k in cs.Sequence.Keyframes)
                        {
                            Console.WriteLine($"      key move {k.MoveTime:0.00} hold {k.HoldTime:0.00} msg {(Message)k.MessageId} -> {k.MessageTargetType}:{k.MessageTargetId} p{k.MessageParam}");
                        }
                    }
                }
            }
            string? rng = args.FirstOrDefault(x => x.StartsWith("rng="));
            if (rng != null)
            {
                uint seed = Convert.ToUInt32(rng[4..], 16);
                Rng.SetRng1(seed);
                Rng.SetRng2(seed * 0x9E3779B1);
            }
            var trigger = new Vector3(4.3f, 6.3f, -3.5f);
            host.PlacePlayer(trigger, new Vector3(1, 0, 0));
            long start = host.Frame;
            PlayerEntity? spire = null;
            int shots = 0;
            int lastTss = 255;
            int startHp = host.Player.Health;
            int gap = 0;
            int offVantage = 0;
            long lastShot = start;
            string? at = args.FirstOrDefault(x => x.StartsWith("at="));
            for (int f = 0; f < frames && !host.Ended; f++)
            {
                host.Step(input);
                if (at != null && host.Frame - start == 60)
                {
                    float[] xz = at[3..].Split(',').Select(Single.Parse).ToArray();
                    host.PlacePlayer(xz.Length == 3 ? new Vector3(xz[0], xz[1] + 1, xz[2]) : new Vector3(xz[0], 5.5f, xz[1]),
                        new Vector3(1, 0, 0));
                }
                spire ??= Enumerable.Range(1, Math.Max(0, PlayerEntity.PlayerCount - 1))
                    .Select(i => PlayerEntity.Players[i]).FirstOrDefault(p => p.IsBot && p.Health > 0);
                if (spire == null)
                {
                    continue;
                }
                if (spire.TimeSinceShot < lastTss)
                {
                    shots++;
                    gap = Math.Max(gap, (int)(host.Frame - lastShot));
                    lastShot = host.Frame;
                }
                // off a vantage node: not within 1.5 of one (the "nook" in the report)
                var nodes = (IReadOnlyList<NodeData3>)_nodeList.GetValue(spire.AiData)!;
                if (!nodes.Any(n => n.NodeType == NodeType.Vantage && (n.Position - spire.Position).Xz.Length < 1.5f))
                {
                    offVantage++;
                }
                lastTss = spire.TimeSinceShot;
                long rel = host.Frame - start;
                if (rel % every == 0)
                {
                    var node = (NodeData3?)_node40.GetValue(spire.AiData);
                    var goal = (NodeData3?)_node3C.GetValue(spire.AiData);
                    string Near(NodeData3? n) => n == null ? "-" : $"{n.Id}{n.NodeType.ToString()[0]} d{(n.Position - spire.Position).Length:0.00}";
                    Console.WriteLine($"f{rel,5} Spire {Fmt(spire.Position)} hp {spire.Health} grounded {(spire.Flags1.TestFlag(PlayerFlags1.Grounded) ? 1 : 0)} "
                        + $"node40 {Near(node)} node3C {Near(goal)} shots {shots}  Samus {Fmt(host.Player.Position)} hp {host.Player.Health}"
                        + $" legs {_legs.GetValue(spire)} speed {spire.Speed.Xz.Length:0.000} aistart {(spire.AiData.Flags2.TestFlag(AiFlags2.AiStart) ? 1 : 0)} seq {CameraSequence.Current?.Name ?? "-"}"
                        + (args.Contains("ctx") ? Ctx(spire) + $" rng2 {Rng.Rng2:x8}" : ""));
                }
            }
            gap = Math.Max(gap, (int)(host.Frame - lastShot));
            Console.WriteLine($"done: {(spire == null ? "Spire never spawned" : $"{shots} shots")}, Samus hp {startHp} -> {host.Player.Health}, "
                + $"longest gap {gap}, off vantage {offVantage}, Spire hp {spire?.Health}");
            if (args.Contains("nodes") && spire != null)
            {
                var list = (IReadOnlyList<NodeData3>)_nodeList.GetValue(spire.AiData)!;
                foreach (NodeData3 n in list)
                {
                    Console.WriteLine($"  node {n.Id} {n.NodeType} {Fmt(n.Position)} max {n.MaxDistance:0.00} f4 {n.Field4}");
                }
            }
        }

        private static int Arg(string[] args, string name, int fallback)
        {
            string? a = args.FirstOrDefault(x => x.StartsWith(name + "="));
            return a == null ? fallback : Int32.Parse(a[(name.Length + 1)..]);
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";
    }
}
