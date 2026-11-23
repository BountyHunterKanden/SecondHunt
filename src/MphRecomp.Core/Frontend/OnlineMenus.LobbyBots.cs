using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

// ONLINE PLAY's lobby controls (release 0.2.0 F2, owner 2026-10-11: "a GUEST can't change their own team colour (host can);
// the host can't remove or change bots"). Rebuilt to the owner's design of 2026-11-22 (Wave 1a):
//  - the most bots is 8 minus PLAYERS: a bot never takes a person's seat and ADD BOT never changes PLAYERS (only EDIT LOBBY's
//    WHO CAN JOIN does). ("now you can just fill the lobby with bots": the take-a-seat path of that morning is gone)
//  - one ADD BOT button over the lobby's corner ("like in LAN"), not a box in each open seat; with no room it stays, greyed,
//    and a tap says why. The seats: the people, their open seats, then the bots (a bot's seat is there only with its bot)
//  - a host's bot card: arrows either side of its picture step its hunter in a fixed order, the same hunter as another seat
//    allowed ("arrows on either side like in LAN"; the tap that skipped taken hunters looked like bots swapping); a tap on
//    the card opens SELECT HUNTER on its own for the bot (MultiplayerMenu.BeginBotPick: hunters, extra characters, skins);
//    LEVEL steps its level (1-3), REMOVE takes it out.
// A guest's TEAM box on its own card asks the host for the other team (NetMatchSession.RequestTeam, LOOK's optional tail); the
// host's TEAM boxes swap any seat as before. The boxes are their own items for the 2 x 2 cards and for the half-height ones
// (OnlineMenus.Seats8.cs), the same calls (+ the card).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // our callbacks (2810-2830 after P8's TEAM boxes 2800-2807; the arrows 2880-2897)
        private const int OnLbLevelCall = 2810, OnLbRemoveCall = 2820, OnLbAddCall = 2830; // LEVEL / REMOVE + the card; ADD BOT
        private const int OnLbLeftCall = 2880, OnLbRightCall = 2890; // + the card

        private static bool IsLobbyBotsCall(int call) => call is >= OnLbLevelCall and <= OnLbAddCall or >= OnLbLeftCall and < OnLbRightCall + LobbySeats8;

        private readonly int[] _lbLevel = Unset(LobbySeats), _lbRemove = Unset(LobbySeats), _lbLeft = Unset(LobbySeats), _lbRight = Unset(LobbySeats);
        private readonly int[] _lbLevel8 = Unset(LobbySeats8), _lbRemove8 = Unset(LobbySeats8), _lbLeft8 = Unset(LobbySeats8), _lbRight8 = Unset(LobbySeats8);
        private int _lbAddBot = -1;

        // ADD BOT: over the seat panel's top right corner (mockup px)
        private const float LbAddX0 = 1150, LbAddY0 = 116, LbAddX1 = 1390, LbAddY1 = 172;

        // a host's bot card (mockup px from the card's corner): its picture, smaller than a person's for the arrows beside it,
        // and where its name and hunter start
        private static (float X, float Y, float D, float TextX) BotCard(bool eight) => eight ? (50f, 26f, 100f, 205f) : (62f, 90f, 150f, 280f);

        // the boxes (mockup px from the card's corner): 2 x 2 cards (610 x 330) and half-height ones (610 x 152)
        private static (float X0, float Top, float X1, float Bottom) BotBox(int which, int k, bool eight)
        {
            (float cx, float cy) = LobbyCardCorner(k, eight ? LobbySeats8 : LobbySeats);
            (float x, float y, float w, float h) = (which, eight) switch
            {
                (0, false) => (280f, 186f, 170f, 52f), // LEVEL
                (1, false) => (460f, 186f, 130f, 52f), // REMOVE
                (2, false) => (8f, 128f, 48f, 74f), // the left arrow
                (3, false) => (218f, 128f, 48f, 74f), // the right arrow
                (0, true) => (205f, 100f, 150f, 44f),
                (1, true) => (430f, 52f, 160f, 44f),
                (2, true) => (6f, 46f, 40f, 60f),
                _ => (154f, 46f, 40f, 60f),
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
            Add(_lbLeft, 2, false, OnLbLeftCall);
            Add(_lbRight, 3, false, OnLbRightCall);
            Add(_lbLevel8, 0, true, OnLbLevelCall);
            Add(_lbRemove8, 1, true, OnLbRemoveCall);
            Add(_lbLeft8, 2, true, OnLbLeftCall);
            Add(_lbRight8, 3, true, OnLbRightCall);
            _lbAddBot = lobby.AddItem(i => input(i, Mx(LbAddX0), My(LbAddY0), Mx(LbAddX1), My(LbAddY1), OnLbAddCall)).Index;
        }

        private void ApplyLobbyBotsSounds(Action<int, int, int, int> like)
        {
            foreach (int[] items in new[] { _lbLevel, _lbRemove, _lbLeft, _lbRight, _lbLevel8, _lbRemove8, _lbLeft8, _lbRight8 })
                foreach (int item in items) like(s_onLobby, item, OptionBoxPage, _soundToggle);
            like(s_onLobby, _lbAddBot, OptionBoxPage, _soundToggle);
        }

        // room for one more bot: 8 seats less the people's (PLAYERS, or the people here if they are more). Counted from the
        // people's seats and the bots, not the seats drawn (owner 2026-11-22: ADD BOT works again after REMOVE)
        private bool CanAddBot(List<LobbySeat> seats) => NoBotReason(seats) == null;

        // why ADD BOT can't add one now (its greyed button's tap says it); null: it can
        private string? NoBotReason(List<LobbySeat> seats)
        {
            if (Runner is not OnlineMatch m || m.Net.Settings is not MatchSettings s) return "this game isn't ready yet.\n\ntry again in a moment.";
            int people = seats.Count(x => x.Kind is SeatKind.You or SeatKind.Person), bots = seats.Count(x => x.Kind == SeatKind.Bot);
            int cap = Math.Max(people, PeopleCap(m));
            if (cap + bots < MaxSeats && s.Players.Count < MaxSeats) return null;
            // (short lines: the notice box wraps at about 24 letters)
            string how = !LobbyEditable(m) ? (bots > 0 ? "remove a bot first." : "")
                : bots > 0 ? "lower PLAYERS in\nedit lobby, or\nremove a bot." : "lower PLAYERS in\nedit lobby.";
            string why = bots == 0 ? $"no room for bots.\n\nall {MaxSeats} seats are for\nplayers."
                : $"no room for more bots.\n\n{cap} player seats + {bots} bot{(bots == 1 ? "" : "s")}\nfill the lobby.";
            return how.Length > 0 ? why + "\n" + how : why;
        }

        // ShowLobby: the boxes that apply (the host's bot cards; the other set of cards' boxes hidden) and its ADD BOT
        private void ShowLobbyBots(MenuEngine menu, List<LobbySeat> seats)
        {
            bool host = Runner != null, eight = LobbyEight(seats.Count);
            for (int k = 0; k < LobbySeats8; k++)
            {
                LobbySeat? seat = k < seats.Count ? seats[k] : null;
                bool bot = host && seat?.Kind == SeatKind.Bot && seat.Index >= 0;
                if (k < LobbySeats)
                {
                    Show(menu, _lbLevel[k], !eight && bot);
                    Show(menu, _lbRemove[k], !eight && bot);
                    Show(menu, _lbLeft[k], !eight && bot);
                    Show(menu, _lbRight[k], !eight && bot);
                }
                Show(menu, _lbLevel8[k], eight && bot);
                Show(menu, _lbRemove8[k], eight && bot);
                Show(menu, _lbLeft8[k], eight && bot);
                Show(menu, _lbRight8[k], eight && bot);
            }
            Show(menu, _lbAddBot, host);
        }

        // a TEAM box shows for the host on every seat in the match, for a guest on its own
        private bool TeamBoxShown(LobbySeat seat, bool host) => seat.Index >= 0 && (host || seat.Kind == SeatKind.You);

        private void LobbyBotsCall(MenuEngine menu, int call)
        {
            List<LobbySeat> seats = SeatsNow();
            if (Runner is not OnlineMatch hm) return;
            if (call == OnLbAddCall)
            {
                // the next bot: after the people's seats (the smaller team, NetMatchSession.AddBot)
                if (NoBotReason(seats) is string why) Import.Popup?.Inform(why);
                else hm.Net.AddBot(NextBot(hm.Net.Settings!, null, s_botLevel));
                return;
            }
            int k = call % 10;
            if (k >= seats.Count || seats[k] is not { Kind: SeatKind.Bot, Player: MatchPlayer bot } seat || seat.Index < 0) return;
            switch (call)
            {
            case >= OnLbLevelCall and < OnLbLevelCall + LobbySeats8:
                hm.Net.ReplaceBot(seat.Index, MultiplayerMenu.OnlineBotLike(bot, (bot.BotLevel + 1) % 3));
                break;
            case >= OnLbRemoveCall and < OnLbRemoveCall + LobbySeats8:
                hm.Net.RemoveBot(seat.Index);
                break;
            case >= OnLbLeftCall and < OnLbLeftCall + LobbySeats8:
                StepBotHunter(hm, seat, -1);
                break;
            case >= OnLbRightCall and < OnLbRightCall + LobbySeats8:
                StepBotHunter(hm, seat, 1);
                break;
            }
        }

        // the hunters a bot's arrows step through, in this order: BotHunters, then the TROOPER and DARK SAMUS where the game
        // allows them (MultiplayerMenu.OnlineBotExtras; recomp, bug-fix batch A5, 2026-10-23)
        private static List<(Hunter Hunter, Character? C)> BotPicks(MatchSettings s) => BotHunters.Select(h => (h, (Character?)null))
            .Concat(MultiplayerMenu.OnlineBotExtras(s).Select(c => (c.Base, (Character?)c))).ToList();

        // a bot card's arrow: the hunter before / after its own in BotPicks, whoever else plays it (owner 2026-11-22: "any
        // hunter in order", duplicates allowed); its level stays, a skin goes back to the hunter's first
        private void StepBotHunter(OnlineMatch hm, LobbySeat seat, int step)
        {
            if (seat.Player is not MatchPlayer p || hm.Net.Settings is not MatchSettings s) return;
            List<(Hunter Hunter, Character? C)> picks = BotPicks(s);
            int at = picks.FindIndex(k => PickKey(k.Hunter, k.C) == PickKey(p));
            int next = at < 0 ? 0 : ((at + step) % picks.Count + picks.Count) % picks.Count;
            hm.Net.ReplaceBot(seat.Index, MakeBot(picks[next], p.BotLevel));
        }

        // a tap on the host's bot card (its picture): SELECT HUNTER on its own for that bot, with what a player gets there (the
        // hunters this device has open, the extra characters where the game allows them, the skins); the pick comes back here
        private void PickBotHunter(MenuEngine menu, LobbySeat seat)
        {
            if (Runner is not OnlineMatch hm || Multiplayer == null || seat is not { Kind: SeatKind.Bot, Player: MatchPlayer p } || seat.Index < 0
                || hm.Net.Settings is not MatchSettings s) return;
            int index = seat.Index;
            string key = PickKey(p);
            bool extras = s.PirateBlaster != false && s.ExtraCharacters != false;
            Multiplayer.BeginBotPick(menu, s_onLobby, p, extras, (hunter, skin, character) =>
            {
                if (Runner != hm) return;
                // the bot it was opened for: on its seat still, else (someone joined or left meanwhile) the same pick's
                List<LobbySeat> now = SeatsNow();
                LobbySeat? to = now.FirstOrDefault(x => x.Kind == SeatKind.Bot && x.Index == index && x.Player is MatchPlayer q && PickKey(q) == key)
                    ?? now.FirstOrDefault(x => x.Kind == SeatKind.Bot && x.Index >= 0 && x.Player is MatchPlayer q && PickKey(q) == key);
                if (to?.Player is not MatchPlayer old) return;
                hm.Net.ReplaceBot(to.Index, MultiplayerMenu.OnlineBot(hunter, skin, character, old.BotLevel));
            });
        }

        // a new bot's hunter (ADD BOT, WHO CAN JOIN's BOTS): after `from` (null: from the start) in BotPicks, the first not
        // on a seat; all taken: simply the next
        private static MatchPlayer NextBot(MatchSettings s, MatchPlayer? from, int level)
        {
            List<(Hunter Hunter, Character? C)> picks = BotPicks(s);
            int start = from == null ? -1 : picks.FindIndex(k => PickKey(k.Hunter, k.C) == PickKey(from));
            for (int i = 1; i <= picks.Count; i++)
            {
                (Hunter Hunter, Character? C) next = picks[(start + i + picks.Count) % picks.Count];
                if (!s.Players.Exists(x => PickKey(x) == PickKey(next.Hunter, next.C))) return MakeBot(next, level);
            }
            return MakeBot(picks[(start + 1 + picks.Count) % picks.Count], level);
        }

        private static MatchPlayer MakeBot((Hunter Hunter, Character? C) pick, int level) =>
            pick.C is Character c ? MultiplayerMenu.OnlineBot(c, level) : MultiplayerMenu.OnlineBot(pick.Hunter, level);

        // a seat's pick: an extra character's id, else its hunter
        private static string PickKey(Hunter hunter, Character? c) => c?.Id ?? hunter.ToString();
        private static string PickKey(MatchPlayer p) =>
            p.Character != null && Characters.Of(p.Hunter, p.Skin) is { HasOriginal: false } c ? c.Id : p.Hunter.ToString();

        // DrawLobby, a host's bot card: the arrows beside its picture, LEVEL and REMOVE (the card's tap is the picker)
        private void DrawBotBoxes(int k, bool eight, MatchPlayer bot, Dictionary<int, List<WidgetTri>> glows)
        {
            int level = eight ? _lbLevel8[k] : _lbLevel[k], remove = eight ? _lbRemove8[k] : _lbRemove[k];
            (float lx0, float ltop, float lx1, float lbottom) = BotBox(0, k, eight);
            Bar(lx0, ltop, lx1, lbottom, level, glows);
            TextS($"level {bot.BotLevel + 1}", (lx0 + lx1) / 2, ltop - (ltop - lbottom - 8 * SizeX) / 2, 2, Orange, level, SizeX);
            (float rx0, float rtop, float rx1, float rbottom) = BotBox(1, k, eight);
            Bar(rx0, rtop, rx1, rbottom, remove, glows);
            TextS("remove", (rx0 + rx1) / 2, rtop - (rtop - rbottom - 8 * SizeX) / 2, 2, Red, remove, SizeX);
            for (int side = 0; side < 2; side++)
            {
                int item = side == 0 ? (eight ? _lbLeft8[k] : _lbLeft[k]) : eight ? _lbRight8[k] : _lbRight[k];
                (float x0, float top, float x1, float bottom) = BotBox(2 + side, k, eight);
                Glow(glows, item, x0, top, x1, bottom, item);
                Arrow(side == 1, (x0 + x1 - ArrowW) / 2, (top + bottom + ArrowH) / 2, item);
            }
        }

        // DrawLobby, the host's: ADD BOT over the panel's corner, greyed while no bot fits (its tap then says why)
        private void DrawAddBot(List<LobbySeat> seats, Dictionary<int, List<WidgetTri>> glows)
        {
            if (_lbAddBot < 0) return;
            float x0 = Mx(LbAddX0), top = My(LbAddY0), x1 = Mx(LbAddX1), bottom = My(LbAddY1);
            bool can = CanAddBot(seats);
            Bar(x0, top, x1, bottom, _lbAddBot, glows);
            TextS("add bot", (x0 + x1) / 2, top - (top - bottom - 8 * SizeX) / 2, 2, can ? Orange : OnGrey, _lbAddBot, SizeX, can ? 1 : 0.5f);
        }
    }
}
