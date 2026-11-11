using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: camera-relative strafe alt forms under a room's fixed camera (owner 2026-11-07). Trace's, Sylux's and
    // Weavel's alt forms steer like a biped, relative to where they face, which is fine behind their own follow camera
    // but not when a MorphCameraEntity takes over the view (Ice Hive's angled platforms): there the d-pad/stick moves
    // them relative to the room camera's view, as the rolling alt forms already do (the same _altRollFb/_altRollLr
    // directions, held across a camera switch while the direction stays held), and the body turns toward where it moves.
    // The default follow camera is untouched. Host-guarded: CampaignHost turns it on for the main player only; off
    // everywhere else (matches, First Hunt, bots). AltCamMoveOff (CampaignLaunch `cammove off`, dev) compares the old.
    public partial class PlayerEntity
    {
        public bool HostAltCamMove { get; set; }
        public static bool AltCamMoveOff { get; set; }

        private bool AltCamMoveActive => HostAltCamMove && !AltCamMoveOff && !IsBot && IsAltForm
            && Values.AltFormStrafe != 0 && MorphCamera != null;

        // recomp: a strafe alt form pushing into an edge or wall (A12 follow-up, owner 2026-11-08: Sylux "struggles on ledges
        // and edges" after the A12 accel fix). Vanilla zeroes the speed into the edge every 30 Hz tick and adds the whole
        // traction again, so each tick pushes ~traction into it and the push-out lifts the ball over the lip. A12's
        // per-frame weighting (s / (1 + s), right for free running) halved that push, as the collision zeroes it every
        // frame here. So the part of the traction aimed into the last frame's edge (its flat normal, set by the
        // collision) gets the whole weight back. AltEdgePushOff (dev) compares without.
        // Not when an upright wall the ball also pushes into faces the same way (owner 2026-11-08, vc 584: Sylux bounced
        // in corners and the vertical tunnel): that edge is the wall's, the ball can't get over it, and pushing climbed
        // it a little each frame and dropped back, a bob that never settled.
        private Vector3 _altEdgeNormal;
        private Vector3 _altWallNormal;
        public static bool AltEdgePushOff { get; set; }

        private Vector3 _altCamWish; // this frame's held direction on the ground plane (ProcessAlt's strafe moves)

        // ProcessMovement: turn the aim and body toward the held direction (not the speed, which a slope or wall turns)
        private void AltCamMoveFace()
        {
            Vector3 wish = _altCamWish;
            _altCamWish = Vector3.Zero;
            float len = MathF.Sqrt(wish.X * wish.X + wish.Z * wish.Z);
            if (!AltCamMoveActive || len < 1 / 4096f)
            {
                return;
            }
            wish /= len;
            _gunVec1 = TurnToward(_gunVec1, wish);
            _facingVector = TurnToward(_facingVector, wish);

            // a quarter of the angle left per frame (at least ~3 degrees, so a turn right round still starts)
            static Vector3 TurnToward(Vector3 vec, Vector3 dir)
            {
                float h = MathF.Sqrt(vec.X * vec.X + vec.Z * vec.Z);
                if (h < 1 / 4096f)
                {
                    return vec;
                }
                float angle = MathF.Atan2(vec.Z, vec.X);
                float delta = MathF.Atan2(dir.Z, dir.X) - angle;
                if (delta > MathF.PI)
                {
                    delta -= 2 * MathF.PI;
                }
                else if (delta < -MathF.PI)
                {
                    delta += 2 * MathF.PI;
                }
                float step = delta * 0.25f;
                if (MathF.Abs(step) < 0.05f)
                {
                    step = MathF.Abs(delta) < 0.05f ? delta : (delta < 0 ? -0.05f : 0.05f);
                }
                angle += step;
                return new Vector3(MathF.Cos(angle) * h, vec.Y, MathF.Sin(angle) * h);
            }
        }
    }
}
