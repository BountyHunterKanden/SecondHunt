using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Shots drawn from the arm (owner 2026-10-09 ~17:50, a fresh start after the arm poses): every shot really starts at the
// eye (the sim's first-person muzzle) for every hunter, and stays there (fairness, netcode, replays, players without the
// imports). The plan: draw each shot on a straight line from the shooter's own cannon tip to where the real shot lands,
// checked once at the spawn against the arena's collision; where that line is blocked (over a crate, round a corner, on
// a wall), the bolt shows on its real path only once past the obstacle, never bent and never through a wall.
// Step 1 (dev): how often a shot would be blocked (18:00: 2086 shots, 92.7% clear, 3.6% point-blank, 1.6% blocked beside
// him, 2.1% blocked mid-flight).
// Step 2 (DrawShotFromArm): ON for the Troopers in third person; the pause's D-pad left/right cycles cannon + shots from
// the arm / cannon + vanilla shots / MPH. Per shot, once at its spawn (BeamProjectileEntity.HostSpawned):
//   clear line tip -> landing: drawn on that line (HostDrawShift = tip - spawn, eased to nothing at the landing point:
//   a straight line, it meets the real shot where that lands);
//   blocked further out (2+ from the tip): drawn from the tip onto the real path just before the obstacle;
//   blocked beside him, or the tip behind a wall from the eye: no shift, drawn only past the obstacle (HostDrawHide);
//   point-blank (the real shot lands < 2 away): from the tip if clear, else vanilla.
//   --es armcannonshots 0 | all   (dev) start with vanilla shots / every native body's shots (Dark Samus too)
//   --es armcannonwallcheck 1   each shot spawned: "wallcheck: ..." to the log -- the line from the tip (a native body's
//   real tip; for MPH bodies a proxy at the Trooper's firing offset) to the shot's first wall hit (or 40 units on)
internal sealed partial class CampaignRenderer
{
    bool _wallCheck;
    int _wallCheckShots;
    // the Trooper's cannon tip while his clips fire, left/up/forward of his position (armcannonmeasure, level aim)
    static readonly Vector3 ProxyTip = new(-0.38f, 0.45f, 0.78f);
    const float WallCheckRange = 40;

    bool _armShots = true, _armShotsAll;
    const float ArmShotNear = 2; // a block closer than this to the tip, or a landing closer than this: "beside him"

    void ConfigureBeamPath(LaunchArgs args)
    {
        _wallCheck = args.Get("armcannonwallcheck") == "1";
        if (args.Get("armcannonshots") == "0") _armShots = false;
        if (args.Get("armcannonshots") == "all") _armShotsAll = true;
    }

    // as each shot spawns: draw it from the shooter's cannon tip (header). Draw only; the shot itself is the sim's.
    void DrawShotFromArm(BeamProjectileEntity beam)
    {
        if (!_armShots || !_cannonOn || !_cannon3p || _host == null || beam.Owner is not PlayerEntity p
            || p.HostFxShootOffset is not Vector3 off || !p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) return;
        if (!_armShotsAll && BodyOf(p)?.Id.Contains("Trooper") != true && !HasHeldGun(p)) return; // (+ Brawl Zero Suit's gun: ZeroSuitGunTip.cs)
        var ahead = new Vector3(p.Field70, 0, p.Field74);
        if (ahead.LengthSquared < 1e-12f) return;
        ahead.Normalize();
        Vector3 tip = p.Position + Vector3.Cross(Vector3.UnitY, ahead) * off.X + Vector3.UnitY * off.Y + ahead * off.Z;
        Vector3 start = beam.SpawnPosition;
        Vector3 dir = beam.Direction.LengthSquared > 1e-12f ? beam.Direction.Normalized()
            : beam.Velocity.LengthSquared > 1e-12f ? beam.Velocity.Normalized() : Vector3.Zero;
        if (dir == Vector3.Zero) return;
        Scene scene = _host.Scene;
        float reach = beam.MaxDistance > 0 ? MathF.Min(beam.MaxDistance, 200) : WallCheckRange;
        var res = new CollisionResult();
        Vector3 end = start + dir * reach;
        if (CollisionDetection.CheckBetweenPoints(start, end, TestFlags.Beams, scene, ref res)) end = res.Position;
        float range = (end - start).Length;
        string mode;
        res = new CollisionResult();
        if (CollisionDetection.CheckBetweenPoints(start, tip, TestFlags.Beams, scene, ref res))
        {
            // his cannon pokes through a wall the eye is in front of: the real path, starting a little out
            mode = "tip-behind";
            if (range >= ArmShotNear) beam.HostDrawHide = 0.6f;
        }
        else if (Blocked(tip, end, scene, out float at) is not Vector3 block)
        {
            mode = "clear";
            beam.HostDrawShift = tip - start;
            beam.HostDrawFadeAt = MathF.Max(range, 0.01f);
        }
        else if (range < ArmShotNear)
        {
            mode = "point-blank"; // the shot hits right in front of him; the hit is all there is to see
        }
        else
        {
            float s = Vector3.Dot(block - start, dir); // the obstacle along the real path
            float meet = MathF.Max(ArmShotNear, s - 0.5f);
            if (at >= ArmShotNear && meet < range && Blocked(tip, start + dir * meet, scene, out _) == null)
            {
                mode = "converge";
                beam.HostDrawShift = tip - start;
                beam.HostDrawFadeAt = meet;
            }
            else
            {
                mode = "beside";
                beam.HostDrawHide = Math.Clamp(s + 0.3f, 0.3f, range);
            }
        }
        if (_cannonMeasure || _wallCheck) Log.Info("MPHCampaign", FormattableString.Invariant(
            $"armshot: render {_cannonRenders} seat {p.SlotIndex} {beam.Beam} range {range:0.0} {mode} shift {(beam.HostDrawShift?.Length ?? 0):0.00} fade {beam.HostDrawFadeAt:0.0} hide {beam.HostDrawHide:0.0}"));
    }

