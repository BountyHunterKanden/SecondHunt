using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

// -femaptrack [room] [door id | all]: walks Samus through a room's connector doors in the PC campaign (CampaignHost) and
// runs the pause map's connector tracking every frame, as the device does (CampaignMap.MapTrack -> AreaMapIndex
// .ConnectorAt). PASS: she arrived and the walk marked a connector (a room's own hall the map draws as a connector, like
// the Crystal Room's Con07, is marked too). Morph-ball doors are skipped (this walker doesn't roll). Uses the campaign
// sim's door cases and save setup.
namespace MphRead
{
    internal static partial class CampaignSim
    {
        public static void MapTrack(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            AreaMapIndex index = AreaMapIndex.Build(Paths.FileSystem);
            string room = args.Length >= 2 ? args[1].ToUpperInvariant() : "UNIT2_LAND";
            string which = args.Length >= 3 ? args[2] : "all";
            RoomMetadata meta = Metadata.RoomMetadata[room];
            int pass = 0, fail = 0;
            foreach (DoorCase c in DoorCases(meta))
            {
                if (which != "all" && c.DoorId.ToString() != which) continue;
                if (c.ExpectBlocked || c.Morph) continue;
                (bool ok, string summary) = WalkAndTrack(index, c);
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {c.Label}: {summary}");
                if (ok) pass++;
                else fail++;
            }
            Console.WriteLine($"  {pass} passed, {fail} failed");
        }

        private static (bool, string) WalkAndTrack(AreaMapIndex index, DoorCase c)
        {
            RoomEntity.TransitionFailure = null;
            using CampaignHost host = CampaignHost.Start(c.Room.Name, setupSave: s => GiveAll(s, c.Layer), hunter: _hunter);
            int settle = IsLandingRoom(c.Room.Id) ? 900 : 300;
            for (int i = 0; i < settle && !host.Ended; i++)
            {
                host.Step(new CampaignInput
                {
                    DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance
                });
            }
            DoorEntity? door = host.FindDoor(c.DoorId);
            if (door == null) return (false, "door not present after load");
            // the connector the map should mark: the door's spot in the map's planet space
            uint[] bits = new uint[8];
            int startRoom = host.RoomId;
            Vector3 facing = door.FacingVector;
            host.PlacePlayer(door.Position + facing * 2.5f + door.UpVector * 0.5f, -facing);
            var input = new CampaignInput { SelectWeapon = c.Weapon };
            var marks = new List<string>();
            int after = -1;
            for (int i = 0; i < 1500 && !host.Ended; i++)
            {
                bool pastDoor = Vector3.Dot(host.Player.Position - door.Position, facing) < -0.5f;
                DoorEntity target = pastDoor && door.LoaderDoor != null ? door.LoaderDoor : door;
                bool open = target.Flags.TestFlag(DoorFlags.ShotOpen) || target.Flags.TestFlag(DoorFlags.Open);
                input.Move = new System.Numerics.Vector2(0, 1);
                input.Buttons = !open && i % 30 < 2 ? CampaignButtons.Shoot : CampaignButtons.None;
                input.DialogButton = !host.DialogPaused || i % 10 != 0 ? HostDialogButton.None
                    : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance;
                host.Step(input);
                input.SelectWeapon = BeamType.None;
                // what CampaignMap.MapTrack does each frame
                string? name = Metadata.GetRoomById(host.RoomId, noThrow: true)?.Name;
                if (name != null && index.ConnectorAt(name, host.Player.Position) is (int pair, int number) && AreaMapIndex.Set(bits, pair, number))
                {
                    marks.Add($"Con{number:00} (area {pair}, frame {i}, in {name})");
                }
                if (after < 0 && host.RoomId != startRoom && GameState.TransitionState == TransitionState.None) after = i;
                if (after >= 0 && i - after > 120) break; // a little way into the next room
            }
            string where = after >= 0 ? $"arrived in room {host.RoomId}" : "never arrived";
            if (marks.Count == 0) return (false, $"{where}, no connector marked");
            return (after >= 0, $"{where}, marked {String.Join(", ", marks)}");
        }
    }
}
