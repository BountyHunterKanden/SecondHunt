using System;
using System.Collections.Generic;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

// ONLINE PLAY's lobby controls (release 0.2.0 F2, owner 2026-10-11: "a GUEST can't change their own team colour (host can);
// the host can't remove or change bots"). On the host's bot cards: a tap on the card steps the bot's hunter, LEVEL steps its
// level (1-3), REMOVE takes it out; on an open seat ADD BOT adds one (the smaller team) while the game has fewer than 8 seats
// (the open seat's own tap still invites). A guest's TEAM box on its own card asks the host for the other team
// (NetMatchSession.RequestTeam, LOOK's optional tail); the host's TEAM boxes swap any seat as before. The boxes are their own
// items for the 2 x 2 cards and for the half-height ones (OnlineMenus.Seats8.cs), the same calls (+ the card).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // our callbacks (2810-2839, after P8's TEAM boxes 2800-2807)
        private const int OnLbLevelCall = 2810, OnLbRemoveCall = 2820, OnLbAddCall = 2830; // + the card
        private const int LobbyBotsFirstCall = OnLbLevelCall, LobbyBotsLastCall = OnLbAddCall + LobbySeats8 - 1;

        private readonly int[] _lbLevel = Unset(LobbySeats), _lbRemove = Unset(LobbySeats), _lbAdd = Unset(LobbySeats);
        private readonly int[] _lbLevel8 = Unset(LobbySeats8), _lbRemove8 = Unset(LobbySeats8), _lbAdd8 = Unset(LobbySeats8);

        // the boxes (mockup px from the card's corner): 2 x 2 cards (610 x 330) and half-height ones (610 x 152)
        private static (float X0, float Top, float X1, float Bottom) BotBox(int which, int k, bool eight)
        {
            (float cx, float cy) = LobbyCardCorner(k, eight ? LobbySeats8 : LobbySeats);
            (float x, float y, float w, float h) = (which, eight) switch
            {
                (0, false) => (240f, 186f, 170f, 52f), // LEVEL
                (1, false) => (430f, 150f, 160f, 52f), // REMOVE
                (2, false) => (240f, 236f, 200f, 56f), // ADD BOT
                (0, true) => (165f, 100f, 150f, 44f),
                (1, true) => (430f, 52f, 160f, 44f),
                _ => (325f, 90f, 200f, 46f),
            };
            return (Mx(cx + x), My(cy + y), Mx(cx + x + w), My(cy + y + h));
        }

        // InstallOnline, after the TEAM boxes (taps hit them before the cards)
        private void InstallLobbyBots(MenuPage lobby, Func<int, float, float, float, float, int, MenuItem> input)
        {
            void Add(int[] items, int which, bool eight, int call)
            {
                for (int k = 0; k < items.Length; k++)
                {
                    (float x0, float top, float x1, float bottom) = BotBox(which, k, eight);
                    int seat = k;
                    items[k] = lobby.AddItem(i => input(i, x0, top, x1, bottom, call + seat)).Index;
                }
            }
            Add(_lbLevel, 0, false, OnLbLevelCall);
            Add(_lbRemove, 1, false, OnLbRemoveCall);
            Add(_lbAdd, 2, false, OnLbAddCall);
            Add(_lbLevel8, 0, true, OnLbLevelCall);
            Add(_lbRemove8, 1, true, OnLbRemoveCall);
            Add(_lbAdd8, 2, true, OnLbAddCall);
        }

        private void ApplyLobbyBotsSounds(Action<int, int, int, int> like)
        {
            foreach (int[] items in new[] { _lbLevel, _lbRemove, _lbAdd, _lbLevel8, _lbRemove8, _lbAdd8 })
                foreach (int item in items) like(s_onLobby, item, OptionBoxPage, _soundToggle);
        }

        // seats the host's REMOVE freed (shown open, for ADD BOT; ADD BOT takes one back), for the game hosted now
        private OnlineMatch? _freedFor;
        private int _freed;

        private int FreedSeats(OnlineMatch m)
        {
            if (m != _hosting) return 0;
            if (_freedFor != m)
            {
                _freedFor = m;
                _freed = 0;
            }
            return _freed;
        }

        private bool CanAddBot(List<LobbySeat> seats) => _hosting?.Net.Settings is MatchSettings s && s.Players.Count < MaxSeats
            && seats.Count < MaxSeats;

        // ShowLobby: the boxes that apply (the host's bots and open seats; the other set of cards' boxes hidden)
        private void ShowLobbyBots(MenuEngine menu, List<LobbySeat> seats)
        {
            bool host = _hosting != null, eight = LobbyEight(seats.Count), add = CanAddBot(seats);
            for (int k = 0; k < LobbySeats8; k++)
            {
                LobbySeat? seat = k < seats.Count ? seats[k] : null;
                bool bot = host && seat?.Kind == SeatKind.Bot && seat.Index >= 0, open = host && add && seat?.Kind == SeatKind.Open;
                if (k < LobbySeats)
                {
                    Show(menu, _lbLevel[k], !eight && bot);
                    Show(menu, _lbRemove[k], !eight && bot);
                    Show(menu, _lbAdd[k], !eight && open);
                }
                Show(menu, _lbLevel8[k], eight && bot);
                Show(menu, _lbRemove8[k], eight && bot);
                Show(menu, _lbAdd8[k], eight && open);
            }
        }

        // a TEAM box shows for the host on every seat in the match, for a guest on its own
        private bool TeamBoxShown(LobbySeat seat, bool host) => seat.Index >= 0 && (host || seat.Kind == SeatKind.You);

        private void LobbyBotsCall(int call)
        {
            List<LobbySeat> seats = SeatsNow();
            int k = (call - OnLbLevelCall) % 10;
            if (_hosting is not OnlineMatch hm || k >= seats.Count) return;
            LobbySeat seat = seats[k];
            switch (call)
            {
            case >= OnLbLevelCall and < OnLbLevelCall + LobbySeats8:
                if (seat is { Kind: SeatKind.Bot, Player: MatchPlayer lp }) hm.Net.ReplaceBot(seat.Index, MultiplayerMenu.OnlineBot(lp.Hunter, (lp.BotLevel + 1) % 3));
                break;
            case >= OnLbRemoveCall and < OnLbRemoveCall + LobbySeats8:
                if (seat.Kind == SeatKind.Bot)
                {
                    FreedSeats(hm);
                    _freed++;
                    hm.Net.RemoveBot(seat.Index);
                }
                break;
            default:
                if (seat.Kind == SeatKind.Open && CanAddBot(seats))
                {
                    if (FreedSeats(hm) > 0) _freed--;
                    hm.Net.AddBot(MultiplayerMenu.OnlineBot(NextBotHunter(hm.Net.Settings!, null), s_botLevel));
                }
                break;
            }
        }

        // a tap on the host's bot card: the next hunter (one no other seat plays, if there is one)
        private void StepBotHunter(LobbySeat seat)
        {
            if (_hosting is not OnlineMatch hm || seat is not { Kind: SeatKind.Bot, Player: MatchPlayer p } || hm.Net.Settings is not MatchSettings s) return;
            hm.Net.ReplaceBot(seat.Index, MultiplayerMenu.OnlineBot(NextBotHunter(s, p.Hunter), p.BotLevel));
        }

        // after `from` (null: from the start) in BotHunters, the first not on a seat; all taken: simply the next
        private static Hunter NextBotHunter(MatchSettings s, Hunter? from)
        {
            int start = from is Hunter h ? Array.IndexOf(BotHunters, h) : -1;
            for (int i = 1; i <= BotHunters.Length; i++)
            {
                Hunter next = BotHunters[(start + i + BotHunters.Length) % BotHunters.Length];
                if (!s.Players.Exists(x => x.Hunter == next)) return next;
            }
            return BotHunters[(start + 1 + BotHunters.Length) % BotHunters.Length];
        }

        // DrawLobby, a host's bot card: LEVEL and REMOVE (the card's tap is the hunter)
        private void DrawBotBoxes(int k, bool eight, MatchPlayer bot, Dictionary<int, List<WidgetTri>> glows)
        {
            int level = eight ? _lbLevel8[k] : _lbLevel[k], remove = eight ? _lbRemove8[k] : _lbRemove[k];
            (float lx0, float ltop, float lx1, float lbottom) = BotBox(0, k, eight);
            Bar(lx0, ltop, lx1, lbottom, level, glows);
            TextS($"level {bot.BotLevel + 1}", (lx0 + lx1) / 2, ltop - (ltop - lbottom - 8 * SizeX) / 2, 2, Orange, level, SizeX);
            (float rx0, float rtop, float rx1, float rbottom) = BotBox(1, k, eight);
            Bar(rx0, rtop, rx1, rbottom, remove, glows);
            TextS("remove", (rx0 + rx1) / 2, rtop - (rtop - rbottom - 8 * SizeX) / 2, 2, Red, remove, SizeX);
        }

        // DrawLobby, a host's open seat: ADD BOT
        private void DrawAddBot(int k, bool eight, Dictionary<int, List<WidgetTri>> glows)
        {
            int add = eight ? _lbAdd8[k] : _lbAdd[k];
            (float x0, float top, float x1, float bottom) = BotBox(2, k, eight);
            Bar(x0, top, x1, bottom, add, glows);
            TextS("add bot", (x0 + x1) / 2, top - (top - bottom - 8 * SizeX) / 2, 2, Orange, add, SizeX);
        }
    }
}
