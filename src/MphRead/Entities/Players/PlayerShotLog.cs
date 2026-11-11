using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp dev: TRY CONTROLS shot log (owner 2026-10-31, an Imperialist hit on Kanden while aiming to his side). Each
    // Imperialist shot of the local player logs where the reticle line (camera + aim) and the shot line (muzzle to the aim
    // point 60 units out, the game's own shot vector) pass each other player's standing hit cylinder, and each hit logs
    // where on the target it landed. The game hits a standing player when the beam segment comes within BipedColRadius +
    // the beam's CylinderRadius (0.5 + 0.1 for the Imperialist) of its feet-to-head axis, from MinPickupHeight (-0.5) to
    // MaxPickupHeight (1.1); a head hit is at MaxPickupHeight - 0.3 or above. Null (no log) everywhere but a dev TRY
    // CONTROLS (SoloArena.ConfigureTry); draws nothing, changes nothing.
    public partial class PlayerEntity
    {
        public static Action<string>? HostShotLog { get; set; }

        private static int _shotLogCount;

        private void ShotLogFire(Vector3 shotVec)
        {
            if (HostShotLog == null || this != Main || CurrentWeapon != BeamType.Imperialist)
            {
                return;
            }
            _shotLogCount++;
            Vector3 cam = CameraInfo.Position;
            Vector3 right = Vector3.Cross(_gunVec1, Vector3.UnitY);
            right = right.LengthSquared > 1e-6f ? right.Normalized() : Vector3.UnitX;
            float muzzleSide = Vector3.Dot(_muzzlePos - cam, right);
            float muzzleUp = (_muzzlePos - cam).Y;
            HostShotLog($"#{_shotLogCount} fire zoomed={EquipInfo.Zoomed} cam={F(cam)} aim={F(_gunVec1)} muzzle={F(_muzzlePos)}"
                + $" (side {muzzleSide:+0.000;-0.000} up {muzzleUp:+0.000;-0.000} from the camera) aimPoint={F(_aimPosition)}"
                + $" shot={F(shotVec)} beamRadius={EquipInfo.Weapon.UnchargedCylRadius / 4096f:0.000}");
            float beamRadius = EquipInfo.Weapon.UnchargedCylRadius / 4096f;
            foreach (EntityBase entity in _scene.Entities)
            {
                if (entity is not PlayerEntity target || target == this || target.Health == 0)
                {
                    continue;
                }
                float limit = target.IsAltForm ? target.Volume.SphereRadius + beamRadius
                    : Fixed.ToFloat(target.Values.BipedColRadius) + beamRadius;
                float minY = Fixed.ToFloat(target.Values.MinPickupHeight);
                float maxY = Fixed.ToFloat(target.Values.MaxPickupHeight);
                Pass(cam, _gunVec1, target, right, out float aimMiss, out float aimSide, out float aimY, out float aimDist);
                Pass(_muzzlePos, shotVec, target, right, out float shotMiss, out float shotSide, out float shotY, out _);
                if (aimDist < 0 || aimMiss > 4 && shotMiss > 4)
                {
                    continue;
                }
                string form = target.IsAltForm ? $"alt form, sphere at {F(target.Volume.SpherePosition)}" : "standing";
                HostShotLog($"#{_shotLogCount}   {target.Hunter} {form} at {F(target.Position)} dist {aimDist:0.00}:"
                    + $" reticle line passes {aimMiss:0.000} from the axis (side {aimSide:+0.000;-0.000}, height {aimY:+0.00;-0.00}),"
                    + $" shot line {shotMiss:0.000} (side {shotSide:+0.000;-0.000}, height {shotY:+0.00;-0.00});"
                    + $" hits within {limit:0.000}, height {minY:0.00}..{maxY:0.00}, head from {maxY - 0.3f:0.00}"
                    + $" -> shot {(shotMiss <= limit && shotY >= minY && shotY <= maxY ? "IN" : "out")}"
                    + $", reticle {(aimMiss <= limit && aimY >= minY && aimY <= maxY ? "IN" : "out")}");
            }
        }

        // where the line from+t*dir comes closest to the target's vertical axis (in the ground plane): the horizontal miss,
        // its side along the shooter's right, the height there above the target's feet, and t (the distance along the line)
        private static void Pass(Vector3 from, Vector3 dir, PlayerEntity target, Vector3 right,
            out float miss, out float side, out float height, out float t)
        {
            Vector3 axis = target.IsAltForm ? target.Volume.SpherePosition : target.Position;
            var d = new Vector2(dir.X, dir.Z);
            var a = new Vector2(axis.X - from.X, axis.Z - from.Z);
            float len2 = d.LengthSquared;
            t = len2 > 1e-8f ? Vector2.Dot(a, d) / len2 : 0;
            Vector3 q = from + dir * t;
            var off = new Vector3(q.X - axis.X, 0, q.Z - axis.Z);
            miss = off.Length;
            side = Vector3.Dot(off, right);
            height = q.Y - target.Position.Y;
        }

        internal static void ShotLogHit(BeamProjectileEntity beam, PlayerEntity target, Vector3 hitPos, bool head,
            bool halfturret, uint damage)
        {
            if (HostShotLog == null || beam.Owner != Main || beam.Beam != BeamType.Imperialist)
            {
                return;
            }
            var off = new Vector3(hitPos.X - target.Position.X, 0, hitPos.Z - target.Position.Z);
            HostShotLog($"#{_shotLogCount}   HIT {target.Hunter}{(halfturret ? " (halfturret)" : "")} at {F(hitPos)}:"
                + $" {off.Length:0.000} from the axis, height {hitPos.Y - target.Position.Y:+0.00;-0.00}, {(head ? "HEAD" : "body")}"
                + $" {damage} dmg, beam travelled {Vector3.Distance(beam.Position, beam.SpawnPosition):0.00}"
                + $" (segment {F(beam.BackPosition)} -> {F(beam.Position)}), target now at {F(target.Position)}");
        }

        private static string F(Vector3 v)
        {
            return $"({v.X:0.000} {v.Y:0.000} {v.Z:0.000})";
        }
    }
}
