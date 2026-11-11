using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Arenas;
using MphRecomp.Arenas.Editor;
using OpenTK.Mathematics;

namespace MphRecomp.App;

internal sealed partial class CampaignRenderer
{
    // <data>/placements (CampaignLaunch.Create)
    public string? PlacementDir { get; set; }

    bool _placing, _prevPlaceSelect;
    PickupPlacement? _placer;
    int _placerRoom = -1, _placeIndex;
    string _placeNote = "";
    Vector3? _padStart; // a jump pad waiting for its landing spot

    // ConfigureSolo: a solo run with the editor on
    void ConfigurePlacer(LaunchArgs args)
    {
        _placing = args.Get("place") == "1";
    }

    // the choices: every placeable pickup, then (Echoes arenas) the affinity crate, then a jump pad, then a player spawn
    int PlaceChoices => ModeStart + Modes.Length + 2;
    int ModeStart => PickupPlacement.Placeable.Length + (_placer?.CanPlaceCrate == true ? 1 : 0);
    static readonly (string Kind, int Team)[] Modes =
    {
        (ModeKinds.Capture, 0), (ModeKinds.Capture, 1), (ModeKinds.Octolith, 0), (ModeKinds.Drop, 0),
        (ModeKinds.Node, 0), (ModeKinds.Defend, 0),
    };
    (string Kind, int Team)? PlacingMode => _placeIndex >= ModeStart && _placeIndex < ModeStart + Modes.Length
        ? Modes[_placeIndex - ModeStart] : null;
    bool PlacingCrate => _placer?.CanPlaceCrate == true && _placeIndex == PickupPlacement.Placeable.Length;
    bool PlacingPad => _placeIndex == PlaceChoices - 2;
    bool PlacingSpawn => _placeIndex == PlaceChoices - 1;

    string PlaceChoiceLabel()
    {
        if (PlacingPad) return _padStart is Vector3 ps
            ? $"Jump pad: now mark WHERE IT LANDS (pad at {ps.X:0.0}, {ps.Y:0.0}, {ps.Z:0.0})"
            : $"Jump pad ({_placer?.PadCount ?? 0} placed): mark the pad first";
        if (PlacingSpawn) return $"Player spawn ({_placer?.SpawnCount ?? 0}/{PickupPlacement.SpawnsWanted} placed)";
        if (PlacingMode is var (kind, team))
        {
            int count = _placer?.ModeCount(kind, team) ?? 0;
            return ModeKinds.Single(kind)
                ? $"{ModeKinds.Label(kind, team)} ({(count > 0 ? "placed: places again = move" : "not placed")})"
                : $"{ModeKinds.Label(kind, team)} ({count} placed)";
        }
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
        if (!PlacingPad) _padStart = null;
        // placing one mode's elements hides the other modes' (owner 2026-11-01); any other choice shows them all
        placer.ShowMode(PlacingMode is var (shown, _) ? ModeKinds.Mode(shown) : null);
        if (pressUp || pressSelect)
        {
            Vector3? at = pressUp ? placer.SpotAtFeet(p) : placer.SpotAtAim(p.CameraInfo.Position, p.NetAimVector, out _);
            if (at is not Vector3 spot)
            {
                _placeNote = "your aim reaches no floor: nothing placed";
            }
            else if (PlacingPad && _padStart is not Vector3)
            {
                _padStart = spot;
                _placeNote = $"pad marked at ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0}): now mark where it lands";
            }
            else if (PlacingPad && _padStart is Vector3 start)
            {
                if (placer.AddJumpPad(start, spot, out MapPads.Launch launch) != null)
                {
                    _padStart = null;
                    changed = true;
                    _placeNote = $"placed jump pad {placer.PadCount}: ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0}) -> "
                        + $"({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0}), speed {launch.Vector.Length:0.00}"
                        + (launch.Blocked ? " (something overhead blocks every arc)" : "");
                }
                else
                {
                    _placeNote = $"too far for a pad (needs speed {launch.Vector.Length:0.00}, max {MapPads.MaxSpeed:0.0}): "
                        + "mark a nearer landing, or D-DOWN to drop the pad";
                }
            }
            else
            {
                string what = PlacingSpawn ? $"spawn {placer.SpawnCount + 1}" : PlaceChoiceLabel();
                if (PlacingMode is var (kind, team)) placer.AddMode(kind, team, spot);
                else if (PlacingSpawn) placer.AddSpawn(spot, p.NetAimVector);
                else if (PlacingCrate) placer.AddAffinityCrate(spot);
                else placer.AddItem(PickupPlacement.Placeable[_placeIndex], spot);
                changed = true;
                _placeNote = $"placed {what} at ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})";
            }
        }
        if (pressDown && PlacingPad && _padStart != null)
        {
            _padStart = null;
            _placeNote = "pad mark dropped";
        }
        else if (pressDown)
        {
            if (NearestFor(placer, p.Position) is PickupPlacement.Target t)
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

    PickupPlacement.Target? NearestFor(PickupPlacement placer, Vector3 at) =>
        PlacingMode is var (kind, team) ? placer.NearestMode(at, kind, team)
        : PlacingSpawn ? placer.NearestSpawn(at) : PlacingPad ? placer.NearestPad(at) : placer.Nearest(at);

    // SoloStatusLine: what's picked, the controls, what D-pad down would delete, and the last change
    string PlacerStatusLine()
    {
        if (!_placing) return "";
        if (_placer == null || _host == null) return "\nPLACE PICKUPS: loading...";
        Vector3 at = _host.Player.Position;
        string near = NearestFor(_placer, at) is PickupPlacement.Target t ? $"{t.Label} {t.Distance:0.0}" : "nothing near";
        string facing = PlacingSpawn ? " (facing where you look)" : "";
        return $"\nPLACE: {PlaceChoiceLabel()}   D-pad L/R: change"
            + $"\nD-UP: at your feet{facing}   SELECT: where you aim   D-DOWN: delete nearest ({near})"
            + $"\n{_placer.PlacedCount} placed, {_placer.DeletedCount} deleted, {_placer.SpawnCount} spawns, {_placer.PadCount} pads   modes: "
            + $"CTF {_placer.ModeCount(ModeKinds.Capture)}/2, bounty {_placer.ModeCount(ModeKinds.Octolith) + _placer.ModeCount(ModeKinds.Drop)}/2, "
            + $"nodes {_placer.ModeCount(ModeKinds.Node)}, defender {_placer.ModeCount(ModeKinds.Defend)}/1   last: {_placeNote}";
    }
}
