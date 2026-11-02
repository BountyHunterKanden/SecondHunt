using System;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// SETTINGS > LOOK > VIEW "aim view" (RecompSettings.AimView, owner 2026-11-02: a player's touch and gyro lag report, "we want
// this to work for anyone in any case"; SecondHunt issue #11 "Static reticle"). The game's first-person view looks along
// the hunter's facing, which eases after the aim (_gunVec1) by a tenth twice a step: a flick left the view 2-12 degrees
// behind the aim for 50-180 ms at any refresh rate, and the reticle slid off the centre while it caught up.
// - INSTANT (default): each picture turns the step's view about the eye by the turn from the facing to the aim, so the
//   view is where the aim is now (the walk's bob and tilt kept); the reticle is placed again for the turned view.
//   (A STATIC option that also took the walk's nod off the view was dropped the same day: no difference to see.)
// - CLASSIC: the game's own eased view.
// With smooth frames on, the aim's own last turn is carried on by the picture's share of the next step (in place of the
// camera's turn). Draw only, like smooth frames: the gun, the arm cannon and the main hunter keep the step's view
// (GunView, RidesCamera), the game reads nothing new, and replays and online play are unchanged.
// Hooks: LookRecordStep (SmoothRecordStep), LookView (SmoothBegin), LookReticleShift (DrawHud), LookCullWiden
// (Scene.HostDrawCullWiden). The room's portal culling found the walls to draw from the step's view only, so walls at the
// edge of the turned view popped in (owner 2026-11-02, Alinos Gateway): the draw list now comes from a frustum widened by
// the turn, and the room then finds the step's own visibility again for the game's logic (enemies, doors, sounds), so
// replays and online are unchanged. The entities' draws (doors, platforms, pickups, objects) use the widened parts too
// (Scene.HostDrawVisibility, owner 2026-11-02 "still a little pop in"); the hunters' draws and anything the game reads
// keep the step's.
internal sealed partial class CampaignRenderer
{
    volatile string _aimView = "instant";
    bool _lookOn; // this picture is turned to the aim
    bool _lookHave0, _lookHave1;
    Vector3 _lookGun0, _lookGun1; // the aim after the step before last / the last step
    Vector3 _lookEye, _lookCarry, _lookCarryAxis; // this picture: the step's eye, smooth frames' move, the aim's carried turn
    float _lookCarryAngle;

    // after each step (SmoothRecordStep): the aim, for smooth frames' carry
    void LookRecordStep()
    {
        _lookGun0 = _lookGun1;
        _lookHave0 = _lookHave1;
        _lookHave1 = LookPlayer(_sfScene) != null;
        if (_lookHave1) _lookGun1 = PlayerEntity.Main.NetAimVector.Normalized();
    }

    // the main hunter when this scene's view is their own first-person eyes, else null
    PlayerEntity? LookPlayer(Scene? scene)
    {
        if (_aimView == "classic" || scene == null || scene != _sfScene || _host == null || MovieShowing) return null;
        if (CameraSequence.Current != null || ReplayFreeCam || PlayerEntity.HostFirstPersonSeat >= 0) return null;
        if (scene.HostView != null || scene.HostCameraOffset != Vector3.Zero) return null;
        PlayerEntity? main = PlayerEntity.Main;
        if (main == null || !main.IsMainPlayer || main.CameraType != CameraType.First || main.Health == 0) return null;
        if (main.IsAltForm || main.IsMorphing || main.IsUnmorphing) return null;
        return main;
    }

