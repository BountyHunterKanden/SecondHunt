using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: splash on the Trooper's standing body (2026-10-19). MphRead's splash
    // (CheckSplashDamage) only looks at players, never a Halfturret: fine for Weavel's small turret, but the Trooper's half
    // is a whole standing body (HalfturretEntity.HostBodyHit), so a missile or Magmaul blast at its feet did nothing to it.
    // With PlayerEntity.HostDroneBodySplash (MatchSettings.DroneBodySplash, per seat by MatchStart; older replays: off) the
    // splash reaches that body as it reaches a player: by its distance to the body's standing cylinder (MinPickupHeight to
    // MaxPickupHeight around HostBodyPosition, BipedColRadius wide), with nothing solid in between, the same falloff, and
    // the damage taken as a hit on the body (DamageFlags.Halfturret: shared between the body and him, as a beam's).
    // Never his own splash (his own shots don't hurt his body). With HostBodySplashOnce (MatchSettings.BodySplashOnce,
    // 2026-10-19: one hit per blast; off before format 53) a blast that already hit him, by its splash or directly,
    // leaves his body alone: the two share his health, so it took both before.
    public partial class BeamProjectileEntity
    {
        public static bool HostBodySplashOnce { get; set; }

        // the players this blast's splash hit (CheckSplashDamage, by slot), so the standing body's isn't a second hit
        private uint _splashHitMask;

        private void HostSplashStandingBodies(EntityBase? colWith)
        {
            foreach (PlayerEntity player in _scene.GetPlayerEntities())
            {
                if (HostBodySplashOnce && (colWith == player || (_splashHitMask & (1u << player.SlotIndex)) != 0))
                {
                    continue;
                }
                if (!player.HostScoutDrone || !player.HostDroneBodySplash || player.Health == 0
                    || !player.Flags2.TestFlag(PlayerFlags2.Halfturret) || player.Halfturret is not HalfturretEntity body
                    || body.Health <= 0 || colWith == body || Owner == player || Owner == body)
                {
                    continue;
                }
                Vector3 feet = body.HostBodyPosition;
                float bottom = feet.Y + Fixed.ToFloat(player.Values.MinPickupHeight);
                float top = feet.Y + Fixed.ToFloat(player.Values.MaxPickupHeight);
                float radius = Fixed.ToFloat(player.Values.BipedColRadius);
                var flat = new Vector2(Position.X - feet.X, Position.Z - feet.Z);
                float flatLen = flat.Length;
                Vector2 edge = flatLen > radius ? flat * (radius / flatLen) : flat;
                var nearest = new Vector3(feet.X + edge.X, Math.Clamp(Position.Y, bottom, top), feet.Z + edge.Y);
                float dist = Vector3.Distance(Position, nearest);
                if (dist >= SplashRadius)
                {
                    continue;
                }
                CollisionResult discard = default;
                if (CollisionDetection.CheckBetweenPoints(Position, nearest, TestFlags.Beams, _scene, ref discard))
                {
                    continue;
                }
                float ratio = MathF.Min(dist / SplashRadius, 1);
                int damage = (int)GetInterpolatedValue(SplashDamageType, SplashDamage, 0, ratio);
                if (damage <= 0)
                {
                    continue;
                }
                player.TakeDamage(damage, DamageFlags.NoDmgInvuln | DamageFlags.Halfturret, GetDamageDirection(Position, nearest), this);
                HostHitPush?.Invoke(this, player, ratio);
            }
        }
    }
}
