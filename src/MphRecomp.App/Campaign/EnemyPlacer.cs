using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev launcher "Hard Mode: place enemies" (docs/ENEMY_IMPORT.md H1): place MPH and First Hunt enemies in the HARD MODE
// campaign's rooms while you play them, and remove, replace or buff the room's own. The file, the spawning, the waves
// and the door gates are Core's (Campaign/EnemyPlacement.cs); here the controls and the status line, which also says
// what can hurt the enemy and warns when nothing the hunter owns on the normal route does (section 8.6). While placing,
// the D-pad and Select are the editor's:
//   D-pad left / right  what to place      D-pad up  place it at your feet     Select (tap)  place it where you aim
//   D-pad down          delete the enemy spawner nearest you (a room's own is kept in the file as a removal)
//   Select held + D-pad up / down  pick a setting     Select held + D-pad left / right  change it
// Settings change the spawner nearest you (within reach), else the last one placed. Every change is saved at once to
// <data>/campaigns/hardmode/<room>.json.
//   --es enemyplace 1  (CampaignLaunch: also turns on hardmode)
internal sealed partial class CampaignRenderer
{
    const float EnemyReach = 6f;

    bool _enemyPlacing, _prevEnemySelect, _enemySelectUsed;
    PickupPlacement? _enemySpots; // the floor finder (nothing saved: dir null)
    int _enemyRoom = -1, _enemyIndex, _enemyField;
    EntityBase? _enemyLast;
    string _enemyNote = "";

    // CampaignLaunch.Create
    public void ConfigureEnemyPlacer(bool on)
    {
        _enemyPlacing = on && EnemyPlacement.Dir != null;
    }

    // CampaignMap.MapFrame: Select is the editor's while it's on
    bool EnemyPlacerOwnsSelect => _enemyPlacing && _host != null;

    EnemyCatalogue.Entry? EnemyChoice()
    {
        var all = EnemyCatalogue.Entries;
        return all.Count == 0 ? null : all[_enemyIndex % all.Count];
    }

    // the spawner settings act on: the nearest within reach, else the last placed (while it's still in the room)
    EntityBase? EnemyTarget()
    {
        if (_host == null) return null;
        if (EnemyPlacement.Nearest(_host.Player.Position, EnemyReach) is EnemyPlacement.Target t) return t.Spawner;
        if (_enemyLast != null && _host.Scene.TryGetEntity(_enemyLast.Id, out EntityBase? e) && e == _enemyLast) return e;
        return null;
    }

    static float YawDegrees(Vector3 dir) =>
        dir.Xz.LengthSquared < 1e-6f ? 0 : ((MathF.Atan2(dir.X, dir.Z) * 180f / MathF.PI) % 360f + 360f) % 360f;

