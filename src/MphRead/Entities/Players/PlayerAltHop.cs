using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the campaign's alt-form hop (owner 2026-11-07): the other hunters' alt forms have no morph ball bombs, which
    // the adventure needs for its bomb-jump ledges, so in the campaign the ALT HOP button hops every alt form (Samus's ball
    // too, bombs kept) off the floor as high as a bomb jump (Values.BombJumpSpeed, the speed a bomb gives its owner in
    // CheckHitByBomb) with the hunter's jump sound. Host-guarded: CampaignHost turns it on for the main player only; off
    // everywhere else (matches, First Hunt, bots). ALT HOP is its own button (owner 2026-11-12: jump also boosts Samus's
    // ball, so one press did both; L1 / X by default, ControlBinds.AltHop: L1 is jump's too, and in alt form here it only
    // hops, BuildInput; A still boosts); the touch double-tap hops too.
    public partial class PlayerEntity
    {
        public bool HostAltHop { get; set; }

        // an arena's hop tunnels (Core Arenas/MainGyroChamber.cs via EchoesMovement, owner 2026-11-16): in a match every
        // hunter's alt form hops inside these boxes only; null everywhere else
        public static (Vector3 Min, Vector3 Max)[]? HostAltHopZones { get; set; }

        // the hop is on: the campaign's flag, or inside an arena's hop tunnel
        public bool AltHopActive => HostAltHop || InAltHopZone();

        private bool InAltHopZone()
        {
            (Vector3 Min, Vector3 Max)[]? zones = HostAltHopZones;
            if (zones == null) return false;
            Vector3 p = Position;
            foreach ((Vector3 min, Vector3 max) in zones)
            {
                if (p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z) return true;
            }
            return false;
        }
        public int AltHops { get; private set; } // (tests)
        private bool _altHopHeld, _altHopPressed;

        // the host's ALT HOP button this frame (CampaignHost.ApplyLocalInput, once a frame): a press hops, so holding it
        // through a morph doesn't
        public bool HostAltHopButton
        {
            get => _altHopHeld;
            set
            {
                _altHopPressed = value && !_altHopHeld;
                _altHopHeld = value;
            }
        }

        // the alt form's input (ProcessAlt, beside HyperBallInput)
        private void AltHopInput()
        {
            // (StandingPrevious too: Weavel's halfturret ball can rest without the Standing flag until it moves)
            if (!AltHopActive || HostBallJump || SpiderAttached
                || !(Flags1.TestFlag(PlayerFlags1.Standing) || Flags1.TestFlag(PlayerFlags1.StandingPrevious))
                || !(_altHopPressed || TouchJumpPressed))
            {
                return;
            }
            // the same height for everyone: Samus's bomb jump scaled for this alt form's gravity (h = v^2 / 2g), so
            // Sylux's ball (half the speed, two thirds the gravity) reaches the same ledges
            PlayerValues samus = Metadata.PlayerValues[(int)Hunter.Samus];
            float ySpeed = Fixed.ToFloat(samus.BombJumpSpeed)
                * MathF.Sqrt(Fixed.ToFloat(Values.AltAirGravity) / Fixed.ToFloat(samus.AltAirGravity));
            if (Speed.Y < ySpeed)
            {
                Speed = Speed.WithY(ySpeed);
                AltHops++;
                PlayHunterSfx(HunterSfx.Jump);
            }
        }
    }
}
