namespace MphRead.Entities
{
    // recomp: Dark Samus walks hovering (D8: drawn floating over the floor), so she has no footsteps and no first-person
    // walk bob (release test notes N13, owner 2026-10-10: "For dark Samus can we eliminate foot step sound and the walking
    // bounce"). Host-guarded: off unless the match host sets HostHoverWalk for her seat (MatchStart). The bob moves the
    // camera, which the gun's muzzle follows, so it's sim: replays from before it play with it off (Settings.HoverWalk).
    // Her footstep timer and its random pick still run (the RNG stream stays the same); only the sound isn't played.
    public partial class PlayerEntity
    {
        public bool HostHoverWalk { get; set; }

        // ProcessBiped, after the walk bob's update: held level (cos 270 = 0, as standing still)
        private void HoverWalkBob()
        {
            _gunViewBob = 270;
            _walkViewBob = 0;
        }
    }
}
