using System;
using MphRecomp.Online;
using MphRecomp.Multiplayer;

// ONLINE PLAY's lobby for a game of 5-8 seats (milestone P8, docs/ONLINE_PLAY.md section 10): the same seat cards in the same
// panel at half height, four rows of two. Up to 4 seats the lobby is the mockup's 2 x 2 (OnlineMenus.cs, unchanged). The
// half-height cards are their own menu items (a tap area is fixed when the page is built): the lobby shows one set or the
// other.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private const int LobbySeats8 = 8;
        private const int OnTeam8Call = 2800; // + the seat (to 2807): a half-height card's TEAM box
        private const int OnTeam8LastCall = OnTeam8Call + LobbySeats8 - 1;
        // (152 tall at a 170 pitch: the bottom-left card's centre stays above the back arrow's corner region, where an item
        // centred would be drawn: MenuLayout.Replays "back", y below -150)
        private const float Card8Pitch = 170, Card8Height = 152;

        private readonly int[] _lbSeat8 = Unset(LobbySeats8), _lbTeam8 = Unset(LobbySeats8);

        // more seats than the 2 x 2 holds: the half-height cards
        private static bool LobbyEight(int seats) => seats > LobbySeats;

        private int LobbySeatItem(int k, int seats) => LobbyEight(seats) ? _lbSeat8[k] : _lbSeat[k];

        private int LobbyTeamItem(int k, int seats) => LobbyEight(seats) ? _lbTeam8[k] : _lbTeam[k];

        // a card's top-left corner in mockup pixels
        private static (float X, float Y) LobbyCardCorner(int k, int seats)
            => (100 + k % 2 * 650, 222 + k / 2 * (LobbyEight(seats) ? Card8Pitch : 352));

        // where a card's top-right word goes ("muted", "talking", "voice off": OnlineMenus.Chat.cs / .Voice.cs)
        private static (float X, float Y) LobbyCardLabel(int k, int seats)
        {
            (float cx, float cy) = LobbyCardCorner(k, seats);
            return (Mx(cx + 590), My(cy + (LobbyEight(seats) ? 14 : 20)));
        }

        private static (float X0, float Top, float X1, float Bottom) SeatRect8(int k)
        {
            (float cx, float cy) = LobbyCardCorner(k, LobbySeats8);
            return (Mx(cx), My(cy), Mx(cx + 610), My(cy + Card8Height));
        }

        private static (float X0, float Top, float X1, float Bottom) TeamRect8(int k)
        {
            (float cx, float cy) = LobbyCardCorner(k, LobbySeats8);
            return (Mx(cx + 430), My(cy + 104), Mx(cx + 590), My(cy + 144));
        }

        // the lobby page's half-height cards and TEAM boxes (after the 2 x 2's: InstallOnline)
        private void InstallLobby8(MenuPage lobby, Func<int, float, float, float, float, int, MenuItem> input)
        {
            for (int k = 0; k < LobbySeats8; k++)
            {
                (float x0, float top, float x1, float bottom) = SeatRect8(k);
                int seat = k;
                _lbSeat8[k] = lobby.AddItem(i => input(i, x0, top, x1, bottom, OnSeatCall + seat)).Index;
            }
            for (int k = 0; k < LobbySeats8; k++)
            {
                (float x0, float top, float x1, float bottom) = TeamRect8(k);
                int seat = k;
                _lbTeam8[k] = lobby.AddItem(i => input(i, x0, top, x1, bottom, OnTeam8Call + seat)).Index;
            }
        }

        // one half-height card's contents (DrawLobby draws the box, the team stripe and the TEAM box)
        private void DrawSeatCard8(LobbySeat seat, int k, int tag, bool host, bool choosing, bool away, int ping)
        {
            (float cx, float cy) = LobbyCardCorner(k, LobbySeats8);
            if (seat.Kind == SeatKind.Open)
            {
                EmptySeat(Mx(cx + 20), My(cy + 16), Mw(120), tag);
                Model(@"startgame\adbot", Mx(cx + 50), My(cy + 46), Mw(60), Mw(60), tag);
                TextS("open", Mx(cx + 165), My(cy + 32), 0, OnGrey, tag, SizeM);
                if (host) TextS("invite", Mx(cx + 165), My(cy + 88), 0, Orange, tag, SizeM);
                return;
            }
            bool you = seat.Kind == SeatKind.You;
            HunterDisc(seat, Mx(cx + 20), My(cy + 16), Mw(120), tag);
            string name = seat.Card != null ? OnlineNames.Display(seat.Card.Name, seat.Card.Tag) : seat.Player?.Nickname ?? "";
            TextS(Fit(name.ToLowerInvariant(), 15), Mx(cx + 165), My(cy + 18), 0, White, tag, SizeM);
            string what = seat.Player != null ? HunterWord(seat.Player) : "";
            if (seat.Host) what += (what.Length > 0 ? "   " : "") + "host";
            if (choosing) TextS("choosing a hunter...", Mx(cx + 165), My(cy + 64), 0, Cyan, tag, SizeX);
            else if (away) TextS("on the results...", Mx(cx + 165), My(cy + 64), 0, OnGrey, tag, SizeX);
            else TextS(what, Mx(cx + 165), My(cy + 64), 0, Orange, tag, SizeX);
            // (a team game's TEAM box takes the card's bottom right: the short words)
            bool teams = (_hosting ?? _joined)?.Net.Settings is MatchSettings ms && MatchSettings.IsTeamMode(ms.Mode);
            // (a host's bot card: LEVEL there and REMOVE where the check goes, OnlineMenus.LobbyBots.cs; release 0.2.0 F2)
            bool botBoxes = host && seat.Kind == SeatKind.Bot && seat.Index >= 0;
            string third = you ? (teams ? "tap to change" : "tap to change hunter")
                : seat.Kind != SeatKind.Bot || botBoxes ? "" : host ? $"bot level {seat.Player!.BotLevel + 1}" : "bot";
            if (third.Length > 0) TextS(third, Mx(cx + 165), My(cy + 102), 0, you ? Pale : OnGrey, tag, SizeX);
            else if (ping >= 0) Ping(Mx(cx + 165), My(cy + 100), Mw(30), ping, tag);
            if (seat.Index >= 0 && !botBoxes)
            {
                Model(choosing || away ? @"gameoptions\cancel" : @"gameoptions\ok", Mx(cx + 540), My(cy + 46), Mw(52), Mw(52), tag);
            }
        }
    }
}
