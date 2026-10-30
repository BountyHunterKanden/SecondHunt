using System.Collections.Generic;

namespace MphRecomp.Online
{
    // Head to head on the server (T3, server 0.7.0; docs/ONLINE_PLAY.md section 5): one record per pair of players, counted
    // once per finished match from the host's results, so both players' licenses show the same numbers (before, each device
    // counted its own and they drifted apart). Vanilla's license fields: rounds against, victories against, the last result.

    // a finished match's places, from the host's RESULTS. Online: GameId = the game it was played in (the server checks every
    // code against who was in that game when the match started; one report per match). LAN: GameId empty and Lan set (a LAN
    // match the server never saw: it trusts the host's word, a few an hour). MatchKey = the host's own id for this match (a
    // queued report sent again is counted once). Rows: every human seat with an online identity (bots and seats without one
    // left out); a team mode compares across teams only. The reply is an OkMsg (Done = counted now, false = already counted).
    // Errors: bad_result, not_in_game, too_many. An older server drops a connection that sends it (ServerAtLeast(SinceServer)).
    public sealed class MatchResultReq : OnlineMsg
    {
        public const string SinceServer = "0.7.0";
        public const int MaxRows = 8;
        public string GameId { get; set; } = "";
        public bool Lan { get; set; }
        public string MatchKey { get; set; } = "";
        public bool Teams { get; set; }
        public List<ResultRow> Rows { get; set; } = new();
    }

    public sealed class ResultRow
    {
        public string Code { get; set; } = "";
        // 0 = first; equal places tie
        public int Place { get; set; }
        public int Team { get; set; } = -1;
    }

    // the viewer's record against a player (PlayerCard.Record): Last = HeadToHeadInfo.Victory / Defeat / Tie from the viewer's
    // side
    public sealed class HeadToHeadInfo
    {
        public const string Victory = "victory", Defeat = "defeat", Tie = "tie";
        public int Rounds { get; set; }
        public int Victories { get; set; }
        public string Last { get; set; } = "";
    }
}
