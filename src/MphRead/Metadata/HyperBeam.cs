using System.Collections.Generic;

namespace MphRead
{
    // recomp: the Hyper Beam (owner 2026-10-12, docs/DARK_SAMUS_HYPER.md): Metroid Prime 3's Hyper Mode beam as a
    // multiplayer pickup for everyone and Dark Samus's affinity weapon, fired as Corruption's gun fires it (main.dol and the
    // owner's video, 11-13; owner: "it's a new beam just like pirate blaster"):
    // - Uncharged: one phazon bolt a press, 8 (11 to the head), 4 UA, at most every ShotCooldown (Corruption's 0.2 s refire;
    //   no auto fire).
    // - Charged: Corruption's Hyper charge, 2.5 s to full (its tweak +0x124), released from MinCharge on (PartialCharge).
    //   The release is a STREAM of PhazonBurst shots, one every StreamGap, for max(0.1, charge) x StreamTime (tweak +0x128:
    //   up to 2.5 s, 75 shots), flying straight (owner 2026-10-24, option a). The release shot is the stream's first and
    //   pays for it all (10 UA at MinCharge to 25 at full); the rest (StreamMP, PlayerHyperBeam.cs) cost nothing.
    //   3 (4 to the head) a shot; Dark Samus's affinity rows 4 (5).
    // Drawn and heard as a recoloured Power Beam (its ROM effects and sounds: PirateBlaster.BaseBeam) until the app gives
    // it Corruption's look. BeamType.HyperBeam = 12 sits after Enemy, so like the blaster it has no row in WeaponsMP:
    // PirateBlaster.Info() is the lookup for any beam that can be it.
    public static class HyperBeam
    {
        // one bolt a press, then 0.2 s before the next (DS frames at 30 fps, as every weapon's cooldown). Longer than the
        // stream's gap, so the stream's shots (each resetting the time since a shot) keep presses out while it runs
        public const byte ShotCooldown = 6;
        // the charge: 2.5 s to full, a release from 0.6 s on (DS frames)
        public const byte MinCharge = 18;
        public const byte FullCharge = 75;
        // the stream: a shot every DS frame (x2 at 60 fps, like every timer), for max(0.1, charge) x StreamTime DS frames.
        // Corruption's: while the gun's +0x214 bit 0x40 is set (main.dol 0x8018b07c) a charged shot goes out (0x8018b0c4) on
        // each frame whose count since the stream's start (player +0x318 minus -0x7378(r13)) is even (0x8018b0a8): every other
        // 60 Hz frame, 30 a second (owner 11-15 "match corruption"; was 2, 15 a second, read off the owner's video)
        public const int StreamGap = 1;
        public const int StreamTime = 75;
        // the stream's own slots after a player's 16 (PlayerEntity.BeamPool; BeamProjectileEntity.ChooseBeamSlot): a shot
        // flies up to its PSLT 80 frames (BeamHyperWind.HyperWindLifeMax), so up to 40 of a stream are alive at once, and the
        // next release's can overlap the last ones. Corruption has no 16-shot cap; its own projectile limit isn't ported
        // (not found in main.dol), so this is sized never to fill (-hyperbeamtest checks)
        public const int StreamPool = 48;

        // the charged Hyper Beam's shots (a release and its stream) take the stream's slots
        public static bool InStreamPool(EquipInfo equip)
        {
            return equip.Weapon.Beam == BeamType.HyperBeam && equip.ChargeLevel >= equip.Weapon.MinCharge * 2; // todo: FPS stuff
        }

        // phazon blue (RGB555: 4,16,31 core and 16,28,31 glow; the Power Beam is 9055, 21407)
        private static readonly ushort[] _colors = { 32260, 32656 };

        public static readonly WeaponInfo WeaponMP = Make("Hyper Beam MP", ammoCost: 4, minChargeCost: 10, chargeCost: 25,
            damage: 8, headshot: 11, chargedDamage: 3, chargedHeadshot: 4, partial: true);
        public static readonly WeaponInfo WeaponMPAffinity = Make("Hyper Beam MP Affinity", ammoCost: 4, minChargeCost: 10,
            chargeCost: 25, damage: 8, headshot: 11, chargedDamage: 4, chargedHeadshot: 5, partial: true);
        // the stream's shots after the release's (fired by PlayerHyperBeam.UpdateHyperBeamStream at full charge, so they're
        // charged shots; never equipped)
        public static readonly WeaponInfo StreamMP = Make("Hyper Beam MP stream", ammoCost: 0, minChargeCost: 0, chargeCost: 0,
            damage: 3, headshot: 4, chargedDamage: 3, chargedHeadshot: 4, partial: false);
        public static readonly WeaponInfo StreamMPAffinity = Make("Hyper Beam MP stream Affinity", ammoCost: 0, minChargeCost: 0,
            chargeCost: 0, damage: 4, headshot: 5, chargedDamage: 4, chargedHeadshot: 5, partial: false);

        public static readonly IReadOnlyList<WeaponInfo> All = new[] { WeaponMP, WeaponMPAffinity, StreamMP, StreamMPAffinity };

        // a stream shot's row for the row the release came from
        public static WeaponInfo Stream(WeaponInfo weapon) => weapon == WeaponMPAffinity ? StreamMPAffinity : StreamMP;

        private static WeaponInfo Make(string description, ushort ammoCost, ushort minChargeCost, ushort chargeCost,
            ushort damage, ushort headshot, ushort chargedDamage, ushort chargedHeadshot, bool partial)
        {
            return new WeaponInfo(
                description: description,
                beam: BeamType.HyperBeam,
                beamKind: BeamType.HyperBeam,
                drawFuncIds: new byte[] { 0, 0 },
                colors: _colors,
                priority: 2,
                flags: WeaponFlags.CanCharge | (partial ? WeaponFlags.PartialCharge : 0) | WeaponFlags.SurfaceCollision,
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
                minCharge: MinCharge,
                fullCharge: FullCharge,
                ammoCost: ammoCost,
                minChargeCost: minChargeCost,
                chargeCost: chargeCost,
                unchargedDamage: damage,
                // a release at any charge fires the stream's shot (its length is what the charge buys)
                minChargeDamage: chargedDamage,
                chargedDamage: chargedDamage,
                headshotDamage: headshot,
                minChargeHeadshotDamage: chargedHeadshot,
                chargedHeadshotDamage: chargedHeadshot,
                unchargedLifespan: 255,
                minChargeLifespan: 255,
                chargedLifespan: 255,
                speedDecay: new ushort[] { 0, 0 },
                padding42: 0,
                speedInterp: new ushort[] { 0, 0 },
                unchargedDmgDirMag: 0,
                minChargeDmgDirMag: 0,
                chargedDmgDirMag: 0,
                zoomFov: 40960,
                unchargedCylRadius: 409,
                minChargeCylRadius: 409,
                chargedCylRadius: 409,
                // recomp: the press shot flies Corruption's PhazonBeam speed: WPSC 06fe1d75af37114a IVEC CNST (0, 2.5, 0) at file
                // offset 0x58, metres a 60 Hz frame, x 1.6/2.7 m to our units (BeamHyperWind.HyperWindScale) = 1.4815 u a tick;
                // speed / 4096 / 2 a tick, so 2.5 x 1.6/2.7 x 8192 = 12136.3 (12136: 1.48145, 0.002% short). Was 16384 (2.0 u).
                // The charged release's stream flies its own WPSC (BeamHyperWind.cs), not these.
                unchargedSpeed: 12136,
                minChargeSpeed: 16384,
                chargedSpeed: 16384,
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
