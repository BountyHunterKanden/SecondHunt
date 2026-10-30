using System.Collections.Generic;

namespace MphRead
{
    // recomp: the Pirate Blaster (owner 2026-10-09): Metroid Prime 1's
    // Space Pirate beam as a multiplayer pickup for everyone and the Trooper's affinity weapon. A 3-bolt BURST (owner pick):
    // 12 a bolt (16 to the head), the bolts 3 frames apart, a burst every 20 frames (about 1.5x the Power Beam's held fire,
    // 54 a second), 5 UA a burst, no charge. Drawn and heard as a recoloured Power Beam (its ROM effects and sounds:
    // BaseBeam) until a Prime import gives it its own look.
    // BeamType.PirateBlaster = 11 sits after Enemy, so it has no row in Weapons1P/WeaponsMP (index = beam, + 9 = affinity):
    // Info() is the lookup for any beam that can be it.
    public static class PirateBlaster
    {
        // 2026-10-19, a little lighter:
        // a bolt's damage and its headshot's while MatchSettings.LighterBlaster is on (set per match by MatchStart; 0 = the
        // rows' 12 / 16, as replays from before it): 10 / 14, about 45 a second held instead of 54
        public const int LighterBolt = 10, LighterHeadshot = 14;
        // 2026-10-20, a little lighter again: with MatchSettings.LighterBlaster2
        // on too, 9 / 13 (8 / 12 was tried and dropped), about 40 a second held (replays before format 56: 10 / 14)
        public const int LighterBolt2 = 9, LighterHeadshot2 = 13;
        public static int HostBoltDamage { get; set; }
        public static int HostBoltHeadshot { get; set; }
        // 2026-10-20, each bolt takes a shot of ammo: with this on (MatchSettings.BlasterBoltCost,
        // set per match by MatchStart; off = replays from before it) the burst's second and third bolts pay 5 UA each too, and a
        // burst that runs dry stops there
        public static bool HostBoltCost { get; set; }

        public const int BurstBolts = 3;
        public const int BurstGap = 3; // DS frames between bolts (x2 at 60 fps, like every cooldown)

        // the weapon's row for any beam (vanilla: Weapons.Current, + 9 for the affinity version)
        public static WeaponInfo Info(BeamType beam, bool affinity = false)
        {
            if (beam == BeamType.PirateBlaster)
            {
                return affinity ? WeaponMPAffinity : WeaponMP;
            }
            if (beam == BeamType.Paralyzer) // recomp: Zero Suit's Paralyzer (Metadata/Paralyzer.cs) reads its rows here too
            {
                return affinity ? Paralyzer.WeaponMPAffinity : Paralyzer.WeaponMP;
            }
            if (beam == BeamType.HyperBeam) // recomp: the Hyper Beam (Metadata/HyperBeam.cs) too
            {
                return affinity ? HyperBeam.WeaponMPAffinity : HyperBeam.WeaponMP;
            }
            return Weapons.Current[(int)beam + (affinity ? 9 : 0)];
        }

        // vanilla tables indexed by beam (ROM effects, sounds, gun animations, HUD icon frames, effectiveness): the blaster
        // uses the Power Beam's rows (and so do the Paralyzer and the Hyper Beam)
        public static BeamType BaseBeam(BeamType beam) => beam == BeamType.PirateBlaster || beam == BeamType.Paralyzer
            || beam == BeamType.HyperBeam ? BeamType.PowerBeam : beam;

        public static int BaseIndex(BeamType beam) => (int)BaseBeam(beam);

        // a beam the player can hold: vanilla's 0-8, the blaster, the Paralyzer and the Hyper Beam
        public static bool IsPlayerBeam(BeamType beam) => beam >= BeamType.PowerBeam && beam <= BeamType.OmegaCannon
            || beam == BeamType.PirateBlaster || beam == BeamType.Paralyzer || beam == BeamType.HyperBeam;

        // Space Pirate red-orange bolts (RGB555: 31,12,4 core and 31,24,16 glow; the Power Beam is 9055, 21407)
        private static readonly ushort[] _colors = { 4511, 17183 };

        public static readonly WeaponInfo WeaponMP = Make("Pirate Blaster MP", ammoCost: 5);
        public static readonly WeaponInfo WeaponMPAffinity = Make("Pirate Blaster MP Affinity", ammoCost: 5);
        // the burst's second and third bolts: the burst's 5 UA is paid by the first
        public static readonly WeaponInfo BoltMP = Make("Pirate Blaster MP bolt", ammoCost: 0);
        public static readonly WeaponInfo BoltMPAffinity = Make("Pirate Blaster MP Affinity bolt", ammoCost: 0);

        public static readonly IReadOnlyList<WeaponInfo> All = new[] { WeaponMP, WeaponMPAffinity, BoltMP, BoltMPAffinity };

        private static WeaponInfo Make(string description, ushort ammoCost)
        {
            return new WeaponInfo(
                description: description,
                beam: BeamType.PirateBlaster,
                beamKind: BeamType.PirateBlaster,
                drawFuncIds: new byte[] { 0, 0 },
                colors: _colors,
                priority: 2,
                flags: WeaponFlags.RepeatFire | WeaponFlags.SurfaceCollision,
                splashDamage: 0,
                minChargeSplashDamage: 0,
                chargedSplashDamage: 0,
                splashDmgTypes: new byte[] { 0, 0 },
                shotCooldown: 20,
                autofireCooldown: 20,
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
                minChargeCost: ammoCost,
                chargeCost: ammoCost,
                unchargedDamage: 12,
                minChargeDamage: 12,
                chargedDamage: 12,
                headshotDamage: 16,
                minChargeHeadshotDamage: 16,
                chargedHeadshotDamage: 16,
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
                unchargedSpeed: 16384,
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
