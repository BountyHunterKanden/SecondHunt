using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead.Entities;

namespace MphRead
{
    // recomp: First Hunt's player and weapon rules (docs/FIRST_HUNT_PORT.md P1; research: docs/first_hunt/gameplay.md),
    // built from the cart's own tables in its arm9 code (the user's import; nothing of the cart is in the app):
    //   - the player values table (0x2071338, 0x108 bytes): MPH's PlayerValues order with some fields missing, so read
    //     field by field; fields First Hunt doesn't have keep MPH Samus's
    //   - the weapon table (0x2071440, 6 slots x 0x3C): Power Beam (slot 0), Electro Lob (4), Missile (5), as MphRead
    //     WeaponInfo rows in the Power Beam / Volt Driver / Missile places, so MphRead's projectile code runs them. Their
    //     look and sounds are those beams' (MPH's) until First Hunt's own (pb_normal, lightningLob, missileCollide)
    // On while a First Hunt level runs (PlayerEntity.HostFirstHunt, PlayerFirstHunt.cs).
    public static class FirstHuntRules
    {
        public const uint Arm9Address = 0x2004000;
        public const uint ValuesAddress = 0x2071338;
        public const uint WeaponsAddress = 0x2071440;
        public const int WeaponSize = 0x3C;

        // First Hunt's slots as MphRead beams
        public const BeamType ElectroLob = BeamType.VoltDriver;

        public static PlayerValues? Values { get; private set; }
        public static IReadOnlyList<WeaponInfo>? Weapons { get; private set; }
        public static ushort DoubleDamageTime { get; private set; } = 450; // values +0xe8
        public static ushort ContactDamage { get; private set; } = 5; // values +0x50 (player against player; P5)

        public static bool Loaded => Values != null && Weapons != null;

        private static byte[]? _arm9 = null;

        // a 32-bit value of the imported arm9 at a cart address (null when not imported)
        public static int? ReadArm9Int(int address)
        {
            int offset = address - (int)Arm9Address;
            if (_arm9 == null || offset < 0 || offset + 4 > _arm9.Length)
            {
                return null;
            }
            return BinaryPrimitives.ReadInt32LittleEndian(_arm9.AsSpan(offset));
        }