    // BuildInput, every sim step: true while the D-pad is the editor's
    bool EnemyPlacerStep(PadState pad, bool dpadHeld, bool pressUp, bool pressDown, bool pressLeft, bool pressRight)
    {
        if (!_enemyPlacing || _host == null) return false;
        bool select = pad[PadButton.Select];
        bool releaseSelect = !select && _prevEnemySelect;
        if (select && !_prevEnemySelect) _enemySelectUsed = false;
        _prevEnemySelect = select;
        if (_enemySpots == null || _enemyRoom != _host.RoomId)
        {
            if (MphRead.GameState.TransitionState != TransitionState.None || EnemyPlacement.Current == null) return dpadHeld;
            _enemySpots = new PickupPlacement(_host.Scene, _host.RoomId, null);
            _enemyRoom = _host.RoomId;
            _enemyLast = null;
            _enemyField = 0;
            var cur = EnemyPlacement.Current;
            _enemyNote = EnemyPlacement.LastError
                ?? (EnemyCatalogue.Entries.Count == 0 ? "no catalogue: " + EnemyCatalogue.LastError : null)
                ?? $"{cur.Enemies.Count} placed, {cur.RoomEdits.Count} room edits, {cur.Gates.Count} gates so far";
            Log.Info("MPHPlace", $"enemy placer on in room {_host.RoomId}: {_enemyNote}; {EnemyCatalogue.Entries.Count} catalogue entries");
            _statusCountdown = 0;
        }
        PlayerEntity p = _host.Player;
        int n = Math.Max(1, EnemyCatalogue.Entries.Count);
        if (select)
        {
            // settings on the target
            if (pressUp || pressDown || pressLeft || pressRight) _enemySelectUsed = true;
            if (EnemyTarget() is EntityBase target)
            {
                int fields = EnemyPlacement.FieldsOf(target).Length;
                if (pressUp) _enemyField = (_enemyField + fields - 1) % fields;
                if (pressDown) _enemyField = (_enemyField + 1) % fields;
                _enemyField %= fields;
                if (pressLeft || pressRight)
                {
                    string name = EnemyPlacement.FieldsOf(target)[_enemyField];
                    EntityBase now = EnemyPlacement.Adjust(target, _enemyField, pressRight ? 1 : -1);
                    if (_enemyLast == target) _enemyLast = now;
                    _enemyNote = $"{name} {EnemyPlacement.FieldValue(now, _enemyField)}" + (EnemyPlacement.LastError is string err ? "  " + err : "  (saved)");
                    Log.Info("MPHPlace", _enemyNote);
                }
            }
            else if (pressLeft || pressRight)
            {
                _enemyNote = "no enemy spawner within reach to change";
            }
        }
        else
        {
            if (pressLeft) _enemyIndex = (_enemyIndex + n - 1) % n;
            if (pressRight) _enemyIndex = (_enemyIndex + 1) % n;
            bool placeAim = releaseSelect && !_enemySelectUsed;
            if ((pressUp || placeAim) && EnemyChoice() is EnemyCatalogue.Entry entry)
            {
                Vector3? at = pressUp ? _enemySpots.SpotAtFeet(p) : _enemySpots.SpotAtAim(p.CameraInfo.Position, p.NetAimVector, out _);
                if (at is not Vector3 spot)
                {
                    _enemyNote = "your aim reaches no floor: nothing placed";
                }
                else
                {
                    // at your feet it looks where you look; where you aim it faces you
                    float facing = pressUp ? YawDegrees(p.NetAimVector) : YawDegrees(p.Position - spot);
                    EntityBase? placed = EnemyPlacement.Place(entry, spot, facing);
                    if (placed != null)
                    {
                        _enemyLast = placed;
                        _enemyField = 0;
                    }
                    _enemyNote = placed == null ? "couldn't place " + entry.Label + (EnemyPlacement.LastError is string e1 ? ": " + e1 : "")
                        : $"placed {entry.Label} at ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})"
                          + (EnemyPlacement.LastError is string e2 ? "  " + e2 : "  (saved)");
                }
                Log.Info("MPHPlace", _enemyNote);
            }
            if (pressDown)
            {
                if (EnemyPlacement.Nearest(p.Position, EnemyReach) is EnemyPlacement.Target t)
                {
                    bool done = EnemyPlacement.Delete(t.Spawner);
                    if (_enemyLast == t.Spawner) _enemyLast = null;
                    _enemyNote = done ? (t.Placed ? "deleted " : "removed the room's ") + t.Label
                        + (EnemyPlacement.LastError is string e3 ? "  " + e3 : "  (saved)") : "can't delete " + t.Label;
                }
                else
                {
                    _enemyNote = "no enemy spawner within reach to delete";
                }
                Log.Info("MPHPlace", _enemyNote);
            }
        }
        if (pressLeft || pressRight || pressUp || pressDown || releaseSelect) _statusCountdown = 0;
        return dpadHeld;
    }

    // Status: what's picked and what hurts it, the controls, the target's setting, and the last change
    string EnemyPlacerStatusLine()
    {
        if (!_enemyPlacing) return "";
        if (_enemySpots == null || _host == null) return "\nHARD MODE PLACER: loading...";
        string line = "\nHARD MODE PLACER";
        if (EnemyChoice() is EnemyCatalogue.Entry entry)
        {
            (string hurt, string? warn) = EnemyCatalogue.Describe(entry, _host.RoomId, _hunter);
            line += $"\nPLACE: {entry.Label} ({entry.SourceRoom})   D-pad L/R: change\n{hurt}" + (warn != null ? "\n" + warn : "");
        }
        else
        {
            line += "\nnothing to place: no enemies found in the imported games" + (EnemyCatalogue.LastError is string e ? " (" + e + ")" : "");
        }
        line += "\nD-UP: at your feet   SELECT tap: where you aim   D-DOWN: delete nearest";
        if (EnemyTarget() is EntityBase target)
        {
            EnemyCatalogue.Entry te = EnemyPlacement.EntryOf(target);
            (string hurt, string? warn) = EnemyCatalogue.Describe(te, _host.RoomId, _hunter, EnemyPlacement.LiveEnemyOf(target));
            string[] fields = EnemyPlacement.FieldsOf(target);
            int f = _enemyField % fields.Length;
            float dist = Vector3.Distance(_host.Player.Position, target.Position);
            line += $"\nTARGET: {te.Label} [{target.Id}] {dist:0.0}   SELECT+UP/DOWN: setting, +L/R: change"
                + $"\n  {fields[f]} {EnemyPlacement.FieldValue(target, f)}   ({String.Join(" ", fields)})"
                + $"\n  {hurt}" + (warn != null ? "\n  " + warn : "");
        }
        else
        {
            line += "\nTARGET: none within reach";
        }
        var cur = EnemyPlacement.Current;
        if (cur != null) line += $"\n{cur.Enemies.Count} placed, {cur.RoomEdits.Count} room edits, {cur.Gates.Count} gates";
        return line + $"   last: {_enemyNote}";
    }
}
