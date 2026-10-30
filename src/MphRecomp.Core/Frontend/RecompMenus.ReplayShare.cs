using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Online;
using MphRecomp.Replay;
using MphRecomp.Config;

namespace MphRecomp.Frontend
{
    // REPLAYS' sharing with friends (2026-10-19; the server side: Online/ReplayMail.cs, the device side:
    // Replay/SharedReplays.cs). The list's SHARED WITH ME tab (tabs as the settings screen's, 2026-10-19: MY REPLAYS,
    // FAVORITES, SHARED WITH ME; touch or L1 / R1): friends' replays waiting on the server first
    // (from, mode, arena, length, size; DOWNLOAD / DELETE), then the ones downloaded (replays/shared/, they play like any
    // other). A replay's page: SEND TO FRIEND (up to 8 friends from the roster; SAVE went 2026-10-19, EXPORT's sheet saves to Downloads). The REPLAYS
    // pages connect to the server as ONLINE PLAY does at login (when this device has been online before), quietly "offline"
    // when it can't be reached.
    public sealed partial class RecompMenus
    {
        // (ReplayCall's range: ReplaySeatCall's license calls end at 2159)
        private const int ShareFirstCall = 2160, ShareGetCall = 2160, ShareDropCall = 2170,
            ReplaySendCall = 2182, PickRowCall = 2185, PickPrevCall = 2190, PickNextCall = 2191, PickSendCall = 2192;
        // the list's tabs (+ the tab), the arrows at their ends
        private const int ReplayTabCall = 2193, ReplayTabPrevCall = 2196, ReplayTabNextCall = 2197;
        private static readonly string[] ReplayTabNames = { "MY REPLAYS", "FAVORITES", "SHARED WITH ME" };
        private const int PickRows = 5;

        // SEND TO FRIEND (a replay's page; the list's record replays corner)
        private const float SendX0 = 376, SendX1 = 458, CornerTop = -185, CornerBottom = -198;
        // the offer row's two buttons, under its mode and arena
        private const float GetX0 = TextX, GetX1 = 160.1f, DropX0 = 164.1f, DropX1 = ColumnRight, OfferButtonDy = 23.4f, OfferButtonH = 11.3f;
        // the friend picker's rows
        private const float PickTop0 = -36, PickPitch = 22, SharePickH = 18, PickBox = 11;

        private static int s_sharePickPage = -1;

        // the list as shown: the tab's (0 my replays, 1 favorites, 2 shared with me: offers, then the downloaded ones)
        private readonly List<(ReplayInfo? Info, ReplayOffer? Offer)> _listed = new();
        private int _replayTab;
        private bool _sharedView => _replayTab == 2;
        private readonly int[] _lGet = { -1, -1, -1 }, _lDrop = { -1, -1, -1 }, _lTab = { -1, -1, -1 };
        private int _lTabPrev = -1, _lTabNext = -1, _dSend = -1;
        private int _spPanel = -1, _spBack = -1, _spTitle = -1, _spTitleString, _spPrev = -1, _spNext = -1, _spSend = -1, _spOffset;
        private readonly int[] _spRow = { -1, -1, -1, -1, -1 };
        private readonly HashSet<string> _spChosen = new();
        private string? _spPath;
        private bool _spSending;
        private MenuEngine? _rpMenu; // (the REPLAYS pages' engine: _stepMenu is only set by a call)
        private bool _rpFetch, _rpConnect;
        private Action? _rpChanged; // SharedReplays.Changed's handler while a REPLAYS page is open (TickReplayShare)
        private readonly HashSet<string> _rpBusy = new(); // offers downloading or deleting

        private static bool IsReplaysPageIndex(int page) => page >= 0
            && (page == s_listPage || page == s_detailPage || page == s_replayLicensePage || page == s_sharePickPage);

