using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaignlock <save.json> [room=UNIT1_LAND] [teleporterId=25]: the owner's Alinos report (2026-10-01): through the
    // Alinos Gateway portal into Elder Passage, a door is locked and the door-unlock chime plays; back in through that
    // door from High Ground, it isn't locked. Replays the route from a real campaign save (the app's campaign<N>.json):
    // portal in, out through the other door, back in through the first one, logging every door lock change, the
    // story room state of Elder Passage's doors and the door/lock/teleporter sounds MphRead starts.
    internal static partial class CampaignSim
    {
        private sealed class SfxTrace : SfxInstanceBase
        {
            public Func<string> Where { get; set; } = () => "";

            private static bool Interesting(int id)
            {
                if (id >= 0x4000)
                {
                    return true; // scripts and DGN sets: few, and all worth seeing here
                }
                string name = ((SfxId)id).ToString();
                return name.Contains("LOCK") || name.Contains("DOOR") || name.Contains("GEN_") || name.Contains("TELEPORT")
                    || name.Contains("SPAWN");
            }

            private void Log(string kind, int id)
            {
                if (Interesting(id))
                {
                    Console.WriteLine($"    {Where()} SFX {kind} {id} {(id < 0x4000 ? ((SfxId)id).ToString() : $"0x{id:X}")}");
                }
            }

            public override int PlaySample(int id, SoundSource? source, bool? loop, bool noUpdate,
                float recency, bool sourceOnly, bool cancellable)
            {
                Log("sample", id);
                return -1;
            }

            public override void PlayScript(int id, SoundSource? source, bool noUpdate,
                float recency, bool sourceOnly, bool cancellable)
            {
                Log("script", id);
            }

            public override void PlayDgn(int id, SoundSource? source, bool loop, bool noUpdate,
                float recency, bool cancellable, float amountA, float amountB)
            {
                Log("dgn", id);
            }
        }

        public static void LockReplay(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (args.Length < 2 || !File.Exists(args[1]))
            {
                Console.WriteLine("usage: -campaignlock <campaign save .json> [room=UNIT1_LAND] [teleporterId=25]");
                return;
            }
            StorySave story = CampaignSaves.FromJson(File.ReadAllText(args[1])).Story;
            string room = args.Length >= 3 ? args[2] : "UNIT1_LAND";
            int teleId = args.Length >= 4 ? Int32.Parse(args[3]) : 25;
            using CampaignHost host = CampaignHost.Start(room, save: story, collectDrawItems: true,
                viewWidth: 1920, viewHeight: 1080, arriving: false);
            Scene scene = host.Scene;
            int frame = 0;
            var trace = new SfxTrace { Where = () => $"f{frame,5} room {scene.RoomId} scene frame {scene.FrameCount}" };
            var lockSeen = new Dictionary<DoorEntity, bool>();
            int lastRoom = -1;
            void Watch()
            {
                if (scene.RoomId != lastRoom)
                {
                    Console.WriteLine($"  f{frame,5} ROOM {lastRoom} -> {scene.RoomId} (layer mask from the room load; transition {GameState.TransitionState})");
                    lastRoom = scene.RoomId;
                    lockSeen.Clear();
                    DumpDoors(host, frame, "on arrival");
                }
                foreach (DoorEntity d in scene.GetDoorEntities())
                {
                    bool locked = d.Flags.TestFlag(DoorFlags.Locked);
                    if (lockSeen.TryGetValue(d, out bool was) && was != locked)
                    {
                        Console.WriteLine($"    f{frame,5} room {scene.RoomId} door#{d.Id} at {Fmt(d.Position)} {(locked ? "LOCKED" : "unlocked")} "
                            + $"(player at {Fmt(host.Player.Position)}; story state {StoryDoor(scene.RoomId, d.Id)})");
                    }
                    lockSeen[d] = locked;
                }
            }
            void Step(CampaignInput input)
            {
                host.Step(input);
                frame++;
                if (Sfx.Instance != trace)
                {
                    Sfx.SetHost(trace); // every room load puts the silent instance back
                }
                Watch();
            }
            Console.WriteLine($"  save {Path.GetFileName(args[1])}: trigger bits {String.Join(",", Enumerable.Range(0, 32).Where(b => (story.TriggerState[b / 8] & (1 << (b % 8))) != 0))}, "
                + $"Alinos 1 area state {GameState.GetAreaState(0, story)}, Elder Passage door#0 {StoryDoor(31, 0)}, door#1 {StoryDoor(31, 1)}");
            Sfx.SetHost(trace);
            for (int i = 0; i < 3000 && (i < 60 || CameraSequence.Current != null); i++)
            {
                Step(default);
            }
            // 1. the portal (Alinos Gateway teleporter #25 -> Elder Passage)
            TeleporterEntity? tele = null;
            foreach (EntityBase e in scene.Entities)
            {
                if (e is TeleporterEntity t && t.Id == teleId)
                {
                    tele = t;
                }
            }
            if (tele == null)
            {
                Console.WriteLine($"  FAIL: no teleporter #{teleId} in {room}");
                return;
            }
            Console.WriteLine($"  1. portal: teleporter #{teleId} active {tele.Active} after the settle{(tele.Active ? "" : " (forced on)")}");
            tele.Active = true;
            Vector3 center = tele.Position;
            var away = new Vector3(tele.FacingVector.X, 0, tele.FacingVector.Z);
            away = away.LengthSquared > 0.01f ? away.Normalized() : Vector3.UnitZ;
            host.PlacePlayer(center + away * 3 + Vector3.UnitY * 0.5f, -away);
            for (int i = 0; i < 20; i++)
            {
                Step(default);
            }
            host.PlacePlayer(center + Vector3.UnitY * 1.0f, -away);
            int startRoom = scene.RoomId;
            for (int i = 0; i < 900 && !host.Ended && (scene.RoomId == startRoom || GameState.InRoomTransition
                || scene.FadeType != FadeType.None); i++)
            {
                Step(default);
            }
            for (int i = 0; i < 150; i++)
            {
                Step(default);
            }
            DumpDoors(host, frame, "settled after the portal");
            if (scene.RoomId != 31)
            {
                Console.WriteLine($"  FAIL: portal went to room {scene.RoomId}, not Elder Passage (31)");
                return;
            }
            // 2. out through the door that is NOT locked, into High Ground
            DoorEntity? exit = Doors(scene).Where(d => d.Id >= 0 && !d.Flags.TestFlag(DoorFlags.Locked))
                .OrderBy(d => (d.Position - host.Player.Position).Length).FirstOrDefault();
            if (exit == null)
            {
                Console.WriteLine("  FAIL: every Elder Passage door is locked");
                return;
            }
            Console.WriteLine($"  2. out through Elder Passage door#{exit.Id} at {Fmt(exit.Position)} -> room {exit.TargetRoomId}");
            if (!CrossDoor(host, exit, Step))
            {
                Console.WriteLine($"  FAIL: didn't get through door#{exit.Id} (room {scene.RoomId})");
                return;
            }
            for (int i = 0; i < 120; i++)
            {
                Step(default);
            }
            // 3. back in through the High Ground door that leads to the other Elder Passage door (the locked one)
            DoorEntity? back = Doors(scene).Where(d => d.Id >= 0 && d.TargetRoomId == 31)
                .OrderByDescending(d => (d.Position - host.Player.Position).Length).FirstOrDefault();
            if (back == null)
            {
                Console.WriteLine($"  FAIL: no door back to Elder Passage in room {scene.RoomId}");
                return;
            }
            Console.WriteLine($"  3. back through room {scene.RoomId} door#{back.Id} at {Fmt(back.Position)} (locked {back.Flags.TestFlag(DoorFlags.Locked)})");
            if (!CrossDoor(host, back, Step))
            {
                Console.WriteLine($"  FAIL: didn't get back into Elder Passage (room {scene.RoomId}, door#{back.Id} {back.Flags})");
                return;
            }
            // walk on into the room, past the volume just inside the door
            for (int i = 0; i < 120; i++)
            {
                Step(new CampaignInput { Move = new System.Numerics.Vector2(0, 1) });
            }
            for (int i = 0; i < 60; i++)
            {
                Step(default);
            }
            DumpDoors(host, frame, "after walking in");
        }

        private static List<DoorEntity> Doors(Scene scene)
        {
            var doors = new List<DoorEntity>();
            foreach (DoorEntity d in scene.GetDoorEntities())
            {
                doors.Add(d);
            }
            return doors;
        }

        private static string StoryDoor(int roomId, int doorId)
        {
            int raw = GameState.StorySave.GetRoomState(roomId, doorId) + 1;
            return raw switch { 0 => "unset", 1 => "unlocked(1)", 3 => "LOCKED(3)", _ => $"raw {raw}" };
        }

        private static void DumpDoors(CampaignHost host, int frame, string when)
        {
            Scene scene = host.Scene;
            Console.WriteLine($"    f{frame,5} room {scene.RoomId} doors {when} (player at {Fmt(host.Player.Position)}):");
            foreach (DoorEntity d in scene.GetDoorEntities())
            {
                Console.WriteLine($"      door#{d.Id} at {Fmt(d.Position)} {(d.Flags.TestFlag(DoorFlags.Locked) ? "LOCKED" : "unlocked")} "
                    + $"-> room {d.TargetRoomId}{(d.Id >= 0 ? $", story {StoryDoor(scene.RoomId, d.Id)}" : " (connector)")}");
            }
        }

        // RunDoor's driver without its own host: stand in front of the door, walk through, shoot it when closed, follow
        // to the corridor's far door; stand in the corridor if the straight walk sticks
        private static bool CrossDoor(CampaignHost host, DoorEntity door, Action<CampaignInput> step)
        {
            Vector3 facing = door.FacingVector;
            host.PlacePlayer(door.Position + facing * 2.5f + door.UpVector * 0.5f, -facing);
            int startRoom = host.RoomId;
            var input = new CampaignInput();
            bool everOpen = false, assisted = false;
            for (int i = 0; i < 1800 && !host.Ended; i++)
            {
                bool pastDoor = Vector3.Dot(host.Player.Position - door.Position, facing) < -0.5f;
                DoorEntity target = pastDoor && door.LoaderDoor != null ? door.LoaderDoor : door;
                bool open = target.Flags.TestFlag(DoorFlags.ShotOpen) || target.Flags.TestFlag(DoorFlags.Open);
                everOpen |= door.Flags.TestFlag(DoorFlags.ShotOpen) || door.Flags.TestFlag(DoorFlags.Open);
                input.Move = new System.Numerics.Vector2(0, 1);
                input.Buttons = !open && i % 30 < 2 ? CampaignButtons.Shoot : CampaignButtons.None;
                input.DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                    : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance;
                step(input);
                if (host.RoomId != startRoom && GameState.TransitionState == TransitionState.None)
                {
                    return true;
                }
                if (i == 600 && !assisted && everOpen && door.LoaderDoor != null && host.RoomId == startRoom
                    && GameState.TransitionState == TransitionState.None)
                {
                    DoorEntity loader = door.LoaderDoor;
                    Vector3 toDoor = (door.Position - loader.Position).Normalized();
                    host.PlacePlayer(loader.Position + toDoor * 2 + loader.UpVector * 0.5f, -toDoor);
                    assisted = true;
                    Console.WriteLine("    (assisted: placed in the corridor after the door opened)");
                }
            }
            return false;
        }
    }
}
