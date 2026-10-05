using System;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MphRead
{
    // -roomentry [room=UNIT3_RM1] [doors=4] [nogc]: the room-entry hitches on the Odin (2026-10-02/03 MPHPerf: a door
    // crossing 50-65 ms in one frame, the room swapping in 110-190 ms, both mostly sim). Walks Samus through doors
    // headless, times every step, and prints each slow one with the host meshes / textures it registered and the GCs it
    // ran, plus MphRead's own EndTransition breakdown (RoomEntity.HostTransitionTrace) and slow entity set-ups
    // (Scene.HostEntityInitTrace). "nogc" skips EndTransition's forced compacting GC (RoomEntity.HostTransitionGc),
    // "predecode" decodes the next room's textures on the room-load worker (Scene.HostPredecodeTextures).
    internal static partial class CampaignSim
    {
        public static void RoomEntry(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 && !args[1].All(Char.IsDigit) ? args[1] : "UNIT3_RM1";
            int doorCount = args.Skip(1).FirstOrDefault(a => a.All(Char.IsDigit)) is string d ? Int32.Parse(d) : 4;
            RoomEntity.HostTransitionGc = !args.Contains("nogc");
            Scene.HostPredecodeTextures = args.Contains("predecode");
            using CampaignHost host = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080,
                arriving: false);
            Scene scene = host.Scene;
            int frame = 0;
            RoomEntity.HostTransitionTrace = msg => Console.WriteLine($"  f{frame,5}   EndTransition: {msg}");
            Scene.HostEntityInitTrace = msg => Console.WriteLine($"  f{frame,5}   entity: {msg}");
            void Step(CampaignInput input)
            {
                int meshes = scene.HostMeshes.Count, textures = scene.HostTextures.Count;
                int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
                long t = Stopwatch.GetTimestamp();
                host.Step(input);
                double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                if (ms > 8)
                {
                    Console.WriteLine($"  f{frame,5} room {scene.RoomId}: step {ms:0.0} ms, new meshes +{scene.HostMeshes.Count - meshes}, "
                        + $"textures +{scene.HostTextures.Count - textures}, gc0 +{GC.CollectionCount(0) - gc0}, gc2 +{GC.CollectionCount(2) - gc2}");
                }
                frame++;
            }
            for (int i = 0; i < 120; i++)
            {
                Step(default);
            }
            for (int n = 0; n < doorCount; n++)
            {
                DoorEntity? door = Doors(scene).Where(x => x.Id >= 0 && !x.Flags.TestFlag(DoorFlags.Locked) && x.TargetRoomId >= 0)
                    .OrderBy(x => (x.Position - host.Player.Position).Length).FirstOrDefault();
                if (door == null)
                {
                    Console.WriteLine($"  no unlocked door in room {scene.RoomId}");
                    break;
                }
                int from = scene.RoomId;
                Console.WriteLine($"  -- door#{door.Id} of room {from} (to {door.TargetRoomId})");
                bool crossed = CrossDoor(host, door, Step);
                Console.WriteLine($"  f{frame,5} room {from} -> {scene.RoomId}: {(crossed ? "crossed" : "FAILED")}");
                for (int i = 0; i < 120; i++)
                {
                    Step(default);
                }
            }
            // every registered host texture (binding id, size, pixels): equal with and without "predecode"
            long sum = 0;
            foreach (var kv in scene.HostTextures.OrderBy(kv => kv.Key))
            {
                long h = kv.Key * 31 + kv.Value.Width * 7 + kv.Value.Height;
                foreach (ColorRgba c in kv.Value.Pixels)
                {
                    h = h * 1000003 + c.Red * 65599 + c.Green * 257 + c.Blue * 17 + c.Alpha;
                }
                sum = sum * 31 + h;
            }
            Console.WriteLine($"  host textures {scene.HostTextures.Count}, checksum {sum:X16}");
            RoomEntity.HostTransitionTrace = null;
            Scene.HostEntityInitTrace = null;
            RoomEntity.HostTransitionGc = true;
            Scene.HostPredecodeTextures = false;
        }
    }
}
