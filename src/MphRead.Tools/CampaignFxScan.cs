using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Effects;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;

namespace MphRead
{
    // -fxscan: the owner's Arcterra escape crash (2026-10-01 23:21, Odin): ArgumentOutOfRangeException on
    // List<Particle> in Scene.ProcessEffects. The two List<Particle> reads there are ParticleDefinitions[0] (new particle)
    // and ParticleDefinitions[ParticleId] (SetParticleId, clamped only from above). Lists every effect element that could
    // hit either: no particle definitions, or a SetParticleId function whose value can go below 0.
    internal static partial class CampaignSim
    {
        public static void FxScan(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int loaded = 0;
            int failed = 0;
            for (int id = 1; id < Metadata.Effects.Count; id++)
            {
                (string name, string? archive) = Metadata.Effects[id];
                if (String.IsNullOrEmpty(name))
                {
                    continue;
                }
                Effect effect;
                try
                {
                    effect = Read.LoadEffect(id, persistent: false);
                    loaded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"{id,3} {name}: load failed: {ex.GetType().Name} {ex.Message}");
                    continue;
                }
                foreach (EffectElement element in effect.Elements)
                {
                    bool spawns = element.Actions.ContainsKey(FuncAction.IncreaseParticleAmount);
                    if (element.Particles.Count == 0)
                    {
                        Console.WriteLine($"{id,3} {name} / {element.Name}: NO particle definitions"
                            + $" (spawns particles: {spawns})");
                    }
                    if (element.Actions.TryGetValue(FuncAction.SetParticleId, out FxFuncInfo? info))
                    {
                        Console.WriteLine($"{id,3} {name} / {element.Name}: SetParticleId defs={element.Particles.Count}"
                            + $" {Describe(element.Funcs, info, 0)}");
                    }
                }
            }
            Console.WriteLine($"loaded {loaded}, failed {failed}");
        }

