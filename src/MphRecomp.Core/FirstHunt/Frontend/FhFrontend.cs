using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.FirstHunt
{
    [Flags]
    public enum FhPad
    {
        None = 0,
        Up = 1,
        Down = 2,
        Left = 4,
        Right = 8,
        Confirm = 16,
        Back = 32
    }

    // a training game picked on the Training page (0x2044870: "Game A/B/C" -> mode and level), or a deathmatch started
    // from the lobby pages (net: "host" / "client", played over LanSession.Current)
    public readonly record struct FhGameStart(FhMode Mode, int Level, string Room, string? Net = null);

    // the result a finished training game brings back to the front end (the results page, 0x203f898)
    public sealed record FhTrainingResult(FhMode Mode, bool Failed, int Score, int DsFrames, int Kills, int Rank, bool MovieUnlocked)
    {
        // one line of text, for the game screen to hand back to the menus (<saves>/firsthunt_last.txt)
        public string ToLine() => $"{(int)Mode} {(Failed ? 1 : 0)} {Score} {DsFrames} {Kills} {Rank} {(MovieUnlocked ? 1 : 0)}";

        public static FhTrainingResult? Parse(string line)
        {
            string[] p = line.Trim().Split(' ');
            if (p.Length != 7) return null;
            int[] v = new int[7];
            for (int i = 0; i < 7; i++)
            {
                if (!Int32.TryParse(p[i], out v[i])) return null;
            }
            return new FhTrainingResult((FhMode)v[0], v[1] != 0, v[2], v[3], v[4], v[5], v[6] != 0);
        }
    }

    // First Hunt's front end (docs/FIRST_HUNT_PORT.md P4; fh_research p4_frontend.md, p4_frontend_draw.md): the cart's
    // pages from its own data (the screen table, menusTD models, menu_state.bin, the popup table) run the way its handlers
    // do: an open state, then highlight / un-highlight / select states as the stylus or the pad moves, the select's
    // top-screen state (_sel_D), then the target page's open. Drawn in menu space (MenuLayout's: top screen y 0..192,
    // touch screen y -192..0); the host places that on its screen and passes touches in bottom-screen pixels.
    public sealed class FhFrontend
    {
        private readonly Dictionary<string, FhMenuState> _states;
        private readonly List<FhMenuScreen> _screens;
        private readonly Dictionary<int, FhMenuModel> _pages = new();
        private readonly Dictionary<string, FhMenuModel?> _backdrops = new();
        private readonly FhMenuModel _frame;
        private readonly FhMenuModel _bottom;
        private readonly FhMenuFont _font;
        private readonly HashSet<string> _hidden = new();
        private readonly Dictionary<string, FhText> _texts = new();
        private readonly HashSet<string> _popupHidden = new();
        private readonly Dictionary<string, FhText> _popupTexts = new();
        private static readonly HashSet<string> _frameHidden = new() { "cursor" };
        // the title draws no header bar ends (fh_ref title_idle: every other page and its popups do)
        private static readonly HashSet<string> _frameHiddenTitle = new() { "cursor", "headerBar1", "headerBar5" };

        public FirstHuntSave Save { get; }
        public UiTextureCache Textures { get; } = new();

        // host hooks: a MENUSFX sound, the music (BGM track, loop; -1 = stop), a game to start, a movie (P6: the host
        // plays it and then shows the title, as the cart's movie state goes back to the front end), leaving First Hunt
        public Action<int>? Sound { get; set; }
        public Action<int, bool>? Music { get; set; }
        public Action<FhGameStart>? StartGame { get; set; }
        public Action<Movies.FhMovie>? PlayMovie { get; set; }
        public Action? Exit { get; set; }

        private enum Phase { Opening, Idle, Selecting, Leaving }

        private int _screen = FhMenuData.Title;
        private Phase _phase = Phase.Opening;
        private FhMenuState? _running;
        private int _highlight = -1; // item index
        private int _pressed = -1;
        // the frame's side lightning: orange (its loop's frames 0-29), blue (30-59) from the press of a button that leaves
        // the page until the next page opens (fh_ref: idle, opening and toggles such as config's types stay orange)
        private bool _boltBlue = false;
        private bool _touchDown = false;
        private bool _litByPress = false; // the lit button is lit by its press (not the pad / the stylus over it)
        private bool _touchPressed = false; // this touch pressed a button (the rest of it then lights nothing)
        private FhTrainingResult? _result;
        private int _music = -1;

        // multiplayer (P5, FhLobby.cs): the host's arena (0x209ddd0) and the one its top screen shows (a lit hex shows its
        // own), the players its slots show; the client's games (LanSession beacons), the first one listed, the rows the
        // page shows, the game lit, a join in progress / done; the deathmatch's results
        private int _arena = 0;
        private int _shownArena = 0;
        private int _seatsShown = 1;
        private List<LanHostInfo> _games = new();
        private int _gamesScroll = 0;
        private int _gamesShown = -1;
        private int _litGame = -1;
        private Task<bool>? _join;
        private LanSession? _joinLan;
        private LanHostInfo? _joinGame;
        private bool _joined = false;
        private int _lobbyTicks = 0;
        private FhMatchResult? _match;
        private bool _matchRecorded = false;

        // the popup (screen 12, drawn over the page it came from) and its table entry (0x207ab04, 5 words: four lines
        // and the buttons: 1 yes/no, 2 ok, 4 cancel, 0 none)
        private const int PopupTable = 0x207ab04;
        public const int PopupMovie = 0, PopupErase = 11, PopupErased = 12;
        private int _popupFrom = -1;
        private int _popupButtons = 0;
        private Action? _popupYes;
        private Action? _popupDone;

        // 0x2084b40: the 26 keys in chara mode (0x2041104: key letter - 'a' indexes it; 0x209df04 = chara mode)
        private static char[]? _charaKeys;

        private static char CharaKey(int index)
        {
            if (_charaKeys == null)
            {
                var keys = new char[26];
                for (int i = 0; i < 26; i += 4)
                {
                    int word = FirstHuntRules.ReadArm9Int(0x2084b40 + i) ?? 0x20202020;
                    for (int j = 0; j < 4 && i + j < 26; j++)
                    {
                        byte ch = (byte)(word >> (8 * j));
                        keys[i + j] = ch >= 0x20 && ch < 0x7f ? (char)ch : ' ';
                    }
                }
                _charaKeys = keys;
            }
            return _charaKeys[index];
        }

        // the char an edit-name key ('A'..'Z') types and shows in the current mode (upper / lower / chara)
        private char EditKey(char key) => _editChara ? CharaKey(key - 'A') : _editUpper ? key : char.ToLowerInvariant(key);

        // page state: the records table shown, the name being edited, the control type being picked
        private FirstHuntSave.Table _recordsTable = FirstHuntSave.Table.Regulator;
        private bool _recordsMultiplayer = false;
        private string _editName = "";
        private bool _editUpper = true;
        private bool _editChara = false;
        private int _pickedControl = 0;
        // the configuration page's top picture: control type (0x209de6c = type * 2, the first of its image pair), set on
        // open, on the type lit (0x203d694 / 0x203d954) and on the type picked
        private int _configImage = 0;

        // the frame model's own loop (0x2047af0: frames 0-29 idle, 30-59 while a button is lit or a transition runs) and
        // the stylus trail (0x2049b84: 16 points on the hex grid that fade by 6 a frame)
        private int _ticks = 0;
        private readonly (Vector2 At, int Life)[] _trail = new (Vector2, int)[16];
        private int _trailNext = 0;
        private bool _stylus = false;
        private Vector2 _stylusAt;
        // the lit button's sparks (0x20495c8, called by every page's update): 7 slots, the lit item's held at full and the
        // rest fading by 8 a step, so a spark that loses the light fades out over ~32 steps while the new one is lit; a
        // new item takes the faintest slot (fh_ref mainmenu_open: none carry over to a new page)
        private readonly (int Screen, int Item, int Alpha)[] _sparks = new (int, int, int)[7];

        public int Screen => _screen;
        public string ScreenName => _screens[_screen].Name;
        public string PageName => _screens[_screen == FhMenuData.Popup && _popupFrom >= 0 ? _popupFrom : _screen].Name; // (under a popup)
        public int Highlight => _highlight;
        public bool Busy => _phase != Phase.Idle;
        public FhTrainingResult? Result => _result;
        public FhMatchResult? MatchResult => _match;
        public int Arena => _arena;
        public IReadOnlyList<LanHostInfo> Games => _games;
        public bool Joined => _joined;
        public IReadOnlyDictionary<string, FhText> Texts => _screen == FhMenuData.Popup ? _popupTexts : _texts;
        public IReadOnlyDictionary<string, FhText> PageTexts => _texts; // (the page's, under a popup too)

        // the front end of the imported game (FirstHuntGame.Register first); save: the cart-format save file
        public FhFrontend(string? savePath)
        {
            _states = FhMenuData.ReadStates(File.ReadAllBytes(Paths.Combine(Paths.FhFileSystem, "menusTD/menu_state.bin")));
            _screens = FhMenuData.ReadScreens();
            _frame = new FhMenuModel("frame") { Tag = TagFrame };
            _bottom = new FhMenuModel("bottom_bg", animated: false) { Flat = true, Tag = TagBottom };
            _font = new FhMenuFont();
            Save = FirstHuntSave.Load(savePath);
        }

        // start at the title, or at the results of a game just played (a training game's, or a deathmatch's)
        public void Start(FhTrainingResult? result = null, FhMatchResult? match = null)
        {
            _result = result;
            _match = match;
            _matchRecorded = false;
            Open(match != null ? FhMenuData.MultiHiScore : result != null ? FhMenuData.TrainingHiScore : FhMenuData.Title);
        }

        // a page directly (the tools' renders)
        public void Show(int screen, FhTrainingResult? result = null, FhMatchResult? match = null)
        {
            _result = result ?? _result;
            if (match != null)
            {
                _match = match;
                _matchRecorded = false;
            }
            Open(screen);
        }

        private string PlayerName => Save.Name.Length > 0 ? Save.Name : "Player";

        // FhMenuModel tags (WidgetTri.Item): pages are their screen + 1
        public const int TagFrame = 0x40, TagBottom = 0x41, TagBackdrop = 0x42;
        // the stylus trail's and the lit button's sparks' triangles (WidgetTri.Item = tag << 12), placed by the code
        public const int TagTrail = 0x43, TagSparks = 0x44;

        // a triangle's model and node, by its WidgetTri.Item
        public (string Model, string Node) NodeOf(int item)
        {
            int tag = item >> 12, node = (item & 0xfff) - 1;
            FhMenuModel? model = tag == TagFrame ? _frame : tag == TagBottom ? _bottom
                : tag == TagBackdrop ? _backdrops.Values.FirstOrDefault(m => m != null)
                : tag >= 1 && tag <= _screens.Count ? Page(tag - 1) : null;
            if (model == null || node < 0 || node >= model.Model.Nodes.Count) return ("?", "?");
            return (model.Name, model.Model.Nodes[node].Name);
        }

        // the animation frame of the node that drew a triangle (WidgetTri.Item), -1 if none
        public int FrameOf(int item)
        {
            int tag = item >> 12, node = (item & 0xfff) - 1;
            FhMenuModel? model = tag == TagFrame ? _frame : tag >= 1 && tag <= _screens.Count ? Page(tag - 1) : null;
            return model == null || node < 0 || node >= model.Model.Nodes.Count ? -1 : model.FrameOf(model.Model.Nodes[node].Name);
        }

        private FhMenuModel Page(int screen)
        {
            if (!_pages.TryGetValue(screen, out FhMenuModel? page))
            {
                page = new FhMenuModel(_screens[screen].Name) { Tag = screen + 1 };
                _pages[screen] = page;
            }
            return page;
        }

        private FhMenuModel? Backdrop(string name)
        {
            if (name.Length == 0) return null;
            if (!_backdrops.TryGetValue(name, out FhMenuModel? model))
            {
                model = File.Exists(Paths.Combine(Paths.FhFileSystem, $"menusTD/{name}_bg_Model.bin")) ? new FhMenuModel(name + "_bg", animated: false) { Flat = true, Tag = TagBackdrop } : null;
                _backdrops[name] = model;
            }
            return model;
        }

        // another page's backdrop (`name`: its Backdrop, e.g. "main_menu"), for a layout's picture
        public void EvaluateBackdrop(string name, List<WidgetTri> output) => Backdrop(name)?.Evaluate(Textures, output);

        private FhMenuState? State(string name) => _states.TryGetValue(name, out FhMenuState? s) ? s : null;

        private void Open(int screen)
        {
            _screen = screen;
            FhMenuScreen sc = _screens[screen];
            FhMenuModel page = Page(screen);
            Array.Clear(_sparks);
            _highlight = -1;
            _pressed = -1;
            _boltBlue = false;
            _hidden.Clear();
            _popupFrom = -1;
            string open = sc.Name + "_open";
            bool clear = true;
            if (screen != FhMenuData.HostStart && screen != FhMenuData.ClientStart)
            {
                FhLobby.End(); // (the lobby pages' LAN session: leaving them ends it)
            }
            if (screen == FhMenuData.TrainingHiScore && _result != null)
            {
                // training_hi_score_open_<mode>_<rank> (mode: Regulator 1, Survivor 2, Morph Ball 3; rank 0 = not placed)
                // training_hi_score_open_<mode>, then its table rows: _open_<mode>_<rank> (0 = not placed, 1-5 = the row)
                int m = ResultMode;
                page.Play(State($"{open}_{m}"), clear: true);
                // the header's second line names the mode (fh_ref trainresult_*: REGULATOR / SURVIVOR / MORPH BALL); its
                // nodes ship enabled or not at random ('regulator' off), so only the run's own is on
                string[] modes = { "regulator", "survivor", "morphballCourse" };
                foreach (Node node in page.Model.Nodes)
                {
                    int k = Array.IndexOf(modes, node.Name);
                    if (k >= 0) node.Enabled = k + 1 == m;
                }
                open = $"{open}_{m}_{_result.Rank + 1}";
                clear = false;
                // the rank word that shows is the run's own (FIRST..FIFTH, none when not placed); PLAY MOVIE only
                // when this run unlocked the movie
                string[] ranks = { "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH" };
                for (int r = 0; r < ranks.Length; r++)
                {
                    if (r != _result.Rank) _hidden.Add(ranks[r]);
                }
                if (!_result.MovieUnlocked)
                {
                    Hide(screen, "training_hi_score_movie");
                    _hidden.UnionWith(new[] { "PLAY_MOVIE", "hex_frPlayMovieL", "hex_frPlayMovieR" });
                }
            }
            if (screen == FhMenuData.Configuration)
            {
                _pickedControl = Save.ControlType;
                _configImage = Save.ControlType;
                open = $"{open}_{Save.ControlType + 1}";
            }
            if (screen == FhMenuData.Title && !Save.MovieAvailable)
            {
                _hidden.Add("hiddenHex"); // the movie's hex shows once all three games have a record (0x2045600)
            }
            if (screen == FhMenuData.EditName)
            {
                _editName = Save.Name;
                _editUpper = true;
                _editChara = false;
            }
            if (screen == FhMenuData.Records)
            {
                _recordsTable = FirstHuntSave.Table.Regulator;
                _recordsMultiplayer = false;
            }
            if (screen == FhMenuData.MultiHiScore && _match != null)
            {
                // multi_hi_score_open_<local slot + 1> (0x2088ea4); placings and names only for the match's players, a
                // player's "out" over the table only for the seats nobody had (0x2042d88)
                open = $"{open}_{Math.Clamp(_match.LocalSlot, 0, 3) + 1}";
                string[] places = { "FIRST", "SECOND", "THIRD", "FOURTH" };
                for (int i = _match.Players; i < 4; i++)
                {
                    _hidden.Add(places[i]);
                    _hidden.Add($"PLAYERNAME_{i + 1}");
                }
                for (int i = 2; i <= Math.Min(_match.Players, 4); i++) _hidden.Add($"out{i}");
                if (!_matchRecorded)
                {
                    // the local player's totals (0x20431f8), then the save (popup 2 "Saving" on the cart)
                    _matchRecorded = true;
                    int slot = _match.LocalSlot;
                    Save.RecordMultiplayer(_match.LocalRank, slot < _match.Players ? _match.Kills[slot] : 0,
                        slot < _match.Players ? _match.Deaths[slot] : 0);
                    Save.Save();
                }
            }
            if (screen == FhMenuData.HostStart)
            {
                // hosting starts with the page (0x20432d8; the DS's wireless here is our LAN session, FhLobby)
                if (FhLobby.Lan is not { IsHost: true })
                {
                    _arena = 0;
                    FhLobby.Host(_arena, PlayerName);
                }
                _seatsShown = 1;
                _lobbyTicks = 0;
            }
            if (screen == FhMenuData.ClientStart)
            {
                if (FhLobby.Lan is not { IsHost: false } || _joined)
                {
                    FhLobby.Browse();
                }
                _joined = false;
                _join = null;
                _joinLan = null;
                _joinGame = null;
                _games = new List<LanHostInfo>();
                _gamesScroll = 0;
                _gamesShown = -1;
                _litGame = -1;
                _lobbyTicks = 0;
            }
            _running = State(open);
            page.Play(_running, clear);
            if (screen == FhMenuData.HostStart)
            {
                // the open shows arena 1's stage; another arena (back from a popup) moves to it
                _shownArena = 0;
                if (_arena != 0)
                {
                    page.Play(State($"host_start_{_arena + 1}_sel_1"));
                    _shownArena = _arena;
                }
            }
            _phase = Phase.Opening;
            RefreshTexts();
            if (sc.OpenSound >= 0) Sound?.Invoke(sc.OpenSound);
            // menus 0, hi-score pages 2 (fh_research p4_sound.md, p5_multiplayer.md)
            int music = screen == FhMenuData.TrainingHiScore || screen == FhMenuData.MultiHiScore ? 2 : 0;
            if (music != _music)
            {
                _music = music;
                Music?.Invoke(music, true);
            }
        }

        // a popup from the cart's table over the current page; yes: its YES (yes/no popups), done: any other button
        private void OpenPopup(int type, Action? yes, Action? done)
        {
            int at = PopupTable + type * 20;
            int from = _screen == FhMenuData.Popup ? _popupFrom : _screen;
            _screen = FhMenuData.Popup;
            _popupFrom = from;
            _popupButtons = FirstHuntRules.ReadArm9Int(at + 16) ?? 2;
            _popupYes = yes;
            _popupDone = done;
            _highlight = -1;
            _pressed = -1;
            _popupHidden.Clear();
            _popupTexts.Clear();
            IReadOnlyList<FhMenuItem> items = _screens[FhMenuData.Popup].Items;
            int line = 0;
            foreach (FhMenuItem item in items)
            {
                if (item.IsButton || (item.Flags & 0x8000) == 0) continue;
                string text = line < 4 ? FhMenuData.Str(FirstHuntRules.ReadArm9Int(at + line * 4) ?? 0, 0x20) : "";
                _popupTexts[item.Node] = new FhText(text, item.Flags, (int)item.TextParam);
                line++;
            }
            foreach (FhMenuItem item in items)
            {
                if (item.IsButton && !PopupButton(item)) _popupHidden.Add(item.Node);
            }
            _running = State("popup_open");
            Page(FhMenuData.Popup).Play(_running, clear: true);
            _phase = Phase.Opening;
        }

        // back to the page under a popup as it was (the lobby pages' popups: their page goes on)
        private void BackToPage()
        {
            if (_screen == FhMenuData.Popup)
            {
                _screen = _popupFrom >= 0 ? _popupFrom : FhMenuData.Title;
                _popupFrom = -1;
            }
            _highlight = -1;
            _pressed = -1;
            _boltBlue = false;
            _running = null;
            _phase = Phase.Idle;
            RefreshTexts();
        }

        private bool PopupButton(FhMenuItem item) => item.StateBase switch
        {
            "popup_yes" or "popup_no" => (_popupButtons & 1) != 0,
            "popup_ok" => (_popupButtons & 2) != 0,
            "popup_cancel" => (_popupButtons & 4) != 0,
            _ => false
        };

        private int ResultMode => _result?.Mode switch { FhMode.Regulator => 1, FhMode.Survivor => 2, _ => 3 };

        private void Hide(int screen, string stateBase)
        {
            foreach (FhMenuItem item in _screens[screen].Items)
            {
                if (item.StateBase == stateBase && item.Node.Length > 0) _hidden.Add(item.Node);
            }
        }

        // client_start's game rows (host1-3: boxes, not buttons): 0-2, -1 = not one
        private int GameRow(FhMenuItem item) => _screen == FhMenuData.ClientStart && item.Node.Length == 5 && item.Node.StartsWith("host")
            && item.Node[4] >= '1' && item.Node[4] <= '3' ? item.Node[4] - '1' : -1;

        private bool Usable(FhMenuItem item) => (item.IsButton || GameRow(item) >= 0 && _gamesScroll + GameRow(item) < _games.Count && _join == null)
            && (item.Flags & 2) == 0
            && !(_screen == FhMenuData.Popup ? _popupHidden : _hidden).Contains(item.Node)
            && !(_screen == FhMenuData.Title && item.StateBase == "title_movie" && !Save.MovieAvailable)
            && !(_screen == FhMenuData.Popup && !PopupButton(item));

        private List<int> Buttons()
        {
            var list = new List<int>();
            IReadOnlyList<FhMenuItem> items = _screens[_screen].Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (Usable(items[i])) list.Add(i);
            }
            list.Sort((a, b) => items[a].Top != items[b].Top ? items[a].Top.CompareTo(items[b].Top) : items[a].Left.CompareTo(items[b].Left));
            return list;
        }

        // the text items: their labels, with the page's fills (0x2045588 name, 0x203f898 results, 0x203fe7c records)
        private void RefreshTexts()
        {
            if (_screen == FhMenuData.Popup) return;
            _texts.Clear();
            foreach (FhMenuItem item in _screens[_screen].Items)
            {
                if (!item.IsButton && (item.Flags & 0x8000) != 0 && item.Node.Length > 0)
                {
                    _texts[item.Node] = new FhText(item.Label, item.Flags, (int)item.TextParam);
                }
            }
            void Set(string node, string text)
            {
                if (_texts.TryGetValue(node, out FhText t)) _texts[node] = t with { Text = text };
            }
            void Name(string name)
            {
                for (int i = 0; i < 10; i++) Set($"char{i + 1}", i < name.Length ? name[i].ToString() : "");
            }
            void Rows(FirstHuntSave.Table table)
            {
                string[] ranks = { "first", "second", "third", "fourth", "fifth" };
                for (int r = 0; r < FirstHuntSave.Rows; r++)
                {
                    (uint score, uint second) = Save.Row(table, r);
                    Set(ranks[r] + "1", score.ToString());
                    Set(ranks[r] + "2", table == FirstHuntSave.Table.Survivor ? "" : Time(table, second));
                    Set($"kill_count_{r + 1}", table == FirstHuntSave.Table.Survivor ? second.ToString() : "");
                }
            }
            switch (_screen)
            {
            case FhMenuData.Options:
                Name(Save.Name);
                Set("op_name", Save.Name);
                break;
            case FhMenuData.EditName:
                Name(_editName);
                for (char c = 'A'; c <= 'Z'; c++) Set($"hex_{c}", EditKey(c).ToString());
                break;
            case FhMenuData.TrainingHiScore when _result != null:
            {
                FirstHuntSave.Table table = _result.Mode switch
                {
                    FhMode.Morphball => FirstHuntSave.Table.Morphball,
                    FhMode.Regulator => FirstHuntSave.Table.Regulator,
                    _ => FirstHuntSave.Table.Survivor
                };
                Set("ScoreFont", table == FirstHuntSave.Table.Survivor ? $"{_result.Score}pts {_result.Kills}kills"
                    : $"{_result.Score}pts {Time(table, (uint)_result.DsFrames)}");
                Rows(table);
                break;
            }
            case FhMenuData.Records:
                if (_recordsMultiplayer)
                {
                    // multiplayer totals (save +0xc0, written by the results page)
                    Set("wins", Save.GamesWon.ToString());
                    Set("kills", Save.TotalKills.ToString());
                    Set("deaths", Save.TotalDeaths.ToString());
                }
                else
                {
                    Rows(_recordsTable);
                }
                for (int i = 1; i <= 5; i++) Set($"player{i}", "");
                break;
            case FhMenuData.HostStart:
            {
                // the host's slot, then the joiners' (0x20427dc: an empty slot says NO ENTRY)
                List<string> seats = FhLobby.Lan is { IsHost: true } lan ? FhLobby.HostSeats(lan, PlayerName) : new List<string> { PlayerName };
                Set("hostPlayerName", PlayerName);
                for (int i = 1; i < FhLobby.MaxPlayers; i++)
                {
                    Set($"client{i}PlayerName", i < seats.Count ? seats[i] : "");
                    Set($"client{i}StatusText", i < seats.Count ? "" : "NO ENTRY");
                }
                break;
            }
            case FhMenuData.ClientStart:
            {
                string[] rows = { "serverOne", "serverTwo", "serverThree", "serverFour" };
                for (int r = 0; r < rows.Length; r++)
                {
                    int g = _gamesScroll + r;
                    Set(rows[r], r < 3 && g < _games.Count ? _games[g].Name : "");
                }
                // the open state leaves the scroll arrows and the first row's game icons up; the cart shows the arrows only
                // with more games than rows and the icons only by a game (fh_ref clientstart_*: an empty list, neither)
                bool scroll = _games.Count > 3, any = _games.Count > _gamesScroll;
                // (ButtonUp / ButtonDown, the items' nodes, too: a hidden button takes no touch or pad highlight)
                foreach (string n in new[] { "ButtonUp", "upArrow", "hex_frUpL", "hex_frUpR", "ButtonDown", "downArrow", "hex_frDownL", "hex_frDownR" }) Show(n, scroll);
                Show("gameNotRunning1", any);
                Show("playerIcon1", any);
                Show("gameRunning1", false); // (a listed LAN game is one still in its lobby)
                // the top screen: the lit game's lobby (or the one being joined): its host, its arena, the players in it
                LanHostInfo? game = _joinGame ?? (_litGame >= 0 && _gamesScroll + _litGame < _games.Count ? _games[_gamesScroll + _litGame] : null);
                List<string> names = new();
                if (_joined && FhLobby.Lan is { IsHost: false } lan && (lan.LobbySettings ?? lan.Settings) is MphRecomp.Multiplayer.MatchSettings joined)
                {
                    names = joined.Players.Select(p => p.Nickname ?? "").ToList();
                }
                int players = names.Count > 0 ? names.Count : (game?.Seats ?? 0) + 1;
                int you = _joined && FhLobby.Lan?.Settings is MphRecomp.Multiplayer.MatchSettings st ? st.LocalSeat : players - 1;
                Set("hostPlayerName", game?.Name ?? "");
                Set("hostStatusText", "HOST");
                Set("stageName", game != null ? FhLobby.ArenaName(FhLobby.ArenaIndex(game.Arena)) : "");
                Set("youPlayerName", PlayerName);
                Set("youPlayerNum", (you + 1).ToString());
                // the other joiners' cards
                int card = 1;
                for (int i = 1; i < players && card <= 2; i++)
                {
                    if (i == you) continue;
                    Set($"client{card}PlayerName", i < names.Count ? names[i] : "");
                    Set($"client{card}PlayerNum", (i + 1).ToString());
                    Set($"client{card}StatusText", "");
                    card++;
                }
                for (; card <= 2; card++)
                {
                    Set($"client{card}PlayerName", "");
                    Set($"client{card}StatusText", "NO ENTRY");
                }
                break;
            }
            case FhMenuData.MultiHiScore when _match != null:
            {
                FhMatchResult m = _match;
                // placings: the players by rank (ties in seat order), names; the table by seat (0x2042d88)
                List<int> order = Enumerable.Range(0, m.Players).OrderBy(i => m.Ranks[i]).ThenBy(i => i).ToList();
                string[] fonts = { "FirstFont", "SecondFont", "ThirdFont", "FourthFont" };
                for (int r = 0; r < fonts.Length; r++) Set(fonts[r], r < order.Count ? m.Names[order[r]] : "");
                for (int i = 0; i < 4; i++)
                {
                    bool active = i < m.Players;
                    Set($"player{i}", active ? m.Names[i] : "");
                    Set($"p{i + 1}KillTotal", active ? m.Kills[i].ToString() : "");
                    Set($"deaths{i + 1}", active ? m.Deaths[i].ToString() : "");
                    for (int j = 0; j < 4; j++)
                    {
                        if (j != i) Set($"p{i + 1}_{j + 1}", active && j < m.Players ? m.KillTable[i, j].ToString() : "");
                    }
                }
                break;
            }
            }
        }

        // 0x2032780: a time in 30 fps frames, Regulator m:ss ("%.d:%.2d"), Morph Ball seconds:hundredths ("%.2d:%.2d")
        public static string Time(FirstHuntSave.Table table, uint frames)
        {
            return table == FirstHuntSave.Table.Regulator ? $"{frames / 1800}:{frames / 30 % 60:00}"
                : $"{frames / 30:00}:{frames % 30 * 1000 / 300:00}";
        }

        // one 60 Hz frame
        // the title's attract (0x2045a78): 40 s on the title with no input plays the opening movie, then the title again
        public const int AttractTicks = 40 * 60;
        private int _idleTicks = 0;

        public void Tick()
        {
            StepLobby();
            if (_screen == FhMenuData.Title && _phase == Phase.Idle && PlayMovie != null)
            {
                if (++_idleTicks >= AttractTicks)
                {
                    _idleTicks = 0;
                    PlayMovie(Movies.FhMovie.Opening);
                }
            }
            else
            {
                _idleTicks = 0;
            }
            FhMenuModel page = Page(_screen);
            page.Tick();
            if (++_ticks % 2 == 0)
            {
                StepFrame();
            }
            else
            {
                StepTrail(-1, default);
            }
            if (_running != null && !page.Finished(_running)) return;
            switch (_phase)
            {
            case Phase.Opening:
                _phase = Phase.Idle;
                break;
            case Phase.Selecting:
            {
                // the cart switches page the frame _sel ends (BizHawk, fh_mainmenu/training/options: current screen set at
                // the press, previous page at press + 51, input back at + 102 = the target's open); _sel_D starts then
                // and doesn't hold the switch
                FhMenuItem item = _screens[_screen].Items[_pressed];
                FhMenuState? leave = item.IsButton ? State(item.StateBase + "_sel_D") ?? State(item.StateBase + "_sel" + Variant + "_D") : null;
                if (leave != null) page.Play(leave);
                goto case Phase.Leaving;
            }
            case Phase.Leaving:
                _phase = Phase.Idle;
                _running = null;
                Act(_screens[_screen].Items[_pressed]);
                break;
            }
        }

        // the frame's loop and the stylus trail, at 30 fps
        private void StepFrame()
        {
            int f = _frame.DefaultFrame;
            // the bolts are blue while a button is lit, by the pad or the stylus (fh_ref_tools p8 pad_main_*, p7), and through
            // a press that leaves the page; a pressed button that stays lit on its page leaves them white (fh_ref config_TL,
            // records_tab_*)
            if (_boltBlue || _highlight >= 0 && !_litByPress)
            {
                f = f < 30 ? f + 30 : f;
                f = f + 1 >= 60 ? 30 : f + 1;
            }
            else
            {
                f = (f % 30 + 1) % 30;
            }
            _frame.DefaultFrame = f;
            // the stylus on the hex grid (0x2049b84): cells 8 wide, rows 14 high, odd half-rows (y % 14 >= 9) shifted half
            // a cell toward the stylus; the held cell's point stays at full, a new point only when the cell changes, the
            // rest fade by 6. The cart runs it every 60 Hz frame (0x20472d8) but sees the stylus down only every other one
            // (fh_ref_tools p7 drag_main_*: the head moves in pairs of frames; p10 tap_*: a lifted cell gone in ~41 frames)
            int cell = -1;
            Vector2 at = default;
            if (_stylus)
            {
                // (floored: the one-screen layout passes touches beyond the touch screen's 256 x 192, the grid going on)
                int x = (int)MathF.Floor(_stylusAt.X), y = (int)MathF.Floor(-_stylusAt.Y);
                int cx = (int)MathF.Floor(x / 8f) * 8, cy = (int)MathF.Floor(y / 14f) * 14;
                if (y - cy >= 9)
                {
                    cx += x - cx >= 4 ? 4 : -4;
                    cy += 7;
                }
                at = new Vector2(cx, -cy);
                for (int i = 0; i < _trail.Length; i++)
                {
                    if (_trail[i].Life > 0 && _trail[i].At == at) cell = i;
                }
                if (cell < 0)
                {
                    cell = _trailNext;
                    _trailNext = (_trailNext + 1) % _trail.Length;
                }
            }
            StepTrail(cell, at);
            StepSparks();
        }

        private void StepTrail(int cell, Vector2 at)
        {
            for (int i = 0; i < _trail.Length; i++)
            {
                _trail[i] = i == cell ? (at, 0xff) : (_trail[i].At, Math.Max(0, _trail[i].Life - 6));
            }
        }

        private void StepSparks()
        {
            // no new item lights while the page changes (0x209dea0 != 0x209de98: fh_ref mainmenu_hl_training, pressed
            // TRAINING has none, while a picked control type, which stays on the page, keeps its own, config_TL); circle
            // buttons (flag 1: the title's Movie hex, Edit Name's keys) have none
            int lit = _highlight >= 0 && (_phase == Phase.Idle || _phase == Phase.Selecting && !_boltBlue)
                && !_screens[_screen].Items[_highlight].Circle ? _highlight : -1;
            int slot = -1, faintest = 0;
            for (int i = 0; i < _sparks.Length; i++)
            {
                if (lit >= 0 && _sparks[i].Alpha > 0 && _sparks[i].Screen == _screen && _sparks[i].Item == lit) slot = i;
                else _sparks[i].Alpha = Math.Max(0, _sparks[i].Alpha - 8);
                if (_sparks[i].Alpha < _sparks[faintest].Alpha) faintest = i;
            }
            if (lit >= 0)
            {
                if (slot < 0) slot = faintest;
                _sparks[slot] = (_screen, lit, 0xff);
            }
        }

        // the sparks on a lit button's top and bottom edges (0x20493e0, after the stylus trail): the full texture across
        // the item's box, 8 high on each edge, the top one turned 180 degrees; "type..." items (configuration's control
        // types) take the short lightning_small (32x8), the rest lightning_small1 (128x8); the frame's texture loop
        private void EmitSparks(List<WidgetTri> output)
        {
            foreach ((int screen, int index, int alpha) in _sparks)
            {
                if (alpha >> 3 == 0 || screen != _screen) continue;
                FhMenuItem item = _screens[screen].Items[index];
                bool small = item.Label.StartsWith("type");
                string material = small ? "lightning_small" : "lightning_small1";
                float w = small ? 32 : 128, h = 8, a = (alpha >> 3) / 31f;
                float x0 = item.X0, x1 = item.X1;
                foreach ((float y, bool top) in new[] { ((float)item.Y0, true), ((float)item.Y1, false) })
                {
                    var at = new[] { new Vector2(x0, y + 4), new Vector2(x0, y - 4), new Vector2(x1, y - 4), new Vector2(x1, y + 4) };
                    Vector2[] st = top
                        ? new[] { new Vector2(w, h), new Vector2(w, 0), new Vector2(0, 0), new Vector2(0, h) }
                        : new[] { new Vector2(0, 0), new Vector2(0, h), new Vector2(w, h), new Vector2(w, 0) };
                    _frame.EmitMaterialQuad(Textures, output, material, at, st, a, SparkZ);
                }
            }
        }

        private const float SparkZ = 1e6f; // (z 3.0 on the cart: in front of the page and the frame)
        // the trail: z 0.5 on the cart (0x2049a80), in front of the page (fh_ref_tools p1 main__training_held40: its
        // centre over lit TRAINING's solid white edge), under the sparks drawn after it
        private const float TrailZ = SparkZ / 2;
        private const float CartTrailZ = 0.5f;

        // the DS writes depth only where a texel is opaque (translucent texels and translucent polygons leave it): the
        // trail, drawn after the page at z 0.5, shows through a nearer button's glass but not its opaque texels (fh_ref_tools
        // p1 main__training_held40: the dot over lit TRAINING's glass, TRAINING's letters over the cells below it). So it
        // draws over everything, then the opaque texels of the scene's nearer opaque polygons it overlaps draw again over it
        private void Occlude(List<WidgetTri> tris, int scene, List<(float X0, float Y0, float X1, float Y1)> boxes, float z)
        {
            if (boxes.Count == 0) return;
            for (int i = 0; i < scene; i++)
            {
                WidgetTri t = tris[i];
                if (t.Z <= z || t.A.A < 1) continue;
                float x0 = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), x1 = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
                float y0 = MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)), y1 = MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
                bool over = false;
                foreach ((float bx0, float by0, float bx1, float by1) in boxes)
                {
                    if (x0 < bx1 && x1 > bx0 && y0 < by1 && y1 > by0) { over = true; break; }
                }
                if (!over) continue;
                if (t.TextureId >= 0) t.TextureId = OpaqueTexels(t.TextureId);
                t.Z = TrailZ + 1;
                tris.Add(t);
            }
        }

        // a texture with only its opaque texels (the rest fully clear)
        private int OpaqueTexels(int id)
        {
            UiTexture texture = Textures[id];
            return Textures.GetOrAdd(texture.Key + "|opaque", () =>
            {
                byte[] rgba = (byte[])texture.Rgba.Clone();
                for (int i = 3; i < rgba.Length; i += 4) rgba[i] = rgba[i] == 255 ? (byte)255 : (byte)0;
                return (texture.Width, texture.Height, rgba);
            }).Id;
        }

        // a pressed button's effect once its states have played
        private void Act(FhMenuItem item)
        {
            string label = item.Label;
            string b = item.StateBase;
            switch (_screen)
            {
            case FhMenuData.Popup:
            {
                Action? then = b == "popup_yes" ? _popupYes : _popupDone;
                _screen = _popupFrom >= 0 ? _popupFrom : FhMenuData.Title;
                _popupFrom = -1;
                if (then != null) then();
                else Open(_screen);
                return;
            }
            case FhMenuData.Training when label.StartsWith("Game ") && label.Length == 6:
            {
                // "Game A/B/C" (0x2044870)
                (FhMode mode, int level, string room) = (label[5] - 'A') switch
                {
                    0 => (FhMode.Regulator, 4, "Level SP Regulator"),
                    1 => (FhMode.Survivor, 5, "Level SP Survivor"),
                    _ => (FhMode.Morphball, 3, "Level SP Morphball")
                };
                _music = -1;
                Music?.Invoke(-1, false);
                StartGame?.Invoke(new FhGameStart(mode, level, room));
                return;
            }
            case FhMenuData.Title when b == "title_movie":
                OpenPopup(PopupMovie, () => { Open(FhMenuData.Title); PlayMovie?.Invoke(Movies.FhMovie.Teaser); }, () => Open(FhMenuData.Title));
                return;
            case FhMenuData.Title when b == "title_touch":
                Open(FhMenuData.MainMenu);
                return;
            case FhMenuData.Options when b == "options_erase_data":
                OpenPopup(PopupErase, () =>
                {
                    Save.Reset();
                    Save.Save();
                    OpenPopup(PopupErased, null, () => Open(FhMenuData.Options));
                }, () => Open(FhMenuData.Options));
                return;
            case FhMenuData.EditName:
                if (b == "edit_name_2_ok")
                {
                    Save.Name = _editName;
                    Save.Save();
                }
                else if (b == "edit_name_2_del")
                {
                    if (_editName.Length > 0) _editName = _editName[..^1];
                }
                else if (b == "edit_name_2_upper" || b == "edit_name_2_lower")
                {
                    _editUpper = b == "edit_name_2_upper";
                    _editChara = false;
                }
                else if (b == "edit_name_2_chara")
                {
                    _editChara = true; // (upper / lower kept for when it's switched back)
                }
                else if (label.Length == 1 && char.IsLetter(label[0]))
                {
                    if (_editName.Length < 10) _editName += EditKey(char.ToUpperInvariant(label[0]));
                }
                RefreshTexts();
                break;
            case FhMenuData.Configuration:
                if (label.StartsWith("type"))
                {
                    _pickedControl = ControlTypeOf(label);
                    _configImage = _pickedControl;
                    return;
                }
                if (b == "configuration_ok" && Save.ControlType != _pickedControl)
                {
                    Save.ControlType = _pickedControl;
                    Save.Save();
                }
                break;
            case FhMenuData.HostStart when HexOf(item) >= 0:
                // an arena hex (host_start_1..3 -> 0x209ddd0): the lobby's arena, which its beacons show
                _arena = HexOf(item);
                _shownArena = _arena;
                FhLobby.Lan?.UpdateTemplate(FhLobby.Template(_arena, PlayerName));
                return;
            case FhMenuData.HostStart when b == "host_start_start":
                StartHosted();
                return;
            case FhMenuData.ClientStart when GameRow(item) >= 0:
                JoinGame(_gamesScroll + GameRow(item));
                return;
            case FhMenuData.ClientStart when label == "scroll_up" || label == "scroll_down":
                _gamesScroll = Math.Clamp(_gamesScroll + (label == "scroll_down" ? 1 : -1), 0, Math.Max(0, _games.Count - 3));
                _gamesShown = -1; // (the rows again, after the scroll's slide)
                _litGame = -1;
                RefreshTexts();
                return;
            case FhMenuData.MultiHiScore when b == "multi_hi_score_play":
            {
                // PLAY AGAIN: back to the lobby (0x2042b5c: the host to Host Start, a client to Client Start)
                bool host = _match?.Host == true;
                _match = null;
                Open(host ? FhMenuData.HostStart : FhMenuData.ClientStart);
                return;
            }
            case FhMenuData.TrainingHiScore when b == "training_hi_score_movie":
                // popup 0 "Play movie?" as from the title; YES -> the teaser, then the title
                OpenPopup(PopupMovie, () => { _result = null; Open(FhMenuData.Title); PlayMovie?.Invoke(Movies.FhMovie.Teaser); },
                    () => Open(FhMenuData.TrainingHiScore));
                return;
            case FhMenuData.TrainingHiScore when b == "training_hi_score_play":
                _result = null;
                break;
            }
            int target = _screens.FindIndex(s => s.Label == label);
            if (target >= 0)
            {
                Open(target);
            }
            // anything else stays on its page (the records tables, the edit name keys...; multiplayer: P5)
        }

        private void HighlightItem(int index)
        {
            if (index == _highlight) return;
            _litByPress = false;
            IReadOnlyList<FhMenuItem> items = _screens[_screen].Items;
            FhMenuModel page = Page(_screen);
            if (_highlight >= 0)
            {
                FhMenuItem old = items[_highlight];
                page.Play(State(GameRow(old) >= 0 ? $"client_start_G{GameRow(old) + 1}_dehi" : old.StateBase + "_dehi"));
                Sound?.Invoke(1);
            }
            _highlight = index;
            if (_screen == FhMenuData.HostStart)
            {
                // a lit arena hex shows its stage on the top screen (host_start_<n>_hi_<shown>); off the hexes, the picked one
                int want = index >= 0 && HexOf(items[index]) >= 0 ? HexOf(items[index]) : _arena;
                if (want != _shownArena)
                {
                    page.Play(State($"host_start_{want + 1}_hi_{_shownArena + 1}"));
                    _shownArena = want;
                }
            }
            if (index >= 0)
            {
                string b = items[index].StateBase;
                int row = GameRow(items[index]);
                if (row >= 0)
                {
                    // a game row: lit, and its lobby on the top screen (its player count's cards, its arena)
                    _litGame = row;
                    page.Play(State($"client_start_sel_G{row + 1}"));
                    LanHostInfo game = _games[_gamesScroll + row];
                    page.Play(State($"client_start_{Math.Clamp(game.Seats + 1, 2, FhLobby.MaxPlayers)}_in"));
                    // (client_start_GAME<n>_in: the stage picture of arena n, MAPS frames 3 / 7 / 11; STAGE_<n> only fade it)
                    page.Play(State($"client_start_GAME{Math.Max(FhLobby.ArenaIndex(game.Arena), 0) + 1}_in"));
                    RefreshTexts();
                }
                else
                {
                    page.Play(State(b + "_hi"));
                }
                Sound?.Invoke(0);
                if (_screen == FhMenuData.Configuration && items[index].Label.StartsWith("type"))
                {
                    _configImage = ControlTypeOf(items[index].Label);
                }
                if (_screen == FhMenuData.Records && b != "records_back")
                {
                    // the records page shows the lit button's table (records_<table>_hi)
                    _recordsMultiplayer = b == "records_multiplayer";
                    _recordsTable = b switch
                    {
                        "records_survivor" => FirstHuntSave.Table.Survivor,
                        "records_morphball" => FirstHuntSave.Table.Morphball,
                        _ => FirstHuntSave.Table.Regulator
                    };
                    RefreshTexts();
                }
            }
        }

        private static int ControlTypeOf(string label) => label switch { "typeA" => 0, "typeB1" => 1, "typeB2" => 2, "typeC1" => 3, _ => 4 };

        private void Press(int index)
        {
            FhMenuItem item = _screens[_screen].Items[index];
            if (_screen == FhMenuData.HostStart && item.StateBase == "host_start_back")
            {
                // BACK: "disconnect?" first (0x2043608), then the Multiplayer page
                HighlightItem(-1);
                Sound?.Invoke(7);
                OpenPopup(7, () => { FhLobby.End(); Open(FhMenuData.Multiplayer); }, BackToPage);
                _boltBlue = true;
                return;
            }
            _pressed = index;
            _highlight = index;
            _litByPress = true;
            _boltBlue = Leaves(item);
            Sound?.Invoke(IsBack(item.StateBase) ? 7 : 9);
            // the results page's select states end with the mode, the host page's with its arena: _sel_<1..3>
            FhMenuState? sel = item.IsButton ? State(item.StateBase + "_sel") ?? State(item.StateBase + "_sel" + Variant) : null;
            _running = sel;
            Page(_screen).Play(sel);
            _phase = Phase.Selecting;
        }

        // a button that leaves its page: one with a leave state (_sel_D), a popup's, the title's, edit name's OK / CANCEL
        private bool Leaves(FhMenuItem item) => _screen == FhMenuData.Popup || _screen == FhMenuData.Title
            || _screen == FhMenuData.EditName && (item.StateBase.EndsWith("_ok") || item.StateBase.EndsWith("_cancel"))
            || State(item.StateBase + "_sel_D") != null || State(item.StateBase + "_sel" + Variant + "_D") != null;

        // the end of the select states a page keys by its own state: the results page's mode, the host page's arena
        private string Variant => _screen switch
        {
            FhMenuData.TrainingHiScore => $"_{ResultMode}",
            FhMenuData.HostStart => $"_{_shownArena + 1}",
            _ => ""
        };

        // host_start's arena hexes (host_start_1..3): 0-2, -1 = not one
        private int HexOf(FhMenuItem item) => _screen == FhMenuData.HostStart && item.StateBase.Length == 12
            && item.StateBase.StartsWith("host_start_") && item.StateBase[11] >= '1' && item.StateBase[11] <= '3' ? item.StateBase[11] - '1' : -1;

        // host START (0x2043824): one player alone -> "Not enough players"; else everyone starts (the joiners on START)
        private void StartHosted()
        {
            LanSession? lan = FhLobby.Lan;
            if (lan is not { IsHost: true })
            {
                OpenPopup(0xa, null, () => Open(FhMenuData.Multiplayer)); // "Lost connection!"
                return;
            }
            if (FhLobby.HostSeats(lan, PlayerName).Count < 2)
            {
                OpenPopup(0x12, null, () => Open(FhMenuData.HostStart));
                return;
            }
            lan.UpdateTemplate(FhLobby.Template(_arena, PlayerName));
            lan.SendStart();
            Launch("host", lan.Settings!.Arena);
        }

        private void Launch(string net, string arena)
        {
            _music = -1;
            Music?.Invoke(-1, false);
            StartGame?.Invoke(new FhGameStart(FhMode.Multiplayer, Math.Max(FhLobby.ArenaIndex(arena), 0), arena, net));
        }

        // a game row tapped (0x203b308): the join request and "Connecting" (CANCEL) until the host answers
        private void JoinGame(int g)
        {
            LanSession? lan = FhLobby.Lan;
            if (lan is not { IsHost: false } || g < 0 || g >= _games.Count) return;
            LanHostInfo game = _games[g];
            string name = PlayerName;
            _joinGame = game;
            _joinLan = lan;
            _joined = false;
            _join = Task.Run(() => lan.Join(game.Address, Hunter.Samus, name, timeoutMs: 5000));
            RefreshTexts();
            OpenPopup(3, null, CancelJoin);
        }

        // a join given up (CANCEL, or the host gone): a new browse (the old session says BYE), the list again
        private void CancelJoin()
        {
            FhLobby.Browse();
            _join = null;
            _joinLan = null;
            _joinGame = null;
            _joined = false;
            _gamesShown = -1;
            _litGame = -1;
            BackToPage();
        }

        // the lobby pages each frame: the host's slots as players join and leave; the client's game list, its join's
        // answer, then the host's START (or its leaving)
        private void StepLobby()
        {
            int page = _screen == FhMenuData.Popup ? _popupFrom : _screen;
            LanSession? lan = FhLobby.Lan;
            if (page == FhMenuData.HostStart && lan is { IsHost: true })
            {
                if (++_lobbyTicks % 15 != 0) return;
                int seats = FhLobby.HostSeats(lan, PlayerName).Count;
                if (seats != _seatsShown)
                {
                    // a player in / out: its slot's card (host_start_player_<n>_in / _out)
                    FhMenuModel model = Page(FhMenuData.HostStart);
                    for (int i = 1; i < FhLobby.MaxPlayers; i++)
                    {
                        bool now = i < seats, before = i < _seatsShown;
                        if (now != before) model.Play(State($"host_start_player_{i}_{(now ? "in" : "out")}"));
                    }
                    _seatsShown = seats;
                    RefreshTexts();
                }
                return;
            }
            if (page != FhMenuData.ClientStart || lan is not { IsHost: false })
            {
                return;
            }
            if (_join != null)
            {
                if (!_join.IsCompleted) return;
                bool ok = _join.Result;
                _join = null;
                if (!ReferenceEquals(_joinLan, lan)) return; // (given up)
                if (ok)
                {
                    _joined = true;
                    BackToPage();
                    OpenPopup(0xe, null, CancelJoin); // "Please wait for the host to start the game." (CANCEL)
                }
                else
                {
                    _joinGame = null;
                    _gamesShown = -1;
                    OpenPopup(0xd, null, BackToPage); // "This game is no longer available to join."
                }
                return;
            }
            if (_joined)
            {
                if (lan.Started && lan.Settings != null)
                {
                    _joined = false;
                    BackToPage();
                    Launch("client", lan.Settings.Arena);
                }
                else if (lan.PeerLeft)
                {
                    FhLobby.Browse();
                    _joined = false;
                    _joinGame = null;
                    _gamesShown = -1;
                    OpenPopup(9, null, BackToPage); // "Disconnected."
                }
                return;
            }
            if (_screen != FhMenuData.ClientStart || ++_lobbyTicks % 15 != 0) return;
            List<LanHostInfo> games = FhLobby.Games(lan);
            bool changed = games.Count != _games.Count
                || games.Where((g, i) => g.Name != _games[i].Name || g.Arena != _games[i].Arena || g.Seats != _games[i].Seats).Any();
            _games = games;
            _gamesScroll = Math.Clamp(_gamesScroll, 0, Math.Max(0, games.Count - 3));
            int shown = Math.Min(3, games.Count - _gamesScroll);
            if (shown != _gamesShown && _phase == Phase.Idle)
            {
                // the rows that show (client_start_<n>_game)
                Page(FhMenuData.ClientStart).Play(State($"client_start_{shown}_game"));
                _gamesShown = shown;
                if (_highlight >= 0 && GameRow(_screens[_screen].Items[_highlight]) >= shown) HighlightItem(-1);
                changed = true;
            }
            if (changed) RefreshTexts();
        }

        private void Show(string node, bool show)
        {
            if (show) _hidden.Remove(node);
            else _hidden.Add(node);
        }

        private static bool IsBack(string b) => b.EndsWith("_back") || b.EndsWith("_cancel") || b.EndsWith("_quit") || b == "popup_no";

        // the stylus in bottom-screen pixels (0..256, 0..192 down): touching a button selects it
        public void Touch(bool down, float x, float y)
        {
            _idleTicks = 0;
            _stylus = down;
            _stylusAt = new Vector2(x, -y);
            if (_phase != Phase.Idle)
            {
                _touchDown = down;
                return;
            }
            int over = -1;
            IReadOnlyList<FhMenuItem> items = _screens[_screen].Items;
            foreach (int i in Buttons())
            {
                if (items[i].Contains(x, y))
                {
                    over = i;
                    break;
                }
            }
            // the cart acts on the touch itself (fh_ref_tools p1 main__training_held60: the page gone with the stylus still
            // down), once per touch. A touch that lands off the buttons only lights them: the button under the held stylus is
            // lit (its description up top, the bolts blue), none when it slides off, and lifting it there presses nothing and
            // takes the light away (fh_ref_tools p7 drag_main_*, slide_train_*: MULTIPLAYER lit while crossed, TRAINING lit
            // while held on it, unlit at the lift)
            if (down && !_touchDown)
            {
                // a touch on a button that leaves the page presses it at once: its _sel lights it, without the _hi state
                // (its description up top) first (fh_ref_tools p1 main__training_held40: the button lit, the top screen
                // without its text); the others light first as before (an arena hex shows its stage, a game row its lobby)
                _touchPressed = over >= 0;
                if (over >= 0 && Leaves(items[over]))
                {
                    if (_highlight != over) HighlightItem(-1);
                }
                else
                {
                    HighlightItem(over);
                }
                if (over >= 0) Press(over);
            }
            else if (!_touchPressed && _touchDown)
            {
                HighlightItem(down ? over : -1);
            }
            _touchDown = down;
        }

        // the pad (pressed this frame): up/down/left/right move the highlight through the page's buttons, confirm
        // selects, back takes the page's BACK (or CANCEL / QUIT / NO) button
        public void Pad(FhPad pressed)
        {
            if (pressed != FhPad.None) _idleTicks = 0;
            if (_phase != Phase.Idle || pressed == FhPad.None) return;
            List<int> buttons = Buttons();
            IReadOnlyList<FhMenuItem> items = _screens[_screen].Items;
            if ((pressed & FhPad.Back) != 0)
            {
                foreach (int i in buttons)
                {
                    if (IsBack(items[i].StateBase))
                    {
                        HighlightItem(i);
                        Press(i);
                        return;
                    }
                }
                if (_screen == FhMenuData.Title) Exit?.Invoke();
                return;
            }
            if (buttons.Count == 0) return;
            if ((pressed & FhPad.Confirm) != 0)
            {
                if (_highlight >= 0) Press(_highlight);
                else
                {
                    HighlightItem(buttons[0]);
                    if (buttons.Count == 1) Press(buttons[0]);
                }
                return;
            }
            int at = buttons.IndexOf(_highlight);
            if ((pressed & (FhPad.Down | FhPad.Right)) != 0) at = at < 0 ? 0 : (at + 1) % buttons.Count;
            else if ((pressed & (FhPad.Up | FhPad.Left)) != 0) at = at < 0 ? buttons.Count - 1 : (at - 1 + buttons.Count) % buttons.Count;
            else return;
            HighlightItem(buttons[at]);
        }

        // the frame's wireless icon in the top screen's corner (its nodes start disabled): on the lobby pages while a LAN
        // session is up; icon0..3 are its signal levels (fh_ref hoststart_idle, a host alone: icon0, grey bars). LAN has no
        // link strength: full bars once anyone is linked
        private void SignalIcon(int pageScreen)
        {
            int level = -1;
            if ((pageScreen == FhMenuData.HostStart || pageScreen == FhMenuData.ClientStart) && FhLobby.Lan is { } lan)
            {
                level = lan.IsHost ? (FhLobby.HostSeats(lan, PlayerName).Count > 1 ? 3 : 0) : _joined ? 3 : -1;
            }
            foreach (Node node in _frame.Model.Nodes)
            {
                if (node.Name == "iconBorder") node.Enabled = level >= 0;
                else if (node.Name.Length == 5 && node.Name.StartsWith("icon")) node.Enabled = node.Name[4] - '0' == level;
            }
        }

        // the page in menu space, back to front: the backdrops, the page and its text, the frame, the stylus trail
        // (0x2046868; the hi-score pages draw the frame on the touch screen only)
        // (`back` / `front` / `frame`: the 2D background layer / the page's 3D / the frame and stylus trail, for compositing)
        public void Evaluate(List<WidgetTri> output, bool back = true, bool front = true, bool frame = true)
        {
            var tris = new List<WidgetTri>();
            var rastered = new HashSet<int>(); // already in DS pixels (the trail's cells)
            bool popup = _screen == FhMenuData.Popup && _popupFrom >= 0;
            int pageScreen = popup ? _popupFrom : _screen;
            // the backdrops are the 2D background layer, behind all the 3D (0x20504b8)
            FhMenuModel? backdrop = Backdrop(_screens[pageScreen].Backdrop);
            if (backdrop != null && back)
            {
                backdrop.ImagePair = pageScreen == FhMenuData.Configuration ? _configImage * 2 : 0;
                backdrop.Evaluate(Textures, output);
            }
            if (back) _bottom.Evaluate(Textures, output);
            if (front) Page(pageScreen).Evaluate(Textures, tris, hidden: _hidden, texts: _texts, font: _font);
            if (popup && front)
            {
                Page(FhMenuData.Popup).Evaluate(Textures, tris, hidden: _popupHidden, texts: _popupTexts, font: _font);
            }
            bool hiScore = pageScreen == FhMenuData.TrainingHiScore || pageScreen == FhMenuData.MultiHiScore;
            if (frame)
            {
                SignalIcon(pageScreen);
                _frame.Evaluate(Textures, tris, hidden: pageScreen == FhMenuData.Title ? _frameHiddenTitle : _frameHidden, top: !hiScore);
            }
            if (frame && pageScreen != FhMenuData.EditName)
            {
                int scene = tris.Count;
                var cells = new List<(float X0, float Y0, float X1, float Y1)>();
                var cursors = new List<(List<WidgetTri> Tris, Vector2 At)>();
                foreach ((Vector2 at, int life) in _trail)
                {
                    if (life <= 7) continue;
                    int start = tris.Count;
                    _frame.EvaluateNodeAt(Textures, tris, "cursor", new Vector3(at.X, at.Y, TrailZ), (life >> 3) / 31f);
                    Retag(tris, start, TagTrail);
                    List<WidgetTri> cursor = tris.GetRange(start, tris.Count - start);
                    tris.RemoveRange(start, cursor.Count);
                    if (DsPixels)
                    {
                        for (int k = 0; k < cursor.Count; k++) cursor[k] = DsRaster(cursor[k], trail: true);
                    }
                    cursors.Add((cursor, at));
                    cells.Add((at.X - 28, at.Y - 37, at.X + 36, at.Y + 28));
                }
                TrailCells(tris, cursors, rastered);
                Occlude(tris, scene, cells, CartTrailZ);
            }
            if (frame)
            {
                int start = tris.Count;
                EmitSparks(tris);
                Retag(tris, start, TagSparks);
            }
            // the DS draws with depth: nearer (larger z) over farther, otherwise in order
            var order = new int[tris.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => tris[a].Z != tris[b].Z ? tris[a].Z.CompareTo(tris[b].Z) : a.CompareTo(b));
            // translucent polygons write no depth on the DS, so among themselves they draw in order whatever their z
            // (fh_ref trainresult: the black ScoreBG, z -37.7, over the green row stripes, z -30, drawn before it): their
            // slots in the depth order are refilled in draw order
            var slots = new List<int>();
            for (int k = 0; k < order.Length; k++)
            {
                if (tris[order[k]].A.A < 1) slots.Add(k);
            }
            int[] translucent = slots.Select(k => order[k]).OrderBy(i => i).ToArray();
            for (int k = 0; k < slots.Count; k++) order[slots[k]] = translucent[k];
            foreach (int i in order) output.Add(DsPixels && !rastered.Contains(i) ? DsRaster(tris[i]) : tris[i]);
        }

        // where cursors overlap the cart shows the strongest one: the DS keeps the larger alpha of a translucent pixel drawn
        // over another (the 3D layer then blends over the 2D grid by it), and the cursors are near one colour (fh_ref_tools
        // p11 ab_*: a tap's centre on its own fade until it drops under a newer neighbour's ring, then that ring's value;
        // p7 drag_main_*: a passed cell fades as a lone tap's does, p10 tap_*). The cursor (a cell, its ring and a dim outer
        // ring) sits on the grid's hex lattice, so each lattice cell goes to the cursor strongest in it, cut out of that
        // cursor's triangles. (In DS pixels when drawing them: cut after the projection, so a cell's texels don't move with
        // its own rounded corners.)
        private void TrailCells(List<WidgetTri> tris, List<(List<WidgetTri> Tris, Vector2 At)> cursors, HashSet<int> rastered)
        {
            Vector2 Place(float x, float y)
            {
                if (!DsPixels) return new Vector2(x, -y);
                UiVertex v = DsRaster(new UiVertex { X = x, Y = -y }, false, free: true);
                return new Vector2(v.X, v.Y);
            }
            // lattice cells: centres 8 apart in rows 7 apart, odd rows half a cell over; a cursor's own cell centred at
            // (cx + 4, cy + 5.5) in touch-screen pixels (fh_ref_tools p10 tap_020: x 40..47, rows 113..121 for a tap at 40,120)
            var best = new Dictionary<(int X, int Row), (float Strength, int Cursor)>();
            for (int c = 0; c < cursors.Count; c++)
            {
                int cx = (int)MathF.Round(cursors[c].At.X), cy = (int)MathF.Round(-cursors[c].At.Y);
                for (int j = -4; j <= 4; j++)
                {
                    for (int i = -5; i <= 5; i++)
                    {
                        float ex = cx + 4 + 8 * i + ((j & 1) != 0 ? 4 : 0), ey = cy + 5.5f + 7 * j;
                        // its strength: the alpha at the cell's centre and around it
                        float strength = 0;
                        foreach ((float dx, float dy) in HexSamples)
                        {
                            Vector2 at = Place(ex + dx, ey + dy);
                            foreach (WidgetTri t in cursors[c].Tris) strength += AlphaAt(t, at);
                        }
                        if (strength <= 0) continue;
                        (int, int) key = ((int)ex, (cy + 7 * j) / 7);
                        // (a tie to the later: drawn over the other)
                        if (!best.TryGetValue(key, out var held) || strength >= held.Strength) best[key] = (strength, c);
                    }
                }
            }
            foreach (KeyValuePair<(int X, int Row), (float Strength, int Cursor)> cell in best)
            {
                float ex = cell.Key.X, ey = cell.Key.Row * 7 + 5.5f;
                int c = cell.Value.Cursor;
                // the cell: (+-4, +-2) and (0, +-5) about its centre, which tiles the lattice
                Vector2[] hex =
                {
                    Place(ex + 4, ey - 2), Place(ex, ey - 5), Place(ex - 4, ey - 2),
                    Place(ex - 4, ey + 2), Place(ex, ey + 5), Place(ex + 4, ey + 2),
                };
                foreach (WidgetTri t in cursors[c].Tris)
                {
                    int before = tris.Count;
                    ClipTri(t, hex, tris);
                    if (DsPixels)
                    {
                        for (int k = before; k < tris.Count; k++) rastered.Add(k);
                    }
                }
            }
        }

        private static readonly (float, float)[] HexSamples = { (0, 0), (2, -1), (0, -2.5f), (-2, -1), (-2, 1), (0, 2.5f), (2, 1) };

        // the triangle's alpha at the point (texel times vertex), 0 off it
        private float AlphaAt(WidgetTri t, Vector2 p)
        {
            float d = (t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (t.B.Y - t.A.Y);
            if (MathF.Abs(d) < 1e-6f) return 0;
            float w1 = ((p.X - t.A.X) * (t.C.Y - t.A.Y) - (t.C.X - t.A.X) * (p.Y - t.A.Y)) / d;
            float w2 = ((t.B.X - t.A.X) * (p.Y - t.A.Y) - (p.X - t.A.X) * (t.B.Y - t.A.Y)) / d;
            float w0 = 1 - w1 - w2;
            if (w0 < 0 || w1 < 0 || w2 < 0) return 0;
            float alpha = w0 * t.A.A + w1 * t.B.A + w2 * t.C.A;
            if (t.TextureId < 0 || alpha <= 0) return Math.Max(0, alpha);
            UiTexture texture = Textures[t.TextureId];
            float u = w0 * t.A.U + w1 * t.B.U + w2 * t.C.U, v = w0 * t.A.V + w1 * t.B.V + w2 * t.C.V;
            int x = Wrap((int)MathF.Floor(u * texture.Width), texture.Width, t.WrapS);
            int y = Wrap((int)MathF.Floor(v * texture.Height), texture.Height, t.WrapT);
            return alpha * texture.Rgba[(y * texture.Width + x) * 4 + 3] / 255f;
        }

        private static int Wrap(int i, int n, UiWrap mode)
        {
            switch (mode)
            {
            case UiWrap.Repeat:
                return (i % n + n) % n;
            case UiWrap.Mirror:
                int m = (i % (2 * n) + 2 * n) % (2 * n);
                return m < n ? m : 2 * n - 1 - m;
            default:
                return Math.Clamp(i, 0, n - 1);
            }
        }

        // the part of a triangle inside a convex polygon, as a fan of triangles (texcoords and colours carried along)
        private static void ClipTri(WidgetTri t, Vector2[] polygon, List<WidgetTri> output)
        {
            var poly = new List<UiVertex> { t.A, t.B, t.C };
            float mx = 0, my = 0;
            foreach (Vector2 p in polygon)
            {
                mx += p.X;
                my += p.Y;
            }
            mx /= polygon.Length;
            my /= polygon.Length;
            for (int e = 0; e < polygon.Length && poly.Count > 0; e++)
            {
                Vector2 a = polygon[e], b = polygon[(e + 1) % polygon.Length];
                if (a == b) continue;
                float inside = MathF.Sign((b.X - a.X) * (my - a.Y) - (b.Y - a.Y) * (mx - a.X));
                float Side(UiVertex v) => inside * ((b.X - a.X) * (v.Y - a.Y) - (b.Y - a.Y) * (v.X - a.X));
                var next = new List<UiVertex>();
                for (int k = 0; k < poly.Count; k++)
                {
                    UiVertex p = poly[k], q = poly[(k + 1) % poly.Count];
                    float sp = Side(p), sq = Side(q);
                    if (sp >= 0) next.Add(p);
                    if (sp >= 0 != sq >= 0)
                    {
                        float f = sp / (sp - sq);
                        next.Add(new UiVertex
                        {
                            X = p.X + (q.X - p.X) * f, Y = p.Y + (q.Y - p.Y) * f, U = p.U + (q.U - p.U) * f, V = p.V + (q.V - p.V) * f,
                            R = p.R + (q.R - p.R) * f, G = p.G + (q.G - p.G) * f, B = p.B + (q.B - p.B) * f, A = p.A + (q.A - p.A) * f,
                        });
                    }
                }
                poly = next;
            }
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                WidgetTri piece = t;
                piece.A = poly[0];
                piece.B = poly[k];
                piece.C = poly[k + 1];
                output.Add(piece);
            }
        }

        private static void Retag(List<WidgetTri> tris, int start, int tag)
        {
            for (int k = start; k < tris.Count; k++)
            {
                WidgetTri t = tris[k];
                t.Item = tag << 12;
                tris[k] = t;
            }
        }

        // where the cart's 3D lands in DS pixels: its menu ortho (0x2049320 -> 0x204fb58, t 0 b -192 in 20.12) rounds the y
        // scale to 43/4096 (exact 42.67), so a row is y * 1.0078125 down from its screen's top, and the DS fills the rows
        // floor(top) .. floor(bottom) - 1 (fh_ref: main menu BACK, the title's copyright one row low, MULTIPLAYER not).
        // As vertices on whole pixels our centre-sampled raster fills the same rows. (The 2D backdrops aren't projected.)
        public static bool DsPixels = true; // false: menu space as the model gives it (tools)

        // (`trail`: the stylus trail, on the touch screen's projection wherever it goes: the one-screen layout lets it run
        // past the touch screen's edges)
        private WidgetTri DsRaster(WidgetTri t, bool trail = false)
        {
            bool top = !trail && t.A.Y + t.B.Y + t.C.Y >= 0;
            t.A = DsRaster(t.A, top, trail);
            t.B = DsRaster(t.B, top, trail);
            t.C = DsRaster(t.C, top, trail);
            // the DS takes a row's texel at its top edge, we take it at the pixel's centre: where a texture runs other than a
            // texel a row from a whole texel (fh_ref training: REGULATOR, SURVIVOR at 0.83 texel/row; main menu TRAINING,
            // whose v starts a hair under 0) ours would land a row off, so its texcoords move up to the row's top (less
            // 1/64, inside the texel rather than on the edge where float error picks either). A texel-a-row texture keeps
            // the centre: the same texel at 1x and crisp at any size, where a moved one would show half a DS row of the
            // texel above. x needs no move (fh_ref: -1 px with it).
            float x1 = t.B.X - t.A.X, y1 = t.B.Y - t.A.Y, x2 = t.C.X - t.A.X, y2 = t.C.Y - t.A.Y;
            float det = x1 * y2 - x2 * y1;
            if (MathF.Abs(det) > 1e-6f)
            {
                float u1 = t.B.U - t.A.U, v1 = t.B.V - t.A.V, u2 = t.C.U - t.A.U, v2 = t.C.V - t.A.V;
                float dudy = (u2 * x1 - u1 * x2) / det, dvdx = (v1 * y2 - v2 * y1) / det, dvdy = (v2 * x1 - v1 * x2) / det;
                int texH = t.TextureId >= 0 && t.TextureId < Textures.Count ? Textures[t.TextureId].Height : 0;
                float topY = MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
                float perRow = MathF.Abs(dvdy) * texH, topTexel = (t.A.V + dvdy * (topY - t.A.Y)) * texH;
                float phase = topTexel - MathF.Round(topTexel);
                bool texelARow = MathF.Abs(perRow - 1) < 1e-3f && MathF.Abs(phase) < 1e-3f && MathF.Abs(dvdx) * texH < 1e-3f;
                if (texH > 0 && !texelARow)
                {
                    // drawn above 1x, a DS row's upper part now samples above the texture's first texel; where the triangle
                    // shows one copy of the texture that must clamp, not repeat in the far edge's texels (training: a line
                    // of the buttons' bottom rows over their tops at 2x)
                    const float e = 1e-3f;
                    if (t.WrapT == UiWrap.Repeat && Math.Min(t.A.V, Math.Min(t.B.V, t.C.V)) > -e && Math.Max(t.A.V, Math.Max(t.B.V, t.C.V)) < 1 + e)
                    {
                        t.WrapT = UiWrap.Clamp;
                    }
                    if (t.WrapS == UiWrap.Repeat && Math.Min(t.A.U, Math.Min(t.B.U, t.C.U)) > -e && Math.Max(t.A.U, Math.Max(t.B.U, t.C.U)) < 1 + e)
                    {
                        t.WrapS = UiWrap.Clamp;
                    }
                    float du = dudy * (0.5f - 1 / 64f), dv = dvdy * (0.5f - 1 / 64f);
                    t.A.U += du; t.B.U += du; t.C.U += du;
                    t.A.V += dv; t.B.V += dv; t.C.V += dv;
                }
            }
            return t;
        }

        private static UiVertex DsRaster(UiVertex v, bool top, bool free = false)
        {
            const float scaleY = 43 * 192 / 8192f;
            v.X = MathF.Floor(v.X);
            v.Y = free ? -MathF.Floor(-v.Y * scaleY)
                : top ? 192 - Math.Min(MathF.Floor((192 - v.Y) * scaleY), 192) : -Math.Min(MathF.Floor(-v.Y * scaleY), 192);
            return v;
        }
    }
}
