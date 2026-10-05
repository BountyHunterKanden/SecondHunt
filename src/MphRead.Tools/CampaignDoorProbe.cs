using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // Door investigation tools (written for the three doors the -campaignsweep driver couldn't open, 2026-09-29):
    //
    //   -campaigndoorprobe <room> <doorId> [layer]
    //       stands 3 u in front of the door; reports what a shot/scan from the camera hits first, what is near the door
    //       (force fields with state), and every trigger/area in the room with its type, state and message targets
    //   -campaignmsgscan <Message> [param1]
    //       every entity in every story room (all layers) that sends that message, e.g. SetTriggerState 25
    //   -campaigndoorcheck <room> <doorId> [bits=25,..] [layer=n] [min=dist] [after=frames] [anyshot] [trace] [nobeams]
    //       drives one door carefully: waits until the player has LANDED before judging a spot (the sweep judges
    //       after 20 frames, mid-fall), requires the door's level + a clear shot, morphs first for ball doors,
    //       steers at the door in world space (the ball's camera needn't face it); bits= sets story trigger-state
    //       bits (events from elsewhere in the game); after= keeps rolling forward after arriving
    //
    // Findings: Sic Transit #7 (Magmaul) is behind message-only ForceField #64, lowered by StateBits Trigger #68 on
    // story bit 25, which Biodefense Chamber 04 (Arcterra's Slench room) sets -- blocked without it, arrives with it.
    // Sic Transit #6 sits on a ledge (arrives when stood on it). Processor Core #10 is at the top of a half-pipe with
    // no floor in front (works from Piston Cave; reaching it from Processor Core takes a Boost Ball climb).
    // Processor Core #9 arrives with world-space steering.
    internal static class CampaignDoorProbe
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args[1];
            int doorId = Int32.Parse(args[2]);
            int layer = args.Length > 3 ? Int32.Parse(args[3]) : 0;
            using CampaignHost host = CampaignHost.Start(room, setupSave: s =>
            {
                s.BossFlags = (BossFlags)(layer * 0x15555);
                s.Weapons = 0xFF;
                s.Health = s.HealthMax = 799;
                s.Ammo[0] = s.AmmoMax[0] = 4000;
                s.Ammo[1] = s.AmmoMax[1] = 950;
            });
            for (int i = 0; i < 300; i++)
            {
                host.Step(new CampaignInput { DialogButton = host.DialogPaused && i % 10 == 0 ? HostDialogButton.Advance : HostDialogButton.None });
            }
            DoorEntity door = host.FindDoor(doorId)!;
            Vector3 facing = door.FacingVector;
            host.PlacePlayer(door.Position + facing * 3 + door.UpVector * 0.5f, -facing);
            for (int i = 0; i < 30; i++) host.Step(default);
            PlayerEntity p = host.Player;
            Console.WriteLine($"door #{doorId} at {Fmt(door.Position)} facing {Fmt(facing)} flags {door.Flags} palette {door.Data.PaletteId} "
                + $"locked-in-data {door.Data.Locked}; player at {Fmt(p.Position)} cam {Fmt(p.CameraInfo.Position)}");
            foreach (TestFlags flags in new[] { TestFlags.Beams, TestFlags.Players, TestFlags.Scan })
            {
                CollisionResult res = default;
                Vector3 target = door.Position + door.UpVector * 0.5f;
                bool hit = CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, target, flags, host.Scene, ref res);
                EntityBase? who = res.EntityCollision?.Entity;
                Console.WriteLine($"  line {flags}: {(hit ? $"blocked at {Fmt(res.Position)} ({res.Distance:0.00} of the way) by "
                    + (who != null ? $"{who.Type} #{who.Id}" : "room collision") : "clear")}");
            }
            Console.WriteLine("  entities within 8 u of the door:");
            foreach (EntityBase e in host.Scene.Entities)
            {
                float d = (e.Position - door.Position).Length;
                if (d > 8 || e == door || e.Type == EntityType.Player)
                {
                    continue;
                }
                string extra = e switch
                {
                    ForceFieldEntity ff => $"active {ff.Active} {ff.Width:0.0}x{ff.Height:0.0} lock {ff.Data.Type} plane dist to player "
                        + $"{Vector3.Dot(ff.FieldFacingVector, p.Position - ff.Position):0.00}",
                    DoorEntity de => $"flags {de.Flags} palette {de.Data.PaletteId} type {de.Data.DoorType}",
                    _ => ""
                };
                Console.WriteLine($"    {e.Type} #{e.Id} at {Fmt(e.Position)} ({d:0.0} u) {extra}");
            }
            Console.WriteLine("  triggers (subtype, active now, flags -> parent / child messages):");
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is TriggerVolumeEntity t)
                {
                    TriggerVolumeEntityData d = t.Data;
                    Console.WriteLine($"    Trigger #{t.Id} {d.Subtype} active {t.Active} (data {d.Active}, always {d.AlwaysActive}, once {d.DeactivateAfterUse}) "
                        + $"flags {d.TriggerFlags} threshold {d.TriggerThreshold} bit {d.RequiredStateBit} at {Fmt(t.Position)} "
                        + $"inside-now {t.Volume.TestPoint(p.Position)} -> #{d.ParentId} {d.ParentMessage}({d.ParentMsgParam1}) / #{d.ChildId} {d.ChildMessage}({d.ChildMsgParam1})");
                }
                else if (e is AreaVolumeEntity a)
                {
                    AreaVolumeEntityData d = a.Data;
                    Console.WriteLine($"    Area #{a.Id} active {a.Active} flags {d.TriggerFlags} inside {d.InsideMessage} exit {d.ExitMessage} -> #{d.ParentId}");
                }
            }
            Console.WriteLine("  all force fields in the room:");
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is not ForceFieldEntity ff)
                {
                    continue;
                }
                Console.WriteLine($"    ForceField #{ff.Id} at {Fmt(ff.Position)} active {ff.Active} lock {ff.Data.Type}, {(ff.Position - door.Position).Length:0.0} u from the door");
            }
        }

        // Every entity in every story room (all layers) that sends the given message, with the struct's param fields:
        //   -campaignmsgscan <Message> [param1]
        public static void MessageScan(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Message wanted = Enum.Parse<Message>(args[1], ignoreCase: true);
            int? wantedParam = args.Length > 2 ? Int32.Parse(args[2]) : null;
            foreach (RoomMetadata meta in Metadata.RoomList)
            {
                if (meta.EntityPath == null || meta.Multiplayer)
                {
                    continue;
                }
                IReadOnlyList<Entity> entities;
                try
                {
                    entities = Read.GetEntities(meta.EntityPath, -1, meta.FirstHunt);
                }
                catch (System.IO.IOException)
                {
                    continue; // unused rooms without an entity file
                }
                foreach (Entity e in entities)
                {
                    object? data = e.GetType().GetProperty("Data")?.GetValue(e);
                    if (data == null)
                    {
                        continue;
                    }
                    System.Reflection.FieldInfo[] fields = data.GetType().GetFields();
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (fields[i].FieldType != typeof(Message) || (Message)fields[i].GetValue(data)! != wanted)
                        {
                            continue;
                        }
                        // the message's own params are the int fields right after it (skipping padding)
                        var parms = new List<string>();
                        int? first = null;
                        for (int j = i + 1; j < fields.Length && parms.Count < 2; j++)
                        {
                            if (fields[j].FieldType == typeof(Message))
                            {
                                break;
                            }
                            if (fields[j].Name.Contains("Param"))
                            {
                                object v = fields[j].GetValue(data)!;
                                parms.Add($"{fields[j].Name}={v}");
                                first ??= Convert.ToInt32(v);
                            }
                        }
                        if (wantedParam != null && first != wantedParam)
                        {
                            continue;
                        }
                        Console.WriteLine($"  {meta.Id} {meta.InGameName} ({meta.Name}) {e.Type}#{e.EntityId} layers 0x{e.LayerMask:X} "
                            + $"{fields[i].Name} {String.Join(" ", parms)}");
                    }
                }
            }
        }

        // Drives one connector door with a careful placement: tries spots in front of the door (and to the sides),
        // waits until the player has LANDED (height steady), and only accepts a spot on the door's own level with a
        // clear line of fire. Optional story trigger-state bits model events from elsewhere in the game.
        //   -campaigndoorcheck <room> <doorId> [bits=25,26] [layer=0]
        public static void DoorCheck(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string room = args[1];
            int doorId = Int32.Parse(args[2]);
            int[] bits = args.Where(a => a.StartsWith("bits=")).SelectMany(a => a[5..].Split(',')).Select(Int32.Parse).ToArray();
            int layer = args.Where(a => a.StartsWith("layer=")).Select(a => Int32.Parse(a[6..])).FirstOrDefault();
            using CampaignHost host = CampaignHost.Start(room, setupSave: s =>
            {
                s.BossFlags = (BossFlags)(layer * 0x15555);
                s.Weapons = 0xFF;
                s.Health = s.HealthMax = 799;
                s.Ammo[0] = s.AmmoMax[0] = 4000;
                s.Ammo[1] = s.AmmoMax[1] = 950;
                foreach (int bit in bits)
                {
                    s.TriggerState[bit / 8] |= (byte)(1 << (bit % 8));
                }
            });
            // (SelectWeapon defaults to 0 = Power Beam: a default CampaignInput holds the Power Beam key)
            CampaignInput Dialogs(int i) => new CampaignInput
            {
                SelectWeapon = BeamType.None,
                DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                    : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
            };
            for (int i = 0; i < 300; i++) host.Step(Dialogs(i));
            DoorEntity door = host.FindDoor(doorId)!;
            PlayerEntity p = host.Player;
            int startRoom = host.RoomId;
            bool trace = args.Contains("trace");
            float minDist = args.Where(a => a.StartsWith("min=")).Select(a => Single.Parse(a[4..])).FirstOrDefault();
            Vector3 facing = door.FacingVector;
            Vector3 right = Vector3.Cross(facing, door.UpVector).Normalized();
            bool morph = door.Data.DoorType == DoorType.MorphBall;
            Console.WriteLine($"{room} door #{doorId} at {Fmt(door.Position)} {door.Data.DoorType} palette {door.Data.PaletteId} flags {door.Flags}"
                + (bits.Length > 0 ? $", story bits {String.Join(",", bits)} set" : ""));
            if (morph)
            {
                for (int i = 0; i < 120 && !p.IsAltForm; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.Morph : CampaignButtons.None });
            }
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e is ForceFieldEntity ff && (ff.Position - door.Position).Length < 8)
                {
                    Console.WriteLine($"  force field #{ff.Id} {(ff.Position - door.Position).Length:0.0} u from the door: active {ff.Active}");
                }
            }
            bool placed = false;
            foreach (float lat in new[] { 0f, 1f, -1f, 2f, -2f })
            {
                foreach (float dist in new[] { 1.0f, 1.25f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 5f, 6f })
                {
                    if (dist < minDist)
                    {
                        continue;
                    }
                    host.PlacePlayer(door.Position + facing * dist + right * lat + door.UpVector * 0.6f, -facing);
                    float lastY = Single.NaN;
                    int steady = 0;
                    for (int i = 0; i < 120 && steady < 10; i++)
                    {
                        host.Step(default);
                        steady = MathF.Abs(p.Position.Y - lastY) < 0.001f ? steady + 1 : 0;
                        lastY = p.Position.Y;
                    }
                    float rise = Vector3.Dot(door.Position - p.Position, door.UpVector);
                    CollisionResult res = default;
                    bool blocked = CollisionDetection.CheckBetweenPoints(p.CameraInfo.Position, door.Position + door.UpVector * (morph ? 0.3f : 1f),
                        TestFlags.Beams, host.Scene, ref res);
                    if (trace)
                    {
                        Console.WriteLine($"    try {dist} out {lat} side: at {Fmt(p.Position)}{(p.IsAltForm ? " ball" : "")} steady {steady} rise {rise:0.00} shot {(blocked ? "blocked" : "clear")}");
                    }
                    if (steady >= 10 && p.Health > 0 && rise < 1.2f && rise > -1.5f && (!blocked || args.Contains("anyshot")))
                    {
                        Console.WriteLine($"  standing {dist} u out, {lat} u to the side at {Fmt(p.Position)} (door {rise:0.00} u above her feet, shot {(blocked ? "blocked" : "clear")})"
                            + $"{(p.IsAltForm ? " as the ball" : "")}");
                        placed = true;
                        break;
                    }
                }
                if (placed)
                {
                    break;
                }
            }
            if (!placed)
            {
                Console.WriteLine("  RESULT: no landed spot on the door's level with a clear shot in front of it");
                return;
            }
            var input = new CampaignInput { SelectWeapon = door.Data.Locked != 0 && door.Data.PaletteId < 8 ? (BeamType)door.Data.PaletteId : BeamType.PowerBeam };
            for (int i = 0; i < 90; i++)
            {
                input.Buttons = CampaignButtons.None;
                host.Step(input);
                input.SelectWeapon = BeamType.None;
            }
            Console.WriteLine($"  holding {p.CurrentWeapon}{(p.IsAltForm ? " (ball)" : "")}, equipped weapon info: beam {p.EquipInfo.Weapon.Beam} "
                + $"kind {p.EquipInfo.Weapon.BeamKind} ({p.EquipInfo.Weapon.Name})");
            if (trace)
            {
                for (int i = 0; i < Weapons.Current.Count; i++)
                {
                    Console.WriteLine($"    Weapons.Current[{i}] = {Weapons.Current[i].Name}: beam {Weapons.Current[i].Beam}, kind {Weapons.Current[i].BeamKind}");
                }
            }
            bool everOpen = false;
            bool wasLocked = door.Flags.TestFlag(DoorFlags.Locked);
            DoorFlags lastFlags = door.Flags;
            int f = 0;
            for (; f < 1500; f++)
            {
                bool pastDoor = Vector3.Dot(p.Position - door.Position, facing) < -0.5f;
                DoorEntity target = pastDoor && door.LoaderDoor != null ? door.LoaderDoor : door;
                bool open = target.Flags.TestFlag(DoorFlags.ShotOpen) || target.Flags.TestFlag(DoorFlags.Open);
                float near = (p.Position - target.Position).Length;
                bool fire = !open && f % 30 < 2;
                input = Dialogs(f);
                // steer at the door (then through it toward the far door) in world space, converted to the camera's frame
                Vector3 goal = (pastDoor && door.LoaderDoor != null ? door.LoaderDoor.Position : door.Position) - target.FacingVector * 1.5f;
                Vector3 toGoal = goal - p.Position;
                toGoal.Y = 0;
                Vector3 camFwd = p.CameraInfo.Facing;
                camFwd.Y = 0;
                bool go = open || !p.IsAltForm || near > 1.5f;
                if (go && toGoal.LengthSquared > 0.01f && camFwd.LengthSquared > 0.0001f)
                {
                    toGoal.Normalize();
                    camFwd.Normalize();
                    Vector3 camRight = Vector3.Cross(camFwd, Vector3.UnitY);
                    input.Move = new System.Numerics.Vector2(Vector3.Dot(toGoal, camRight), Vector3.Dot(toGoal, camFwd));
                }
                input.Buttons = !fire ? CampaignButtons.None : !p.IsAltForm ? CampaignButtons.Shoot
                    : near <= 1.5f ? CampaignButtons.AltAttack : CampaignButtons.None;
                host.Step(input);
                everOpen |= door.Flags.TestFlag(DoorFlags.ShotOpen) || door.Flags.TestFlag(DoorFlags.Open);
                if (trace && door.Flags != lastFlags)
                {
                    Console.WriteLine($"    f{f}: door {lastFlags} -> {door.Flags}");
                    lastFlags = door.Flags;
                }
                if (trace && f % 30 == 0)
                {
                    Console.WriteLine($"    f{f}: player {Fmt(p.Position)}{(p.IsAltForm ? " ball" : "")} vel {Fmt(p.Speed)} room {host.RoomId} transition {GameState.TransitionState}"
                        + $" far door {door.LoaderDoor?.Flags}");
                }
                if (trace && f < 400 && !args.Contains("nobeams"))
                {
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e is BeamProjectileEntity b && (b.Position - door.Position).Length < 2.5f)
                        {
                            Console.WriteLine($"    f{f}: beam {b.Beam} flags {b.Flags} at {Fmt(b.Position)} ({(b.Position - door.Position).Length:0.00} u from the door)");
                        }
                    }
                }
                if (wasLocked && !door.Flags.TestFlag(DoorFlags.Locked))
                {
                    Console.WriteLine($"  f{f}: door unlocked (holding {p.CurrentWeapon})");
                    wasLocked = false;
                }
                if (host.RoomId != startRoom && GameState.TransitionState == TransitionState.None)
                {
                    break;
                }
            }
            int after = args.Where(a => a.StartsWith("after=")).Select(a => Int32.Parse(a[6..])).FirstOrDefault();
            if (after > 0 && host.RoomId != startRoom)
            {
                Console.WriteLine($"  after arriving: holding forward for {after} frames");
                for (int i = 0; i < after; i++)
                {
                    input = Dialogs(i);
                    input.Move = new System.Numerics.Vector2(0, 1);
                    host.Step(input);
                    if (i % 30 == 0)
                    {
                        Console.WriteLine($"    +{i}: player {Fmt(p.Position)}{(p.IsAltForm ? " ball" : "")} room {host.RoomId}");
                    }
                }
            }
            RoomMetadata? arrived = Metadata.GetRoomById(host.RoomId);
            Console.WriteLine($"  RESULT: {(host.RoomId != startRoom ? $"ARRIVED in {arrived?.InGameName} after {f} frames" : $"still in room {host.RoomId}")}, "
                + $"door opened {everOpen}, door flags {door.Flags}, player at {Fmt(p.Position)}{(p.IsAltForm ? " (ball)" : "")}");
        }

        private static string Fmt(Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
    }
}
