namespace MphRead.Entities
{
    // recomp (P8, 8-player matches; docs/ONLINE_PLAY.md section 10): the game's per-player tables hold 4 seats. They are
    // sized by SlotCapacity instead, and every loop over seats runs to MaxPlayers, which stays the game's own 4 unless
    // a match host sets it for a match of 5-8 seats (MatchStart). A 1-4 player match is then exactly vanilla.
    public partial class PlayerEntity
    {
        public const int VanillaMaxPlayers = 4;
        public const int SlotCapacity = 8;

        // what MaxPlayers is for a match of this many seats: 4 (vanilla) up to 4 seats, 8 above
        public static int MaxPlayersFor(int seats) => seats > VanillaMaxPlayers ? SlotCapacity : VanillaMaxPlayers;
    }
}