        // InstallReplays: the list's and the details page's share items, then the picker page
        private void InstallReplayShare(MenuPage list, MenuPage detail, Func<int, MenuAction[], MenuPage> addFramePage,
            Func<int, float, float, float, float, int, MenuItem> input, Func<int, float, float, float, float, int, MenuItem> touch,
            MenuItem panel, MenuItem backIcon, MenuItem backButton, MenuItem title)
        {
            for (int r = 0; r < ReplayRows; r++)
            {
                float top = RowTop0 - RowPitch47 * r - OfferButtonDy;
                int row = r;
                _lGet[r] = list.AddItem(i => input(i, GetX0, top, GetX1, top - OfferButtonH, ShareGetCall + row)).Index;
                _lDrop[r] = list.AddItem(i => input(i, DropX0, top, DropX1, top - OfferButtonH, ShareDropCall + row)).Index;
            }
            // the tabs: touch only (L1 / R1 with a pad), as the settings screen's
            List<(float X0, float X1)> tabs = ReplayTabLayout();
            for (int t = 0; t < tabs.Count; t++)
            {
                (float x0, float x1) = tabs[t];
                int call = ReplayTabCall + t;
                _lTab[t] = list.AddItem(i => touch(i, x0, TabTop, x1, TabBottom, call)).Index;
            }
            _lTabPrev = list.AddItem(i => touch(i, TabIconL0, TabTop + 3, TabX0 - 1, TabBottom - 3, ReplayTabPrevCall)).Index;
            _lTabNext = list.AddItem(i => touch(i, TabX1 + 1, TabTop + 3, TabIconR1, TabBottom - 3, ReplayTabNextCall)).Index;
            _dSend = detail.AddItem(i => input(i, SendX0, CornerTop, SendX1, CornerBottom, ReplaySendCall)).Index;

            // ---- SEND TO FRIEND: the friends, five a page ----
            MenuPage page = addFramePage(detail.Index, new[] { Call(MenuKeys.R, PickNextCall), Call(MenuKeys.L, PickPrevCall) });
            s_sharePickPage = page.Index;
            _spPanel = page.AddItem(i => Clone(panel, i, PanelDx, 0, stretchX: PanelStretchX, stretchDown: PanelStretchDown)).Index;
            page.AddItem(i => Clone(backIcon, i, 0, 0));
            _spBack = page.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions.Select(a => WithTarget(a, detail.Index)).ToList())).Index;
            _spTitleString = _strings.Add("SEND TO FRIEND");
            _spTitle = page.AddItem(i => Clone(title, i, 0, 0, stringId: _spTitleString)).Index;
            for (int r = 0; r < PickRows; r++)
            {
                float top = PickTop0 - PickPitch * r;
                int row = r;
                _spRow[r] = page.AddItem(i => input(i, BarX0, top, BarX1, top - SharePickH, PickRowCall + row)).Index;
            }
            _spPrev = page.AddItem(i => touch(i, 128 - ArrowGap - ArrowW - 4, ArrowTop + 4, 128 - ArrowGap + 4, ArrowTop - ArrowH - 4, PickPrevCall)).Index;
            _spNext = page.AddItem(i => touch(i, 128 + ArrowGap - 4, ArrowTop + 4, 128 + ArrowGap + ArrowW + 4, ArrowTop - ArrowH - 4, PickNextCall)).Index;
            _spSend = page.AddItem(i => input(i, SendX0, CornerTop, SendX1, CornerBottom, PickSendCall)).Index;
        }

        private void ApplyReplayShareSounds(Action<int, int, int, int> like)
        {
            for (int r = 0; r < ReplayRows; r++)
            {
                like(s_listPage, _lGet[r], OptionBoxPage, _soundToggle);
                like(s_listPage, _lDrop[r], OptionBoxPage, _soundToggle);
            }
            like(s_detailPage, _dSend, MultiplayerMenu.CreateJoinPage, _soundListRow);
            like(s_sharePickPage, _spBack, MultiplayerMenu.CreateJoinPage, _soundBack);
            foreach (int row in _spRow) like(s_sharePickPage, row, MultiplayerMenu.CreateJoinPage, _soundListRow);
            like(s_sharePickPage, _spSend, MultiplayerMenu.CreateJoinPage, _soundListRow);
        }

        // ---- the list ----

        private void BuildListed()
        {
            _listed.Clear();
            if (_sharedView)
            {
                foreach (ReplayOffer o in SharedReplays.Waiting.OrderByDescending(o => o.Sent)) _listed.Add((null, o));
                foreach (ReplayInfo r in _replays.Where(r => r.Shared)) _listed.Add((r, null));
            }
            else
            {
                foreach (ReplayInfo r in _replays.Where(r => !r.Shared && (_replayTab == 0 || r.Pinned))) _listed.Add((r, null));
            }
        }

        // the share items of the rows shown (ShowReplayRows): an offer's DOWNLOAD / DELETE, nothing on a replay's row
        private void ShowShareRows(MenuEngine menu, int shown)
        {
            for (int r = 0; r < ReplayRows; r++)
            {
                ReplayOffer? offer = r < shown ? _listed[_replayOffset + r].Offer : null;
                if (offer == null)
                {
                    menu.SetState(_lGet[r], MenuState.Hidden);
                    menu.SetState(_lDrop[r], MenuState.Hidden);
                    continue;
                }
                bool busy = _rpBusy.Contains(offer.Id);
                SetKeepingFocus(menu, _lGet[r], busy ? MenuState.State5 : MenuState.Idle);
                SetKeepingFocus(menu, _lDrop[r], busy ? MenuState.State5 : MenuState.Idle);
            }
            Show(menu, _lRecord, _replayTab == 0);
        }

        // an item shown again: a focused one stays focused
        private static void SetKeepingFocus(MenuEngine menu, int item, MenuState state)
        {
            if (state == MenuState.Idle && menu.ItemState(item) == MenuState.Focused) return;
            menu.SetState(item, state);
        }

        private int SharedWaiting => SharedReplays.Blocked(ProfileDir) ? 0 : SharedReplays.Waiting.Count;

        // the tabs' places: each as wide as its name wants, across TabX0..TabX1 (the settings screen's TabLayout)
        private static List<(float X0, float X1)> ReplayTabLayout()
        {
            var layout = new List<(float, float)>();
            float units = ReplayTabNames.Sum(n => n.Length + 4f), room = TabX1 - TabX0 - TabGap * (ReplayTabNames.Length - 1), x = TabX0;
            foreach (string name in ReplayTabNames)
            {
                float w = room * (name.Length + 4f) / units;
                layout.Add((x, x + w));
                x += w + TabGap;
            }
            return layout;
        }

        // the tabs over the list (DrawTabs' look: this one lit, the others plain, SHARED WITH ME green while friends'
        // replays wait), the status line, an empty tab's words (DrawList)
        private void DrawReplayTabs(int shown, float statusX, int statusAlign)
        {
            if (++_padAsked % 60 == 0) _padShown = PadConnected();
            int tag = _lTitle, n = SharedWaiting;
            List<(float X0, float X1)> tabs = ReplayTabLayout();
            for (int t = 0; t < tabs.Count; t++)
            {
                (float x0, float x1) = tabs[t];
                bool on = t == _replayTab, waiting = t == 2 && n > 0;
                if (on) Fill(x0, TabTop, x1, TabBottom, FrameOrange.R, FrameOrange.G, FrameOrange.B, 0.55f, tag);
                else Fill(x0, TabTop, x1, TabBottom, 0, 0, 0, 0.43f, tag);
                Box(x0, TabTop, x1, TabBottom, tag);
                string name = waiting ? $"{ReplayTabNames[t]} ({n})" : ReplayTabNames[t];
                float room = x1 - x0 - 5, width = MathF.Max(_drawMenu!.Font.Measure(name), 1);
                float size = Math.Clamp(room / width, SizeX * 0.7f, SizeM);
                int from = _out.Count;
                TextS(name, (x0 + x1) / 2, TabTop - (TabTop - TabBottom - 8 * size) / 2, 2, on ? White : waiting ? Green : Orange, tag, size);
                if (!on && !waiting) Dim(from, 0.75f);
            }
            float arrowTop = TabTop - (TabTop - TabBottom - SecArrowH) / 2;
            float leftArrow = TabX0 - TabArrowGap - SecArrowW, rightArrow = TabX1 + TabArrowGap;
            SmallArrow(false, leftArrow, arrowTop, _lTabPrev);
            SmallArrow(true, rightArrow, arrowTop, _lTabNext);
            if (_padShown)
            {
                // the shoulder buttons: a small dark key with its name
                foreach ((string key, float x0) in new[] { ("l1", leftArrow - 2 - TabIconW), ("r1", rightArrow + SecArrowW + 2) })
                {
                    Fill(x0, TabTop - 1, x0 + TabIconW, TabBottom + 1, 0, 0, 0, 0.6f, tag);
                    Box(x0, TabTop - 1, x0 + TabIconW, TabBottom + 1, tag);
                    float size = Math.Clamp((TabIconW - 4) / MathF.Max(_drawMenu!.Font.Measure(key), 1), SizeX, SizeM);
                    TextS(key, x0 + TabIconW / 2, TabTop - 1 - (TabTop - TabBottom + 2 - 8 * size) / 2, 2, Pale, tag, size);
                }
            }
            if (_replayTab == 1 && shown == 0)
            {
                Text("no favorites yet.", 128, -70, 2, White, tag);
                Text("open a replay and press favorite to keep it here.", 128, -84, 2, Pale, tag);
            }
            if (!_sharedView) return;
            string? status = _connecting ? "connecting..." : _client is { Connected: true } ? null : "offline";
            if (status != null) Text(status, statusX, PageIndicatorTop - 2.2f, statusAlign, Pale, tag);
            if (shown == 0)
            {
                Text("nothing shared with you yet.", 128, -70, 2, White, tag);
                Text(SharedReplays.Blocked(ProfileDir) ? "friend replays are off (recomp settings > help)."
                    : "a friend's replay waits here until you download it.", 128, -84, 2, Pale, tag);
            }
        }

        // another tab: its rows from the top (L1 / R1 wrap round; a touch on the tab shown does nothing)
        private void GoToReplayTab(MenuEngine menu, int tab)
        {
            tab = (tab + ReplayTabNames.Length) % ReplayTabNames.Length;
            if (tab == _replayTab) return;
            ToggleSound(menu);
            _replayTab = tab;
            _replayOffset = 0;
            BuildListed();
            int focused = menu.FocusedItem();
            if (focused != -1) menu.SetState(focused, MenuState.Idle);
            ShowReplayRows(menu, 0);
            if (_sharedView) _rpFetch = true;
        }

        // a friend's replay waiting on the server, in a list row
        private void DrawOffer(ReplayOffer o, int r, Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _lRow[r];
            float top = RowTop0 - RowPitch47 * r;
            Fill(BarX0, top, BarX1, top - BarH, 0, 0, 0, 0.35f, tag);
            Box(BarX0, top, BarX1, top - BarH, tag);
            Glow(glows, tag, BarX0, top, BarX1, top - BarH, tag);
            // who and when (where a replay's hunters are)
            float left = BarX0 + IconDx, room = TextX - 6 - left;
            Text("from", left, top - 3.2f, 0, Pale, tag);
            FitText(StreamerPrivacy.Display(o.From.Name, o.From.Tag).ToLowerInvariant(), left, top - 13.3f, 0, room, Orange, tag);
            string sent = DateTimeOffset.FromUnixTimeSeconds(o.Sent).LocalDateTime.ToString("MMM d", CultureInfo.InvariantCulture).ToLowerInvariant();
            FitText($"{sent}, {Math.Max(1, (o.Bytes + 1023) / 1024)} kb", left, top - 25.9f, 0, room, White, tag);
            // what: mode, arena, length
            Text(Fit(OfferMode(o), 18), TextX, top - 3.2f, 0, Orange, tag);
            FitText(o.Arena.ToLowerInvariant(), TextX, top - 13.3f, 0, ColumnRight - TextX - 30, White, tag);
            if (o.Seconds > 0) Text(Length(o.Seconds), ColumnRight, top - 13.3f, 1, White, tag);
            bool busy = _rpBusy.Contains(o.Id);
            float bt = top - OfferButtonDy, bb = bt - OfferButtonH;
            Fill(GetX0, bt, GetX1, bb, 0, 0, 0, 0.43f, _lGet[r]);
            Box(GetX0, bt, GetX1, bb, _lGet[r]);
            Glow(glows, _lGet[r], GetX0, bt, GetX1, bb, _lGet[r]);
            Text(busy ? "wait..." : "download", (GetX0 + GetX1) / 2, bt - 1.65f, 2, busy ? Pale : Green, _lGet[r]);
            Fill(DropX0, bt, DropX1, bb, 0, 0, 0, 0.43f, _lDrop[r]);
            Box(DropX0, bt, DropX1, bb, _lDrop[r]);
            Glow(glows, _lDrop[r], DropX0, bt, DropX1, bb, _lDrop[r]);
            Text("delete", (DropX0 + DropX1) / 2, bt - 1.65f, 2, busy ? Pale : Orange, _lDrop[r]);
            Picture(o.Arena, MapX, top + 2.2f, MapW, MapH, MapFrame, tag);
        }

        private static string OfferMode(ReplayOffer o)
            => Enum.TryParse(o.Mode, out GameMode mode) ? ModeName(mode) : o.Mode.ToLowerInvariant();

        // the notice when a friend's replay comes in (OnlineMenus.Find's FindPush; not while friend replays are off)
        private void ReplayOffered(ReplayOffer o)
        {
            string from = StreamerPrivacy.Display(o.From.Name, o.From.Tag).ToLowerInvariant();
            Import.Popup?.Inform($"{from} shared a replay with you.\n\n{OfferMode(o)} - {o.Arena.ToLowerInvariant()}. it's in replays > shared with me.");
        }

        // text cut down to `room` by shrinking it (to SizeX), then by letters
        private void FitText(string text, float x, float top, int align, float room, (float R, float G, float B) colour, int tag)
        {
            float size = Math.Clamp(room / MathF.Max(_drawMenu!.Font.Measure(text), 1), SizeX, 1);
            while (text.Length > 1 && _drawMenu.Font.Measure(text) * size > room) text = text[..^1];
            TextS(text, x, top + (1 - size) * 4, align, colour, tag, size);
        }

        // a downloaded one's sender, from its file's name (ReplayLibrary.SaveShared: "... from Kanden_421 (2)")
        private static string? SharedWho(ReplayInfo r)
        {
            if (!r.Shared) return null;
            int at = r.Title.LastIndexOf(" from ", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string who = r.Title[(at + 6)..].Trim();
            if (who.EndsWith(')') && who.LastIndexOf(" (", StringComparison.Ordinal) is int p and > 0) who = who[..p];
            int u = who.LastIndexOf('_');
            if (u > 0 && u < who.Length - 1 && who[(u + 1)..].All(char.IsDigit)) who = who[..u] + "#" + who[(u + 1)..];
            return who.ToLowerInvariant();
        }

        // ---- calls ----

        private void ShareCall(MenuEngine menu, int call)
        {
            _rpMenu = menu;
            if (call is >= ShareGetCall and < ShareGetCall + ReplayRows or >= ShareDropCall and < ShareDropCall + ReplayRows)
            {
                int row = call % 10, at = _replayOffset + row;
                if (at >= _listed.Count || _listed[at].Offer is not ReplayOffer offer || _rpBusy.Contains(offer.Id)) return;
                if (call - row == ShareGetCall) Download(offer);
                else AskDrop(offer);
                return;
            }
            if (call is >= PickRowCall and < PickRowCall + PickRows)
            {
                Pick(menu, call - PickRowCall);
                return;
            }
            switch (call)
            {
            case ReplayTabCall:
            case ReplayTabCall + 1:
            case ReplayTabCall + 2:
                GoToReplayTab(menu, call - ReplayTabCall);
                return;
            case ReplayTabPrevCall:
            case ReplayTabNextCall:
                GoToReplayTab(menu, _replayTab + (call == ReplayTabNextCall ? 1 : -1));
                return;
            case PickPrevCall:
            case PickNextCall:
                int pages = PickPages;
                if (pages > 1)
                {
                    _spOffset = (_spOffset / PickRows + (call == PickNextCall ? 1 : pages - 1)) % pages * PickRows;
                    ShowPick(menu, 0);
                }
                return;
            case PickSendCall:
                Send(menu);
                return;
            }
            ReplayInfo? open = OpenInfo;
            if (open == null) return;
            if (call == ReplaySendCall)
            {
                _spPath = open.Path;
                menu.GoTo(s_sharePickPage);
            }
        }

        // the connection the REPLAYS pages use (ONLINE PLAY's, kept while these pages are open)
        private OnlineClient? ShareClient => _client is { Connected: true } c ? c : null;

        private void Download(ReplayOffer offer)
        {
            OnlineClient? client = ShareClient;
            string? dir = ReplayDir;
            if (client == null || dir == null)
            {
                Import.Popup?.Inform("can't reach the server right now.\n\ntry again in a moment.");
                return;
            }
            _rpBusy.Add(offer.Id);
            RedrawReplays();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string path = await SharedReplays.DownloadAsync(client, ServerUri, dir, offer);
                    Post(() =>
                    {
                        _rpBusy.Remove(offer.Id);
                        // its row focused on the list; not while another replay's page is open (or it's playing)
                        if (_rpMenu?.Page?.Index == s_listPage) _replayOpen = path;
                        ListReplays();
                        RedrawReplays();
                    });
                }
                catch (Exception ex) // (any: a damaged download mustn't leave the row busy)
                {
                    Post(() =>
                    {
                        _rpBusy.Remove(offer.Id);
                        RedrawReplays();
                        Import.Popup?.Inform("couldn't download that replay.\n\n" + Problem(ex));
                    });
                }
            });
        }

        private void AskDrop(ReplayOffer offer)
        {
            string from = StreamerPrivacy.Display(offer.From.Name, offer.From.Tag).ToLowerInvariant();
            void Drop()
            {
                OnlineClient? client = ShareClient;
                if (client == null)
                {
                    Import.Popup?.Inform("can't reach the server right now.\n\ntry again in a moment.");
                    return;
                }
                _rpBusy.Add(offer.Id);
                RedrawReplays();
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await SharedReplays.DeleteAsync(client, offer);
                        Post(() =>
                        {
                            _rpBusy.Remove(offer.Id);
                            RedrawReplays();
                        });
                    }
                    catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                        or ObjectDisposedException or InvalidOperationException)
                    {
                        Post(() =>
                        {
                            _rpBusy.Remove(offer.Id);
                            RedrawReplays();
                            Import.Popup?.Inform("couldn't delete that replay.\n\n" + Problem(ex));
                        });
                    }
                });
            }
            if (Import.Popup == null) Drop();
            else Import.Popup.Ask($"delete this shared replay without watching it?\n\n{OfferMode(offer)} - {offer.Arena.ToLowerInvariant()} from {from}", yes =>
            {
                if (yes) Drop();
            });
        }

        // ---- SEND TO FRIEND ----

        // the friends, online ones first
        private List<PlayerCard> PickFriends() => (_roster?.Friends ?? new List<PlayerCard>())
            .OrderBy(p => p.Presence == Presence.Offline).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        private int PickPages => Math.Max(1, (PickFriends().Count + PickRows - 1) / PickRows);

        private void EnterSharePick(MenuEngine menu)
        {
            if (_lastPage == s_detailPage)
            {
                _spOffset = 0;
                _spChosen.Clear();
            }
            _rpConnect = true; // (the friends as they are now)
            ShowPick(menu, 0);
        }

        private void ShowPick(MenuEngine menu, int focus)
        {
            List<PlayerCard> friends = PickFriends();
            if (_spOffset >= friends.Count) _spOffset = 0;
            int shown = Math.Max(0, Math.Min(PickRows, friends.Count - _spOffset));
            int had = Array.FindIndex(_spRow, i => menu.ItemState(i) == MenuState.Focused);
            if (had >= 0) focus = had;
            bool sendFocused = menu.ItemState(_spSend) == MenuState.Focused;
            for (int r = 0; r < PickRows; r++)
            {
                menu.SetState(_spRow[r], r >= shown ? MenuState.Hidden
                    : r == Math.Clamp(focus, 0, shown - 1) && !sendFocused ? MenuState.Focused : MenuState.Idle);
            }
            menu.SetState(_spSend, _spChosen.Count == 0 || _spSending ? MenuState.State5 : sendFocused ? MenuState.Focused : MenuState.Idle);
            _strings.Set(_spTitleString, PickPages > 1 ? $"SEND TO FRIEND {_spOffset / PickRows + 1}/{PickPages}" : "SEND TO FRIEND");
        }

        private void Pick(MenuEngine menu, int row)
        {
            List<PlayerCard> friends = PickFriends();
            int at = _spOffset + row;
            if (at >= friends.Count) return;
            string code = friends[at].Code;
            if (!_spChosen.Remove(code))
            {
                if (_spChosen.Count >= ReplayMailRules.MaxRecipients)
                {
                    Import.Popup?.Inform($"a replay goes to {ReplayMailRules.MaxRecipients} friends at most.");
                    return;
                }
                _spChosen.Add(code);
            }
            ShowPick(menu, row);
        }

        private void Send(MenuEngine menu)
        {
            if (_spChosen.Count == 0 || _spSending || _spPath == null) return;
            OnlineClient? client = ShareClient;
            if (client == null)
            {
                Import.Popup?.Inform("can't reach the server right now.\n\ntry again in a moment.");
                return;
            }
            string path = _spPath;
            List<string> codes = _spChosen.ToList();
            Dictionary<string, string> names = PickFriends().ToDictionary(p => p.Code, p => StreamerPrivacy.Display(p.Name, p.Tag).ToLowerInvariant());
            string Who(string code) => names.TryGetValue(code, out string? n) ? n : "a friend";
            _spSending = true;
            ShowPick(menu, 0);
            System.Threading.Tasks.Task.Run(async () =>
            {
                string text;
                bool sent = false;
                try
                {
                    ReplaySendMsg m = await SharedReplays.SendAsync(client, ServerUri, path, codes);
                    sent = m.Sent.Count > 0;
                    var lines = new List<string>();
                    if (m.Sent.Count > 0) lines.Add("sent to " + string.Join(", ", m.Sent.Select(Who)) + ".");
                    foreach (ReplayRefusal r in m.Refused) lines.Add(SharedReplays.Why(r, Who(r.Code)) + ".");
                    text = lines.Count > 0 ? string.Join("\n", lines) : "nobody got it.";
                }
                catch (Exception ex) when (ex is OnlineException or IOException or UnauthorizedAccessException
                    or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    text = "couldn't send that replay.\n\n" + Problem(ex);
                }
                Post(() =>
                {
                    _spSending = false;
                    if (sent) _spChosen.Clear();
                    RedrawReplays();
                    Import.Popup?.Inform(text, () =>
                    {
                        if (sent && _rpMenu?.Page?.Index == s_sharePickPage) _rpMenu.GoTo(s_detailPage);
                    });
                });
            });
        }

        private void DrawSharePick(Dictionary<int, List<WidgetTri>> glows)
        {
            List<PlayerCard> friends = PickFriends();
            int shown = Math.Max(0, Math.Min(PickRows, friends.Count - _spOffset));
            for (int r = 0; r < shown; r++)
            {
                PlayerCard p = friends[_spOffset + r];
                int tag = _spRow[r];
                float top = PickTop0 - PickPitch * r;
                bool on = _spChosen.Contains(p.Code);
                Fill(BarX0, top, BarX1, top - SharePickH, 0, 0, 0, 0.35f, tag);
                Box(BarX0, top, BarX1, top - SharePickH, tag);
                Glow(glows, tag, BarX0, top, BarX1, top - SharePickH, tag);
                // the check box: the setup's box, filled green when picked
                float bx = BarX0 + 4, bt = top - (SharePickH - PickBox) / 2;
                Fill(bx, bt, bx + PickBox, bt - PickBox, 0, 0, 0, 0.5f, tag);
                if (on) Fill(bx + 2, bt - 2, bx + PickBox - 2, bt - PickBox + 2, Green.R, Green.G, Green.B, 1, tag);
                Box(bx, bt, bx + PickBox, bt - PickBox, tag);
                FitText(StreamerPrivacy.Display(p.Name, p.Tag).ToLowerInvariant(), bx + PickBox + 6, top - 4.6f, 0, 170, on ? White : Orange, tag);
                (string word, (float R, float G, float B) colour) = p.Presence switch
                {
                    Presence.Lobby => ("in lobby", Cyan),
                    Presence.Match => ("in match", Orange),
                    Presence.Online => ("online", Green),
                    _ => ("offline", OnGrey),
                };
                Text(word, BarX1 - 4, top - 4.6f, 1, colour, tag);
            }
            if (PickPages > 1)
            {
                Arrow(false, 128 - ArrowGap - ArrowW, ArrowTop, _spPrev);
                Arrow(true, 128 + ArrowGap, ArrowTop, _spNext);
            }
            string? status = _connecting ? "connecting..." : ShareClient == null ? "offline: can't reach the server."
                : _roster == null ? "getting your friends..." : friends.Count == 0 ? "no friends yet: add them in online play > friends." : null;
            if (status != null) Text(status, 128, shown == 0 ? -70 : PickTop0 - PickPitch * PickRows - 6, 2, Pale, _spTitle);
            Text($"{_spChosen.Count} of {ReplayMailRules.MaxRecipients} picked", SendX0 - 6, CornerTop - 2.5f, 1, Pale, _spSend);
            bool live = _spChosen.Count > 0 && !_spSending;
            Fill(SendX0, CornerTop, SendX1, CornerBottom, 0, 0, 0, 0.43f, _spSend);
            Box(SendX0, CornerTop, SendX1, CornerBottom, _spSend);
            Glow(glows, _spSend, SendX0, CornerTop, SendX1, CornerBottom, _spSend);
            Text(_spSending ? "sending..." : "send", (SendX0 + SendX1) / 2, CornerTop - 2.5f, 2, live ? Green : Pale, _spSend);
        }

        // a replay's page: SEND TO FRIEND in the corner; a shared one's sender where FAVORITE and RENAME are
        private void DrawDetailShare(ReplayInfo r, Dictionary<int, List<WidgetTri>> glows)
        {
            Fill(SendX0, CornerTop, SendX1, CornerBottom, 0, 0, 0, 0.43f, _dSend);
            Box(SendX0, CornerTop, SendX1, CornerBottom, _dSend);
            Glow(glows, _dSend, SendX0, CornerTop, SendX1, CornerBottom, _dSend);
            FitText("send to friend", (SendX0 + SendX1) / 2, CornerTop - 2.5f, 2, SendX1 - SendX0 - 4, Orange, _dSend);
            if (SharedWho(r) is string who)
            {
                Text("from", FactsX, ButtonTop - 2.65f, 0, Pale, _dPlay);
                FitText(who, FactsX + 26, ButtonTop - 2.65f, 0, 2 * ButtonPitch - 26, White, _dPlay);
            }
        }

        // ---- every frame, and after the server's answers ----

        // RecompMenus.TickReplays: connect while a REPLAYS page is open (as ONLINE PLAY's pages do; only once this device has
        // been online: the REPLAYS pages make no identity), fetch the offers when SHARED WITH ME opens
        private void TickReplayShare()
        {
            int page = _rpMenu?.Page?.Index ?? -1;
            bool open = page == s_listPage || page == s_detailPage || page == s_sharePickPage;
            // the static event holds this menu only while its pages are open
            if (open && _rpChanged == null) SharedReplays.Changed += _rpChanged = () => Post(RedrawReplays);
            else if (!open && _rpChanged != null)
            {
                SharedReplays.Changed -= _rpChanged;
                _rpChanged = null;
            }
            if (!open) return;
            if (_connecting || _joining || _creating || _hosting != null || _previewMe != null) return;
            if (ShareClient is OnlineClient client)
            {
                if (_rpFetch)
                {
                    _rpFetch = false;
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            if (ReplayMail.Supported(client)) await SharedReplays.RefreshAsync(client);
                        }
                        catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                            or ObjectDisposedException or InvalidOperationException)
                        {
                            Console.Error.WriteLine("shared replays: " + ex.Message);
                        }
                    });
                }
                if (_rpConnect)
                {
                    _rpConnect = false;
                    RefreshOnline(lists: true); // (the roster: the picker's friends)
                }
                return;
            }
            string? dir = ProfileDir;
            if (dir == null || !File.Exists(Path.Combine(dir, OnlineIdentity.FileName))) return;
            if (!_rpConnect && Environment.TickCount64 < _retryAt) return;
            // (a fresh login fetches the offers and the roster itself: OnlineMenus.RefreshOnline)
            _rpConnect = _rpFetch = false;
            RefreshOnline(lists: true);
        }

        // the items for what's known now (OnlineMenus.Redraw and SharedReplays.Changed)
        private void RedrawReplays()
        {
            MenuEngine? menu = _rpMenu;
            int page = menu?.Page?.Index ?? -1;
            if (menu == null) return;
            if (page == s_listPage)
            {
                BuildListed();
                ReshowReplayList();
            }
            else if (page == s_sharePickPage) ShowPick(menu, 0);
        }
    }
}
