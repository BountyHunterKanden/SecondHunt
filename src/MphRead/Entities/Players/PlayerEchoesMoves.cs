using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Echoes' multiplayer movement in Echoes arenas (owner 2026-10-09; Core Arenas/EchoesMovement.cs turns it on
    // for an Echoes arena's room in a match and off on every other room load). Host-guarded: with the hooks off nothing
    // here runs and the player is MphRead's own. Values are Echoes' TweakPlayer (Metroid6's multiplayer tweak, dumped in
    // extract_out/multiplayer/mp2/mp2tweak/twpl.txt) as ratios on MPH's own speeds, so every hunter keeps its own feel:
    //   - double jump: Echoes' multiplayer loadout has the Space Jump Boots (SpawnInventory DoubleJump 1). MPH's own
    //     SpaceJump ability already allows one more jump in the air after a jump off the ground (PlayerInput's jump:
    //     a jump while standing doesn't use up the jump); every hunter gets it on spawn. MPH's jump speed for both.
    //     A ground jump is also one made while MPH still has her Grounded (HostEchoesLedgeJump): off the floor for under
    //     8 ticks without a jump or a jump pad. The arenas' floors drop her off the ground for a step or few all the time
    //     while running (slopes, seams, steps down; -echoesarena flicker: 10-20% of grounded time in most arenas, mostly
    //     1 step), and a press then was the air jump, so the second press did nothing (owner 2026-10-09, Sidehopper
    //     Station). Echoes' own LedgeFallTime (0.05 s, 3 steps) still left 1-4% of those moments; Grounded leaves ~0.
    //   - water (Pipeline's flooded cave, HostWater): submerged = her position (waist; the ball's centre) under a water
    //     box's surface. Echoes as shipped (owner 2026-10-09 "Change water to shipped"): the multiplayer loadout's Gravity
    //     Boost also gives free movement under water, so gravity, jumps and walking stay her own there (the floaty no-boost
    //     water, FluidGravitationalAccel -10 / WaterJumpFactor 0.37, was built first and dropped). No double jump under
    //     water: the air press is the Gravity Boost's.
    //   - Gravity Boost (owner: "the water booster jet pack thing for under water"): in the air under water, press jump
    //     again and hold it, as in Echoes: an upward push for up to GravityBoostTime (1.5 s), net +10/35 of her gravity
    //     (Echoes: 9000 N on 200 kg = 45 m/s^2 against 35), rising no faster than her jump speed; letting go keeps 0.3 of
    //     the rise (GravityBoostCancelDampening); once per time in the air (GravityBoostMultipleAllowed 0). Pressed while
    //     falling it stops the fall first (owner 2026-10-09: late in the arc it barely lifted, felt like it didn't fire).
    //     Holding the first jump doesn't start it (it did at first: a long first press used up the boost).
    // Sim code: plain float math on 60 Hz steps, no trig (replays and network play stay bit-exact).
    public partial class PlayerEntity
    {
        // the room being loaded is an Echoes arena in a match with Echoes movement on (set on every room load)
        public static bool HostEchoesMoves { get; set; }
        // a jump while still Grounded keeps the double jump (set with HostEchoesMoves; off = replays recorded before it)
        public static bool HostEchoesLedgeJump { get; set; }
        // no fall damage while still on a jump pad's flight (set with HostEchoesMoves; off = replays recorded before it).
        // MPH's own rule skips it only 10 frames after the pad (PlayerCollision), so a high pad's landing hurt (T6 N4)
        public static bool HostEchoesPadNoFall { get; set; }
        // that room's water boxes (MPH units; the surface = Max.Y), null = none
        public static (Vector3 Min, Vector3 Max)[]? HostWater { get; set; }

        public const float GravityBoostNet = 10f / 35f, GravityBoostKeep = 0.3f;
        public const int GravityBoostTime = 90; // steps (60 Hz): 1.5 s

        private int _echoesBoost; // steps left of a running boost (0 = none)
        private bool _echoesBoostUsed;

        // under a water box's surface now
        public bool EchoesSubmerged
        {
            get
            {
                (Vector3 Min, Vector3 Max)[]? water = HostWater;
                if (!HostEchoesMoves || water == null) return false;
                Vector3 p = Position;
                for (int i = 0; i < water.Length; i++)
                {
                    (Vector3 min, Vector3 max) = water[i];
                    if (p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z && p.Y >= min.Y && p.Y < max.Y) return true;
                }
                return false;
            }
        }

        public bool EchoesBoosting => _echoesBoost > 0;

        // Spawn: the double jump in an Echoes arena; no boost carried over
        private void EchoesMovesSpawn()
        {
            if (HostEchoesMoves)
            {
                _abilities |= AbilityFlags.SpaceJump;
            }
            _echoesBoost = 0;
            _echoesBoostUsed = false;
        }

        // PlayerInput's jump: this jump is off the ground, so it keeps the double jump. Standing, or (HostEchoesLedgeJump)
        // still Grounded: MPH clears that 8 ticks after she leaves the floor, and at once on any jump (PlayerInput sets
        // _timeSinceGrounded past it), so it never gives a third jump; a jump pad's flight is in the air at once
        private bool EchoesMovesGroundJump()
        {
            return Flags1.TestFlag(PlayerFlags1.Standing) || HostEchoesLedgeJump && Flags1.TestFlag(PlayerFlags1.Grounded)
                && !Flags1.TestFlag(PlayerFlags1.UsedJumpPad);
        }

        // PlayerInput's jump press, before it jumps: true = the press is the Gravity Boost's (in the air under water), so
        // no double jump
        private bool EchoesMovesAirPress()
        {
            if (EchoesMovesGroundJump() || !EchoesSubmerged) return false;
            Flags1 |= PlayerFlags1.UsedJump;
            if (!_echoesBoostUsed && !IsAltForm)
            {
                _echoesBoost = GravityBoostTime;
                _echoesBoostUsed = true;
                if (Speed.Y < 0) Speed = Speed.WithY(0);
            }
            return true;
        }

        // ProcessMovement's gravity for this step (g = MphRead's, negative = down); also keeps the boost
        private float EchoesMovesGravity(float g)
        {
            if (Flags1.TestFlag(PlayerFlags1.Standing))
            {
                _echoesBoostUsed = false;
                _echoesBoost = 0;
            }
            bool wet = EchoesSubmerged;
            float down = MathF.Abs(Fixed.ToFloat(Values.BipedGravity));
            float jump = Fixed.ToFloat(Values.JumpSpeed);
            if (_echoesBoost > 0)
            {
                if (!wet || IsAltForm || !Controls.Jump.IsDown)
                {
                    _echoesBoost = 0;
                    if (Speed.Y > 0 && wet) Speed = Speed.WithY(Speed.Y * GravityBoostKeep);
                }
                else
                {
                    _echoesBoost--;
                    if (Speed.Y >= jump) return 0;
                    return down * GravityBoostNet;
                }
            }
            return g;
        }
    }
}
