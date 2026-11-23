using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Formats.Collision;

namespace MphRead
{
    // Holiday mode research (owner 2026-11-20): where vanilla places its falling snow (fx212 fallingSnow, fx228 snowFlurry)
    // and which arenas have snow surfaces (collision terrain 5), plus the snow effects' element definitions.
    //   -holidayscan
    public static class HolidayTool
    {
        static readonly int[] SnowFx = { 212, 228 };

        public static void Scan(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            for (int id = 0; id < 160; id++)
            {
                RoomMetadata? meta;
                try { meta = Metadata.GetRoomById(id, noThrow: true); } catch (ArgumentOutOfRangeException) { break; }
                if (meta == null)
                {
                    continue;
                }
                string head = $"{id} {meta.Name} ({meta.InGameName}){(meta.Multiplayer ? " MP" : "")}";
                var terr = new Dictionary<Terrain, int>();
                try
                {
                    if (Collision.GetCollision(meta).Info is MphCollisionInfo col)
                    {
                        foreach (CollisionData d in col.Data)
                        {
                            terr[d.Terrain] = terr.GetValueOrDefault(d.Terrain) + 1;
                        }
                    }
                }
                catch (Exception ex)
                {
                    head += $" [collision {ex.GetType().Name}]";
                }
                int snow = terr.GetValueOrDefault(Terrain.Snow), ice = terr.GetValueOrDefault(Terrain.Ice), total = terr.Values.Sum();
                var lines = new List<string>();
                if (meta.EntityPath != null)
                {
                    try
                    {
                        foreach (Entity entity in Read.GetEntities(meta.EntityPath, layerId: -1, firstHunt: false))
                        {
                            if (entity is Entity<ObjectEntityData> obj && SnowFx.Contains(obj.Data.EffectId))
                            {
                                ObjectEntityData d = obj.Data;
                                RawCollisionVolume v = d.Volume;
                                lines.Add($"    object#{entity.EntityId} layers 0x{entity.LayerMask:X} fx{d.EffectId} {Metadata.Effects[d.EffectId].Name}"
                                    + $" pos {d.Header.Position.ToFloatVector()} flags {d.EffectFlags} interval {d.EffectInterval}"
                                    + $" on 0x{d.EffectOnIntervals & 0xFFFF:X4} offset {d.EffectPositionOffset.ToFloatVector()}"
                                    + $" vol {v.Type} {v.BoxVector1.ToFloatVector()} {v.BoxVector2.ToFloatVector()} {v.BoxVector3.ToFloatVector()}"
                                    + $" {v.BoxPosition.ToFloatVector()} {v.BoxDot1.FloatValue} {v.BoxDot2.FloatValue} {v.BoxDot3.FloatValue}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"    [entities {ex.GetType().Name}]");
                    }
                }
                if (lines.Count > 0 || snow > 0 || ice > 0)
                {
                    Console.WriteLine($"{head}: collision {total}, snow {snow}, ice {ice}");
                    foreach (string l in lines) Console.WriteLine(l);
                }
            }
            foreach (int fx in SnowFx.Append(126))
            {
                Effect effect = Read.LoadEffect(fx, persistent: false);
                Console.WriteLine($"fx{fx} {effect.Name}: {effect.Elements.Count} element(s)");
                foreach (EffectElement e in effect.Elements)
                {
                    Console.WriteLine($"  {e.Name} model {e.ModelName} flags {e.Flags} life {e.Lifespan} drain {e.DrainTime} buffer {e.BufferTime}"
                        + $" accel {e.Acceleration} draw {e.DrawType} child {e.ChildEffectId}");
                    Console.WriteLine($"    particles: {string.Join(", ", e.Particles.Select(p => p.Name))}");
                    Console.WriteLine($"    actions: {string.Join(", ", e.Actions.Keys)}");
                }
            }
        }
    }
}
