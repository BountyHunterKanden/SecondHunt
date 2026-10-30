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

        // this match (MatchNetDriver sets it as each match starts)
        public static bool On { get; set; }

        public static bool Read(BinaryReader r)
            => r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == Mark;
    }
}
