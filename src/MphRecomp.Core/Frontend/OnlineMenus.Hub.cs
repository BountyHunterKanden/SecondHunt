using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Online;
using MphRecomp.Config;

namespace MphRecomp.Frontend
{
    // The Gaming Hub's screens (server 0.10.0, docs/GAMING_HUB.md; HubReq / LeadersReq / StatsReq): GAMING HUB from the
    // online hub's frame button (each board's #1, the favourite hunters / weapons / modes / arenas, who played lately, the
    // totals), LEADERBOARDS (one board's top 10, all / friends / rivals, your own place), and the HUNTER LICENSE's STATS and
    // FAVOURITES views (tabs over the card). An older server has none of it: the button and the tabs hide.
    public sealed partial class RecompMenus
    {
        private const int HubGamingCall = 2400, GhBoardCall = 2410, GhRecentCall = 2420, GhUpCall = 2430, GhDownCall = 2431;
        private const int LdPrevCall = 2440, LdNextCall = 2441, LdBoardCall = 2442, LdKeyPrevCall = 2443, LdKeyNextCall = 2444;
        private const int LdScopeCall = 2446, LdRowCall = 2450, LdMineCall = 2460, LicViewCall = 2470, LdCardCall = 2480;
        private const int GhAllCall = 2418, GhMineCall = 2419;
        private const int HubFirstCall = 2400, HubLastCall = 2499;
        private const int GhRows = 6, LicViews = 3;
        private const float GhPitch = 92;

        // the GAMING HUB button in the online hub's frame, left of SETTINGS (menu units, as SETTINGS')
        private const float GhButtonX0 = 318, GhButtonX1 = 392;

        private static int s_onGHub = -1, s_onLeaders = -1;
        private static bool IsHubPage(int page) => page >= 0 && (page == s_onGHub || page == s_onLeaders);

        // the server has the Gaming Hub (the PC previews: unless told it hasn't)
        private bool HubOn => _previewMe != null ? !PreviewNoHub : SampleHub || Conn?.ServerAtLeast(HubReq.SinceServer) == true;

        private int _hGaming = -1, _ghPanel = -1, _ghSide = -1, _ghTitle = -1, _ghUp = -1, _ghDown = -1, _ghAll = -1, _ghMine = -1;
        private readonly int[] _ghBoard = Unset(8), _ghRecent = Unset(GhRows);
        private int _ldPanel = -1, _ldTitle = -1, _ldPrev = -1, _ldBoard = -1, _ldNext = -1, _ldMine = -1;
        private readonly int[] _ldScope = Unset(3), _ldRow = Unset(LeadersReq.Top), _liView = Unset(LicViews), _ldCard = Unset(8);

        private HubMsg? _hub;
        private bool _hubLoading;
        private string? _hubError;
        private int _ghOffset;

        // the board LEADERBOARDS shows (kept between visits), each board's last answer (no flicker on a switch; refreshed
        // every time)
        private static string s_ldCat = HubCategory.Points, s_ldScope = LeadersReq.All;
        private readonly Dictionary<string, LeadersMsg> _ldCache = new();
        private bool _ldLoading;
        private string? _ldError;
        // ALL BOARDS: the eight boards' top 5 side by side (the board picker's first stop; a card opens its board)
        private const string LdAll = "all";
        private static readonly string[] LdOrder = new[] { LdAll }.Concat(HubCategory.All).ToArray();
        private static bool LdAllView => s_ldCat == LdAll;
        private const int LdCardRows = 5;
        private static string[] LdScopes => ShowRivals ? new[] { LeadersReq.All, LeadersReq.Friends, LeadersReq.Rivals }
            : new[] { LeadersReq.All, LeadersReq.Friends };

        // the license on screen's STATS / FAVOURITES (0 the card, 1 stats, 2 favourites)
        private StatsMsg? _stats;
        private string? _statsFor, _statsError;
        private string? _statsAsking; // (the code asked for now: one question at a time per hunter)
        private int _licView;

        // MphRead.Tools -onlinemenu: sample answers instead of the server's
        private HubMsg? _previewHub;
        private Dictionary<string, LeadersMsg>? _previewLeaders;
        private StatsMsg? _previewStats;
        internal bool PreviewNoHub { get; set; }

        internal void OnlinePreviewHub(HubMsg hub, Dictionary<string, LeadersMsg> leaders, StatsMsg stats)
        {
            _previewHub = hub;
            _previewLeaders = leaders;
            _previewStats = stats;
        }

        // dev builds only: a sample_hub.txt beside the settings file fills GAMING HUB, LEADERBOARDS and the licenses' STATS /
        // FAVOURITES with HubSample's made-up answers instead of the server's, on any server (the made-up hunters' licenses
        // too). Client-side only, as fake_friends.txt.
        private bool SampleHub => !RecompSettings.PublicBuild && _previewMe == null && ProfileDir is string dir
            && System.IO.File.Exists(System.IO.Path.Combine(dir, "sample_hub.txt"));
        private (HubMsg Hub, Dictionary<string, LeadersMsg> Leaders, StatsMsg Stats)? _sampleHub;
        private (HubMsg Hub, Dictionary<string, LeadersMsg> Leaders, StatsMsg Stats) SampleData()
        {
            PlayerCard? me = MeCard;
            if (_sampleHub is not { } d || d.Stats.Code != (me?.Code ?? ""))
                _sampleHub = d = HubSample.Build(me?.Code ?? "", me?.Name ?? "SAMUS", me?.Tag ?? 815);
            return d;
        }

        internal (int Hub, int Leaders, int Button) OnlineHubPages => (s_onGHub, s_onLeaders, _hGaming);
        internal int OnlineHubMine => _ghMine;
        internal (int View, string Category, string Scope, int Offset) OnlineHubView => (_licView, s_ldCat, s_ldScope, _ghOffset);
        // (the live test, -onlinehubtest: what the server answered, and any error shown)
        internal (HubMsg? Hub, LeadersMsg? Board, StatsMsg? Stats, string? Error) OnlineHubData
            => (_hub, Board, _stats, _hubError ?? _ldError ?? _statsError);

        private static string LdKey(string cat, string scope) => cat + "/" + scope;
        private LeadersMsg? Board => _ldCache.TryGetValue(LdKey(s_ldCat, s_ldScope), out LeadersMsg? m) ? m : null;

        // ---- install ----

        private void InstallHub(Func<int, MenuAction[], MenuPage> addFramePage, Func<int, float, float, float, float, int, MenuItem> input,
            Func<int, float, float, MenuItem> panelAt, Action<MenuPage, int> back, MenuItem title, MenuPage hub)
        {
            MenuItem Px(MenuPage page, float x, float y, float w, float h, int call) => page.AddItem(i => input(i, Mx(x), My(y), Mx(x + w), My(y + h), call));
            _hGaming = hub.AddItem(i => input(i, GhButtonX0, StButtonTop + 1, GhButtonX1, StButtonBottom - 1, HubGamingCall)).Index;

            // ---- GAMING HUB: the boards and favourites left, online lately right ----
            MenuPage gh = addFramePage(s_onHub, Array.Empty<MenuAction>());
            s_onGHub = gh.Index;
            _ghPanel = gh.AddItem(i => panelAt(i, Mx(70), Mx(1270))).Index;
            _ghSide = gh.AddItem(i => panelAt(i, Mx(1310), Mx(1850))).Index;
            back(gh, s_onHub);
            _ghTitle = gh.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("HUNTER HUB"))).Index;
            _ghAll = Px(gh, 74, 200, 1192, 38, GhAllCall).Index; // (the leaderboards header: ALL BOARDS)
            for (int b = 0; b < _ghBoard.Length; b++)
            {
                (float x, float y) = GhBoardSpot(b);
                _ghBoard[b] = Px(gh, x, y, 560, 64, GhBoardCall + b).Index;
            }
            for (int r = 0; r < GhRows; r++) _ghRecent[r] = Px(gh, 1330, GhRowY(r), FrRowX1 - 1330, 84, GhRecentCall + r).Index;
            _ghUp = Px(gh, FrScrollX0, GhRowY(0), FrScrollX1 - FrScrollX0, FrArrowH, GhUpCall).Index;
            _ghDown = Px(gh, FrScrollX0, GhListBottom - FrArrowH, FrScrollX1 - FrScrollX0, FrArrowH, GhDownCall).Index;
            _ghMine = Px(gh, GhMineX0, GhMineY0, GhMineX1 - GhMineX0, GhMineY1 - GhMineY0, GhMineCall).Index;