    // SmoothBegin: the picture's view turned to the aim (phase: the share of the next step to carry the aim's turn by, 0
    // without smooth frames; carry: smooth frames' camera move for this picture). False leaves the view alone.
    bool LookView(Scene scene, float phase, Vector3 carry, ref Matrix4 view)
    {
        _lookOn = false;
        if (LookPlayer(scene) is not PlayerEntity main) return false;
        Vector3 facing = main.FacingVector, aim = main.NetAimVector;
        if (facing.LengthSquared < 1e-6f || aim.LengthSquared < 1e-6f) return false;
        facing.Normalize();
        aim.Normalize();
        _lookCarryAngle = 0;
        if (phase > 0 && _lookHave0 && _lookHave1)
        {
            Vector3 axis = Vector3.Cross(_lookGun0, _lookGun1);
            float sin = axis.Length, angle = MathF.Atan2(sin, Vector3.Dot(_lookGun0, _lookGun1));
            if (sin > 1e-5f && angle < SmoothTurnJump)
            {
                _lookCarryAxis = axis / sin;
                _lookCarryAngle = angle * phase;
                aim = Rotate(aim, _lookCarryAxis, _lookCarryAngle).Normalized();
            }
        }
        Matrix4 world = scene.ViewMatrix.Inverted();
        _lookEye = world.Row3.Xyz;
        _lookCarry = carry;
        Vector3 pos = world.Row3.Xyz + carry, fwd = -world.Row2.Xyz.Normalized(), up = world.Row1.Xyz.Normalized();
        Vector3 turnAxis = Vector3.Cross(facing, aim);
        float turnSin = turnAxis.Length;
        if (turnSin > 1e-6f)
        {
            turnAxis /= turnSin;
            float turn = MathF.Atan2(turnSin, Vector3.Dot(facing, aim));
            fwd = Rotate(fwd, turnAxis, turn);
            up = Rotate(up, turnAxis, turn);
        }
        view = Matrix4.LookAt(pos, pos + fwd, up);
        _lookOn = true;
        return true;
    }

    const float LookCullMargin = 0.15f; // radians (~9 degrees): the walk's nod, the eye's carry

    // Scene.HostDrawCullWiden, as the step builds its draw list: how far the pictures until the next step can turn from
    // the step's view (the facing-to-aim turn, plus the aim's carried turn with smooth frames)
    float LookCullWiden(Scene scene)
    {
        if (LookPlayer(scene) is not PlayerEntity main) return 0;
        Vector3 facing = main.FacingVector, aim = main.NetAimVector;
        if (facing.LengthSquared < 1e-6f || aim.LengthSquared < 1e-6f) return 0;
        facing.Normalize();
        aim.Normalize();
        float widen = MathF.Acos(Math.Clamp(Vector3.Dot(facing, aim), -1f, 1f));
        if (_smoothFrames && _lookHave1)
            widen += MathF.Min(MathF.Acos(Math.Clamp(Vector3.Dot(_lookGun1, aim), -1f, 1f)), SmoothTurnJump);
        return widen + LookCullMargin;
    }

    // DrawHud: how far to move the reticle (NDC) to where the aim lands in the turned view
    bool LookReticleShift(Scene scene, out float dx, out float dy)
    {
        dx = dy = 0;
        if (!_lookOn || !_sfOn || scene != _sfScene || scene.HostReticleItem < 0) return false;
        // the step's aim point turned about the eye by the same carry as the view, so the reticle keeps its place in the
        // view between steps (placed at the step's point it fell behind each picture and snapped back each step: on a
        // 120 Hz screen a second, jumpy reticle trailing the first, owner 2026-11-02)
        Vector3 aimAt = scene.HostReticleAim;
        if (_lookCarryAngle != 0) aimAt = _lookEye + Rotate(aimAt - _lookEye, _lookCarryAxis, _lookCarryAngle);
        aimAt += _lookCarry;
        Matrix.ProjectPosition(aimAt, scene.ViewMatrix, scene.PerspectiveMatrix, out Vector2 pos);
        Vector2 at = Scene.HostWarpScreenPos(pos); // the Panini view, as PlayerHud places it
        Vector2 was = scene.HostReticleAt;
        dx = (at.X - was.X) * scene.HostHudNdcPerX;
        dy = -2 * (at.Y - was.Y);
        return true;
    }
}
