using System.IO;

namespace MphRecomp.Multiplayer.Net
{
    // Fruity Prime's movement sync on a served match (docs/FRUITY_PORT.md P1, 2026-10-20): one switch for the match,
    // MatchSettings.FruitySync, set by the match host (--fruity-p1 on; the server's --served-fruity-p1, the PC test's fruity=1)
    // and sent to the joiners as one byte after the settings on MsgWelcome/MsgStart (an older client stops reading before
    // it). On, it replaces our pieces with theirs, host and device alike: the report is the seat's place (ClientPosition
    // FruityOffer), latest input wins with no pacing (NetMatchSession), the device corrects only on a long divergence
    // (ClientPrediction.FruityCorrect) and draws others at their read point (RemoteSmoothing.FruityRead). Phone-hosted and LAN
    // matches never have it, so they run as before.
    public static class FruitySync
    {
        public const byte Mark = 0x46; // 'F'

        // Fruity P3 pickups (docs/FRUITY_PORT.md, 2026-11-13): a second byte after the mark says the match host honours
        // pickup claims (Net/PickupClaims.cs), so a device may predict its own pickups; an older host sends only the mark
        // and its devices wait for the host as before
        public const byte PickupsMark = 0x50; // 'P'

        // Fruity P4 lives (docs/FRUITY_PORT.md, 2026-11-13): a third byte, after PickupsMark, says the match host stamps its
        // snapshots with each seat's life (match id, slot generation, life id: NetMatchSession.Lives.cs) and drops input
        // packets a device stamped with a life the seat no longer has; a device of such a host stamps its inputs
        public const byte LivesMark = 0x4C; // 'L'

        // Fruity P2 shot events (docs/FRUITY_PORT.md, 2026-11-13): a fourth byte, after LivesMark, says the match host fires
        // a device's seat from the shot events its inputs carry (NetMatchSession.Shots.cs), not from the trigger; a device of
        // such a host sends its own player's shots as events
        public const byte ShotsMark = 0x53; // 'S'

        // this match (MatchNetDriver sets it as each match starts)
        public static bool On { get; set; }

        // the last MsgWelcome/MsgStart carried PickupsMark (a joiner only; read by MatchNetDriver as the match starts)
        public static bool HostPickups { get; private set; }

        // the last MsgWelcome/MsgStart carried LivesMark (a joiner only: its inputs then carry the life trailer)
        public static bool HostLives { get; private set; }

        // the last MsgWelcome/MsgStart carried ShotsMark (a joiner only: its inputs then carry the shot trailer)
        public static bool HostShots { get; private set; }

        public static bool Read(BinaryReader r)
        {
            bool on = r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == Mark;
            HostPickups = on && r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == PickupsMark;
            HostLives = HostPickups && r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == LivesMark;
            HostShots = HostLives && r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == ShotsMark;
            return on;
        }
    }
}
