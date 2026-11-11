using System;

namespace MphRead.Entities
{
    // recomp: the campaign's alt-form hop (owner 2026-11-07): the other hunters' alt forms have no morph ball bombs, which
    // the adventure needs for its bomb-jump ledges, so in the campaign the jump button hops every alt form (Samus's ball
    // too, bombs kept) off the floor as high as a bomb jump (Values.BombJumpSpeed, the speed a bomb gives its owner in
    // CheckHitByBomb) with the hunter's jump sound. Host-guarded: CampaignHost turns it on for the main player only; off
    // everywhere else (matches, First Hunt, bots).
    public partial class PlayerEntity
    {
        public bool HostAltHop { get; set; }
        public int AltHops { get; private set; } // (tests)

        // the alt form's input (ProcessAlt, beside HyperBallInput)
        private void AltHopInput()
        {
            // (StandingPrevious too: Weavel's halfturret ball can rest without the Standing flag until it moves)
            if (!HostAltHop || HostBallJump || SpiderAttached
                || !(Flags1.TestFlag(PlayerFlags1.Standing) || Flags1.TestFlag(PlayerFlags1.StandingPrevious))
                || !(Controls.Jump.IsPressed || TouchJumpPressed))
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