        // -fxpool [room=UNIT4_RM1] [frames=900] [doors=3]: watches MphRead's effect pools while a room runs and while
        // Samus leaves it through its doors. A healthy pool never holds the same entry twice, never has an element both
        // active and free, and never has an active element without particle definitions (the escape crash). Also counts
        // objects still holding an effect entry that's back in the free pool (ObjectEntity.RemoveEffect kept it).
        public static void FxPool(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT4_RM1";
            int frames = args.Length >= 3 ? Int32.Parse(args[2]) : 900;
            int doorCount = args.Length >= 4 ? Int32.Parse(args[3]) : 3;
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                arriving: false);
            Scene scene = host.Scene;
            int frame = 0;
            int problems = 0;
            Scene.HostEffectPoolWarning = msg => { problems++; Console.WriteLine($"  WARNING {msg}"); };
            string lastReport = "";
            void Check(string when)
            {
                string report = PoolReport(scene);
                if (report != "" && report != lastReport)
                {
                    problems++;
                    Console.WriteLine($"  f{frame,5} room {scene.RoomId} {when}: {report}");
                }
                lastReport = report;
            }
            void Step(CampaignInput input)
            {
                try
                {
                    host.Step(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  f{frame,5} room {scene.RoomId} SIM EXCEPTION {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
                frame++;
                Check("step");
            }
            for (int i = 0; i < frames; i++)
            {
                Step(default);
            }
            Console.WriteLine($"  f{frame,5} room {scene.RoomId} after {frames} frames: {Summary(scene)}");
            if (args.Contains("double"))
            {
                // the crash's mechanism on purpose: an owner that detaches its entry and later releases it again
                // (ObjectEntity.RemoveEffect then Destroy), and an entry whose elements a room change already freed
                scene.LoadEffect(105, persistent: false); // smokeBurst
                OpenTK.Mathematics.Matrix4 at = OpenTK.Mathematics.Matrix4.CreateTranslation(host.Player.Position);
                EffectEntry? a = scene.SpawnEffectGetEntry(105, at);
                if (a != null)
                {
                    scene.DetachEffectEntry(a, setExpired: false);
                    scene.UnlinkEffectEntry(a);
                    Console.WriteLine($"  double release of an entry: {Summary(scene)}");
                }
                EffectEntry? b = scene.SpawnEffectGetEntry(105, at);
                if (b != null)
                {
                    scene.ClearNonPersistentEffects();
                    scene.UnlinkEffectEntry(b);
                    Console.WriteLine($"  release after ClearNonPersistentEffects: {Summary(scene)}");
                }
                for (int i = 0; i < 300; i++)
                {
                    EffectEntry? c = scene.SpawnEffectGetEntry(105, at);
                    Step(default);
                    if (c != null && i % 3 == 0)
                    {
                        scene.UnlinkEffectEntry(c);
                    }
                }
                Console.WriteLine($"  f{frame,5} after 300 frames of spawning: {Summary(scene)}");
            }
            for (int n = 0; n < doorCount; n++)
            {
                DoorEntity? door = Doors(scene).Where(d => d.Id >= 0 && !d.Flags.TestFlag(DoorFlags.Locked) && d.TargetRoomId >= 0)
                    .OrderBy(d => (d.Position - host.Player.Position).Length).FirstOrDefault();
                if (door == null)
                {
                    Console.WriteLine($"  no unlocked door in room {scene.RoomId}");
                    break;
                }
                int from = scene.RoomId;
                bool crossed = CrossDoor(host, door, Step);
                Console.WriteLine($"  f{frame,5} door#{door.Id} room {from} -> {scene.RoomId} ({(crossed ? "crossed" : "FAILED")}): {Summary(scene)}");
                for (int i = 0; i < frames; i++)
                {
                    Step(default);
                }
                Console.WriteLine($"  f{frame,5} room {scene.RoomId} after {frames} frames: {Summary(scene)}");
            }
            Console.WriteLine(problems == 0 ? "  RESULT: pools clean" : $"  RESULT: {problems} pool problem report(s)");
        }

        // -fxobjects: every story-room object whose effect is switched on and off by its interval mask (AttachEffect):
        // the objects whose ObjectEntity.RemoveEffect runs while the room plays
        public static void FxObjects(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            for (int id = 27; id <= 92; id++)
            {
                RoomMetadata? meta = Metadata.GetRoomById(id, noThrow: true);
                if (meta?.EntityPath == null)
                {
                    continue;
                }
                IReadOnlyList<Entity> entities;
                try
                {
                    entities = Read.GetEntities(meta.EntityPath, layerId: -1, firstHunt: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{id} {meta.Name}: {ex.GetType().Name}");
                    continue;
                }
                foreach (Entity entity in entities)
                {
                    if (entity is Entity<ObjectEntityData> obj)
                    {
                        ObjectEntityData data = obj.Data;
                        bool attach = ((int)data.EffectFlags & 0x10) != 0;
                        bool destroy = ((int)data.EffectFlags & 0x20) != 0;
                        if (attach && data.EffectId > 0)
                        {
                            Console.WriteLine($"{id} {meta.Name} ({meta.InGameName}) object#{entity.EntityId} layers 0x{entity.LayerMask:X}"
                                + $" fx{data.EffectId} {Metadata.Effects[data.EffectId].Name} flags 0x{(int)data.EffectFlags:X}"
                                + $" interval {data.EffectInterval} on 0x{data.EffectOnIntervals & 0xFFFF:X4}"
                                + $"{(destroy ? " DestroyEffect" : " detach")}");
                        }
                    }
                }
            }
        }

        private static T PoolField<T>(Scene scene, string name)
        {
            return (T)typeof(Scene).GetField(name, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(scene)!;
        }

        private static string Summary(Scene scene)
        {
            var active = PoolField<List<EffectElementEntry>>(scene, "_activeElements");
            var freeEntries = PoolField<Queue<EffectEntry>>(scene, "_inactiveEffects");
            var freeElements = PoolField<Queue<EffectElementEntry>>(scene, "_inactiveElements");
            string report = PoolReport(scene);
            return $"active elements {active.Count}, free entries {freeEntries.Count}, free elements {freeElements.Count}"
                + (report == "" ? ", pools clean" : $", {report}");
        }

        private static string PoolReport(Scene scene)
        {
            var active = PoolField<List<EffectElementEntry>>(scene, "_activeElements");
            var freeEntries = PoolField<Queue<EffectEntry>>(scene, "_inactiveEffects");
            var freeElements = PoolField<Queue<EffectElementEntry>>(scene, "_inactiveElements");
            var parts = new List<string>();
            int dupEntries = freeEntries.Count - new HashSet<EffectEntry>(freeEntries).Count;
            if (dupEntries > 0)
            {
                parts.Add($"{dupEntries} effect entr{(dupEntries == 1 ? "y" : "ies")} free twice");
            }
            var freeSet = new HashSet<EffectElementEntry>(freeElements);
            int dupElements = freeElements.Count - freeSet.Count;
            if (dupElements > 0)
            {
                parts.Add($"{dupElements} element(s) free twice");
            }
            int dupActive = active.Count - new HashSet<EffectElementEntry>(active).Count;
            if (dupActive > 0)
            {
                parts.Add($"{dupActive} element(s) active twice");
            }
            int both = active.Count(e => freeSet.Contains(e));
            if (both > 0)
            {
                parts.Add($"{both} element(s) active AND free");
            }
            var noDefs = active.Where(e => e.ParticleDefinitions.Count == 0).ToList();
            if (noDefs.Count > 0)
            {
                parts.Add($"{noDefs.Count} active element(s) with NO particle definitions"
                    + $" ({String.Join(", ", noDefs.Select(e => $"fx{e.EffectId}").Distinct())})");
            }
            var freeEntrySet = new HashSet<EffectEntry>(freeEntries);
            var stale = new List<string>();
            System.Reflection.FieldInfo? field = typeof(ObjectEntity).GetField("_effectEntry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is ObjectEntity obj && field?.GetValue(obj) is EffectEntry held && freeEntrySet.Contains(held))
                {
                    stale.Add($"object#{obj.Id}");
                }
            }
            if (stale.Count > 0)
            {
                parts.Add($"{stale.Count} object(s) holding a freed effect entry ({String.Join(", ", stale)})");
            }
            return String.Join("; ", parts);
        }

        private static string Describe(IReadOnlyDictionary<uint, FxFuncInfo> funcs, FxFuncInfo info, int depth)
        {
            string values = String.Join(" ", info.Parameters.Select(p => p == Int32.MinValue ? "END"
                : $"{p}({Fixed.ToFloat(p):0.###})"));
            if (depth < 4 && info.FuncId >= 46 && info.FuncId <= 49)
            {
                int count = info.FuncId == 49 ? 4 : 2;
                var subs = new List<string>();
                for (int i = 0; i < count && i < info.Parameters.Count; i++)
                {
                    subs.Add(funcs.TryGetValue((uint)info.Parameters[i], out FxFuncInfo? sub)
                        ? Describe(funcs, sub, depth + 1) : $"?{info.Parameters[i]}");
                }
                return $"fx{info.FuncId}[{String.Join(", ", subs)}]";
            }
            return $"fx{info.FuncId}({values})";
        }
    }
}
