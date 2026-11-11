using System;
using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: DEATH's FOLLOW CAM (owner 2026-11-09: "the camera following the ragdoll before going freecam ... utilize the
    // morph/alt form cam ... hand over freecam [when the body] is barely or isn't moving anymore ... and if that doesn't
    // happen within two seconds hand it over anyway"; "I want this to happen online"). When the main player dies as a
    // drawn ragdoll, the view pulls back from the eyes to an alt-form-style chase camera on the body (the hunter's own
    // alt camera distance and height), then hands over once the body is near still, or after 2 s. The handover is silent
    // (owner 2026-11-09: "it snaps to the free cam i want free cam to kind of silently take over"): the chase camera eases
    // to a stop where it is, and from there MPH's free camera's moves and turns steer the view, so it never swings back
    // to the free camera left at the death. View only (Scene.HostView's fallback): MPH's free camera, the respawn and the
    // match go on as before underneath, so replays and online play are untouched, and on each device it follows that
    // device's own death
    public partial class PlayerEntity
    {
        // host hook: on with DEATH's RAGDOLL, part of it, no toggle (owner 2026-11-09; draw only: excluded from the replay walk)
        public static bool HostDeathFollow { get; set; }

        // host hook: a replay's watched seat (another seat than the main player's), followed the same when it dies (owner
        // 2026-11-09, "I want replays to do the follow cam"; MatchReplay.cs places its DeathFollowView). -1: none. View
        // only: excluded from the replay walk
        public static int HostDeathFollowSeat { get; set; } = -1;

        private const int DeathCamMaxTicks = 2 * 60; // the handover at the latest, 2 s after the death
        private const int DeathCamStillTicks = 10; // this many near-still steps in a row hand over early
        private const float DeathCamStillMove = 0.01f; // near still: no point of the body moved more than this in a step
        private const float DeathCamPitchLimit = 80; // the steered view's pitch limit, in degrees
        private const float DeathCamChase = 0.2f; // the camera's share of the way to its spot each step
        private const float DeathCamAim = 0.35f; // the look point's share of the way to the body each step

        private sealed class DeathCam
        {
            public PlayerRagdoll Doll = null!;
            public int Ticks;
            public int Still;
            public Vector3 Position;
            public Vector3 Target;
            public Vector3 Back;
            public bool Handed; // handed over to MPH's free camera: its moves and turns steer the view
            public Vector3 Spot; // from the handover: where the chase camera comes to a stop
            public Vector3 Look; // from the handover: the body's centre it keeps looking at
            public Vector3 Moved; // the free camera's moves since the handover, in the view's frame
            public float YawAdd; // the free camera's turns since the handover, in degrees
            public float PitchAdd;
            public float FreeYaw; // the free camera's facing at the last step, in degrees
            public float FreePitch;
            public HostView? View;
        }

        // (one object, so the replay walk excludes it as one field)
        private DeathCam? _deathCam;

        // the view while following this dead body (null: MPH's own camera). Scene.HostView falls back to the main player's
        public HostView? DeathFollowView => HostDeathFollow && _health == 0 && CameraSequence.Current == null
            ? _deathCam?.View : null;

        // this dead body was followed at its death (its view: DeathFollowView, on until the respawn)
        public bool DeathFollowed => HostDeathFollow && _health == 0 && CameraSequence.Current == null && _deathCam != null;

        // the end of UpdateCamera: MPH's camera for this step is worked out, the body stepped
        private void DeathFollowStep()
        {
            PlayerRagdoll? doll = _ragdoll;
            if (!HostDeathFollow || !IsMainPlayer && SlotIndex != HostDeathFollowSeat || _health > 0 || doll == null || !RagdollDrawn || !DrawnWhileDead
                || CameraSequence.Current != null || CameraType != CameraType.Free)
            {
                _deathCam = null;
                return;
            }
            DeathCam? cam = _deathCam;
            (float freeYaw, float freePitch) = DeathCamAngles(CameraInfo.Facing);
            if (cam == null || cam.Doll != doll)
            {
                // a new death: from the eyes, backing off behind the way the hunter faced
                var back = new Vector3(-CameraInfo.Facing.X, 0, -CameraInfo.Facing.Z);
                cam = _deathCam = new DeathCam
                {
                    Doll = doll,
                    Position = CameraInfo.Position,
                    Target = CameraInfo.Position + CameraInfo.Facing,
                    Back = back.LengthSquared > 1e-6f ? back.Normalized() : -Vector3.UnitZ,
                    FreeYaw = freeYaw,
                    FreePitch = freePitch
                };
            }
            // the free camera's turn this step (as UpdateCameraFree turned it), and its moves: past its pull-out, MPH's
            // free camera moves by the controls (a bot's don't steer: a replay watching a bot's seat keeps the stop)
            bool steered = !IsBot && _camSwitchTimer >= Values.CamSwitchTime * 2; // todo: FPS stuff (as UpdateCameraFree)
            float turnYaw = steered ? freeYaw - cam.FreeYaw : 0;
            turnYaw -= MathF.Round(turnYaw / 360) * 360;
            float turnPitch = steered ? freePitch - cam.FreePitch : 0;
            cam.FreeYaw = freeYaw;
            cam.FreePitch = freePitch;
            int forward = steered ? (Controls.MoveUp.IsDown ? 1 : Controls.MoveDown.IsDown ? -1 : 0) : 0;
            int left = steered ? (Controls.MoveLeft.IsDown ? 1 : Controls.MoveRight.IsDown ? -1 : 0) : 0;
            Vector3 centre = (doll.Pos[PlayerRagdoll.Pelvis] + doll.Pos[PlayerRagdoll.Chest]) / 2;
            bool handing = false;
            if (!cam.Handed)
            {
                cam.Ticks++;
                cam.Still = doll.Moved < DeathCamStillMove && !doll.Twitching ? cam.Still + 1 : 0;
                // the alt form's chase camera: its distance back and height over the body, keeping the side it's on
                var away = new Vector3(cam.Position.X - centre.X, 0, cam.Position.Z - centre.Z);
                if (away.LengthSquared > 0.3f * 0.3f)
                {
                    cam.Back = away.Normalized();
                }
                // (the spot kept out of walls first, so the camera eases round a ledge the body falls past, not pops)
                Vector3 spot = centre + cam.Back * Fixed.ToFloat(Values.Field78) + Vector3.UnitY * Fixed.ToFloat(Values.Field7C);
                spot = DeathCamOutOfWalls(centre, spot);
                cam.Position += (spot - cam.Position) * DeathCamChase;
                cam.Target += (centre - cam.Target) * DeathCamAim;
                cam.Position = DeathCamOutOfWalls(centre, cam.Position);
                // (the controls don't cut it short: a stick still held from the fight would end it at once)
                if (!doll.Asleep && cam.Still < DeathCamStillTicks && cam.Ticks < DeathCamMaxTicks)
                {
                    cam.View = new HostView(cam.Position, cam.Target, Vector3.UnitY, 0);
                    return;
                }
                // the handover: the chase camera stops where it's headed
                cam.Handed = true;
                handing = true;
                cam.Spot = spot;
                cam.Look = centre;
            }
            else
            {
                cam.YawAdd += turnYaw;
                cam.PitchAdd += turnPitch;
            }
            // handed over: the chase camera eases to its stop, the free camera's turns turn the view and its moves move it
            // along the view's own facing and side (UpdateCameraFree's steps), kept out of walls
            Vector3 prev = cam.Position + cam.Moved;
            if (!handing)
            {
                cam.Position += (cam.Spot - cam.Position) * DeathCamChase;
                cam.Target += (cam.Look - cam.Target) * DeathCamAim;
            }
            (float baseYaw, float basePitch) = DeathCamAngles(cam.Target - cam.Position);
            float pitch = Math.Clamp(basePitch + cam.PitchAdd, -DeathCamPitchLimit, DeathCamPitchLimit);
            cam.PitchAdd = pitch - basePitch;
            float yaw = baseYaw + cam.YawAdd;
            float yawRad = MathHelper.DegreesToRadians(yaw);
            float pitchRad = MathHelper.DegreesToRadians(pitch);
            var facing = new Vector3(MathF.Sin(yawRad) * MathF.Cos(pitchRad), MathF.Sin(pitchRad),
                MathF.Cos(yawRad) * MathF.Cos(pitchRad));
            if (!handing && (forward != 0 || left != 0))
            {
                Vector3 step = facing * (forward * 0.4f / 2);
                if (left != 0)
                {
                    // the free camera's side step, turned from its facing to the view's
                    float turn = MathHelper.DegreesToRadians(yaw - freeYaw);
                    float sx = CameraInfo.Field50 * left * 0.4f / 2;
                    float sz = CameraInfo.Field54 * left * 0.4f / 2;
                    step += new Vector3(sx * MathF.Cos(turn) + sz * MathF.Sin(turn), 0, sz * MathF.Cos(turn) - sx * MathF.Sin(turn));
                }
                cam.Moved = DeathCamOutOfWalls(prev, prev + step) - cam.Position;
            }
            Vector3 position = cam.Position + cam.Moved;
            if (_scene.Room != null && _scene.Room.Meta.HasLimits)
            {
                position = Vector3.Clamp(position, _scene.Room.Meta.CameraMin, _scene.Room.Meta.CameraMax);
            }
            cam.View = new HostView(position, position + facing, Vector3.UnitY, 0);
        }

        // a facing's yaw (as UpdateCameraFree's: atan2 of x and z) and pitch, in degrees
        private static (float Yaw, float Pitch) DeathCamAngles(Vector3 facing)
        {
            float length = facing.Length;
            if (length < 1e-6f)
            {
                return (0, 0);
            }
            float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(facing.X, facing.Z));
            float pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(facing.Y / length, -1, 1)));
            return (yaw, pitch);
        }

        // as MPH's free camera keeps out of walls (UpdateCameraFree): the way from the body to the camera, pushed out of
        // what it crosses, then kept inside the room's camera limits
        private Vector3 DeathCamOutOfWalls(Vector3 from, Vector3 pos)
        {
            float margin = 0.5f;
            IReadOnlyList<CollisionCandidate> candidates = CollisionDetection.GetCandidatesForLimits(from, pos,
                margin, null, Vector3.Zero, includeEntities: true, _scene);
            var results = new CollisionResult[8];
            int count = CollisionDetection.CheckSphereBetweenPoints(candidates, from, pos, margin,
                limit: 8, includeOffset: false, TestFlags.Players, _scene, results);
            for (int i = 0; i < count; i++)
            {
                CollisionResult result = results[i];
                float dot = -(Vector3.Dot(pos, result.Plane.Xyz) - result.Plane.W - margin);
                if (dot > 0)
                {
                    pos += result.Plane.Xyz * dot;
                }
            }
            if (_scene.Room != null && _scene.Room.Meta.HasLimits)
            {
                pos = Vector3.Clamp(pos, _scene.Room.Meta.CameraMin, _scene.Room.Meta.CameraMax);
            }
            return pos;
        }
    }
}