            // ---- LEADERBOARDS: the board picker, the scope tabs, the top 10 and your own place ----
            MenuPage ld = addFramePage(gh.Index, new[] { Call(MenuKeys.R, LdKeyNextCall), Call(MenuKeys.L, LdKeyPrevCall) });
            s_onLeaders = ld.Index;
            _ldPanel = ld.AddItem(i => panelAt(i, Mx(70), Mx(1850))).Index;
            back(ld, gh.Index);
            _ldTitle = ld.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("LEADERBOARDS"))).Index;
            _ldPrev = Px(ld, 100, 214, 80, 66, LdPrevCall).Index;
            _ldBoard = Px(ld, 196, 214, 804, 66, LdBoardCall).Index;
            _ldNext = Px(ld, 1016, 214, 80, 66, LdNextCall).Index;
            for (int s = 0; s < _ldScope.Length; s++) _ldScope[s] = Px(ld, LdScopeX(s), 214, 220, 66, LdScopeCall + s).Index;
            for (int r = 0; r < _ldRow.Length; r++) _ldRow[r] = Px(ld, 100, LdRowY(r), 1720, 44, LdRowCall + r).Index;
            _ldMine = Px(ld, 100, 846, 1720, 44, LdMineCall).Index;
            for (int c = 0; c < _ldCard.Length; c++)
            {
                (float x, float y) = LdCardSpot(c);
                _ldCard[c] = Px(ld, x, y, LdCardW, LdCardH, LdCardCall + c).Index;
            }

            // ---- the license's CARD / STATS / FAVOURITES tabs over the card (after its items: they win its taps) ----
            MenuPage lic = _file.Pages[s_onLicense];
            for (int v = 0; v < LicViews; v++) _liView[v] = Px(lic, LicViewX(v), 214, 330, 60, LicViewCall + v).Index;
        }

        private void ApplyHubSounds(Action<int, int, int, int> like)
        {
            like(s_onHub, _hGaming, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int b in _ghBoard.Append(_ghAll).Append(_ghMine)) like(s_onGHub, b, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int r in _ghRecent) like(s_onGHub, r, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int a in new[] { _ghUp, _ghDown }) like(s_onGHub, a, OptionBoxPage, _soundToggle);
            foreach (int a in new[] { _ldPrev, _ldBoard, _ldNext }) like(s_onLeaders, a, OptionBoxPage, _soundToggle);
            foreach (int s in _ldScope) like(s_onLeaders, s, OptionBoxPage, _soundToggle);
            foreach (int r in _ldRow.Append(_ldMine).Concat(_ldCard)) like(s_onLeaders, r, MultiplayerMenu.CreateJoinPage, _soundListRow);
            foreach (int v in _liView) like(s_onLicense, v, OptionBoxPage, _soundToggle);
        }

        // where things sit (mockup px)
        private static (float X, float Y) GhBoardSpot(int b) => (100 + b % 2 * 580, 252 + 72 * (b / 2));
        private static float GhRowY(int r) => 256 + GhPitch * r;
        private static float GhListBottom => GhRowY(GhRows) - 8;
        // MY LICENSE, bottom right under online lately (owner 2026-11-07)
        private const float GhMineX0 = 1530, GhMineX1 = 1850, GhMineY0 = 940, GhMineY1 = 1010;
        private static float LdRowY(int r) => 330 + 50 * r;
        private const float LdCardW = 420, LdCardH = 290;
        private static (float X, float Y) LdCardSpot(int c) => (100 + c % 4 * 430, 298 + c / 4 * 300);
        private static float LdScopeX(int s) => 1480 - (LdScopes.Length * 220 + (LdScopes.Length - 1) * 10) / 2f + s * 230;
        private static float LicViewX(int v) => 100 + 345 * v;

        // ---- showing ----

        private void ShowGamingHub(MenuEngine menu, bool focus)
        {
            bool data = _hub != null;
            List<LeaderRow> recent = _hub?.Recent ?? new List<LeaderRow>();
            _ghOffset = Math.Clamp(_ghOffset, 0, Math.Max(0, recent.Count - GhRows));
            int count = Math.Clamp(recent.Count - _ghOffset, 0, GhRows);
            int lost = -2;
            void Keep(int item, bool on, int r)
            {
                if (!on && item >= 0 && menu.ItemState(item) == MenuState.Focused) lost = r;
                Show(menu, item, on);
            }
            for (int b = 0; b < _ghBoard.Length; b++) Keep(_ghBoard[b], data, -1);
            Keep(_ghAll, data, -1);
            for (int r = 0; r < GhRows; r++) Keep(_ghRecent[r], r < count, r);
            Keep(_ghUp, recent.Count > GhRows, -1);
            Keep(_ghDown, recent.Count > GhRows, -1);
            bool mine = MeCard != null;
            Keep(_ghMine, mine, -1);
            // (back from your own license: on MY LICENSE)
            if (focus && mine && _lastPage == s_onLicense && _licCard?.Code == MeCard?.Code)
            {
                menu.Focus(_ghMine);
                return;
            }
            if (!data)
            {
                if (mine && (focus || menu.FocusedItem() < 0)) menu.Focus(_ghMine);
                return;
            }
            if (lost >= 0 && count > 0) menu.Focus(_ghRecent[Math.Min(lost, count - 1)]);
            else if (focus || lost == -1 || menu.FocusedItem() < 0)
            {
                // (back from LEADERBOARDS: on the board it showed, ALL BOARDS' header)
                int b = Array.IndexOf(HubCategory.All, s_ldCat);
                menu.Focus(focus && _lastPage == s_onLeaders ? b >= 0 ? _ghBoard[b] : _ghAll : _ghBoard[0]);
            }
        }

        private void ShowLeaders(MenuEngine menu, bool focus)
        {
            LeadersMsg? m = Board;
            bool all = LdAllView;
            int rows = all ? 0 : Math.Min(m?.Rows.Count ?? 0, _ldRow.Length);
            int lost = -2;
            void Keep(int item, bool on, int r)
            {
                if (!on && item >= 0 && menu.ItemState(item) == MenuState.Focused) lost = r;
                Show(menu, item, on);
            }
            for (int s = 0; s < _ldScope.Length; s++) Keep(_ldScope[s], s < LdScopes.Length, -1);
            for (int r = 0; r < _ldRow.Length; r++) Keep(_ldRow[r], r < rows, r);
            Keep(_ldMine, !all && MineBelow(m) != null, -1);
            for (int c = 0; c < _ldCard.Length; c++) Keep(_ldCard[c], all, -1);
            if (lost >= 0 && rows > 0) menu.Focus(_ldRow[Math.Min(lost, rows - 1)]);
            else if (focus || lost == -1 || menu.FocusedItem() < 0) menu.Focus(_ldBoard);
        }

        // your own row when it's not in the top 10
        private LeaderRow? MineBelow(LeadersMsg? m) => m?.Mine is LeaderRow mine && !m.Rows.Any(r => r.Code == mine.Code) ? mine : null;

        // the license's view tabs (with the hub only)
        private void ShowLicenseViews(MenuEngine menu)
        {
            bool on = HubOn;
            if (!on) _licView = 0;
            foreach (int v in _liView) Show(menu, v, on);
        }

        // ---- fetching (as FetchLicense) ----

        // the page on screen's answer again (the connection's back)
        private void FetchHubPage()
        {
            int? page = _onlineMenu?.Page?.Index;
            if (page == s_onGHub) FetchHub();
            else if (page == s_onLeaders) FetchLeaders();
            else if (page == s_onLicense && _licView > 0) FetchStats();
        }

        private bool HubConn(out OnlineClient? client)
        {
            client = Conn;
            if (client != null && client.Connected) return true;
            if (_hosting == null && _joined == null && !_connecting) RefreshOnline(lists: true);
            return false;
        }

        private void FetchHub()
        {
            if (_previewMe != null || SampleHub)
            {
                _hub = _previewMe != null ? _previewHub : SampleData().Hub;
                return;
            }
            if (_hubLoading || !HubConn(out OnlineClient? client)) return;
            _hubLoading = true;
            Task.Run(async () =>
            {
                try
                {
                    HubMsg m = await client!.RequestAsync<HubMsg>(new HubReq());
                    Post(() =>
                    {
                        _hub = m;
                        _hubError = null;
                        _hubLoading = false;
                        if (_onlineMenu?.Page?.Index == s_onGHub) ShowGamingHub(_onlineMenu, false);
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _hubError = Problem(ex);
                        _hubLoading = false;
                        Redraw();
                    });
                }
            });
        }

        // the board on screen (ALL BOARDS: all eight, asked at once)
        private void FetchLeaders()
        {
            if (LdAllView)
            {
                foreach (string c in HubCategory.All) FetchBoard(c, s_ldScope);
            }
            else FetchBoard(s_ldCat, s_ldScope);
        }

        // a board's answer is on screen (its own page or ALL BOARDS)
        private static bool Showing(string cat, string scope) => scope == s_ldScope && (cat == s_ldCat || LdAllView);

        private void FetchBoard(string cat, string scope)
        {
            if (_previewMe != null || SampleHub)
            {
                Dictionary<string, LeadersMsg>? boards = _previewMe != null ? _previewLeaders : SampleData().Leaders;
                if (boards != null && boards.TryGetValue(LdKey(cat, scope), out LeadersMsg? p)) _ldCache[LdKey(cat, scope)] = p;
                else if (_previewMe == null) _ldCache[LdKey(cat, scope)] = new LeadersMsg { Category = cat, Scope = scope }; // (nobody yet)
                return;
            }
            if (!HubConn(out OnlineClient? client)) return;
            _ldLoading = true;
            _ldError = null;
            Task.Run(async () =>
            {
                try
                {
                    LeadersMsg m = await client!.RequestAsync<LeadersMsg>(new LeadersReq { Category = cat, Scope = scope });
                    Post(() =>
                    {
                        _ldCache[LdKey(cat, scope)] = m;
                        if (!Showing(cat, scope)) return; // (an older board's answer: kept, not shown)
                        _ldLoading = false;
                        if (_onlineMenu?.Page?.Index == s_onLeaders) ShowLeaders(_onlineMenu, false);
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        if (!Showing(cat, scope)) return;
                        _ldError = Problem(ex);
                        _ldLoading = false;
                        Redraw();
                    });
                }
            });
        }

        private void FetchStats()
        {
            PlayerCard? c = _licCard;
            if (c == null) return;
            string code = c.Code;
            if (_statsFor != code)
            {
                _stats = null;
                _statsFor = code;
                _statsError = null;
            }
            if (_previewMe != null)
            {
                _stats = _previewStats;
                return;
            }
            if (SampleHub)
            {
                StatsMsg mine = SampleData().Stats;
                _stats = code == MeCard?.Code ? mine : HubSample.StatsFor(c, mine);
                return;
            }
            if (!HubConn(out OnlineClient? client)) return;
            if (_statsAsking == code) return;
            _statsAsking = code;
            string ask = code == MeCard?.Code ? "" : code; // ("" = the asker)
            Task.Run(async () =>
            {
                try
                {
                    StatsMsg m = await client!.RequestAsync<StatsMsg>(new StatsReq { Code = ask });
                    Post(() =>
                    {
                        if (_statsAsking == code) _statsAsking = null;
                        if (_statsFor != code) return;
                        _stats = m;
                        _statsError = null;
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        if (_statsAsking == code) _statsAsking = null;
                        if (_statsFor != code) return;
                        _statsError = Problem(ex);
                        Redraw();
                    });
                }
            });
        }

        // ---- calls ----

        private static PlayerCard Card(LeaderRow r) => new()
        {
            Code = r.Code, Name = r.Name, Tag = r.Tag, License = HubSample.IsSample(r.Code) ? HubSample.License(r.Name) : null,
        };

        private void HubCall(MenuEngine menu, int call)
        {
            switch (call)
            {
            case HubGamingCall:
                if (HubOn) menu.GoTo(s_onGHub);
                return;
            case >= GhBoardCall and < GhBoardCall + 8:
                s_ldCat = HubCategory.All[call - GhBoardCall];
                menu.GoTo(s_onLeaders);
                return;
            case GhAllCall:
                s_ldCat = LdAll;
                menu.GoTo(s_onLeaders);
                return;
            case GhMineCall:
                // your own card, on its CARD tab (STATS and FAVOURITES beside it; no online matches yet: they say so)
                if (MeCard is PlayerCard me)
                {
                    OpenLicense(menu, me);
                    _licView = 0;
                }
                return;
            case >= LdCardCall and < LdCardCall + 8:
                if (!LdAllView) return;
                s_ldCat = HubCategory.All[call - LdCardCall];
                _ldError = null;
                FetchLeaders();
                ShowLeaders(menu, false);
                return;
            case >= GhRecentCall and < GhRecentCall + GhRows:
                if (_hub != null && _ghOffset + call - GhRecentCall < _hub.Recent.Count) OpenLicense(menu, Card(_hub.Recent[_ghOffset + call - GhRecentCall]));
                return;
            case GhUpCall:
            case GhDownCall:
                HubScrollTo(menu, _ghOffset + (call == GhUpCall ? -1 : 1));
                return;
            case LdPrevCall:
            case LdNextCall:
            case LdBoardCall:
                LeadersStep(menu, call == LdPrevCall ? -1 : 1);
                return;
            case LdKeyPrevCall:
            case LdKeyNextCall:
                LeadersStep(menu, call == LdKeyPrevCall ? -1 : 1);
                KeyClick(menu, call == LdKeyPrevCall ? _ldPrev : _ldNext);
                return;
            case >= LdScopeCall and < LdScopeCall + 3:
                if (call - LdScopeCall < LdScopes.Length && s_ldScope != LdScopes[call - LdScopeCall])
                {
                    s_ldScope = LdScopes[call - LdScopeCall];
                    FetchLeaders();
                    ShowLeaders(menu, false);
                }
                return;
            case >= LdRowCall and < LdRowCall + LeadersReq.Top:
                if (Board is LeadersMsg m && call - LdRowCall < m.Rows.Count) OpenLicense(menu, Card(m.Rows[call - LdRowCall]));
                return;
            case LdMineCall:
                if (MineBelow(Board) is LeaderRow mine) OpenLicense(menu, Card(mine));
                return;
            case >= LicViewCall and < LicViewCall + LicViews:
                _licView = HubOn ? call - LicViewCall : 0;
                if (_licView > 0) FetchStats();
                return;
            }
        }

        private void LeadersStep(MenuEngine menu, int d)
        {
            int n = LdOrder.Length;
            s_ldCat = LdOrder[(Math.Max(0, Array.IndexOf(LdOrder, s_ldCat)) + d + n) % n];
            _ldError = null;
            FetchLeaders();
            ShowLeaders(menu, false);
        }

        // L / R on the license: the card's tabs, then STATS and FAVOURITES (with the hub)
        private void LicenseStep(int d)
        {
            int n = HubOn ? CardTabs + LicViews - 1 : CardTabs;
            int at = _licView > 0 ? CardTabs + _licView - 1 : _licTab;
            at = (at + d + n) % n;
            if (at < CardTabs)
            {
                _licView = 0;
                _licTab = at;
            }
            else
            {
                _licView = at - CardTabs + 1;
                FetchStats();
            }
        }

        // a license just opened: from the hub's pages it opens on STATS
        private void OpenedLicense(int from)
        {
            if (!HubOn) _licView = 0;
            else if (from == s_onGHub || from == s_onLeaders) _licView = 1;
            if (_licView > 0) FetchStats();
        }

        // ---- the online lately list's scrolling (as the ALL FRIENDS lists': SectionScroll.cs, FrontendSession) ----

        public bool HubScrolls(MenuEngine menu) => s_onGHub >= 0 && menu.Page?.Index == s_onGHub && (_hub?.Recent.Count ?? 0) > GhRows;

        public bool HubScrollBy(MenuEngine menu, int rows)
        {
            if (!HubScrolls(menu)) return false;
            if (rows != 0) HubScrollTo(menu, _ghOffset + rows);
            return true;
        }

        private void HubScrollTo(MenuEngine menu, int to)
        {
            int was = _ghOffset;
            _ghOffset = to;
            ShowGamingHub(menu, false);
            if (_ghOffset != was) Redraw();
        }

        private int HubGrab(MenuEngine menu, float x, float y)
        {
            int count = _hub?.Recent.Count ?? 0;
            float top = GhRowY(0), bottom = GhListBottom;
            if (count <= GhRows || y > My(top) || y < My(bottom)) return 0;
            if (x >= Mx(1330) && x <= Mx(FrRowX1)) _dragKind = 6;
            else if (x >= Mx(FrScrollX0 - 8) && x <= Mx(FrScrollX1 + 8) && y < My(top + FrArrowH) && y > My(bottom - FrArrowH)) _dragKind = 7;
            else return 0;
            _dragY0 = y;
            _dragScroll0 = _ghOffset;
            return _dragKind == 6 ? 1 : 2;
        }

        private void HubDrag(MenuEngine menu, float y)
        {
            if (!HubScrolls(menu)) return;
            if (_dragKind == 6)
            {
                HubScrollTo(menu, _dragScroll0 + (int)MathF.Round((y - _dragY0) / Mw(GhPitch)));
                return;
            }
            int count = _hub!.Recent.Count;
            (float t0, float t1, float thumb, _) = FriendsBar(count, GhRows, 0, GhRowY(0), GhListBottom);
            float travel = t1 - t0 - thumb, at = (My(t0) - y) / Mw(1) - thumb / 2;
            HubScrollTo(menu, travel <= 0 ? 0 : (int)MathF.Round(at / travel * (count - GhRows)));
        }

        // the D-pad up from the first row on screen or down from the last scrolls the list a row
        public bool HubEdgeStep(MenuEngine menu, int dy)
        {
            if (dy == 0 || !HubScrolls(menu)) return false;
            int focused = menu.FocusedItem(), slot = Array.IndexOf(_ghRecent, focused);
            if (slot < 0) return false;
            int count = _hub!.Recent.Count;
            bool step = dy > 0 ? slot == 0 && _ghOffset > 0 : slot == Math.Min(GhRows, count - _ghOffset) - 1 && _ghOffset + GhRows < count;
            if (!step) return false;
            HubScrollTo(menu, _ghOffset - dy);
            menu.Host?.OnSound(menu, MenuSounds.Neutral, "hub list step");
            menu.Focus(focused);
            return true;
        }

        // ---- drawing ----

        private static string HubValue(string cat, long v) => cat == HubCategory.WinRate ? $"{v / 10}.{v % 10}%"
            : v.ToString("N0", CultureInfo.InvariantCulture);

        // ties in favourites: the game's order (unknown keys last)
        private static int KnownOrder(string cat, string key) => ContentKeys.Find(cat, key)?.Order ?? int.MaxValue;

        private static string Pct(int tenths) => $"{tenths / 10}.{tenths % 10}%";

        // a favourite's name: ContentKeys' (an arena: its in-game name, "sanctorus", where the room is known here)
        private static string FavName(string cat, string key)
        {
            if (cat == ContentKeys.Arena && MphRecomp.Multiplayer.MatchArenas.All.FirstOrDefault(r => ContentKeys.ArenaKey(r.Name) == key)
                is { InGameName.Length: > 0 } room) return room.InGameName.ToLowerInvariant();
            return ContentKeys.Display(cat, key).ToLowerInvariant();
        }

        private static string HubName(string name, int tag) => StreamerPrivacy.Display(name, tag).ToLowerInvariant();

        // the empty states' two lines (as DrawFriends')
        private void HubNote(string first, string second, float x, float y, int tag)
        {
            TextS(first, Mx(x), My(y), 2, White, tag, SizeS);
            if (second.Length > 0) TextS(Fit(second, 50), Mx(x), My(y + 60), 2, Pale, tag, SizeM);
        }

        private (string, string)? HubOffline() => !Live ? (_connecting ? "connecting..." : "can't reach the server.", _netError ?? "") : null;

        private void HubHeader(float x0, float x1, float y, string label, int tag)
        {
            Fill(Mx(x0), My(y), Mx(x1), My(y + 38), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, tag);
            TextS(label, Mx((x0 + x1) / 2), My(y + 8), 2, Dark, tag, SizeM);
        }

        // ---- ROM pictures (all in menu units) ----

        private static readonly string[] HubRankTitles = { "bounty hunter", "super hunter", "elite hunter", "master hunter", "legendary hunter" };
        private static readonly string[] HubStarModels = { "redstar", "bronzestar", "silverstar", "goldstar", "blackstar" };
        private static readonly string[] HubBeamKeys =
            { "power_beam", "volt_driver", "missile", "battlehammer", "imperialist", "judicator", "magmaul", "shock_coil", "omega_cannon" };
        private bool _hubIconsMissing;

        // the license's star for a rank level (gamerscard <colour>star's leftmost star, its black keyed out, as Star)
        private void HubRankStar(int level, float x, float top, float size, int tag)
        {
            List<WidgetTri> row = Still(@"gamerscard\" + HubStarModels[Math.Clamp(level, 0, 4)], idle: true);
            if (row.Count == 0) return;
            float left = row.Min(MinX);
            float right = row.Where(t => MinX(t) <= left + 0.01f).Max(MaxX);
            List<WidgetTri> star = row.Where(t => MinX(t) >= left - 0.01f && MaxX(t) <= right + 0.01f).ToList();
            UiTextureCache textures = _drawMenu!.Widgets.Textures;
            int source = star[0].TextureId;
            if (level >= 4)
            {
                // the black star is black with its own alpha (made for the light card): it gets a pale plate on the dark rows
                Fill(x - size * 0.06f, top, x + size * 1.06f, top - size * 1.12f, Pale.R, Pale.G, Pale.B, 0.9f, tag);
            }
            if (source >= 0)
            {
                UiTexture sharp = textures.GetOrAdd($"hub star sharp {source}", () => HubSharpStar(textures[source], level >= 4));
                for (int i = 0; i < star.Count; i++)
                {
                    WidgetTri t = star[i];
                    t.TextureId = sharp.Id;
                    star[i] = t;
                }
            }
            Fit(star, x, top, size, size, tag, 1);
        }

        // the ROM star (16 px across) sharpened for big screens: its shape measured from the texture (centre, points' and
        // valleys' radius, turn) and its colour, drawn again 16x as a clean five-pointed star with smooth edges, the black
        // around it clear. Done on the device from the imported ROM's own star; nothing new ships.
        private static (int, int, byte[]) HubSharpStar(UiTexture t, bool ownAlpha)
        {
            const int up = 16, sub = 4;
            int w = t.Width, h = t.Height;
            byte[] src = t.Rgba;
            // coverage: the star's own alpha (the black star), else brightness against the star's colour (its edge pixels are
            // the colour faded into the black)
            var cov = new float[w * h];
            var bright = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                int m = Math.Max(src[i * 4], Math.Max(src[i * 4 + 1], src[i * 4 + 2]));
                if (m > 4) bright.Add(m);
            }
            bright.Sort();
            float full = bright.Count > 0 ? bright[bright.Count * 4 / 5] : 255;
            for (int i = 0; i < w * h; i++)
            {
                cov[i] = ownAlpha ? src[i * 4 + 3] / 255f
                    : MathF.Min(1, Math.Max(src[i * 4], Math.Max(src[i * 4 + 1], src[i * 4 + 2])) / full);
            }
            float sum = 0, cx = 0, cy = 0, cr = 0, cg = 0, cb = 0, solid = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    sum += cov[i]; cx += cov[i] * (x + 0.5f); cy += cov[i] * (y + 0.5f);
                    if (cov[i] >= 0.9f)
                    {
                        cr += src[i * 4]; cg += src[i * 4 + 1]; cb += src[i * 4 + 2]; solid++;
                    }
                }
            }
            if (sum < 1 || solid < 1) return (w, h, (byte[])src.Clone());
            cx /= sum; cy /= sum;
            byte r = (byte)(cr / solid), g = (byte)(cg / solid), b = (byte)(cb / solid);
            float Cov(float fx, float fy)
            {
                fx -= 0.5f; fy -= 0.5f;
                int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
                float ax = fx - x0, ay = fy - y0;
                float At(int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : cov[y * w + x];
                return (At(x0, y0) * (1 - ax) + At(x0 + 1, y0) * ax) * (1 - ay) + (At(x0, y0 + 1) * (1 - ax) + At(x0 + 1, y0 + 1) * ax) * ay;
            }
            float Reach(float deg)
            {
                float a = deg * MathF.PI / 180, dx = MathF.Cos(a), dy = MathF.Sin(a), last = 0;
                for (float d = 0; d < w + h; d += 0.05f)
                {
                    if (Cov(cx + dx * d, cy + dy * d) >= 0.5f) last = d;
                    else if (d > last + 1) break;
                }
                return last;
            }
            // the turn whose five rays reach furthest = the points; the rays between them = the valleys
            float turn = 0, best = -1;
            for (float p = 0; p < 72; p += 0.5f)
            {
                float s = 0;
                for (int k = 0; k < 5; k++) s += Reach(p + 72 * k);
                if (s > best)
                {
                    best = s;
                    turn = p;
                }
            }
            float outer = best / 5, inner = 0;
            for (int k = 0; k < 5; k++) inner += Reach(turn + 36 + 72 * k) / 5;
            if (outer < 1 || inner <= 0 || inner >= outer) return (w, h, (byte[])src.Clone());
            // inside the star: fold the angle into a point's half-sector, then the side of the point-to-valley edge
            float c36 = MathF.Cos(MathF.PI / 5), s36 = MathF.Sin(MathF.PI / 5);
            float ex = inner * c36 - outer, ey = inner * s36;
            bool Inside(float px, float py)
            {
                float dx = px - cx, dy = py - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > outer) return false;
                float a = (MathF.Atan2(dy, dx) * 180 / MathF.PI - turn) % 72;
                if (a < 0) a += 72;
                if (a > 36) a = 72 - a;
                a *= MathF.PI / 180;
                float qx = d * MathF.Cos(a) - outer, qy = d * MathF.Sin(a);
                return ex * qy - ey * qx >= 0;
            }
            int W = w * up, H = h * up;
            var px = new byte[W * H * 4];
            int lo = Math.Max(0, (int)((cx - outer - 1) * up)), hiX = Math.Min(W, (int)((cx + outer + 1) * up));
            int loY = Math.Max(0, (int)((cy - outer - 1) * up)), hiY = Math.Min(H, (int)((cy + outer + 1) * up));
            for (int y = loY; y < hiY; y++)
            {
                for (int x = lo; x < hiX; x++)
                {
                    int n = 0;
                    for (int sy = 0; sy < sub; sy++)
                    {
                        for (int sx = 0; sx < sub; sx++)
                        {
                            if (Inside((x + (sx + 0.5f) / sub) / up, (y + (sy + 0.5f) / sub) / up)) n++;
                        }
                    }
                    if (n == 0) continue;
                    int o = (y * W + x) * 4;
                    px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = (byte)(n * 255 / (sub * sub));
                }
            }
            return (W, H, px);
        }

        // a weapon's HUD icon (hud_weaponicon's frame for the beam, 4x, from Sylux's file: the bare symbol in the beam's
        // colour, no tile (owner's pick); the Pirate Blaster's own; the Hyper Beam and Paralyzer the Power Beam's), null
        // for the alt form or when the ROM's icon can't be read
        private UiTexture? HubWeaponIcon(string key)
        {
            int beam = key is "hyper_beam" or "paralyzer" ? 0 : Array.IndexOf(HubBeamKeys, key);
            bool blaster = key == "pirate_blaster";
            if (_hubIconsMissing || beam < 0 && !blaster) return null;
            try
            {
                return _drawMenu!.Widgets.Textures.GetOrAdd("hub weapon " + key, () =>
                {
                    MphRead.Hud.HudObject obj = MphRead.Hud.HudInfo.GetHudObject("_archives/localSylux/hud_weaponicon.bin");
                    int w = obj.Width, h = obj.Height;
                    IReadOnlyList<byte> tiled;
                    IReadOnlyList<MphRead.ColorRgba> palette = obj.PaletteData;
                    if (blaster) (tiled, palette) = MphRead.Entities.PlayerEntity.PirateBlasterWeaponIcon(obj);
                    else
                    {
                        int image = beam < obj.AnimParams.Count ? obj.AnimParams[beam].ImageIndex : 0;
                        tiled = obj.CharacterData.Skip(image * w * h).Take(w * h).ToArray();
                    }
                    byte[] idx = HudPortraits.Linear(tiled, w, h);
                    const int s = 4;
                    var px = new byte[w * s * h * s * 4];
                    for (int y = 0; y < h * s; y++)
                    {
                        for (int x = 0; x < w * s; x++)
                        {
                            byte i = idx[y / s * w + x / s];
                            if (i == 0 || i >= palette.Count) continue;
                            int o = (y * w * s + x) * 4;
                            px[o] = palette[i].Red; px[o + 1] = palette[i].Green; px[o + 2] = palette[i].Blue; px[o + 3] = 255;
                        }
                    }
                    return (w * s, h * s, px);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _hubIconsMissing = true; // no ROM HUD here (the text still says which weapon)
                return null;
            }
        }

        // the HUNTER LICENSE portraits (gamerscard), by Hunter
        private static readonly string[] HubPortraits = { "samusmost", "kandenmost", "tracemost", "syluxmost", "noxmost", "spiremost", "weavelmost" };

        // a picked character's skin whose head picture stands for it, by hunter key + the licence's skin: the licence's most
        // played skin where this device has it, else the first imported one (as the match HUD's portrait); null = none here
        private readonly Dictionary<string, string?> _hubCharacterSkin = new();

        // the PC preview: a licence to show (a leaderboard row's sample player has none uploaded)
        internal void PreviewLicenseShown(HunterLicense l) => _licLicense = l;

        // a hunter key's picture, framed as the arena pictures are (ArenaPicture's orange edge): a vanilla hunter's (or a
        // Samus suit's) HUNTER LICENSE portrait; TROOPER / DARK SAMUS / the Zero Suit their rendered head picture's face
        // (CharacterPictures, the match HUD portrait's crop, bigger), black inside until it's drawn; no seat icon or
        // look-alike recolour (owner 2026-11-07). On a licence (lic) the player's most played skin (owner 2026-11-07: "the
        // most used skin for the favorite hunter dark samus or trooper ... and same for sylux vanilla or prime 4"); the
        // community page has no per-skin counts. False = nothing drawn
        private bool HubHunterIcon(string key, float x, float top, float d, int tag, HunterLicense? lic = null)
        {
            float frame = Mw(3);
            if (MphRecomp.Multiplayer.Characters.All.FirstOrDefault(c => !c.HasOriginal
                && (ContentKeys.HunterKey(c.Id, c.Base.ToString()) == key || ContentKeys.Key(c.Name) == key)) is { } c)
            {
                Fill(x, top, x + d, top - d, FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, tag);
                Fill(x + frame, top - frame, x + d - frame, top - d + frame, 0, 0, 0, 1, tag);
                if (HubCharacterPortrait(key, c, lic?.SkinOf(c.Id)) is UiTexture t)
                {
                    Quad(t.Id, x + frame, top - frame, x + d - frame, top - d + frame, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                }
                return true;
            }
            if (!Enum.TryParse(key, true, out Hunter h) || !Enum.IsDefined(h))
            {
                if (!key.Contains("samus") && !key.Contains("suit")) return false;
                h = Hunter.Samus;
            }
            if ((int)h is >= 0 and < 7)
            {
                Fill(x, top, x + d, top - d, FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, tag);
                // a skin played most on a hunter with its own look (Samus's suits, Sylux in Prime 4): its head picture where
                // this device has that skin and its picture is drawn, else the licence portrait (owner 2026-11-07)
                if (lic != null && MphRecomp.Multiplayer.Characters.All.FirstOrDefault(k => k.HasOriginal && k.Base == h) is { } own
                    && lic.SkinOf(own.Id) is string skin
                    && HubCharacterPortrait(key, own, skin, only: true) is UiTexture st)
                {
                    Fill(x + frame, top - frame, x + d - frame, top - d + frame, 0, 0, 0, 1, tag);
                    Quad(st.Id, x + frame, top - frame, x + d - frame, top - d + frame, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                    return true;
                }
                HubAspectModel(@"gamerscard\" + HubPortraits[(int)h], x + frame, top - frame, d - 2 * frame, d - 2 * frame, tag);
                return true;
            }
            return SeatIcon(new MphRecomp.Multiplayer.MatchPlayer { Hunter = h }, x, top, d, tag); // the Guardian's ROM icon
        }

        // the face square of a picked character's head picture, 192 px: HudPortraits' crop (the head square's middle
        // Zoom, box-averaged over black); null while the picture isn't drawn yet (CharacterPictures.For queues it). want: the
        // licence's most played skin, used where this device has it; only: that skin or nothing (no first-imported fallback)
        private UiTexture? HubCharacterPortrait(string key, MphRecomp.Multiplayer.Character c, string? want = null, bool only = false)
        {
            string slot = key + "/" + want + (only ? "/only" : "");
            if (!_hubCharacterSkin.TryGetValue(slot, out string? skin))
            {
                List<string> skins = MphRecomp.Multiplayer.Characters.Skins(c, HdRoot);
                skin = want != null && skins.Contains(want, StringComparer.OrdinalIgnoreCase) ? want
                    : only ? null : skins.FirstOrDefault(s => !MphRecomp.Multiplayer.Characters.IsOriginal(s));
                _hubCharacterSkin[slot] = skin;
            }
            string? pictures = MultiplayerMenu.CharacterPicturesDir ?? (ProfileDir is string dir ? CharacterPictures.Dir(dir) : null);
            if (skin == null || CharacterPictures.For(HdRoot, pictures, skin) is not { } pics) return null;
            UiTextureCache textures = _drawMenu!.Widgets.Textures;
            string id = "hub portrait " + pics.Head;
            if (textures.TryGet(id, out UiTexture? have) && have != null) return have;
            byte[] head;
            int hw, hh;
            try
            {
                head = MphRecomp.Import.PngOut.Read(pics.Head, out hw, out hh);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _hubCharacterSkin[slot] = null; // an unreadable picture: the black inside, not a retry every frame
                return null;
            }
            return textures.GetOrAdd(id, () =>
            {
                const int size = 192;
                float full = Math.Min(hw, hh), side = full * HudPortraits.Zoom, step = side / size;
                float top = hh * CharacterPictures.HeadCropTop + (full - side) / 2, left = (hw - side) / 2;
                var px = new byte[size * size * 4];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float sx = left + x * step, sy = top + y * step, r = 0, g = 0, b = 0, n = 0;
                        int ax = (int)sx, ay = (int)sy, bx = Math.Max(ax + 1, (int)(sx + step)), by = Math.Max(ay + 1, (int)(sy + step));
                        for (int py = ay; py < by; py++)
                        {
                            for (int qx = ax; qx < bx; qx++)
                            {
                                int o = (Math.Clamp(py, 0, hh - 1) * hw + Math.Clamp(qx, 0, hw - 1)) * 4;
                                float a = head[o + 3] / 255f;
                                r += head[o] * a; g += head[o + 1] * a; b += head[o + 2] * a; n++;
                            }
                        }
                        int p = (y * size + x) * 4;
                        px[p] = (byte)(r / n); px[p + 1] = (byte)(g / n); px[p + 2] = (byte)(b / n); px[p + 3] = 255;
                    }
                }
                return (size, size, px);
            });
        }

        private static string? HubModeModel(string key) => key switch
        {
            "battle" => "bigdeathmatch", "bounty" => "bounty", "capture" => "capture", "defender" => "challenge",
            "nodes" => "nodes", "prime_hunter" => "primehunter", "survival" => "survival", _ => null,
        };

        // a ROM model fitted into the box keeping its shape, centred
        private void HubAspectModel(string name, float x, float top, float w, float h, int tag, bool idle = true)
        {
            List<WidgetTri> m = Still(name, idle);
            if (m.Count == 0) return;
            float bw = m.Max(MaxX) - m.Min(MinX), bh = m.Max(MaxY) - m.Min(MinY);
            if (bw < 1e-4f || bh < 1e-4f) return;
            float scale = MathF.Min(w / bw, h / bh), fw = bw * scale, fh = bh * scale;
            Fit(m, x + (w - fw) / 2, top - (h - fh) / 2, fw, fh, tag, 1);
        }

        // RESULTS' medal for places 1-3 (false: no medal for that place)
        private bool HubMedal(int place, float x, float top, float d, int tag)
        {
            if (place is < 1 or > 3) return false;
            HubAspectModel(@"results\" + new[] { "medalgold", "medalsilver", "medalbronze" }[place - 1], x, top, d, d, tag, idle: false);
            return true;
        }

        // a favourite's picture in a size x size square: a hunter's icon, a weapon's HUD icon, a mode's icon, an arena's
        // stage picture (4:3, framed); lic: the licence shown (a hunter's picture in its most played skin)
        private void HubPicture(string cat, string key, float x, float top, float size, int tag, HunterLicense? lic = null)
        {
            switch (cat)
            {
                case ContentKeys.Hunter:
                    HubHunterIcon(key, x, top, size, tag, lic);
                    break;
                case ContentKeys.Weapon when HubWeaponIcon(key) is UiTexture t:
                    float s = MathF.Min(size / t.Width, size / t.Height), w = t.Width * s, h = t.Height * s;
                    Quad(t.Id, x + (size - w) / 2, top - (size - h) / 2, x + (size + w) / 2, top - (size + h) / 2, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                    break;
                case ContentKeys.Mode when HubModeModel(key) is string model:
                    HubAspectModel(@"multiplayer\" + model, x, top, size, size, tag);
                    break;
                case ContentKeys.Arena when MphRecomp.Multiplayer.MatchArenas.All.FirstOrDefault(r => ContentKeys.ArenaKey(r.Name) == key) is { } room:
                    float frame = Mw(3), ah = (size - 2 * frame) * 3 / 4;
                    ArenaPicture(room.Name, x + frame, top - (size - ah) / 2, size - 2 * frame, ah, frame, tag);
                    break;
            }
        }

        private void DrawGamingHubButton(Dictionary<int, List<WidgetTri>> glows)
        {
            if (_hGaming < 0 || !HubOn) return;
            Fill(GhButtonX0, StButtonTop, GhButtonX1, StButtonBottom, 0, 0, 0, 0.43f, _hGaming);
            Box(GhButtonX0, StButtonTop, GhButtonX1, StButtonBottom, _hGaming);
            Glow(glows, _hGaming, GhButtonX0, StButtonTop, GhButtonX1, StButtonBottom, _hGaming);
            const string label = "hunter hub";
            float size = MathF.Min(1, (GhButtonX1 - GhButtonX0 - 4) / MathF.Max(1, _drawMenu!.Font.Measure(label)));
            TextS(label, (GhButtonX0 + GhButtonX1) / 2, StButtonTop - (StButtonTop - StButtonBottom - 8 * size) / 2, 2, Orange, _hGaming, size);
        }

        // MY LICENSE: your own card with its STATS and FAVOURITES (drawn while the hub loads too)
        private void DrawMyLicenseButton(Dictionary<int, List<WidgetTri>> glows)
        {
            if (_ghMine < 0 || MeCard == null) return;
            int item = _ghMine;
            Fill(Mx(GhMineX0), My(GhMineY0), Mx(GhMineX1), My(GhMineY1), 0, 0, 0, 0.43f, item);
            Box(Mx(GhMineX0), My(GhMineY0), Mx(GhMineX1), My(GhMineY1), item);
            Glow(glows, item, Mx(GhMineX0), My(GhMineY0), Mx(GhMineX1), My(GhMineY1), item);
            PersonDisc(Mx(GhMineX0 + 10), My(GhMineY0 + 8), Mw(54), item);
            TextS("my license", Mx((GhMineX0 + 74 + GhMineX1) / 2), My(GhMineY0 + 22), 2, Orange, item, SizeM);
        }

        private void DrawGamingHub(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _ghTitle;
            HubHeader(74, 1266, 200, "leaderboards", tag);
            HubHeader(1314, 1846, 200, "online lately", tag);
            DrawMyLicenseButton(glows);
            if (_hub is not HubMsg hub)
            {
                (string first, string second) = HubOffline() ?? (_hubError != null ? ("can't load the hunter hub.", _hubError) : ("loading...", ""));
                HubNote(first, second, 670, 480, tag);
                return;
            }
            // the header opens all eight boards at once
            Glow(glows, _ghAll, Mx(74), My(200), Mx(1266), My(238), _ghAll);
            TextS("see all >", Mx(1250), My(208), 1, Dark, _ghAll, SizeX);
            for (int b = 0; b < _ghBoard.Length; b++)
            {
                string cat = HubCategory.All[b];
                (float x, float y) = GhBoardSpot(b);
                int item = _ghBoard[b];
                Fill(Mx(x), My(y), Mx(x + 560), My(y + 64), 0, 0, 0, 0.35f, item);
                Box(Mx(x), My(y), Mx(x + 560), My(y + 64), item);
                Glow(glows, item, Mx(x), My(y), Mx(x + 560), My(y + 64), item);
                TextS(HubCategory.Display(cat).ToLowerInvariant(), Mx(x + 16), My(y + 6), 0, Pale, item, SizeX);
                if (hub.Leaders.TryGetValue(cat, out LeaderRow? top))
                {
                    TextS(Fit(HubName(top.Name, top.Tag), 24), Mx(x + 16), My(y + 34), 0, top.Code == MeCard?.Code ? Cyan : White, item, SizeX);
                    TextS(HubValue(cat, top.Value), Mx(x + 546), My(y + 34), 1, Cyan, item, SizeX);
                }
                else TextS("--", Mx(x + 16), My(y + 34), 0, OnGrey, item, SizeX);
            }
            HubHeader(74, 1266, 548, "favourites", tag);
            (string Title, string Cat, List<ShareRow> Rows)[] favs =
            {
                ("hunters", ContentKeys.Hunter, hub.Hunters), ("weapons", ContentKeys.Weapon, hub.Weapons),
                ("modes", ContentKeys.Mode, hub.Modes), ("arenas", ContentKeys.Arena, hub.Arenas),
            };
            // each: the most played's picture, the top three with their % (of all counted matches; a weapon's: of all kills)
            for (int f = 0; f < favs.Length; f++)
            {
                float x = 100 + f % 2 * 580, y = 600 + f / 2 * 146;
                Fill(Mx(x), My(y), Mx(x + 560), My(y + 136), 0, 0, 0, 0.35f, tag);
                Box(Mx(x), My(y), Mx(x + 560), My(y + 136), tag);
                List<ShareRow> rows = favs[f].Rows;
                if (rows.Count > 0) HubPicture(favs[f].Cat, rows[0].Key, Mx(x + 12), My(y + 12), Mw(112), tag);
                TextS(favs[f].Title, Mx(x + 140), My(y + 8), 0, Orange, tag, SizeX);
                TextS(favs[f].Cat == ContentKeys.Weapon ? "% of kills" : "% of matches", Mx(x + 546), My(y + 8), 1, Pale, tag, SizeX);
                for (int k = 0; k < Math.Min(3, rows.Count); k++)
                {
                    float ty = k == 0 ? y + 38 : y + 48 + 28 * k;
                    TextS(Fit(FavName(favs[f].Cat, rows[k].Key), k == 0 ? 16 : 20), Mx(x + 140), My(ty), 0, White, tag, k == 0 ? SizeM : SizeX);
                    TextS(Pct(rows[k].Share), Mx(x + 546), My(ty), 1, k == 0 ? Cyan : Pale, tag, k == 0 ? SizeM : SizeX);
                }
                if (rows.Count == 0) TextS("--", Mx(x + 140), My(y + 38), 0, OnGrey, tag, SizeM);
            }

            // online lately (a tap opens their license), the totals under it
            List<LeaderRow> recent = hub.Recent;
            int count = Math.Clamp(recent.Count - _ghOffset, 0, GhRows);
            float x1 = recent.Count > GhRows ? FrRowX1 : 1830;
            for (int r = 0; r < count; r++)
            {
                LeaderRow row = recent[_ghOffset + r];
                int item = _ghRecent[r];
                float y = GhRowY(r);
                Fill(Mx(1330), My(y), Mx(x1), My(y + 84), 0, 0, 0, 0.35f, item);
                Box(Mx(1330), My(y), Mx(x1), My(y + 84), item);
                Glow(glows, item, Mx(1330), My(y), Mx(x1), My(y + 84), item);
                PersonDisc(Mx(1342), My(y + 8), Mw(68), item);
                TextS(Fit(HubName(row.Name, row.Tag), 18), Mx(1424), My(y + 10), 0, row.Code == MeCard?.Code ? Cyan : White, item, SizeM);
                TextS(row.Value.ToString("N0", CultureInfo.InvariantCulture) + " points", Mx(1424), My(y + 50), 0, Pale, item, SizeX);
            }
            if (count == 0) TextS("nobody yet.", Mx(1580), My(300), 2, OnGrey, tag, SizeM);
            DrawFriendsScroll(recent.Count, GhRows, _ghOffset, GhRowY(0), GhListBottom, _ghUp, _ghDown, tag, glows);
            // all-time counts (not who's on now): players with a counted online match, counted online matches
            TextS("players with stats", Mx(1340), My(818), 0, Pale, tag, SizeX);
            TextS(hub.Players.ToString("N0", CultureInfo.InvariantCulture), Mx(1830), My(818), 1, White, tag, SizeX);
            TextS("matches played", Mx(1340), My(852), 0, Pale, tag, SizeX);
            TextS(hub.Matches.ToString("N0", CultureInfo.InvariantCulture), Mx(1830), My(852), 1, White, tag, SizeX);
        }

        private void DrawLeaders(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _ldTitle;
            string cat = s_ldCat;
            bool all = LdAllView;
            // the board picker: < the board > (A on the board steps on), the scope tabs right of it
            foreach ((int item, float x, bool right) in new[] { (_ldPrev, 100f, false), (_ldNext, 1016f, true) })
            {
                Glow(glows, item, Mx(x), My(214), Mx(x + 80), My(280), item);
                Arrow(right, Mx(x + 40) - ArrowW / 2, My(247) + ArrowH / 2, item);
            }
            PxButton(196, 214, 804, 66, all ? "all boards" : HubCategory.Display(cat).ToLowerInvariant(), SizeM, _ldBoard, glows);
            string[] scopes = LdScopes;
            for (int s = 0; s < scopes.Length; s++) Tab(LdScopeX(s), 214, 220, 66, scopes[s], scopes[s] == s_ldScope, _ldScope[s], glows);
            if (all)
            {
                for (int c = 0; c < _ldCard.Length; c++) DrawLeaderCard(HubCategory.All[c], c, glows);
                if (HubOffline() is (string offFirst, string offSecond)) HubNote(offFirst, offSecond, 960, 480, tag);
                return;
            }

            TextS("place", Mx(150), My(298), 2, Pale, tag, SizeX);
            TextS("hunter", Mx(260), My(298), 0, Pale, tag, SizeX);
            string head = cat == HubCategory.WinRate ? $"win rate ({HubCategory.WinRateMinGames}+ games)" : HubCategory.Display(cat).ToLowerInvariant();
            TextS(head, Mx(1800), My(298), 1, Pale, tag, SizeX);

            LeadersMsg? m = Board;
            List<LeaderRow> rows = m?.Rows ?? new List<LeaderRow>();
            for (int r = 0; r < Math.Min(rows.Count, _ldRow.Length); r++)
            {
                bool tied = rows.Count(o => o.Place == rows[r].Place) > 1;
                DrawLeaderRow(cat, rows[r], LdRowY(r), tied, _ldRow[r], glows);
            }
            if (MineBelow(m) is LeaderRow mine)
            {
                TextS("...", Mx(150), My(LdRowY(_ldRow.Length) - 4), 2, OnGrey, tag, SizeX);
                DrawLeaderRow(cat, mine, 846, false, _ldMine, glows);
            }
            if (rows.Count == 0)
            {
                (string first, string second) = HubOffline() ?? (_ldError != null ? ("can't load this board.", _ldError)
                    : m == null || _ldLoading && m.Rows.Count == 0 ? ("loading...", "")
                    : s_ldScope == LeadersReq.Friends ? ("no friends on this board yet.", "")
                    : s_ldScope == LeadersReq.Rivals ? ("no rivals on this board yet.", "") : ("nobody on this board yet.", ""));
                HubNote(first, second, 960, 480, tag);
            }
        }

        private void DrawLeaderRow(string cat, LeaderRow row, float y, bool tied, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            bool me = row.Code == MeCard?.Code;
            Fill(Mx(100), My(y), Mx(1820), My(y + 44), 0, 0, 0, 0.35f, item);
            Box(Mx(100), My(y), Mx(1820), My(y + 44), item);
            Glow(glows, item, Mx(100), My(y), Mx(1820), My(y + 44), item);
            // the top three get vanilla's results medal with a small place beside it; the rest a plain place number
            if (HubMedal(row.Place, Mx(104), My(y + 4), Mw(36), item))
                TextS(row.Place + (tied ? "=" : ""), Mx(146), My(y + 14), 0, row.Place == 1 ? Orange : White, item, SizeX);
            else
                TextS(row.Place + (tied ? "=" : ""), Mx(166), My(y + 8), 2, White, item, SizeM);
            PersonDisc(Mx(206), My(y + 4), Mw(36), item);
            TextS(Fit(HubName(row.Name, row.Tag), 30), Mx(260), My(y + 8), 0, me ? Cyan : White, item, SizeM);
            float vx = 1800;
            if (cat == HubCategory.Points)
            {
                // the points board: the player's rank (their star points, not these points), as on the license
                int level = Math.Clamp(row.Rank, 0, HubRankTitles.Length - 1);
                HubRankStar(level, Mx(1120), My(y + 4), Mw(36), item);
                TextS(HubRankTitles[level], Mx(1166), My(y + 12), 0, Pale, item, SizeX);
            }
            TextS(HubValue(cat, row.Value), Mx(vx), My(y + 8), 1, me ? Cyan : White, item, SizeM);
        }

        // ALL BOARDS' card for one board: its top five and your place (a tap opens that board)
        private void DrawLeaderCard(string cat, int c, Dictionary<int, List<WidgetTri>> glows)
        {
            int item = _ldCard[c];
            (float x, float y) = LdCardSpot(c);
            Fill(Mx(x), My(y), Mx(x + LdCardW), My(y + LdCardH), 0, 0, 0, 0.35f, item);
            Box(Mx(x), My(y), Mx(x + LdCardW), My(y + LdCardH), item);
            Glow(glows, item, Mx(x), My(y), Mx(x + LdCardW), My(y + LdCardH), item);
            HubHeader(x + 4, x + LdCardW - 4, y + 4, HubCategory.Display(cat).ToLowerInvariant(), item);
            LeadersMsg? m = _ldCache.TryGetValue(LdKey(cat, s_ldScope), out LeadersMsg? got) ? got : null;
            List<LeaderRow> rows = m?.Rows ?? new List<LeaderRow>();
            for (int k = 0; k < Math.Min(LdCardRows, rows.Count); k++)
            {
                LeaderRow row = rows[k];
                float ty = y + 52 + 38 * k;
                bool me = row.Code == MeCard?.Code;
                if (!HubMedal(row.Place, Mx(x + 12), My(ty - 3), Mw(30), item))
                    TextS(row.Place.ToString(CultureInfo.InvariantCulture), Mx(x + 27), My(ty), 2, White, item, SizeX);
                TextS(Fit(HubName(row.Name, row.Tag), 14), Mx(x + 54), My(ty), 0, me ? Cyan : White, item, SizeX);
                TextS(HubValue(cat, row.Value), Mx(x + LdCardW - 14), My(ty), 1, me ? Cyan : White, item, SizeX);
            }
            if (rows.Count == 0)
                TextS(m == null ? (_ldError != null ? "can't load." : "loading...") : "nobody yet.", Mx(x + LdCardW / 2), My(y + 120), 2, OnGrey, item, SizeX);
            // you, under the five
            Fill(Mx(x + 8), My(y + 246), Mx(x + LdCardW - 8), My(y + 282), 0, 0, 0, 0.4f, item);
            LeaderRow? mine = rows.FirstOrDefault(r => r.Code == MeCard?.Code) ?? m?.Mine;
            TextS(mine != null ? "you  #" + mine.Place : "you  --", Mx(x + 16), My(y + 252), 0, mine != null ? Cyan : OnGrey, item, SizeX);
            if (mine != null) TextS(HubValue(cat, mine.Value), Mx(x + LdCardW - 14), My(y + 252), 1, Cyan, item, SizeX);
        }

        // the license's CARD / STATS / FAVOURITES tabs (with the hub) and the STATS or FAVOURITES view in the card's box;
        // true = a view is up (the card isn't drawn)
        private bool DrawLicenseViews(PlayerCard c, Dictionary<int, List<WidgetTri>> glows)
        {
            if (!HubOn) return false;
            string[] labels = { "card", "stats", "favourites" };
            for (int v = 0; v < LicViews; v++) Tab(LicViewX(v), 214, 330, 60, labels[v], v == _licView, _liView[v], glows);
            if (_licView == 0) return false;
            int tag = _liCard;
            StatsMsg? s = _statsFor == c.Code ? _stats : null;
            if (s == null || s.Totals.GetValueOrDefault(HubCategory.Games) == 0)
            {
                (string first, string second) = HubOffline() ?? (_statsError != null ? ("can't load these stats.", _statsError)
                    : s == null ? ("loading...", "") : ("no online matches yet.", "online matches count here."));
                HubNote(first, second, 610, 480, tag);
                return true;
            }
            if (_licView == 1) DrawLicenseStats(s, tag);
            else DrawLicenseFavourites(s, tag);
            return true;
        }

        // short names for the place chips, in HubCategory.All's order
        private static readonly string[] HubShortNames =
            { "points", "games", "win rate", "win streak", "kill streak", "alt kills", "biped kills", "headshots" };

        private static string HubTime(long secs)
        {
            long d = secs / 86400, h = secs / 3600 % 24, m = secs / 60 % 60;
            return d > 0 ? $"{d}d {h}h" : $"{h}h {m}m";
        }

        private static string HubCount(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private void DrawLicenseStats(StatsMsg s, int tag)
        {
            long Total(string cat) => s.Totals.GetValueOrDefault(cat);
            // the rank band: the rank's star and name (from the star points), the world place on the points board, the
            // Overall Points
            long points = Total(HubCategory.Points);
            int level = HubRules.Level((int)Math.Clamp(Total(HubCategory.RankPoints), 0, HubRules.MaxPoints));
            Fill(Mx(104), My(296), Mx(1116), My(396), 0, 0, 0, 0.45f, tag);
            Box(Mx(104), My(296), Mx(1116), My(396), tag);
            HubRankStar(level, Mx(118), My(300), Mw(92), tag);
            bool ranked = s.Places.TryGetValue(HubCategory.Points, out int world);
            TextS("world rank", Mx(226), My(310), 0, Pale, tag, SizeX);
            TextS(ranked ? "#" + HubCount(world) : "--", Mx(226), My(340), 0, ranked ? Cyan : OnGrey, tag, SizeS);
            TextS("rank", Mx(640), My(310), 2, Pale, tag, SizeX);
            TextS(HubRankTitles[level], Mx(640), My(344), 2, Orange, tag, SizeM);
            TextS("overall points", Mx(1100), My(310), 1, Pale, tag, SizeX);
            TextS(HubCount(points), Mx(1100), My(340), 1, White, tag, SizeS);

            // the totals
            (string Label, string Value)[] totals =
            {
                ("games", HubCount(Total(HubCategory.Games))), ("wins", HubCount(Total(HubCategory.Wins))),
                ("win rate", HubValue(HubCategory.WinRate, Total(HubCategory.WinRate))), ("time played", HubTime(Total(HubCategory.Seconds))),
                ("kills", HubCount(Total(HubCategory.Kills))), ("deaths", HubCount(Total(HubCategory.Deaths))),
                ("headshots", HubCount(Total(HubCategory.Headshots))), ("shots fired", HubCount(Total(HubCategory.Shots))),
            };
            for (int k = 0; k < totals.Length; k++)
            {
                float x = 104 + k % 4 * 254, y = 404 + k / 4 * 70;
                Fill(Mx(x), My(y), Mx(x + 250), My(y + 64), 0, 0, 0, 0.35f, tag);
                Box(Mx(x), My(y), Mx(x + 250), My(y + 64), tag);
                TextS(totals[k].Label, Mx(x + 12), My(y + 6), 0, Pale, tag, SizeX);
                TextS(totals[k].Value, Mx(x + 12), My(y + 32), 0, White, tag, SizeM);
            }

            // kills in biped form against alt form, as a bar
            long biped = Total(HubCategory.BipedKills), alt = Total(HubCategory.AltKills), both = biped + alt;
            int bipedPct = both > 0 ? (int)(biped * 1000 / both) : 0;
            TextS($"biped kills {HubCount(biped)}" + (both > 0 ? $" ({Pct(bipedPct)})" : ""), Mx(104), My(550), 0, Orange, tag, SizeX);
            TextS($"alt form kills {HubCount(alt)}" + (both > 0 ? $" ({Pct(1000 - bipedPct)})" : ""), Mx(1116), My(550), 1, Cyan, tag, SizeX);
            Fill(Mx(104), My(580), Mx(1116), My(602), 0, 0, 0, 0.5f, tag);
            if (both > 0)
            {
                float split = 104 + 1012f * biped / both;
                if (biped > 0) Fill(Mx(104), My(580), Mx(split), My(602), Orange.R, Orange.G, Orange.B, 1, tag);
                if (alt > 0) Fill(Mx(split), My(580), Mx(1116), My(602), Cyan.R, Cyan.G, Cyan.B, 1, tag);
            }
            Box(Mx(104), My(580), Mx(1116), My(602), tag);

            // the place on each board, with the value it's ranked by
            HubHeader(104, 1116, 616, "your place on each board", tag);
            for (int k = 0; k < HubCategory.All.Length; k++)
            {
                string cat = HubCategory.All[k];
                float x = 104 + k % 4 * 254, y = 662 + k / 4 * 64;
                Fill(Mx(x), My(y), Mx(x + 250), My(y + 58), 0, 0, 0, 0.35f, tag);
                Box(Mx(x), My(y), Mx(x + 250), My(y + 58), tag);
                TextS(HubShortNames[k], Mx(x + 12), My(y + 4), 0, Pale, tag, SizeX);
                bool placed = s.Places.TryGetValue(cat, out int place);
                TextS(placed ? "#" + HubCount(place) : "--", Mx(x + 12), My(y + 27), 0, placed ? Cyan : OnGrey, tag, SizeM);
                TextS(HubValue(cat, Total(cat)), Mx(x + 238), My(y + 30), 1, White, tag, SizeX);
            }
        }

        private void DrawLicenseFavourites(StatsMsg s, int tag)
        {
            (string Title, string Cat, Dictionary<string, int> Counts)[] favs =
            {
                ("favourite hunter", ContentKeys.Hunter, s.Hunters), ("favourite weapon", ContentKeys.Weapon, s.Weapons),
                ("favourite mode", ContentKeys.Mode, s.Modes), ("favourite arena", ContentKeys.Arena, s.Arenas),
            };
            // whose skins: your own licence on your card, else theirs as uploaded
            HunterLicense? lic = _licCard != null && _licCard.Code == MeCard?.Code ? Multiplayer?.License ?? _licLicense : _licLicense;
            for (int f = 0; f < favs.Length; f++)
            {
                float x = 104 + f % 2 * 509, y = 296 + f / 2 * 250;
                Fill(Mx(x), My(y), Mx(x + 503), My(y + 240), 0, 0, 0, 0.35f, tag);
                Box(Mx(x), My(y), Mx(x + 503), My(y + 240), tag);
                HubHeader(x + 4, x + 499, y + 4, favs[f].Title, tag);
                string cat = favs[f].Cat, unit = cat == ContentKeys.Weapon ? "kills" : "matches";
                long total = favs[f].Counts.Values.Sum(v => (long)v);
                List<KeyValuePair<string, int>> top = favs[f].Counts.Where(kv => kv.Value > 0)
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => KnownOrder(cat, kv.Key)).Take(3).ToList();
                if (top.Count == 0)
                {
                    TextS("--", Mx(x + 140), My(y + 54), 0, OnGrey, tag, SizeM);
                    continue;
                }
                // the favourite: its picture, name, % and how many of the total
                HubPicture(cat, top[0].Key, Mx(x + 14), My(y + 52), Mw(108), tag, lic);
                TextS(Fit(FavName(cat, top[0].Key), 17), Mx(x + 140), My(y + 52), 0, White, tag, SizeM);
                TextS(Pct((int)(top[0].Value * 1000L / Math.Max(1, total))), Mx(x + 140), My(y + 86), 0, Cyan, tag, SizeS);
                string of = $"{HubCount(top[0].Value)} of {HubCount(total)} {unit}";
                TextS(of.Length <= 21 ? of : $"{HubCount(top[0].Value)} {unit}", Mx(x + 140), My(y + 132), 0, Pale, tag, SizeX);
                // the next two, small
                for (int k = 1; k < top.Count; k++)
                {
                    float ty = y + 140 + 34 * k;
                    HubPicture(cat, top[k].Key, Mx(x + 14), My(ty - 3), Mw(30), tag, lic);
                    TextS(Fit(FavName(cat, top[k].Key), 20), Mx(x + 54), My(ty), 0, White, tag, SizeX);
                    TextS(Pct((int)(top[k].Value * 1000L / Math.Max(1, total))), Mx(x + 489), My(ty), 1, Pale, tag, SizeX);
                }
            }
        }
    }
}
