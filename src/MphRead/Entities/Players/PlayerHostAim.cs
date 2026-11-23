using System;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: third-person true aim. The third-person camera (Scene.HostCameraOffset) sits behind, above and beside the
    // eye, and the reticle shows the eye's aim point 60 units out, so only something that far away was hit where the
    // reticle was: up close a shot from the gun ran off to the side and low of it (a hunter's head got the chest, owner
    // 2026-11-12). With true aim the aim point is the first thing the camera's ray through that point hits: the room, a
    // closed door or force field, an enemy or another hunter. It stays on that ray, so the reticle doesn't move, and the
    // gun and its shots point at it.
    //
    // Sim state, not the app's (owner 2026-11-13: "true aim for third person for local matches too"): the app passes its
    // camera's side and distance each step as CampaignInput.TrueAim (HostTrueAimCode; 0 = vanilla aim), the host sets each
    // seat's HostTrueAimCam from that (a LAN guest's comes with its input, puppets' with the snapshot), and the camera is
    // rebuilt here from the seat's own CameraInfo by HostThirdPersonOffset, the same function the app draws with. So a
    // replay (the code is in its inputs), a LAN host and every guest compute the same aim. Online sends 0 (first person).
    public partial class PlayerEntity
    {
        // the third-person camera: behind, above and beside the eye (CAMERA SIDE: right or left), pulled in by TpMargin
        // short of the room's collision
        public const float HostTpBack = 2.4f, HostTpUp = 0.35f, HostTpSide = 0.75f, HostTpMargin = 0.3f;

        // this seat's third-person camera for the aim: 0 = none (vanilla aim); bit 15 on, bit 14 left shoulder, bits 0-13
        // the camera's distance before this step's ease in 4096ths (HostTrueAimCode)
        public ushort HostTrueAimCam;

        public static ushort HostTrueAimCode(bool left, float dist)
        {
            int units = !(dist < 0x3FFF / 4096f) ? 0x3FFF : dist > 0 ? (int)MathF.Round(dist * 4096) : 0; // (MaxValue: snap out)
            return (ushort)(0x8000 | (left ? 0x4000 : 0) | units);
        }

        // the third-person camera for a first-person view: the offset from the eye, with dist eased toward what the room
        // allows (snap in toward a wall, ease back out) and kept in 4096ths, so the code the app sends next holds it exactly.
        // False when the view has no camera yet (a new player before it spawns: all zeros)
        public static bool HostThirdPersonOffset(Scene scene, Matrix4 view, bool left, ref float dist, out Vector3 offset)
        {
            offset = Vector3.Zero;
            if (!(MathF.Abs(view.Determinant) > 1e-3f))
            {
                return false;
            }
            Matrix4 cam = view.Inverted(); // rows: right, up, back, position
            Vector3 eye = cam.Row3.Xyz;
            Vector3 want = cam.Row2.Xyz.Normalized() * HostTpBack + cam.Row1.Xyz.Normalized() * HostTpUp
                + cam.Row0.Xyz.Normalized() * (left ? -HostTpSide : HostTpSide);
            float len = want.Length;
            Vector3 dir = want / len;
            float allowed = len;
            CollisionResult res = default;
            if (CollisionDetection.CheckBetweenPoints(eye, eye + dir * (len + HostTpMargin), TestFlags.Players, scene, ref res))
            {
                allowed = Math.Clamp(res.Distance * (len + HostTpMargin) - HostTpMargin, 0, len);
            }
            dist = allowed < dist ? allowed : dist + (allowed - dist) * 0.15f;
            dist = MathF.Min(MathF.Round(dist * 4096), 0x3FFF) / 4096f;
            offset = dir * dist;
            return true;
        }

        private void HostAimFromCamera()
        {
            if (HostTrueAimCam == 0 || IsAltForm || HostSeated)
            {
                return;
            }
            // rebuilt from this step's camera each call (UpdateAimVecs runs twice a frame), as the app will after the step
            float dist = (HostTrueAimCam & 0x3FFF) / 4096f;
            if (!HostThirdPersonOffset(_scene, CameraInfo.ViewMatrix, (HostTrueAimCam & 0x4000) != 0, ref dist, out Vector3 offset)
                || offset == Vector3.Zero)
            {
                return;
            }
            Vector3 eye = CameraInfo.Position;
            Vector3 cam = eye + offset;
            // vanilla's point, rebuilt as its own setters do (UpdateAimX/Y, the grapple's and the barrier's): UpdateAimVecs runs
            // twice a frame and not every frame sets it, so this never starts from an already moved one
            Vector3 aim = eye + _gunVec1 * Fixed.ToFloat(Values.AimDistance);
            Vector3 dir = aim - cam;
            float len = dir.Length;
            if (!(len > 1))
            {
                return;
            }
            dir /= len;
            // from the eye's depth on the ray: what's between the camera and the player isn't aimed at
            float start = Math.Clamp(Vector3.Dot(eye - cam, dir), 0, len - 1);
            Vector3 p1 = cam + dir * start;
            float hit = HostAimRayHit(p1, aim); // 0..1 from p1 to aim
            if (hit >= 1)
            {
                return;
            }
            // not right at the eye: a shot from the gun at a spot level with it goes sideways
            float depth = MathF.Max(start + (len - start) * hit, MathF.Min(start + 1, len));
            _aimPosition = cam + dir * depth;
        }

        // the nearest beam stop on p1 -> p2, as BeamProjectileEntity.CheckCollision tests a shot
        private float HostAimRayHit(Vector3 p1, Vector3 p2)
        {
            const float radius = 0.1f;
            float minDist = 1;
            CollisionResult res = default;
            if (CollisionDetection.CheckBetweenPoints(p1, p2, TestFlags.Beams, _scene, ref res) && res.Distance < minDist
                && Vector3.Dot(p1, res.Plane.Xyz) - res.Plane.W >= 0)
            {
                minDist = res.Distance;
            }
            foreach (DoorEntity door in _scene.GetDoorEntities())
            {
                if (door.Flags.TestFlag(DoorFlags.Open) || door.ConnectorInactive)
                {
                    continue;
                }
                Vector3 doorFacing = door.FacingVector;
                Vector3 lockPos = door.LockPosition;
                var plane = new Vector4(doorFacing, 0);
                if (Vector3.Dot(p1 - lockPos, doorFacing) < 0)
                {
                    plane *= -1;
                }
                Vector3 wvec = plane.Xyz * (lockPos + 0.4f * plane.Xyz);
                plane.W = wvec.X + wvec.Y + wvec.Z;
                if (CollisionDetection.CheckCylinderIntersectPlane(p1, p2, plane, ref res) && res.Distance < minDist
                    && (res.Position - lockPos).LengthSquared < door.RadiusSquared)
                {
                    minDist = res.Distance;
                }
            }
            foreach (ForceFieldEntity forceField in _scene.GetForceFieldEntities())
            {
                if (forceField.Active && CollisionDetection.CheckCylinderIntersectPlane(p1, p2, forceField.Plane, ref res)
                    && res.Distance < minDist)
                {
                    Vector3 between = res.Position - forceField.Position;
                    float up = Vector3.Dot(between, forceField.FieldUpVector);
                    float right = Vector3.Dot(between, forceField.FieldRightVector);
                    if (MathF.Abs(up) <= forceField.Height && MathF.Abs(right) <= forceField.Width)
                    {
                        minDist = res.Distance;
                    }
                }
            }
            foreach (EnemyInstanceEntity enemy in _scene.GetEnemyInstanceEntities())
            {
                res = default;
                if (enemy.Flags.TestFlag(EnemyFlags.CollideBeam)
                    && CollisionDetection.CheckCylinderOverlapVolume(enemy.HurtVolume, p1, p2, radius, ref res)
                    && res.Distance < minDist)
                {
                    minDist = res.Distance;
                }
            }
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                res = default;
                if (player == this || player.Health == 0 || player.HostSeated || !player.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                if (CollisionDetection.CheckCylinderOverlapSphere(p1, p2, player.Volume.SpherePosition,
                    player.Volume.SphereRadius + radius, ref res) && res.Distance < minDist)
                {
                    minDist = res.Distance;
                }
            }
            return Math.Clamp(minDist, 0, 1);
        }
    }
}
