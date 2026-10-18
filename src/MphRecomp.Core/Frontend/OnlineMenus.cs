using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Online;

// ONLINE PLAY (owner 2026-10-09; mockups in
// extract_out/online_menus/mockups): the multiplayer menu's ONLINE PLAY door (MultiplayerMenu call 92) opens the hub
// (mockup H_C): QUICK MATCH (the first open game for this game version, vanilla's FIND GAME; none open: it offers to CREATE
// one), BROWSE and CREATE under it, and a FRIENDS ONLINE panel on the right (JOIN on a friend in an open lobby). BROWSE
// (mockup B_A) lists the open games three a page in the REPLAYS look: the seats, host name#tag with a friend / rival badge,
// mode, players, ping, JOIN and
// the arena's picture; filters MODE and FRIENDS ONLY on top (the server lists only games that can still be joined, so
// there's no HIDE FULL), REFRESH. A row opens its details (mockup B_C): picture, host, rules, who can join, ping, JOIN,
// the seats. JOIN (and QUICK MATCH, a friend's JOIN, an invite) first asks for this player's hunter (SELECT HUNTER on its
// own, MultiplayerMenu.BeginOnlineSetup; back returns to the page the JOIN was on), then goes into the game through the
// server (OnlineMatch.JoinAsync, then a seat from the host as that hunter), waits in the lobby for the host's START, then
// plays the match as the LAN joiner does (FrontendRequestKind.StartMatch, net=online: the match screen plays
// OnlineMatch.Current) and comes back to the hub.
// CREATE (mockup W_A, 2026-10-09 slice 2) goes through the local setup pages (MultiplayerMenu.BeginOnlineSetup: mode, rules,
// SELECT HUNTER), then WHO CAN JOIN: PUBLIC / FRIENDS / FRIENDS + RIVALS / INVITE ONLY, the game's PLAYERS and BOTS, CREATE.
// That hosts the game on the server (OnlineMatch.HostAsync) and opens the lobby (mockup L_B, the side-panel layout for every
// size): seat cards (you, the people who joined, open seats, bots), the GAME panel, INVITE FRIENDS, START; a joiner waits in
// the same lobby (no START) until the host starts. Tap your own card to change hunter (SELECT HUNTER and back: the others
// see "choosing a hunter" meanwhile); in a team mode the host's TEAM on each card swaps its team; each card shows the
// player's ping and whether they're ready. START asks first when someone is still choosing or still on the results.
// After the match: RESULTS (MultiplayerMenu), whose PLAY AGAIN brings everyone back to the same lobby (OnlineAfterResults:
// the match session goes on, NetMatchSession protocol 4) and QUIT leaves the game.
// ALL FRIENDS opens FRIENDS AND RIVALS and its FIND A HUNTER (OnlineMenus.Friends.cs, slice 3). A name (HOST LICENSE and the
// details page's seats, the lobby's seat cards, the friends pages' rows) opens their HUNTER LICENSE (OnlineMenus.License.cs).
// WHO CAN JOIN also sets the bots' level.
// Built as the REPLAYS pages are (RecompMenus.Replays.cs): the CREATE OR JOIN frame, input items with only a focused look,
// everything else drawn from the ROM's own models by DrawOnline; MenuLayout.Replays places the pages. Positions come from
// the picked mockups (1920 x 1080: 4.933 px a menu unit, menu x 128 at px 960; see Mx / My).
// The connection: made as the hub comes in (this device's key, OnlineIdentity, beside the Hunter License), closed when the
// player leaves the online pages; network work runs off the menu thread and hands its results back through _posts.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        public const string OnlineServer = "wss://play.secondhuntonline.com/v1/ws";

        private static int s_onHub = -1, s_onBrowse = -1, s_onDetail = -1, s_onWho = -1, s_onLobby = -1;
        public static bool IsOnlinePage(MenuPage page) => IsOnline(page.Index);
        public static bool IsOnlinePageIndex(int page) => IsOnline(page);
        private static bool IsOnline(int page) => page >= 0
            && (page == s_onHub || page == s_onBrowse || page == s_onDetail || page == s_onWho || page == s_onLobby || page == s_onFriends
                || page == s_onAdd || page == s_onLicense || page == s_onSettings || IsFindPage(page) || IsChatPage(page));

        // CREATE's setup pages and "change hunter" (FrontendSession)
        public MultiplayerMenu? Multiplayer { get; set; }

        // who this player is online (FrontendSession: the Hunter License name and rank, the last SELECT HUNTER pick)
        public sealed record OnlinePlayer(string Name, int Rank, Hunter Hunter, string? Skin, string? Character);
        public Func<OnlinePlayer>? OnlineMe { get; set; }
        // play the joined match (FrontendSession: FrontendRequestKind.StartMatch with this setup line)
        public Action<string>? StartOnlineMatch { get; set; }

        // our callbacks (2500-2599)
        private const int OnQuickCall = 2500, OnBrowseCall = 2501, OnCreateCall = 2502, OnAllFriendsCall = 2503;
        private const int OnFriendJoinCall = 2510; // + the row
        private const int OnRowCall = 2520, OnRowJoinCall = 2530; // + the row
        private const int OnPrevCall = 2540, OnNextCall = 2541, OnModeCall = 2542, OnFriendsOnlyCall = 2543, OnRefreshCall = 2544;
        private const int OnJoinCall = 2550, OnHostLicenseCall = 2551;
        private const int OnWhoCall = 2560; // + the row
        private const int OnPlayersCall = 2564, OnPlayersLeftCall = 2565, OnPlayersRightCall = 2566;
        private const int OnBotsCall = 2567, OnBotsLeftCall = 2568, OnBotsRightCall = 2569, OnCreateGameCall = 2570;
        private const int OnSeatCall = 2580; // + the seat
        private const int OnRulesCall = 2588; // the host's EDIT RULES (GAME panel): as its own seat card's tap
        private const int OnInviteCall = 2590, OnStartCall = 2591, OnLeaveCall = 2592;
        private const int OnBotLevelCall = 2593, OnBotLevelLeftCall = 2594, OnBotLevelRightCall = 2595;
        private const int OnTeamCall = 2596; // + the seat (to 2599)
        private const int OnLastCall = OnTeamCall + 3;
        private const int OnRows = 3, OnFriendRows = 3, WhoRows = 4, LobbySeats = 4;

        // ---- geometry: the mockups' pixels in menu units ----
        private const float MockScale = 4.933f;
        private static float Mx(float px) => (px - 72) / MockScale - 52;
        private static float My(float py) => -24.6f - (py - 197) / MockScale;
        private static float Mw(float px) => px / MockScale;
        // the mockups' three text sizes (4.95 / 3.6 / 3.0 px a font pixel at 1920 wide)
        private const float SizeS = 1, SizeM = 3.6f / 4.95f, SizeX = 3.0f / 4.95f;
        // HIDE RIVALS (owner 2026-10-11, release 0.2.0 F2: "get rid of rival system"): no RIVALS tab, ADD RIVAL, rival badges,
        // FRIENDS + RIVALS choice or rival invites in the menus. The server and the roster's rivals are unchanged (kept); true
        // brings it all back.
        public const bool ShowRivals = false;
        // WHO CAN JOIN's rows as shown (WhoChoices indexes): FRIENDS + RIVALS (2) only with rivals
        private static readonly int[] WhoShown = ShowRivals ? new[] { 0, 1, 2, 3 } : new[] { 0, 1, 3 };
        // the ROM panel (blackdrop) as REPLAYS places it: drawn x -4.3..258.1 before its move; StretchX grows it about its middle
        private const float PanelMid = 126.9f, PanelWidth = 262.4f;

        private static readonly (float R, float G, float B) Cyan = (90 / 255f, 222 / 255f, 1), Green = (120 / 255f, 236 / 255f, 96 / 255f),
            Red = (236 / 255f, 60 / 255f, 60 / 255f), HeaderBar = (190 / 255f, 178 / 255f, 128 / 255f), Dark = (0.08f, 0.08f, 0.06f),
            OnGrey = (0.6f, 0.6f, 0.6f);

        // items (-1 = none)
        private int _hPanel = -1, _hSide = -1, _hTitle = -1, _hQuick = -1, _hBrowse = -1, _hCreate = -1, _hAllFriends = -1;
        private readonly int[] _hFriendJoin = { -1, -1, -1 };
        private int _bPanel = -1, _bTitle = -1, _bPrev = -1, _bNext = -1, _bMode = -1, _bFriendsOnly = -1, _bRefresh = -1;
        private readonly int[] _bRow = { -1, -1, -1 }, _bJoin = { -1, -1, -1 };
        private int _gPanel = -1, _gTitle = -1, _gJoin = -1, _gLicense = -1;
        private int _onBrowseTitle = -1, _onDetailTitle = -1, _onLobbyTitle = -1;
        private int _wPanel = -1, _wSide = -1, _wTitle = -1, _wPlayers = -1, _wPlayersL = -1, _wPlayersR = -1, _wBots = -1, _wBotsL = -1, _wBotsR = -1, _wCreate = -1;
        private readonly int[] _wRow = { -1, -1, -1, -1 };
        private int _lbPanel = -1, _lbSide = -1, _lbTitle = -1, _lbInvite = -1, _lbStart = -1, _lbRules = -1;
        private readonly int[] _lbSeat = { -1, -1, -1, -1 }, _lbTeam = { -1, -1, -1, -1 };
        private int _wLevel = -1, _wLevelL = -1, _wLevelR = -1;
        private readonly Dictionary<int, int> _onBack = new(); // each page's back arrow
        private MenuSounds? _onlineSounds;

        // ---- state (menu thread) ----
        private OnlineClient? _client;
        private bool _connecting, _joining;
        private string? _netError; // why the last connect or list failed (shown on the hub)
        private int _hunters = -1; // the server's count of players online (its health line), -1 = not known
        private List<GameCard> _games = new();
        private bool _gamesListed; // the server's list has come at least once
        private List<PlayerCard> _friendsOn = new();
        private int _pingMs = -1; // this device to the server and back (a list request), -1 = not known
        private int _gameOffset, _modeFilter; // _modeFilter: 0 = all, else OnModes[n - 1]
        private bool _friendsOnly;
        private string? _gameOpen; // the game on the details page (its id)
        private MenuEngine? _onlineMenu;
        private long _nextRefresh;
        private readonly ConcurrentQueue<Action> _posts = new();
        private readonly CancellationTokenSource _onlineStop = new();
        private volatile bool _joinCancelled;
        private PlayerCard? _previewMe; // MphRead.Tools -onlinemenu: sample data, no connection (OnlinePreview)
        // CREATE: WHO CAN JOIN's choice (0 public, 1 friends, 2 friends + rivals, 3 invite only), the game's seats and bots
        private static int s_who, s_seats = 4, s_bots, s_botLevel = 1;
        private bool _creating;
        private volatile bool _createCancelled;
        // the lobby: the game this device hosts or joined (its match owns the connection), and who's in it (the server's cards)
        private OnlineMatch? _hosting, _joined;
        // who runs the lobby: the game this device hosts, or the served game it joined and owns (docs/DEDICATED_SERVERS.md:
        // the server runs the match, the owner's MODE / RULES, bots, teams and START go to it as OWNER messages)
        private OnlineMatch? Runner => _hosting ?? (_joined is { Net.IsOwner: true } owned ? owned : null);
        private MatchSettings? _hostTemplate;
        private bool _invitePending;
        // a joiner waiting in a lobby gives up on a host it hasn't heard for this long (the host sends LOBBY every second)
        private const long HostSilentMs = 30000;
        // a served game's owner pressed START: the server's match host answers well within this, or it refused (an arena missing)
        private const int StartWaitMs = 6000;
        // the match the match screen handed back for RESULTS (PLAY AGAIN or QUIT), when it isn't in the lobby yet
        private static OnlineMatch? Kept => OnlineMatch.Current is { HandedBack: true, Disposed: false } k ? k : null;
        // a JOIN waiting on its SELECT HUNTER (the lobby page starts it), and the page the JOIN was on
        private (string? GameId, string? Host)? _pendingJoin;
        private int _joinFrom = -1;
        private bool Live => _client != null || _previewMe != null;
        private PlayerCard? MeCard => _client?.Me ?? _previewMe;

        // tools (MphRead.Tools -onlinemenu / -onlinemenutest): the pages, what's known, and where a game sits on BROWSE
        internal static (int Hub, int Browse, int Detail) OnlinePages => (s_onHub, s_onBrowse, s_onDetail);
        internal static (int Who, int Lobby) OnlineCreatePages => (s_onWho, s_onLobby);
        internal OnlineMatch? OnlineHosting => _hosting;
        internal OnlineMatch? OnlineRunner => Runner; // (the hosted game, or the served game whose lobby this device owns)
        internal bool OnlineInLobby => _joined != null;
        internal bool OnlineConnected => _client != null;
        internal bool OnlineJoining => _joining;
        internal IReadOnlyList<GameCard> OnlineGames => _games;
        internal int OnlineShownIndex(string gameId) => Shown().FindIndex(g => g.Id == gameId);

        // tools (MphRead.Tools -onlinemenu): show these instead of the server's, and never connect
        internal void OnlinePreview(PlayerCard me, List<GameCard> games, List<PlayerCard> friendsOnline, int hunters, int pingMs)
        {
            _previewMe = me;
            _games = games;
            _friendsOn = friendsOnline;
            _hunters = hunters;
            _pingMs = pingMs;
            _netError = null;
            Redraw();
        }

        private static readonly GameMode[] OnModes =
            { GameMode.Battle, GameMode.Bounty, GameMode.Capture, GameMode.Defender, GameMode.Nodes, GameMode.PrimeHunter, GameMode.Survival };

        // ONLINE PLAY with imports an update made stale (owner 2026-10-17): the game's box asks to update them first, once
        // per run (NO goes online) -- a stale arena counts as not imported, so online leaves it out (ImportedContent.Usable)
        public const string StaleOnlineText = "some imports are out of date, so their maps are left out online.\n\nupdate them first?";
        private bool _staleOnlineAsked;

        public void OpenOnline(MenuEngine menu)
        {
            if (s_onHub < 0) return;
            if (!_staleOnlineAsked && Import.Popup != null && _importRows != null && StaleImportCount() > 0)
            {
                _staleOnlineAsked = true;
                int back = menu.Page?.Index ?? -1;
                Import.Popup.Ask(StaleOnlineText, yes =>
                {
                    if (yes && _importRows != null)
                    {
                        _importBackPage = back;
                        menu.GoTo(_importRows.Page);
                    }
                    else menu.GoTo(s_onHub);
                });
                return;
            }
            menu.GoTo(s_onHub);
        }

        private int StaleImportCount()
        {
            int stale = 0;
            try
            {
                foreach (MphRecomp.Import.ImportGameInfo g in Import.Service?.Games ?? Array.Empty<MphRecomp.Import.ImportGameInfo>())
                {
                    foreach (MphRecomp.Import.ImportItemInfo i in g.Items) if (ImportGames.Offered(i) && ImportGames.NeedsUpdate(i)) stale++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
            return stale;
        }

        private string? ProfileDir => _settingsPath != null ? Path.GetDirectoryName(_settingsPath) : null;
        private static Uri ServerUri => new(OnlineServerOverride ?? OnlineServer);
        internal static string? OnlineServerOverride { get; set; } // (tools: a local test server)
        private static string VersionKey => OnlineMatch.VersionKey(LanSession.RomKeyFor(Paths.MphKey));

        private void InstallOnline()
        {
            if (_file.Pages.Count <= MultiplayerMenu.LobbyPage) return;
            MenuPage frame = _file.Pages[MultiplayerMenu.CreateJoinPage];
            MenuItem? panel = frame.Items.FirstOrDefault(it => HasWidget(it, "blackdrop"));
            MenuItem? backIcon = frame.Items.FirstOrDefault(it => HasWidget(it, "backicon"));
            MenuItem? backButton = frame.Items.FirstOrDefault(it => HasWidget(it, "backhighlight"));
            MenuItem? title = frame.Items.FirstOrDefault(it => TextAt(it, 128, 172));
            MenuItemState? glow = _file.Pages[MultiplayerMenu.LobbyPage].Items.Where(it => HasWidget(it, "player_highlight"))
                .SelectMany(it => it.States).FirstOrDefault(s => s.Code == (int)MenuState.Idle && s.WidgetIndex >= 0);
            if (panel == null || backIcon == null || backButton == null || title == null || glow == null) return;

            // back < 0: B runs LEAVE (the lobby asks first) instead of changing page
            MenuPage AddFramePage(int back, params MenuAction[] more) => _file.AddPage(index => new MenuPage
            {
                Index = index, Field0 = frame.Field0, Field14 = frame.Field14, Field16 = frame.Field16, Field18 = frame.Field18,
                Field1D = frame.Field1D, Field1E = frame.Field1E, Flags = frame.Flags, Items = Array.Empty<MenuItem>(),
                Actions = new[] { back >= 0 ? new MenuAction { Kind = (ushort)MenuKeys.B, TargetPage = (byte)back, Item = 0xFFFF, Field17 = 4 }
                    : Call(MenuKeys.B, OnLeaveCall) }.Concat(more).ToArray(),
                Timers = Array.Empty<MenuTimer>()
            });

            // as REPLAYS': no look until focused (the lobby's glow, shaped round it by DrawOnline); A or a tap runs `call`
            MenuItem Input(int index, float x0, float yTop, float x1, float yBottom, int call) => new()
            {
                Index = index,
                Field34 = (byte)MenuState.Idle,
                Field35 = (byte)MenuState.Idle,
                States = new[]
                {
                    new MenuItemState { Code = (int)MenuState.Focused, Kind = glow.Kind, Field2 = glow.Field2, WidgetIndex = glow.WidgetIndex, WidgetFlags = glow.WidgetFlags }
                },
                Links = Array.Empty<MenuLink>(),
                Actions = new[]
                {
                    new MenuAction
                    {
                        Kind = (ushort)MenuKeys.A, RectX = (short)MathF.Floor(x0), RectY = (short)MathF.Floor(yBottom + 192),
                        RectW = (short)MathF.Ceiling(x1), RectH = (short)MathF.Ceiling(yTop + 192), Calls = new[] { (0, call) },
                        TargetPage = 0xFF, Item = 0xFFFF
                    }
                }
            };
            MenuItem Touch(int index, float x0, float yTop, float x1, float yBottom, int call) => new()
            {
                Index = index, Field34 = (byte)MenuState.Idle, Field35 = (byte)MenuState.Idle, States = Array.Empty<MenuItemState>(),
                Links = Array.Empty<MenuLink>(), Actions = new[] { TouchRect(x0, yBottom + 192, x1, yTop + 192, call) }
            };
            // the ROM panel moved and resized to span x0..x1
            MenuItem Panel(int i, float x0, float x1) => Clone(panel, i, (x0 + x1) / 2 - PanelMid, 0, stretchX: x1 - x0 - PanelWidth,
                stretchDown: PanelStretchDown);
            void Back(MenuPage page, int to)
            {
                page.AddItem(i => Clone(backIcon, i, 0, 0));
                _onBack[page.Index] = page.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions
                    .Select(a => to >= 0 ? WithTarget(a, to) : WithCall(a, OnLeaveCall)).ToList())).Index;
            }

            // ---- the hub (H_C) ----
            MenuPage hub = AddFramePage(MultiplayerMenu.TypePage);
            s_onHub = hub.Index;
            _hPanel = hub.AddItem(i => Panel(i, Mx(70), Mx(1270))).Index;
            _hSide = hub.AddItem(i => Panel(i, Mx(1310), Mx(1850))).Index;
            Back(hub, MultiplayerMenu.TypePage);
            _hTitle = hub.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("ONLINE PLAY"))).Index;
            _hQuick = hub.AddItem(i => Input(i, Mx(100), My(226), Mx(1240), My(556), OnQuickCall)).Index;
            _hBrowse = hub.AddItem(i => Input(i, Mx(100), My(586), Mx(655), My(886), OnBrowseCall)).Index;
            _hCreate = hub.AddItem(i => Input(i, Mx(685), My(586), Mx(1240), My(886), OnCreateCall)).Index;
            for (int r = 0; r < OnFriendRows; r++)
            {
                (float x0, float y0, float x1, float y1) = FriendJoinRect(r);
                int row = r;
                _hFriendJoin[r] = hub.AddItem(i => Input(i, x0, y0, x1, y1, OnFriendJoinCall + row)).Index;
            }
            _hAllFriends = hub.AddItem(i => Input(i, Mx(1350), My(826), Mx(1810), My(892), OnAllFriendsCall)).Index;

            // ---- browse (B_A) ----
            MenuPage browse = AddFramePage(hub.Index, Call(MenuKeys.R, OnNextCall), Call(MenuKeys.L, OnPrevCall));
            s_onBrowse = browse.Index;
            _bPanel = browse.AddItem(i => Panel(i, Mx(70), Mx(1850))).Index;
            Back(browse, hub.Index);
            _onBrowseTitle = _strings.Add("BROWSE GAMES");
            _bTitle = browse.AddItem(i => Clone(title, i, 0, 0, stringId: _onBrowseTitle)).Index;
            for (int r = 0; r < OnRows; r++)
            {
                (float x0, float top, float x1, float bottom) = GameRowRect(r);
                int row = r;
                _bRow[r] = browse.AddItem(i => Input(i, x0, top, x1, bottom, OnRowCall + row)).Index;
            }
            for (int r = 0; r < OnRows; r++)
            {
                (float x0, float top, float x1, float bottom) = RowJoinRect(r);
                int row = r;
                _bJoin[r] = browse.AddItem(i => Input(i, x0, top, x1, bottom, OnRowJoinCall + row)).Index; // after the rows: taps hit it first
            }
            (float mx0, float my0, float mx1, float my1) = FilterRect(0);
            _bMode = browse.AddItem(i => Input(i, mx0, my0, mx1, my1, OnModeCall)).Index;
            (float fx0, float fy0, float fx1, float fy1) = FilterRect(1);
            _bFriendsOnly = browse.AddItem(i => Input(i, fx0, fy0, fx1, fy1, OnFriendsOnlyCall)).Index;
            _bRefresh = browse.AddItem(i => Input(i, Mx(1640), My(204), Mx(1830), My(244), OnRefreshCall)).Index;
            _bPrev = browse.AddItem(i => Touch(i, 128 - ArrowGap - ArrowW - 22, ArrowTop + 4, 128 - ArrowGap - 14, ArrowTop - ArrowH - 4, OnPrevCall)).Index;
            _bNext = browse.AddItem(i => Touch(i, 128 + ArrowGap + 14, ArrowTop + 4, 128 + ArrowGap + ArrowW + 22, ArrowTop - ArrowH - 4, OnNextCall)).Index;

            // ---- one game (B_C) ----
            MenuPage detail = AddFramePage(browse.Index);
            s_onDetail = detail.Index;
            _gPanel = detail.AddItem(i => Panel(i, Mx(70), Mx(1850))).Index;
            Back(detail, browse.Index);
            _onDetailTitle = _strings.Add("");
            _gTitle = detail.AddItem(i => Clone(title, i, 0, 0, stringId: _onDetailTitle)).Index;
            _gJoin = detail.AddItem(i => Input(i, Mx(790), My(600), Mx(1340), My(690), OnJoinCall)).Index;
            _gLicense = detail.AddItem(i => Input(i, Mx(1390), My(615), Mx(1750), My(681), OnHostLicenseCall)).Index;
            for (int k = 0; k < _gSeat.Length; k++)
            {
                (float x0, float top, float x1, float bottom) = DetailSeatRect(k);
                int seat = k;
                _gSeat[k] = detail.AddItem(i => Input(i, x0, top, x1, bottom, LicSeatCall + seat)).Index;
            }

            // ---- WHO CAN JOIN (W_A): after CREATE's SELECT HUNTER, back to it ----
            MenuPage who = AddFramePage(MultiplayerMenu.HunterPage);
            s_onWho = who.Index;
            _wPanel = who.AddItem(i => Panel(i, Mx(70), Mx(1270))).Index;
            // the YOUR GAME box ends above CREATE (the panel shortened by 200 mockup pixels)
            _wSide = who.AddItem(i => Clone(panel, i, (Mx(1310) + Mx(1850)) / 2 - PanelMid, 0, stretchX: Mx(1850) - Mx(1310) - PanelWidth,
                stretchDown: PanelStretchDown - Mw(200))).Index;
            Back(who, MultiplayerMenu.HunterPage);
            _wTitle = who.AddItem(i => Clone(title, i, 0, 0, stringId: _strings.Add("WHO CAN JOIN"))).Index;
            for (int r = 0; r < WhoRows; r++)
            {
                (float x0, float top, float x1, float bottom) = WhoRowRect(Math.Max(0, Array.IndexOf(WhoShown, r)));
                int row = r;
                _wRow[r] = who.AddItem(i => Input(i, x0, top, x1, bottom, OnWhoCall + row)).Index;
            }
            (float bx0, float bt, float bx1, float bb) = WhoBox(0);
            _wBots = who.AddItem(i => Input(i, bx0, bt, bx1, bb, OnBotsCall)).Index;
            _wBotsL = who.AddItem(i => Touch(i, Mx(1548), bt + 2, bx0 - 1, bb - 2, OnBotsLeftCall)).Index;
            _wBotsR = who.AddItem(i => Touch(i, bx1 + 1, bt + 2, Mx(1812), bb - 2, OnBotsRightCall)).Index;
            (float px0, float pt, float px1, float pb) = WhoBox(1);
            _wPlayers = who.AddItem(i => Input(i, px0, pt, px1, pb, OnPlayersCall)).Index;
            _wPlayersL = who.AddItem(i => Touch(i, Mx(1548), pt + 2, px0 - 1, pb - 2, OnPlayersLeftCall)).Index;
            _wPlayersR = who.AddItem(i => Touch(i, px1 + 1, pt + 2, Mx(1812), pb - 2, OnPlayersRightCall)).Index;
            (float lx0, float lt, float lx1, float lb) = WhoBox(2);
            _wLevel = who.AddItem(i => Input(i, lx0, lt, lx1, lb, OnBotLevelCall)).Index;
            _wLevelL = who.AddItem(i => Touch(i, Mx(1548), lt + 2, lx0 - 1, lb - 2, OnBotLevelLeftCall)).Index;
            _wLevelR = who.AddItem(i => Touch(i, lx1 + 1, lt + 2, Mx(1812), lb - 2, OnBotLevelRightCall)).Index;
            _wCreate = who.AddItem(i => Input(i, Mx(1330), My(760), Mx(1830), My(870), OnCreateGameCall)).Index;

            // ---- the lobby (L_B for every size): back = LEAVE ----
            MenuPage lobby = AddFramePage(-1);
            s_onLobby = lobby.Index;
            _lbPanel = lobby.AddItem(i => Panel(i, Mx(70), Mx(1390))).Index;
            _lbSide = lobby.AddItem(i => Panel(i, Mx(1430), Mx(1850))).Index;
            Back(lobby, -1);
            _onLobbyTitle = _strings.Add("LOBBY");
            _lbTitle = lobby.AddItem(i => Clone(title, i, 0, 0, stringId: _onLobbyTitle)).Index;
            for (int k = 0; k < LobbySeats; k++)
            {
                (float x0, float top, float x1, float bottom) = SeatRect(k);
                int seat = k;
                _lbSeat[k] = lobby.AddItem(i => Input(i, x0, top, x1, bottom, OnSeatCall + seat)).Index;
            }
            // (after the cards: a tap on TEAM hits it first)
            for (int k = 0; k < LobbySeats; k++)
            {
                (float x0, float top, float x1, float bottom) = TeamRect(k);
                int seat = k;
                _lbTeam[k] = lobby.AddItem(i => Input(i, x0, top, x1, bottom, OnTeamCall + seat)).Index;
            }
            InstallLobby8(lobby, Input); // (P8: 5-8 seats, OnlineMenus.Seats8.cs)
            InstallLobbyBots(lobby, Input); // (the host's LEVEL / REMOVE / ADD BOT: OnlineMenus.LobbyBots.cs)
            // (owner 2026-10-16: the host's EDIT RULES in the GAME panel, over INVITE FRIENDS; both a little lower for it)
            _lbRules = lobby.AddItem(i => Input(i, Mx(1450), My(LbRulesY), Mx(1830), My(LbRulesY + LbBarH), OnRulesCall)).Index;
            _lbInvite = lobby.AddItem(i => Input(i, Mx(1450), My(LbInviteY), Mx(1830), My(LbInviteY + LbBarH), OnInviteCall)).Index;
            _lbStart = lobby.AddItem(i => Input(i, Mx(1446), My(LbStartY), Mx(1836), My(LbStartY + 88), OnStartCall)).Index;

            // ---- FRIENDS AND RIVALS, FIND A HUNTER (OnlineMenus.Friends.cs) ----
            InstallFriends(AddFramePage, Input, Touch, Panel, Back, panel, title);

            // ---- HUNTER LICENSE (OnlineMenus.License.cs), ONLINE SETTINGS (OnlineMenus.Settings.cs) ----
            InstallLicense(AddFramePage, Input, Panel, Back, title);
            InstallSettings(AddFramePage, Input, Panel, Back, title, hub);
            InstallFind(AddFramePage, Input, Touch, Panel, Back, title); // (FIND GAME, the vote: OnlineMenus.Find.cs)
            InstallChat(AddFramePage, Input, Panel, Back, title); // (CHAT, REPORT A HUNTER, the lobby's CHAT bar: OnlineMenus.Chat.cs)
            InstallResultNames(Touch);
        }

        // WHO CAN JOIN's rows, and its BOTS (0) / PLAYERS (1) value boxes
        private static (float X0, float Top, float X1, float Bottom) WhoRowRect(int row)
            => (Mx(100), My(222 + 170 * row), Mx(1240), My(370 + 170 * row));

        private static (float X0, float Top, float X1, float Bottom) WhoBox(int which)
            => (Mx(1610), My(476 + 80 * which), Mx(1750), My(536 + 80 * which));

        // a seat card's TEAM box (team modes; the host's to change): the card's bottom-right corner
        private static (float X0, float Top, float X1, float Bottom) TeamRect(int seat)
            => (Mx(100 + seat % 2 * 650 + 430), My(222 + seat / 2 * 352 + 256), Mx(100 + seat % 2 * 650 + 590), My(222 + seat / 2 * 352 + 312));

        // the lobby's seat cards: two columns, two rows (four seats; P8's eight would be four rows of the mockup's height)
        private static (float X0, float Top, float X1, float Bottom) SeatRect(int seat)
            => (Mx(100 + seat % 2 * 650), My(222 + seat / 2 * 352), Mx(710 + seat % 2 * 650), My(552 + seat / 2 * 352));

        // the ROM items whose sounds ours borrow (InstallReplays found them): back as CREATE OR JOIN's, opening a page as the
        // main menu's adventure, JOIN / GO as starting a file, a filter as an options value box
        private void ApplyOnlineSounds(MenuSounds sounds)
        {
            if (ReferenceEquals(sounds, _onlineSounds)) return;
            _onlineSounds = sounds;
            void Like(int page, int item, int romPage, int romItem)
            {
                if (page >= 0 && item >= 0 && romItem >= 0) sounds.ItemLike(page, item, romPage, romItem);
            }
            foreach ((int page, int back) in _onBack) Like(page, back, MultiplayerMenu.CreateJoinPage, _soundBack);
            Like(s_onHub, _hQuick, FileSelect.Page, _soundFileStart);
            Like(s_onHub, _hBrowse, MainMenuPage, _soundCampaign);
            Like(s_onHub, _hCreate, MainMenuPage, _soundCampaign);
            Like(s_onHub, _hAllFriends, MainMenuPage, _soundCampaign);
            for (int r = 0; r < OnFriendRows; r++) Like(s_onHub, _hFriendJoin[r], FileSelect.Page, _soundFileStart);
            for (int r = 0; r < OnRows; r++)
            {
                Like(s_onBrowse, _bRow[r], MainMenuPage, _soundCampaign);
                Like(s_onBrowse, _bJoin[r], FileSelect.Page, _soundFileStart);
            }
            Like(s_onBrowse, _bMode, OptionBoxPage, _soundToggle);
            Like(s_onBrowse, _bFriendsOnly, OptionBoxPage, _soundToggle);
            Like(s_onBrowse, _bRefresh, OptionBoxPage, _soundToggle);
            Like(s_onDetail, _gJoin, FileSelect.Page, _soundFileStart);
            for (int r = 0; r < WhoRows; r++) Like(s_onWho, _wRow[r], OptionBoxPage, _soundToggle);
            Like(s_onWho, _wPlayers, OptionBoxPage, _soundToggle);
            Like(s_onWho, _wBots, OptionBoxPage, _soundToggle);
            Like(s_onWho, _wLevel, OptionBoxPage, _soundToggle);
            for (int k = 0; k < LobbySeats; k++) Like(s_onLobby, _lbTeam[k], OptionBoxPage, _soundToggle);
            Like(s_onWho, _wCreate, FileSelect.Page, _soundFileStart);
            for (int k = 0; k < LobbySeats; k++) Like(s_onLobby, _lbSeat[k], MainMenuPage, _soundCampaign);
            for (int k = 0; k < LobbySeats8; k++) Like(s_onLobby, _lbSeat8[k], MainMenuPage, _soundCampaign);
            for (int k = 0; k < LobbySeats8; k++) Like(s_onLobby, _lbTeam8[k], OptionBoxPage, _soundToggle);
            Like(s_onLobby, _lbInvite, MainMenuPage, _soundCampaign);
            Like(s_onLobby, _lbRules, MainMenuPage, _soundCampaign);
            Like(s_onLobby, _lbStart, FileSelect.Page, _soundFileStart);
            ApplyLobbyBotsSounds(Like);
            ApplyFriendsSounds(Like);
            ApplyLicenseSounds(Like);
            ApplySettingsSounds(Like);
            ApplyFindSounds(Like);
            ApplyChatSounds(Like);
        }

        private static (float X0, float Top, float X1, float Bottom) FriendJoinRect(int row)
            => (Mx(1680), My(316 + 170 * row), Mx(1840), My(366 + 170 * row));

        private static (float X0, float Top, float X1, float Bottom) GameRowRect(int row)
            => (Mx(100), My(284 + 210 * row), Mx(1730), My(462 + 210 * row));

        private static (float X0, float Top, float X1, float Bottom) RowJoinRect(int row)
            => (Mx(1200), My(396 + 210 * row), Mx(1430), My(452 + 210 * row));

        // the filter value boxes: MODE, FRIENDS ONLY (labels left of them)
        private static (float X0, float Top, float X1, float Bottom) FilterRect(int which) => which == 0
            ? (Mx(220), My(210), Mx(516), My(254)) : (Mx(840), My(210), Mx(1006), My(254));

        // ---- runtime ----

        // RecompMenus.OnPageEntered
        private void EnterOnline(MenuEngine menu, int page)
        {
            if (PauseChat != null)
            {
                // an online match's pause: only CHAT (and its keyboard, REPORT A HUNTER) are ours; its back (the lobby) is the pause
                if (page == s_onLobby) menu.GoTo(PausePage);
                else if (IsChatPage(page))
                {
                    _onlineMenu = menu;
                    EnterChat(menu, page);
                }
                return;
            }
            bool online = IsOnline(page);
            if (page == MultiplayerMenu.ResultsPage)
            {
                CountHeadToHead(); // (after an online match)
                ReportMatchResult(); // (a network match this device hosted: head to head on the server, OnlineMenus.Records.cs)
                ShowResultNames(menu); // (its names open their licenses)
            }
            // CREATE's setup pages (and the host's "change hunter") keep the connection and the hosted game
            bool setup = MultiplayerMenu.InOnlineSetup && page >= MultiplayerMenu.ModePage && page <= MultiplayerMenu.HunterPage;
            // (and a seat card's HUNTER LICENSE, back to the lobby)
            bool seatLicense = page == s_onLicense && _licReturn == s_onLobby || KeepsLobby(page); // (and CHAT, its keyboard: OnlineMenus.Chat.cs)
            if (_hosting != null && page != s_onLobby && !setup && !seatLicense) LeaveHosting();
            if (_joined != null && page != s_onLobby && !setup && !seatLicense) _joinCancelled = true; // (the join task closes it and comes back)
            // a match handed back for RESULTS that went anywhere else than PLAY AGAIN / QUIT: out of its game
            if (Kept is OnlineMatch kept && kept != _hosting && kept != _joined && page != MultiplayerMenu.ResultsPage
                && page != MultiplayerMenu.LoadingPage && !(page == s_onLicense && _licReturn == MultiplayerMenu.ResultsPage))
            {
                kept.Dispose();
            }
            if (!online)
            {
                // (FIND A HUNTER's name keyboard keeps it too)
                bool typing = (_typingName || _chatTyping) && page == MultiplayerMenu.NamePage;
                if (_client != null && !_joining && page != MultiplayerMenu.LoadingPage && !setup && !typing) CloseOnline();
                return;
            }
            _onlineMenu = menu;
            OnlineMatch.ReturnClientsToMenus = true; // (a match started from here hands its connection back: OnlineMatch.Dispose)
            ApplyOnlineSounds(menu.Sounds);
            if (page != s_onLobby)
            {
                _pendingJoin = null; // (back from a joiner's SELECT HUNTER)
                if (page != s_onWho) MultiplayerMenu.EndOnlineSetup();
            }
            if (page == s_onWho)
            {
                ShowWho(menu, focus: true);
                RefreshOnline(lists: false); // (connected for CREATE)
            }
            else if (page == s_onLobby)
            {
                MultiplayerMenu.EndOnlineSetup();
                if (_pendingJoin is { } pick && _hosting == null && _joined == null && !_joining)
                {
                    // the joiner's SELECT HUNTER picked: into the game as that hunter (the lobby fills in once the seat is ours)
                    _pendingJoin = null;
                    ShowLobby(menu, focus: false);
                    _strings.Set(_onLobbyTitle, "LOBBY");
                    Join(pick.GameId, pick.Host);
                    if (!_joining) menu.GoTo(JoinBack); // (no connection any more: Join said so)
                    return;
                }
                if (_hosting == null && _joined == null)
                {
                    menu.GoTo(s_onHub); // (a lobby that's gone)
                    return;
                }
                // back from "change hunter": the host's seat in the match takes the new pick (the template's player is
                // the match's: NetMatchSession.Build keeps the humans' objects), and everyone hears it; a joiner's as its LOOK
                (Hunter hunter, string? skin, string? character) = MultiplayerMenu.MyLook;
                if (_hosting != null && _hostTemplate is { Players.Count: > 0 } t)
                {
                    t.Players[0].Hunter = hunter;
                    t.Players[0].Skin = skin;
                    t.Players[0].Character = character;
                    if (MultiplayerMenu.TakeOnlineRulesEdit()) TakeRulesEdit(_hosting, t);
                    _hosting.Net.SetHostChoosing(false);
                    _hosting.Net.LobbyChanged();
                }
                else if (_joined is OnlineMatch j)
                {
                    j.Net.SetLook(hunter, character, skin, choosing: false, Me().Rank);
                    // (a served game's owner back from its MODE / RULES tabs: the match host's lobby takes them)
                    if (j.Net.IsOwner && MultiplayerMenu.TakeOnlineRulesEdit() && j.Net.OwnerLobby() is MatchSettings lobby)
                    {
                        TakeRulesEdit(j, lobby);
                        j.Net.OwnerRules(lobby);
                    }
                }
                ShowLobby(menu, focus: true);
            }
            else if (page == s_onHub)
            {
                MultiplayerMenu.EndOnlineSetup();
                ShowHub(menu);
                menu.SetState(_lastPage == s_onSettings ? _hSettings : _hQuick, MenuState.Focused);
                RefreshOnline(lists: true);
                FirstVisit();
            }
            else if (page == s_onSettings)
            {
                ShowSettings(menu, focus: true);
                RefreshOnline(lists: false);
            }
            else if (page == s_onBrowse)
            {
                if (_lastPage != s_onDetail) _gameOffset = 0;
                ShowGames(menu, FocusRowOf(_gameOpen));
                if (_lastPage != s_onDetail) RefreshOnline(lists: true);
            }
            else if (page == s_onFriends)
            {
                if (_lastPage != s_onAdd && _lastPage != s_onLicense) _frOffset = 0;
                ShowFriends(menu, 0);
                RefreshOnline(lists: true);
            }
            else if (page == s_onAdd)
            {
                bool typed = _typingName;
                _typingName = false;
                ShowAdd(menu, typed ? -1 : 0);
                if (typed) menu.SetState(_adSearch, MenuState.Focused);
                else RefreshOnline(lists: true);
            }
            else if (page == s_onLicense)
            {
                ShowLicense(menu, focus: true);
            }
            else if (IsFindPage(page))
            {
                EnterFind(menu, page);
            }
            else if (IsChatPage(page))
            {
                EnterChat(menu, page);
            }
            else
            {
                ShowOnGame(menu);
            }
        }

        private int FocusRowOf(string? id)
        {
            int at = id == null ? -1 : Shown().FindIndex(g => g.Id == id);
            if (at >= 0) _gameOffset = at / OnRows * OnRows;
            return at >= 0 ? at - _gameOffset : 0;
        }

        // every menu frame (RecompMenus.Tick): the network's results, the server's pushes, the hub's slow refresh
        private void TickOnline()
        {
            while (_posts.TryDequeue(out Action? post)) post();
            PublishLobbyVoice(); // (voice in the lobby: OnlineMenus.Voice.cs)
            SyncLobbyTalk();
            TickLobby();
            TickFind();
            TickChat();
            if (_client is { Connected: false })
            {
                Console.Error.WriteLine("online: the server connection dropped: " + _client.CloseReason);
                CloseOnline(); // (dropped: connect again)
                _netError = "lost the server. connecting again...";
                Redraw();
            }
            if (_client == null)
            {
                RetryOnline();
                return;
            }
            bool changed = false;
            InviteEvent? invite = null;
            while (_client.Events.TryDequeue(out OnlineMsg? ev))
            {
                if (FindPush(ev)) continue; // (Find Game, parties: OnlineMenus.Find.cs)
                if (ev is PresenceEvent or AddedEvent or RemovedEvent or RequestEvent or GameClosedEvent) changed = true;
                else if (ev is InviteEvent inv) invite = inv;
            }
            int page = _onlineMenu?.Page?.Index ?? -1;
            bool friendsPage = page == s_onFriends || page == s_onAdd || page == s_onLicense;
            if (invite != null && (page == s_onHub || page == s_onBrowse || page == s_onDetail || friendsPage)) Invited(invite);
            if (page != s_onHub && page != s_onBrowse && page != s_onDetail && !friendsPage) return;
            if (changed || Environment.TickCount64 >= _nextRefresh) RefreshOnline(lists: page != s_onDetail);
            if (changed && page == s_onLicense) FetchLicense();
        }

        private void Post(Action a) => _posts.Enqueue(a);

        // no connection on a page that shows the server's lists (the hub, BROWSE, a game, the friends pages, ONLINE SETTINGS):
        // connect again, sooner after a drop and then less often (2, 4, 8, 15, 30 s; release test T1: after a match the hub
        // stayed "offline" until it was opened again)
        private int _retries;
        private long _retryAt;
        private static readonly int[] RetryMs = { 2000, 4000, 8000, 15000, 30000 };
        // the hub's counts and lists while connected (N18: the health line ~40 B + three small lists each time)
        private const long RefreshEveryMs = 10000;

        private void RetryOnline()
        {
            int page = _onlineMenu?.Page?.Index ?? -1;
            bool listing = page == s_onHub || page == s_onBrowse || page == s_onDetail || page == s_onFriends || page == s_onAdd
                || page == s_onSettings;
            if (!listing || _connecting || _joining || _creating || _hosting != null || _previewMe != null) return;
            if (Environment.TickCount64 < _retryAt) return;
            RefreshOnline(lists: true);
        }

        private void ConnectFailed()
        {
            _retryAt = Environment.TickCount64 + RetryMs[Math.Min(_retries, RetryMs.Length - 1)];
            _retries++;
        }

        // the lobby: who came and went (the server's member pushes; the host's match also says so through PlayersChanged)
        private void TickLobby()
        {
            OnlineMatch? m = _hosting ?? _joined ?? Kept;
            if (m == null || m.Disposed) return;
            bool changed = false;
            while (m.Client.Events.TryDequeue(out OnlineMsg? ev))
            {
                if (ev is not MemberEvent member || member.GameId != m.Game.Id) continue;
                m.Members.RemoveAll(c => c.Code == member.Player.Code);
                if (member.Joined) m.Members.Add(member.Player);
                // (a served game: its owner left the lobby, the next player owns it and is the game's host on every screen)
                if (member.Owner is string owner && m.Members.FirstOrDefault(c => c.Code == owner) is PlayerCard next) m.Game.Host = next;
                changed = true;
            }
            // once a second: the host's LOBBY to everyone (also from RESULTS), a joiner's LOOK while it's in the lobby (or
            // choosing a hunter from it)
            int page = _onlineMenu?.Page?.Index ?? -1;
            bool inLobby = (m == _hosting || m == _joined) && (page == s_onLobby || page == s_onLicense && _licReturn == s_onLobby
                || MultiplayerMenu.InOnlineSetup && page == MultiplayerMenu.HunterPage || KeepsLobby(page));
            m.Net.LobbyTick(inLobby);
            if (changed) Redraw();
        }

        // the match session's lobby changed (its receive thread): drawn again on the menu thread
        private void LobbyChangedThere() => Post(Redraw);

        // a friend's invite (only seen on the hub, BROWSE and details: elsewhere the connection is closed)
        private void Invited(InviteEvent invite)
        {
            if (_joining || _creating || Import.Popup == null) return;
            string from = OnlineNames.Display(invite.From.Name, invite.From.Tag).ToLowerInvariant();
            string mode = ModeOf(invite.Game) is GameMode m ? ModeName(m) : invite.Game.Info.Mode.ToLowerInvariant();
            Import.Popup.Ask($"{from} invites you to a {mode} game.\n\njoin it?", yes =>
            {
                if (yes && _onlineMenu != null) JoinCard(_onlineMenu, invite.Game);
            });
        }

        private void CloseOnline()
        {
            OnlineClient? c = _client;
            _client = null;
            if (c != null) _ = Task.Run(async () => await c.DisposeAsync());
        }

        // connect if needed, then the counts, the open games and the friends online (off the menu thread)
        private void RefreshOnline(bool lists)
        {
            _nextRefresh = Environment.TickCount64 + RefreshEveryMs;
            if (_connecting || _joining || _creating || _hosting != null || _previewMe != null) return;
            OnlineClient? client = _client;
            if (client != null && !client.Connected)
            {
                CloseOnline();
                client = null;
            }
            if (client == null)
            {
                _connecting = true;
                OnlinePlayer me = Me();
                string? dir = ProfileDir;
                string? license = Multiplayer != null ? LicenseJson(Multiplayer.License) : null;
                Task.Run(async () =>
                {
                    try
                    {
                        OnlineIdentity id = OnlineIdentity.LoadOrCreate(dir);
                        // after a match: its connection, once the server has this player out of that game (a new login now
                        // would cut that LeaveGame off and the server would keep the seat: N1, N21)
                        OnlineClient? back = await OnlineMatch.TakeReturnedClientAsync();
                        // a server that doesn't answer shows "can't reach the server" after 15 s, not "connecting..." for good
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_onlineStop.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        OnlineClient c = back ?? await OnlineClient.ConnectAsync(ServerUri, id, OnlineNames.Clean(me.Name),
                            OperatingSystem.IsAndroid() ? "android" : "desktop", timeout.Token);
                        if (back != null) Console.WriteLine("online: the match's connection is the hub's again");
                        _ = UploadLicense(c, license);
                        _ = FlushResults(c, dir); // (head-to-head reports still waiting: LAN matches, a lost connection)
                        Post(() =>
                        {
                            _connecting = false;
                            _client = c;
                            _netError = null;
                            _retries = 0;
                            KnowFindable(c.Findable);
                            OfferRejoin(c);
                            RefreshOnline(lists: true);
                            if (_onlineMenu is MenuEngine at) TryRehost(at); // (PLAY AGAIN after the host left)
                            if (_onlineMenu?.Page?.Index == s_onLicense) FetchLicense();
                            FetchReplayRecord(); // (a replay's license open: its head to head from the server)
                        });
                    }
                    catch (Exception ex) // (every kind: an unlisted one, such as Wine's crypto error, left "connecting..." for good)
                    {
                        Console.Error.WriteLine("online: can't connect: " + ex);
                        Post(() =>
                        {
                            _connecting = false;
                            _netError = Problem(ex);
                            ConnectFailed();
                            Redraw();
                        });
                    }
                });
                return;
            }
            if (!lists) return;
            Task.Run(async () =>
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    GamesMsg games = await client.RequestAsync<GamesMsg>(new ListGamesReq());
                    int ping = (int)clock.ElapsedMilliseconds;
                    RosterMsg roster = await client.RequestAsync<RosterMsg>(new RosterReq());
                    List<PlayerCard> recent = (await client.RequestAsync<PlayersMsg>(new RecentReq())).Players;
                    int hunters = await HuntersOnline();
                    await SendPing(client, ping); // (Find Game's region and host pick: OnlineMenus.Find.cs)
                    Post(() =>
                    {
                        _games = games.Games;
                        _gamesListed = true;
                        _pingMs = ping;
                        _friendsOn = roster.Friends.Where(f => f.Presence != Presence.Offline)
                            .OrderBy(f => f.Presence == Presence.Lobby ? 0 : f.Presence == Presence.Match ? 1 : 2).ToList();
                        _roster = roster;
                        _recent = recent;
                        if (hunters >= 0) _hunters = hunters;
                        _netError = null;
                        Redraw();
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _netError = Problem(ex);
                        Redraw();
                    });
                }
            });
        }

        // the server's health line ("ok 0.1.0 online 3"): its players online count
        private static async Task<int> HuntersOnline()
        {
            try
            {
                Uri ws = ServerUri;
                var health = new UriBuilder(ws) { Scheme = ws.Scheme == "wss" ? "https" : "http", Path = "/v1/health" }.Uri;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string[] parts = (await http.GetStringAsync(health)).Trim().Split(' ');
                int at = Array.IndexOf(parts, "online");
                return at >= 0 && at + 1 < parts.Length && int.TryParse(parts[at + 1], out int n) ? n : -1;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return -1;
            }
        }

        // why something online failed, in words (not .NET's: a trimmed Android build gives resource ids such as
        // "net_webstatus_connectfailure" for a WebSocket or HTTP error: N21)
        private static string Problem(Exception ex) => ex switch
        {
            OperationCanceledException => "no answer from the server in 15 s.",
            OnlineException { Code: "disconnected" or "timeout" } => "lost the server. connecting again...",
            OnlineException o => o.Message.ToLowerInvariant(),
            System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or System.Net.Sockets.SocketException
                => "can't reach the server. trying again...",
            _ => ex.Message.ToLowerInvariant(),
        };

        private OnlinePlayer Me() => OnlineMe?.Invoke() ?? new OnlinePlayer(OnlineNames.Default, -1, Hunter.Samus, null, null);

        // the open games after the filters: this game version's first, then the others (shown, but not joinable)
        private List<GameCard> Shown() => _games
            .Where(g => _modeFilter == 0 || ModeOf(g) is GameMode m && Family(m) == OnModes[_modeFilter - 1])
            .Where(g => !_friendsOnly || g.Host.Friend || ShowRivals && g.Host.Rival)
            .Where(ExtrasWanted)
            .OrderBy(g => g.Info.Version == VersionKey ? 0 : 1).ThenBy(g => g.Host.Friend ? 0 : g.Host.Rival ? 1 : 2).ToList();

        // ONLINE SETTINGS' NO EXTRA PICKUP / CHARACTER GAMES: those games aren't listed (or joined: Join, FIND GAME); a game in
        // an imported arena isn't listed with NO EXTRA ARENA GAMES or on a device that doesn't have it (owner 2026-10-10, N28)
        private bool ExtrasWanted(GameCard g) => !(OnlineSettings.NoExtraPickups && g.Info.ExtraPickups)
            && !(OnlineSettings.NoExtraCharacters && g.Info.ExtraCharacters)
            && !(ExtraArenas.IsExtra(g.Info.Arena) && (OnlineSettings.NoExtraArenas || !ExtraArenas.Have(g.Info.Arena)));

        private static GameMode? ModeOf(GameCard g) => Enum.TryParse(g.Info.Mode, out GameMode m) ? m : null;

        private static GameMode Family(GameMode m) => m switch
        {
            GameMode.BattleTeams => GameMode.Battle, GameMode.BountyTeams => GameMode.Bounty, GameMode.DefenderTeams => GameMode.Defender,
            GameMode.NodesTeams => GameMode.Nodes, GameMode.SurvivalTeams => GameMode.Survival, _ => m
        };

        private int OnGamePages => Math.Max(1, (Shown().Count + OnRows - 1) / OnRows);

        private GameCard? OpenGame => _games.FirstOrDefault(g => g.Id == _gameOpen);

        // the items for what's known now (after any network result)
        private void Redraw()
        {
            MenuEngine? menu = _onlineMenu;
            int page = menu?.Page?.Index ?? -1;
            if (menu == null) return;
            if (page == s_onHub) ShowHub(menu);
            else if (page == s_onBrowse) ShowGames(menu, Array.FindIndex(_bRow, i => menu.ItemState(i) == MenuState.Focused) is int f && f >= 0 ? f : -1);
            else if (page == s_onDetail) ShowOnGame(menu);
            else if (page == s_onWho) ShowWho(menu, focus: false);
            else if (page == s_onLobby) ShowLobby(menu, focus: false);
            else if (page == s_onFriends) ShowFriends(menu, -1);
            else if (page == s_onAdd) ShowAdd(menu, -1);
            else if (page == s_onLicense) ShowLicense(menu, focus: false);
            else if (page == s_onSettings) ShowSettings(menu, focus: false);
            else if (page == s_onFind) ShowFind(menu, focus: false);
            else if (page == s_onVote) ShowVote(menu, focus: false);
            else if (page == s_onChat) ShowChat(menu, focus: false);
        }

        private void ShowHub(MenuEngine menu)
        {
            for (int r = 0; r < OnFriendRows; r++)
            {
                bool joinable = r < _friendsOn.Count && _friendsOn[r].Presence == Presence.Lobby && _friendsOn[r].GameId != null;
                if (!joinable) menu.SetState(_hFriendJoin[r], MenuState.Hidden);
                else if (menu.ItemState(_hFriendJoin[r]) == MenuState.Hidden) menu.SetState(_hFriendJoin[r], MenuState.Idle);
            }
        }

        // focus: the row to focus (-1 = keep what's focused)
        private void ShowGames(MenuEngine menu, int focus)
        {
            List<GameCard> shown = Shown();
            if (_gameOffset >= shown.Count) _gameOffset = 0;
            int count = Math.Max(0, Math.Min(OnRows, shown.Count - _gameOffset));
            for (int r = 0; r < OnRows; r++)
            {
                if (r >= count)
                {
                    menu.SetState(_bRow[r], MenuState.Hidden);
                    menu.SetState(_bJoin[r], MenuState.Hidden);
                    continue;
                }
                bool here = shown[_gameOffset + r].Info.Version == VersionKey;
                if (focus >= 0) menu.SetState(_bRow[r], r == Math.Min(focus, count - 1) ? MenuState.Focused : MenuState.Idle);
                else if (menu.ItemState(_bRow[r]) == MenuState.Hidden) menu.SetState(_bRow[r], MenuState.Idle);
                MenuState join = here ? MenuState.Idle : MenuState.State5;
                if (menu.ItemState(_bJoin[r]) != MenuState.Focused || !here) menu.SetState(_bJoin[r], join);
            }
            if (count == 0 && focus >= 0) menu.SetState(_bRefresh, MenuState.Focused);
            _strings.Set(_onBrowseTitle, OnGamePages > 1 ? $"BROWSE GAMES {_gameOffset / OnRows + 1}/{OnGamePages}" : "BROWSE GAMES");
        }

        private void ShowOnGame(MenuEngine menu)
        {
            GameCard? g = OpenGame;
            if (g == null)
            {
                menu.GoTo(s_onBrowse); // gone (started, closed or full since the list came)
                return;
            }
            string arena = ArenaLabel(g.Info.Arena);
            _strings.Set(_onDetailTitle, (ModeOf(g) is GameMode m ? ModeName(m) + " - " + arena : arena).ToUpperInvariant());
            bool here = g.Info.Version == VersionKey;
            if (!here) menu.SetState(_gJoin, MenuState.State5);
            else if (menu.ItemState(_gJoin) is MenuState.State5 or MenuState.Hidden) menu.SetState(_gJoin, MenuState.Focused);
            int seats = Math.Min(Math.Clamp(g.Info.MaxPlayers, 1, 8), _gSeat.Length);
            for (int k = 0; k < _gSeat.Length; k++) Show(menu, _gSeat[k], k < seats && k < g.Members.Count); // (open seats: nobody to show)
        }

        // Returns true when the call was one of ONLINE PLAY's
        private bool OnlineCall(MenuEngine menu, int call)
        {
            if ((call < OnQuickCall || call > OnLastCall) && (call < FriendsFirstCall || call > FriendsLastCall)
                && (call < LicenseFirstCall || call > LicenseLastCall) && (call < SettingsFirstCall || call > SettingsLastCall)
                && (call < FindFirstCall || call > FindLastCall) && (call < ChatFirstCall || call > ChatLastCall)
                && (call < OnTeam8Call || call > OnTeam8LastCall) && (call < LobbyBotsFirstCall || call > LobbyBotsLastCall)) return false;
            _onlineMenu = menu;
            if (call is >= ChatFirstCall and <= ChatLastCall)
            {
                ChatCall(menu, call);
                return true;
            }
            if (call == OnLeaveCall && ChatBack(menu)) return true; // (REPORT A HUNTER's back)
            if (call is >= FindFirstCall and <= FindLastCall)
            {
                FindCall(menu, call);
                return true;
            }
            if (call == OnLeaveCall && FindBack(menu)) return true; // (FIND GAME's and the vote's back)
            if (call is >= SettingsFirstCall and <= SettingsLastCall)
            {
                SettingsCall(menu, call);
                return true;
            }
            if (call is >= LicenseFirstCall and <= LicenseLastCall)
            {
                LicenseCall(menu, call);
                return true;
            }
            if (call == OnLeaveCall && menu.Page?.Index == s_onLicense)
            {
                menu.GoTo(_licReturn >= 0 ? _licReturn : s_onHub); // (HUNTER LICENSE's back: where it was opened from)
                return true;
            }
            if (call is >= OnTeam8Call and <= OnTeam8LastCall or >= LobbyBotsFirstCall and <= LobbyBotsLastCall)
            {
                LobbyCall(menu, call); // (before FRIENDS' catch-all below: P8's TEAM boxes never got here before)
                return true;
            }
            if (call >= FriendsFirstCall)
            {
                FriendsCall(menu, call);
                return true;
            }
            if (call is >= OnWhoCall and <= OnCreateGameCall or >= OnBotLevelCall and <= OnBotLevelRightCall)
            {
                WhoCall(menu, call);
                return true;
            }
            if (call is >= OnSeatCall and <= OnLeaveCall or >= OnTeamCall and <= OnLastCall or >= OnTeam8Call and <= OnTeam8LastCall
                or >= LobbyBotsFirstCall and <= LobbyBotsLastCall)
            {
                LobbyCall(menu, call);
                return true;
            }
            if (call is >= OnFriendJoinCall and < OnFriendJoinCall + OnFriendRows)
            {
                int r = call - OnFriendJoinCall;
                if (r < _friendsOn.Count && _friendsOn[r].GameId is string id) PickThenJoin(menu, id, OnlineNames.Display(_friendsOn[r].Name, _friendsOn[r].Tag));
                return true;
            }
            if (call is >= OnRowCall and < OnRowCall + OnRows or >= OnRowJoinCall and < OnRowJoinCall + OnRows)
            {
                int r = call % 10, at = _gameOffset + r;
                List<GameCard> shown = Shown();
                if (at >= shown.Count) return true;
                GameCard g = shown[at];
                _gameOpen = g.Id;
                if (call - r == OnRowCall) menu.GoTo(s_onDetail);
                else JoinCard(menu, g);
                return true;
            }
            switch (call)
            {
            case OnQuickCall:
                // FIND GAME when the server has it (its defaults are QUICK MATCH: OnlineMenus.Find.cs); else the first open game
                if (_client != null && FindOn)
                {
                    menu.GoTo(s_onFind);
                    break;
                }
                if (_gamesListed && _client != null && !_games.Any(g => g.Info.Version == VersionKey)) NoOpenGames(menu);
                else PickThenJoin(menu, null, null);
                break;
            case OnBrowseCall:
                menu.GoTo(s_onBrowse);
                break;
            case OnCreateCall:
                // the local setup pages (mode, rules, SELECT HUNTER), then WHO CAN JOIN
                if (Multiplayer != null) Multiplayer.BeginOnlineSetup(menu, s_onWho, s_onHub, fresh: true);
                else Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                break;
            case OnAllFriendsCall:
                menu.GoTo(s_onFriends);
                break;
            case OnHostLicenseCall:
                if (OpenGame is GameCard hosted) OpenLicense(menu, hosted.Host);
                break;
            case OnPrevCall:
            case OnNextCall:
                if (OnGamePages > 1)
                {
                    int pages = OnGamePages;
                    _gameOffset = (_gameOffset / OnRows + (call == OnNextCall ? 1 : pages - 1)) % pages * OnRows;
                    ShowGames(menu, 0);
                }
                break;
            case OnModeCall:
                _modeFilter = (_modeFilter + 1) % (OnModes.Length + 1);
                _gameOffset = 0;
                ShowGames(menu, -1);
                break;
            case OnFriendsOnlyCall:
                _friendsOnly = !_friendsOnly;
                _gameOffset = 0;
                ShowGames(menu, -1);
                break;
            case OnRefreshCall:
                RefreshOnline(lists: true);
                break;
            case OnJoinCall:
                if (OpenGame is GameCard open) JoinCard(menu, open);
                break;
            }
            return true;
        }

        private void JoinCard(MenuEngine menu, GameCard g)
        {
            if (JoinBlock(g.Info) is string why)
            {
                Import.Popup?.Inform(JoinBlockMessage(g.Info, why));
                return;
            }
            PickThenJoin(menu, g.Id, OnlineNames.Display(g.Host.Name, g.Host.Tag));
        }

        // why this device can't join this game, short for a JOIN's side line (null: it can). Every JOIN greys out on it and
        // the hub counts only games without one (release 0.2.0 F2: an Echoes arena this device lacks was counted + joinable)
        private string? JoinBlock(GameInfo i) =>
            i.Version != VersionKey ? "other version"
            : ExtraArenas.IsExtra(i.Arena) && !ExtraArenas.Have(i.Arena) ? "needs echoes import"
            : ExtraArenas.IsExtra(i.Arena) && OnlineSettings.NoExtraArenas ? "extra arenas off"
            : OnlineSettings.NoExtraPickups && i.ExtraPickups ? "extra pickups off"
            : OnlineSettings.NoExtraCharacters && i.ExtraCharacters ? "extra characters off"
            : null;

        // the same for a game known only by its id (a friend's): an unlisted game (no card) is left to the server
        private string? JoinBlock(string? gameId) => _games.FirstOrDefault(g => g.Id == gameId) is GameCard g ? JoinBlock(g.Info) : null;

        // the popup for a blocked JOIN (the long form of JoinBlock)
        private static string JoinBlockMessage(GameInfo i, string reason) => reason switch
        {
            "other version" => "this game runs a different version.\n\nboth players need the same game update and rom.",
            "needs echoes import" => $"this game is in {ExtraArenas.Label(i.Arena)}, an arena you don't have.\n\nimport metroid prime 2: echoes on import games to play it.",
            "extra arenas off" => "this game is in an extra arena.\n\nturn off no extra arena games in online settings to join it.",
            "extra pickups off" => "this game has extra pickups.\n\nturn off no extra pickup games in online settings to join it.",
            _ => "this game has extra characters.\n\nturn off no extra character games in online settings to join it.",
        };

        // JOIN, QUICK MATCH (null: the first open game), a friend's JOIN: this player's hunter first (SELECT HUNTER on its own,
        // its MODE / RULES tabs steps only), then the join from the lobby page; back on SELECT HUNTER returns here
        private void PickThenJoin(MenuEngine menu, string? gameId, string? host)
        {
            if (_joining || _creating || !Reachable()) return;
            if (_games.FirstOrDefault(g => g.Id == gameId) is GameCard blocked && JoinBlock(blocked.Info) is string why)
            {
                Import.Popup?.Inform(JoinBlockMessage(blocked.Info, why));
                return;
            }
            _joinFrom = menu.Page?.Index ?? s_onHub;
            if (Multiplayer == null)
            {
                Join(gameId, host);
                return;
            }
            _pendingJoin = (gameId, host);
            Multiplayer.BeginOnlineSetup(menu, s_onLobby, _joinFrom, fresh: false);
        }

        // QUICK MATCH with nothing open for this game version: host one (CREATE's setup) instead of a dead end
        private void NoOpenGames(MenuEngine menu)
        {
            if (Import.Popup == null) return;
            Import.Popup.Ask("no open games right now.\n\nhost one for others to join?", yes =>
            {
                if (yes && Multiplayer != null && !_joining && !_creating) Multiplayer.BeginOnlineSetup(menu, s_onWho, s_onHub, fresh: true);
            });
        }

        // where a join that didn't get in goes back to: the page its JOIN was on
        private int JoinBack => _joinFrom >= 0 && _joinFrom != s_onLobby ? _joinFrom : s_onHub;

        // the hub's connection is up; else says why not (and tries again)
        private bool Reachable()
        {
            if (_client is { Connected: true }) return true;
            Import.Popup?.Inform(_connecting ? "connecting to the server..." : "can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
            if (!_connecting) RefreshOnline(lists: true);
            return false;
        }

        // ---- CREATE: WHO CAN JOIN ----

        private static readonly (string Label, string Line)[] WhoChoices =
        {
            ("public", "anyone can find and join"), ("friends", "only your friends see it"),
            ("friends + rivals", "your friends and rivals see it"), ("invite only", "only hunters you invite"),
        };

        private const int MaxSeats = 8; // PlayerEntity.SlotCapacity (P8: 5-8 seats get the half-height lobby cards, OnlineMenus.Seats8.cs)

        private void ShowWho(MenuEngine menu, bool focus)
        {
            s_bots = Math.Clamp(s_bots, 0, s_seats - 2);
            if (!WhoShown.Contains(s_who)) s_who = 1; // (FRIENDS + RIVALS hidden: friends)
            if (focus)
            {
                for (int r = 0; r < WhoRows; r++)
                    menu.SetState(_wRow[r], !WhoShown.Contains(r) ? MenuState.Hidden : r == s_who ? MenuState.Focused : MenuState.Idle);
            }
        }

        private void WhoCall(MenuEngine menu, int call)
        {
            switch (call)
            {
            case >= OnWhoCall and < OnWhoCall + WhoRows:
                s_who = call - OnWhoCall;
                break;
            case OnPlayersCall:
                s_seats = s_seats >= MaxSeats ? 2 : s_seats + 1;
                break;
            case OnPlayersLeftCall:
            case OnPlayersRightCall:
                s_seats = Math.Clamp(s_seats + (call == OnPlayersRightCall ? 1 : -1), 2, MaxSeats);
                break;
            case OnBotsCall:
                s_bots = s_bots >= s_seats - 2 ? 0 : s_bots + 1;
                break;
            case OnBotsLeftCall:
            case OnBotsRightCall:
                s_bots = Math.Clamp(s_bots + (call == OnBotsRightCall ? 1 : -1), 0, s_seats - 2);
                break;
            case OnCreateGameCall:
                CreateGame(menu);
                break;
            case OnBotLevelCall:
                s_botLevel = (s_botLevel + 1) % 3;
                break;
            case OnBotLevelLeftCall:
            case OnBotLevelRightCall:
                s_botLevel = Math.Clamp(s_botLevel + (call == OnBotLevelRightCall ? 1 : -1), 0, 2);
                break;
            }
            ShowWho(menu, focus: false);
        }

        // CREATE: the game on the server (who sees it: WHO CAN JOIN; seats for people = PLAYERS - BOTS), this device hosting
        // its match from the setup (seat 0 = this player, then the bots), then the lobby
        private void CreateGame(MenuEngine menu)
        {
            if (_creating || _hosting != null || _joining) return;
            OnlineClient? client = _client;
            if (client == null || !client.Connected)
            {
                Import.Popup?.Inform(_connecting ? "connecting to the server..." : "can't reach the server.\n\n" + (_netError ?? "try again in a moment."));
                if (!_connecting) RefreshOnline(lists: false);
                return;
            }
            MatchSettings template;
            try
            {
                template = MultiplayerMenu.BuildMatch();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
            {
                Import.Popup?.Inform("can't make this game.\n\n" + ex.Message.ToLowerInvariant());
                return;
            }
            OnlinePlayer me = Me();
            template.Players.RemoveAll(p => p.Bot || p.Remote);
            if (template.Players.Count == 0) return;
            template.Players[0].Nickname = OnlineNames.Clean(me.Name);
            template.Players[0].RankLevel = me.Rank;
            Hunter[] others = BotHunters.Where(h => h != template.Players[0].Hunter).OrderBy(_ => Random.Shared.Next()).ToArray();
            // (P8: more bots than hunters left: they repeat, as the vanilla menu's)
            for (int b = 0; b < s_bots && others.Length > 0; b++) template.Players.Add(MultiplayerMenu.OnlineBot(others[b % others.Length], s_botLevel));
            int people = Math.Max(2, s_seats - s_bots);
            bool open = s_who == 0, friends = s_who is 0 or 1 or 2, rivals = s_who is 0 or 2;
            HostGame(menu, client, template, people, open, friends, rivals,
                $"{people} people + {s_bots} bots level {s_botLevel}, {WhoChoices[s_who].Label}");
        }

        // the game on the server and this device hosting its match (CREATE, and PLAY AGAIN after the host left), then the lobby
        private void HostGame(MenuEngine menu, OnlineClient client, MatchSettings template, int people, bool open, bool friends,
            bool rivals, string note)
        {
            if (_creating || _hosting != null || _joining) return;
            string romKey = LanSession.RomKeyFor(Paths.MphKey);
            _creating = true;
            _createCancelled = false;
            _client = null; // the match owns the connection from here
            Import.Popup?.BusyCancel("creating your game...", () => _createCancelled = true);
            Task.Run(async () =>
            {
                try
                {
                    OnlineMatch match = await OnlineMatch.HostAsync(client, ServerUri, template, romKey, people, open, friends, rivals);
                    Post(() =>
                    {
                        _creating = false;
                        if (_createCancelled)
                        {
                            match.Dispose();
                            Import.Popup?.Close();
                            RefreshOnline(lists: false);
                            return;
                        }
                        Import.Popup?.Close();
                        if (match.Served)
                        {
                            // the server runs the match: this device is a joiner that owns the lobby, waiting for its START
                            _joined = match;
                            _joining = true;
                            _joinCancelled = false;
                            match.Net.PlayersChanged += LobbyChangedThere;
                            Console.WriteLine($"online: the server runs {match.Game.Id}, this device owns its lobby ({template.Mode} in {template.Arena}, {note})");
                            menu.GoTo(s_onLobby);
                            Task.Run(() => WaitThenPlay(match));
                            return;
                        }
                        _hosting = match;
                        _hostTemplate = template;
                        match.Net.PlayersChanged += LobbyChangedThere;
                        Console.WriteLine($"online: hosting {match.Game.Id} ({template.Mode} in {template.Arena}, {note})");
                        menu.GoTo(s_onLobby);
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or IOException)
                {
                    Console.Error.WriteLine("online: can't create: " + ex);
                    _ = Task.Run(async () => await client.DisposeAsync());
                    Post(() =>
                    {
                        _creating = false;
                        if (_createCancelled) Import.Popup?.Close();
                        else Import.Popup?.Inform("couldn't create the game.\n\n" + Problem(ex));
                        RefreshOnline(lists: false);
                    });
                }
            });
        }

        // the hunters a bot may be (the ROM's bot names: MultiplayerMenu.OnlineBot)
        private static readonly Hunter[] BotHunters =
            { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel };

        // ---- the lobby ----

        private enum SeatKind { You, Person, Bot, Open }

        // Index: the seat in the match (Settings.Players), -1 for an open seat or someone the match doesn't have yet
        private sealed record LobbySeat(SeatKind Kind, MatchPlayer? Player, PlayerCard? Card, bool Host, int Index = -1);

        // who sits where: the people (the match's players, then anyone the server says came since a joiner's WELCOME), the
        // open seats, the bots (the ones that still fit: NetMatchSession cuts bots past MaxSeats)
        private List<LobbySeat> SeatsNow()
        {
            var seats = new List<LobbySeat>();
            OnlineMatch? m = _hosting ?? _joined;
            MatchSettings? s = m?.Net.Settings;
            if (m == null || s == null) return seats;
            string hostCode = m.Game.Host.Code;
            var cards = m.Members.ToList();
            PlayerCard? CardOf(string? name)
            {
                PlayerCard? c = cards.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (c != null) cards.Remove(c);
                return c;
            }
            int local = _hosting != null ? 0 : s.LocalSeat;
            // (a joiner's match has no bots of its own, MatchClient.ForSeat: the host's LOBBY says which seats are)
            bool IsBot(int i) => s.Players[i].Bot || i < m.Net.Lobby.Count && m.Net.Lobby[i].Bot;
            // (a served game's owner: the bots as the match host's LOBBY has them, with their levels, for its bot controls)
            MatchSettings? hostLobby = m == Runner && !m.Net.IsHost ? m.Net.OwnerLobby() : null;
            if (hostLobby != null && hostLobby.Players.Count != s.Players.Count) hostLobby = null;
            MatchPlayer BotOf(int i) => hostLobby?.Players[i] ?? s.Players[i];
            for (int i = 0; i < s.Players.Count; i++)
            {
                MatchPlayer p = s.Players[i];
                if (IsBot(i)) continue;
                PlayerCard? c;
                if (_hosting != null && i == 0)
                {
                    c = m.Game.Host;
                    cards.RemoveAll(x => x.Code == hostCode);
                }
                else c = CardOf(p.Nickname);
                // (seat 0 is always the host's: NetMatchSession puts the host's own players first)
                // (a served game: the owner's card, wherever it sits)
                seats.Add(new LobbySeat(i == local ? SeatKind.You : SeatKind.Person, p, c, m.Served ? c?.Code == hostCode : i == 0, i));
            }
            foreach (PlayerCard c in cards) seats.Add(new LobbySeat(SeatKind.Person, null, c, c.Code == hostCode));
            int people = seats.Count;
            List<int> bots = Enumerable.Range(0, s.Players.Count).Where(IsBot).ToList();
            int total = Math.Min(MaxSeats, Math.Max(people, m.Game.Info.MaxPlayers + bots.Count));
            int fit = Math.Max(0, Math.Min(bots.Count, total - people));
            int open = Math.Max(0, Math.Min(total - people - fit, m.Game.Info.MaxPlayers - people));
            open = Math.Min(open + FreedSeats(m), Math.Max(0, MaxSeats - people - fit)); // (seats bots left: ADD BOT, OnlineMenus.LobbyBots.cs)
            for (int i = 0; i < open; i++) seats.Add(new LobbySeat(SeatKind.Open, null, null, false));
            for (int i = 0; i < fit; i++) seats.Add(new LobbySeat(SeatKind.Bot, BotOf(bots[i]), null, false, bots[i]));
            return seats.Take(LobbySeats8).ToList();
        }

        private void ShowLobby(MenuEngine menu, bool focus)
        {
            List<LobbySeat> seats = SeatsNow();
            int you = seats.FindIndex(x => x.Kind == SeatKind.You);
            // (P8: the 2 x 2 cards or the half-height ones, the other set hidden)
            bool eight = LobbyEight(seats.Count);
            for (int k = 0; k < LobbySeats; k++) if (eight) menu.SetState(_lbSeat[k], MenuState.Hidden);
            for (int k = 0; k < LobbySeats8; k++) if (!eight) menu.SetState(_lbSeat8[k], MenuState.Hidden);
            for (int k = 0; k < (eight ? LobbySeats8 : LobbySeats); k++)
            {
                int item = LobbySeatItem(k, seats.Count);
                if (k >= seats.Count) menu.SetState(item, MenuState.Hidden);
                else if (focus) menu.SetState(item, k == Math.Max(0, you) ? MenuState.Focused : MenuState.Idle);
                else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
            }
            bool host = Runner != null;
            bool teams = (_hosting ?? _joined)?.Net.Settings is MatchSettings ts && MatchSettings.IsTeamMode(ts.Mode);
            // (TEAM: the host's on every seat, a guest's on its own: release 0.2.0 F2)
            for (int k = 0; k < LobbySeats; k++) Show(menu, _lbTeam[k], !eight && teams && k < seats.Count && TeamBoxShown(seats[k], host));
            for (int k = 0; k < LobbySeats8; k++) Show(menu, _lbTeam8[k], eight && teams && k < seats.Count && TeamBoxShown(seats[k], host));
            ShowLobbyBots(menu, seats);
            if (!host) menu.SetState(_lbInvite, MenuState.Hidden);
            else if (menu.ItemState(_lbInvite) == MenuState.Hidden) menu.SetState(_lbInvite, MenuState.Idle);
            Show(menu, _lbRules, host);
            if (!host) menu.SetState(_lbStart, MenuState.Hidden);
            else if (Runner!.Net.Joined == 0) menu.SetState(_lbStart, MenuState.State5);
            else if (menu.ItemState(_lbStart) is MenuState.State5 or MenuState.Hidden) menu.SetState(_lbStart, MenuState.Idle);
            Show(menu, _lbTalk, LobbyTalkShown()); // (voice's TALK under START: OnlineMenus.Voice.cs)
            int filled = seats.Count(x => x.Kind != SeatKind.Open);
            _strings.Set(_onLobbyTitle, $"LOBBY {filled}/{seats.Count}");
        }

        private void LobbyCall(MenuEngine menu, int call)
        {
            switch (call)
            {
            case >= OnSeatCall and < OnSeatCall + LobbySeats8:
            {
                List<LobbySeat> seats = SeatsNow();
                int k = call - OnSeatCall;
                if (k >= seats.Count) break;
                if (seats[k].Kind == SeatKind.You && Multiplayer != null && (_hosting ?? _joined) is OnlineMatch mine) EditOwnSeat(menu, mine);
                else if (seats[k].Kind == SeatKind.Bot && Runner != null) StepBotHunter(seats[k]); // (OnlineMenus.LobbyBots.cs)
                else if (seats[k].Kind == SeatKind.Open && Runner != null) Invite();
                else if (seats[k].Kind == SeatKind.Person && seats[k].Card is PlayerCard card) OpenLicense(menu, card); // (the game stays)
                break;
            }
            case >= OnTeamCall and <= OnLastCall or >= OnTeam8Call and <= OnTeam8LastCall:
            {
                // the host swaps a seat's team (the one it plays now: TeamAssignment fills the unset ones); a guest asks for the
                // other team on its own card (release 0.2.0 F2)
                List<LobbySeat> seats = SeatsNow();
                int k = call - (call >= OnTeam8Call ? OnTeam8Call : OnTeamCall);
                if (Runner == null && _joined is OnlineMatch jm && jm.Net.Settings is MatchSettings js && k < seats.Count
                    && seats[k].Kind == SeatKind.You && seats[k].Index >= 0 && seats[k].Index < js.Players.Count)
                {
                    int[] now = js.TeamAssignment();
                    jm.Net.RequestTeam(1 - now[seats[k].Index]);
                    break;
                }
                if (Runner is not OnlineMatch hm || hm.Net.Settings is not MatchSettings hs || k >= seats.Count || seats[k].Index < 0) break;
                int[] teams = hs.TeamAssignment();
                if (seats[k].Index < teams.Length) hm.Net.SetTeam(seats[k].Index, 1 - teams[seats[k].Index]);
                break;
            }
            case >= LobbyBotsFirstCall and <= LobbyBotsLastCall:
                LobbyBotsCall(call);
                break;
            case OnInviteCall:
                if (Runner != null) Invite();
                break;
            case OnRulesCall:
                if (Runner is OnlineMatch hosted && Multiplayer != null) EditOwnSeat(menu, hosted);
                break;
            case OnStartCall:
                if (Runner is OnlineMatch m) TryStart(m);
                break;
            case OnLeaveCall:
                if (_hosting is { } h && h.Net.Joined > 0 && Import.Popup != null)
                {
                    Import.Popup.Ask("leave your game?\n\nit closes for everyone in it.", yes =>
                    {
                        if (yes) menu.GoTo(s_onHub);
                    });
                }
                else menu.GoTo(s_onHub); // (EnterOnline closes the game, or ends the joiner's wait)
                break;
            }
        }

        // this player's own seat card (or the host's EDIT RULES): SELECT HUNTER, then back here; the others see "choosing a
        // hunter" meanwhile. The host's has its MODE / RULES tabs live, and its changes come back to this lobby
        // (TakeRulesEdit); a guest's is the hunter only.
        private void EditOwnSeat(MenuEngine menu, OnlineMatch mine)
        {
            if (mine.Net.IsHost) mine.Net.SetHostChoosing(true);
            else
            {
                (Hunter look, string? sk, string? ch) = MultiplayerMenu.MyLook;
                mine.Net.SetLook(look, ch, sk, choosing: true, Me().Rank);
            }
            Multiplayer!.BeginOnlineSetup(menu, s_onLobby, s_onLobby, fresh: false, edit: mine.Net.RunsLobby);
        }

        // START: nobody joined, a team mode with an empty team, someone still on the results (start without them?) or still
        // choosing a hunter (start anyway?), then the match
        private void TryStart(OnlineMatch m)
        {
            if (m.Net.Joined == 0)
            {
                Import.Popup?.Inform("nobody has joined yet.\n\nwait for a hunter to join, or invite friends.");
                return;
            }
            if (m.Net.Settings is MatchSettings s && MatchSettings.IsTeamMode(s.Mode) && s.TeamAssignment() is int[] teams
                && (Array.IndexOf(teams, 0) < 0 || Array.IndexOf(teams, 1) < 0))
            {
                Import.Popup?.Inform("both teams need a hunter.\n\nswap a team with TEAM on a card.");
                return;
            }
            static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Take(3).Select(n => n.ToLowerInvariant()))
                + (names.Count > 3 ? $" +{names.Count - 3}" : "");
            IReadOnlyList<string> away = m.Net.NotBack(), choosing = m.Net.StillChoosing();
            if (away.Count > 0 && Import.Popup != null)
            {
                Import.Popup.Ask($"{Names(away)} {(away.Count == 1 ? "is" : "are")} still on the results.\n\nstart without them?", yes =>
                {
                    if (!yes || Runner != m) return;
                    m.Net.KickNotBack("the game started without you.");
                    TryStart(m);
                });
                return;
            }
            if (choosing.Count > 0 && Import.Popup != null)
            {
                Import.Popup.Ask($"{Names(choosing)} {(choosing.Count == 1 ? "is" : "are")} still choosing a hunter.\n\nstart anyway?", yes =>
                {
                    if (yes && Runner == m) StartNow(m);
                });
                return;
            }
            StartNow(m);
        }

        private void StartNow(OnlineMatch m)
        {
            if (!m.Net.IsHost)
            {
                // a served game's owner: the server's match host starts it, and the lobby's wait (WaitThenPlay) plays it
                m.Start();
                Import.Popup?.BusyCancel("starting the match...", () => { }); // (Play closes it)
                _ = Task.Delay(StartWaitMs).ContinueWith(_ => Post(() =>
                {
                    if (_joined != m || m.Net.Started || m.Disposed) return;
                    Import.Popup?.Inform("the server didn't start the match.\n\nsomeone may not have this arena: try another.");
                }));
                return;
            }
            _hosting = null;
            _hostTemplate = null;
            m.Start(); // the others get the final match; this device plays it
            Play(m);
        }

        // the host left the lobby: the game closes on the server (OnlineMatch.Dispose: BYE, out of the game, socket closed)
        private void LeaveHosting()
        {
            OnlineMatch? m = _hosting;
            _hosting = null;
            _hostTemplate = null;
            m?.Dispose();
        }

        // INVITE FRIENDS (and an open seat): the host's friends and rivals who are online and not in a game, after a yes
        private void Invite()
        {
            OnlineMatch? m = Runner;
            if (m == null || _invitePending) return;
            _invitePending = true;
            Task.Run(async () =>
            {
                try
                {
                    RosterMsg roster = await m.Client.RequestAsync<RosterMsg>(new RosterReq());
                    List<PlayerCard> who = roster.Friends.Concat(ShowRivals ? roster.Rivals : new List<PlayerCard>()).GroupBy(c => c.Code)
                        .Select(g => g.First()).Where(c => c.Presence == Presence.Online && c.GameId == null).ToList();
                    Post(() =>
                    {
                        _invitePending = false;
                        if (who.Count == 0)
                        {
                            Import.Popup?.Inform("no friends online right now.\n\nadd friends from all friends.");
                            return;
                        }
                        string names = string.Join("\n", who.Take(4).Select(c => OnlineNames.Display(c.Name, c.Tag).ToLowerInvariant()))
                            + (who.Count > 4 ? $"\n+{who.Count - 4} more" : "");
                        Import.Popup?.Ask($"invite your friends online?\n\n{names}", yes =>
                        {
                            if (!yes) return;
                            foreach (PlayerCard c in who)
                            {
                                _ = SendInvite(m, c.Code);
                            }
                        });
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    Post(() =>
                    {
                        _invitePending = false;
                        Import.Popup?.Inform("couldn't invite.\n\n" + Problem(ex));
                    });
                }
            });
        }

        private static async Task SendInvite(OnlineMatch m, string code)
        {
            try
            {
                await m.Client.RequestAsync<OkMsg>(new InviteReq { Code = code });
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("online: invite " + code + ": " + ex.Message);
            }
        }

        // JOIN (a game id) or QUICK MATCH (null: the first open game for this version): into the game on the server, a seat
        // from its host, then the game's box until the host starts (NO leaves); the match then plays as a LAN joiner's does
        private void Join(string? gameId, string? host)
        {
            if (_joining || !Reachable()) return;
            // (N27: a game in an arena this device lacks; its host refuses the JOIN too, this says so before trying)
            if (_games.FirstOrDefault(g => g.Id == gameId) is GameCard card && JoinBlock(card.Info) is string why)
            {
                Import.Popup?.Inform(JoinBlockMessage(card.Info, why));
                return;
            }
            OnlineClient client = _client!;
            _joining = true;
            _joinCancelled = false;
            _client = null; // the match owns the connection from here (OnlineMatch.Dispose closes it)
            OnlinePlayer me = Me();
            string romKey = LanSession.RomKeyFor(Paths.MphKey);
            string what = host != null ? $"joining {host.ToLowerInvariant()}'s game..." : "finding a game...";
            Import.Popup?.BusyCancel(what, () => _joinCancelled = true);
            Task.Run(async () =>
            {
                OnlineMatch? match = null;
                try
                {
                    match = await OnlineMatch.JoinAsync(client, ServerUri, romKey, gameId, OnlineSettings.NoExtraPickups,
                        OnlineSettings.NoExtraCharacters);
                    string name = OnlineNames.Display(match.Game.Host.Name, match.Game.Host.Tag).ToLowerInvariant();
                    if (_joinCancelled) throw new OperationCanceledException();
                    if (!match.Net.Join(me.Hunter, OnlineNames.Clean(me.Name), me.Rank, timeoutMs: 15000, character: me.Character, skin: me.Skin))
                    {
                        throw new OnlineException("refused", match.Net.Status);
                    }
                    // the lobby (L_B, no START) until the host starts; LEAVE there (or any other page) cancels
                    OnlineMatch inLobby = match;
                    inLobby.Net.PlayersChanged += LobbyChangedThere;
                    Post(() =>
                    {
                        if (_joinCancelled) return;
                        Import.Popup?.Close();
                        _joined = inLobby;
                        Console.WriteLine($"online: in {name}'s game {inLobby.Game.Id} as seat {inLobby.Net.Settings?.LocalSeat}");
                        _onlineMenu?.GoTo(s_onLobby);
                    });
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or IOException)
                {
                    JoinEnded(match, client, ex);
                    return;
                }
                WaitThenPlay(match);
            });
        }

        // a joiner in the lobby (off the menu thread): waits for the host's START, then plays; the host leaving (or not heard
        // for HostSilentMs), being taken out, or LEAVE end it
        private void WaitThenPlay(OnlineMatch match)
        {
            try
            {
                while (!_joinCancelled && !match.Net.Started && !match.Net.HostLeft && match.Net.Kicked == null
                    && match.Net.SilentMs(0) < HostSilentMs)
                {
                    match.Net.WaitForStart(200);
                }
                if (_joinCancelled) throw new OperationCanceledException();
                if (match.Net.Kicked is string why) throw new OnlineException("kicked", why);
                if (!match.Net.Started && !match.Net.HostLeft) throw new OnlineException("host_gone", "no word from the host");
                if (!match.Net.Started || match.Net.Settings == null) throw new OnlineException("host_left", "the host left the game");
                Post(() => Play(match));
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException or IOException)
            {
                JoinEnded(match, null, ex);
            }
        }

        // the server still holds this player's seat in a started match (the app closed or lost its connection mid-match:
        // OnlineClient.Held, at login): REJOIN, or leave it (release test T1)
        private void OfferRejoin(OnlineClient c)
        {
            GameCard? held = c.Held;
            c.Held = null;
            if (held == null || Import.Popup == null) return;
            if (_joining || _creating || _hosting != null || _joined != null || OnlineMatch.Current != null) return;
            string mode = ModeOf(held) is GameMode m ? ModeName(m) : held.Info.Mode.ToLowerInvariant();
            Import.Popup.Ask($"your {mode} match in {ArenaLabel(held.Info.Arena)} is still going.\n\nrejoin it?", yes =>
            {
                if (yes)
                {
                    Rejoin(held);
                    return;
                }
                if (_client is OnlineClient now) _ = LeaveHeld(now);
            });
        }

        private static async Task LeaveHeld(OnlineClient client)
        {
            try
            {
                await client.RequestAsync<OkMsg>(new LeaveGameReq());
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                Console.Error.WriteLine("online: leave the held seat: " + ex.Message);
            }
        }

        // back into the started match the server holds this player's seat in (its slot, a new relay token), then as any join:
        // the host knows the seat and answers with START and a full snapshot, and the match screen opens
        private void Rejoin(GameCard held)
        {
            if (_joining || !Reachable()) return;
            OnlineClient client = _client!;
            _joining = true;
            _joinCancelled = false;
            _client = null; // the match owns the connection from here
            OnlinePlayer me = Me();
            string romKey = LanSession.RomKeyFor(Paths.MphKey);
            Import.Popup?.BusyCancel("rejoining your match...", () => _joinCancelled = true);
            Task.Run(() => RejoinRun(client, me, romKey));
        }

        private async Task RejoinRun(OnlineClient client, OnlinePlayer me, string romKey)
        {
            OnlineMatch? match = null;
            try
            {
                match = await OnlineMatch.RejoinAsync(client, ServerUri, romKey);
                if (_joinCancelled) throw new OperationCanceledException();
                if (!match.Net.Join(me.Hunter, OnlineNames.Clean(me.Name), me.Rank, timeoutMs: 15000, character: me.Character, skin: me.Skin))
                {
                    throw new OnlineException("refused", match.Net.Status);
                }
                Console.WriteLine($"online: rejoined {match.Game.Id} as seat {match.Net.Settings?.LocalSeat}");
            }
            catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException or IOException)
            {
                if (ex is OnlineException { Code: "not_found" })
                {
                    ex = new OnlineException("not_found", "that match is over");
                }
                JoinEnded(match, client, ex);
                return;
            }
            WaitThenPlay(match);
        }

        // a join that ended without a match (refused, cancelled, the host gone): out of the game, back where JOIN was (or, from
        // the lobby, the hub) and why
        private void JoinEnded(OnlineMatch? match, OnlineClient? client, Exception ex)
        {
            bool cancelled = ex is OperationCanceledException;
            bool served = match?.Served == true; // (its match host is the server's: "the host" is no player)
            if (!cancelled) Console.Error.WriteLine("online: can't join: " + ex);
            if (match != null) match.Dispose();
            else if (client != null) _ = Task.Run(async () => await client.DisposeAsync());
            Post(() =>
            {
                bool wasIn = _joined != null; // (in the lobby: the game is over, the hub; else back where JOIN was)
                _joining = false;
                _joined = null;
                int at = _onlineMenu?.Page?.Index ?? -1;
                if (at == s_onLobby || at == s_onLicense && _licReturn == s_onLobby || at == s_onChat) _onlineMenu!.GoTo(wasIn ? s_onHub : JoinBack);
                else if (MultiplayerMenu.InOnlineSetup && at == MultiplayerMenu.HunterPage) _onlineMenu!.GoTo(s_onHub); // (choosing)
                if (cancelled) Import.Popup?.Close();
                else if (ex is OnlineException { Code: "no_games" } && _onlineMenu != null) NoOpenGames(_onlineMenu);
                else Import.Popup?.Inform(ex switch
                {
                    OnlineException { Code: "host_left" or "host_gone" } when served => "the server closed the game.\n\ntry another one.",
                    OnlineException { Code: "host_left" } => "the host left.\n\nthe game is closed.",
                    OnlineException { Code: "host_gone" } => "lost the host.\n\nthe game is closed.",
                    OnlineException { Code: "kicked" } k => k.Message.ToLowerInvariant() + "\n\nthe others are playing.",
                    _ => "couldn't join.\n\n" + Problem(ex),
                });
                RefreshOnline(lists: true); // a new connection (the match's went with it)
            });
        }

        // release 0.2.0 F1: PLAY AGAIN after a match whose host left and whose game is gone (this device didn't take it over):
        // a new lobby of the same match (mode, arena, rules, its bots; the old host's seat and the other people out), hosted
        // here as soon as the hub has a connection (TryRehost)
        private static MatchSettings? s_lastMatch;
        private (MatchSettings Template, int People)? _rehost;

        // the host back from its MODE / RULES tabs (the lobby's own seat): the setup's mode, arena and rules into the hosted
        // game's template (its people and bots stay), the joiners hear it with the lobby (LobbyChanged), and the server's
        // card (the browse list, FIND GAME's filters) is updated too
        private void TakeRulesEdit(OnlineMatch hosting, MatchSettings t)
        {
            MatchSettings now;
            try
            {
                now = MultiplayerMenu.BuildMatch();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
            {
                Import.Popup?.Inform("can't change this game.\n\n" + ex.Message.ToLowerInvariant());
                return;
            }
            t.Mode = now.Mode;
            t.Arena = now.Arena;
            t.PointGoal = now.PointGoal;
            t.TimeLimitSeconds = now.TimeLimitSeconds;
            t.TimeGoalSeconds = now.TimeGoalSeconds;
            t.DamageLevel = now.DamageLevel;
            t.FriendlyFire = now.FriendlyFire;
            t.OctolithReset = now.OctolithReset;
            t.RadarPlayers = now.RadarPlayers;
            t.AffinityWeapons = now.AffinityWeapons;
            t.Crates = now.Crates;
            t.Breakables = now.Breakables;
            t.PirateBlaster = now.PirateBlaster;
            t.ExtraCharacters = now.ExtraCharacters;
            if (!MatchSettings.IsTeamMode(t.Mode)) foreach (MatchPlayer p in t.Players) p.Team = -1;
            Console.WriteLine($"online: host changed the game to {t.Mode} in {t.Arena}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await hosting.UpdateGameAsync(t);
                }
                catch (Exception ex) when (ex is OnlineException or System.Net.WebSockets.WebSocketException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or IOException)
                {
                    Console.Error.WriteLine("online: can't update the game's card: " + ex.Message);
                }
            });
        }

        private (MatchSettings, int)? RehostTemplate(MatchSettings? last)
        {
            if (last == null || last.Players.Count == 0) return null;
            var t = new MatchSettings
            {
                Mode = last.Mode, Arena = last.Arena, PointGoal = last.PointGoal, TimeLimitSeconds = last.TimeLimitSeconds,
                TimeGoalSeconds = last.TimeGoalSeconds, DamageLevel = last.DamageLevel, FriendlyFire = last.FriendlyFire,
                OctolithReset = last.OctolithReset, RadarPlayers = last.RadarPlayers, AffinityWeapons = last.AffinityWeapons,
                Crates = last.Crates, Breakables = last.Breakables, Abilities = last.Abilities, PirateBlaster = last.PirateBlaster,
                ExtraCharacters = last.ExtraCharacters
            };
            OnlinePlayer me = Me();
            (Hunter hunter, string? skin, string? character) = MultiplayerMenu.MyLook;
            MatchPlayer own = last.LocalSeat >= 0 && last.LocalSeat < last.Players.Count ? last.Players[last.LocalSeat] : new MatchPlayer { Hunter = hunter };
            t.Players.Add(new MatchPlayer
            {
                Hunter = own.Hunter, Recolor = own.Recolor, Character = own.Character ?? character, Skin = own.Skin ?? skin,
                Nickname = OnlineNames.Clean(me.Name), RankLevel = me.Rank
            });
            // the match's bots (a guest's copy marks them by rank: -1, MatchClient.ForSeat), as bots of this device
            int bots = 0, people = 1;
            for (int i = 0; i < last.Players.Count; i++)
            {
                if (i == last.LocalSeat) continue;
                MatchPlayer p = last.Players[i];
                if (p.Bot || i != 0 && p.RankLevel < 0) // (seat 0: the old host's own player, never a bot)
                {
                    t.Players.Add(MultiplayerMenu.OnlineBot(p.Hunter, p.Bot ? p.BotLevel : s_botLevel));
                    bots++;
                }
                else
                {
                    people++;
                }
            }
            return (t, Math.Max(2, people));
        }

        // the hub with a connection: a pending PLAY AGAIN lobby (above) is created now
        private void TryRehost(MenuEngine menu)
        {
            if (_rehost is not { } r || _client is not { Connected: true } c || _creating || _hosting != null || _joining) return;
            if (menu.Page?.Index != s_onHub && menu.PendingPage != s_onHub) return;
            _rehost = null;
            HostGame(menu, c, r.Template, r.People, open: s_who == 0, friends: s_who is 0 or 1 or 2, rivals: s_who is 0 or 2,
                $"PLAY AGAIN after the host left: {r.People} people + {r.Template.Players.Count(p => p.Bot)} bots");
        }

        // RESULTS after an online match (MultiplayerMenu: PLAY AGAIN or QUIT, wired by FrontendSession): PLAY AGAIN brings the
        // host back into its game's lobby and a joiner into the same lobby to wait for the host; QUIT leaves the game (the hub)
        public void OnlineAfterResults(MenuEngine menu, bool again)
        {
            _onlineMenu = menu;
            OnlineMatch? m = Kept;
            if (m == null)
            {
                // (the game went with the match: release 0.2.0 F1, the host left a 2-player match) PLAY AGAIN opens a new lobby
                // of the same match, hosted here, instead of an error
                if (again) _rehost = RehostTemplate(s_lastMatch);
                menu.GoTo(s_onHub);
                TryRehost(menu);
                return;
            }
            if (!again)
            {
                m.Dispose();
                menu.GoTo(s_onHub);
                return;
            }
            m.Net.ClearPlayersChanged();
            m.Net.PlayersChanged += LobbyChangedThere;
            if (m.Net.IsHost)
            {
                _hosting = m;
                _hostTemplate = m.Net.Template;
                if (_hostTemplate is { Players.Count: > 0 } t) t.Players[0].RankLevel = Me().Rank; // (RESULTS may have changed it)
                m.BackToLobby();
                Console.WriteLine($"online: back in the lobby of {m.Game.Id} with {m.Net.Joined} joined");
                menu.GoTo(s_onLobby);
                return;
            }
            if (m.Net.Kicked != null)
            {
                string why = m.Net.Kicked.ToLowerInvariant() + "\n\nthe others are playing.";
                m.Dispose();
                menu.GoTo(s_onHub);
                Import.Popup?.Inform(why);
                return;
            }
            if (m.Net.HostLeft)
            {
                // the host left and this device didn't take the match over (release 0.2.0 F1: "PLAY AGAIN errored out"): out of
                // that game, and a new lobby of the same match hosted here once the hub has its connection back
                _rehost = RehostTemplate(m.Net.Settings ?? s_lastMatch);
                m.Dispose();
                menu.GoTo(s_onHub);
                TryRehost(menu);
                return;
            }
            _joined = m;
            _joining = true;
            _joinCancelled = false;
            (Hunter hunter, string? skin, string? character) = MultiplayerMenu.MyLook;
            m.Net.SetLook(hunter, character, skin, choosing: false, Me().Rank);
            Console.WriteLine($"online: back in the lobby of {m.Game.Id}, waiting for the host");
            menu.GoTo(s_onLobby);
            Task.Run(() => WaitThenPlay(m));
        }

        // the host started: the match screen plays it through OnlineMatch.Current, then the menus come back on RESULTS (the
        // license counts it as a Wi-Fi match: MultiplayerMenu.ArmOnlineMatch; head to head: CountHeadToHead) and then the hub
        private void Play(OnlineMatch match)
        {
            s_matchCards = SeatCards(match);
            _joining = false;
            _joined = null;
            Import.Popup?.Close();
            if (StartOnlineMatch == null)
            {
                match.Dispose();
                Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            match.Rearm(); // (a rematch: no longer RESULTS' kept match, whatever page comes before its screen)
            OnlineMatch.Current = match;
            MatchResults.Last = null;
            MatchSettings s = match.Net.Settings!;
            s_lastMatch = s;
            if (Multiplayer != null) Multiplayer.ArmOnlineMatch(s, s_onHub);
            else MultiplayerMenu.PendingReturn = s_onHub;
            Console.WriteLine($"online: start {s.Mode} in {s.Arena} as seat {s.LocalSeat} of {s.Players.Count}");
            StartOnlineMatch(MatchSetupText.Write(s, "online"));
        }

        // ---- drawing (FrontendSession.Build, after the page's own triangles) ----

        public void DrawOnline(MenuEngine menu, List<WidgetTri> tris)
        {
            MenuPage? page = menu.Page;
            if (page == null || !IsOnlinePage(page)) return;
            _drawMenu = menu;
            _out = tris;
            int panel = page.Index == s_onHub ? _hPanel : page.Index == s_onBrowse ? _bPanel : page.Index == s_onDetail ? _gPanel
                : page.Index == s_onWho ? _wPanel : page.Index == s_onFriends ? _frPanel : page.Index == s_onAdd ? _adPanel
                : page.Index == s_onLicense ? _liPanel : page.Index == s_onSettings ? _stPanel : page.Index == s_onFind ? _fiPanel
                : page.Index == s_onVote ? _voPanel : page.Index == s_onChat ? _chPanel : page.Index == s_onReport ? _rpPanel : _lbPanel;
            bool settled = menu.PendingPage == -1 && menu.ItemState(panel) == MenuState.Idle && menu.ItemCode(panel) == (int)MenuState.Idle;
            var glows = new Dictionary<int, List<WidgetTri>>();
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                int item = tris[t].Item;
                if (item < 0 || item >= page.Items.Count || page.Items[item].States.Count != 1) continue;
                if (!glows.TryGetValue(item, out List<WidgetTri>? g)) glows[item] = g = new List<WidgetTri>();
                g.Add(tris[t]);
                tris.RemoveAt(t);
            }
            if (!settled) return;
            try
            {
                if (page.Index == s_onHub) DrawHub(glows);
                else if (page.Index == s_onBrowse) DrawBrowse(glows);
                else if (page.Index == s_onDetail) DrawGame(glows);
                else if (page.Index == s_onWho) DrawWho(glows);
                else if (page.Index == s_onFriends) DrawFriends(glows);
                else if (page.Index == s_onAdd) DrawAdd(glows);
                else if (page.Index == s_onLicense) DrawLicense(glows);
                else if (page.Index == s_onSettings) DrawSettings(glows);
                else if (page.Index == s_onFind) DrawFind(glows);
                else if (page.Index == s_onVote) DrawVote(glows);
                else if (page.Index == s_onChat) DrawChat(glows);
                else if (page.Index == s_onReport) DrawReport(glows);
                else DrawLobby(glows);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                Console.Error.WriteLine("online: can't draw (" + ex.Message + ")");
            }
        }

        private void DrawHub(Dictionary<int, List<WidgetTri>> glows)
        {
            int tag = _hTitle;
            if (MeCard is PlayerCard me && me.Code.Length > 0)
                TextS(OnlineNames.Display(me.Name, me.Tag).ToLowerInvariant(), Mx(1850), My(52), 1, Cyan, tag, SizeM);
            // QUICK MATCH
            int q = _hQuick;
            Bar(Mx(100), My(226), Mx(1240), My(556), q, glows);
            DoorIcon("globe", Mx(130), My(256), Mw(270), q);
            TextS(FindOn ? "find game" : "quick match", Mx(440), My(276), 0, Orange, q, SizeS);
            TextS(!FindOn ? "jump into a game now" : _finding ? "searching..." : _party != null ? $"search with your party of {_party.Members.Count}"
                : "any mode, players near you", Mx(440), My(342), 0, White, q, SizeM);
            string status = _connecting ? "connecting..." : !Live && _netError != null ? "offline: " + _netError
                : _hunters >= 0 ? $"{_hunters} hunter{(_hunters == 1 ? "" : "s")} online" : "";
            TextS(Fit(status, 34), Mx(440), My(388), 0, !Live && _netError != null && !_connecting ? Red : Cyan, q, SizeM);
            Capsule(Mx(440), My(448), Mw(420), Mw(76), "go", Live, -1, glows, SizeS);
            // BROWSE, CREATE
            int open = _games.Count(g => JoinBlock(g.Info) == null);
            Bar(Mx(100), My(586), Mx(655), My(886), _hBrowse, glows);
            DoorIcon("join", Mx(124), My(651), Mw(170), _hBrowse);
            TextS("browse", Mx(320), My(656), 0, Orange, _hBrowse, SizeS);
            TextS(!Live ? "" : $"{open} open game{(open == 1 ? "" : "s")}", Mx(320), My(722), 0, Cyan, _hBrowse, SizeM);
            Bar(Mx(685), My(586), Mx(1240), My(886), _hCreate, glows);
            DoorIcon("create", Mx(709), My(651), Mw(170), _hCreate);
            TextS("create", Mx(905), My(656), 0, Orange, _hCreate, SizeS);
            TextS("host a game", Mx(905), My(722), 0, Pale, _hCreate, SizeM);
            // the age rule as one dim line, not a popup (owner 10-09); tagged with the panel so it takes no taps of its own
            TextS("online play is for ages 13 and up", Mx(100), My(918), 0, (0.75f, 0.8f, 0.8f), _hPanel, SizeX, 0.7f);
            // FRIENDS ONLINE
            int side = _hAllFriends;
            Fill(Mx(1314), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("friends online", Mx(1580), My(208), 2, Dark, side, SizeM);
            for (int r = 0; r < OnFriendRows && r < _friendsOn.Count; r++)
            {
                PlayerCard f = _friendsOn[r];
                float y = 266 + 170 * r;
                Seat(f, Mx(1330), My(y + 10), Mw(110), side);
                TextS(OnlineNames.Display(f.Name, f.Tag).ToLowerInvariant(), Mx(1460), My(y + 16), 0, White, side, SizeM);
                (string state, (float, float, float) colour) = f.Presence switch
                {
                    Presence.Lobby => ("in lobby", Cyan),
                    Presence.Match => ("in match", Orange),
                    _ => ("online", Green)
                };
                TextS(state, Mx(1460), My(y + 62), 0, colour, side, SizeX);
                GameCard? g = f.GameId == null ? null : _games.FirstOrDefault(x => x.Id == f.GameId);
                string where = g != null ? $"{(ModeOf(g) is GameMode m ? ModeName(m) : g.Info.Mode.ToLowerInvariant())} {g.Players}/{g.Info.MaxPlayers}"
                    : f.Presence == Presence.Online ? "in menus" : "";
                string? block = f.Presence == Presence.Lobby ? JoinBlock(f.GameId) : null;
                TextS(block ?? where, Mx(1460), My(y + 102), 0, block != null ? OnGrey : Pale, side, SizeX);
                if (f.Presence == Presence.Lobby && f.GameId != null)
                {
                    (float x0, float top, float x1, float bottom) = FriendJoinRect(r);
                    Capsule(x0, top, x1 - x0, top - bottom, "join", block == null, _hFriendJoin[r], glows, SizeX);
                }
            }
            if (_friendsOn.Count == 0)
            {
                TextS(!Live ? "" : "no friends online.", Mx(1580), My(420), 2, White, side, SizeM);
                TextS(!Live ? "" : "add friends from all friends.", Mx(1580), My(470), 2, Pale, side, SizeX);
            }
            Bar(Mx(1350), My(826), Mx(1810), My(892), side, glows);
            TextS("all friends", Mx(1580), My(844), 2, Orange, side, SizeM);
            DrawSettingsButton(glows);
        }

        private void DrawBrowse(Dictionary<int, List<WidgetTri>> glows)
        {
            List<GameCard> shown = Shown();
            // the filters and REFRESH
            TextS("mode", Mx(104), My(220), 0, Pale, _bMode, SizeM);
            (float mx0, float my0, float mx1, float my1) = FilterRect(0);
            ValueBox(mx0, my0, mx1, my1, _modeFilter == 0 ? "all" : ModeName(OnModes[_modeFilter - 1]), _bMode, glows);
            TextS("friends only", Mx(578), My(220), 0, Pale, _bFriendsOnly, SizeM);
            (float fx0, float fy0, float fx1, float fy1) = FilterRect(1);
            ValueBox(fx0, fy0, fx1, fy1, _friendsOnly ? "on" : "off", _bFriendsOnly, glows);
            Glow(glows, _bRefresh, Mx(1640), My(210), Mx(1830), My(254), _bRefresh);
            TextS("refresh", Mx(1812), My(220), 1, Orange, _bRefresh, SizeM);
            int count = Math.Max(0, Math.Min(OnRows, shown.Count - _gameOffset));
            for (int r = 0; r < count; r++)
            {
                GameCard g = shown[_gameOffset + r];
                int tag = _bRow[r];
                (float bx0, float top, float bx1, float bottom) = GameRowRect(r);
                float by = 284 + 210 * r; // the row's top in mockup pixels
                Fill(bx0, top, bx1, bottom, 0, 0, 0, 0.35f, tag);
                Box(bx0, top, bx1, bottom, tag);
                Glow(glows, tag, bx0, top, bx1, bottom, tag);
                SeatStrip(g, Mx(138), My(by + 36), Mw(106), Mw(146), tag);
                float tx = Mx(740);
                string host = OnlineNames.Display(g.Host.Name, g.Host.Tag).ToLowerInvariant();
                TextS(host, tx, My(by + 18), 0, White, tag, SizeS);
                Badge(g.Host, tx + _drawMenu!.Font.Measure(host) + Mw(14), My(by + 16), Mw(44), tag);
                TextS(ModeOf(g) is GameMode m ? ModeName(m) : g.Info.Mode.ToLowerInvariant(), tx, My(by + 72), 0, Orange, tag, SizeS);
                string players = $"{g.Players}/{g.Info.MaxPlayers} players";
                TextS(players, tx, My(by + 130), 0, Pale, tag, SizeM);
                BanIcons(g.Info, tx + _drawMenu!.Font.Measure(players) * SizeM + Mw(24), My(by + 122), Mw(40), tag);
                string? block = JoinBlock(g.Info);
                bool here = block == null;
                if (here) Ping(Mx(1200), My(by + 62), Mw(40), _pingMs, tag);
                else TextS(block!, Mx(1200), My(by + 70), 0, OnGrey, tag, SizeX);
                (float jx0, float jtop, float jx1, float jbottom) = RowJoinRect(r);
                Capsule(jx0, jtop, jx1 - jx0, jtop - jbottom, "join", here, _bJoin[r], glows, SizeS);
                ArenaPicture(g.Info.Arena, Mx(1502), My(by - 11), Mw(318), Mw(200), 1.2f, tag);
            }
            if (count == 0)
            {
                int tag = _bTitle;
                string first = _connecting ? "connecting..." : !Live ? "can't reach the server." : _games.Count == 0 ? "no open games right now."
                    : "no games match the filters.";
                string second = !Live && !_connecting ? _netError ?? "" : "tap refresh to look again.";
                Text(first, 128, -80, 2, White, tag);
                TextS(Fit(second, 50), 128, -94, 2, Pale, tag, SizeM);
            }
            if (OnGamePages > 1)
            {
                float w = _drawMenu!.Font.Measure($"BROWSE GAMES {OnGamePages}/{OnGamePages}") / 2;
                Arrow(false, 128 - w - 22, ArrowTop, _bPrev);
                Arrow(true, 128 + w + 12, ArrowTop, _bNext);
            }
        }

        private void DrawGame(Dictionary<int, List<WidgetTri>> glows)
        {
            GameCard? g = OpenGame;
            if (g == null) return;
            int tag = _gJoin;
            ArenaPicture(g.Info.Arena, Mx(114), My(236), Mw(600), Mw(380), 1.6f, tag);
            float fx = Mx(790), fr = Mx(1800);
            float y = 240;
            string host = OnlineNames.Display(g.Host.Name, g.Host.Tag).ToLowerInvariant();
            TextS("host", fx, My(y), 0, Pale, tag, SizeM);
            bool badge = g.Host.Friend || ShowRivals && g.Host.Rival;
            TextS(host, badge ? fr - Mw(56) : fr, My(y), 1, White, tag, SizeM);
            if (badge) Badge(g.Host, fr - Mw(40), My(y - 4), Mw(36), tag);
            y += 50;
            var facts = new List<(string, string)>
            {
                ("arena", ArenaLabel(g.Info.Arena)),
                ("players", $"{g.Players}/{g.Info.MaxPlayers}"),
            };
            if (TimeLimit(g.Info.Rules) is string limit) facts.Add(("time limit", limit));
            facts.Add(("who can join", WhoCanJoin(g.Info)));
            if (!g.Info.ExtraPickups || !g.Info.ExtraCharacters)
            {
                facts.Add(("extras", g.Info.ExtraPickups ? "no extra characters" : "no extra pickups or characters"));
            }
            if (g.Info.Mods != GameInfo.NoMods) facts.Add(("mods", g.Info.Mods.ToLowerInvariant()));
            if (g.Info.Version != VersionKey) facts.Add(("version", "different: can't join"));
            else if (JoinBlock(g.Info) is string why) facts.Add(("join", "can't: " + why));
            foreach ((string k, string v) in facts)
            {
                TextS(k, fx, My(y), 0, Pale, tag, SizeM);
                TextS(Fit(v, 28), fr, My(y), 1, White, tag, SizeM);
                y += 44;
            }
            TextS("ping", fx, My(y), 0, Pale, tag, SizeM);
            Ping(fr - Mw(200), My(y - 6), Mw(36), _pingMs, tag);
            Capsule(Mx(790), My(600), Mw(550), Mw(90), "join", JoinBlock(g.Info) == null, _gJoin, glows, SizeS);
            Bar(Mx(1390), My(615), Mx(1750), My(681), _gLicense, glows);
            TextS("host license", Mx(1570), My(634), 2, Orange, _gLicense, SizeM);
            // the seats: who's in (the server's members), then the open ones
            int max = Math.Clamp(g.Info.MaxPlayers, 1, 8), shownSeats = Math.Min(max, 4);
            for (int i = 0; i < shownSeats; i++)
            {
                float x = 130 + 430 * i;
                if (i < g.Members.Count)
                {
                    PlayerCard p = g.Members[i];
                    (float sx0, float stop, float sx1, float sbottom) = DetailSeatRect(i);
                    Glow(glows, _gSeat[i], sx0, stop, sx1, sbottom, _gSeat[i]);
                    Seat(p, Mx(x), My(756), Mw(110), tag);
                    TextS(Fit(OnlineNames.Display(p.Name, p.Tag).ToLowerInvariant(), 13), Mx(x + 140), My(770), 0, White, tag, SizeM);
                    TextS(p.Code == g.Host.Code ? "host" : p.Friend ? "friend" : ShowRivals && p.Rival ? "rival" : "", Mx(x + 140), My(818), 0, Orange, tag, SizeX);
                }
                else
                {
                    EmptySeat(Mx(x), My(756), Mw(110), tag);
                    TextS("open", Mx(x + 140), My(796), 0, OnGrey, tag, SizeM);
                }
            }
            if (max > 4) TextS($"+{max - 4} more seats", Mx(1800), My(870), 1, Pale, tag, SizeX);
            TextS("tap a name to see their hunter license", Mx(960), My(900), 2, OnGrey, tag, SizeX);
        }

        private void DrawWho(Dictionary<int, List<WidgetTri>> glows)
        {
            for (int slot = 0; slot < WhoShown.Length; slot++)
            {
                int r = WhoShown[slot];
                int tag = _wRow[r];
                (float x0, float top, float x1, float bottom) = WhoRowRect(slot);
                float by = 222 + 170 * slot; // the row's top in mockup pixels
                Bar(x0, top, x1, bottom, tag, glows);
                WhoIcon(r, Mx(134), My(by + 14), Mw(120), tag);
                TextS(WhoChoices[r].Label, Mx(310), My(by + 30), 0, Orange, tag, SizeS);
                TextS(WhoChoices[r].Line, Mx(310), My(by + 92), 0, White, tag, SizeM);
                if (r == s_who) Model(@"gameoptions\ok", Mx(1110), My(by + 24), Mw(100), Mw(100), tag);
            }
            // YOUR GAME: the setup's mode, arena and rules, then BOTS and PLAYERS
            int side = _wPlayers;
            Fill(Mx(1314), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("your game", Mx(1580), My(208), 2, Dark, side, SizeM);
            float y = 266;
            foreach ((string k, string v) in Multiplayer?.OnlineSummary() ?? Array.Empty<(string, string)>())
            {
                TextS(k, Mx(1336), My(y), 0, Pale, side, SizeM);
                TextS(Fit(v, 16), Mx(1824), My(y), 1, White, side, SizeM);
                y += 50;
            }
            WhoValue(0, "bots", s_bots, _wBots, glows);
            WhoValue(1, "players", s_seats, _wPlayers, glows);
            WhoValue(2, "bot level", s_botLevel + 1, _wLevel, glows);
            Capsule(Mx(1330), My(760), Mw(500), Mw(110), "create", Live, _wCreate, glows, SizeS);
            string status = _connecting ? "connecting..." : !Live && _netError != null ? "offline: " + _netError : "";
            TextS(Fit(status, 30), Mx(1580), My(890), 2, _connecting ? Cyan : Red, _wCreate, SizeX);
        }

        // a label, the setup's option arrows and a value box (A or a tap on the box steps it round, the arrows step it)
        private void WhoValue(int which, string label, int value, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            (float x0, float top, float x1, float bottom) = WhoBox(which);
            TextS(label, Mx(1336), top - (top - bottom - 8 * SizeM) / 2, 0, Pale, item, SizeM);
            ValueBox(x0, top, x1, bottom, value.ToString(CultureInfo.InvariantCulture), item, glows);
            float ay = top - (top - bottom - ArrowH) / 2;
            Arrow(false, x0 - ArrowW - 2.5f, ay, item);
            Arrow(true, x1 + 2.5f, ay, item);
        }

        // PUBLIC = the Wi-Fi globe, FRIENDS = the FRIENDS AND RIVALS button's three hunters, FRIENDS + RIVALS = the rival radar
        // disc, INVITE ONLY = the person disc
        private void WhoIcon(int row, float x, float top, float d, int tag)
        {
            int tex = row switch
            {
                0 => Derived("globe", @"multiplayer\wifi", src => Crop(src, 1, 2, 72, 73)),
                1 => Derived("friends", @"wifi_createjoin\wififriend", src => Crop(src, 1, 2, 72, 73)),
                2 => Derived("radar", @"multiplayer\rivalradar", src => Disc(src, src.Width / 2, 0, src.Width, src.Height)),
                _ => Derived("person", @"multiplayer\friendconfig", src => Disc(src, 0, 0, src.Width, src.Height)),
            };
            if (tex >= 0) Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        private void DrawLobby(Dictionary<int, List<WidgetTri>> glows)
        {
            OnlineMatch? m = _hosting ?? _joined;
            MatchSettings? settings = m?.Net.Settings;
            if (m == null || settings == null) return;
            bool host = Runner != null;
            List<LobbySeat> seats = SeatsNow();
            int[]? teams = MatchSettings.IsTeamMode(settings.Mode) ? settings.TeamAssignment() : null;
            for (int k = 0; k < seats.Count; k++)
            {
                LobbySeat seat = seats[k];
                bool eight = LobbyEight(seats.Count); // (P8: 5-8 seats, half-height cards: OnlineMenus.Seats8.cs)
                int tag = LobbySeatItem(k, seats.Count);
                (float x0, float top, float x1, float bottom) = eight ? SeatRect8(k) : SeatRect(k);
                float cx = 100 + k % 2 * 650, cy = 222 + k / 2 * 352; // the card's corner in mockup pixels
                Fill(x0, top, x1, bottom, 0, 0, 0, 0.47f, tag);
                Box(x0, top, x1, bottom, tag);
                Glow(glows, tag, x0, top, x1, bottom, tag);
                bool addBot = host && seat.Kind == SeatKind.Open && CanAddBot(seats);
                bool botBoxes = host && seat.Kind == SeatKind.Bot && seat.Index >= 0 && seat.Player != null;
                if (eight && seat.Kind == SeatKind.Open)
                {
                    DrawSeatCard8(seat, k, tag, host, false, false, -1);
                    if (addBot) DrawAddBot(k, true, glows);
                    continue;
                }
                if (seat.Kind == SeatKind.Open)
                {
                    EmptySeat(Mx(cx + 24), My(cy + 75), Mw(180), tag);
                    Model(@"startgame\adbot", Mx(cx + 69), My(cy + 120), Mw(90), Mw(90), tag);
                    TextS("open", Mx(cx + 240), My(cy + 110), 0, OnGrey, tag, SizeM);
                    if (host) TextS("invite", Mx(cx + 240), My(cy + 176), 0, Orange, tag, SizeM);
                    if (addBot) DrawAddBot(k, false, glows);
                    continue;
                }
                // the seat in the lobby: its ping (yours: this device's), choosing a hunter, back from the results
                NetMatchSession.LobbySeat? info = seat.Index >= 0 && seat.Index < m.Net.Lobby.Count ? m.Net.Lobby[seat.Index] : null;
                bool you = seat.Kind == SeatKind.You;
                bool choosing = !you && info?.Choosing == true, away = !you && info is { Back: false };
                int ping = you ? m.Relay.PingMs : seat.Kind == SeatKind.Person ? info?.PingMs ?? -1 : -1;
                if (teams != null && seat.Index >= 0 && seat.Index < teams.Length)
                {
                    (float r, float g, float b) = teams[seat.Index] == 0 ? Orange : Green;
                    Fill(x0 + 0.8f, top - 0.8f, x0 + Mw(14), bottom + 0.8f, r, g, b, 1, tag);
                    (float tx0, float ttop, float tx1, float tbottom) = eight ? TeamRect8(k) : TeamRect(k);
                    string team = teams[seat.Index] == 0 ? "orange" : "green";
                    int teamItem = LobbyTeamItem(k, seats.Count);
                    bool mine = TeamBoxShown(seat, host);
                    if (mine) Bar(tx0, ttop, tx1, tbottom, teamItem, glows);
                    TextS(team, (tx0 + tx1) / 2, ttop - (ttop - tbottom - 8 * SizeX) / 2, 2, (r, g, b), mine ? teamItem : tag, SizeX);
                }
                if (botBoxes) DrawBotBoxes(k, eight, seat.Player!, glows);
                if (eight)
                {
                    DrawSeatCard8(seat, k, tag, host, choosing, away, ping);
                    continue;
                }
                HunterDisc(seat, Mx(cx + 24), My(cy + 75), Mw(180), tag);
                string name = seat.Card != null ? OnlineNames.Display(seat.Card.Name, seat.Card.Tag) : seat.Player?.Nickname ?? "";
                TextS(Fit(name.ToLowerInvariant(), 15), Mx(cx + 240), My(cy + 96), 0, White, tag, SizeM);
                string what = seat.Player != null ? HunterWord(seat.Player) : "";
                if (seat.Host) what += (what.Length > 0 ? "   " : "") + "host";
                if (choosing) TextS("choosing a hunter...", Mx(cx + 240), My(cy + 150), 0, Cyan, tag, SizeX);
                else if (away) TextS("on the results...", Mx(cx + 240), My(cy + 150), 0, OnGrey, tag, SizeX);
                else TextS(what, Mx(cx + 240), My(cy + 150), 0, Orange, tag, SizeX);
                // (the host's own card: no hint, its EDIT RULES is in the GAME panel; owner 2026-10-16)
                string third = you ? (host ? "" : "tap to change hunter")
                    : seat.Kind != SeatKind.Bot ? "" : host ? "" : $"bot level {seat.Player?.BotLevel + 1 ?? 1}";
                TextS(third, Mx(cx + 240), My(cy + 196), 0, you ? Pale : OnGrey, tag, SizeX);
                if (ping >= 0) Ping(Mx(cx + 240), My(cy + 238), Mw(34), ping, tag);
                // ready: in the lobby with a hunter (the check), else the red X; nothing for someone the match doesn't have yet
                if (seat.Index >= 0 && !botBoxes) // (a host's bot card: REMOVE where the check goes)
                {
                    Model(choosing || away ? @"gameoptions\cancel" : @"gameoptions\ok", Mx(cx + 510), My(cy + 125), Mw(80), Mw(80), tag);
                }
            }
            // GAME: the arena's picture and name, the rules, who can join; the host's INVITE FRIENDS and START
            int side = _lbTitle;
            Fill(Mx(1434), My(200), Mx(1846), My(238), HeaderBar.R, HeaderBar.G, HeaderBar.B, 1, side);
            TextS("game", Mx(1640), My(208), 2, Dark, side, SizeM);
            ArenaPicture(settings.Arena, Mx(1458), My(256), Mw(364), Mw(230), 1.4f, side);
            TextS(Fit(ArenaLabel(settings.Arena), 18), Mx(1640), My(500), 2, White, side, SizeX);
            var facts = new List<(string, string)> { ("mode", ModeName(settings.Mode)) };
            if (GoalLine(settings) is (string, string) goal) facts.Add(goal);
            if (settings.TimeLimitSeconds is float t && t > 0) facts.Add(("time", $"{(int)t / 60}:{(int)t % 60:00}"));
            facts.Add(("join", WhoCanJoin(m.Game.Info)));
            float y = 546;
            foreach ((string k, string v) in facts)
            {
                TextS(k, Mx(1452), My(y), 0, Pale, side, SizeX);
                TextS(Fit(v, 16), Mx(1830), My(y), 1, White, side, SizeX);
                y += 40;
            }
            if (host)
            {
                Bar(Mx(1450), My(LbRulesY), Mx(1830), My(LbRulesY + LbBarH), _lbRules, glows);
                TextS("edit rules", Mx(1640), My(LbRulesY + 13), 2, Orange, _lbRules, SizeX);
                Bar(Mx(1450), My(LbInviteY), Mx(1830), My(LbInviteY + LbBarH), _lbInvite, glows);
                TextS("invite friends", Mx(1640), My(LbInviteY + 13), 2, Orange, _lbInvite, SizeX);
                Capsule(Mx(1446), My(LbStartY), Mw(390), Mw(88), "start", Runner!.Net.Joined > 0, _lbStart, glows, SizeM);
                if (Runner.Net.Joined == 0) TextS("waiting for hunters...", Mx(1640), My(LbStartY + 98), 2, Pale, side, SizeX);
            }
            else
            {
                TextS("waiting for the host", Mx(1640), My(770), 2, Pale, side, SizeX);
                TextS("to start...", Mx(1640), My(810), 2, Pale, side, SizeX);
            }
            DrawLobbyChat(glows, seats); // (OnlineMenus.Chat.cs)
            if (_onBack.TryGetValue(s_onLobby, out int back)) TextS("leave", Mx(200), My(990), 0, Orange, back, SizeM);
        }

        // the GAME panel's host buttons (EDIT RULES, INVITE FRIENDS, START), top edges; TALK goes at 922 (OnlineMenus.Voice.cs)
        private const float LbRulesY = 698, LbInviteY = 756, LbBarH = 50, LbStartY = 816;

        // a seat's round icon: SELECT HUNTER's (TROOPER / DARK SAMUS too, RecompMenus.SeatIcons.cs), or the person disc (a
        // hunter not known yet)
        private void HunterDisc(LobbySeat seat, float x, float top, float d, int tag)
        {
            if (seat.Player != null && SeatIcon(seat.Player, x, top, d, tag)) return;
            if (seat.Card != null) Seat(seat.Card, x, top, d, tag);
        }

        private static string HunterWord(MatchPlayer p) => Characters.Of(p.Hunter, p.Skin) is { HasOriginal: false } c
            ? c.Name.ToLowerInvariant() : p.Hunter.ToString().ToLowerInvariant();

        // the mode's goal as the setup names it (MultiplayerMenu's rules)
        private static (string, string)? GoalLine(MatchSettings s)
        {
            if (s.Mode is GameMode.Defender or GameMode.DefenderTeams or GameMode.PrimeHunter)
            {
                return s.TimeGoalSeconds is float g ? ("time goal", $"{(int)g / 60}:{(int)g % 60:00}") : null;
            }
            if (s.PointGoal is not int n) return null;
            string v = n.ToString(CultureInfo.InvariantCulture);
            return s.Mode switch
            {
                GameMode.Survival or GameMode.SurvivalTeams => ("lives", v),
                GameMode.Bounty or GameMode.BountyTeams or GameMode.Capture => ("octoliths", v),
                _ => ("point goal", v)
            };
        }

        // an arena as the menus name it (the room's in-game name; a game from an older build may send the room's)
        private static string ArenaLabel(string arena) => (MatchArenas.Find(arena)?.InGameName is { Length: > 0 } n ? n : arena).ToLowerInvariant();

        // ---- pieces ----

        private static string? TimeLimit(string? rules)
        {
            if (rules == null || !rules.EndsWith(" s", StringComparison.Ordinal)) return null;
            return float.TryParse(rules[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0
                ? $"{(int)s / 60}:{(int)s % 60:00}" : null;
        }

        private static string WhoCanJoin(GameInfo i) => i.Public ? "anyone" : i.Friends && i.Rivals ? "friends + rivals"
            : i.Friends ? "friends" : i.Rivals ? "rivals" : "invite only";

        // a dark bar with the setup's box outline and, when focused, the lobby's glow
        private void Bar(float x0, float top, float x1, float bottom, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            Fill(x0, top, x1, bottom, 0, 0, 0, 0.35f, item);
            Box(x0, top, x1, bottom, item);
            Glow(glows, item, x0, top, x1, bottom, item);
        }

        private void ValueBox(float x0, float top, float x1, float bottom, string value, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            Fill(x0, top, x1, bottom, 0, 0, 0, 0.43f, item);
            Box(x0, top, x1, bottom, item);
            Glow(glows, item, x0, top, x1, bottom, item);
            TextS(value, (x0 + x1) / 2, top - (top - bottom - 8 * SizeM) / 2, 2, White, item, SizeM);
        }

        // the lobby's START GAME pill with our label (dimmed when it can't be used); item -1: no glow of its own
        private void Capsule(float x, float top, float w, float h, string label, bool live, int item, Dictionary<int, List<WidgetTri>> glows, float size)
        {
            int tag = item >= 0 ? item : _hQuick;
            Model(@"startgame\startgame", x, top, w, h, tag, alpha: live ? 1 : 0.4f);
            if (item >= 0) Glow(glows, item, x, top, x + w, top - h, item);
            TextS(label, x + w * 0.36f, top - (h - 8 * size) / 2 - 0.2f, 2, Orange, tag, size, live ? 1 : 0.4f);
        }

        // four rising bars (the DS's signal look): green good, pale fair, red poor; then the milliseconds
        private void Ping(float x, float top, float h, int ms, int tag)
        {
            if (ms < 0) return;
            int level = ms < 60 ? 4 : ms < 100 ? 3 : ms < 160 ? 2 : 1;
            (float r, float g, float b) = level >= 3 ? Green : level == 2 ? Pale : Red;
            float bw = h * 0.22f;
            for (int i = 0; i < 4; i++)
            {
                float bh = h * (0.35f + 0.65f * i / 3), bx = x + i * bw * 1.45f;
                Fill(bx, top - h + bh, bx + bw, top - h, 0, 0, 0, 0.8f, tag);
                if (i < level) Fill(bx + 0.4f, top - h + bh - 0.4f, bx + bw - 0.4f, top - h + 0.4f, r, g, b, 1, tag);
                else Fill(bx + 0.4f, top - h + bh - 0.4f, bx + bw - 0.4f, top - h + 0.4f, 0.24f, 0.24f, 0.24f, 1, tag);
            }
            TextS($"{ms} ms", x + 4 * bw * 1.45f + Mw(12), top - h + 8 * SizeM, 0, White, tag, SizeM);
        }

        // a game's seats in a row: who's in (round icons), then open seats up to its size (eight: two rows of four, smaller)
        private void SeatStrip(GameCard g, float x, float top, float d, float pitch, int tag)
        {
            int max = Math.Clamp(g.Info.MaxPlayers, 1, 8);
            int per = max > 4 ? 4 : max;
            float s = max > 4 ? 0.48f : 1;
            for (int i = 0; i < max; i++)
            {
                float sx = x + i % per * pitch, sy = top - i / per * (pitch * 0.5f);
                float dd = d * s;
                if (i < g.Members.Count) Seat(g.Members[i], sx, sy, dd, tag);
                else EmptySeat(sx, sy, dd, tag);
            }
        }

        // a player's round icon: their license's hunter (LicensePick: the one their HUNTER LICENSE card shows), else, with no
        // license uploaded, the friends-and-rivals page's person
        private void Seat(PlayerCard p, float x, float top, float d, int tag)
        {
            if (LicensePick(p.License) is { } pick && SeatIcon(pick, x, top, d, tag)) return;
            int tex = Derived("person", @"multiplayer\friendconfig", src => Disc(src, 0, 0, src.Width, src.Height));
            if (tex >= 0) Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        private void EmptySeat(float x, float top, float d, int tag)
        {
            UiTexture t = _drawMenu!.Widgets.Textures.GetOrAdd("online empty seat", () =>
            {
                const int n = 64;
                byte[] px = new byte[n * n * 4];
                for (int y = 0; y < n; y++)
                {
                    for (int x0 = 0; x0 < n; x0++)
                    {
                        float dist = MathF.Sqrt((x0 + 0.5f - n / 2f) * (x0 + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                        float outer = Math.Clamp(n * 0.44f - dist + 0.5f, 0, 1), ring = Math.Clamp(dist - (n * 0.44f - 2.6f) + 0.5f, 0, 1);
                        int i = (y * n + x0) * 4;
                        px[i] = (byte)(230 * ring);
                        px[i + 1] = (byte)(99 * ring);
                        px[i + 2] = (byte)(8 * ring);
                        px[i + 3] = (byte)(255 * outer * (0.59f + 0.41f * ring));
                    }
                }
                return (n, n, px);
            });
            Quad(t.Id, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        // friend = the person disc, rival = the rival radar disc
        private void Badge(PlayerCard p, float x, float top, float h, int tag)
        {
            int tex = p.Friend ? Derived("person", @"multiplayer\friendconfig", src => Disc(src, 0, 0, src.Width, src.Height))
                : ShowRivals && p.Rival ? Derived("radar", @"multiplayer\rivalradar", src => Disc(src, src.Width / 2, 0, src.Width, src.Height)) : -1;
            if (tex >= 0) Quad(tex, x, top, x + h, top - h, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        // the hub's round door icons: the Wi-Fi globe, a round JOIN (owner 2026-10-09: the join disc closed, its card-slot bar
        // dropped) and CREATE, each from the art in its texture's top-left (the doors' 128 px textures)
        private void DoorIcon(string which, float x, float top, float d, int tag)
        {
            int tex = which switch
            {
                "globe" => Derived("globe", @"multiplayer\wifi", src => Crop(src, 1, 2, 72, 73)),
                "create" => Derived("create", @"createjoin\create", src => Crop(src, 1, 2, 72, 73)),
                _ => Derived("round join", @"createjoin\join", RoundJoin),
            };
            if (tex >= 0) Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 1, tag);
        }

        // the extra content a game bans (Extra pickups + Extra characters rules, owner 2026-10-10: small icons, none when it's
        // all allowed): the Pirate Blaster's three bolts, then the person disc, each slashed
        private void BanIcons(GameInfo info, float x, float top, float d, int tag)
        {
            if (!info.ExtraPickups)
            {
                UiTexture t = _drawMenu!.Widgets.Textures.GetOrAdd("online no pickups", () => Slashed(BlasterDisc()));
                Quad(t.Id, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 0.9f, tag);
                x += d * 1.3f;
            }
            if (!info.ExtraPickups || !info.ExtraCharacters)
            {
                int tex = Derived("no characters", @"multiplayer\friendconfig", src => Slashed(Disc(src, 0, 0, src.Width, src.Height)));
                if (tex >= 0) Quad(tex, x, top, x + d, top - d, 0, 0, 1, 1, 1, 1, 1, 0.9f, tag);
            }
        }

        // the blaster's ring icon (PlayerPirateBlaster's 32 px mask) at 2x in the menus' orange on a dark disc with an orange rim
        private static (int, int, byte[]) BlasterDisc()
        {
            const int n = 64;
            string[] mask = MphRead.Entities.PlayerEntity._blasterIconMask;
            byte[] px = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dist = MathF.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                    float cover = Math.Clamp(n / 2f - dist + 0.5f, 0, 1), rim = Math.Clamp(dist - (n / 2f - 3.5f) + 0.5f, 0, 1);
                    // (the bolts a little smaller than the disc: 2x from 4 px in)
                    int mx = (x - 4) * 32 / 56, my = (y - 4) * 32 / 56;
                    char c = x >= 4 && y >= 4 && my < mask.Length && mx < mask[my].Length ? mask[my][mx] : '.';
                    (byte r, byte g, byte b) = rim > 0.5f ? ((byte)230, (byte)99, (byte)16) : c == '#' ? ((byte)255, (byte)190, (byte)90)
                        : c == 'o' ? ((byte)230, (byte)99, (byte)16) : ((byte)20, (byte)14, (byte)10);
                    int o = (y * n + x) * 4;
                    px[o] = r;
                    px[o + 1] = g;
                    px[o + 2] = b;
                    px[o + 3] = (byte)(255 * cover);
                }
            }
            return (n, n, px);
        }

        // a red slash, top-left to bottom-right, over an icon (its own alpha kept, the slash drawn where the icon is)
        private static (int, int, byte[]) Slashed((int W, int H, byte[] Px) icon)
        {
            (int w, int h, byte[] px) = icon;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                    float d = MathF.Abs(u - v) / MathF.Sqrt(2) * w; // px from the diagonal
                    float a = Math.Clamp(w * 0.07f - d + 0.5f, 0, 1) * (u > 0.12f && u < 0.88f ? 1 : 0);
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(px[o] * (1 - a) + 235 * a);
                    px[o + 1] = (byte)(px[o + 1] * (1 - a) + 40 * a);
                    px[o + 2] = (byte)(px[o + 2] * (1 - a) + 30 * a);
                    px[o + 3] = Math.Max(px[o + 3], (byte)(255 * a));
                }
            }
            return (w, h, px);
        }

        // a texture made from a ROM model's (its first) texture, kept
        private int Derived(string key, string model, Func<UiTexture, (int, int, byte[])> make)
        {
            int id = Still(model).Select(t => t.TextureId).FirstOrDefault(t => t >= 0, -1);
            if (id < 0) return -1;
            UiTextureCache textures = _drawMenu!.Widgets.Textures;
            UiTexture src = textures[id];
            return textures.GetOrAdd("online " + key, () => make(src)).Id;
        }

        private static (int, int, byte[]) Crop(UiTexture t, int x0, int y0, int x1, int y1)
        {
            int w = x1 - x0, h = y1 - y0;
            byte[] px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Clamp(x + x0, 0, t.Width - 1), sy = Math.Clamp(y + y0, 0, t.Height - 1);
                    Array.Copy(t.Rgba, (sy * t.Width + sx) * 4, px, (y * w + x) * 4, 4);
                }
            }
            return (w, h, px);
        }

        // a square piece of a texture scaled to 64 px and cut round (soft edge)
        private static (int, int, byte[]) Disc(UiTexture t, int x0, int y0, int x1, int y1)
        {
            const int n = 64;
            byte[] px = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    int sx = Math.Clamp(x0 + (x * (x1 - x0)) / n, 0, t.Width - 1), sy = Math.Clamp(y0 + (y * (y1 - y0)) / n, 0, t.Height - 1);
                    int s = (sy * t.Width + sx) * 4, o = (y * n + x) * 4;
                    float dist = MathF.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                    float cover = Math.Clamp(n / 2f - dist + 0.5f, 0, 1);
                    px[o] = t.Rgba[s];
                    px[o + 1] = t.Rgba[s + 1];
                    px[o + 2] = t.Rgba[s + 2];
                    px[o + 3] = (byte)(t.Rgba[s + 3] * cover);
                }
            }
            return (n, n, px);
        }

        // the round JOIN (as the mockups' _round_join): a flat (230, 99, 16) disc r 36 round (37, 38), the join art inside r 33
        // left of x 53 (its arrow and ring; the card-slot bar starts at 55), cropped to (1, 2)..(74, 75)
        private static (int, int, byte[]) RoundJoin(UiTexture t)
        {
            const int x0 = 1, y0 = 2, n = 73;
            byte[] px = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float sx = x + x0 + 0.5f, sy = y + y0 + 0.5f;
                    float dist = MathF.Sqrt((sx - 37) * (sx - 37) + (sy - 38) * (sy - 38));
                    float outer = Math.Clamp(36 - dist + 0.5f, 0, 1), inner = Math.Clamp(33 - dist + 0.5f, 0, 1) * Math.Clamp(53 - sx + 0.5f, 0, 1);
                    int s = (Math.Clamp(y + y0, 0, t.Height - 1) * t.Width + Math.Clamp(x + x0, 0, t.Width - 1)) * 4, o = (y * n + x) * 4;
                    float a = t.Rgba[s + 3] / 255f * inner;
                    // the art over the flat disc
                    px[o] = (byte)(t.Rgba[s] * a + 230 * (1 - a));
                    px[o + 1] = (byte)(t.Rgba[s + 1] * a + 99 * (1 - a));
                    px[o + 2] = (byte)(t.Rgba[s + 2] * a + 16 * (1 - a));
                    px[o + 3] = (byte)(255 * outer);
                }
            }
            return (n, n, px);
        }

        // the arena's picture in an orange frame (the ROM's stage picture, an imported arena's own, else random's)
        private void ArenaPicture(string arena, float x, float top, float w, float h, float frame, int tag)
        {
            Fill(x - frame, top + frame, x + w + frame, top - h - frame, FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, tag);
            RoomMetadata? meta = MatchArenas.Find(arena);
            string? stage = meta == null ? null : MultiplayerMenu.StageModel(_stageFolder + "x", meta.Id);
            string? png = meta != null && stage == null ? MultiplayerMenu.ImportedPicture(meta.Id) : null;
            if (png != null)
            {
                try
                {
                    UiTexture tex = _drawMenu!.Widgets.Textures.GetOrAdd($"arena picture {png} {File.GetLastWriteTimeUtc(png).Ticks}", () =>
                    {
                        byte[] px = MphRecomp.Import.PngOut.Read(png, out int pw, out int ph);
                        return (pw, ph, px);
                    });
                    Quad(tex.Id, x, top, x + w, top - h, 0, 0, 1, 1, 1, 1, 1, 1, tag);
                    return;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
                {
                    Console.Error.WriteLine("online: no arena picture: " + ex.Message);
                }
            }
            Model(stage != null ? stage[..^"_Model.bin".Length] : _stageFolder + "random", x, top, w, h, tag);
        }

        // the menus' font at a size (1 = the setup's text), scaled about its anchor
        private void TextS(string text, float x, float top, int align, (float R, float G, float B) colour, int tag, float size, float alpha = 1)
        {
            int start = _out.Count;
            Text(text, x, top, align, colour, tag, alpha);
            if (size == 1) return;
            UiVertex S(UiVertex v)
            {
                v.X = x + (v.X - x) * size;
                v.Y = top + (v.Y - top) * size;
                return v;
            }
            for (int t = start; t < _out.Count; t++)
            {
                WidgetTri tri = _out[t];
                tri.A = S(tri.A); tri.B = S(tri.B); tri.C = S(tri.C);
                _out[t] = tri;
            }
        }
    }
}
