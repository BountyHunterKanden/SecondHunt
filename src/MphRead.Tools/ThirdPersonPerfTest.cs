using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats.Culling;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -tpperf [arena=Combat Hall] [room=UNIT1_LAND]: what the third-person true aim (PlayerHostAim.cs) and the camera-part
    // draw walk (RoomEntity.HostCameraNodeRef) cost (owner 2026-11-13: "do any of these changes impact performance?"). Each
    // piece timed on its own (HostAimFromCamera runs twice a step for each seat with a code; HostCameraNodeRef once a
    // step), then whole steps with them on and off in turn, with draw lists on and the draw frustum widened as the app has it.
    internal static partial class CampaignSim
    {
        public static void ThirdPersonPerf(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string arenaName = args.FirstOrDefault(a => a.StartsWith("arena="))?[6..] ?? "Combat Hall";
            string roomName = args.FirstOrDefault(a => a.StartsWith("room="))?[5..] ?? "UNIT1_LAND";
            var aimFromCamera = (Action<PlayerEntity>)typeof(PlayerEntity).GetMethod("HostAimFromCamera", Priv)!
                .CreateDelegate(typeof(Action<PlayerEntity>));
            var camNodeRef = (Func<RoomEntity, NodeRef>)typeof(RoomEntity).GetMethod("HostCameraNodeRef", Priv)!
                .CreateDelegate(typeof(Func<RoomEntity, NodeRef>));
            Scene.HostDrawCullWiden = _ => 0.15f; // (the app's LookCullWiden is never 0: the draw walk runs every step either way)
            try
            {
                RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
                Console.WriteLine($"  bot match: Battle in {arena.InGameName}, Samus + 3 bots, draw lists on (1920x1080)");
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 3, 3);
                settings.PointGoal = 99; // (no early end)
                settings.TimeLimitSeconds = 3600;
                using (CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080))
                {
                    Measure(host, aimFromCamera, camNodeRef);
                }
                Console.WriteLine($"  campaign: {roomName}, draw lists on (1920x1080)");
                using (CampaignHost host = CampaignHost.Start(roomName, setupSave: s => GiveAll(s, 0), collectDrawItems: true,
                    viewWidth: 1920, viewHeight: 1080, arriving: false))
                {
                    Measure(host, aimFromCamera, camNodeRef);
                }
            }
            finally
            {
                Scene.HostDrawCullWiden = null;
            }
        }

        private static void Measure(CampaignHost host, Action<PlayerEntity> aimFromCamera, Func<RoomEntity, NodeRef> camNodeRef)
        {
            Scene scene = host.Scene;
            RoomEntity? room = null;
            foreach (EntityBase e in scene.Entities)
            {
                room ??= e as RoomEntity;
            }
            if (room == null)
            {
                Console.WriteLine("    (no room entity)");
                return;
            }
            float tpDist = Single.MaxValue;
            ushort tpCode = 0;
            int mode = 0; // 0 first person, 1 third person (the sim told 0: vanilla aim), 2 third person + true aim
            int frame = 0;
            CampaignInput Script()
            {
                // a player walking about, turning and shooting (the test's own pattern, not the game's RNG)
                frame++;
                int phase = frame / 90 % 4;
                return new CampaignInput
                {
                    Move = new System.Numerics.Vector2(phase == 1 ? 1 : phase == 3 ? -1 : 0, phase == 2 ? -0.5f : 1),
                    AimDelta = new System.Numerics.Vector2(frame / 240 % 2 == 0 ? 1.5f : -1.5f, frame / 60 % 2 == 0 ? 0.25f : -0.25f),
                    Buttons = frame / 30 % 3 == 0 ? CampaignButtons.Shoot : CampaignButtons.None,
                    TrueAim = mode == 2 ? tpCode : (ushort)0
                };
            }
            void Step()
            {
                host.Step(Script());
                if (mode > 0)
                {
                    tpCode = ThirdPerson(host, ref tpDist); // (the app's camera, as it was before true aim)
                }
                else
                {
                    tpCode = 0;
                    tpDist = Single.MaxValue;
                    scene.HostCameraOffset = Vector3.Zero;
                }
            }
            for (int i = 0; i < 600 && !host.Ended; i++)
            {
                Step();
            }

            // the pieces, on the step's state as it is now
            mode = 2;
            for (int i = 0; i < 30 && !host.Ended; i++)
            {
                Step();
            }
            PlayerEntity p = host.Player;
            const int calls = 200_000;
            p.HostTrueAimCam = tpCode == 0 ? PlayerEntity.HostTrueAimCode(false, PlayerEntity.HostTpBack) : tpCode;
            double aimUs = Time(() => aimFromCamera(p), calls);
            ushort code = p.HostTrueAimCam;
            p.HostTrueAimCam = 0;
            double offUs = Time(() => aimFromCamera(p), calls);
            p.HostTrueAimCam = code;
            double nodeUs = Time(() => camNodeRef(room), calls);
            float dist = tpDist;
            double camUs = Time(() =>
            {
                float d = dist;
                PlayerEntity.HostThirdPersonOffset(scene, p.CameraInfo.ViewMatrix, false, ref d, out _);
            }, calls);
            int enemies = 0, doors = 0;
            foreach (EnemyInstanceEntity _ in scene.GetEnemyInstanceEntities())
            {
                enemies++;
            }
            foreach (DoorEntity _ in scene.GetDoorEntities())
            {
                doors++;
            }
            Console.WriteLine($"    true aim, one call (HostAimFromCamera): {aimUs:0.00} us ({doors} doors, {enemies} enemies, "
                + $"{PlayerEntity.PlayerCount} players to test); off (code 0): {offUs:0.000} us. Twice a step per seat with a code");
            Console.WriteLine($"    camera-part walk start (HostCameraNodeRef), one call: {nodeUs:0.00} us, once a step");
            Console.WriteLine($"    the app's camera ray (HostThirdPersonOffset, as before true aim), one call: {camUs:0.00} us, once a step");

            // whole steps, the three in turn (so all see the same stretch of play); rounds cut short by the end are dropped
            var ms = new[] { new List<double>(), new List<double>(), new List<double>() };
            for (int round = 0; round < 12 && !host.Ended; round++)
            {
                for (int m = 0; m < 3; m++)
                {
                    mode = m;
                    for (int i = 0; i < 30 && !host.Ended; i++)
                    {
                        Step(); // (settle: the camera eases out)
                    }
                    var sw = Stopwatch.StartNew();
                    int n = 0;
                    for (; n < 300 && !host.Ended; n++)
                    {
                        Step();
                    }
                    if (n == 300)
                    {
                        ms[m].Add(sw.Elapsed.TotalMilliseconds / n);
                    }
                }
            }
            mode = 0;
            scene.HostCameraOffset = Vector3.Zero;
            if (ms.Any(l => l.Count == 0))
            {
                Console.WriteLine("    (ended before the step timing)");
                return;
            }
            double fp = Median(ms[0]), tp = Median(ms[1]), ta = Median(ms[2]);
            Console.WriteLine($"    whole step (sim + draw lists + the camera ray), median of {ms[2].Count} x 300: first person {fp:0.000} ms, "
                + $"third person {tp:0.000} ms ({(tp - fp) * 1000:+0;-0} us), third person + true aim {ta:0.000} ms ({(ta - tp) * 1000:+0;-0} us "
                + $"over third person); spread {ms[0].Min():0.000}-{ms[0].Max():0.000} / {ms[1].Min():0.000}-{ms[1].Max():0.000} / "
                + $"{ms[2].Min():0.000}-{ms[2].Max():0.000}");
        }

        private static double Time(Action action, int calls)
        {
            for (int i = 0; i < 1000; i++)
            {
                action();
            }
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < calls; i++)
            {
                action();
            }
            return sw.Elapsed.TotalMilliseconds * 1000 / calls;
        }

        private static double Median(List<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }
    }
}
