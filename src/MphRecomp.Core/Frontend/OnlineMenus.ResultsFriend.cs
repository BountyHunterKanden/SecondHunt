using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MphRecomp.Multiplayer;
using MphRecomp.Online;
using MphRecomp.Config;

// ADD FRIEND on RESULTS (owner GO 2026-11-16, docs/IDEAS_SURVEY.md): after an online match each other human's row gets a
// person button (pad, touch, mouse) at the right end of its name cell, through the friends path the license uses (LinkAction: ADD, ACCEPT
// their request, take ours back, REMOVE). Its look says where things stand: + add, green their request waiting, dim ours
// sent, cyan friends, red bar blocked (unblock on their license). No button for bots, this device's own row or a hunter
// whose card the match didn't bring; offline RESULTS unchanged. The roster comes over the connection RESULTS kept for PLAY
// AGAIN (a login of its own would cut it). 5-8 players (compact RESULTS): the buttons and the names' taps move with their
// rows as MultiplayerMenu.LayoutResults8 moves the names.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private const int ResFriendCall = 2850; // + RESULTS' shown row (0-7)
        private const int ResNameMoreCall = 2858; // + shown row - 4: the names' taps for rows 5-8 (compact RESULTS)
        private const int ResFriendFirstCall = ResFriendCall, ResFriendLastCall = 2869;

        // the button: a person disc at the name cell's right end (DS x; the cell is 3..72 and a 10-letter name, centred,
        // ends near 62), less than the row's height
        private const float ResFriendX = 60, ResFriendD = 12;

        private readonly int[] _resFriend = Unset(8); // RESULTS' rows' ADD FRIEND buttons
        private readonly int[] _resNameMore = Unset(4); // the names' taps of rows 5-8
        private int _resFriendRow = -1; // the button last pressed (its label stays until another is focused)

        private static readonly float[] ResCellY = { 117, 89, 61, 33 }; // (MultiplayerMenu.LayoutResults8's cells)

        private void InstallResultFriends(Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, float, float, int, MenuItem> touch)
        {
            if (_file.Pages.Count <= MultiplayerMenu.ResultsPage) return;
            MenuPage results = _file.Pages[MultiplayerMenu.ResultsPage];
            for (int k = 0; k < _resNameMore.Length; k++)
            {
                float y = ResultRowY[k] - 192;
                int call = ResNameMoreCall + k;
                _resNameMore[k] = results.AddItem(i => touch(i, 30, y + 12, 106, y - 4, call)).Index;
            }
            // (the buttons after every name tap: the topmost item takes a touch, and the names' rectangles reach under them)
            for (int row = 0; row < _resFriend.Length; row++)
            {
                float mid = ResCellY[row % 4] - 189; // (rows 5-8: at their source row, moved by the compact transform)
                int call = ResFriendCall + row;
                _resFriend[row] = results.AddItem(i => input(i, ResFriendX, mid + ResFriendD / 2, ResFriendX + ResFriendD,
                    mid - ResFriendD / 2, call)).Index;
            }
        }

        private void ApplyResultsFriendSounds(Action<int, int, int, int> like)
        {
            foreach (int b in _resFriend) like(MultiplayerMenu.ResultsPage, b, OptionBoxPage, _soundToggle);
        }

        // the names' tap areas, rows 0-7 (0-3 OnlineMenus.License.cs's)
        private int ResultNameTap(int row) => row < 4 ? _resultName[row] : _resNameMore[row - 4];

        // a row's item where compact RESULTS puts it (as LayoutResults8 does its names), or back at its own row
        private static void PlaceResultRow(MenuEngine menu, int item, int row, bool compact, float pivotX)
        {
            if (item < 0) return;
            float py = ResCellY[row % 4] - 189;
            float ty = ResCellY[row / 2] - 189 + (row % 2 == 0 ? 7 : -7);
            if (compact) menu.SetItemTransform(item, 0.8f, pivotX, py, 0, ty - py);
            else menu.ClearItemTransform(item);
        }

        // the hunter on RESULTS' shown row, when it's an online match's other human and the match brought their card
        private static PlayerCard? ResultCard(MatchResults? r, int row, bool others)
        {
            if (!MultiplayerMenu.OnlineResults || r == null || s_matchCards == null) return null;
            MatchResults.Player?[] shown = MultiplayerMenu.ResultsShown(r);
            if (row >= shown.Length || shown[row] is not MatchResults.Player p || p.Bot) return null;
            if (others && p.Seat == r.LocalSeat) return null;
            return s_matchCards.TryGetValue(p.Seat, out PlayerCard? c) ? c : null;
        }

        // RESULTS coming in (fetch: and the roster, for the buttons' looks)
        private void ShowResultFriends(MenuEngine menu, bool fetch = true)
        {
            if (menu.Page?.Index != MultiplayerMenu.ResultsPage) return;
            MatchResults? r = MatchResults.Last;
            bool compact = r != null && r.Rows.Count > 4;
            string? me = MeCard?.Code;
            bool any = false;
            for (int row = 0; row < _resFriend.Length; row++)
            {
                PlayerCard? c = ResultCard(r, row, others: true);
                // (not in a public build yet: after the fixes-only release, owner 2026-11-17)
                bool on = !RecompSettings.PublicBuild && c != null && c.Code != me && (row < 4 || compact);
                Show(menu, _resFriend[row], on);
                PlaceResultRow(menu, _resFriend[row], row, compact, ResFriendX + ResFriendD / 2);
                any |= on;
            }
            if (fetch)
            {
                _resFriendRow = -1;
                if (any) _onlineMenu = menu; // (Redraw and the popups' answers reach RESULTS)
                if (any) FetchResultsRoster();
            }
        }

        private void FetchResultsRoster()
        {
            if (_previewMe != null) return;
            OnlineClient? client = Conn;
            if (client == null || !client.Connected)
            {
                if (Kept == null && !_connecting) RefreshOnline(lists: true);
                return;
            }
            FetchRosterOn(client);
        }

        // RefreshOnline on RESULTS' kept match: the roster over its connection (a login now would cut it); false = no kept match
        private bool KeptRoster(bool lists)
        {
            if (Kept is not { Client: { Connected: true } client }) return false;
            if (lists) FetchRosterOn(client);
            return true;
        }

        private void FetchRosterOn(OnlineClient client)
        {
            Task.Run(async () =>
            {
                try
                {
                    RosterMsg roster = await client.RequestAsync<RosterMsg>(new RosterReq());
                    Post(() =>
                    {
                        _roster = WithFakeFriends(roster);
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    // (the buttons keep the card's own friend flag)
                }
            });
        }

        private void ResultsFriendCall(MenuEngine menu, int call)
        {
            MatchResults? r = MatchResults.Last;
            if (call >= ResNameMoreCall)
            {
                if (ResultCard(r, 4 + call - ResNameMoreCall, others: false) is PlayerCard who) OpenLicense(menu, who);
                return;
            }
            int row = call - ResFriendCall;
            if (ResultCard(r, row, others: true) is not PlayerCard c) return;
            _resFriendRow = row;
            if (Blocked(c))
            {
                string name = StreamerPrivacy.Display(c.Name, c.Tag).ToLowerInvariant();
                Import.Popup?.Inform($"you blocked {name}.\n\nunblock them on their hunter license first.");
                return;
            }
            LinkAction(c, LinkKind.Friend);
        }

        // (-onlinemenutest) RESULTS' row's button shown / what its label says
        public bool ResultsFriendShown(MenuEngine menu, int row) => row >= 0 && row < _resFriend.Length && _resFriend[row] >= 0
            && menu.ItemState(_resFriend[row]) != MenuState.Hidden;

        public string? ResultsFriendLabel(int row) =>
            ResultCard(MatchResults.Last, row, others: true) is PlayerCard c ? FriendLookLabel(FriendLook(c)) : null;

        private enum ResFriendLook { Add, Accept, Sent, Friends, Blocked }

        private ResFriendLook FriendLook(PlayerCard c) => Blocked(c) ? ResFriendLook.Blocked
            : Linked(c, LinkKind.Friend) ? ResFriendLook.Friends : IncomingFrom(c, LinkKind.Friend) ? ResFriendLook.Accept
            : Sent(c, LinkKind.Friend) ? ResFriendLook.Sent : ResFriendLook.Add;

        private static string FriendLookLabel(ResFriendLook look) => look switch
        {
            ResFriendLook.Accept => "accept friend",
            ResFriendLook.Sent => "request sent",
            ResFriendLook.Friends => "friends",
            ResFriendLook.Blocked => "blocked",
            _ => "add friend",
        };

        // (DrawOnline, RESULTS: after the page's own triangles)
        private void DrawResultsFriends(MenuEngine menu, List<WidgetTri> tris)
        {
            MenuPage page = menu.Page!;
            var mine = new HashSet<int>(_resFriend.Where(b => b >= 0));
            var glows = new Dictionary<int, List<WidgetTri>>();
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                int item = tris[t].Item;
                if (!mine.Contains(item)) continue;
                if (!glows.TryGetValue(item, out List<WidgetTri>? g)) glows[item] = g = new List<WidgetTri>();
                g.Add(tris[t]);
                tris.RemoveAt(t);
            }
            if (menu.PendingPage != -1 || !MultiplayerMenu.OnlineResults) return;
            _drawMenu = menu;
            _out = tris;
            MatchResults? r = MatchResults.Last;
            int focused = menu.FocusedItem();
            int labelRow = -1;
            for (int row = 0; row < _resFriend.Length; row++)
            {
                int item = _resFriend[row];
                if (item < 0 || item >= page.Items.Count || menu.ItemState(item) == MenuState.Hidden) continue;
                if (ResultCard(r, row, others: true) is not PlayerCard c) continue;
                if (item == focused) labelRow = row;
                int start = tris.Count;
                float mid = ResCellY[row % 4] - 189, x0 = ResFriendX, top = mid + ResFriendD / 2;
                ResFriendLook look = FriendLook(c);
                Fill(x0 - 1, top + 1, x0 + ResFriendD + 1, top - ResFriendD - 1, 0, 0, 0, 0.45f, item);
                Glow(glows, item, x0, top, x0 + ResFriendD, top - ResFriendD, item);
                float shade = look is ResFriendLook.Sent or ResFriendLook.Blocked ? 0.45f : 1;
                int tex = Derived("person", @"multiplayer\friendconfig", src => Disc(src, 0, 0, src.Width, src.Height));
                (float R, float G, float B) tint = look switch
                {
                    ResFriendLook.Friends => Cyan,
                    ResFriendLook.Accept => Green,
                    _ => (1f, 1f, 1f),
                };
                float d = ResFriendD - 2;
                if (tex >= 0) Quad(tex, x0 + 1, top - 1, x0 + 1 + d, top - 1 - d, 0, 0, 1, 1, tint.R * shade, tint.G * shade, tint.B * shade, 1, item);
                if (look is ResFriendLook.Add or ResFriendLook.Accept)
                {
                    // the "+" at the disc's lower right
                    (float R, float G, float B) plus = look == ResFriendLook.Add ? Orange : Green;
                    float px = x0 + ResFriendD - 3, py = top - ResFriendD + 3;
                    Fill(px - 3, py + 0.8f, px + 3, py - 0.8f, plus.R, plus.G, plus.B, 1, item);
                    Fill(px - 0.8f, py + 3, px + 0.8f, py - 3, plus.R, plus.G, plus.B, 1, item);
                }
                else if (look == ResFriendLook.Blocked)
                {
                    Fill(x0, mid + 1.2f, x0 + ResFriendD, mid - 1.2f, Red.R, Red.G, Red.B, 1, item);
                }
                menu.TransformTris(item, tris, start);
            }
            if (labelRow < 0 && focused < 0) labelRow = _resFriendRow; // (a tap: the label until the pad takes over)
            if (labelRow >= 0 && ResultCard(r, labelRow, others: true) is PlayerCard lc)
            {
                // the label in a dark tag just over the button, its right edge on the button's (its row moved as the button is)
                int item = _resFriend[labelRow];
                int start = tris.Count;
                float mid = ResCellY[labelRow % 4] - 189, top = mid + ResFriendD / 2 + 10;
                string label = FriendLookLabel(FriendLook(lc));
                float w = label.Length * 8 * SizeX + 6;
                float x1 = ResFriendX + ResFriendD + 2;
                Fill(x1 - w, top + 1, x1, top - 8 * SizeX - 2, 0, 0, 0, 0.75f, item);
                TextS(label, x1 - w + 3, top, 0, OnGrey, item, SizeX);
                menu.TransformTris(item, tris, start);
            }
        }
    }
}
