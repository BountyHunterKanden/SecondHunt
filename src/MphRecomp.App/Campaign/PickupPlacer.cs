using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev launcher "Solo arena" + "Place pickups" (owner 2026-10-10): place pickups where you want them while you play the
// arena alone; in an Echoes arena also the affinity weapon's crates. The arena's own pickups and crates (and the
// affinity crates the owner picked on the map pictures, docs/PICKUPS.md) are there to keep or delete. The logic and the
// saved file are PickupPlacement's (Core); here the controls and the status line. While placing, the D-pad is the
// editor's (the L2 weapon ring still picks weapons):
//   D-pad left / right  what to place      D-pad up  place it at your feet     Select  place it where you aim
//   D-pad down          delete the pickup or crate nearest you (the status line names it)
// Every change is saved at once to <data>/placements/<room>.json.
//   --es place 1  (with --es solo 1)
internal sealed partial class CampaignRenderer
{
    // <data>/placements (CampaignLaunch.Create)
    public string? PlacementDir { get; set; }

    bool _placing, _prevPlaceSelect;
    PickupPlacement? _placer;
    int _placerRoom = -1, _placeIndex;
    string _placeNote = "";

    // ConfigureSolo: a solo run with the editor on
    void ConfigurePlacer(LaunchArgs args)
    {
        _placing = args.Get("place") == "1";
    }

    // the choices: every placeable pickup, then (Echoes arenas) the affinity crate
    int PlaceChoices => PickupPlacement.Placeable.Length + (_placer?.CanPlaceCrate == true ? 1 : 0);
    bool PlacingCrate => _placer?.CanPlaceCrate == true && _placeIndex == PickupPlacement.Placeable.Length;

    string PlaceChoiceLabel()
    {
        if (PlacingCrate) return "Affinity crate";
        ItemType t = PickupPlacement.Placeable[_placeIndex % PickupPlacement.Placeable.Length];
        return $"{PickupPlacement.Label(t)} (respawn {PickupPlacement.RespawnSeconds(t)} s)";
    }

    // BuildInput, every sim step (between steps, so the room's entities can change): true while the D-pad is the editor's
    bool PlacerStep(PadState pad, bool dpadHeld, bool pressUp, bool pressDown, bool pressLeft, bool pressRight)
    {
        if (!_placing || !_solo || _host == null) return false;
        bool select = pad[PadButton.Select];
        bool pressSelect = select && !_prevPlaceSelect;
        _prevPlaceSelect = select;
        if (_placer == null || _placerRoom != _host.RoomId)
        {
            if (MphRead.GameState.TransitionState != TransitionState.None) return dpadHeld;
            _placer = new PickupPlacement(_host.Scene, _host.RoomId, PlacementDir);
            _placerRoom = _host.RoomId;
            _placeIndex = 0;
            _placeNote = _placer.LastError ?? $"{_placer.PlacedCount} placed, {_placer.DeletedCount} deleted so far";
            Log.Info("MPHPlace", $"editor on in room {_host.RoomId}: {_placeNote}; file {_placer.Path}");
            _statusCountdown = 0;
        }
        PickupPlacement placer = _placer;
        PlayerEntity p = _host.Player;
        int n = PlaceChoices;
        bool changed = false;
        if (pressLeft) _placeIndex = (_placeIndex + n - 1) % n;
        if (pressRight) _placeIndex = (_placeIndex + 1) % n;
        if (pressUp || pressSelect)
        {
            Vector3? at = pressUp ? placer.SpotAtFeet(p) : placer.SpotAtAim(p.CameraInfo.Position, p.NetAimVector, out _);
            if (at is not Vector3 spot)
            {
                _placeNote = "your aim reaches no floor: nothing placed";
            }
            else
            {
                string what = PlaceChoiceLabel();
                if (PlacingCrate) placer.AddAffinityCrate(spot);
                else placer.AddItem(PickupPlacement.Placeable[_placeIndex], spot);
                changed = true;
                _placeNote = $"placed {what} at ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})";
            }
        }
        if (pressDown)
        {
            if (placer.Nearest(p.Position) is PickupPlacement.Target t)
            {
                placer.Delete(t.Entity);
                changed = true;
                _placeNote = "deleted " + t.Label;
            }
            else
            {
                _placeNote = "nothing within reach to delete";
            }
        }
        if (changed)
        {
            _placeNote += placer.Save() ? "  (saved)" : "  " + placer.LastError;
            Log.Info("MPHPlace", _placeNote);
        }
        if (pressLeft || pressRight || pressUp || pressDown || pressSelect) _statusCountdown = 0;
        return dpadHeld;
    }

    // SoloStatusLine: what's picked, the controls, what D-pad down would delete, and the last change
    string PlacerStatusLine()
    {
        if (!_placing) return "";
        if (_placer == null || _host == null) return "\nPLACE PICKUPS: loading...";
        string near = _placer.Nearest(_host.Player.Position) is PickupPlacement.Target t ? $"{t.Label} {t.Distance:0.0}" : "nothing near";
        return $"\nPLACE: {PlaceChoiceLabel()}   D-pad L/R: change"
            + $"\nD-UP: at your feet   SELECT: where you aim   D-DOWN: delete nearest ({near})"
            + $"\n{_placer.PlacedCount} placed, {_placer.DeletedCount} deleted   last: {_placeNote}";
    }
}