        // from the cart's arm9.bin (loaded at Arm9Address)
        public static void Load(byte[] arm9)
        {
            _arm9 = arm9;
            byte[] v = arm9.AsSpan((int)(ValuesAddress - Arm9Address), 0x108).ToArray();
            Values = ReadValues(v);
            DoubleDamageTime = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(0xe8));
            ContactDamage = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(0x50));
            byte[] w = arm9.AsSpan((int)(WeaponsAddress - Arm9Address), 6 * WeaponSize).ToArray();
            var list = new List<WeaponInfo>(MphRead.Weapons.WeaponsMP);
            WeaponInfo powerBeam = Weapon(w[(0 * WeaponSize)..((0 + 1) * WeaponSize)], list[(int)BeamType.PowerBeam], "First Hunt Power Beam");
            WeaponInfo electro = Weapon(w[(4 * WeaponSize)..((4 + 1) * WeaponSize)], list[(int)ElectroLob], "First Hunt Electro Lob");
            WeaponInfo missile = Weapon(w[(5 * WeaponSize)..((5 + 1) * WeaponSize)], list[(int)BeamType.Missile], "First Hunt Missile");
            foreach (int add in new[] { 0, 9 }) // (the affinity rows too: First Hunt has none)
            {
                list[(int)BeamType.PowerBeam + add] = powerBeam;
                list[(int)ElectroLob + add] = electro;
                list[(int)BeamType.Missile + add] = missile;
            }
            Weapons = list;
        }

        // one weapon table row (research: fields read by the fire code 0x201144c and the projectile create 0x2018290);
        // charged values interpolate from the uncharged ones by the charge past the minimum, as MphRead's PartialCharge
        private static WeaponInfo Weapon(byte[] r, WeaponInfo look, string description)
        {
            ushort H(int o) => BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(o));
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(o));
            byte flagsByte = r[1];
            WeaponFlags flags = look.Flags & WeaponFlags.SurfaceCollision;
            if ((flagsByte & 1) != 0)
            {
                flags |= WeaponFlags.CanCharge | WeaponFlags.PartialCharge; // (and the second pickup: PlayerFirstHunt.cs)
            }
            if ((flagsByte & 2) != 0)
            {
                flags |= WeaponFlags.RepeatFire;
            }
            ushort splash = r[2];
            ushort cost = H(0xc), chargeCost = H(0xe), damage = H(0x10), chargedDamage = H(0x12);
            int speed = I(0x14), chargedSpeed = I(0x18), gravity = I(0x1c), chargedGravity = I(0x20);
            int homing = I(0x24), chargedHoming = I(0x28), splashRadius = I(0x2c);
            // a hit: the target's radius + 0.4 (0x201789c)
            const int hitRadius = 1638;
            return new WeaponInfo(
                description: description,
                beam: look.Beam,
                beamKind: look.BeamKind,
                drawFuncIds: [look.DrawFuncIds[0], look.DrawFuncIds[1]],
                colors: [look.Colors[0], look.Colors[1]],
                priority: look.Priority,
                flags: flags | (WeaponFlags)look.Priority,
                splashDamage: splash,
                minChargeSplashDamage: splash,
                chargedSplashDamage: splash,
                splashDmgTypes: [0, 0], // linear to 0 at the radius (0x2018094)
                shotCooldown: r[3],
                autofireCooldown: r[5],
                ammoType: 1, // the one ammo pool (player +0xe0)
                colEffects: [look.CollisionEffects[0], look.CollisionEffects[1]],
                muzzleEffects: [look.MuzzleEffects[0], look.MuzzleEffects[1]],
                dmgDirTypes: [look.DmgDirTypes[0], look.DmgDirTypes[1]],
                dmgInterp: [0, 0],
                afflictions: [Affliction.None, Affliction.None],
                padding21: 0,
                minCharge: H(0x8),
                fullCharge: H(0xa),
                ammoCost: cost,
                minChargeCost: cost,
                chargeCost: chargeCost,
                unchargedDamage: damage,
                minChargeDamage: damage,
                chargedDamage: chargedDamage,
                headshotDamage: damage, // (no headshots)
                minChargeHeadshotDamage: damage,
                chargedHeadshotDamage: chargedDamage,
                unchargedLifespan: 255, // every weapon (proj +0x15)
                minChargeLifespan: 255,
                chargedLifespan: 255,
                speedDecay: [0, 0],
                padding42: 0,
                speedInterp: [0, 0],
                unchargedDmgDirMag: look.UnchargedDmgDirMag,
                minChargeDmgDirMag: look.MinChargeDmgDirMag,
                chargedDmgDirMag: look.ChargedDmgDirMag,
                zoomFov: look.ZoomFov,
                unchargedCylRadius: hitRadius,
                minChargeCylRadius: hitRadius,
                chargedCylRadius: hitRadius,
                unchargedSpeed: speed,
                minChargeSpeed: speed,
                chargedSpeed: chargedSpeed,
                unchargedFinalSpeed: speed,
                minChargeFinalSpeed: speed,
                chargedFinalSpeed: chargedSpeed,
                unchargedGravity: gravity,
                minChargeGravity: gravity,
                chargedGravity: chargedGravity,
                unchargedHoming: homing,
                minChargeHoming: homing,
                chargedHoming: chargedHoming,
                homingRange: look.HomingRange, // (First Hunt homes on the lock-on target; MPH's search stands in)
                homingTolerance: look.HomingTolerance,
                unchargedSplashRadius: splashRadius,
                minChargeSplashRadius: splashRadius,
                chargedSplashRadius: splashRadius,
                unchargedDistance: 0,
                minChargeDistance: 0,
                chargedDistance: 0,
                unchargedSpread: 0,
                minChargeSpread: 0,
                chargedSpread: 0,
                unRicoLossH: 0,
                minRicoLossH: 0,
                chRicoLossH: 0,
                unRicoLossV: 0,
                minRicoLossV: 0,
                chRicoLossV: 0,
                projectileCount: 1,
                minChargedProjectileCount: 1,
                chargeProjectileCount: 1,
                // the gun heat meter (player +0xe4): cap, clear, drain, per shot, per frame at full charge = gun smoke
                smokeStart: H(0x30),
                smokeMinimum: H(0x32),
                smokeDrain: H(0x34),
                smokeShotAmount: H(0x36),
                smokeChargeAmount: H(0x38)
            );
        }

        // the camera's half field of view, fx degrees as PlayerValues.NormalFov: the cart builds its projection from a
        // literal sin/cos pair (0xa7f/0xc13 = 41 degrees; FX_Div(sin, cos) = tan) in the camera setup's literal pool, where
        // MPH reads the hunter's NormalFov (39). Measured in BizHawk (projection 0x2088d60: cot 1.148 at 4:3 = 82 vertical).
        private const int CameraFovSinCos = 0x201ffc0;

        private static int? CameraHalfFov()
        {
            if (ReadArm9Int(CameraFovSinCos) is not int sin || ReadArm9Int(CameraFovSinCos + 4) is not int cos || sin <= 0 || cos <= 0)
            {
                return null;
            }
            return (int)Math.Round(Math.Atan2(sin, cos) * 180 / Math.PI * 4096);
        }

        // the values table, field by field (scratch fh_research/values_map.txt); First Hunt has no StandSpeedFactor,
        // AltColYPos, NormalFov (CameraHalfFov instead), Field70, Min/MaxPickupHeight, MuzzleOffset, AltFormStrafe or anything past MPH's 0x120
        private static PlayerValues ReadValues(byte[] v)
        {
            PlayerValues m = Metadata.PlayerValues[(int)Hunter.Samus];
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(o));
            ushort H(int o) => BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(o));
            short S(int o) => BinaryPrimitives.ReadInt16LittleEndian(v.AsSpan(o));
            return new PlayerValues(Hunter.Samus,
                walkBipedTraction: I(0x00), strafeBipedTraction: I(0x04), walkSpeedCap: I(0x08), strafeSpeedCap: I(0x0c),
                altMinHSpeed: I(0x10), boostSpeedCap: I(0x14), bipedGravity: I(0x18), altAirGravity: I(0x1c),
                altGroundGravity: I(0x20), jumpSpeed: I(0x24), walkSpeedFactor: I(0x28), altGroundSpeedFactor: I(0x2c),
                strafeSpeedFactor: I(0x30), airSpeedFactor: I(0x34), standSpeedFactor: m.StandSpeedFactor,
                rollAltTraction: I(0x38), altColRadius: I(0x3c), altColYPos: m.AltColYPos,
                boostChargeMin: H(0x40), boostChargeMax: H(0x42), boostSpeedMin: I(0x44), boostSpeedMax: I(0x48),
                altHSpeedCapIncrement: I(0x4c), field58: I(0x54), field5C: I(0x58), walkBobMax: I(0x5c),
                aimDistance: I(0x60), camSwitchTime: H(0x64), padding6A: 0, normalFov: CameraHalfFov() ?? m.NormalFov, field70: m.Field70,
                aimYOffset: I(0x68), field78: I(0x6c), field7C: I(0x70), field80: I(0x74), field84: I(0x78),
                field88: I(0x7c), field8C: I(0x80), field90: I(0x84),
                minPickupHeight: m.MinPickupHeight, maxPickupHeight: m.MaxPickupHeight,
                bipedColRadius: I(0x88), fieldA0: I(0x8c), fieldA4: I(0x90), fieldA8: I(0x94),
                damageInvuln: S(0x98), damageFlashTime: H(0x9a), fieldB0: I(0x9c), fieldB4: I(0xa0), fieldB8: I(0xa4),
                muzzleOffset: m.MuzzleOffset,
                // +0xa8 is a bomb fuse (35) where MPH has BombCooldown; the squared bomb radii are 0 in First Hunt
                // (its bomb code squares the radii itself): MPH's kept for both until the bomb code is ported
                bombCooldown: m.BombCooldown, bombSelfRadius: I(0xac), bombSelfRadiusSquared: m.BombSelfRadiusSquared,
                bombRadius: I(0xb4), bombRadiusSquared: m.BombRadiusSquared, bombJumpSpeed: I(0xbc),
                bombRefillTime: I(0xc0), bombDamage: S(0xc4), bombEnemyDamage: S(0xc6), fieldE0: S(0xc8),
                spawnInvulnerability: S(0xca), aimMinTouchTime: H(0xcc), paddingE6: 0,
                fieldE8: I(0xd0), fieldEC: I(0xd4), swayStartTime: I(0xd8), swayIncrement: I(0xdc), swayLimit: I(0xe0),
                gunIdleTime: I(0xe4), mpAmmoCap: S(0xea), ammoRecharge: v[0xec], padding103: 0, energyTank: H(0xee),
                field106: S(0xf0), altFormStrafe: m.AltFormStrafe, padding109: 0, padding10A: 0,
                fallDamageSpeed: I(0xf4), fallDamageMax: I(0xf8), field114: I(0xfc), field118: I(0x100),
                jumpPadSlideFactor: I(0x104),
                altTiltAngleCap: m.AltTiltAngleCap, altMinWobble: m.AltMinWobble, altMaxWobble: m.AltMaxWobble,
                altMinSpinAccel: m.AltMinSpinAccel, altMaxSpinAccel: m.AltMaxSpinAccel, altMinSpinSpeed: m.AltMinSpinSpeed,
                altMaxSpinSpeed: m.AltMaxSpinSpeed, altTiltAngleMax: m.AltTiltAngleMax, altBounceWobble: m.AltBounceWobble,
                altBounceTilt: m.AltBounceTilt, altBounceSpin: m.AltBounceSpin,
                altAttackKnockbackAccel: m.AltAttackKnockbackAccel, altAttackKnockbackTime: m.AltAttackKnockbackTime,
                altAttackStartup: m.AltAttackStartup, field154: m.Field154, field158: m.Field158,
                lungeHSpeed: m.LungeHSpeed, lungeVSpeed: m.LungeVSpeed, altAttackDamage: m.AltAttackDamage,
                altAttackCooldown: m.AltAttackCooldown);
        }
    }
}
