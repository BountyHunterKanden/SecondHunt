namespace MphRead.Entities
{
    // recomp (Dedicated match servers, docs/DEDICATED_SERVERS.md): a match host with no player of its own. Every human
    // seat is remote, MainPlayerIndex stays on seat 0 only so Main isn't null, and that seat is driven by its relayed
    // input like any other remote seat (PlayerInput.ProcessHostInput) and is never the "main player" (IsMainPlayer).
    // Set per match by MatchStart (MatchSettings.ServedHost on the host); off everywhere else.
    public partial class PlayerEntity
    {
        public static bool HostNoLocalSeat { get; set; }
    }
}
