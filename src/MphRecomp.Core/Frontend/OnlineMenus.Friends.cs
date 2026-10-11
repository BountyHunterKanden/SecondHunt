using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Online;

// ONLINE PLAY's FRIENDS AND RIVALS and FIND A HUNTER pages (Online play menus thread, owner 2026-10-09; mockups F_A / F_B /
// F_C and A_A / A_B in extract_out/online_menus/mockups, "OK as shown"). The hub's ALL FRIENDS opens FRIENDS AND RIVALS:
// four tabs (FRIENDS, RIVALS, REQUESTS with the incoming count, RECENT), five rows a page (L / R or the title's arrows):
// the person disc, name#tag, where they are, their presence (online green, in lobby cyan, in match orange, offline dimmed),
// JOIN on someone in an open game; REQUESTS' incoming rows take OK / X, the sent ones CANCEL; RECENT's rows ADD FRIEND / ADD
// RIVAL (or the word when already linked, "sent" while waiting). Tapping a row opens their HUNTER LICENSE (OnlineMenus.License.cs:
// REMOVE, BLOCK and the rest are there). "your code" at the bottom, ADD (the person disc) opens FIND A HUNTER: SEARCH NAME (the name
// keyboard, then the server's search; a space and the number work as #tag, the keyboard has no #), FRIEND CODE (YOUR CODE /
// NEW CODE cards and the ROM keypad's keys: OK sends a friend request, which completes at once when they entered yours, as
// vanilla's both-enter-the-code), FROM RECENT. Everything goes through the hub's connection (OnlineClient requests off the
// menu thread, results through _posts); the server's pushes (requests, adds, removes, presence) refresh the lists.
// Not in the mockups' data: the server keeps no "last seen" or match result for RECENT, so those lines show presence.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private static int s_onFriends = -1, s_onAdd = -1;

        // our callbacks (2600-2674)
        private const int FrTabCall = 2600; // + the tab
        private const int FrAddCall = 2604, FrPrevCall = 2605, FrNextCall = 2606;
        private const int FrRowCall = 2610, FrWideCall = 2615, FrLeftCall = 2620, FrRightCall = 2625, FrOkCall = 2630, FrNoCall = 2635; // + the row
        private const int AdTabCall = 2640; // + the tab
        private const int AdFieldCall = 2643, AdSearchCall = 2644, AdOkCall = 2645, AdBackspaceCall = 2646, AdPrevCall = 2647, AdNextCall = 2648;
        private const int AdKeyCall = 2650; // + the digit
        private const int AdRowCall = 2660, AdLeftCall = 2665, AdRightCall = 2670; // + the row
        private const int FriendsFirstCall = FrTabCall, FriendsLastCall = AdRightCall + 4;
        private const int FrRows = 5, FrTabs = 4, AdRows = 3, AdTabs = 3;
        private const int CodeDigits = 12, SearchMax = 15; // a name (10) and " 1234"

        // items (-1 = none)
        private int _frPanel = -1, _frTitle = -1, _frAdd = -1, _frPrev = -1, _frNext = -1, _frTitleString = -1;
        private readonly int[] _frTab = Unset(FrTabs), _frRow = Unset(FrRows), _frWide = Unset(FrRows), _frLeft = Unset(FrRows),
            _frRight = Unset(FrRows), _frOk = Unset(FrRows), _frNo = Unset(FrRows);
        private int _adPanel = -1, _adTitle = -1, _adField = -1, _adSearch = -1, _adOk = -1, _adBackspace = -1, _adPrev = -1, _adNext = -1,
            _adTitleString = -1;
        private readonly int[] _adTab = Unset(AdTabs), _adKey = Unset(10), _adRow = Unset(AdRows), _adLeft = Unset(AdRows), _adRight = Unset(AdRows);
        private string? _numKey, _numBack; // the ROM keypad's key models (ENTER NEW CODE, page 61)

        // ---- state (menu thread) ----
        private RosterMsg? _roster; // friends, rivals, requests (null = not known yet)
        private List<PlayerCard> _recent = new();
        private static int s_frTab, s_adTab;
        private int _frOffset, _adOffset;
        private string _searchText = "";
        private List<PlayerCard>? _found; // the last search's hunters (null = no search yet)
        private bool _searching, _typingName;
        private string _codeTyped = "";
        private string? _codeName; // the typed code's hunter (null = not looked up, "" = nobody has it)

        private static int[] Unset(int n) => Enumerable.Repeat(-1, n).ToArray();

        // tools (MphRead.Tools -onlinemenu / -onlinefriendstest)
        internal static (int Friends, int Add) OnlineFriendsPages => (s_onFriends, s_onAdd);
        internal RosterMsg? OnlineRoster => _roster;
        internal IReadOnlyList<PlayerCard>? OnlineFound => _found;
        internal string? OnlineCodeName => _codeName;
        internal PlayerCard? OnlineMeCard => MeCard;

        internal void OnlinePreviewFriends(RosterMsg roster, List<PlayerCard> recent, List<PlayerCard>? found, string search, string typed)
        {
            _roster = roster;
            _recent = recent;
            _found = found;
            _searchText = search;
            _codeTyped = typed;
            Redraw();
        }

        // the search the name keyboard would run (tools: no keyboard)
        internal void OnlineSearch(string text)
        {
            _searchText = text;
            RunSearch();
        }

        private void InstallFriends(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, float, float, int, MenuItem> touch, Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back,
            MenuItem panel, MenuItem title)
        {
            string? ModelOf(string name) => _file.Widgets.Select(w => w.ModelPath)
                .FirstOrDefault(p => p.EndsWith("\\" + name + "_Model.bin", StringComparison.OrdinalIgnoreCase)) is string p ? p[..^"_Model.bin".Length] : null;
            _numKey = ModelOf("numkey");
            _numBack = ModelOf("numback");
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));

            // ---- FRIENDS AND RIVALS (F_A / F_B / F_C): the title higher, the panel lower (the tabs between) ----
            MenuPage friends = addFramePage(s_onHub, new[] { Call(MenuKeys.R, FrNextCall), Call(MenuKeys.L, FrPrevCall) });
            s_onFriends = friends.Index;
            _frPanel = friends.AddItem(i => Clone(panel, i, (Mx(70) + Mx(1850)) / 2 - PanelMid, -Mw(54), stretchX: Mx(1850) - Mx(70) - PanelWidth,
                stretchDown: PanelStretchDown - Mw(54))).Index;
            back(friends, s_onHub);
            _frTitleString = _strings.Add(FriendsTitle);
            _frTitle = friends.AddItem(i => Clone(title, i, 0, Mw(42), stringId: _frTitleString)).Index;
            for (int t = 0; t < FrTabs; t++)
            {
                (float tx, float tw) = FrTabSpot(t);
                _frTab[t] = Px(friends, tx, 140, tw, 70, FrTabCall + t).Index;
            }
            for (int r = 0; r < FrRows; r++) _frRow[r] = Px(friends, 100, FriendRowY(r), 1720, 110, FrRowCall + r).Index;
            // the row buttons after the rows (taps hit them first); a row shows at most two of them
            for (int r = 0; r < FrRows; r++)
            {
                float by = FriendRowY(r);
                _frWide[r] = Px(friends, 1520, by + 24, 270, 62, FrWideCall + r).Index;
                _frLeft[r] = Px(friends, 1300, by + 24, 240, 62, FrLeftCall + r).Index;
                _frRight[r] = Px(friends, 1560, by + 24, 240, 62, FrRightCall + r).Index;
                _frOk[r] = Px(friends, 1560, by + 8, 94, 94, FrOkCall + r).Index;
                _frNo[r] = Px(friends, 1680, by + 8, 94, 94, FrNoCall + r).Index;
            }
            _frAdd = Px(friends, 1736, 918, 108, 140, FrAddCall).Index;
            float ay = ArrowTop + Mw(42);
            _frPrev = friends.AddItem(i => touch(i, 128 - ArrowGap - ArrowW - 60, ay + 4, 128 - ArrowGap - 14, ay - ArrowH - 4, FrPrevCall)).Index;
            _frNext = friends.AddItem(i => touch(i, 128 + ArrowGap + 14, ay + 4, 128 + ArrowGap + ArrowW + 60, ay - ArrowH - 4, FrNextCall)).Index;

            // ---- FIND A HUNTER (A_A / A_B, and FROM RECENT) ----
            MenuPage add = addFramePage(friends.Index, new[] { Call(MenuKeys.R, AdNextCall), Call(MenuKeys.L, AdPrevCall) });
            s_onAdd = add.Index;
            _adPanel = add.AddItem(i => panelAt(i, Mx(70), Mx(1850))).Index;
            back(add, friends.Index);
            _adTitleString = _strings.Add("FIND A HUNTER");
            _adTitle = add.AddItem(i => Clone(title, i, 0, 0, stringId: _adTitleString)).Index;
            for (int t = 0; t < AdTabs; t++) _adTab[t] = Px(add, 300 + t * 424, 222, 400, 66, AdTabCall + t).Index;
            _adField = Px(add, 330, 324, 900, 90, AdFieldCall).Index;
            _adSearch = Px(add, 1280, 330, 380, 80, AdSearchCall).Index;
            for (int r = 0; r < AdRows; r++) _adRow[r] = Px(add, 100, AddRowY(r), 1720, 110, AdRowCall + r).Index;
            for (int r = 0; r < AdRows; r++)
            {
                _adLeft[r] = Px(add, 1300, AddRowY(r) + 24, 240, 62, AdLeftCall + r).Index;
                _adRight[r] = Px(add, 1560, AddRowY(r) + 24, 240, 62, AdRightCall + r).Index;
            }
            for (int d = 0; d < 10; d++)
            {
                (float kx, float ky) = KeyAt(d);
                _adKey[d] = Px(add, kx, ky, 116, 116, AdKeyCall + d).Index;
            }
            (float bx, float bky) = KeyAt(10);
            _adBackspace = Px(add, bx, bky, 116, 116, AdBackspaceCall).Index;
            _adOk = Px(add, 1640, 616, 130, 130, AdOkCall).Index;
            _adPrev = add.AddItem(i => touch(i, 128 - ArrowGap - ArrowW - 60, ArrowTop + 4, 128 - ArrowGap - 14, ArrowTop - ArrowH - 4, AdPrevCall)).Index;
            _adNext = add.AddItem(i => touch(i, 128 + ArrowGap + 14, ArrowTop + 4, 128 + ArrowGap + ArrowW + 60, ArrowTop - ArrowH - 4, AdNextCall)).Index;
        }

        private static float FriendRowY(int row) => 272 + 126 * row;
        private static float AddRowY(int row) => 456 + 126 * row;

        // the keypad (mockup px): 7 8 9 / 4 5 6 / 1 2 3 / 0 . back; 10 = back
        private static (float X, float Y) KeyAt(int key)
        {
            (int row, int col) = key switch
            {
                0 => (3, 0),
                10 => (3, 2),
                _ => (2 - (key - 1) / 3, (key - 1) % 3),
            };
            return (1150 + col * 140, 336 + row * 140);
        }

        private void ApplyFriendsSounds(Action<int, int, int, int> like)
        {
            foreach (int t in _frTab) like(s_onFriends, t, OptionBoxPage, _soundToggle);
            foreach (int t in _adTab) like(s_onAdd, t, OptionBoxPage, _soundToggle);
            for (int r = 0; r < FrRows; r++)
            {
                like(s_onFriends, _frRow[r], MainMenuPage, _soundCampaign);
                like(s_onFriends, _frWide[r], FileSelect.Page, _soundFileStart);
                foreach (int b in new[] { _frLeft[r], _frRight[r], _frOk[r], _frNo[r] }) like(s_onFriends, b, OptionBoxPage, _soundToggle);
            }
            like(s_onFriends, _frAdd, MainMenuPage, _soundCampaign);
            for (int r = 0; r < AdRows; r++)
            {
                like(s_onAdd, _adRow[r], MainMenuPage, _soundCampaign);
                like(s_onAdd, _adLeft[r], OptionBoxPage, _soundToggle);
                like(s_onAdd, _adRight[r], OptionBoxPage, _soundToggle);
            }
            foreach (int k in _adKey.Append(_adBackspace).Append(_adField)) like(s_onAdd, k, OptionBoxPage, _soundToggle);
            like(s_onAdd, _adSearch, FileSelect.Page, _soundFileStart);
            like(s_onAdd, _adOk, FileSelect.Page, _soundFileStart);
        }

        // ---- the rows ----

        private enum RowButtons { None, Join, Cancel, AddBoth, Answer }

        // (rivals hidden, ShowRivals: the page is FRIENDS, three tabs FRIENDS / REQUESTS / RECENT, wider, the same ids)
        private const string FriendsTitle = ShowRivals ? "FRIENDS AND RIVALS" : "FRIENDS";
        private static readonly int[] FrTabsShown = ShowRivals ? new[] { 0, 1, 2, 3 } : new[] { 0, 2, 3 };

        // a tab's x and width (mockup px); a hidden tab gets the first spot (its item is hidden)
        private static (float X, float W) FrTabSpot(int tab)
        {
            int slot = Math.Max(0, Array.IndexOf(FrTabsShown, tab));
            return FrTabsShown.Length == 4 ? (130 + slot * 354, 330) : (130 + slot * 472, 448);
        }

        private sealed record PlayerRow(PlayerCard Card, string Line, (float R, float G, float B) LineColour, string? State,
            (float R, float G, float B) StateColour, bool Dim, string? Badge, RowButtons Buttons, string Kind = LinkKind.Friend);

        private int Incoming => Requests().Count(q => q.Incoming);

        // the requests shown (rival requests hidden with the rivals; the server keeps them)
        private IEnumerable<RequestInfo> Requests() => (_roster?.Requests ?? new List<RequestInfo>()).Where(q => ShowRivals || q.Kind != LinkKind.Rival);

        private List<PlayerRow> FriendRows()
        {
            RosterMsg r = _roster ?? new RosterMsg();
            return s_frTab switch
            {
                0 => Ordered(r.Friends).Select(PresenceRow).ToList(),
                1 => Ordered(r.Rivals).Select(PresenceRow).ToList(),
                2 => Requests().OrderBy(q => q.Incoming ? 0 : 1).Select(q => q.Incoming
                    ? new PlayerRow(q.Player, $"wants to be your {q.Kind}", Pale, null, White, false, q.Kind, RowButtons.Answer, q.Kind)
                    : new PlayerRow(q.Player, $"{q.Kind} request sent - waiting", OnGrey, null, White, false, q.Kind, RowButtons.Cancel, q.Kind)).ToList(),
                _ => _recent.Select(c => LinkRow(c, PresenceLine(c))).ToList(),
            };
        }

        private static IEnumerable<PlayerCard> Ordered(IEnumerable<PlayerCard> cards) => cards
            .OrderBy(c => c.Presence switch { Presence.Lobby => 0, Presence.Match => 1, Presence.Online => 2, _ => 3 })
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        private PlayerRow PresenceRow(PlayerCard c)
        {
            (string state, (float, float, float) colour) = c.Presence switch
            {
                Presence.Lobby => ("in lobby", Cyan),
                Presence.Match => ("in match", Orange),
                Presence.Online => ("online", Green),
                _ => ("offline", OnGrey),
            };
            bool joinable = c.Presence == Presence.Lobby && c.GameId != null;
            string? block = joinable ? JoinBlock(c.GameId) : null; // (JOIN greyed, the reason as the line: release 0.2.0 F2)
            return new PlayerRow(c, block != null ? "can't join: " + block : PresenceLine(c), block != null ? OnGrey : Pale, state, colour,
                c.Presence == Presence.Offline, null,
                joinable ? RowButtons.Join : RowButtons.None);
        }

        // RECENT and search results: the ADD FRIEND / ADD RIVAL slots
        private PlayerRow LinkRow(PlayerCard c, string line)
        {
            bool friend = Linked(c, LinkKind.Friend), rival = Linked(c, LinkKind.Rival);
            return new PlayerRow(c, line, Pale, null, White, false, friend ? LinkKind.Friend : rival && ShowRivals ? LinkKind.Rival : null, RowButtons.AddBoth);
        }

        // where they are: the game they're in (when it's listed), else their presence
        private string PresenceLine(PlayerCard c)
        {
            GameCard? g = c.GameId == null ? null : _games.FirstOrDefault(x => x.Id == c.GameId);
            if (g != null) return $"{(ModeOf(g) is GameMode m ? ModeName(m) : g.Info.Mode.ToLowerInvariant())} - {ArenaLabel(g.Info.Arena)} {g.Players}/{g.Info.MaxPlayers}";
            return c.Presence switch { Presence.Lobby => "in a game lobby", Presence.Match => "playing a match", Presence.Online => "in menus", _ => "" };
        }

        // friend / rival already (the roster when it's known, else the card), or a request of ours waiting
        private bool Linked(PlayerCard c, string kind) => _roster == null ? kind == LinkKind.Friend ? c.Friend : c.Rival
            : (kind == LinkKind.Friend ? _roster.Friends : _roster.Rivals).Any(x => x.Code == c.Code);

        private bool Sent(PlayerCard c, string kind) => _roster?.Requests.Any(q => !q.Incoming && q.Kind == kind && q.Player.Code == c.Code) ?? false;

        private static int Pages(int count, int per) => Math.Max(1, (count + per - 1) / per);

        // ---- FRIENDS AND RIVALS ----

        // focus: the row to focus (-1 = keep what's focused)
        private void ShowFriends(MenuEngine menu, int focus)
        {
            List<PlayerRow> rows = FriendRows();
            if (_frOffset >= rows.Count) _frOffset = 0;
            int count = Math.Clamp(rows.Count - _frOffset, 0, FrRows);
            for (int r = 0; r < FrRows; r++)
            {
                PlayerRow? row = r < count ? rows[_frOffset + r] : null;
                Show(menu, _frRow[r], row != null);
                if (row != null && focus >= 0) menu.SetState(_frRow[r], r == Math.Min(focus, count - 1) ? MenuState.Focused : MenuState.Idle);
                Show(menu, _frWide[r], row?.Buttons is RowButtons.Join or RowButtons.Cancel);
                Show(menu, _frLeft[r], row?.Buttons == RowButtons.AddBoth && Addable(row.Card, LinkKind.Friend));
                Show(menu, _frRight[r], row?.Buttons == RowButtons.AddBoth && Addable(row.Card, LinkKind.Rival));
                Show(menu, _frOk[r], row?.Buttons == RowButtons.Answer);
                Show(menu, _frNo[r], row?.Buttons == RowButtons.Answer);
            }
            for (int t = 0; t < FrTabs; t++) Show(menu, _frTab[t], FrTabsShown.Contains(t));
            if (count == 0 && focus >= 0) menu.SetState(_frTab[s_frTab], MenuState.Focused);
            int pages = Pages(rows.Count, FrRows);
            _strings.Set(_frTitleString, pages > 1 ? $"{FriendsTitle} {_frOffset / FrRows + 1}/{pages}" : FriendsTitle);
        }

        // (friend OR rival, vanilla: no ADD RIVAL for a friend; ADD FRIEND on a rival upgrades them)
        private bool Addable(PlayerCard c, string kind) => !Linked(c, kind) && !Sent(c, kind)
            && !(kind == LinkKind.Rival && (!ShowRivals || Linked(c, LinkKind.Friend)));

        private static void Show(MenuEngine menu, int item, bool on)
        {
            if (item < 0) return;
            if (!on) menu.SetState(item, MenuState.Hidden);
            else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
        }

        private void FriendsCall(MenuEngine menu, int call)
        {
            if (call >= AdTabCall)
            {
                AddCall(menu, call);
                return;
            }
            List<PlayerRow> rows = FriendRows();
            PlayerRow? RowOf(int first)
            {
                int at = _frOffset + call - first;
                return at < rows.Count ? rows[at] : null;
            }
            switch (call)
            {
            case >= FrTabCall and < FrTabCall + FrTabs:
                s_frTab = call - FrTabCall;
                _frOffset = 0;
                ShowFriends(menu, -1);
                break;
            case FrAddCall:
                menu.GoTo(s_onAdd);
                break;
            case FrPrevCall:
            case FrNextCall:
            {
                int pages = Pages(rows.Count, FrRows);
                if (pages > 1)
                {
                    _frOffset = (_frOffset / FrRows + (call == FrNextCall ? 1 : pages - 1)) % pages * FrRows;
                    ShowFriends(menu, 0);
                }
                break;
            }
            case >= FrRowCall and < FrRowCall + FrRows:
                if (RowOf(FrRowCall) is PlayerRow tapped) OpenLicense(menu, tapped.Card);
                break;
            case >= FrWideCall and < FrWideCall + FrRows:
                if (RowOf(FrWideCall) is not PlayerRow wide) break;
                if (wide.Buttons == RowButtons.Join) PickThenJoin(menu, wide.Card.GameId, OnlineNames.Display(wide.Card.Name, wide.Card.Tag));
                else if (wide.Buttons == RowButtons.Cancel) Act(new RemoveReq { Code = wide.Card.Code, Kind = wide.Kind }, _ => null);
                break;
            case >= FrLeftCall and < FrLeftCall + FrRows:
                if (RowOf(FrLeftCall) is PlayerRow left) AddLink(left.Card, LinkKind.Friend);
                break;
            case >= FrRightCall and < FrRightCall + FrRows:
                if (RowOf(FrRightCall) is PlayerRow right) AddLink(right.Card, LinkKind.Rival);
                break;
            case >= FrOkCall and < FrOkCall + FrRows:
                if (RowOf(FrOkCall) is PlayerRow yes) Answer(yes, true);
                break;
            case >= FrNoCall and < FrNoCall + FrRows:
                if (RowOf(FrNoCall) is PlayerRow no) Answer(no, false);
                break;
            }
        }

        private void Answer(PlayerRow row, bool accept)
        {
            string name = OnlineNames.Display(row.Card.Name, row.Card.Tag).ToLowerInvariant();
            Act(new AnswerReq { Code = row.Card.Code, Kind = row.Kind, Accept = accept },
                ok => accept ? $"you and {name} are {row.Kind}s now." : null);
        }

        private void AddLink(PlayerCard c, string kind)
        {
            string name = OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant();
            Act(new AddReq { Code = c.Code, Kind = kind }, ok => ok.Done ? $"you and {name} are {kind}s now."
                : $"{kind} request sent to {name}.\n\nthey'll see it in their requests.");
        }

        // a friends request on the connection there is (Conn: the hub's, or the lobby's game's); `said` turns the answer into a
        // message (null = none); then the lists (and an open license) again
        private void Act(OnlineMsg request, Func<OkMsg, string?> said)
        {
            OnlineClient? client = Conn;
            if (client == null || !client.Connected)
            {
                Import.Popup?.Inform(_connecting ? "connecting to the server..." : "can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
                if (!_connecting) RefreshOnline(lists: true);
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    OkMsg ok = await client.RequestAsync<OkMsg>(request);
                    Post(() =>
                    {
                        if (said(ok) is string text) Import.Popup?.Inform(text);
                        AfterAct();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        Import.Popup?.Inform("couldn't do that.\n\n" + Problem(ex));
                        AfterAct();
                    });
                }
            });
        }

        private void AfterAct()
        {
            RefreshOnline(lists: true);
            if (_onlineMenu?.Page?.Index == s_onLicense) FetchLicense();
        }

        // ---- FIND A HUNTER ----

        private List<PlayerRow> AddRows() => s_adTab switch
        {
            0 => (_found ?? new List<PlayerCard>()).Select(c => LinkRow(c, FoundLine(c))).ToList(),
            2 => _recent.Select(c => LinkRow(c, PresenceLine(c))).ToList(),
            _ => new List<PlayerRow>(),
        };

        private string FoundLine(PlayerCard c)
        {
            bool friend = Linked(c, LinkKind.Friend), rival = Linked(c, LinkKind.Rival);
            if (!ShowRivals) rival = false;
            return friend && rival ? "your friend and rival" : friend ? "already your friend" : rival ? "already your rival"
                : PresenceLine(c) is { Length: > 0 } where ? where : "offline";
        }

        private void ShowAdd(MenuEngine menu, int focus)
        {
            bool search = s_adTab == 0, code = s_adTab == 1;
            Show(menu, _adField, search);
            Show(menu, _adSearch, search);
            foreach (int k in _adKey.Append(_adBackspace).Append(_adOk)) Show(menu, k, code);
            List<PlayerRow> rows = AddRows();
            if (_adOffset >= rows.Count) _adOffset = 0;
            int count = Math.Clamp(rows.Count - _adOffset, 0, AdRows);
            for (int r = 0; r < AdRows; r++)
            {
                PlayerRow? row = r < count ? rows[_adOffset + r] : null;
                Show(menu, _adRow[r], row != null);
                if (row != null && focus >= 0) menu.SetState(_adRow[r], r == Math.Min(focus, count - 1) ? MenuState.Focused : MenuState.Idle);
                Show(menu, _adLeft[r], row != null && Addable(row.Card, LinkKind.Friend));
                Show(menu, _adRight[r], row != null && Addable(row.Card, LinkKind.Rival));
            }
            if (focus >= 0 && count == 0)
            {
                int first = search ? _adField : code ? _adKey[1] : _adTab[s_adTab];
                menu.SetState(first, MenuState.Focused);
            }
            int pages = Pages(rows.Count, AdRows);
            _strings.Set(_adTitleString, pages > 1 ? $"FIND A HUNTER {_adOffset / AdRows + 1}/{pages}" : "FIND A HUNTER");
        }

        private void AddCall(MenuEngine menu, int call)
        {
            List<PlayerRow> rows = AddRows();
            PlayerRow? RowOf(int first)
            {
                int at = _adOffset + call - first;
                return at < rows.Count ? rows[at] : null;
            }
            switch (call)
            {
            case >= AdTabCall and < AdTabCall + AdTabs:
                s_adTab = call - AdTabCall;
                _adOffset = 0;
                ShowAdd(menu, -1);
                break;
            case AdFieldCall:
                if (EditText == null)
                {
                    Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                    break;
                }
                _typingName = true; // (the connection stays up on the keyboard's page)
                EditText(menu, _searchText, SearchMax, s_onAdd, text =>
                {
                    if (text == null) return;
                    _searchText = text.Trim();
                    RunSearch();
                });
                break;
            case AdSearchCall:
                if (_searchText.Length == 0) goto case AdFieldCall;
                RunSearch();
                break;
            case AdPrevCall:
            case AdNextCall:
            {
                int pages = Pages(rows.Count, AdRows);
                if (pages > 1)
                {
                    _adOffset = (_adOffset / AdRows + (call == AdNextCall ? 1 : pages - 1)) % pages * AdRows;
                    ShowAdd(menu, 0);
                }
                break;
            }
            case >= AdKeyCall and < AdKeyCall + 10:
                if (_codeTyped.Length >= CodeDigits) break;
                _codeTyped += (char)('0' + call - AdKeyCall);
                if (_codeTyped.Length == CodeDigits) LookUpCode();
                break;
            case AdBackspaceCall:
                if (_codeTyped.Length > 0) _codeTyped = _codeTyped[..^1];
                _codeName = null;
                break;
            case AdOkCall:
                if (_codeTyped.Length < CodeDigits)
                {
                    Import.Popup?.Inform("enter all 12 numbers of their code.");
                    break;
                }
                string typed = _codeTyped;
                Act(new AddReq { Code = FriendCodes.Parse(typed) ?? typed, Kind = LinkKind.Friend }, ok =>
                {
                    if (_codeTyped == typed)
                    {
                        _codeTyped = "";
                        _codeName = null;
                    }
                    return ok.Done ? "you're friends now." : "friend request sent.\n\nwhen they enter your code too, you're friends.";
                });
                break;
            case >= AdRowCall and < AdRowCall + AdRows:
                if (RowOf(AdRowCall) is PlayerRow tapped) OpenLicense(menu, tapped.Card);
                break;
            case >= AdLeftCall and < AdLeftCall + AdRows:
                if (RowOf(AdLeftCall) is PlayerRow left) AddLink(left.Card, LinkKind.Friend);
                break;
            case >= AdRightCall and < AdRightCall + AdRows:
                if (RowOf(AdRightCall) is PlayerRow right) AddLink(right.Card, LinkKind.Rival);
                break;
            }
        }

        // the name keyboard has no "#": "KANDEN 4412" searches as kanden#4412
        private static string SearchQuery(string text)
        {
            Match m = Regex.Match(text.Trim(), @"^(.*\S)\s+(\d{1,4})$");
            return !text.Contains('#') && m.Success ? m.Groups[1].Value + "#" + m.Groups[2].Value : text.Trim();
        }

        private void RunSearch()
        {
            OnlineClient? client = _client;
            string query = SearchQuery(_searchText);
            if (query.Length == 0 || _searching) return;
            if (client == null || !client.Connected)
            {
                Import.Popup?.Inform(_connecting ? "connecting to the server..." : "can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
                if (!_connecting) RefreshOnline(lists: true);
                return;
            }
            _searching = true;
            Task.Run(async () =>
            {
                try
                {
                    PlayersMsg found = await client.RequestAsync<PlayersMsg>(new SearchReq { Query = query });
                    Post(() =>
                    {
                        _searching = false;
                        _found = found.Players;
                        _adOffset = 0;
                        if (_onlineMenu is MenuEngine menu && menu.Page?.Index == s_onAdd) ShowAdd(menu, 0);
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _searching = false;
                        Import.Popup?.Inform("couldn't search.\n\n" + Problem(ex));
                    });
                }
            });
        }

        // a whole code typed: whose it is, on the NEW CODE card
        private void LookUpCode()
        {
            OnlineClient? client = _client;
            string code = FriendCodes.Parse(_codeTyped) ?? _codeTyped;
            if (client == null || !client.Connected) return;
            Task.Run(async () =>
            {
                string name;
                try
                {
                    PlayerMsg p = await client.RequestAsync<PlayerMsg>(new GetPlayerReq { Code = code });
                    name = OnlineNames.Display(p.Player.Name, p.Player.Tag).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    name = "";
                }
                Post(() =>
                {
                    if ((FriendCodes.Parse(_codeTyped) ?? _codeTyped) == code) _codeName = name;
                });
            });
        }

        // ---- drawing ----

        private void DrawFriends(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _frTitle;
            if (!FrTabsShown.Contains(s_frTab)) s_frTab = 0;
            string[] labels = { "friends", "rivals", Incoming > 0 ? $"requests {Incoming}" : "requests", "recent" };
            foreach (int t in FrTabsShown)
            {
                (float tx, float tw) = FrTabSpot(t);
                Tab(tx, 140, tw, 70, labels[t], t == s_frTab, _frTab[t], glows);
            }
            List<PlayerRow> rows = FriendRows();
            int count = Math.Clamp(rows.Count - _frOffset, 0, FrRows);
            for (int r = 0; r < count; r++)
            {
                DrawRow(rows[_frOffset + r], FriendRowY(r), _frRow[r], _frWide[r], _frLeft[r], _frRight[r], _frOk[r], _frNo[r], glows);
            }
            if (count == 0)
            {
                (string first, string second) = !Live ? (_connecting ? "connecting..." : "can't reach the server.", _netError ?? "")
                    : _roster == null ? ("loading...", "") : s_frTab switch
                    {
                        0 => ("no friends yet.", "tap add to find hunters."),
                        1 => ("no rivals yet.", "add a rival from recent or add."),
                        2 => ("no requests.", "requests to and from you show here."),
                        _ => ("nobody yet.", "hunters you play online show here."),
                    };
                TextS(first, Mx(960), My(480), 2, White, tag, SizeS);
                TextS(Fit(second, 50), Mx(960), My(540), 2, Pale, tag, SizeM);
            }
            int pages = Pages(rows.Count, FrRows);
            if (pages > 1)
            {
                float w = _drawMenu!.Font.Measure($"{FriendsTitle} {pages}/{pages}") / 2;
                Arrow(false, 128 - w - 22, ArrowTop + Mw(42), _frPrev);
                Arrow(true, 128 + w + 12, ArrowTop + Mw(42), _frNext);
            }
            if (MeCard is PlayerCard me && me.Code.Length > 0)
                TextS("your code " + FriendCodes.Format(me.Code), Mx(960), My(1000), 2, Pale, tag, SizeM);
            // ADD: the person disc, its label under it
            PersonDisc(Mx(1746), My(928), Mw(88), _frAdd);
            Glow(glows, _frAdd, Mx(1740), My(922), Mx(1840), My(1022), _frAdd);
            TextS("add", Mx(1790), My(1030), 2, Orange, _frAdd, SizeM);
        }

        private void DrawAdd(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _adTitle;
            string[] labels = { "search name", "friend code", "from recent" };
            for (int t = 0; t < AdTabs; t++) Tab(300 + t * 424, 222, 400, 66, labels[t], t == s_adTab, _adTab[t], glows);
            if (s_adTab == 1)
            {
                DrawCode(glows);
                return;
            }
            if (s_adTab == 0)
            {
                // NAME field (the name keyboard) and SEARCH
                TextS("name", Mx(130), My(348), 0, Pale, _adField, SizeS);
                Fill(Mx(330), My(324), Mx(1230), My(414), 0, 0, 0, 0.7f, _adField);
                Box(Mx(330), My(324), Mx(1230), My(414), _adField);
                Glow(glows, _adField, Mx(330), My(324), Mx(1230), My(414), _adField);
                string shown = _searchText.ToLowerInvariant();
                TextS(shown, Mx(360), My(349), 0, White, _adField, SizeS);
                float cursor = 360 + _drawMenu!.Font.Measure(shown) * MockScale + (shown.Length > 0 ? 6 : 0);
                Fill(Mx(cursor), My(388), Mx(cursor + 34), My(394), Orange.R, Orange.G, Orange.B, 1, _adField);
                Capsule(Mx(1280), My(330), Mw(380), Mw(80), _searching ? "..." : "search", Live, _adSearch, glows, SizeM);
                TextS("type a name. add its number after it to find one hunter.", Mx(960), My(856), 2, OnGrey, tag, SizeX);
            }
            else
            {
                TextS("hunters you played online lately", Mx(130), My(348), 0, Pale, tag, SizeM);
                TextS(ShowRivals ? "add them as a friend or a rival. they get a request." : "add them as a friend. they get a request.", Mx(960), My(856), 2, OnGrey, tag, SizeX);
            }
            List<PlayerRow> rows = AddRows();
            int count = Math.Clamp(rows.Count - _adOffset, 0, AdRows);
            for (int r = 0; r < count; r++)
            {
                DrawRow(rows[_adOffset + r], AddRowY(r), _adRow[r], -1, _adLeft[r], _adRight[r], -1, -1, glows);
            }
            if (count == 0)
            {
                string line = !Live ? (_connecting ? "connecting..." : "can't reach the server.") : s_adTab == 2 ? "nobody yet: play online first."
                    : _searching ? "searching..." : _found == null ? "" : "no hunter found with that name.";
                TextS(line, Mx(960), My(560), 2, White, tag, SizeM);
            }
            int pages = Pages(rows.Count, AdRows);
            if (pages > 1)
            {
                float w = _drawMenu!.Font.Measure($"FIND A HUNTER {pages}/{pages}") / 2;
                Arrow(false, 128 - w - 22, ArrowTop, _adPrev);
                Arrow(true, 128 + w + 12, ArrowTop, _adNext);
            }
        }

        // FRIEND CODE: YOUR CODE and NEW CODE cards (as ENTER NEW CODE's), the keypad, OK
        private void DrawCode(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _adOk;
            PlayerCard? me = MeCard;
            string mine = me != null && me.Code.Length == CodeDigits ? me.Code : "";
            for (int j = 0; j < 2; j++)
            {
                float x = 130 + j * 480, y = 326, w = 440, h = 520;
                Fill(Mx(x), My(y), Mx(x + w), My(y + h), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, tag);
                Fill(Mx(x + 5), My(y + 5), Mx(x + w - 5), My(y + h - 5), 8 / 255f, 12 / 255f, 8 / 255f, 1, tag);
                Fill(Mx(x + 5), My(y + 5), Mx(x + w - 5), My(y + 5 + 8 * 3.6f + 16), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, tag);
                TextS(j == 0 ? "your code" : "new code", Mx(x + w / 2), My(y + 12), 2, Dark, tag, SizeM);
                Model(@"results\medalsilver", Mx(x + 50), My(y + 130), Mw(w - 100), Mw(w - 100), tag, alpha: 0.18f);
                string name = j == 0 ? me != null ? OnlineNames.Display(me.Name, me.Tag).ToLowerInvariant() : ""
                    : _codeName is { Length: > 0 } n ? n : _codeName != null ? "nobody has this code" : "?";
                TextS(Fit(name, 22), Mx(x + w / 2), My(y + 90), 2, j == 1 && _codeName == "" ? Red : Orange, tag, SizeM);
                string digits = j == 0 ? mine : _codeTyped;
                for (int k = 0; k < 3; k++)
                {
                    string part = digits.Length > k * 4 ? digits.Substring(k * 4, Math.Min(4, digits.Length - k * 4)) : "";
                    if (j == 1) part += new string('_', 4 - part.Length);
                    TextS(part, Mx(x + w / 2), My(y + 190 + k * 90), 2, j == 0 ? Orange : White, tag, SizeS);
                }
            }
            for (int d = 0; d <= 10; d++)
            {
                (float kx, float ky) = KeyAt(d);
                int item = d < 10 ? _adKey[d] : _adBackspace;
                string? model = d < 10 ? _numKey : _numBack;
                if (model != null) Model(model, Mx(kx), My(ky), Mw(116), Mw(116), item);
                else Fill(Mx(kx), My(ky), Mx(kx + 116), My(ky + 116), FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, item);
                Glow(glows, item, Mx(kx), My(ky), Mx(kx + 116), My(ky + 116), item);
                if (d < 10) TextS(d.ToString(), Mx(kx + 58), My(ky + 58 - 4 * 4.95f), 2, Dark, item, SizeS);
                else if (model == null) TextS("<", Mx(kx + 58), My(ky + 58 - 4 * 4.95f), 2, Dark, item, SizeS);
            }
            Model(@"gameoptions\ok", Mx(1640), My(616), Mw(130), Mw(130), _adOk, alpha: _codeTyped.Length == CodeDigits ? 1 : 0.5f);
            Glow(glows, _adOk, Mx(1640), My(616), Mx(1770), My(746), _adOk);
            TextS("enter their code. when they enter yours too, you're friends.", Mx(960), My(940), 2, Pale, tag, SizeX);
        }

        // a tab: a black box, the active one glowing with the blue dot under it
        private void Tab(float x, float y, float w, float h, string label, bool active, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            Fill(Mx(x), My(y), Mx(x + w), My(y + h), 0, 0, 0, 0.92f, item);
            if (active)
            {
                List<WidgetTri> g = Still(@"startgame\player_highlight");
                if (g.Count > 0) Nine(g, Mx(x) - 4, My(y) + 4, Mx(x + w) + 4, My(y + h) - 4, 16, 0.5f, item);
                Dot(Mx(x + w / 2 - 12), My(y + h + 6), Mw(24), item);
            }
            else Glow(glows, item, Mx(x), My(y), Mx(x + w), My(y + h), item);
            TextS(label, Mx(x + w / 2), My(y + (h - 8 * 3.6f) / 2 + 2), 2, active ? Orange : (150 / 255f, 90 / 255f, 40 / 255f), item, SizeM);
        }

        // a player row (mockup px top `by`): the person disc, name#tag (+ friend / rival badge), its line, presence, buttons
        private void DrawRow(PlayerRow row, float by, int rowItem, int wide, int left, int right, int ok, int no, Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = rowItem;
            PlayerCard c = row.Card;
            Fill(Mx(100), My(by), Mx(1820), My(by + 110), 0, 0, 0, 0.35f, tag);
            Box(Mx(100), My(by), Mx(1820), My(by + 110), tag);
            Glow(glows, tag, Mx(100), My(by), Mx(1820), My(by + 110), tag);
            if (LicensePick(c.License) is not { } pick || !SeatIcon(pick, Mx(116), My(by + 8), Mw(94), tag)) // (their hunter of choice)
            {
                PersonDisc(Mx(116), My(by + 8), Mw(94), tag, row.Dim ? 0.4f : 1); // (offline: darker)
            }
            string name = OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant();
            TextS(name, Mx(240), My(by + 16), 0, row.Dim ? OnGrey : White, tag, SizeM);
            if (row.Badge != null)
            {
                var kind = new PlayerCard { Friend = row.Badge == LinkKind.Friend, Rival = row.Badge == LinkKind.Rival };
                Badge(kind, Mx(240) + _drawMenu!.Font.Measure(name) * SizeM + Mw(14), My(by + 14), Mw(33), tag);
            }
            TextS(Fit(row.Line, 44), Mx(240), My(by + 66), 0, row.Dim ? OnGrey : row.LineColour, tag, SizeX);
            if (row.State != null) TextS(row.State, Mx(960), My(by + 38), 0, row.StateColour, tag, SizeM);
            switch (row.Buttons)
            {
            case RowButtons.Join:
                Capsule(Mx(1520), My(by + 24), Mw(270), Mw(62), "join", JoinBlock(c.GameId) == null, wide, glows, SizeM);
                break;
            case RowButtons.Cancel:
                PxButton(1520, by + 24, 270, 62, "cancel", SizeM, wide, glows);
                break;
            case RowButtons.Answer:
                Model(@"gameoptions\ok", Mx(1560), My(by + 8), Mw(94), Mw(94), ok);
                Glow(glows, ok, Mx(1560), My(by + 8), Mx(1654), My(by + 102), ok);
                Model(@"gameoptions\cancel", Mx(1680), My(by + 8), Mw(94), Mw(94), no);
                Glow(glows, no, Mx(1680), My(by + 8), Mx(1774), My(by + 102), no);
                break;
            case RowButtons.AddBoth:
                LinkSlot(c, LinkKind.Friend, 1300, by + 24, left, glows);
                LinkSlot(c, LinkKind.Rival, 1560, by + 24, right, glows);
                break;
            }
        }

        // ADD FRIEND / ADD RIVAL, or the word when already linked, "sent" while a request waits
        private void LinkSlot(PlayerCard c, string kind, float x, float y, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            if (kind == LinkKind.Rival && (!ShowRivals || Linked(c, LinkKind.Friend))) return; // (a friend is never a rival: the friend slot says it)
            if (Linked(c, kind)) TextS(kind, Mx(x + 120), My(y + 16), 2, kind == LinkKind.Friend ? Green : Red, item >= 0 ? item : _frTitle, SizeM);
            else if (Sent(c, kind)) TextS("sent", Mx(x + 120), My(y + 20), 2, OnGrey, item >= 0 ? item : _frTitle, SizeX);
            else PxButton(x, y, 240, 62, "add " + kind, SizeX, item, glows);
        }

        // the mockups' outlined button (mockup px)
        private void PxButton(float x, float y, float w, float h, string label, float size, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            Fill(Mx(x), My(y), Mx(x + w), My(y + h), 0, 0, 0, 0.43f, item);
            Box(Mx(x), My(y), Mx(x + w), My(y + h), item);
            Glow(glows, item, Mx(x), My(y), Mx(x + w), My(y + h), item);
            TextS(label, Mx(x + w / 2), My(y + (h - 8 * 4.95f * size) / 2 + 2), 2, Orange, item, size);
        }

        private void PersonDisc(float x, float top, float d, int tag, float shade = 1)
        {
            int tex = Derived("person", @"multiplayer\friendconfig", src => Disc(src, 0, 0, src.Width, src.Height));
            if (tex >= 0) Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, shade, shade, shade, 1, tag);
        }

        // the active tab's blue dot (the mockups' focus marker): a blue disc with a black edge
        private void Dot(float x, float top, float d, int tag)
        {
            UiTexture t = _drawMenu!.Widgets.Textures.GetOrAdd("online blue dot", () =>
            {
                const int n = 32;
                byte[] px = new byte[n * n * 4];
                for (int y = 0; y < n; y++)
                {
                    for (int x0 = 0; x0 < n; x0++)
                    {
                        float dist = MathF.Sqrt((x0 + 0.5f - n / 2f) * (x0 + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                        float outer = Math.Clamp(n / 2f - dist + 0.5f, 0, 1), inner = Math.Clamp(n / 2f - 4 - dist + 0.5f, 0, 1);
                        int i = (y * n + x0) * 4;
                        px[i] = (byte)(40 * inner);
                        px[i + 1] = (byte)(150 * inner);
                        px[i + 2] = (byte)(230 * inner);
                        px[i + 3] = (byte)(255 * outer);
                    }
                }
                return (n, n, px);
            });
            Quad(t.Id, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }
    }
}
