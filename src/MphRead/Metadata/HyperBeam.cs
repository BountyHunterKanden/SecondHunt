using System.Collections.Generic;

namespace MphRead
{
    // recomp: the Hyper Beam (owner 2026-10-12, docs/DARK_SAMUS_HYPER.md): Metroid Prime 3's Hyper Mode beam as a
    // multiplayer pickup for everyone and Dark Samus's affinity weapon.
    // - Uncharged: one phazon bolt, 14 (18 to the head), 6 UA, at most every ShotCooldown frames (no auto fire).
    // - Charged (everyone else): a regular full charge (no partial charge), 40 (52 to the head), 25 UA, flying straight
    //   (owner 2026-10-24, option a: Corruption's PhazonBurst look, no homing).
    // - Dark Samus (her affinity row): the same uncharged shot, but a full charge held turns into Hyper Mode RAPID FIRE:
    //   RapidMP shots, 8 (11 to the head) every RapidGap frames, 4 UA each, while fire is held and the UA lasts. Releasing
    //   ends it and fires nothing more (PlayerHyperBeam.cs). About 120 a second if every shot lands, ~10 s on a full pool.
    // Drawn and heard as a recoloured Power Beam (its ROM effects and sounds: PirateBlaster.BaseBeam) until the app gives
    // it Corruption's look. BeamType.HyperBeam = 12 sits after Enemy, so like the blaster it has no row in WeaponsMP:
    // PirateBlaster.Info() is the lookup for any beam that can be it.
    public static class HyperBeam
    {
        // one bolt a press, then 0.4 s before the next (DS frames at 30 fps, as every weapon's cooldown)
        public const byte ShotCooldown = 12;
        // Dark Samus's rapid fire: a shot every 4 DS frames (x2 at 60 fps, like every timer), 7.5 a second
        public const int RapidGap = 4;
        public const ushort RapidCost = 4;

        // phazon blue (RGB555: 4,16,31 core and 16,28,31 glow; the Power Beam is 9055, 21407)
        private static readonly ushort[] _colors = { 32260, 32656 };

        public static readonly WeaponInfo WeaponMP = Make("Hyper Beam MP", ammoCost: 6, chargeCost: 25,
            damage: 14, headshot: 18, chargedDamage: 40, chargedHeadshot: 52, cooldown: ShotCooldown, charge: true);
        // her row: the charge costs only what a rapid shot does, so holding fire at full charge isn't released for want of
        // the 25 UA a charged shot would take (PlayerInput's release check); its charged shot is never fired
        public static readonly WeaponInfo WeaponMPAffinity = Make("Hyper Beam MP Affinity", ammoCost: 6, chargeCost: RapidCost,
            damage: 14, headshot: 18, chargedDamage: 14, chargedHeadshot: 18, cooldown: ShotCooldown, charge: true);
        // a rapid-fire shot (fired by PlayerHyperBeam.UpdateHyperBeamRapid, never equipped)
        public static readonly WeaponInfo RapidMP = Make("Hyper Beam MP rapid", ammoCost: RapidCost, chargeCost: RapidCost,
            damage: 8, headshot: 11, chargedDamage: 8, chargedHeadshot: 11, cooldown: RapidGap, charge: false);

        public static readonly IReadOnlyList<WeaponInfo> All = new[] { WeaponMP, WeaponMPAffinity, RapidMP };

        private static WeaponInfo Make(string description, ushort ammoCost, ushort chargeCost, ushort damage, ushort headshot,
            ushort chargedDamage, ushort chargedHeadshot, byte cooldown, bool charge)
        {
            return new WeaponInfo(
                description: description,
                beam: BeamType.HyperBeam,
                beamKind: BeamType.HyperBeam,
                drawFuncIds: new byte[] { 0, 0 },
                colors: _colors,
                priority: 2,
                flags: (charge ? WeaponFlags.CanCharge : 0) | WeaponFlags.SurfaceCollision,
                splashDamage: 0,
                minChargeSplashDamage: 0,
                chargedSplashDamage: 0,
                splashDmgTypes: new byte[] { 0, 0 },
                shotCooldown: cooldown,
                autofireCooldown: cooldown,
                ammoType: 0,
                colEffects: new byte[] { 4, 95 },
                muzzleEffects: new byte[] { 65, 65 },
                dmgDirTypes: new byte[] { 0, 0 },
                dmgInterp: new byte[] { 0, 0 },
                afflictions: new Affliction[] { Affliction.None, Affliction.None },
                padding21: 0,
                minCharge: 18,
                fullCharge: 30,
                ammoCost: ammoCost,
                minChargeCost: chargeCost,
                chargeCost: chargeCost,
                unchargedDamage: damage,
                minChargeDamage: damage,
                chargedDamage: chargedDamage,
                headshotDamage: headshot,
                minChargeHeadshotDamage: headshot,
                chargedHeadshotDamage: chargedHeadshot,
                unchargedLifespan: 255,
                minChargeLifespan: 255,
                chargedLifespan: 255,
                speedDecay: new ushort[] { 0, 0 },
                padding42: 0,
                speedInterp: new ushort[] { 0, 0 },
                unchargedDmgDirMag: 0,
                minChargeDmgDirMag: 0,
                chargedDmgDirMag: 1228, // the Power Beam's charged knockback
                zoomFov: 40960,
                unchargedCylRadius: 409,
                minChargeCylRadius: 409,
                chargedCylRadius: 819,
                unchargedSpeed: 16384,
                minChargeSpeed: 16384,
                chargedSpeed: 8192,
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