    // the first wall on the line from -> to (stopping just short of `to`, which may sit on a wall), and its distance from `from`
    static Vector3? Blocked(Vector3 from, Vector3 to, Scene scene, out float at)
    {
        at = 0;
        Vector3 seg = to - from;
        float len = seg.Length;
        if (len <= 0.2f) return null;
        var res = new CollisionResult();
        if (!CollisionDetection.CheckBetweenPoints(from, to - seg / len * 0.15f, TestFlags.Beams, scene, ref res)) return null;
        at = (res.Position - from).Length;
        return res.Position;
    }

    void WallCheckShot(BeamProjectileEntity beam)
    {
        if (!_wallCheck || _host == null || beam.Owner is not PlayerEntity p) return;
        var ahead = new Vector3(p.Field70, 0, p.Field74);
        if (ahead.LengthSquared < 1e-12f) return;
        ahead.Normalize();
        Vector3 left = Vector3.Cross(Vector3.UnitY, ahead);
        bool real = p.HostFxShootOffset is Vector3;
        Vector3 off = p.HostFxShootOffset ?? ProxyTip;
        Vector3 tip = p.Position + left * off.X + Vector3.UnitY * off.Y + ahead * off.Z;
        Vector3 start = beam.SpawnPosition;
        Vector3 dir = beam.Direction.LengthSquared > 1e-12f ? beam.Direction.Normalized()
            : beam.Velocity.LengthSquared > 1e-12f ? beam.Velocity.Normalized() : Vector3.Zero;
        if (dir == Vector3.Zero) return;
        Scene scene = _host.Scene;
        var res = new CollisionResult();
        Vector3 end = start + dir * WallCheckRange;
        bool wall = CollisionDetection.CheckBetweenPoints(start, end, TestFlags.Beams, scene, ref res);
        if (wall) end = res.Position;
        float range = (end - start).Length;
        string verdict;
        res = new CollisionResult();
        if (CollisionDetection.CheckBetweenPoints(start, tip, TestFlags.Beams, scene, ref res)) verdict = "tip-behind-wall";
        else
        {
            Vector3 seg = end - tip;
            float len = seg.Length;
            Vector3 stop = len > 0.2f ? end - seg / len * 0.15f : tip;
            res = new CollisionResult();
            verdict = len > 0.2f && CollisionDetection.CheckBetweenPoints(tip, stop, TestFlags.Beams, scene, ref res)
                ? FormattableString.Invariant($"line-blocked at {(res.Position - tip).Length:0.00}")
                : "clear";
        }
        _wallCheckShots++;
        Log.Info("MPHCampaign", FormattableString.Invariant(
            $"wallcheck: #{_wallCheckShots} render {_cannonRenders} seat {p.SlotIndex} {p.Hunter} {(real ? BodyOf(p)?.Id ?? "body" : "proxy")}")
            + $" {beam.Beam} range {range:0.0}{(wall ? "" : "+")} {verdict}");
    }
}
