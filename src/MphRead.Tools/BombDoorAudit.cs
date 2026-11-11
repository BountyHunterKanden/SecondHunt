using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -bombdoors [list]
    //   audit of the campaign's bomb-lock doors (palette 8, the morph ball doors only a bomb or an alt attack unlocks)
    //   and every other morph ball door, per story room and layer. Without "list" it then loads each room with a
    //   morph ball door as Samus (control: it stays shut without a bomb or shot) and as each other hunter (it opens
    //   when the hunter stands 2 u in front of it with no bomb or shot, unless a story lock still holds it).
    // Finding 2026-11-06: the story rooms have no palette-8 doors; all 14 morph ball doors are palette 9.
    internal static class BombDoorAudit
    {
        private static readonly Hunter[] _others = { Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var bombRooms = new List<(RoomMetadata Room, List<short> Doors)>();
            int bombCount = 0;
            int ballCount = 0;
            foreach (RoomMetadata meta in Metadata.RoomList)
            {
                if (meta.EntityPath == null || meta.Multiplayer || meta.FirstHunt)
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
                    continue;
                }
                var bombDoors = new List<short>();
                foreach (Entity e in entities)
                {
                    if (e is not Entity<DoorEntityData> door)
                    {
                        continue;
                    }
                    DoorEntityData d = door.Data;
                    if (d.PaletteId != 8 && d.DoorType != DoorType.MorphBall)
                    {
                        continue;
                    }
                    string layers = door.LayerMask == 0xFFFF ? "all"
                        : String.Join(",", Enumerable.Range(0, 16).Where(l => (door.LayerMask & (1 << l)) != 0));
                    string kind = d.PaletteId == 8 ? "BOMB LOCK" : $"ball door, palette {d.PaletteId}";
                    Console.WriteLine($"{meta.Name,-10} {meta.InGameName,-24} door #{door.EntityId,-3} {d.DoorType,-9} {kind,-22} "
                        + $"locked {d.Locked} layers {layers} -> {d.EntityFilename.MarshalString()}");
                    if (d.PaletteId == 8) bombCount++; else ballCount++;
                    if (!bombDoors.Contains(door.EntityId))
                    {
                        bombDoors.Add(door.EntityId);
                    }
                }
                if (bombDoors.Count > 0)
                {
                    bombRooms.Add((meta, bombDoors));
                }
            }
            Console.WriteLine($"{bombCount} bomb-lock doors in {bombRooms.Count} rooms; {ballCount} other morph ball doors");
            if (args.Contains("list"))
            {
                return;
            }
            int pass = 0;
            int fail = 0;
            int absent = 0;
            foreach ((RoomMetadata room, List<short> doors) in bombRooms)
            {
                foreach (Hunter hunter in new[] { Hunter.Samus }.Concat(_others))
                {
                    using CampaignHost host = CampaignHost.Start(room.Name, hunter: hunter);
                    CampaignInput Dialogs(int i) => new CampaignInput
                    {
                        SelectWeapon = BeamType.None,
                        DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                            : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
                    };
                    for (int i = 0; i < 200; i++) host.Step(Dialogs(i));
                    foreach (short id in doors)
                    {
                        DoorEntity? door = host.FindDoor(id);
                        if (door == null)
                        {
                            absent++;
                            continue; // not on this room's default layer
                        }
                        bool startLocked = door.Flags.TestFlag(DoorFlags.Locked);
                        host.PlacePlayer(door.Position + door.FacingVector * 2f + door.UpVector * 0.6f, -door.FacingVector);
                        bool opened = false;
                        for (int i = 0; i < 60 && !opened; i++)
                        {
                            host.Step(Dialogs(i));
                            opened = door.Flags.TestFlag(DoorFlags.ShouldOpen) || !door.Flags.TestFlag(DoorFlags.Closed);
                        }
                        // Samus: a ball door still needs a bomb or shot; others: it opens unless a story lock holds it
                        bool ok = hunter == Hunter.Samus || startLocked ? !opened : opened;
                        if (ok) pass++; else fail++;
                        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {room.Name} door #{id} as {hunter}: "
                            + $"{(startLocked ? "locked" : "unlocked")} at load, {(opened ? "opened" : "stayed shut")} on approach");
                    }
                }
            }
            Console.WriteLine($"bombdoors: {pass} pass, {fail} fail ({absent} door/hunter checks skipped: door not on the default layer)");
        }
    }
}
