using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Online;
using MphRecomp.Replay;

// REPLAYS (Replay mode, docs/REPLAY_MODE.md R3; owner 2026-10-08): the vanilla multiplayer menu's REPLAYS button (RIVAL
// RADAR's corner, MultiplayerMenu call 93) opens a list in the multiplayer setup screens' look, made from their pieces
// (owner's pick: mockup A, length beside the result and PLAY after the date; no side panels, the width used instead;
// "record replays" as a setting in the corner opposite back). Tabs over it as the settings screen's (2026-10-19: MY
// REPLAYS, FAVORITES, SHARED WITH ME; touch or L1 / R1), three replays a page, newest first, the D-pad on past the last
// row to the next page (the page's n/m and arrows at the bottom): each row
// shows the four hunters (SELECT HUNTER's round icons, seats 1-4, the player who recorded it ringed), the mode, a medal
// with WIN / LOSS, the length, the date, a PLAY button (the lobby's START GAME pill) and the arena's picture far right,
// popping out of the row; a favorite has HUNTER LICENSE's gold star on its mode line. A row opens its details page: the
// picture, date, length, result, PLAY, EXPORT (the file to another app: 2026-10-19, was SHARE), FAVORITE (kept past the last ten),
// RENAME, DELETE and the four players.
// A network match's replay (O10, Replays track) names its players: NAME on the row over the foot of each opponent's icon,
// NAME#tag on the details page, where a player who carries a friend code is tappable: their HUNTER LICENSE as it was
// (RecompMenus.ReplayLicense.cs).
// PLAY works as the file select's files (owner 2026-10-08): a tap selects it, a second tap plays (A plays a focused one),
// with the file start's sound and no question. Sounds are borrowed from the ROM items they stand for (ApplyReplaySounds).
// The pages are the CREATE OR JOIN page's frame (its backdrop panel stretched wide, back arrow, title) plus input items
// with no look of their own except a focused one (the lobby's red player glow); everything else is drawn by DrawReplays
// from the ROM's own models (FrontendSession.Build), so pictures and icons can sit at any size. MenuLayout.Replays places
// the pages. The folder is read as the list comes in (never at startup: the in-game pause menu loads these menus too).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        // the two pages (appended; -1 = not installed): static for MenuLayout.For, as MultiplayerMenu's card and keypad
        private static int s_listPage = -1, s_detailPage = -1;
        public static bool IsReplaysPage(MenuPage page) => page.Index >= 0
            && (page.Index == s_listPage || page.Index == s_detailPage || page.Index == s_replayLicensePage
                || page.Index == s_sharePickPage);

        private IReadOnlyList<ReplayInfo> _replays = Array.Empty<ReplayInfo>();
        private string? _replayOpen; // the replay on the details page (its path)
        private int _replayOffset; // the list's first row (a multiple of ReplayRows)

        public int ReplaysPage => s_listPage;

        // watch a replay (FrontendSession: FrontendRequestKind.PlayReplay, Arg = the path); null = nothing plays it here
        public Action<string>? PlayReplay { get; set; }
        // EXPORT: the replay file out of the app (FrontendRequestKind.ShareReplay, Arg = the path); null = not on this host
        public Action<string>? ShareReplay { get; set; }

        // the game's name keyboard for RENAME (MultiplayerMenu.EditText: start text, most letters, the page it comes back
        // to, done = the new text or null = cancelled); null = not yet
        public Action<MenuEngine, string, int, int, Action<string?>>? EditText { get; set; }
        private const int TitleMax = 20;

        // our callbacks (RecompMenus takes every call >= 1000; ReplayCall answers these before its rows pages' dispatch)
        private const int ReplayRowCall = 2100, ReplayPlayCall = 2110, ReplayRowDeleteCall = 2120; // + the row
        private const int ReplayPrevCall = 2130, ReplayNextCall = 2131, ReplayRecordCall = 2132, ReplayDeleteFocusedCall = 2133;
        private const int ReplayUpCall = 2134, ReplayDownCall = 2135;
        private const int ReplayWatchCall = 2140, ReplayFavoriteCall = 2141, ReplayRenameCall = 2142, ReplayDeleteCall = 2143,
            ReplayShareCall = 2144;
        private const int ReplaySeatCall = 2150; // + the seat (the details page's players); 2154-2159: RecompMenus.ReplayLicense.cs
        private const int ReplayLastCall = 2199; // 2160-2199: RecompMenus.ReplayShare.cs
        private const int ReplayRows = 3;

        // ---- geometry (menu units, Y up, the touch screen's space widened; MenuLayout.Replays shows x -60..316) ----
        // the CREATE OR JOIN panel (blackdrop, drawn x -4.3..258.1 y -155..-24.6) stretched to x -52..308, down to -170
        private const float PanelStretchX = 97.6f, PanelStretchDown = 15, PanelDx = 1.1f;
        // a row: its bar, the map popping out of its right end
        private const float BarX0 = -46, BarX1 = 283, BarH = 36, RowTop0 = -36, RowPitch47 = 47;
        private const float IconD = 30.3f, IconPitch = 34.7f, IconDx = 5.3f, IconDy = 2.8f;
        private const float TextX = 101.5f, ColumnRight = 222.7f;
        private const float PillW = 46.5f, PillH = 11.3f, StarSize = 9.7f;
        private const float MapX = 237.5f, MapW = 64.2f, MapH = 40.4f, MapFrame = 1.2f;
        // the title's page arrows (the setup's option arrows), fixed beside "replays 10/10"
        private const float ArrowW = 9.8f, ArrowH = 12.4f, ArrowGap = 62, ArrowTop = -9.8f;
        private const float PageArrowGap = 30, PageIndicatorTop = -173f; // the list's page n/m, bottom centre
        // record replays, in the bottom-right corner (MenuLayout.Replays' corner region, x 330..462 y -200..-182)
        private const float RecordBoxX0 = 424, RecordBoxX1 = 458, RecordTop = -185, RecordBottom = -198, RecordLabelX = 419;
        // the details page
        private const float PicX = -42.9f, PicTop = -34.6f, PicW = 139.4f, PicH = 88.1f, PicFrame = 1.6f;
        private const float FactsX = 111.8f, FactsRight = 297.7f, FactsTop = -36.6f, FactsPitch = 12.5f;
        private const float BigPillTop = -77, BigPillW = 113.1f, BigPillH = 22.6f;
        private const float ButtonTop = -108.1f, ButtonW = 58.6f, ButtonH = 13.3f, ButtonPitch = 61.6f;
        // EXPORT: a button's size beside PLAY, at the facts' right edge, level with the pill's middle
        private const float ShareX0 = FactsRight - ButtonW, ShareTop = BigPillTop - (BigPillH - ButtonH) / 2;
        private const float PlayerX0 = -39.7f, PlayerPitch = 86.9f, PlayerTop = -133.6f, PlayerIcon = 30.3f;

        // the game's text colours: the title (page 60's), orange labels (CREATE GAME's), pale ("HUNTER"), white, and the
        // setup boxes' orange (gameoptions box_type) for the pictures' frames
        private static readonly (float R, float G, float B) TitleColour = (1, 206 / 255f, 156 / 255f), Orange = (1, 165 / 255f, 66 / 255f),
            Pale = (1, 247 / 255f, 156 / 255f), White = (1, 1, 1), FrameOrange = (230 / 255f, 99 / 255f, 8 / 255f);

        // items on the list page / the details page (-1 = none)
        private int _lPanel = -1, _lBack = -1, _lTitle = -1, _lPrev = -1, _lNext = -1, _lRecord = -1;
        private readonly int[] _lRow = { -1, -1, -1 }, _lPlay = { -1, -1, -1 };
        private int _dPanel = -1, _dBack = -1, _dTitle = -1, _dPlay = -1, _dFavorite = -1, _dRename = -1, _dDelete = -1, _dShare = -1;
        private readonly int[] _dSeat = { -1, -1, -1, -1 };
        // the ROM items whose sounds ours borrow (-1 = not found): CREATE OR JOIN's back arrow, the main menu's adventure
        // highlight (opening a replay sounds like choosing the campaign), a file select file (PLAY sounds like starting it),
        // an options page's value box (record replays sounds like changing one, every press)
        private const int MainMenuPage = 18, OptionBoxPage = 22;
        private int _soundBack = -1, _soundCampaign = -1, _soundFileStart = -1, _soundToggle = -1;
        private MenuSounds? _replaySounds;
        // PLAY pressed: the replay to start once its file-start sound has played (the host pauses the menus' audio as the
        // replay comes up), and the menu frames left until then
        private string? _launchPath;
        private int _launchTicks;
        private const int LaunchDelay = 30;
        private int _listTitleString = -1, _detailTitleString = -1;
        private string _stageFolder = @"stage\"; // the ROM's arena pictures (the ARENA page's screenshot frame's folder)

        // the multiplayer menu's REPLAYS button
        public void OpenReplays(MenuEngine menu)
        {
            if (ReplaysPage >= 0) menu.GoTo(ReplaysPage);
        }

        private string? ReplayDir => _settingsPath != null ? Path.GetDirectoryName(_settingsPath) : null;

        private void InstallReplays()
        {
            if (_file.Pages.Count <= MultiplayerMenu.LobbyPage) return;
            MenuPage frame = _file.Pages[MultiplayerMenu.CreateJoinPage];
            MenuItem? panel = frame.Items.FirstOrDefault(it => HasWidget(it, "blackdrop"));
            MenuItem? backIcon = frame.Items.FirstOrDefault(it => HasWidget(it, "backicon"));
            MenuItem? backButton = frame.Items.FirstOrDefault(it => HasWidget(it, "backhighlight"));
            MenuItem? title = frame.Items.FirstOrDefault(it => TextAt(it, 128, 172));
            MenuItem? shot = frame.Items.FirstOrDefault(it => HasWidget(it, "screenshot"));
            // the lobby's red glow round the local player's card (its Idle look, a 25-frame pulse): our items' focused look
            MenuItemState? glow = _file.Pages[MultiplayerMenu.LobbyPage].Items.Where(it => HasWidget(it, "player_highlight"))
                .SelectMany(it => it.States).FirstOrDefault(s => s.Code == (int)MenuState.Idle && s.WidgetIndex >= 0);
            if (panel == null || backIcon == null || backButton == null || title == null || glow == null) return;
            if (shot?.States.FirstOrDefault(s => s.WidgetIndex >= 0) is MenuItemState s0)
            {
                string path = _file.Widgets[s0.WidgetIndex].ModelPath;
                _stageFolder = path[..(path.LastIndexOf('\\') + 1)];
            }

            MenuPage AddFramePage(int back, params MenuAction[] more)
            {
                MenuPage page = _file.AddPage(index => new MenuPage
                {
                    Index = index,
                    Field0 = frame.Field0,
                    Field14 = frame.Field14,
                    Field16 = frame.Field16,
                    Field18 = frame.Field18,
                    Field1D = frame.Field1D,
                    Field1E = frame.Field1E,
                    Flags = frame.Flags,
                    Items = Array.Empty<MenuItem>(),
                    Actions = new[] { new MenuAction { Kind = (ushort)MenuKeys.B, TargetPage = (byte)back, Item = 0xFFFF, Field17 = 4 } }.Concat(more).ToArray(),
                    Timers = Array.Empty<MenuTimer>()
                });
                return page;
            }

            // an input item: no look until focused (then the lobby's glow, which DrawReplays shapes round it); A or a tap in
            // its rectangle (menu space) runs `call`; secondTap: a tap only selects it, the next tap runs it (Flags bit 1, as
            // the file select's files)
            MenuItem Input(int index, float x0, float yTop, float x1, float yBottom, int call, bool secondTap = false, MenuState? init = null)
            {
                var actions = new List<MenuAction>
                {
                    new()
                    {
                        Kind = (ushort)MenuKeys.A, RectX = (short)MathF.Floor(x0), RectY = (short)MathF.Floor(yBottom + 192),
                        RectW = (short)MathF.Ceiling(x1), RectH = (short)MathF.Ceiling(yTop + 192), Calls = new[] { (0, call) },
                        TargetPage = 0xFF, Item = 0xFFFF, Flags = (byte)(secondTap ? 2 : 0)
                    }
                };
                return new MenuItem
                {
                    Index = index,
                    Field34 = (byte)(init ?? MenuState.Idle),
                    Field35 = (byte)(init ?? MenuState.Idle),
                    States = new[]
                    {
                        new MenuItemState { Code = (int)MenuState.Focused, Kind = glow.Kind, Field2 = glow.Field2, WidgetIndex = glow.WidgetIndex, WidgetFlags = glow.WidgetFlags }
                    },
                    Links = Array.Empty<MenuLink>(),
                    Actions = actions
                };
            }

            // a touch-only item (the page arrows)
            MenuItem Touch(int index, float x0, float yTop, float x1, float yBottom, int call) => new()
            {
                Index = index,
                Field34 = (byte)MenuState.Idle,
                Field35 = (byte)MenuState.Idle,
                States = Array.Empty<MenuItemState>(),
                Links = Array.Empty<MenuLink>(),
                Actions = new[] { TouchRect(x0, yBottom + 192, x1, yTop + 192, call) }
            };

            // ---- the list ----
            // X deletes the focused row's replay: a page action, so it makes no row sound (a row's borrowed sound plays for
            // any of its actions). L1 / R1 the tabs; up / down page actions so the D-pad goes on past a page's last row
            MenuPage list = AddFramePage(MultiplayerMenu.TypePage, Call(MenuKeys.R, ReplayTabNextCall), Call(MenuKeys.L, ReplayTabPrevCall),
                Call(MenuKeys.X, ReplayDeleteFocusedCall), Call(MenuKeys.Up, ReplayUpCall), Call(MenuKeys.Down, ReplayDownCall));
            s_listPage = list.Index;
            _lPanel = list.AddItem(i => Clone(panel, i, PanelDx, 0, stretchX: PanelStretchX, stretchDown: PanelStretchDown)).Index;
            list.AddItem(i => Clone(backIcon, i, 0, 0));
            _lBack = list.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions.Select(a => WithTarget(a, MultiplayerMenu.TypePage)).ToList())).Index;
            _listTitleString = _strings.Add(""); // (the tabs are drawn where the title was)
            _lTitle = list.AddItem(i => Clone(title, i, 0, 0, stringId: _listTitleString)).Index;
            for (int r = 0; r < ReplayRows; r++)
            {
                float top = RowTop0 - RowPitch47 * r;
                int row = r;
                _lRow[r] = list.AddItem(i => Input(i, BarX0, top, BarX1, top - BarH, ReplayRowCall + row)).Index;
            }
            for (int r = 0; r < ReplayRows; r++)
            {
                (float x0, float y0, float x1, float y1) = PillRect(r);
                int row = r;
                // after the rows, so a tap on the pill is the pill's (the engine tries the last items first)
                _lPlay[r] = list.AddItem(i => Input(i, x0, y0, x1, y1, ReplayPlayCall + row, secondTap: true)).Index;
            }
            _lPrev = list.AddItem(i => Touch(i, 128 - PageArrowGap - ArrowW - 4, PageIndicatorTop + 4, 128 - PageArrowGap + 4, PageIndicatorTop - ArrowH - 4, ReplayPrevCall)).Index;
            _lNext = list.AddItem(i => Touch(i, 128 + PageArrowGap - 4, PageIndicatorTop + 4, 128 + PageArrowGap + ArrowW + 4, PageIndicatorTop - ArrowH - 4, ReplayNextCall)).Index;
            _lRecord = list.AddItem(i => Input(i, RecordLabelX - 76, RecordTop + 1, RecordBoxX1, RecordBottom - 1, ReplayRecordCall)).Index;

            // ---- one replay ----
            MenuPage detail = AddFramePage(list.Index);
            s_detailPage = detail.Index;
            _dPanel = detail.AddItem(i => Clone(panel, i, PanelDx, 0, stretchX: PanelStretchX, stretchDown: PanelStretchDown)).Index;
            detail.AddItem(i => Clone(backIcon, i, 0, 0));
            _dBack = detail.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions.Select(a => WithTarget(a, list.Index)).ToList())).Index;
            _detailTitleString = _strings.Add("");
            _dTitle = detail.AddItem(i => Clone(title, i, 0, 0, stringId: _detailTitleString)).Index;
            _dPlay = detail.AddItem(i => Input(i, FactsX, BigPillTop, FactsX + BigPillW, BigPillTop - BigPillH, ReplayWatchCall, secondTap: true)).Index;
            int[] buttons = new int[3];
            int[] calls = { ReplayFavoriteCall, ReplayRenameCall, ReplayDeleteCall };
            for (int j = 0; j < 3; j++)
            {
                float x0 = FactsX + ButtonPitch * j;
                int call = calls[j];
                buttons[j] = detail.AddItem(i => Input(i, x0, ButtonTop, x0 + ButtonW, ButtonTop - ButtonH, call)).Index;
            }
            (_dFavorite, _dRename, _dDelete) = (buttons[0], buttons[1], buttons[2]);
            _dShare = detail.AddItem(i => Input(i, ShareX0, ShareTop, ShareX0 + ButtonW, ShareTop - ButtonH, ReplayShareCall)).Index;
            // the players (a tap: their license as it was, when the replay carries who they are)
            for (int k = 0; k < _dSeat.Length; k++)
            {
                float x0 = PlayerX0 + PlayerPitch * k;
                int call = ReplaySeatCall + k;
                _dSeat[k] = detail.AddItem(i => Input(i, x0 - 2.4f, PlayerTop + 2.4f, x0 + PlayerPitch - 4.8f, PlayerTop - PlayerIcon - 2.4f, call)).Index;
            }

            // ---- a player's license as it was (RecompMenus.ReplayLicense.cs) ----
            InstallReplayLicense(AddFramePage, (i, x0, y0, x1, y1, call) => Input(i, x0, y0, x1, y1, call), panel, backIcon, backButton, title, detail.Index);
            // ---- sharing: the SHARED view, SAVE, SEND TO FRIEND (RecompMenus.ReplayShare.cs) ----
            InstallReplayShare(list, detail, AddFramePage, (i, x0, y0, x1, y1, call) => Input(i, x0, y0, x1, y1, call), Touch,
                panel, backIcon, backButton, title);

            // the sounds' ROM items, found by what they do (each revision numbers its items its own way)
            _soundBack = backButton.Index;
            MenuItem? ByAction(int page, Func<MenuAction, bool> match) => page < _file.Pages.Count
                ? _file.Pages[page].Items.FirstOrDefault(it => it.Actions.Any(a => (a.Kind & (ushort)MenuKeys.A) != 0 && match(a))) : null;
            _soundCampaign = ByAction(MainMenuPage, a => a.TargetPage == FileSelect.Page)?.Index ?? -1;
            _soundFileStart = ByAction(FileSelect.Page, a => a.TargetPage == FileSelect.BeginGamePage && a.Calls.Count > 0)?.Index ?? -1;
            _soundToggle = OptionBoxPage < _file.Pages.Count ? _file.Pages[OptionBoxPage].Items.FirstOrDefault(it => HasWidget(it, "box_arrows")
                && it.Actions.Any(a => (a.Kind & (ushort)MenuKeys.A) != 0 && a.Calls.Count > 0))?.Index ?? -1 : -1;
        }

        // Our items' sounds (MenuSounds.ItemLike), once per sound table: the back arrows sound like CREATE OR JOIN's, a row
        // (opening its details) like the main menu's adventure, PLAY like starting a file, record replays like an option box.
        private void ApplyReplaySounds(MenuSounds sounds)
        {
            if (ReferenceEquals(sounds, _replaySounds)) return;
            _replaySounds = sounds;
            void Like(int page, int item, int romPage, int romItem)
            {
                if (item >= 0 && romItem >= 0) sounds.ItemLike(page, item, romPage, romItem);
            }
            Like(s_listPage, _lBack, MultiplayerMenu.CreateJoinPage, _soundBack);
            Like(s_detailPage, _dBack, MultiplayerMenu.CreateJoinPage, _soundBack);
            for (int r = 0; r < ReplayRows; r++)
            {
                Like(s_listPage, _lRow[r], MainMenuPage, _soundCampaign);
                Like(s_listPage, _lPlay[r], FileSelect.Page, _soundFileStart);
            }
            Like(s_detailPage, _dPlay, FileSelect.Page, _soundFileStart);
            Like(s_listPage, _lRecord, OptionBoxPage, _soundToggle);
            foreach (int seat in _dSeat) Like(s_detailPage, seat, MainMenuPage, _soundCampaign);
            ApplyReplayLicenseSounds(Like);
            ApplyReplayShareSounds(Like);
        }

        private static (float X0, float Top, float X1, float Bottom) PillRect(int row)
        {
            float top = RowTop0 - RowPitch47 * row;
            float x0 = TextX + (ColumnRight - TextX) - PillW;
            return (x0, top - 24.2f, x0 + PillW, top - 24.2f - PillH);
        }

        // ---- runtime ----

        private void ListReplays()
        {
            string? dir = ReplayDir;
            try
            {
                _replays = dir != null ? ReplayLibrary.List(dir) : Array.Empty<ReplayInfo>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("replays: folder unreadable (" + ex.Message + ")");
                _replays = Array.Empty<ReplayInfo>();
            }
            BuildListed();
        }

        private ReplayInfo? OpenInfo => _replays.FirstOrDefault(i => i.Path == _replayOpen);

        private int ReplayPages => Math.Max(1, (_listed.Count + ReplayRows - 1) / ReplayRows);

        // RecompMenus.OnPageEntered: the folder read as the list comes in; the details page shows the replay open
        private void EnterReplays(MenuEngine menu, int page)
        {
            if (IsReplaysPageIndex(page))
            {
                ApplyReplaySounds(menu.Sounds);
                _rpMenu = menu;
            }
            if (page == s_listPage)
            {
                if (_lastPage != s_detailPage) _replayOffset = 0;
                ListReplays();
                // back from a replay's page or from watching it: its tab (MY REPLAYS unless it's a favorite opened from
                // FAVORITES, or a friend's) and page of the list, its row focused; from elsewhere with none open, MY REPLAYS
                ReplayInfo? open = OpenInfo;
                if (open?.Shared == true) _replayTab = 2;
                else if (open != null) { if (_replayTab != 1 || !open.Pinned) _replayTab = 0; }
                else if (!IsReplaysPageIndex(_lastPage)) _replayTab = 0;
                BuildListed();
                _rpFetch = true;
                int at = _replayOpen == null ? -1 : _listed.FindIndex(e => e.Info?.Path == _replayOpen);
                if (at >= 0) _replayOffset = at / ReplayRows * ReplayRows;
                if (_replayOffset >= _listed.Count) _replayOffset = 0;
                ShowReplayRows(menu, at >= 0 ? at - _replayOffset : 0);
            }
            else if (page == s_detailPage)
            {
                ListReplays();
                ShowDetail(menu);
                // back from a player's license: their seat focused
                if (_lastPage == s_replayLicensePage && _rlSeat is >= 0 and < 4) menu.SetState(_dSeat[_rlSeat], MenuState.Focused);
            }
            else if (page == s_replayLicensePage)
            {
                EnterReplayLicense(menu);
            }
            else if (page == s_sharePickPage)
            {
                EnterSharePick(menu);
            }
        }

        // the list's items for the rows shown: rows past the end hidden, PLAY greyed where the replay can't play here
        private void ShowReplayRows(MenuEngine menu, int focus)
        {
            int shown = Math.Max(0, Math.Min(ReplayRows, _listed.Count - _replayOffset));
            // an offer's button focused (it stays so): no row focused as well
            bool other = Enumerable.Range(0, shown).Any(r => _listed[_replayOffset + r].Offer != null
                    && (menu.ItemState(_lGet[r]) == MenuState.Focused || menu.ItemState(_lDrop[r]) == MenuState.Focused));
            for (int r = 0; r < ReplayRows; r++)
            {
                if (r >= shown)
                {
                    menu.SetState(_lRow[r], MenuState.Hidden);
                    menu.SetState(_lPlay[r], MenuState.Hidden);
                    continue;
                }
                ReplayInfo? info = _listed[_replayOffset + r].Info;
                menu.SetState(_lRow[r], !other && r == Math.Clamp(focus, 0, shown - 1) ? MenuState.Focused : MenuState.Idle);
                menu.SetState(_lPlay[r], info == null ? MenuState.Hidden : info.PlaysHere ? MenuState.Idle : MenuState.State5);
            }
            ShowShareRows(menu, shown);
            if (shown == 0 && _replayTab == 0) menu.SetState(_lRecord, MenuState.Focused);
        }

        // D-pad up / down on the list (page actions: the page's own sound, the focus moved quietly): the same column a row
        // up or down (the bar, PLAY, an offer's DOWNLOAD / DELETE), on past the last row to the next page and then RECORD
        // REPLAYS (MY REPLAYS), back up likewise
        private void ReplayStep(MenuEngine menu, bool down)
        {
            int shown = Math.Max(0, Math.Min(ReplayRows, _listed.Count - _replayOffset));
            int focused = menu.FocusedItem(), row = -1, col = 0;
            for (int r = 0; r < shown; r++)
            {
                if (focused == _lRow[r]) row = r;
                else if (focused == _lPlay[r]) (row, col) = (r, 1);
                else if (focused == _lGet[r]) (row, col) = (r, 2);
                else if (focused == _lDrop[r]) (row, col) = (r, 3);
            }
            void Focus(int item)
            {
                if (focused != -1) menu.SetState(focused, MenuState.Idle);
                menu.SetState(item, MenuState.Focused);
                focused = item;
            }
            // row r's item in this column, else its bar
            int Cell(int r, int c)
            {
                if (c == 1 && menu.ItemState(_lPlay[r]) == MenuState.Idle) return _lPlay[r];
                if (c >= 2 && _listed[_replayOffset + r].Offer is ReplayOffer o && !_rpBusy.Contains(o.Id))
                {
                    int item = c == 2 ? _lGet[r] : _lDrop[r];
                    if (menu.ItemState(item) != MenuState.Hidden) return item;
                }
                return _lRow[r];
            }
            // another page of rows, focused on the given row in this column
            void Page(int offset, int r)
            {
                if (focused != -1) menu.SetState(focused, MenuState.Idle);
                _replayOffset = offset;
                ShowReplayRows(menu, r);
                focused = menu.FocusedItem();
                int at = Math.Min(r, Math.Min(ReplayRows, _listed.Count - _replayOffset) - 1);
                if (at >= 0 && Cell(at, col) != focused) Focus(Cell(at, col));
            }
            if (focused == _lRecord && _replayTab == 0)
            {
                if (!down && shown > 0) Focus(_lRow[shown - 1]);
            }
            else if (row == -1)
            {
                if (shown > 0) Focus(_lRow[0]);
                else if (_replayTab == 0) Focus(_lRecord);
            }
            else if (down)
            {
                if (row < shown - 1) Focus(Cell(row + 1, col));
                else if (_replayOffset + ReplayRows < _listed.Count) Page(_replayOffset + ReplayRows, 0);
                else if (_replayTab == 0) Focus(_lRecord);
            }
            else
            {
                if (row > 0) Focus(Cell(row - 1, col));
                else if (_replayOffset > 0) Page(_replayOffset - ReplayRows, ReplayRows - 1);
            }
        }

        private void ShowDetail(MenuEngine menu)
        {
            ReplayInfo? r = OpenInfo;
            if (r == null)
            {
                menu.GoTo(s_listPage); // gone (deleted, or pushed out by newer recordings)
                return;
            }
            _strings.Set(_detailTitleString, DetailTitle(r).ToUpperInvariant());
            // nothing focused as it opens (owner 2026-10-08); the D-pad or A focuses PLAY first
            menu.SetState(_dPlay, r.PlaysHere ? MenuState.Idle : MenuState.State5);
            // a friend's replay: no FAVORITE or RENAME (its sender's name goes there)
            Show(menu, _dFavorite, !r.Shared);
            Show(menu, _dRename, !r.Shared);
            for (int k = 0; k < _dSeat.Length; k++) Show(menu, _dSeat[k], LicenseOf(r, k) != null);
        }

        // a seat's name as the replay carries it: NAME#tag (tag 0 = not known: the name alone); null = none carried
        private static string? SeatName(ReplayInfo r, int seat, bool tag = true)
            => r.Identity(seat) is ReplaySeatIdentity id && id.Name.Length > 0 ? tag && id.Tag > 0 ? OnlineNames.Display(id.Name, id.Tag) : id.Name : null;

        // the seat's identity when its license can be opened (a friend code to know them by)
        private static ReplaySeatIdentity? LicenseOf(ReplayInfo r, int seat)
            => seat < r.Players.Count && !r.Players[seat].Bot && r.Identity(seat) is ReplaySeatIdentity id && !string.IsNullOrEmpty(id.FriendCode) ? id : null;

        // Returns true when the call was one of REPLAYS'
        private bool ReplayCall(MenuEngine menu, int call)
        {
            if (call < ReplayRowCall || call > ReplayLastCall) return false;
            if (call >= ShareFirstCall)
            {
                ShareCall(menu, call);
                return true;
            }
            if (call >= ReplaySeatCall)
            {
                ReplayLicenseCall(menu, call);
                return true;
            }
            if (call == ReplayDeleteFocusedCall)
            {
                // X: the focused row's (or its PLAY's) replay
                int focused = menu.FocusedItem();
                int at = Array.IndexOf(_lRow, focused);
                if (at < 0) at = Array.IndexOf(_lPlay, focused);
                if (at < 0) at = Array.IndexOf(_lGet, focused);
                if (at < 0) at = Array.IndexOf(_lDrop, focused);
                if (at < 0) return true;
                call = ReplayRowDeleteCall + at;
            }
            if (call is >= ReplayRowCall and < ReplayRowCall + ReplayRows or >= ReplayPlayCall and < ReplayPlayCall + ReplayRows
                or >= ReplayRowDeleteCall and < ReplayRowDeleteCall + ReplayRows)
            {
                int row = call % 10, at = _replayOffset + row;
                if (at >= _listed.Count) return true;
                if (_listed[at].Offer is ReplayOffer offer)
                {
                    // a friend's replay not yet here: the row downloads it, X deletes it
                    _rpMenu = menu;
                    if (_rpBusy.Contains(offer.Id) || call - row == ReplayPlayCall) return true;
                    if (call - row == ReplayRowCall) Download(offer);
                    else AskDrop(offer);
                    return true;
                }
                ReplayInfo r = _listed[at].Info!;
                _replayOpen = r.Path;
                if (call - row == ReplayRowCall) menu.GoTo(s_detailPage);
                else if (call - row == ReplayPlayCall) Watch(r);
                else AskDelete(r, backToList: false);
                return true;
            }
            switch (call)
            {
            case ReplayPrevCall:
            case ReplayNextCall:
                if (ReplayPages > 1)
                {
                    // wraps round, as the rows pages do
                    int pages = ReplayPages;
                    _replayOffset = (_replayOffset / ReplayRows + (call == ReplayNextCall ? 1 : pages - 1)) % pages * ReplayRows;
                    ShowReplayRows(menu, 0);
                }
                return true;
            case ReplayUpCall:
            case ReplayDownCall:
                ReplayStep(menu, call == ReplayDownCall);
                return true;
            case ReplayRecordCall:
                _settings.RecordReplays = !_settings.RecordReplays;
                _settings.Save(_settingsPath);
                return true;
            }
            ReplayInfo? open = OpenInfo;
            if (open == null) return true;
            switch (call)
            {
            case ReplayWatchCall:
                Watch(open);
                break;
            case ReplayFavoriteCall:
                ToggleFavorite(open);
                break;
            case ReplayRenameCall:
                Rename(open);
                break;
            case ReplayDeleteCall:
                AskDelete(open, backToList: true);
                break;
            case ReplayShareCall:
                if (ShareReplay == null) Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                else ShareReplay(open.Path);
                break;
            }
            return true;
        }

        // the row's PLAY: the game's ask box first (owner 2026-10-08: "you get a confirmation that you want to watch it")
        private void Watch(ReplayInfo r)
        {
            if (!r.PlaysHere)
            {
                Import.Popup?.Inform("this replay can't play here.\n\n" + r.Refused!.ToLowerInvariant());
                return;
            }
            if (PlayReplay == null)
            {
                Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            if (_launchPath != null) return; // one already starting
            _replayOpen = r.Path;
            _launchPath = r.Path;
            _launchTicks = LaunchDelay;
        }

        // every menu frame (RecompMenus.Tick): a pressed PLAY starts its replay after LaunchDelay frames
        private void TickReplays()
        {
            TickReplayLicense();
            TickReplayShare();
            if (_launchPath == null || --_launchTicks > 0) return;
            string path = _launchPath;
            _launchPath = null;
            MultiplayerMenu.PendingReturn = ReplaysPage; // back to the list after it, on its row
            // the REPLAYS pages' server connection isn't kept while it plays (back on the list connects again)
            if (_client != null && !_connecting) CloseOnline();
            PlayReplay?.Invoke(path);
        }

        // FAVORITE: kept in replays/pinned, past the last ten (ReplayLibrary.Pin)
        private void ToggleFavorite(ReplayInfo r)
        {
            string? dir = ReplayDir;
            if (dir == null) return;
            if (!r.Pinned)
            {
                Try(() => _replayOpen = ReplayLibrary.Pin(dir, r.Path));
                return;
            }
            // back among the recent ones: if ten newer ones are there already, it goes at once (ReplayLibrary.Prune)
            string name = Path.GetFileName(r.Path);
            int newer = _replays.Count(i => !i.Pinned && string.CompareOrdinal(Path.GetFileName(i.Path), name) > 0);
            if (newer < ReplayLibrary.KeepRecent || Import.Popup == null)
            {
                Try(() => _replayOpen = ReplayLibrary.Unpin(dir, r.Path));
                return;
            }
            Import.Popup.Ask($"remove it from favorites?\n\nyou have {ReplayLibrary.KeepRecent} newer replays, so this one will be deleted.", yes =>
            {
                if (!yes) return;
                Try(() => ReplayLibrary.Unpin(dir, r.Path));
                GoToReplayList();
            });
        }

        private void Rename(ReplayInfo r)
        {
            if (EditText == null || _stepMenu == null || s_detailPage < 0)
            {
                Import.Popup?.Inform(FrontendSession.WorkInProgressText);
                return;
            }
            EditText(_stepMenu, r.Title[..Math.Min(r.Title.Length, TitleMax)], TitleMax, s_detailPage, text =>
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                // (also a favorite: a named replay is never pruned with the last ten)
                Try(() => _replayOpen = ReplayDir is string dir ? ReplayLibrary.RenameAndKeep(dir, r.Path, text)
                    : ReplayLibrary.Rename(r.Path, text));
            });
        }

        private void AskDelete(ReplayInfo r, bool backToList)
        {
            void Delete()
            {
                Try(() => ReplayLibrary.Delete(r.Path));
                if (backToList) GoToReplayList();
                else ReshowReplayList();
            }
            if (Import.Popup == null) Delete();
            else Import.Popup.Ask($"delete this replay?\n\n{r.Title.ToLowerInvariant()}", yes =>
            {
                if (yes) Delete();
            });
        }

        // a file move or delete that failed (gone, in use, a folder Android won't let us write) says so in the game's box
        private void Try(Action file)
        {
            try
            {
                file();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Import.Popup?.Inform("couldn't change that replay.\n\n" + ex.Message.ToLowerInvariant());
            }
            ListReplays();
            if (_stepMenu?.Page?.Index == s_detailPage && OpenInfo is ReplayInfo r)
            {
                _strings.Set(_detailTitleString, DetailTitle(r).ToUpperInvariant());
            }
        }

        private void GoToReplayList()
        {
            if (_stepMenu != null && ReplaysPage >= 0) _stepMenu.GoTo(ReplaysPage);
        }

        // the list after a delete: the page stepped back when its last row went, the focus kept on a row
        private void ReshowReplayList()
        {
            MenuEngine? menu = _rpMenu ?? _stepMenu;
            if (menu == null || menu.Page?.Index != s_listPage) return;
            int focus = Array.FindIndex(_lRow, i => menu.ItemState(i) == MenuState.Focused);
            while (_replayOffset > 0 && _replayOffset >= _listed.Count) _replayOffset -= ReplayRows;
            ShowReplayRows(menu, focus < 0 ? 0 : focus);
        }

        // ---- what's shown ----

        // a renamed replay's own name; else "battle - combat hall"
        private static string DetailTitle(ReplayInfo r)
        {
            if (r.Header == null) return r.Title.ToLowerInvariant();
            return Renamed(r) ? r.Title.ToLowerInvariant() : $"{ModeName(r.Header.Settings.Mode)} - {r.Arena.ToLowerInvariant()}";
        }

        // the recorder names a replay "<Mode> <arena>" (ReplayLibrary); anything else is the player's own name for it
        // (a friend's: its file is named for who sent it, so it reads as mode and arena)
        private static bool Renamed(ReplayInfo r) => r.Header != null && !r.Shared
            && !r.Title.Equals($"{r.Header.Settings.Mode} {r.Arena}", StringComparison.OrdinalIgnoreCase)
            && !r.Title.Equals($"{r.Header.Settings.Mode} {r.Header.Settings.Arena}", StringComparison.OrdinalIgnoreCase);

        private static string ModeName(GameMode mode) => mode switch
        {
            GameMode.BattleTeams => "team battle",
            GameMode.SurvivalTeams => "team survival",
            GameMode.BountyTeams => "team bounty",
            GameMode.NodesTeams => "team nodes",
            GameMode.DefenderTeams => "team defender",
            GameMode.PrimeHunter => "prime hunter",
            _ => mode.ToString().ToLowerInvariant()
        };

        private static string Length(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

        private static string Fit(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";

        private static string Day(DateTime? t) => t is DateTime d ? d.ToString("MMM d", CultureInfo.InvariantCulture).ToLowerInvariant() : "";

        // ---- drawing (FrontendSession.Build, after the page's own triangles) ----

        private readonly List<WidgetTri> _scratch = new();
        private readonly Dictionary<string, List<WidgetTri>> _stills = new();
        private MenuEngine? _drawMenu;
        private List<WidgetTri> _out = null!;

        // The rows, pictures, icons and texts of the REPLAYS pages, in menu space, each tagged with one of the page's items
        // (the layout places whole items); a focused item's glow (its own look: the lobby's player_highlight) reshaped
        // round it. Nothing while the page comes in or goes.
        public void DrawReplays(MenuEngine menu, List<WidgetTri> tris)
        {
            MenuPage? page = menu.Page;
            if (page == null || !IsReplaysPage(page)) return;
            _drawMenu = menu;
            _out = tris;
            int panel = page.Index == s_listPage ? _lPanel : page.Index == s_detailPage ? _dPanel : page.Index == s_sharePickPage ? _spPanel : _rlPanel;
            bool settled = menu.PendingPage == -1 && menu.ItemState(panel) == MenuState.Idle && menu.ItemCode(panel) == (int)MenuState.Idle;
            // the glows: lifted out of the engine's triangles, drawn again by Glow below in their place in the order
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
                if (page.Index == s_listPage) DrawList(menu, glows);
                else if (page.Index == s_detailPage) DrawDetail(menu, glows);
                else if (page.Index == s_sharePickPage) DrawSharePick(glows);
                else DrawReplayLicense(glows);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                // a missing ROM model (an older extraction): the page stays usable without its pictures
                Console.Error.WriteLine("replays: can't draw (" + ex.Message + ")");
            }
        }

        private void DrawList(MenuEngine menu, Dictionary<int, List<WidgetTri>> glows)
        {
            int shown = Math.Max(0, Math.Min(ReplayRows, _listed.Count - _replayOffset));
            for (int r = 0; r < shown; r++)
            {
                if (_listed[_replayOffset + r].Offer is ReplayOffer offer)
                {
                    DrawOffer(offer, r, glows);
                    continue;
                }
                ReplayInfo info = _listed[_replayOffset + r].Info!;
                int tag = _lRow[r];
                float top = RowTop0 - RowPitch47 * r;
                // the bar: the setup's value box (gameoptions box_type) stretched over a dark band
                Fill(BarX0, top, BarX1, top - BarH, 0, 0, 0, 0.35f, tag);
                Box(BarX0, top, BarX1, top - BarH, tag);
                Glow(glows, _lRow[r], BarX0, top, BarX1, top - BarH, tag);
                Hunters(info, BarX0 + IconDx, top - IconDy, IconD, IconPitch, tag);
                Names(info, BarX0 + IconDx, top - IconDy, IconD, IconPitch, tag);
                // mode (or the player's name for it), result + length, date + PLAY
                string what = info.Header == null ? "can't be read" : Renamed(info) ? info.Title.ToLowerInvariant() : ModeName(info.Header.Settings.Mode);
                Text(Fit(what, info.Pinned ? 16 : 18), TextX, top - 3.2f, 0, Orange, tag);
                if (info.Pinned) Star(ColumnRight - StarSize, top - 2.3f, StarSize, tag); // a favorite
                Result(info, TextX, top - 13.3f, 9.7f, tag);
                if (info.Header != null) Text(Length(info.Seconds), ColumnRight, top - 14.5f, 1, White, tag);
                if (SharedWho(info) is string who) FitText("from " + who, TextX, top - 25.9f, 0, ColumnRight - PillW - 4 - TextX, White, tag);
                else Text(Day(info.Recorded), TextX, top - 25.9f, 0, White, tag);
                (float px0, float py0, float px1, float py1) = PillRect(r);
                Pill(px0, py0, px1 - px0, py0 - py1, info.PlaysHere, _lPlay[r], glows);
                // the arena, far right, out past the bar's end and over its edges
                Picture(info, MapX, top + 2.2f, MapW, MapH, MapFrame, tag);
            }
            DrawReplayTabs(shown, ReplayPages > 1 ? 128 + PageArrowGap + ArrowW + 8 : 128, ReplayPages > 1 ? 0 : 2);
            if (shown == 0 && _replayTab == 0)
            {
                int tag = _lTitle;
                Text(_listed.Count == 0 ? "no replays yet." : "", 128, -70, 2, White, tag);
                Text(_settings.RecordReplays ? "play a match against bots: it is recorded here." : "turn on record replays below to keep your matches.",
                    128, -84, 2, Pale, tag);
            }
            if (ReplayPages > 1)
            {
                Arrow(false, 128 - PageArrowGap - ArrowW, PageIndicatorTop, _lPrev);
                Arrow(true, 128 + PageArrowGap, PageIndicatorTop, _lNext);
                Text($"{_replayOffset / ReplayRows + 1}/{ReplayPages}", 128, PageIndicatorTop - 2.2f, 2, White, _lTitle);
            }
            // record replays (2026-10-08: on / off in the corner); MY REPLAYS only
            if (_replayTab != 0) return;
            Text("record replays", RecordLabelX, RecordTop - 2.5f, 1, Pale, _lRecord);
            Fill(RecordBoxX0, RecordTop, RecordBoxX1, RecordBottom, 0, 0, 0, 0.43f, _lRecord);
            Box(RecordBoxX0, RecordTop, RecordBoxX1, RecordBottom, _lRecord);
            Glow(glows, _lRecord, RecordBoxX0, RecordTop, RecordBoxX1, RecordBottom, _lRecord);
            Text(_settings.RecordReplays ? "on" : "off", (RecordBoxX0 + RecordBoxX1) / 2, RecordTop - 2.5f, 2, White, _lRecord);
        }

        private void DrawDetail(MenuEngine menu, Dictionary<int, List<WidgetTri>> glows)
        {
            ReplayInfo? r = OpenInfo;
            if (r == null) return;
            int tag = _dPlay;
            Picture(r, PicX, PicTop, PicW, PicH, PicFrame, tag);
            string when = r.Recorded is DateTime t ? t.ToString("MMM d HH:mm", CultureInfo.InvariantCulture).ToLowerInvariant() : "";
            Text("date", FactsX, FactsTop, 0, Pale, tag);
            Text(when, FactsRight, FactsTop, 1, White, tag);
            Text("length", FactsX, FactsTop - FactsPitch, 0, Pale, tag);
            Text(Length(r.Seconds) + (r.Complete ? "" : " (ended early)"), FactsRight, FactsTop - FactsPitch, 1, White, tag);
            Text("result", FactsX, FactsTop - 2 * FactsPitch, 0, Pale, tag);
            if (!r.PlaysHere) Text("can't play here", FactsRight, FactsTop - 2 * FactsPitch, 1, White, tag);
            else if (r.LocalPlace is int) Result(r, FactsRight - ResultWidth(r) - 10.9f - 2.4f, FactsTop - 2 * FactsPitch + 1.6f, 10.9f, tag);
            Pill(FactsX, BigPillTop, BigPillW, BigPillH, r.PlaysHere, _dPlay, glows);
            Fill(ShareX0, ShareTop, ShareX0 + ButtonW, ShareTop - ButtonH, 0, 0, 0, 0.35f, _dShare);
            Box(ShareX0, ShareTop, ShareX0 + ButtonW, ShareTop - ButtonH, _dShare);
            Glow(glows, _dShare, ShareX0, ShareTop, ShareX0 + ButtonW, ShareTop - ButtonH, _dShare);
            Text("export", ShareX0 + ButtonW / 2, ShareTop - 2.65f, 2, Orange, _dShare);
            string[] labels = { "favorite", "rename", "delete" };
            int[] items = { _dFavorite, _dRename, _dDelete };
            DrawDetailShare(r, glows);
            for (int j = r.Shared ? 2 : 0; j < 3; j++)
            {
                float x0 = FactsX + ButtonPitch * j;
                Fill(x0, ButtonTop, x0 + ButtonW, ButtonTop - ButtonH, 0, 0, 0, 0.35f, items[j]);
                Box(x0, ButtonTop, x0 + ButtonW, ButtonTop - ButtonH, items[j]);
                Glow(glows, items[j], x0, ButtonTop, x0 + ButtonW, ButtonTop - ButtonH, items[j]);
                if (j == 0 && r.Pinned)
                {
                    // a favorite: the star in the box, the label white beside it
                    float star = ButtonH - 4.4f;
                    Star(x0 + 2.4f, ButtonTop - 2.2f, star, items[j]);
                    Text(labels[j], x0 + 2.4f + star + (ButtonW - 2.4f - star) / 2, ButtonTop - 2.65f, 2, White, items[j]);
                }
                else Text(labels[j], x0 + ButtonW / 2, ButtonTop - 2.65f, 2, Orange, items[j]);
            }
            // the players, seat 1-4: round icon, hunter, you / bot / their name
            IReadOnlyList<MatchPlayer> seats = r.Players;
            for (int i = 0; i < seats.Count && i < 4; i++)
            {
                float x = PlayerX0 + PlayerPitch * i;
                Icon(seats[i], x, PlayerTop, PlayerIcon, i == r.LocalSeat, tag);
                Text(HunterWord(seats[i]), x + 36.4f, PlayerTop - 6.9f, 0, White, tag);
                string who = i == r.LocalSeat ? "you" : seats[i].Bot ? "bot" : (SeatName(r, i) ?? seats[i].Nickname ?? "").ToLowerInvariant();
                // (drawn as the page's: the layout places an item by its middle, and seat 1's reaches into the back arrow's corner)
                bool tappable = LicenseOf(r, i) != null;
                float room = PlayerPitch - 36.4f - 4, size = Math.Clamp(room / MathF.Max(_drawMenu!.Font.Measure(who), 1), SizeX, 1);
                while (who.Length > 1 && _drawMenu.Font.Measure(who) * size > room) who = who[..^1];
                TextS(who, x + 36.4f, PlayerTop - 18.6f + (1 - size) * 4, 0, tappable && i != r.LocalSeat ? TitleColour : Pale, tag, size);
                if (tappable) Glow(glows, _dSeat[i], x - 2.4f, PlayerTop + 2.4f, x + PlayerPitch - 4.8f, PlayerTop - PlayerIcon - 2.4f, tag);
            }
        }

        // ---- pieces ----

        private void Hunters(ReplayInfo r, float x, float top, float d, float pitch, int tag)
        {
            IReadOnlyList<MatchPlayer> seats = r.Players;
            for (int i = 0; i < seats.Count && i < 4; i++) Icon(seats[i], x + pitch * i, top, d, i == r.LocalSeat, tag);
        }

        // a network match's opponents: each one's name on a dark plate over the foot of their icon (no tag: the details page
        // has it), cut to the icon's width
        private void Names(ReplayInfo r, float x, float top, float d, float pitch, int tag)
        {
            if (r.Net.Length == 0) return;
            IReadOnlyList<MatchPlayer> seats = r.Players;
            const float size = 0.62f;
            for (int i = 0; i < seats.Count && i < 4; i++)
            {
                if (i == r.LocalSeat || seats[i].Bot) continue;
                string name = (SeatName(r, i, tag: false) ?? seats[i].Nickname ?? "").ToLowerInvariant();
                if (name.Length == 0) continue;
                float room = d + 1;
                while (name.Length > 1 && _drawMenu!.Font.Measure(name) * size > room) name = name[..^1];
                float x0 = x + pitch * i - 1.5f, plate = 8 * size + 1.6f, bottom = top - d - 0.6f;
                Fill(x0, bottom + plate, x0 + d + 3, bottom, 0, 0, 0, 0.72f, tag);
                TextS(name, x + pitch * i + d / 2, bottom + plate - 0.8f, 2, White, tag, size);
            }
        }

        // SELECT HUNTER's round icon (TROOPER / DARK SAMUS too, RecompMenus.SeatIcons.cs); the player who recorded the
        // replay ringed (the main menu's round focus ring)
        private void Icon(MatchPlayer seat, float x, float top, float d, bool ringed, int tag)
        {
            SeatIcon(seat, x, top, d, tag);
            if (ringed) Model(@"main menu\small_highlight", x - d * 0.12f, top + d * 0.12f, d * 1.24f, d * 1.24f, tag, texture: 1);
        }

        // HUNTER LICENSE's gold rank star (gamerscard goldstar: a row of stars, the rank's count; the leftmost one)
        private void Star(float x, float top, float size, int tag)
        {
            List<WidgetTri> row = Still(@"gamerscard\goldstar", idle: true);
            if (row.Count == 0) return;
            float left = row.Min(MinX);
            float right = row.Where(t => MinX(t) <= left + 0.01f).Max(MaxX);
            List<WidgetTri> star = row.Where(t => MinX(t) >= left - 0.01f && MaxX(t) <= right + 0.01f).ToList();
            // its texture is the star on opaque black (the license card is black): the black keyed out, so it sits on a row
            UiTextureCache textures = _drawMenu!.Widgets.Textures;
            int source = star[0].TextureId;
            if (source >= 0)
            {
                UiTexture keyed = textures.GetOrAdd($"replays goldstar keyed {source}", () =>
                {
                    UiTexture t = textures[source];
                    byte[] px = (byte[])t.Rgba.Clone();
                    for (int i = 0; i < px.Length; i += 4)
                    {
                        if (px[i] == 0 && px[i + 1] == 0 && px[i + 2] == 0) px[i + 3] = 0;
                    }
                    return (t.Width, t.Height, px);
                });
                for (int i = 0; i < star.Count; i++)
                {
                    WidgetTri t = star[i];
                    t.TextureId = keyed.Id;
                    star[i] = t;
                }
            }
            Fit(star, x, top, size, size, tag, 1);
        }

        private float ResultWidth(ReplayInfo r) => _drawMenu!.Font.Measure(r.LocalPlace == 0 ? "win" : "loss");

        // RESULTS' medal for the place (gold, silver, bronze, last) and WIN / LOSS; nothing when the place isn't known
        // (recorded before replay format 3, or the match ended early)
        private void Result(ReplayInfo r, float x, float top, float size, int tag)
        {
            if (r.LocalPlace is not int place) return;
            string[] medals = { "medalgold", "medalsilver", "medalbronze", "medallast" };
            Model(@"results\" + medals[Math.Min(place, 3)], x, top, size, size, tag);
            Text(place == 0 ? "win" : "loss", x + size + 2.4f, top - (size - 8) / 2, 0, place == 0 ? TitleColour : White, tag);
        }

        // the lobby's START GAME pill as PLAY (its glow when focused; dimmed when the replay can't play here)
        private void Pill(float x, float top, float w, float h, bool live, int item, Dictionary<int, List<WidgetTri>> glows)
        {
            Model(@"startgame\startgame", x, top, w, h, item, alpha: live ? 1 : 0.4f);
            Glow(glows, item, x, top, x + w, top - h, item);
            var (r, g, b) = Orange;
            Text("play", x + w * 0.33f, top - (h - 8) / 2 - 0.2f, 2, (r, g, b), item, live ? 1 : 0.4f);
        }

        // the arena's picture in an orange frame: the ROM's stage picture, an imported arena's own (thumb.png), or random's
        private void Picture(ReplayInfo r, float x, float top, float w, float h, float frame, int tag)
            => Picture(r.Header?.Settings.Arena, x, top, w, h, frame, tag);

        // (by the arena's name or in-game name: a shared replay's offer carries the in-game one)
        private void Picture(string? arena, float x, float top, float w, float h, float frame, int tag)
        {
            Fill(x - frame, top + frame, x + w + frame, top - h - frame, FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, tag);
            RoomMetadata? meta = string.IsNullOrEmpty(arena) ? null : MatchArenas.Find(arena);
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
                    Console.Error.WriteLine("replays: no arena picture: " + ex.Message);
                }
            }
            Model(stage != null ? stage[..^"_Model.bin".Length] : _stageFolder + "random", x, top, w, h, tag);
        }

        // the setup's option arrows (gameoptions arrows_type: both arrows in one model), the left or the right one
        private void Arrow(bool right, float x, float top, int tag)
        {
            List<WidgetTri> model = Still(@"gameoptions\arrows_type");
            if (model.Count == 0) return;
            float cx = (model.Min(t => MinX(t)) + model.Max(t => MaxX(t))) / 2;
            List<WidgetTri> half = model.Where(t => (MinX(t) + MaxX(t)) / 2 > cx == right).ToList();
            Fit(half, x, top, ArrowW, ArrowH, tag, 1);
        }

        // a box outline: gameoptions box_type (32 x 16, its orange edge 3 texels) stretched as nine pieces
        private void Box(float x0, float top, float x1, float bottom, int tag)
        {
            List<WidgetTri> model = Still(@"gameoptions\box_type");
            if (model.Count > 0) Nine(model, x0, top, x1, bottom, 3, 0.5f, tag);
        }

        // a focused item's glow (the lobby's player_highlight, 64 x 64 with a 16-texel soft edge) round a box, 4 units out
        private void Glow(Dictionary<int, List<WidgetTri>> glows, int item, float x0, float top, float x1, float bottom, int tag)
        {
            if (!glows.TryGetValue(item, out List<WidgetTri>? g) || g.Count == 0) return;
            Nine(g, x0 - 4, top + 4, x1 + 4, bottom - 4, 16, 0.5f, tag);
        }

        // ---- drawing helpers (menu space) ----

        // a ROM model as the game draws it (frame 0), kept; idle: as its widget's Idle look (the HUNTER LICENSE star is
        // only framed by its Idle animation's texture transform)
        private List<WidgetTri> Still(string name, bool idle = false)
        {
            string path = name + "_Model.bin";
            string key = idle ? path + "|idle" : path;
            if (!_stills.TryGetValue(key, out List<WidgetTri>? tris))
            {
                tris = new List<WidgetTri>();
                MenuWidgets widgets = _drawMenu!.Widgets;
                MenuWidget? idleWidget = idle ? _file.Widgets.FirstOrDefault(w => w.ModelPath.Equals(path, StringComparison.OrdinalIgnoreCase)
                    && w.AnimPath != null && w.AnimPath.EndsWith("_Idle_Anim.bin", StringComparison.OrdinalIgnoreCase)) : null;
                widgets.Evaluate(widgets.Instance(path, idleWidget?.AnimPath), 0, loop: true, alpha: 1, tris);
                if (tris.Count == 0)
                {
                    // a model whose look comes from its animation (the main menu's ring is see-through without it): a
                    // widget's, at the frame it shows the most
                    foreach (MenuWidget w in _file.Widgets.Where(w => w.AnimPath != null && w.ModelPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        ModelInstance inst = widgets.Instance(w.ModelPath, w.AnimPath);
                        var frame = new List<WidgetTri>();
                        for (int f = 0; f < MenuWidgets.FrameCount(inst); f++)
                        {
                            frame.Clear();
                            widgets.Evaluate(inst, f, loop: false, alpha: 1, frame);
                            if (frame.Count > tris.Count || frame.Count == tris.Count && frame.Count > 0 && frame.Sum(t => t.A.A) > tris.Sum(t => t.A.A))
                            {
                                tris.Clear();
                                tris.AddRange(frame);
                            }
                        }
                        if (tris.Count > 0) break;
                    }
                }
                _stills[key] = tris;
            }
            return tris;
        }

        // a ROM model fitted into a box (its bounds onto x..x+w, top..top-h); texture >= 0: only that material's
        // triangles (the n-th texture the model uses)
        private void Model(string name, float x, float top, float w, float h, int tag, float alpha = 1, int texture = -1, bool idle = false)
        {
            List<WidgetTri> model = Still(name, idle);
            if (texture >= 0)
            {
                int[] ids = model.Select(t => t.TextureId).Distinct().ToArray();
                if (texture >= ids.Length) return;
                int id = ids[texture];
                model = model.Where(t => t.TextureId == id).ToList();
                // that piece at its full strength (the frame kept is the whole model's brightest)
                float peak = model.Max(t => t.A.A);
                if (peak > 0) alpha /= peak;
            }
            Fit(model, x, top, w, h, tag, alpha);
        }

        private void Fit(List<WidgetTri> model, float x, float top, float w, float h, int tag, float alpha)
        {
            if (model.Count == 0) return;
            float bx0 = model.Min(MinX), bx1 = model.Max(MaxX), by0 = model.Min(MinY), by1 = model.Max(MaxY);
            if (bx1 - bx0 < 1e-4f || by1 - by0 < 1e-4f) return;
            UiVertex Move(UiVertex v)
            {
                v.X = x + (v.X - bx0) / (bx1 - bx0) * w;
                v.Y = top - (by1 - v.Y) / (by1 - by0) * h;
                v.A *= alpha;
                return v;
            }
            foreach (WidgetTri t in model)
            {
                WidgetTri c = t;
                c.A = Move(t.A); c.B = Move(t.B); c.C = Move(t.C);
                c.Item = tag;
                _out.Add(c);
            }
        }

        // a texture (the model's, over the UVs it uses) as nine pieces: corners `border` texels at `unitsPerTexel`, the
        // edges and middle stretched; colour and alpha from the model's first vertex (the glow's pulse)
        private void Nine(List<WidgetTri> model, float x0, float top, float x1, float bottom, float border, float unitsPerTexel, int tag)
        {
            WidgetTri first = model[0];
            UiTexture tex = _drawMenu!.Widgets.Textures[first.TextureId];
            float u0 = model.Min(t => MathF.Min(t.A.U, MathF.Min(t.B.U, t.C.U))), u1 = model.Max(t => MathF.Max(t.A.U, MathF.Max(t.B.U, t.C.U)));
            float v0 = model.Min(t => MathF.Min(t.A.V, MathF.Min(t.B.V, t.C.V))), v1 = model.Max(t => MathF.Max(t.A.V, MathF.Max(t.B.V, t.C.V)));
            float bu = border / tex.Width, bv = border / tex.Height, b = border * unitsPerTexel;
            float[] xs = { x0, x0 + b, x1 - b, x1 }, ys = { top, top - b, bottom + b, bottom };
            float[] us = { u0, u0 + bu, u1 - bu, u1 }, vs = { v0, v0 + bv, v1 - bv, v1 };
            UiVertex c = first.A;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if (xs[i + 1] - xs[i] <= 0 || ys[j] - ys[j + 1] <= 0) continue;
                    Quad(first.TextureId, xs[i], ys[j], xs[i + 1], ys[j + 1], us[i], vs[j], us[i + 1], vs[j + 1], c.R, c.G, c.B, c.A, tag,
                        first.WrapS, first.WrapT);
                }
            }
        }

        private void Fill(float x0, float top, float x1, float bottom, float r, float g, float b, float a, int tag)
            => Quad(-1, x0, top, x1, bottom, 0, 0, 0, 0, r, g, b, a, tag);

        private void Quad(int texture, float x0, float top, float x1, float bottom, float u0, float v0, float u1, float v1,
            float r, float g, float b, float a, int tag, UiWrap wrapS = UiWrap.Clamp, UiWrap wrapT = UiWrap.Clamp)
        {
            var p0 = new UiVertex(x0, top, u0, v0, r, g, b, a);
            var p1 = new UiVertex(x1, top, u1, v0, r, g, b, a);
            var p2 = new UiVertex(x1, bottom, u1, v1, r, g, b, a);
            var p3 = new UiVertex(x0, bottom, u0, v1, r, g, b, a);
            _out.Add(new WidgetTri { A = p0, B = p1, C = p2, TextureId = texture, Item = tag, WrapS = wrapS, WrapT = wrapT });
            _out.Add(new WidgetTri { A = p0, B = p2, C = p3, TextureId = texture, Item = tag, WrapS = wrapS, WrapT = wrapT });
        }

        // the menus' font, a line whose cells' tops are at `top` (menu space); align 0 left, 1 right, 2 centred
        private void Text(string text, float x, float top, int align, (float R, float G, float B) colour, int tag, float alpha = 1)
        {
            int start = _out.Count;
            _drawMenu!.Font.Emit(text, x, top + 192 - MenuFont.Cell, align, 0, MenuFont.Cell, colour.R, colour.G, colour.B, alpha, 0, _out);
            for (int t = start; t < _out.Count; t++)
            {
                WidgetTri tri = _out[t];
                tri.Item = tag;
                _out[t] = tri;
            }
        }

        private static float MinX(WidgetTri t) => MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X));
        private static float MaxX(WidgetTri t) => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
        private static float MinY(WidgetTri t) => MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y));
        private static float MaxY(WidgetTri t) => MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
    }
}
