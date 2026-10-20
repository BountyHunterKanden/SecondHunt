namespace MphRead.Entities
{
    // recomp (Dedicated match servers, docs/DEDICATED_SERVERS.md): a match host with no player of its own. Every human
    // seat is remote, MainPlayerIndex stays on seat 0 only so Main isn't null, and that seat is driven by its relayed
    // input like any other remote seat (PlayerInput.ProcessHostInput) and is never the "main player" (IsMainPlayer).
    // Set per match by MatchStart (MatchSettings.ServedHost on the host); off everywhere else.
    public partial class PlayerEntity
    {
        public static bool HostNoLocalSeat { get; set; }

        // recomp host hook (a replay of a served match, MatchReplay.cs; drawing only): the match plays seatless as it was
        // recorded, and the HUD shown is HostHudSeat's, Main (seat 0) included, set up and updated by Renderer like a replay's
        // other seat (HostUpdateSeatHud); the match never reads it. Default off (a served host itself draws no HUD).
        public static bool HostSeatlessHud { get; set; }

        // a HUD to draw: always with a local seat; seatless, only a replay's watched seat once its HUD is set up
        public static bool HostHudShown => !HostNoLocalSeat || HostSeatlessHud && HostHudSeat is PlayerEntity p
            && p._hostHudReady && p.LoadFlags.TestFlag(LoadFlags.Active);
    }
}
