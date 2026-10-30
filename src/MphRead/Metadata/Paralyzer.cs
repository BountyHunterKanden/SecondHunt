using System.Collections.Generic;

namespace MphRead
{
    // recomp: the Paralyzer (owner 2026-10-12, docs/ZERO_SUIT_SAMUS.md section 7):
    // Zero Suit Samus's stun pistol as a multiplayer pickup for everyone and her affinity weapon.
    // - One bolt a trigger press like Brawl's (owner 2026-10-15: "greatly reduce the rate of fire"; was 4 a shot every 3
    //   frames), then "much quicker ... slower than power beam ... much stronger bolts so that much more precise aiming is
    //   rewarded": 15 (19 to the head) at most every ShotCooldown frames (3 a second, half the Power Beam's 6), no auto
    //   fire, the Power Beam's reach and speed. About 45 a second if every bolt lands, over the Power Beam's 36.
    // - Charged (her affinity row only: nobody else can charge it, owner 10-15; full charge only, no partial charge): a
    //   short-range blast, ~30 (40 to the head), that fizzles out after ChargedReach. It STUNS (PlayerParalyzer.cs): the
    //   target can't move, jump or change form for StunFrames but can still aim and shoot.
    // Drawn and heard as a recoloured Power Beam (its ROM effects and sounds: PirateBlaster.BaseBeam) until it has its own
    // look. BeamType.Paralyzer = 13 (12 is the Hyper Beam's, reserved), so like the blaster it has no row in WeaponsMP:
    // PirateBlaster.Info() is the lookup for any beam that can be it.
    public static class Paralyzer
    {
        // the stun (DS frames x2 at 60 fps, like every timer): 1.5 s, then 2 s before the same player can be stunned again
        public const int StunFrames = 45 * 2;
        public const int ImmunityFrames = 60 * 2;

        // no armour (owner pick): 90 health where every other hunter has 100. MP spawns at energy tank - 1 (99) with a max
        // of 2 tanks - 1 (199), so hers are 89 and 179 (about 11% more damage taken, without rounding every hit)
        public const int HealthPercent = 90;

        // the charged blast's reach: 1 unit a DS frame for 2 frames = 2 units from the muzzle, up close and personal (owner
        // 10-15: "only so big that you have to get really close and personal to actually paralyze someone"; was 6 units)
        public const int ChargedSpeed = 4096;
        public const ushort ChargedLifespan = 2;

        // Zero Mission's Paralyzer blue (RGB555: 16,28,31 core and 24,31,31 glow; the Power Beam is 9055, 21407)
        private static readonly ushort[] _colors = { 32656, 32760 };

        // one bolt a press, then 1/3 s before the next (DS frames at 30 fps, as every weapon's cooldown; was 18 = 0.6 s)
        public const byte ShotCooldown = 10;

        public static readonly WeaponInfo WeaponMP = Make("Paralyzer MP", charge: false);
        public static readonly WeaponInfo WeaponMPAffinity = Make("Paralyzer MP Affinity", charge: true);

        public static readonly IReadOnlyList<WeaponInfo> All = new[] { WeaponMP, WeaponMPAffinity };

        // a hit that stuns: her affinity row's charged shot (the affinity row is only ever her seat's: HostAffinityBeam)
        public static bool Stuns(WeaponInfo? weapon, bool charged) => charged && weapon == WeaponMPAffinity;

        private static WeaponInfo Make(string description, bool charge)
        {
            return new WeaponInfo(
                description: description,
                beam: BeamType.Paralyzer,
                beamKind: BeamType.Paralyzer,
                drawFuncIds: new byte[] { 0, 0 },
                colors: _colors,
                priority: 2,
                flags: (charge ? WeaponFlags.CanCharge : 0) | WeaponFlags.SurfaceCollision,
                splashDamage: 0,
                minChargeSplashDamage: 0,
                chargedSplashDamage: 0,
                splashDmgTypes: new byte[] { 0, 0 },
                shotCooldown: ShotCooldown,
                autofireCooldown: ShotCooldown,
                ammoType: 0,
                colEffects: new byte[] { 4, 95 },
                muzzleEffects: new byte[] { 65, 65 },
                dmgDirTypes: new byte[] { 0, 0 },
                dmgInterp: new byte[] { 0, 0 },
                afflictions: new Affliction[] { Affliction.None, Affliction.None },
                padding21: 0,
                minCharge: 18,
                fullCharge: 30,
                ammoCost: 1,
                minChargeCost: 10,
                chargeCost: 10,
                unchargedDamage: 15,
                minChargeDamage: 15,
                chargedDamage: 30,
                headshotDamage: 19,
                minChargeHeadshotDamage: 19,
                chargedHeadshotDamage: 40,
                unchargedLifespan: 255,
                minChargeLifespan: 255,
                chargedLifespan: ChargedLifespan,
                speedDecay: new ushort[] { 0, 0 },
                padding42: 0,
                speedInterp: new ushort[] { 0, 0 },
                unchargedDmgDirMag: 0,
                minChargeDmgDirMag: 0,
                chargedDmgDirMag: 0, // no knockback: a stunned target stays where it was hit
                zoomFov: 40960,
                unchargedCylRadius: 409,
                minChargeCylRadius: 409,
                chargedCylRadius: 819,
                unchargedSpeed: 12288,
                minChargeSpeed: 12288,
                chargedSpeed: ChargedSpeed,
                unchargedFinalSpeed: 0,
                minChargeFinalSpeed: 0,
                chargedFinalSpeed: 0,
                unchargedGravity: 0,
                minChargeGravity: 0,
                chargedGravity: 0,
                unchargedHoming: 0,
                minChargeHoming: 0,
                chargedHoming: 0,
                homingRange: 409600,
                homingTolerance: 3896,
                unchargedSplashRadius: 0,
                minChargeSplashRadius: 0,
                chargedSplashRadius: 0,
                unchargedDistance: 0,
                minChargeDistance: 0,
                chargedDistance: 0,
                unchargedSpread: 0,
                minChargeSpread: 0,
                chargedSpread: 0,
                unRicoLossH: 3686,
                minRicoLossH: 3686,
                chRicoLossH: 3686,
                unRicoLossV: 3686,
                minRicoLossV: 3686,
                chRicoLossV: 3686,
                projectileCount: 1,
                minChargedProjectileCount: 1,
                chargeProjectileCount: 1,
                smokeStart: 75,
                smokeMinimum: 25,
                smokeDrain: 5,
                smokeShotAmount: 10,
                smokeChargeAmount: 75
            );
        }
    }
}
