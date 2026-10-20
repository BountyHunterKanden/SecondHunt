using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: hit claims (2026-10-19; MphRecomp Net/HitClaims.cs). A beam
    // knows which shot of its player it came from (HostShotNumber, PlayerHitClaims.cs), and a host keeps a stand-in copy of
    // each claimable shot's first beam: when an online guest's screen saw that shot hit someone the host's own copy of the
    // shot missed, and the host checked the claim, the stand-in hits them here as the shot's own hit would have (the same
    // damage, headshot, falloff, effects and kill credit as the player hit in ProcessCollision). Nothing here runs unless a
    // host rule is on (PlayerEntity.HostHitClaims) and a claim was taken.
    public partial class BeamProjectileEntity
    {
        // the owner's shot this beam came from (PlayerEntity's hit claim shot count), 0 = not one of a player's weapon shots
        public int HostShotNumber { get; set; }

        // a stand-in: what a player hit reads from the shot (Spawn's values), copied from one of its beams
        internal void HostCopyHitFields(BeamProjectileEntity b)
        {
            Beam = b.Beam;
            BeamKind = b.BeamKind;
            Flags = b.Flags;
            Owner = b.Owner;
            Damage = b.Damage;
            HeadshotDamage = b.HeadshotDamage;
            MaxDistance = b.MaxDistance;
            DamageInterpolation = b.DamageInterpolation;
            DamageDirType = b.DamageDirType;
            DamageDirMag = b.DamageDirMag;
            Afflictions = b.Afflictions;
            HostParalyzerStun = b.HostParalyzerStun;
            CollisionEffect = b.CollisionEffect;
            DrawFuncId = b.DrawFuncId;
            CylinderRadius = b.CylinderRadius;
            SpawnPosition = b.SpawnPosition;
            Position = b.SpawnPosition;
            Direction = b.Direction;
            Velocity = b.Velocity;
            Acceleration = b.Acceleration;
            HostShotNumber = b.HostShotNumber;
        }

        // a stand-in's claimed hit on `player` at `point` (head: the host's check of the claim found it a headshot): ProcessCollision's
        // player hit (a Halfturret is never claimed), without its impact effect and sound (the guest drew those already)
        internal void HostClaimHit(PlayerEntity player, Vector3 point, bool head)
        {
            Position = point;
            PlayerEntity.NetCountBeamHit(Owner, player);
            if (player.BeamEffectiveness[PirateBlaster.BaseIndex(Beam)] == Effectiveness.Zero)
            {
                return;
            }
            DamageFlags damageFlags = DamageFlags.NoDmgInvuln;
            Vector3 damageDir = GetDamageDirection(point, player.Position);
            float pct = MaxDistance > 0 ? Vector3.Distance(point, SpawnPosition) / MaxDistance : 0;
            float damage;
            if (head)
            {
                damage = MaxDistance > 0 ? GetInterpolatedValue(DamageInterpolation, HeadshotDamage, 0, pct) : HeadshotDamage;
                if (MathF.Abs(Damage - HeadshotDamage) > 1 / 4096f)
                {
                    damageFlags |= DamageFlags.Headshot;
                }
            }
            else
            {
                damage = MaxDistance > 0 ? GetInterpolatedValue(DamageInterpolation, Damage, 0, pct) : Damage;
            }
            uint wholeDamage = (uint)Math.Clamp(damage, 0, Int32.MaxValue);
            if (wholeDamage != 0)
            {
                player.TakeDamage(wholeDamage, damageFlags, damageDir, this);
                HostHitPush?.Invoke(this, player, -1);
            }
            if (Flags.TestFlag(BeamFlags.LifeDrain) && Owner is PlayerEntity ownerPlayer
                && !ownerPlayer.IsPrimeHunter && ownerPlayer.TeamIndex != player.TeamIndex)
            {
                ownerPlayer.GainHealth(wholeDamage);
            }
        }

        // a claim's headshot as ProcessCollision decides it, from the hit's height over the player then and the shot's travel
        public static bool HostClaimHeadshot(BeamType beam, Vector3 spawn, Vector3 point, Vector3 playerPos, float headHeight,
            bool altForm)
        {
            if (altForm || point.Y - playerPos.Y < headHeight - 0.3f)
            {
                return false;
            }
            return beam == BeamType.Imperialist || (point - spawn).LengthSquared <= 15 * 15;
        }
    }
}
