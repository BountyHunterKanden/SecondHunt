using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;

namespace MphRead
{
    // -campaignspawn [room=UNIT1_LAND]: the frame the main player spawns (spawn effect + PLAYER_SPAWN) against what's on
    // screen. Vanilla (BizHawk, USA rev 1, Alinos first landing, the music session's alinos2_hooks.csv): the room's
    // script starts at f744 and the player's own PLAYER_SPAWN plays at f1436 = 692 frames later, in the landing camseq,
    // not at room load; on a revisit she spawns when she steps out of the ship (EXIT SHIP).
    //   A  first landing (arriving): spawns ~692 frames after the room load (checked for UNIT1_LAND only)
    //   B  cockpit view (arriving: false, holdSpawn): never spawned under the cockpit; ReleaseSpawn -> the next step
    //   C  on foot (arriving: false): spawns at the room load (EXIT SHIP with no cockpit host)
    internal static partial class CampaignSim
    {
        public static void SpawnTiming(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args.Length >= 2 ? args[1] : "UNIT1_LAND";
            int pass = 0, fail = 0;
            void Check(string name, bool ok)
            {
                if (ok) pass++; else fail++;
                Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {name}");
            }
            static bool Spawned(CampaignHost h) => h.Player.LoadFlags.TestFlag(LoadFlags.Spawned);

            using (CampaignHost host = CampaignHost.Start(room, setupSave: s => GiveAll(s, 0)))
            {
                int seqStart = -1, seqEnd = -1, spawn = -1;
                for (int f = 0; f < 4000 && (spawn == -1 || seqEnd == -1); f++)
                {
                    host.Step(default);
                    if (seqStart == -1 && CameraSequence.Current != null) seqStart = f;
                    if (seqStart != -1 && seqEnd == -1 && CameraSequence.Current == null) seqEnd = f;
                    if (spawn == -1 && Spawned(host)) spawn = f;
                }
                Console.WriteLine($"  A {room} first landing: camseq frames {seqStart}-{seqEnd}, player spawned at frame {spawn}");
                if (room == "UNIT1_LAND")
                {
                    Check("A: spawns 692 +-3 frames after the room load (vanilla)", spawn != -1 && Math.Abs(spawn - 692) <= 3);
                }
            }
            using (CampaignHost host = CampaignHost.Start(room, setupSave: s => GiveAll(s, 0), arriving: false, holdSpawn: true))
            {
                int early = -1;
                for (int f = 0; f < 900; f++)
                {
                    host.Step(default);
                    if (early == -1 && Spawned(host)) early = f;
                }
                host.ReleaseSpawn();
                int after = -1;
                for (int f = 0; f < 30 && after == -1; f++)
                {
                    host.Step(default);
                    if (Spawned(host)) after = f;
                }
                Console.WriteLine($"  B cockpit view: spawned during 900 held frames at {early}, after ReleaseSpawn at step {after}");
                Check("B: held under the cockpit, spawns on the step after EXIT", early == -1 && after == 0);
            }
            // B2: the cockpit's own landing camseq (driven the way CampaignShip / -fecockpit drive it) flies the same path
            // with her held as with her spawned
            if (room == "UNIT1_LAND")
            {
                (int ticks, OpenTK.Mathematics.Vector3 from, OpenTK.Mathematics.Vector3 down, bool spawned) Cockpit(bool hold)
                {
                    var save = new StorySave();
                    save.SetVisitedRoom(27);
                    using CampaignHost host = CampaignHost.Start(room, save: save, arriving: false, holdSpawn: hold);
                    CameraInfo cam = host.Player.CameraInfo;
                    var seq = CameraSequence.Load(MphRecomp.Campaign.ShipNavigation.CockpitSeqs(MphRecomp.Campaign.Planet.Alinos).Land, host.Scene);
                    seq.Initialize();
                    seq.SetUp(cam, 0);
                    seq.Flags |= CamSeqFlags.BlockInput;
                    cam.Update();
                    OpenTK.Mathematics.Vector3 from = cam.Position;
                    int ticks = -1;
                    for (int t = 1; t <= 1800 && ticks == -1; t++)
                    {
                        if (CameraSequence.Current == seq) seq.Process();
                        host.Step(default);
                        if (seq.Flags.TestFlag(CamSeqFlags.Complete)) ticks = t;
                    }
                    return (ticks, from, cam.Position, Spawned(host));
                }
                var held = Cockpit(hold: true);
                var free = Cockpit(hold: false);
                Console.WriteLine($"  B2 cockpit land camseq: held {held.ticks} ticks {held.from} -> {held.down} (spawned {held.spawned}); "
                    + $"unheld {free.ticks} ticks -> {free.down}");
                Check("B2: the held cockpit landing flies the same camera path and she stays unspawned",
                    held.ticks > 0 && held.ticks == free.ticks && (held.down - free.down).Length < 0.01f
                    && (held.down - held.from).Length > 1 && !held.spawned);
            }
            using (CampaignHost host = CampaignHost.Start(room, setupSave: s => GiveAll(s, 0), arriving: false))
            {
                int spawn = -1;
                for (int f = 0; f < 30 && spawn == -1; f++)
                {
                    host.Step(default);
                    if (Spawned(host)) spawn = f;
                }
                Console.WriteLine($"  C on foot: spawned at frame {spawn}");
                Check("C: on foot spawns at the room load, hold off by default", spawn == 0 && !PlayerEntity.HostHoldMainSpawn);
            }
            Console.WriteLine($"  {pass}/{pass + fail} passed");
        }
    }
}
