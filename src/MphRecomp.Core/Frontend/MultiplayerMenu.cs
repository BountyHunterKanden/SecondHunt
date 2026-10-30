using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MphRead;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Config;

// The game-code side of the vanilla multiplayer menu (docs/MULTIPLAYER_MENU.md, "P-MP"): what overlay 0's page handlers do
// on top of the menu data for CHOOSE MULTIPLAYER TYPE (25), CREATE OR JOIN A GAME (26), the mode page (27), the seven rules
// pages (28-34) with their ADVANCED box, SELECT HUNTER (35) and the host lobby (38). The data already holds every piece,
// its animations, links and most page changes; the game code keeps one match setup (ov0's struct at 0x020CBD90), fills the
// placeholder strings from it ("setting a", "10", "arena name", "client a"...) and shows or hides the pieces that depend on
// it. START GAME hands the setup to the platform shell (FrontendRequestKind.StartMatch, MatchSetupText), which plays the
// match and comes back to the lobby (ReturnPage).
//
// Read from USA rev 1 overlay 0 (2026-10-07, M0): the value tables (0x02143D44..0x02143E50), the arena list and its mode
// filter (0x02143D28 / 0x02143E8C), the defaults (0x2136414), the lobby (page handler 0x2139258: ADD BOT 0x2134C88, the
// seat calls), and BizHawk runs of the real menus for the looks. The callback ids are the same in USA rev 0 and rev 1 for
// these pages (unlike file select's); Bind still checks each one is on the page where the data should have it, and the
// whole menu stays off (the old lobby request) on a ROM where it isn't. Pieces are found by widget, text or position,
// never by item number (rev 1 adds the "TM" item to the logo pages).
//
// After the match, RESULTS (47, M2) from the results the match screen leaves (MatchResults): PLAY AGAIN and QUIT as vanilla's.
//
// CHOOSE MULTIPLAYER TYPE's doors are ours (decision 1 = A: HUNTER LICENSE / LOCAL PLAY / ONLINE PLAY) and REPLAYS takes RIVAL
// RADAR's corner (M4), built from the file screen's MOVIES pieces.
//
// LAN (M3): the host lobby (38) beacons while it can take a player, CREATE OR JOIN's AVAILABLE GAMES rows list the games
// heard, and a joiner goes SELECT HUNTER (36) -> the "please wait" lobby (39) until the host's START GAME (LanSession).
// The last row ("enter address") opens the ROM's ENTER NEW CODE keypad (61) to type the host's address.
//
// Echoes arenas (M5): once imported (MatchArenas.All's host rooms), they join the arena list before RANDOM (owner 14:20),
// in the modes their entity data supports, named and sized as the vanilla ones are; RANDOM can pick them.
//
// The nickname (M6, decision 5) lives in a player profile (HunterLicense: hunter_license.json beside the settings):
// vanilla's first-visit notice points at the HUNTER LICENSE door, which opens the player's license card (page 60, as
// vanilla shows your own: rank, five tabs of stats, hunter of choice) with EDIT NAME -> the ROM's name keyboard (page 62,
// US keys). Each match counts for the license as vanilla's (wins, kills, favourites, ranking points against people) and
// for vanilla's unlocks (locked hunters and arenas).
//
// Not yet (later milestones): the replay list behind REPLAYS (Replay mode's R3) and the friends and rivals roster.
namespace MphRecomp.Frontend
{
    public sealed partial class MultiplayerMenu
    {
        public const int TypePage = 25, CreateJoinPage = 26, ModePage = 27, HunterPage = 35, LobbyPage = 38;
        // the joiner's pages (LAN, M3): its SELECT HUNTER and its "please wait for all players to be ready." lobby
        public const int JoinHunterPage = 36, JoinLobbyPage = 39;
        // the Wi-Fi page: its FRIENDS AND RIVALS icon becomes the HUNTER LICENSE door's
        private const int WifiPage = 52;
        // after a match (group 10)
        public const int ResultsPage = 47;
        // START GAME's way out, as vanilla: the loading page, then the fade into the game (BizHawk: 15 -> 17 69 frames later)
        public const int LoadingPage = 15, IntoGamePage = 17;
        private const int IntoGameTicks = 34;

        // the pages this code answers on (and ENTER NEW CODE when it's the join-by-address keypad)
        public static bool Owns(int page) => page >= TypePage && page <= 48 || page == AddressPage && _keypad || page == NamePage && _keyboard
            || page == LicensePage && _card;
        // the multi-card setup pages (frame group 7), and RESULTS (group 10: the same two-box top screen, medal left and texts
        // right): one combined screen (MenuLayout.Setup, owner 2026-10-07)
        public static bool IsSetupPage(MenuPage page) => page.Index >= CreateJoinPage && page.Index <= 39 && page.Field1D == 7
            || page.Index == ResultsPage && page.Field1D == 10;

        // page 60 is the HUNTER LICENSE card (BindCard): its own combined screen (MenuLayout.License)
        public static bool IsCardPage(MenuPage page) => page.Index == LicensePage && _card;

        // pages 61 (the join-by-address keypad) and 62 (the name keyboard): their own combined screens (MenuLayout.Keypad /
        // Keyboard; Reports R3, owner ~22:27)
        public static bool IsKeypadPage(MenuPage page) => page.Index == AddressPage && _keypad;
        public static bool IsKeyboardPage(MenuPage page) => page.Index == NamePage && _keyboard;

        public MenuPopup? Popup { get; set; }
        public Action<string>? Log { get; set; }
        public Action<FrontendRequest>? Request { get; set; }
        // a press on something still to come (the game's WORK IN PROGRESS box)
        public Action<string>? WorkInProgress { get; set; }
        // REPLAYS (call 93): the replay list (Replay mode: RecompMenus.Replays.cs, wired by FrontendSession)
        public Action<MenuEngine>? OpenReplays { get; set; }
        // ONLINE PLAY (call 92): the online hub (Online play menus: OnlineMenus.cs, wired by FrontendSession)
        public Action<MenuEngine>? OpenOnline { get; set; }
        // this player's last SELECT HUNTER pick and its look, for an online game's seat (OnlineMenus.cs)
        public static (Hunter Hunter, string? Skin, string? Character) MyLook => (_setup.MyHunter, _setup.MySkin, _setup.MyCharacter);
        // this player's lobby colour pick (-1 = auto), sent with the look (LAN JOIN, online JOIN / LOOK)
        public static int MyColour => _setup.MyColour;

        // ONLINE PLAY's CREATE (Online play menus, OnlineMenus.cs): the same setup pages as CREATE GAME, but SELECT HUNTER goes on
        // to `next` (WHO CAN JOIN, or the online lobby after "change hunter" or before a JOIN) instead of the lobby, and back
        // leaves for `back` (the online page it came from) without the wireless question. Off again on CHOOSE MULTIPLAYER TYPE
        // and CREATE OR JOIN. SELECT HUNTER on its own (not fresh: "change hunter", a joiner's pick) keeps its MODE / RULES tabs
        // as steps only, as the LAN joiner's page does: that game's setup isn't this player's to change there.
        // edit: the HOST's "change hunter" from its online lobby (owner 2026-10-15: once the game was made the host couldn't
        // change anything): SELECT HUNTER with its MODE / RULES tabs live; back or a pick returns to the lobby, which takes
        // the mode, arena and rules when a mode / rules page was opened (TakeOnlineRulesEdit, OnlineMenus.cs)
        private static int s_onlineNext = -1, s_onlineBack = -1;
        private static bool s_onlineHunterOnly, s_onlineEdit, s_onlineRulesEdited;
        public static bool TakeOnlineRulesEdit()
        {
            bool edited = s_onlineRulesEdited;
            s_onlineRulesEdited = false;
            return edited;
        }
        public static bool InOnlineSetup => s_onlineNext >= 0;
        // SELECT HUNTER on its own ended in a pick (not back): RecompMenus' TRY CONTROLS (RecompMenus.ZoomAim.cs)
        public static bool OnlineHunterPicked { get; private set; }
        private static int AfterHunter => s_onlineNext >= 0 ? s_onlineNext : LobbyPage;

        // fresh: a new game as CREATE GAME's (the boxes empty, no bots) from the mode page; else SELECT HUNTER only
        public void BeginOnlineSetup(MenuEngine menu, int next, int back, bool fresh, bool edit = false)
        {
            s_onlineNext = next;
            s_onlineBack = back;
            s_onlineHunterOnly = !fresh && !edit;
            s_onlineEdit = edit && !fresh;
            s_onlineRulesEdited = false;
            OnlineHunterPicked = false;
            if (!fresh)
            {
                menu.GoTo(HunterPage);
                return;
            }
            EndLan();
            _setup.ModeChosen = false;
            _setup.RulesSeen = false;
            for (int s = 1; s < _setup.Seats.Length; s++) _setup.Seats[s] = new Seat();
            _setup.Seats[0].Ready = true;
            menu.GoTo(ModePage);
        }

        public static void EndOnlineSetup()
        {
            s_onlineNext = s_onlineBack = -1;
            s_onlineHunterOnly = s_onlineEdit = false;
        }

        // the game as set up, for WHO CAN JOIN's YOUR GAME box: mode, arena, then the mode's rules as its page labels them
        public IReadOnlyList<(string Label, string Value)> OnlineSummary()
        {
            ModeInfo m = CurrentMode;
            var lines = new List<(string, string)> { ("mode", Word(m.Name) + (_setup.TeamPlay && m.Teams != m.Solo ? " teams" : "")), ("arena", ArenaName(_setup.Arena)) };
            string[] labels = RuleLabels(m);
            for (int r = 0; r < m.Rules.Length; r++)
            {
                Rule rule = m.Rules[r];
                if (rule is Rule.TeamPlay or Rule.AutoReset) continue;
                string value = rule == Rule.TimeLimit ? $"{_setup.TimeLimit}:00" : RuleValue(rule);
                lines.Add((labels[r].ToLowerInvariant(), value.ToLowerInvariant()));
            }
            return lines;
        }

        // a bot for an online game's seat (the hunter's ROM bot name, as ADD BOT's), level 0-2
        public static MatchPlayer OnlineBot(Hunter hunter, int level) => new()
        {
            Hunter = hunter, Bot = true, BotLevel = level, Nickname = BotNames[hunter].ToUpperInvariant(), RankLevel = -1,
        };

        // recomp (bug-fix batch A5, 2026-10-23): an online host's bot can also be the TROOPER or DARK SAMUS, as a local
        // lobby's can (SlotOpen): the game's EXTRA PICKUPS + EXTRA CHARACTERS on and a skin of the character on this
        // device. The bot wears the character's first skin here and goes by the character's name
        public static List<Character> OnlineBotExtras(MatchSettings s)
        {
            var list = new List<Character>();
            string? hd = CharacterHdRoot ?? s_profileHd;
            if (hd == null || s.PirateBlaster == false || s.ExtraCharacters == false) return list;
            foreach (Character c in new[] { Characters.Trooper, Characters.DarkSamus })
            {
                if (Characters.Skins(c, hd).Count > 0) list.Add(c);
            }
            return list;
        }

        public static MatchPlayer OnlineBot(Character c, int level)
        {
            MatchPlayer bot = OnlineBot(c.Base, level);
            string? hd = CharacterHdRoot ?? s_profileHd;
            bot.Character = c.Id;
            bot.Skin = hd == null ? null : Characters.Skins(c, hd).FirstOrDefault();
            bot.Nickname = c.Name;
            return bot;
        }

        // the same bot at another level (or remade by a new host): an extra character keeps its character, skin and name
        public static MatchPlayer OnlineBotLike(MatchPlayer p, int level)
        {
            MatchPlayer bot = OnlineBot(p.Hunter, level);
            if (p.Character == null) return bot;
            bot.Character = p.Character;
            bot.Skin = p.Skin;
            bot.Nickname = p.Nickname;
            return bot;
        }
        public bool Bound { get; private set; }

        // ---- the setup (process-wide like the game's struct, so it outlives the menus while a match runs) ----

        private enum Rule { PointGoal, NodesGoal, OctolithGoal, Lives, TimeLimit, TimeGoal, TeamPlay, AutoReset }

        // id = the game's mode number (S+0; the arena filter's column is id - 2)
        private sealed record ModeInfo(int Id, int Call, int Page, string Name, GameMode Solo, GameMode Teams, Rule[] Rules,
            int GoalDefault, int TimeLimitDefault);

        private static readonly ModeInfo[] Modes =
        {
            new(2, 103, 28, "battle", GameMode.Battle, GameMode.BattleTeams,
                new[] { Rule.PointGoal, Rule.TimeLimit, Rule.TeamPlay }, 2, 2),
            new(3, 99, 32, "bounty", GameMode.Bounty, GameMode.BountyTeams,
                new[] { Rule.OctolithGoal, Rule.TimeLimit, Rule.AutoReset, Rule.TeamPlay }, 2, 5),
            new(4, 104, 34, "capture", GameMode.Capture, GameMode.Capture,
                new[] { Rule.OctolithGoal, Rule.TimeLimit, Rule.AutoReset }, 4, 5),
            new(5, 102, 31, "defender", GameMode.Defender, GameMode.DefenderTeams,
                new[] { Rule.TimeGoal, Rule.TimeLimit, Rule.TeamPlay }, 0, 5),
            new(6, 106, 33, "nodes", GameMode.Nodes, GameMode.NodesTeams,
                new[] { Rule.NodesGoal, Rule.TimeLimit, Rule.TeamPlay }, 3, 5),
            new(7, 105, 29, "prime hunter", GameMode.PrimeHunter, GameMode.PrimeHunter,
                new[] { Rule.TimeGoal, Rule.TimeLimit }, 0, 5),
            new(8, 101, 30, "survival", GameMode.Survival, GameMode.SurvivalTeams,
                new[] { Rule.Lives, Rule.TimeLimit, Rule.TeamPlay }, 2, 5),
        };

        // the value tables (vanilla's steps; the defaults are indexes into them). The rows now step by 1 between each
        // table's first and last value (F1, owner 2026-10-24: the time goal by 0.1 minute, the unit it shows), wrapping
        // both ways as vanilla's do; a held arrow repeats (FrontendSession.TickHold) and stops at the ends
        private static readonly int[] BattleGoals = { 1, 5, 7, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100 };
        private static readonly int[] NodesGoals = { 40, 50, 60, 70, 80, 90, 100, 120, 140, 160, 180, 190, 200, 250 };
        private static readonly int[] OctolithGoals = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 25 };
        private static readonly int[] LivesValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        private static readonly int[] TimeLimitMinutes = { 3, 5, 7, 9, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 };
        private static readonly int[] TimeGoalSeconds = { 60, 90, 120, 150, 180, 210, 240, 270, 300, 360, 420, 480, 540, 600 };
        private const int TimeGoalDefault = 1;

        // the arenas in the game's order (room ids; 29 = "random"), and which modes each allows (columns battle, bounty,
        // capture, defender, nodes, prime hunter, survival); imported arenas go in before random (RefreshArenas)
        private const int RandomArena = 29;
        private static readonly int[] VanillaArenaRooms =
        {
            95, 93, 100, 97, 102, 113, 105, 110, 111, 104, 98, 103, 109, 107, 94, 101, 112, 96, 106, 108, 119, 99, 115, 114,
            116, 117, 118, RandomArena
        };
        private static readonly string[] VanillaArenaModes =
        {
            "1001111", "1011111", "1001111", "1000111", "1011111", "1011111", "1011111", "0111000", "1000111", "1001111",
            "1000111", "1000111", "1001111", "1011111", "1011111", "1011111", "1111111", "1110111", "1000111", "1110011",
            "1011111", "1010111", "1001111", "1001111", "1011111", "1011111", "1001111", "1111111"
        };
        private static int[] ArenaRooms = VanillaArenaRooms;
        private static string[] ArenaModes = VanillaArenaModes;
        // the modes behind the columns, for an imported arena's census
        private static readonly GameMode[] ArenaModeColumns =
            { GameMode.Battle, GameMode.Bounty, GameMode.Capture, GameMode.Defender, GameMode.Nodes, GameMode.PrimeHunter, GameMode.Survival };

        // whether the game's own arena table offers the mode (or its teams version) on a retail arena; null for an arena
        // or mode the table doesn't list. For the screens that don't use this menu (the fallback MatchLobby, BotProbe):
        // the entity census alone allows e.g. Battle on the expanded Transfer Lock, which the game never offers
        public static bool? VanillaOffers(int roomId, GameMode mode)
        {
            GameMode solo = mode switch
            {
                GameMode.BattleTeams => GameMode.Battle, GameMode.SurvivalTeams => GameMode.Survival,
                GameMode.BountyTeams => GameMode.Bounty, GameMode.NodesTeams => GameMode.Nodes,
                GameMode.DefenderTeams => GameMode.Defender, _ => mode
            };
            int arena = Array.IndexOf(VanillaArenaRooms, roomId);
            int column = Array.IndexOf(ArenaModeColumns, solo);
            if (arena < 0 || roomId == RandomArena || column < 0) return null;
            return VanillaArenaModes[arena][column] == '1';
        }
        // "map size": the room table's first field (arm9 0x020B8490 + id * 0x70): 0-2 small, 3 medium, 4-6 large
        private static readonly Dictionary<int, int> ArenaSize = new()
        {
            [95] = 2, [93] = 3, [100] = 2, [97] = 3, [102] = 3, [113] = 4, [105] = 3, [110] = 4, [111] = 3, [104] = 2,
            [98] = 2, [103] = 2, [109] = 3, [107] = 3, [94] = 4, [101] = 4, [112] = 4, [96] = 4, [106] = 4, [108] = 4,
            [119] = 3, [99] = 4, [115] = 3, [114] = 4, [116] = 4, [117] = 4, [118] = 4
        };

        // SELECT HUNTER's icon order (calls 132-138); the lobby's hunter arrows step the same order
        private static readonly Hunter[] HunterOrder =
            { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Trace, Hunter.Noxus, Hunter.Sylux, Hunter.Weavel };
        // the ROM's bot names, found by text
        private static readonly Dictionary<Hunter, string> BotNames = new()
        {
            [Hunter.Samus] = "sambot", [Hunter.Kanden] = "kanbot", [Hunter.Spire] = "spibot", [Hunter.Trace] = "trabot",
            [Hunter.Noxus] = "noxbot", [Hunter.Sylux] = "sybot", [Hunter.Weavel] = "weabot"
        };
        // decision 5 (owner 2026-10-07): a fixed default until the profile exists, never the device or OS user name
        public const string DefaultNickname = "HUNTER";

        private sealed class Seat
        {
            public bool Used, Bot, Ready;
            public Hunter Hunter = Hunter.Samus;
            public int Level; // 0-2 = one to three stars
            public int Team; // 0 / 1 (vanilla's bit set = team 1)
            public bool Remote; // a player on another device (LAN): the host's joiner, or on a joiner every other seat
            public string? Name; // a remote player's nickname
            public int Rank = -1; // a remote player's Hunter License rank level (LAN), -1 = not known
            public string? Skin, Character; // a remote player's MatchPlayer.Skin / Character (LAN), kept into START
            public int JoinerId; // the host's LAN joiner in this seat (LanSession.Joiners), 0 = none
            public int ColourPick = -1; // the seat's picked colour (MatchPlayer.ColourPick, LookAlikes.CopyIndices), -1 = auto
        }

        private sealed class Setup
        {
            public int Mode = 2;
            public bool ModeChosen; // the GAME MODE box shows it (a new CREATE GAME starts empty)
            public bool RulesSeen; // SETTINGS / ADVANCED / ARENA boxes filled (the rules page has been reached)
            public int Goal, TimeLimit, TimeGoal; // the values themselves: the goal, minutes, seconds
            public bool TeamPlay, AutoReset, Radar, Affinity, FriendlyFire;
            public int Damage = 1;
            public int Arena; // index into ArenaRooms
            // (P8: 8 seats; 5-8 used only past 4, MultiplayerMenu.Seats8.cs)
            public readonly Seat[] Seats = { new() { Used = true, Ready = true }, new(), new(), new(), new(), new(), new(), new() };
            public string Nickname = DefaultNickname;
            public Hunter MyHunter = Hunter.Samus; // this player's last SELECT HUNTER choice (seat 0 again after a LAN game)
            public string? MySkin, MyCharacter; // this player's last pick's look (D9; null = ORIGINAL)
            public int MyColour = -1; // this player's lobby colour pick (-1 = auto, the seat's own colour)
            public int Rank; // this player's Hunter License rank level (sent to the other devices)
        }

        private static Setup _setup = new();
        private static bool _defaultsSet;
        private static readonly Random _random = new();

        // the page the menus show when they come back from a match this menu started (-1: none); the shells ask
        // FrontendSession.ReturnPage, which takes it. RESULTS needs the match's results: a match left without them (quit
        // from the pause menu, an error) comes back to the lobby.
        private static int _pendingReturn = -1;
        public static int PendingReturn
        {
            get => _pendingReturn == ResultsPage && MatchResults.Last == null ? s_onlineResultsReturn >= 0 ? s_onlineResultsReturn : LobbyPage
                : _pendingReturn;
            set => _pendingReturn = value;
        }

        // ONLINE PLAY's matches (OnlineMenus.cs, 2026-10-09): counted as vanilla's Wi-Fi ones (owner
        // 2026-10-07: Wi-Fi = internet battles) and back on RESULTS like a LAN match; its PLAY AGAIN and QUIT go to
        // OnlineResultsDone (a match left without results: `returnPage`, the online hub). -1 = not an online match.
        private static int s_onlineResultsReturn = -1;
        public static bool OnlineResults => s_onlineResultsReturn >= 0; // (RESULTS is an online match's)
        // RESULTS' PLAY AGAIN (true) or QUIT after an online match: ONLINE PLAY takes it from there (OnlineMenus.OnlineAfterResults:
        // the same game's lobby again, or out of it); none: the return page
        public Action<MenuEngine, bool>? OnlineResultsDone { get; set; }

        public void ArmOnlineMatch(MatchSettings settings, int returnPage)
        {
            MatchResults.Last = null;
            ArmMatch(settings, wifi: true);
            s_onlineResultsReturn = returnPage;
            PendingReturn = _resultsBound ? ResultsPage : returnPage;
        }

        // ---- what Bind finds ----

        private MenuFile? _file;
        private MenuStrings? _strings;
        private readonly Dictionary<string, int> _text = new(); // ROM strings found by their text
        // group 7's shared placeholders (the same string ids on every setup page)
        private int _gameModeValue = -1, _arenaName = -1, _mapSize = -1;
        private readonly int[] _settingLabel = { -1, -1, -1, -1 }, _settingValue = { -1, -1, -1, -1 };
        private readonly int[] _advanced = { -1, -1, -1, -1 };
        private readonly int[] _players = { -1, -1, -1, -1 };
        // ADVANCED box values ("rad", "dam", "gun", "ff")
        private int _advRadar = -1, _advDamage = -1, _advWeapons = -1, _advFriendlyFire = -1;
        private int _arenaCount = -1; // "1/20"
        private readonly Dictionary<int, int[]> _rowValues = new(); // rules page -> its rows' value strings, top to bottom
        private int _endWireless = -1;
        // CHOOSE MULTIPLAYER TYPE's doors (decision 1 = A): the single-card icon, which draws the HUNTER LICENSE picture
        private int _licenseIconSpot = -1;

        // ---- binding ----

        private static bool HasWidget(MenuFile file, MenuItem it, string name) => it.States.Any(s => s.WidgetIndex >= 0
            && file.Widgets[s.WidgetIndex].ModelPath.EndsWith("\\" + name + "_Model.bin", StringComparison.OrdinalIgnoreCase));

        private static int FindItem(MenuPage page, Func<MenuItem, bool> match)
        {
            for (int i = 0; i < page.Items.Count; i++)
            {
                if (match(page.Items[i])) return i;
            }
            return -1;
        }

        private static IEnumerable<int> Calls(MenuItem it) => it.Actions.SelectMany(a => a.Calls.Select(c => c.B));
        private static bool PageCalls(MenuPage page, params int[] calls) =>
            calls.All(c => page.Items.Any(it => Calls(it).Contains(c)));

        private static int TextAt(MenuPage page, float x, float y) =>
            FindItem(page, it => it.IsText && MathF.Abs(it.X - x) < 1.5f && MathF.Abs(it.Y - y) < 1.5f);
        private static int StringOf(MenuPage page, int item) => item < 0 ? -1 : page.Items[item].States[0].Text!.StringId;

        public bool Bind(MenuFile file, MenuStrings strings)
        {
            _file = file;
            _strings = strings;
            if (file.Pages.Count <= LobbyPage) return false;
            MenuPage type = file.Pages[TypePage], create = file.Pages[CreateJoinPage], mode = file.Pages[ModePage];
            MenuPage hunter = file.Pages[HunterPage], lobby = file.Pages[LobbyPage];
            // every call this code answers, on the page the data should have it
            if (!PageCalls(type, 90, 91, 92) || !PageCalls(create, 94) || !PageCalls(mode, Modes.Select(m => m.Call).ToArray())
                || !PageCalls(hunter, 132, 133, 134, 135, 136, 137, 138) || !PageCalls(lobby, 146, 173, 176, 179, 182))
            {
                return false;
            }
            foreach (ModeInfo m in Modes)
            {
                MenuPage p = file.Pages[m.Page];
                if (!PageCalls(p, 109, 110, 113, 114)) return false;
                AddArtLayer(p); // (the typeable value box and its numpad: MultiplayerMenu.ValueEdit.cs)
                // the rows' values: texts in the value column, top to bottom
                int[] values = Enumerable.Range(0, p.Items.Count)
                    .Where(i => p.Items[i].IsText && MathF.Abs(p.Items[i].X - 222.4f) < 3 && p.Items[i].Y > 0 && p.Items[i].Y < 192)
                    .OrderByDescending(i => p.Items[i].Y).Select(i => StringOf(p, i)).ToArray();
                if (values.Length != m.Rules.Length) return false;
                _rowValues[m.Page] = values;
            }
            // the top screen's boxes, from the create page (the same strings on every group 7 page)
            _gameModeValue = StringOf(create, TextAt(create, 194, 357));
            _arenaName = StringOf(create, TextAt(create, 194, 333));
            _mapSize = StringOf(create, TextAt(create, 194, 325));
            float[] settingY = { 359, 331, 303, 275 };
            for (int r = 0; r < 4; r++)
            {
                _settingLabel[r] = StringOf(create, TextAt(create, 57, settingY[r]));
                _settingValue[r] = StringOf(create, TextAt(create, 57, settingY[r] - 10));
            }
            for (int r = 0; r < 4; r++) _advanced[r] = StringOf(create, AdvancedLine(create, r)); // (moved by BindExtraRules)
            float[] playerY = { 226, 216, 206, 196 };
            for (int r = 0; r < 4; r++) _players[r] = StringOf(create, TextAt(create, 194, playerY[r]));
            MenuPage battle = file.Pages[Modes[0].Page];
            _arenaCount = StringOf(battle, TextAt(battle, 84, 131));
            int ByText(MenuPage p, string text) => StringOf(p, FindItem(p, it => it.IsText && strings[it.States[0].Text!.StringId] == text));
            _advRadar = ByText(battle, "rad");
            _advDamage = ByText(battle, "dam");
            _advWeapons = ByText(battle, "gun");
            _advFriendlyFire = ByText(battle, "ff");
            int[] needed = { _gameModeValue, _arenaName, _mapSize, _arenaCount, _advRadar, _advDamage, _advWeapons, _advFriendlyFire };
            if (needed.Any(s => s < 0) || _settingLabel.Concat(_settingValue).Concat(_advanced).Concat(_players).Any(s => s < 0))
            {
                return false;
            }
            // the words the game writes, found by text (their ids differ between revisions)
            foreach (string t in new[] { "on", "off", "small", "medium", "large", "map size: ", "random", "radar off", "radar on",
                "damage low", "damage med", "damage high", "default weapons", "affinity weapons", "f.f. off", "f.f. on", "low",
                "med", "high", "battle", "bounty", "capture", "defender", "nodes", "prime hunter", "survival" }
                .Concat(BotNames.Values))
            {
                _text[t] = Text(strings, s => s == t);
            }
            _endWireless = Text(strings, s => s.StartsWith("this will end wireless communications", StringComparison.OrdinalIgnoreCase));
            for (int k = 0; k < 4; k++)
            {
                _rowBar[k] = FindItem(create, it => HasWidget(file, it, "redbar") && Calls(it).Contains(95 + k));
                _rowText[k] = FindItem(create, it => it.IsText && strings[it.States[0].Text!.StringId] == "game" + (k + 1));
            }
            BindValuePad(file, strings); // (before BindAddress swaps page 61 for its copy)
            BindAddress(file, strings, create);
            BindKeyboard(file, strings);
            BindCard(file, strings);
            BindHunterLocks(file);
            BindPicker(file, strings);
            BindStarBars(file);
            BindSeats8(file, strings); // (P8: after the star bars, which the copies take)
            BindExtraRules(file, strings); // (the ADVANCED box's EXTRA PICKUPS / EXTRA CHARACTERS rows)
            BindMatchMusic(file, strings); // (the rules pages' MUSIC row, MultiplayerMenu.MatchMusic.cs)
            BindModeDescriptions(file); // (in a box under the mode panel, MultiplayerMenu.ModeDescription.cs)
            BindDoors(file, strings, type);
            BindReplays(file, strings, type);
            _resultsBound = BindResults(file, strings);
            Bound = true;
            return true;
        }

        // Decision 1 = A (owner 2026-10-07 10:30): the three doors by where the players are, plus the profile. Vanilla's
        // icons, highlights and focus links (the doors' order is ours: MoveDoors); the labels and focus descriptions become ours, in the game's
        // font (lower case = its small capitals), written over page 25's own strings (found by text; only page 25 shows
        // them, plus the never-reached single-card and rival radar info pages):
        //   single-card spot -> HUNTER LICENSE (the profile: license, friends and rivals; M6), page 52's friends button with our picture
        //   multi-card spot  -> LOCAL PLAY (page 26: CREATE GAME = host, AVAILABLE GAMES = join), vanilla's description
        //   Wi-Fi spot       -> ONLINE PLAY (page 52's pages when online play exists)
        private void BindDoors(MenuFile file, MenuStrings strings, MenuPage type)
        {
            MoveDoors(file, strings, type); // (finds the labels by the ROM's text, so before they're filled below)
            int licenseId = Text(strings, s => s == "hunter license");
            string license = licenseId >= 0 ? strings[licenseId] : "hunter license";
            (string Rom, string Ours)[] texts =
            {
                ("single-card play", license),
                ("multi-card play", "local play"),
                ("nintendo wi-fi connection", "online play"),
                ("allow up to 3 other players to download", "view your hunter license and your roster of friends and rivals."),
                ("hunt anyone in the world with nintendo wi-fi connection", "hunt anyone in the world online."),
            };
            foreach ((string rom, string ours) in texts)
            {
                int item = FindItem(type, it => it.IsText && strings[it.States[0].Text!.StringId]
                    .StartsWith(rom, StringComparison.OrdinalIgnoreCase));
                if (item >= 0) strings.Fill(StringOf(type, item), ours);
            }
            _licenseIconSpot = FindItem(type, it => HasWidget(file, it, "singlecart"));
        }

        // The doors' order (owner 2026-10-07 23:19: "i want hunter license third and local first and online second"):
        // LOCAL PLAY moves to the single-card spot, ONLINE PLAY to the multi-card spot, HUNTER LICENSE to the Wi-Fi spot.
        // Each door's pieces move together (icon, highlight with its touch area, label; LOCAL PLAY's DS icon too), by the
        // distance between the spots' own highlights and labels. The D-pad finds doors by position (MenuEngine.Navigate)
        // and the first focus is the top-left one, so both follow; the descriptions share one box and stay.
        private void MoveDoors(MenuFile file, MenuStrings strings, MenuPage type)
        {
            int Ring(int call) => FindItem(type, it => HasWidget(file, it, "big_highlight") && Calls(it).Contains(call));
            int Label(string rom) => FindItem(type, it => it.IsText && strings[it.States[0].Text!.StringId]
                .StartsWith(rom, StringComparison.OrdinalIgnoreCase));
            // vanilla's spots, left to right: single-card (call 90), multi-card (91), Wi-Fi (92)
            int[] icons = { FindItem(type, it => HasWidget(file, it, "singlecart")), FindItem(type, it => HasWidget(file, it, "multiplayer")),
                FindItem(type, it => HasWidget(file, it, "wifi")) };
            int[] rings = { Ring(90), Ring(91), Ring(92) };
            int[] labels = { Label("single-card play"), Label("multi-card play"), Label("nintendo wi-fi connection") };
            int dsIcon = FindItem(type, it => HasWidget(file, it, "wifiicon"));
            if (icons.Concat(rings).Concat(labels).Any(i => i < 0))
            {
                Log?.Invoke("multiplayer: the doors stay in vanilla's order (page 25 isn't the expected layout)");
                return;
            }
            int[] to = { 2, 0, 1 }; // the spot each door moves to: HUNTER LICENSE, LOCAL PLAY, ONLINE PLAY
            float[] dx = Enumerable.Range(0, 3).Select(d => type.Items[rings[to[d]]].X - type.Items[rings[d]].X).ToArray();
            float[] labelDx = Enumerable.Range(0, 3).Select(d => type.Items[labels[to[d]]].X - type.Items[labels[d]].X).ToArray();
            void Move(int item, float by)
            {
                MenuItem src = type.Items[item];
                type.ReplaceItem(item, i => RecompMenus.Clone(src, i, by, 0, links: src.Links, actions: src.Actions));
            }
            for (int d = 0; d < 3; d++)
            {
                Move(icons[d], dx[d]);
                Move(rings[d], dx[d]);
                Move(labels[d], labelDx[d]);
            }
            if (dsIcon >= 0) Move(dsIcon, dx[1]);
        }

        // M4 (decision 3, owner 2026-10-07): REPLAYS in RIVAL RADAR's corner, built like the file screen's MOVIES (page 24: the
        // film-reel icon, its small highlight and the "movies" label, which sit in the same corner), the radar hidden. The
        // highlight takes the radar bar's item index, so the radar's focus links (its description) and its press (call 93)
        // carry over; the description is ours. Until Replay mode's replay list (R3) a press opens WORK IN PROGRESS.
        private void BindReplays(MenuFile file, MenuStrings strings, MenuPage type)
        {
            if (file.Pages.Count <= FileSelect.Page) return;
            MenuPage files = file.Pages[FileSelect.Page];
            int radar = FindItem(type, it => HasWidget(file, it, "rivalradar") && Calls(it).Contains(93));
            int radarText = FindItem(type, it => it.IsText && strings[it.States[0].Text!.StringId] == "rival radar");
            int radarInfo = FindItem(type, it => it.IsText && strings[it.States[0].Text!.StringId]
                .StartsWith("find new rivals", StringComparison.OrdinalIgnoreCase));
            int icon = FindItem(files, it => HasWidget(file, it, "movies"));
            int highlight = FindItem(files, it => HasWidget(file, it, "small_highlight")
                && it.Actions.Any(a => a.TargetPage != 0xFF && a.Calls.Count == 0));
            int label = FindItem(files, it => it.IsText && strings[it.States[0].Text!.StringId] == "movies");
            if (radar < 0 || icon < 0 || highlight < 0 || label < 0)
            {
                Log?.Invoke("multiplayer: no REPLAYS button (this ROM's MOVIES or RIVAL RADAR pieces aren't where expected)");
                return;
            }
            MenuItem radarItem = type.Items[radar], highlightItem = files.Items[highlight];
            MenuAction press = radarItem.Actions.First(a => a.Calls.Any(c => c.B == 93));
            MenuAction movies = highlightItem.Actions.First(a => a.TargetPage != 0xFF);
            var action = new MenuAction
            {
                Kind = movies.Kind, Field2 = movies.Field2, Flags = movies.Flags, Field4 = movies.Field4, RectX = movies.RectX,
                RectY = movies.RectY, RectW = movies.RectW, RectH = movies.RectH, Calls = press.Calls, Item = (ushort)radar,
                TargetPage = 0xFF, Field17 = movies.Field17
            };
            type.ReplaceItem(radar, i => RecompMenus.Clone(highlightItem, i, 0, 0, links: radarItem.Links, actions: new[] { action }));
            MenuItem iconItem = files.Items[icon], labelItem = files.Items[label];
            type.AddItem(i => RecompMenus.Clone(iconItem, i, 0, 0));
            int replays = strings.Add("replays");
            type.AddItem(i => RecompMenus.Clone(labelItem, i, 0, 0, stringId: replays));
            if (radarText >= 0) strings.Fill(StringOf(type, radarText), "");
            if (radarInfo >= 0) strings.Fill(StringOf(type, radarInfo), "watch replays of your recent matches.");
        }

        // The HUNTER LICENSE door's own picture (owner 2026-10-08: "it should follow the sort of sepia style that all the other
        // doors have", then the three stars, "g3 is good"): the single-card door keeps its triangles, UVs and states (so it
        // slides in and out with the other doors, owner 2026-10-29) and draws LicenseDoorPicture's texture, which has that
        // button's layout, instead of its own.
        private void UseLicenseDoorPicture(MenuEngine menu, List<WidgetTri> tris, int start)
        {
            if (_licenseIconSpot < 0 || _licenseIconSpot >= menu.Page!.Items.Count) return;
            int texture = menu.Widgets.Textures.GetOrAdd(LicenseDoorPicture.Key, LicenseDoorPicture.Draw).Id;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != _licenseIconSpot) continue;
                WidgetTri tri = tris[t];
                tri.TextureId = texture;
                tris[t] = tri;
            }
        }

        // searches the ROM's text as it was before any placeholder was filled (Fill writes into the ROM's own ids, so a live
        // search could find a box's current contents: an imported arena's name found in the ARENA box itself, after which
        // the box showed whatever it held, i.e. the arena before)
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuStrings, string[]> _romText = new();

        private static int Text(MenuStrings strings, Func<string, bool> match)
        {
            string[] rom = _romText.GetValue(strings, s => Enumerable.Range(0, s.RomCount).Select(i => s[i]).ToArray());
            for (int i = 0; i < rom.Length; i++)
            {
                if (match(rom[i])) return i;
            }
            return -1;
        }

        // a ROM word by its English text (falls back to the text itself)
        private string Word(string english) =>
            _text.TryGetValue(english, out int id) && id >= 0 ? _strings![id] : english;

        // ---- the setup's rules ----

        private static ModeInfo CurrentMode => Modes.First(m => m.Id == _setup.Mode);

        private static int[] GoalTable(Rule rule) => rule switch
        {
            Rule.PointGoal => BattleGoals,
            Rule.NodesGoal => NodesGoals,
            Rule.OctolithGoal => OctolithGoals,
            _ => LivesValues
        };

        // the mode's goal row (Lives' table for a mode without one)
        private static Rule GoalRule(ModeInfo m) =>
            m.Rules.FirstOrDefault(r => r is Rule.PointGoal or Rule.NodesGoal or Rule.OctolithGoal or Rule.Lives, Rule.Lives);

        // a rule value brought into its table's range
        private static int ClampTo(int[] table, int value) => Math.Clamp(value, table[0], table[^1]);

        // a different mode resets every rule to its defaults and the arena to the mode's first (0x2136414); the same mode
        // keeps them
        private static void PickMode(int id)
        {
            if (_defaultsSet && _setup.Mode == id) return;
            _setup.Mode = id;
            _defaultsSet = true;
            ModeInfo m = CurrentMode;
            _setup.Goal = GoalTable(GoalRule(m))[m.GoalDefault];
            _setup.TimeLimit = TimeLimitMinutes[m.TimeLimitDefault];
            _setup.TimeGoal = TimeGoalSeconds[TimeGoalDefault];
            _setup.TeamPlay = m.Solo == GameMode.Capture;
            _setup.AutoReset = m.Rules.Contains(Rule.AutoReset);
            _setup.Radar = false;
            _setup.Damage = 1;
            _setup.Affinity = false;
            _setup.FriendlyFire = false;
            _setup.Arena = 0;
            if (!ArenaAllowed(_setup.Arena)) StepArena(+1);
        }

        // the mode's arenas that are open (vanilla 0x02136C00: locked ones are left out of the arrows; RANDOM and imported
        // arenas always open)
        private static bool ArenaAllowed(int index) => ArenaModes[index][_setup.Mode - 2] == '1' && (_unlocks?.ArenaOpen(ArenaRooms[index]) ?? true);

        // M5: the imported arenas (MatchArenas.All past the retail ids: Echoes, once IMPORT GAMES has them) go in before
        // random, with the modes their entity data supports (MatchArenas' census: today battle, prime hunter, survival);
        // the chosen arena stays chosen when the list changes
        private void RefreshArenas()
        {
            List<RoomMetadata> imported;
            try
            {
                // (the ADVANCED box's EXTRA ARENAS rule off: none; a joiner's setup shows the host's game, whatever its own)
                imported = MatchArenas.All.Where(r => r.Id > MatchArenas.LastId && (_setupFromHost || s_extraRules.Arenas) && !MphRecomp.Arenas.FirstHuntArena.IsDevRoom(r.Name)).ToList();
            }
            catch (Exception ex)
            {
                Log?.Invoke("multiplayer: no imported arenas: " + ex.Message);
                imported = new List<RoomMetadata>();
            }
            int[] rooms = VanillaArenaRooms[..^1].Concat(imported.Select(r => r.Id)).Append(RandomArena).ToArray();
            if (rooms.SequenceEqual(ArenaRooms)) return;
            string[] modes = VanillaArenaModes[..^1].Concat(imported.Select(ImportedModes)).Append(VanillaArenaModes[^1]).ToArray();
            int chosen = ArenaRooms[Math.Clamp(_setup.Arena, 0, ArenaRooms.Length - 1)];
            ArenaRooms = rooms;
            ArenaModes = modes;
            _setup.Arena = Math.Max(0, Array.IndexOf(rooms, chosen));
            if (_setup.Mode >= 2 && !ArenaAllowed(_setup.Arena)) StepArena(+1);
            Log?.Invoke($"multiplayer: {imported.Count} imported arena(s) in the list"
                + String.Concat(imported.Select((r, i) => $", {r.InGameName} {modes[VanillaArenaRooms.Length - 1 + i]}")));
        }

        private string ImportedModes(RoomMetadata room)
        {
            try
            {
                return String.Concat(ArenaModeColumns.Select(m => MatchArenas.Supports(room, m) ? '1' : '0'));
            }
            catch (Exception ex)
            {
                Log?.Invoke($"multiplayer: {room.InGameName} census failed ({ex.Message}): battle only");
                return "1000000";
            }
        }

        private static void StepArena(int step)
        {
            for (int n = 0; n < ArenaRooms.Length; n++)
            {
                _setup.Arena = (_setup.Arena + step + ArenaRooms.Length) % ArenaRooms.Length;
                if (ArenaAllowed(_setup.Arena)) return;
            }
        }

        private static int Wrap(int value, int step, int count) => ((value + step) % count + count) % count;

        // one step of a value row: wraps past an end as vanilla's rows do, except while an arrow is held (it stops there)
        private static int StepValue(int value, int step, int min, int max)
        {
            int next = value + step;
            if (next > max) return HeldRepeat ? max : min;
            if (next < min) return HeldRepeat ? min : max;
            return next;
        }

        // true while FrontendSession repeats a held arrow
        internal static bool HeldRepeat;

        // set when a press stepped a numeric rule (FrontendSession: only those arrows repeat while held)
        internal bool RuleStepped;

        // the time goal's step: 0.1 minute, the unit the row shows
        private const int TimeGoalStep = 6;

        // set when a rule's value really changed (a held repeat at its row's end leaves it: FrontendSession drops that step's sound)
        internal bool RuleMoved;

        private void StepRule(Rule rule, int step)
        {
            var before = (_setup.Goal, _setup.TimeLimit, _setup.TimeGoal);
            StepRuleValue(rule, step);
            if (before != (_setup.Goal, _setup.TimeLimit, _setup.TimeGoal)) RuleMoved = true;
        }

        private void StepRuleValue(Rule rule, int step)
        {
            switch (rule)
            {
            case Rule.PointGoal:
            case Rule.NodesGoal:
            case Rule.OctolithGoal:
            case Rule.Lives:
                int[] table = GoalTable(rule);
                _setup.Goal = StepValue(ClampTo(table, _setup.Goal), step, table[0], table[^1]);
                RuleStepped = true;
                break;
            case Rule.TimeLimit:
                _setup.TimeLimit = StepValue(ClampTo(TimeLimitMinutes, _setup.TimeLimit), step, TimeLimitMinutes[0], TimeLimitMinutes[^1]);
                RuleStepped = true;
                break;
            case Rule.TimeGoal:
                int seconds = ClampTo(TimeGoalSeconds, _setup.TimeGoal) / TimeGoalStep * TimeGoalStep;
                _setup.TimeGoal = StepValue(seconds, step * TimeGoalStep, TimeGoalSeconds[0], TimeGoalSeconds[^1]);
                RuleStepped = true;
                break;
            case Rule.TeamPlay:
                if (CurrentMode.Solo is GameMode.Capture or GameMode.PrimeHunter) return; // no handler in those modes
                _setup.TeamPlay = !_setup.TeamPlay;
                break;
            case Rule.AutoReset:
                _setup.AutoReset = !_setup.AutoReset;
                break;
            }
        }

        private static string Minutes(int seconds) => seconds % 60 == 0
            ? (seconds / 60).ToString(CultureInfo.InvariantCulture)
            : $"{seconds / 60}.{seconds % 60 * 10 / 60}";

        private string RuleValue(Rule rule) => rule switch
        {
            Rule.TimeLimit => _setup.TimeLimit.ToString(CultureInfo.InvariantCulture),
            Rule.TimeGoal => Minutes(_setup.TimeGoal),
            Rule.TeamPlay => (_setup.TeamPlay ? Word("on") : Word("off")).ToUpperInvariant(),
            Rule.AutoReset => (_setup.AutoReset ? Word("on") : Word("off")).ToUpperInvariant(),
            _ => ClampTo(GoalTable(rule), _setup.Goal).ToString(CultureInfo.InvariantCulture)
        };

        // the row labels as the page prints them ("POINT GOAL\n\nTIME LIMIT\n\nTEAM PLAY")
        private string[] RuleLabels(ModeInfo m)
        {
            MenuPage p = _file!.Pages[m.Page];
            foreach (MenuItem it in p.Items)
            {
                if (!it.IsText) continue;
                string s = _strings![it.States[0].Text!.StringId];
                string[] parts = s.Split("\n\n");
                if (parts.Length >= m.Rules.Length && parts.Length <= 4 && s.Contains("TIME LIMIT", StringComparison.Ordinal))
                {
                    return parts.Take(m.Rules.Length).ToArray();
                }
            }
            return m.Rules.Select(r => r.ToString().ToUpperInvariant()).ToArray();
        }

        private string ArenaName(int index)
        {
            int room = ArenaRooms[index];
            if (room == RandomArena) return Word("random");
            RoomMetadata? meta = Metadata.GetRoomById(room, noThrow: true);
            return ArenaWord(meta?.InGameName ?? "");
        }

        // an arena name as the ROM writes it: lower case (the small capitals). The ROM's own string, when it has one, is
        // this same text, so there's nothing to look up (the old lookup cached the ARENA box's own id: see Text)
        private static string ArenaWord(string name) => name.ToLowerInvariant();

        private string MapSize(int index)
        {
            int room = ArenaRooms[index];
            if (!ArenaSize.TryGetValue(room, out int size))
            {
                // an imported arena: its room table entry's size (the same field; Echoes arenas register as large)
                RoomSize? own = room == RandomArena ? null : Metadata.GetRoomById(room, noThrow: true)?.Size;
                if (own is not (RoomSize.Small or RoomSize.Medium or RoomSize.Large)) return "";
                size = (int)own;
            }
            return Word("map size: ") + Word(size <= 2 ? "small" : size == 3 ? "medium" : "large");
        }

        private (int N, int Count) ArenaPosition()
        {
            int count = 0, n = 0;
            for (int i = 0; i < ArenaRooms.Length; i++)
            {
                if (!ArenaAllowed(i)) continue;
                count++;
                if (i == _setup.Arena) n = count;
            }
            return (n, count);
        }

        // ---- the top screen's boxes and the rows ----

        private void FillStrings()
        {
            MenuStrings s = _strings!;
            ModeInfo m = CurrentMode;
            s.Fill(_gameModeValue, _setup.ModeChosen ? Word(m.Name) : ""); // lower case as the ROM has it: the small capitals
            bool rules = _setup.RulesSeen;
            string[] labels = RuleLabels(m);
            for (int r = 0; r < 4; r++)
            {
                bool row = rules && r < m.Rules.Length;
                s.Fill(_settingLabel[r], row ? labels[r] : "");
                s.Fill(_settingValue[r], row ? RuleValue(m.Rules[r]) : "");
            }
            // (Prime Hunter has no friendly fire row in the ADVANCED box, but this box still lists it: BizHawk)
            string[] adv =
            {
                Word(_setup.Radar ? "radar on" : "radar off"),
                Word(_setup.Damage == 0 ? "damage low" : _setup.Damage == 1 ? "damage med" : "damage high"),
                Word(_setup.FriendlyFire ? "f.f. on" : "f.f. off"),
                Word(_setup.Affinity ? "affinity weapons" : "default weapons"),
            };
            for (int r = 0; r < 4; r++) s.Fill(_advanced[r], rules ? adv[r] : "");
            s.Fill(_arenaName, rules ? ArenaName(_setup.Arena) : "");
            s.Fill(_mapSize, rules ? MapSize(_setup.Arena) : "");
            (int n, int count) = ArenaPosition();
            s.Fill(_arenaCount, $"{n}/{count}");
            if (_rowValues.TryGetValue(m.Page, out int[]? values))
            {
                for (int r = 0; r < values.Length; r++) s.Fill(values[r], RuleValue(m.Rules[r]));
            }
            // the ADVANCED box
            s.Fill(_advRadar, (_setup.Radar ? Word("on") : Word("off")).ToUpperInvariant());
            s.Fill(_advDamage, Word(_setup.Damage == 0 ? "low" : _setup.Damage == 1 ? "med" : "high").ToUpperInvariant());
            s.Fill(_advWeapons, Word(_setup.Affinity ? "affinity weapons" : "default weapons"));
            s.Fill(_advFriendlyFire, (_setup.FriendlyFire ? Word("on") : Word("off")).ToUpperInvariant());
            FillExtraRules(s, rules);
            FillMatchMusic(s);
            // PLAYERS: every used seat, in seat order (the lobby's name texts are the same strings; P8: seats 5-8 too)
            for (int r = 0; r < _setup.Seats.Length; r++)
            {
                Seat seat = _setup.Seats[r];
                bool waiting = _setupFromHost && _joinState != 2; // a joiner before the host's answer: no seats yet
                int id = r < 4 ? _players[r] : _seats8.Strings[r - 4];
                if (id < 0) continue;
                string name = !seat.Used || waiting ? "" : seat.Bot ? BotWord(seat)
                    : seat.Remote ? (seat.Name ?? "").ToLowerInvariant() : StreamerPrivacy.Name(_setup.Nickname, mine: true);
                if (r < 4) s.Fill(id, name);
                else s.Set(id, name); // (seats 5-8: our strings)
            }
        }

        // ---- page entry ----

        private int _pendingInto = -1; // ticks left before the loading page gives way to the fade into the game
        private const int IntoGameHold = 12, IntoGameFade = 10; // page 17: ticks before the fade to black, and its length
        private int _intoTicks = -1;
        // the fade to black into a match: -1 none, else 0..1 (FrontendSession draws it; the app fades the music out with it)
        public float IntoGameDim { get; private set; } = -1;

        // true while this menu is the pause menu over a running game (FrontendSession.Paused): it leaves LAN sessions be
        public Func<bool>? OverGame;

        public void OnPageEntered(MenuEngine menu, int page)
        {
            if (!Bound) return;
            if (page != IntoGamePage) IntoGameDim = _intoTicks = -1;
            if (page == LoadingPage && _pendingInto == -2)
            {
                _pendingInto = IntoGameTicks;
                return;
            }
            if (page == IntoGamePage && _pendingInto == -3)
            {
                // vanilla (BizHawk US rev 1, C1 2026-10-24): page 17 holds ~12 ticks, then the music fades out (StopMusic
                // fade 20 frames) while both screens dim to black over 10 ticks, and the game starts once they are black.
                // The page's own intogamefade item (33 ticks, from entry) stays off: IntoGameDim draws the dim instead
                _pendingInto = -1;
                _intoTicks = 0;
                IntoGameDim = 0;
                for (int i = 0; i < menu.Page!.Items.Count; i++)
                {
                    if (menu.Page.Items[i].States.Any(st => st.WidgetIndex >= 0
                        && menu.File.Widgets[st.WidgetIndex].ModelPath.Contains("intogamefade", StringComparison.OrdinalIgnoreCase)))
                    {
                        menu.SetState(i, MenuState.Hidden);
                    }
                }
                return;
            }
            // a LAN session lives on its own pages (the host's lobby; the joiner's list, hunter and lobby) and on the way into
            // the match (above): anywhere else it ends. Not from the in-match pause menu's own session (OverGame): the match
            // screen plays that LanSession, and ending it here sent the joiner BYE (owner 2026-10-11: host pause -> guest
            // kicked to results); the match screen closes it itself when it goes
            if (Lan != null && OverGame?.Invoke() != true && !(Hosting ? page == LobbyPage : page is CreateJoinPage or JoinHunterPage or JoinLobbyPage or AddressPage))
            {
                EndLan();
            }
            if (page == GameEnding.GoodPage) GoodEnding(); // (only the good ending's results page: vanilla ov1 0x021045C8)
            if (!Owns(page)) return;
            MenuPage p = menu.Page!;
            Panel8(menu); // (P8: 5-8 seats, the PLAYERS box in two columns)
            UpdateLocks();
            if (page is TypePage or CreateJoinPage) EndOnlineSetup(); // (ONLINE PLAY's CREATE left)
            if (s_onlineEdit && page >= ModePage && page < HunterPage) s_onlineRulesEdited = true; // (the host's lobby takes them)
            if (s_onlineHunterOnly && page >= ModePage && page < HunterPage)
            {
                menu.GoTo(HunterPage); // (ONLINE PLAY's SELECT HUNTER on its own: a step tab the data still leads away)
                return;
            }
            if (page == TypePage)
            {
                RefreshUnlocks(); // the campaign's files since the last visit
            }
            if (page == CreateJoinPage)
            {
                RefreshArenas(); // arenas imported since the last visit
                if (_setup.Mode >= 2 && !ArenaAllowed(_setup.Arena)) StepArena(+1); // (UNLOCKS back to vanilla)
                EnterCreateJoin(menu, p);
            }
            if (page is HunterPage or JoinHunterPage) ShowHunterLocks(menu, page);
            EnterPicker(menu, page);
            _valEdit = null;
            if (page == AddressPage) EnterAddress(menu);
            if (page == NamePage) EnterName(menu);
            if (page == LicensePage) EnterCard(menu);
            if (page >= CreateJoinPage && page <= JoinLobbyPage)
            {
                ShowMatchMusic(menu, page); // (before FillStrings: its text is cut with the menu's font)
                FillStrings();
                Tabs(menu, p, page);
                Preview(menu, p);
            }
            if (page == LobbyPage) Lobby(menu, entering: true);
            if (page == JoinLobbyPage)
            {
                _joinShown = false;
                JoinLobby(menu, entering: true);
            }
            if (page == ResultsPage)
            {
                CountMatch();
                Results(menu, entering: true);
            }
        }

        public void Tick(MenuEngine menu)
        {
            EnterNext(menu);
            ResultsTick(menu);
            LanTick(menu);
            TickTime();
            WarmPictures(menu); // (the D9 picker's pictures, drawn ahead)
            TickLooks(menu); // (the D9 lobby pictures' fades)
            if (_pendingInto > 0 && --_pendingInto == 0)
            {
                _pendingInto = -3;
                menu.GoTo(IntoGamePage);
            }
            if (_intoTicks >= 0 && menu.Page?.Index == IntoGamePage)
            {
                _intoTicks++;
                IntoGameDim = Math.Clamp((_intoTicks - IntoGameHold) / (float)IntoGameFade, 0, 1);
                if (_intoTicks == IntoGameHold + IntoGameFade + 1)
                {
                    _intoTicks = -1; // (IntoGameDim stays 1, black, until a page is entered again)
                    StartMatch(menu);
                }
            }
        }

        // CREATE OR JOIN: listen for LAN games; AVAILABLE GAMES shows the ones heard (Rows, every half second)
        private void EnterCreateJoin(MenuEngine menu, MenuPage p)
        {
            StartBrowse();
            Rows(menu, entering: true);
        }

        // the MODE / ARENA / HUNTER tabs across the touch screen's top: the steps done so far, the current one lit
        private void Tabs(MenuEngine menu, MenuPage p, int page)
        {
            int step = page == ModePage ? 0 : page >= 28 && page <= 34 ? 1 : page == HunterPage ? 2 : page == LobbyPage ? 3 : -1;
            if (step < 0) return;
            int[] tabs = Enumerable.Range(0, p.Items.Count).Where(i => HasWidget(_file!, p.Items[i], "tab"))
                .OrderBy(i => p.Items[i].X).ToArray();
            int[] dots = Enumerable.Range(0, p.Items.Count).Where(i => HasWidget(_file!, p.Items[i], "bluedot"))
                .OrderBy(i => p.Items[i].X).ToArray();
            int[] labels = Enumerable.Range(0, p.Items.Count).Where(i => p.Items[i].IsText && p.Items[i].Y > 177 && p.Items[i].Y < 178.5f)
                .OrderBy(i => p.Items[i].X).ToArray();
            for (int t = 0; t < tabs.Length && t < 3; t++)
            {
                if (t > step) continue;
                menu.SetState(tabs[t], t == step ? MenuState.Selected : MenuState.Idle);
                if (t < labels.Length) menu.SetState(labels[t], MenuState.Idle);
                if (t < dots.Length && t < step) menu.SetState(dots[t], MenuState.Idle);
            }
        }

        // the arena's preview on the top screen (the game puts the arena's stage picture in the screenshot frame); an
        // imported arena (M5) shows its own picture (thumb.png, rendered at import: Arenas/ArenaThumbnail) on the frame of
        // the ROM's random picture
        private int _previewItem = -1, _previewWidget = -1;
        private string? _previewPicture;

        private void Preview(MenuEngine menu, MenuPage p)
        {
            _previewItem = FindItem(p, it => HasWidget(_file!, it, "screenshot"));
            _previewWidget = -1;
            if (_previewItem < 0 || !_setup.RulesSeen) return;
            MenuItemState? shot = p.Items[_previewItem].States.FirstOrDefault(s => s.WidgetIndex >= 0);
            if (shot == null) return;
            string path = _file!.Widgets[shot.WidgetIndex].ModelPath;
            int room = ArenaRooms[_setup.Arena];
            string? stage = StageModel(path, room);
            _previewPicture = stage == null ? ImportedPicture(room) : null;
            if (stage == null) stage = StageModel(path, RandomArena); // (an imported arena with no picture yet: random's)
            if (stage == null) return;
            _previewWidget = _file.WidgetIndex(stage, null);
            menu.SetState(_previewItem, MenuState.Idle);
        }

        // an imported arena's picture, when its import made one (beside its HD look)
        internal static string? ImportedPicture(int room)
        {
            RoomMetadata? meta = room == RandomArena ? null : Metadata.GetRoomById(room, noThrow: true);
            string? dir = meta == null ? null : MphRecomp.Arenas.EchoesArena.HdDir(meta.Name);
            string? file = dir == null ? null : System.IO.Path.Combine(dir, MphRecomp.Arenas.ArenaThumbnail.FileName);
            return file != null && System.IO.File.Exists(file) ? file : null;
        }

        // the imported picture over the random picture's triangles: its texture instead, the frame's UVs spread over it
        private void UsePicture(MenuEngine menu, List<WidgetTri> picture)
        {
            string file = _previewPicture!;
            UiTexture tex;
            try
            {
                tex = menu.Widgets.Textures.GetOrAdd($"arena picture {file} {System.IO.File.GetLastWriteTimeUtc(file).Ticks}", () =>
                {
                    byte[] px = MphRecomp.Import.PngOut.Read(file, out int w, out int h);
                    return (w, h, px);
                });
            }
            catch (Exception ex)
            {
                Log?.Invoke("multiplayer: no arena picture: " + ex.Message);
                _previewPicture = null;
                return;
            }
            float u0 = picture.Min(t => MathF.Min(t.A.U, MathF.Min(t.B.U, t.C.U))), u1 = picture.Max(t => MathF.Max(t.A.U, MathF.Max(t.B.U, t.C.U)));
            float v0 = picture.Min(t => MathF.Min(t.A.V, MathF.Min(t.B.V, t.C.V))), v1 = picture.Max(t => MathF.Max(t.A.V, MathF.Max(t.B.V, t.C.V)));
            if (u1 - u0 < 1e-6f || v1 - v0 < 1e-6f) return;
            UiVertex Spread(UiVertex v)
            {
                v.U = (v.U - u0) / (u1 - u0);
                v.V = (v.V - v0) / (v1 - v0);
                return v;
            }
            for (int t = 0; t < picture.Count; t++)
            {
                WidgetTri tri = picture[t];
                tri.A = Spread(tri.A); tri.B = Spread(tri.B); tri.C = Spread(tri.C);
                tri.TextureId = tex.Id;
                tri.WrapS = tri.WrapT = UiWrap.Clamp;
                picture[t] = tri;
            }
        }

        // stage/<name>_Model.bin for a room: its entity file's name (mp1, mp4_dm1, ctf1_dm1...), or that without "_dm"
        // and underscores (unit1land, goreab2); random = stage/random
        internal static string? StageModel(string screenshotPath, int room)
        {
            string folder = screenshotPath[..(screenshotPath.LastIndexOf('\\') + 1)];
            if (room == RandomArena) return folder + "random_Model.bin";
            RoomMetadata? meta = Metadata.GetRoomById(room, noThrow: true);
            if (meta?.EntityPath == null) return null;
            // the game's paths use backslashes, which Path.GetFileName only splits on Windows (Android kept the whole path)
            string entity = meta.EntityPath[(meta.EntityPath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
            string name = entity.EndsWith("_Ent.bin", StringComparison.OrdinalIgnoreCase) ? entity[..^8] : entity;
            name = name.ToLowerInvariant();
            if (StageNames.Contains(name)) return folder + name + "_Model.bin";
            int dm = name.IndexOf("_dm", StringComparison.Ordinal);
            if (dm >= 0) name = name[..dm];
            name = name.Replace("_", "");
            return StageNames.Contains(name) ? folder + name + "_Model.bin" : null;
        }

        // the ROM's stage pictures (frontend stage/ folder)
        private static readonly HashSet<string> StageNames = new()
        {
            "ad1", "ad1_dm1", "ad2", "ad2_dm1", "ctf1", "ctf1_dm1", "e3level", "goreab2", "mp1", "mp2", "mp3", "mp4", "mp4_dm1",
            "mp5", "mp6", "mp7", "mp8", "mp9", "mp10", "mp11", "mp12", "mp13", "mp14", "unit1land", "unit2land", "unit3land",
            "unit4land"
        };

        // FrontendSession's draw pass: the screenshot frame's triangles become the chosen arena's picture; on RESULTS the
        // bar across both DS screens (crossbar: the dark band under the name and place, and the table's header) is split
        // at the screens' edge, its top half drawn with the top screen's band (topbar), so the combined screen puts it
        // under the medal and the texts instead of above the table
        public void ReplacePreview(MenuEngine menu, List<WidgetTri> tris, int start)
        {
            if (Bound) DrawPicker(menu, tris, start);
            if (Bound && menu.Page?.Index == TypePage) UseLicenseDoorPicture(menu, tris, start);
            if (menu.Page != null && KeyboardOverCard(menu.Page.Index))
            {
                // the keyboard floats over the license: its own screen (the mask: a black panel and a grid over both DS
                // screens) and the framed panel behind the keys aren't drawn
                for (int t = tris.Count - 1; t >= start; t--)
                {
                    if (tris[t].Item == _keyMask || tris[t].Item == _keyDrop) tris.RemoveAt(t);
                }
            }
            if (Bound && menu.Page?.Index == ResultsPage && _crossbar >= 0 && _topbar >= 0)
            {
                var split = new List<WidgetTri>();
                for (int t = tris.Count - 1; t >= start; t--)
                {
                    if (tris[t].Item != _crossbar) continue;
                    split.Clear();
                    SplitAtScreenEdge(tris[t], split);
                    for (int k = 0; k < split.Count; k++)
                    {
                        WidgetTri piece = split[k];
                        if ((piece.A.Y + piece.B.Y + piece.C.Y) / 3 > 0) piece.Item = _topbar;
                        split[k] = piece;
                    }
                    tris.RemoveAt(t);
                    tris.InsertRange(t, split);
                }
                return;
            }
            if (!Bound || _previewItem < 0 || _previewWidget < 0 || menu.Page == null || !Owns(menu.Page.Index)) return;
            int at = -1;
            float z = 0;
            for (int t = tris.Count - 1; t >= start; t--)
            {
                if (tris[t].Item != _previewItem) continue;
                z = tris[t].Z;
                at = t;
                tris.RemoveAt(t);
            }
            if (at < 0) return;
            var picture = new List<WidgetTri>();
            try
            {
                menu.Widgets.Evaluate(_previewWidget, 0, loop: true, alpha: 1, picture);
            }
            catch (Exception ex)
            {
                Log?.Invoke("multiplayer: no arena picture: " + ex.Message);
                _previewWidget = -1;
                return;
            }
            if (_previewPicture != null && picture.Count > 0) UsePicture(menu, picture);
            MenuItem item = menu.Page.Items[_previewItem];
            for (int t = 0; t < picture.Count; t++)
            {
                WidgetTri tri = picture[t];
                tri.A.X += item.X; tri.B.X += item.X; tri.C.X += item.X;
                tri.A.Y += item.Y; tri.B.Y += item.Y; tri.C.Y += item.Y;
                tri.Item = _previewItem;
                tri.Z = z;
                picture[t] = tri;
            }
            tris.InsertRange(at, picture);
        }

        // A triangle cut along y = 0 (the DS screens' edge in menu space): 1 piece if it doesn't cross it, else 3
        private static void SplitAtScreenEdge(WidgetTri tri, List<WidgetTri> output)
        {
            UiVertex[] v = { tri.A, tri.B, tri.C };
            int above = v.Count(x => x.Y > 0), below = v.Count(x => x.Y < 0);
            if (above == 0 || below == 0)
            {
                output.Add(tri);
                return;
            }
            static UiVertex Cut(UiVertex a, UiVertex b)
            {
                float t = a.Y / (a.Y - b.Y);
                return new UiVertex(a.X + (b.X - a.X) * t, 0, a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t,
                    a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
            }
            // the lone vertex on its side of the edge, then the other two in winding order
            bool loneAbove = above == 1;
            int lone = Array.FindIndex(v, x => loneAbove ? x.Y > 0 : x.Y < 0);
            UiVertex p = v[lone], q = v[(lone + 1) % 3], r = v[(lone + 2) % 3];
            UiVertex pq = Cut(p, q), pr = Cut(p, r);
            WidgetTri Make(UiVertex a, UiVertex b, UiVertex c)
            {
                WidgetTri w = tri;
                w.A = a; w.B = b; w.C = c;
                return w;
            }
            output.Add(Make(p, pq, pr));
            output.Add(Make(pq, q, r));
            output.Add(Make(pq, r, pr));
        }

        // ---- calls ----

        public bool OnCall(MenuEngine menu, MenuAction action, int b, int item)
        {
            if (!Bound || menu.Page == null || !Owns(menu.Page.Index)) return false;
            int page = menu.Page.Index;
            switch (page)
            {
            case TypePage:
                return OnTypeCall(menu, b);
            case ResultsPage:
                return OnResultsCall(menu, b);
            case AddressPage:
                return OnAddressCall(menu, b);
            case NamePage:
                return OnNameCall(menu, b);
            case LicensePage:
                return OnCardCall(menu, action, b);
            case CreateJoinPage:
                if (b == 94) // CREATE GAME (the data opens the mode page): a new game, the boxes empty, no bots
                {
                    EndLan(); // (stops listening; after a LAN game this player is seat 0 again)
                    _setup.ModeChosen = false;
                    _setup.RulesSeen = false;
                    for (int s = 1; s < _setup.Seats.Length; s++) _setup.Seats[s] = new Seat();
                    _setup.Seats[0].Ready = true;
                    return true;
                }
                if (b is >= 95 and <= 98) // an AVAILABLE GAMES row (the data opens the joiner's SELECT HUNTER)
                {
                    int k = b - 95;
                    if (k == _addressRow) return true; // the address row (its data opens the keypad instead)
                    _joinFromKeypad = false;
                    _joinTarget = k < _rows.Length ? _rows[k] : null;
                    if (_joinTarget != null) LoadBeacon(_joinTarget);
                    return true;
                }
                if (b is 78 or 76) return true; // a highlight chosen (the companion call: 78 on rev 0, 76 on rev 1)
                if (b == 17) // the back arrow (no page in its data, rev 0 and 1; B's data goes to 25): as B
                {
                    menu.GoTo(TypePage);
                    return true;
                }
                break;
            }
            if (b == 17) // back: vanilla ends the wireless session after asking (rules pages: B first closes ADVANCED)
            {
                if (page >= 28 && page <= 34 && AdvancedOpen(menu))
                {
                    CloseAdvanced(menu);
                    return true;
                }
                if (page >= ModePage && page <= JoinLobbyPage)
                {
                    AskEndSession(menu);
                    return true;
                }
                return false;
            }
            // a joiner can't change the host's game: its tabs only show the steps (and ONLINE PLAY's SELECT HUNTER on its own)
            if (page is JoinHunterPage or 37 or JoinLobbyPage && b is 0 or 1 or 2) return true;
            if (page == HunterPage && s_onlineHunterOnly && b is 0 or 1 or 2) return true;
            if (page >= ModePage && page <= LobbyPage && b is 0 or 1 or 2)
            {
                // the tabs (the mode and OK actions carry the same calls, with the page change in the data)
                if (action.TargetPage != 0xFF) return true;
                int target = b == 0 ? ModePage : b == 1 ? CurrentMode.Page : HunterPage;
                if (target != page) menu.GoTo(target);
                return true;
            }
            if (b == 100 || b == 13) return true; // (bookkeeping calls with nothing to show)
            if (page == ModePage) return OnModeCall(b);
            if (page >= 28 && page <= 34) return OnRulesCall(menu, b);
            if (page is HunterPage or JoinHunterPage && OnPickerCall(menu, b)) return true;
            if (page == HunterPage) return OnHunterCall(menu, b);
            if (page == LobbyPage) return OnLobbyCall(menu, b);
            if (page == JoinHunterPage) return OnJoinHunterCall(menu, b);
            if (page == JoinLobbyPage) return true; // its seats only show the host's lobby
            return false;
        }

        // CHOOSE MULTIPLAYER TYPE's doors (decision 1 = A, BindDoors): LOCAL PLAY opens CREATE OR JOIN A GAME as vanilla's
        // MULTI-CARD did; HUNTER LICENSE the name keyboard (M6; the license card and roster later); ONLINE PLAY (online
        // play, phase 4) is still to come
        private bool OnTypeCall(MenuEngine menu, int b)
        {
            switch (b)
            {
            case 89: // arriving: vanilla's first-visit nickname notice, once per profile (M6)
                NicknameNotice();
                return true;
            case 91: // LOCAL PLAY (vanilla's MULTI-CARD PLAY spot)
                menu.GoTo(CreateJoinPage);
                return true;
            case 90: // HUNTER LICENSE (vanilla's SINGLE-CARD PLAY spot): the card (M6), else the name keyboard
                _nameReturn = TypePage;
                _textDone = null; // (the nickname, not a RENAME left some other way)
                if (_card) menu.GoTo(LicensePage);
                else if (_keyboard && StreamerPrivacy.HideName) Popup?.Inform(StreamerPrivacy.NameLocked); // (STREAMING)
                else if (_keyboard) menu.GoTo(NamePage);
                else WorkInProgress?.Invoke("hunter license");
                return true;
            case 92: // ONLINE PLAY (vanilla's NINTENDO WI-FI CONNECTION spot): the online hub (OnlineMenus.cs)
                if (OpenOnline != null) OpenOnline(menu);
                else WorkInProgress?.Invoke("online play");
                return true;
            case 93: // REPLAYS (RIVAL RADAR's corner, BindReplays): the replay list is Replay mode's R3
                if (OpenReplays != null) OpenReplays(menu);
                else WorkInProgress?.Invoke("replays");
                return true;
            }
            return false;
        }

        private bool OnModeCall(int b)
        {
            ModeInfo? m = Modes.FirstOrDefault(x => x.Call == b);
            if (m == null) return false;
            RefreshArenas();
            PickMode(m.Id);
            _setup.ModeChosen = true;
            _setup.RulesSeen = true; // the rules page fills the boxes as it comes in
            return true;
        }

        private static readonly Dictionary<int, (Rule Rule, int Step)> RuleCalls = new()
        {
            [107] = (Rule.PointGoal, 1), [108] = (Rule.PointGoal, -1),
            [109] = (Rule.TimeLimit, 1), [110] = (Rule.TimeLimit, -1),
            [111] = (Rule.TeamPlay, 1), [112] = (Rule.TeamPlay, -1),
            [122] = (Rule.TimeGoal, 1), [123] = (Rule.TimeGoal, -1),
            [124] = (Rule.Lives, 1), [125] = (Rule.Lives, -1),
            [126] = (Rule.OctolithGoal, 1), [127] = (Rule.OctolithGoal, -1),
            [128] = (Rule.AutoReset, 1), [129] = (Rule.AutoReset, -1),
            [130] = (Rule.NodesGoal, 1), [131] = (Rule.NodesGoal, -1),
        };

        private bool OnRulesCall(MenuEngine menu, int b)
        {
            if (RuleCalls.TryGetValue(b, out var rc))
            {
                StepRule(rc.Rule, rc.Step);
            }
            else if (b is 113 or 114) // the arena
            {
                StepArena(b == 113 ? 1 : -1);
                Preview(menu, menu.Page!);
            }
            else if (b == 115) CoverPage(menu, covered: true); // ADVANCED: the data shows the box
            else if (b == 117) CoverPage(menu, covered: false); // its OK: the data hides it
            else if (b == 116) { } // a tap on the box's background
            else if (b == 118) _setup.Radar = !_setup.Radar;
            else if (b == 119) _setup.Damage = (_setup.Damage + 1) % 3; // A and Y both step forward
            else if (b == 120) _setup.Affinity = !_setup.Affinity;
            else if (b == 121)
            {
                if (CurrentMode.Solo == GameMode.PrimeHunter) return true;
                _setup.FriendlyFire = !_setup.FriendlyFire;
            }
            else if (OnExtraRulesCall(b)) { }
            else if (OnMatchMusicCall(menu, b)) { }
            else
            {
                return false;
            }
            FillStrings();
            return true;
        }

        private int _advancedBox = -1;

        private bool AdvancedOpen(MenuEngine menu)
        {
            MenuPage p = menu.Page!;
            _advancedBox = FindItem(p, it => HasWidget(_file!, it, "popup"));
            return _advancedBox >= 0 && menu.ItemState(_advancedBox) != MenuState.Hidden;
        }

        // B with the ADVANCED box up: as its OK (117), the action that hides the box and its links
        private void CloseAdvanced(MenuEngine menu)
        {
            MenuPage p = menu.Page!;
            int ok = FindItem(p, it => Calls(it).Contains(117));
            if (ok >= 0)
            {
                MenuAction? a = p.Items[ok].Actions.FirstOrDefault(x => x.Calls.Any(c => c.B == 117));
                if (a != null && a.Item != 0xFFFF && a.Item < p.Items.Count) menu.SetState(a.Item, MenuState.Hidden);
            }
            else if (_advancedBox >= 0)
            {
                menu.SetState(_advancedBox, MenuState.Hidden);
            }
            CoverPage(menu, covered: false);
        }

        // Vanilla's ADVANCED box hides the rules page under it. Our texts draw above every widget, so the page's own
        // touch-screen texts and its OK check (none of them the box's links) are hidden while it's up, and come back with it.
        private readonly List<int> _covered = new();

        private void CoverPage(MenuEngine menu, bool covered)
        {
            if (!covered)
            {
                foreach (int item in _covered) Show(menu, item, true);
                _covered.Clear();
                return;
            }
            MenuPage p = menu.Page!;
            int box = FindItem(p, it => HasWidget(_file!, it, "popup"));
            if (box < 0) return;
            var boxItems = new HashSet<int>(p.Items[box].Links.Select(l => l.Target)) { box };
            _covered.Clear();
            for (int i = 0; i < p.Items.Count; i++)
            {
                MenuItem it = p.Items[i];
                if (boxItems.Contains(i) || menu.ItemState(i) == MenuState.Hidden) continue;
                bool pageText = it.IsText && it.Y < 175; // (the tabs' labels stay, as vanilla's)
                bool pageOk = HasWidget(_file!, it, "ok") && !Calls(it).Contains(117);
                if (!pageText && !pageOk) continue;
                menu.SetState(i, MenuState.Hidden);
                _covered.Add(i);
            }
        }

        // "this will end wireless communications. are you sure?": YES leaves for CHOOSE MULTIPLAYER TYPE
        private void AskEndSession(MenuEngine menu)
        {
            if (s_onlineBack >= 0) // ONLINE PLAY's CREATE: back to the online page it came from
            {
                menu.GoTo(s_onlineBack);
                return;
            }
            if (Popup == null)
            {
                menu.GoTo(TypePage);
                return;
            }
            string text = _endWireless >= 0 ? _strings![_endWireless] : "this will end wireless communications.\n\nare you sure?";
            Popup.Ask(text, yes =>
            {
                if (yes) menu.GoTo(TypePage);
            });
        }

        private bool OnHunterCall(MenuEngine menu, int b)
        {
            if (b is >= 132 and <= 138) // A on an icon, or a tap on the big portrait: that hunter, then the lobby
            {
                if (!HunterOpen(HunterOrder[b - 132])) return true; // (its icon is hidden; a focus that still lands there)
                _setup.Seats[0].Hunter = _setup.MyHunter = HunterOrder[b - 132];
                OnlineHunterPicked = true;
                menu.GoTo(AfterHunter);
                return true;
            }
            if (b is >= 139 and <= 145) return true; // a tap on an icon: the data shows its portrait
            return false;
        }

        // ---- the lobby (page 38) ----

        private sealed class SeatItems
        {
            public int Name = -1, Remove = -1, ArrowFrame = -1, Left = -1, Right = -1, Check = -1, TeamA = -1, TeamB = -1;
            public int Grid = -1, Spot = -1; // (P8: the grid and the picture's touch spot move with the seat too)
            public readonly Dictionary<Hunter, int> Portrait = new();
            public readonly int[] Stars = { -1, -1, -1 };
        }

        private SeatItems[]? _seats;
        private int _addBot = -1, _addBotIcon = -1, _addBotText = -1, _hint = -1, _illegal = -1;
        private int[] _start = Array.Empty<int>();

        private static readonly float[] SeatX = { 10, 67, 124, 180 };

        // the ROM's pieces of one seat (0-3) on a lobby page
        private SeatItems FindSeat(MenuPage p, int seat)
        {
            string[] choice = { "choicesamus", "choicekanden", "choicespire", "choicetrace", "choicenoxus", "choicesylux", "choiceweavel" };
            int[][] arrows = { new[] { 174, 175 }, new[] { 177, 178 }, new[] { 180, 181 }, new[] { 183, 184 } };
            // team 0 = the orange piece ("team_red"), team 1 = the green one ("team_blue"), as in the match (GameState: 0 = orange)
            int[][] teams = { new[] { 148, 147 }, new[] { 154, 153 }, new[] { 162, 161 }, new[] { 170, 169 } };
            int[] checks = { -1, 176, 179, 182 };
            int[] removes = { -1, 156, 164, 172 };
            int[] stars = { -1, 150, 158, 166 };
            int[] spots = { -1, 155, 163, 171 }; // (the picture's touch spot: a no-op)
            float[] nameX = { 42, 100, 157, 214 };
            var si = new SeatItems();
            for (int h = 0; h < HunterOrder.Length; h++)
            {
                int ch = FindItem(p, it => HasWidget(_file!, it, choice[h]) && MathF.Abs(it.X - SeatX[seat]) < 2);
                si.Portrait[HunterOrder[h]] = ch;
            }
            si.Name = FindItem(p, it => it.IsText && MathF.Abs(it.X - nameX[seat]) < 2 && it.Y > 40 && it.Y < 120);
            int ItemCalling(int call) => FindItem(p, it => Calls(it).Contains(call));
            // the arrows themselves (a bot's check also makes their calls on Left / Right: matching by call alone found
            // the check, and the frame search then took the seat before's frame; owner report R9)
            si.Left = FindItem(p, it => HasWidget(_file!, it, "highlight_arrowleft") && Calls(it).Contains(arrows[seat][0]));
            si.Right = FindItem(p, it => HasWidget(_file!, it, "highlight_arrowright") && Calls(it).Contains(arrows[seat][1]));
            // the arrows' frame: the arrows_option piece just before them in the list
            for (int i = si.Left - 1; i >= 0 && i >= si.Left - 3; i--)
            {
                if (HasWidget(_file!, p.Items[i], "arrows_option")) si.ArrowFrame = i;
            }
            si.TeamA = ItemCalling(teams[seat][0]);
            si.TeamB = ItemCalling(teams[seat][1]);
            if (seat > 0)
            {
                si.Check = ItemCalling(checks[seat]);
                si.Remove = ItemCalling(removes[seat]);
                for (int k = 0; k < 3; k++) si.Stars[k] = ItemCalling(stars[seat] + k);
                si.Spot = ItemCalling(spots[seat]);
            }
            si.Grid = FindItem(p, it => HasWidget(_file!, it, "playergrid") && MathF.Abs(it.X - SeatX[seat]) < 2); // (< 2: the joiner's lobby (39) has seat 4 at x 181, the host's at 180)
            return si;
        }

        private void BindLobby(MenuPage p)
        {
            var seats = new SeatItems[_setup.Seats.Length];
            for (int s = 0; s < 4; s++) seats[s] = FindSeat(p, s);
            // P8: seats 5-8, the copies BindSeats8 made
            SeatItems[] more = _seats8.Seats.TryGetValue(p.Index, out SeatItems[]? copies) ? copies : Array.Empty<SeatItems>();
            for (int s = 4; s < seats.Length; s++) seats[s] = s - 4 < more.Length ? more[s - 4] : new SeatItems();
            _seats = seats;
            _addBot = FindItem(p, it => Calls(it).Contains(173));
            _addBotIcon = FindItem(p, it => HasWidget(_file!, it, "adbot"));
            _addBotText = FindItem(p, it => it.IsText && _strings![it.States[0].Text!.StringId] == "add bot");
            _hint = FindItem(p, it => it.IsText && _strings![it.States[0].Text!.StringId].StartsWith("you can wait for more", StringComparison.Ordinal));
            _illegal = FindItem(p, it => it.IsText && _strings![it.States[0].Text!.StringId] == "illegal teams");
            _start = new[]
            {
                FindItem(p, it => Calls(it).Contains(146)),
                FindItem(p, it => HasWidget(_file!, it, "startgame")),
                FindItem(p, it => it.IsText && _strings![it.States[0].Text!.StringId].StartsWith("START", StringComparison.Ordinal)),
            };
        }

        private static void Show(MenuEngine menu, int item, bool visible)
        {
            if (item < 0) return;
            MenuState now = menu.ItemState(item);
            if (visible ? now != MenuState.Hidden : now == MenuState.Hidden) return;
            menu.SetState(item, visible ? MenuState.Idle : MenuState.Hidden);
        }

        private int UsedSeats => _setup.Seats.Count(s => s.Used);

        // both teams need one or two players (0x213715C); P8: with 5-8 seats, one to four
        private bool TeamsLegal()
        {
            if (!_setup.TeamPlay) return true;
            int a = _setup.Seats.Count(s => s.Used && s.Team == 0), b = _setup.Seats.Count(s => s.Used && s.Team == 1);
            int most = UsedSeats > 4 ? 4 : 2;
            return a >= 1 && a <= most && b >= 1 && b <= most;
        }

        private void Lobby(MenuEngine menu, bool entering = false)
        {
            if (menu.PendingPage != -1) return; // leaving: every piece is on its way out
            if (entering || _seats == null) BindLobby(menu.Page!);
            for (int s = 0; s < _seats!.Length; s++)
            {
                Seat seat = _setup.Seats[s];
                SeatItems si = _seats![s];
                foreach ((Hunter h, int item) in si.Portrait) Show(menu, item, seat.Used && seat.Hunter == h);
                bool editing = seat.Used && (s == 0 || !seat.Ready); // the hunter arrows (a bot's until it's confirmed)
                Show(menu, si.ArrowFrame, editing);
                Show(menu, si.Left, editing);
                Show(menu, si.Right, editing);
                Show(menu, si.Check, seat.Used && seat.Bot && !seat.Ready);
                Show(menu, si.Remove, seat.Used && seat.Bot);
                for (int k = 0; k < 3; k++) Show(menu, si.Stars[k], seat.Used && seat.Bot && seat.Ready && seat.Level == k);
                Show(menu, si.TeamA, seat.Used && _setup.TeamPlay && seat.Team == 0);
                Show(menu, si.TeamB, seat.Used && _setup.TeamPlay && seat.Team == 1);
                Show(menu, si.Name, seat.Used);
            }
            LayoutSeats8(menu, (item, visible) => Show(menu, item, visible)); // (P8: two rows past 4)
            bool full = UsedSeats >= _setup.Seats.Length;
            Show(menu, _addBot, !full);
            Show(menu, _addBotIcon, !full);
            Show(menu, _addBotText, !full);
            Show(menu, _hint, !full);
            HostHint(menu.Page!);
            bool legal = TeamsLegal();
            Show(menu, _illegal, !legal);
            bool ready = UsedSeats > 1 && _setup.Seats.All(s => !s.Used || s.Ready) && legal;
            foreach (int item in _start) Show(menu, item, ready);
            FillStrings();
        }

        private bool OnLobbyCall(MenuEngine menu, int b)
        {
            // P8: a seat 5-8 copy's call is its ROM seat's, moved (MultiplayerMenu.Seats8.cs): that seat's instead
            bool copy = FromSeat8Call(ref b, out int seat8);
            int SeatOr(int rom) => copy ? seat8 : rom;
            int[] arrows = { 174, 175, 177, 178, 180, 181, 183, 184 };
            int a = Array.IndexOf(arrows, b);
            if (a >= 0)
            {
                StepSlot(SeatOr(a / 2), a % 2 == 0 ? -1 : 1);
            }
            else if (b == 173)
            {
                AddBot(); // (a joiner who comes later takes the last bot's seat)
            }
            else if (b is 176 or 179 or 182) _setup.Seats[SeatOr((b - 176) / 3 + 1)].Ready = true;
            else if (b is >= 150 and <= 152 or >= 158 and <= 160 or >= 166 and <= 168)
            {
                int rom = b < 158 ? 1 : b < 166 ? 2 : 3, k = (b - (rom == 1 ? 150 : rom == 2 ? 158 : 166));
                _setup.Seats[SeatOr(rom)].Level = (k + 1) % 3; // the star bar showing k + 1 stars: on to the next level
            }
            else if (b is 156 or 164 or 172)
            {
                int seat = SeatOr(b == 156 ? 1 : b == 164 ? 2 : 3);
                if (_setup.Seats[seat].Remote && Lan is { IsHost: true } lan) lan.Kick(_setup.Seats[seat].JoinerId); // (told BYE)
                _setup.Seats[seat] = new Seat();
                CompactSeats(); // (P8: back to 4 seats, the ones past 4 move down)
            }
            else if (b is 147 or 148 or 153 or 154 or 161 or 162 or 169 or 170)
            {
                int seat = SeatOr(b < 153 ? 0 : b < 161 ? 1 : b < 169 ? 2 : 3);
                // the shown team button switches to the other team
                _setup.Seats[seat].Team = 1 - _setup.Seats[seat].Team;
            }
            else if (b is 149 or 155 or 157 or 163 or 165 or 171) return true; // no-ops (a portrait's "arrived hidden")
            else if (b == 146)
            {
                Start(menu);
                return true;
            }
            else
            {
                return false;
            }
            Lobby(menu);
            return true;
        }

        // the next open hunter in the icon order for this seat (vanilla 0x02123070 skips locked ones)
        private void StepHunter(int seat, int step)
        {
            Seat s = _setup.Seats[seat];
            if (!s.Used) return;
            int i = Math.Max(0, Array.IndexOf(HunterOrder, s.Hunter));
            for (int n = 0; n < HunterOrder.Length; n++)
            {
                i = Wrap(i, step, HunterOrder.Length);
                if (HunterOpen(HunterOrder[i])) break;
            }
            s.Hunter = HunterOrder[i];
        }

        // ADD BOT (0x2134C88): the last free seat (4, 3, 2), a random hunter no other bot has, level 0, not confirmed
        private void AddBot()
        {
            int seat = AddBotSeat(); // (P8: then seats 5-8)
            if (seat < 0) return;
            int h = _random.Next(HunterOrder.Length);
            while (!HunterOpen(HunterOrder[h])) h = (h + 1) % HunterOrder.Length; // open ones only (0x02134CDC; Samus always)
            for (int tries = 0; tries < 7; tries++)
            {
                Hunter candidate = HunterOrder[(h + tries) % HunterOrder.Length];
                if (HunterOpen(candidate) && !_setup.Seats.Any(x => x.Used && x.Bot && x.Hunter == candidate))
                {
                    h = (h + tries) % HunterOrder.Length;
                    break;
                }
            }
            // a team-play bot joins the smaller team
            int team0 = _setup.Seats.Count(x => x.Used && x.Team == 0), team1 = _setup.Seats.Count(x => x.Used && x.Team == 1);
            _setup.Seats[seat] = new Seat { Used = true, Bot = true, Hunter = HunterOrder[h], Level = 0, Team = team0 <= team1 ? 0 : 1 };
        }

        // ---- RESULTS (page 47, M2) ----
        //
        // Overlay 0 (USA rev 1): the page fill 0x211FE3C on arrival, the stat window 0x211F684 on L/R, the arrows 0x211FA54;
        // BizHawk (Battle, 3 bots, POINT GOAL 1, 2026-10-07): the rows in the game's result order with the local player's
        // name Selected (its colour pulses), its medal and place on the top screen ("tied for 2nd place" with three players
        // on 0 points), the first column fixed and L/R scrolling the other two, PLAY AGAIN -> SELECT HUNTER (35), QUIT -> "this
        // will end wireless communications. are you sure?" -> page 25. Not rebuilt: vanilla's "saving to the game card" box
        // first (it saves the Hunter License's stats; nothing to save until M6).

        private enum Stat
        {
            Points, Time, Kills, Deaths, Efficiency, SelfDestructs, DamageDealt, BestWeapon, AltFormKills, HeadshotKills,
            FriendKills, Interceptions, Field260, Field268
        }

        // each mode's columns (offline list 0x02143BCC, by mode id - 2) with its header as the ROM writes it (found by text)
        private static readonly (Stat Stat, string Header)[] StatTail =
        {
            (Stat.SelfDestructs, "self-\ndestructs"), (Stat.DamageDealt, "damage\ndealt"), (Stat.BestWeapon, "best\nweapon"),
            (Stat.AltFormKills, "alt-form\nkills"), (Stat.HeadshotKills, "headshot kills"), (Stat.FriendKills, "friend kills"),
        };

        private static (Stat Stat, string Header)[] StatColumns(int mode)
        {
            (Stat, string) deaths = (Stat.Deaths, "deaths"), efficiency = (Stat.Efficiency, "efficiency"), kills = (Stat.Kills, "kills");
            (Stat, string)[] head = mode switch
            {
                2 => new[] { (Stat.Points, "points"), deaths, efficiency, kills },
                3 => new[] { (Stat.Points, "bounties"), kills, (Stat.Interceptions, "intrcpt."), (Stat.Field268, "fumbles"), deaths, efficiency },
                4 => new[] { (Stat.Points, "captures"), kills, (Stat.Interceptions, "intrcpt."), (Stat.Field268, "fumbles"), deaths, efficiency },
                5 => new[] { (Stat.Time, "time in ring"), kills, deaths, efficiency },
                6 => new[] { (Stat.Points, "points"), kills, (Stat.Field260, "node\ncaptures"), (Stat.Field268, "nodes lost"), deaths, efficiency },
                7 => new[] { (Stat.Time, "p. h.\ntime"), kills, (Stat.Field268, "prime\nmutations"), (Stat.Field260, "prime kills"), deaths, efficiency },
                _ => new[] { (Stat.Time, "survival time"), deaths, efficiency, kills },
            };
            return head.Concat(mode == 7 ? StatTail.Where(t => t.Stat != Stat.FriendKills) : StatTail).ToArray();
        }

        // best weapon's names (0x021437DC): the beams in MphRead's BeamType order, then the alt form, then none
        private static readonly string[] WeaponWords = { "pwr.", "vlt.", "msl.", "btl.", "imp.", "jud.", "mag.", "shk.", "omg.", "alt.", "-" };
        private static readonly string[] PlaceWords = { "1st place", "2nd place", "3rd place", "last place" };
        private static readonly string[] TiedWords = { "tied for 1st place", "tied for 2nd place", "tied for 3rd place" };
        // the lightning's sound (0x206F2F4(100): sound script 100)
        private const int LightningSound = 0x4000 | 100;

        private bool _resultsBound;
        private int _resultsMode = -1, _resultsPlayer = -1, _resultsPlace = -1;
        // per row (top to bottom): the three name items (white, team 0, team 1) sharing one string, and the three value texts
        private readonly int[][] _resultName = new int[4][];
        private readonly int[] _resultNameString = { -1, -1, -1, -1 };
        private readonly int[][] _resultValue = new int[4][];
        private readonly int[] _resultHeader = { -1, -1, -1 };
        private readonly Dictionary<int, int> _medal = new(); // place -> item
        private int _lightning = -1, _teamDivide = -1, _crossbar = -1, _topbar = -1;
        private int[] _arrowsBack = Array.Empty<int>(), _arrowsMore = Array.Empty<int>();
        private static int _resultsScroll;

        private bool BindResults(MenuFile file, MenuStrings strings)
        {
            if (file.Pages.Count <= ResultsPage) return false;
            MenuPage p = file.Pages[ResultsPage];
            if (!PageCalls(p, 194, 195, 196, 197)) return false;
            float[] rowY = { 117, 89, 61, 33 }, valueY = { 118, 90, 62, 34 }, columnX = { 111, 169, 227 };
            uint[] colours = { 0x1F1F1F1F, 0x1F00101F, 0x1F061B06 }; // white, team 0 (orange), team 1 (green)
            for (int r = 0; r < 4; r++)
            {
                int row = r;
                // (the name column: x 38 on rev 0, 42 on rev 1; left of the first value column)
                _resultName[r] = colours.Select(c => FindItem(p, it => it.IsText && it.X > 20 && it.X < columnX[0] - 40
                    && MathF.Abs(it.Y - rowY[row]) < 1.5f && it.States.Any(st => st.Code == (int)MenuState.Idle
                    && st.Text?.StartColor == c))).ToArray();
                _resultNameString[r] = StringOf(p, _resultName[r][0]);
                _resultValue[r] = columnX.Select(x => FindItem(p, it => it.IsText && MathF.Abs(it.X - x) < 2
                    && MathF.Abs(it.Y - valueY[row]) < 1.5f)).ToArray();
            }
            for (int c = 0; c < 3; c++) _resultHeader[c] = StringOf(p, TextAt(p, columnX[c], 151));
            // the top screen: GAME MODE's text is the setup pages' box label too, so RESULTS gets a string of its own
            // (BizHawk: drawn in the place text's orange, where the data has it black)
            int mode = TextAt(p, 163, 233), placeItem = TextAt(p, 163, 204);
            if (mode >= 0)
            {
                _resultsMode = strings.Add("");
                MenuItem src = p.Items[mode];
                MenuItem? orange = placeItem >= 0 ? p.Items[placeItem] : null;
                int id = _resultsMode;
                p.ReplaceItem(mode, i => Recolour(RecompMenus.Clone(src, i, 0, 0, stringId: id), orange));
            }
            _resultsPlayer = StringOf(p, TextAt(p, 163, 216));
            _resultsPlace = StringOf(p, placeItem);
            _crossbar = FindItem(p, it => HasWidget(file, it, "crossbar"));
            _topbar = FindItem(p, it => HasWidget(file, it, "topbar"));
            string[] medals = { "medalgold", "medalsilver", "medalbronze", "medallast" };
            for (int m = 0; m < 4; m++) _medal[m] = FindItem(p, it => HasWidget(file, it, medals[m]));
            _lightning = FindItem(p, it => HasWidget(file, it, "lightning"));
            _teamDivide = FindItem(p, it => HasWidget(file, it, "teamdivide"));
            // (vanilla's pairs, 0x211FA54: one pair shows while there are columns back, the other while there are more)
            _arrowsBack = new[] { FindItem(p, it => HasWidget(file, it, "blackarrowright")), FindItem(p, it => HasWidget(file, it, "highlight_arrowleft")) };
            _arrowsMore = new[] { FindItem(p, it => HasWidget(file, it, "blackarrowleft")), FindItem(p, it => HasWidget(file, it, "highlight_arrowright")) };
            foreach (string t in WeaponWords.Concat(PlaceWords).Concat(TiedWords).Append("max")
                .Concat(Enumerable.Range(2, 7).SelectMany(m => StatColumns(m).Select(c => c.Header))))
            {
                if (!_text.ContainsKey(t)) _text[t] = Text(strings, s => s == t);
            }
            bool ok = _resultName.All(r => r.All(i => i >= 0)) && _resultValue.All(r => r.All(i => i >= 0))
                && _resultHeader.All(h => h >= 0) && _resultsMode >= 0 && _resultsPlayer >= 0 && _resultsPlace >= 0;
            if (!ok) Log?.Invoke("multiplayer: this ROM's RESULTS page isn't the expected layout (the lobby after a match)");
            else BindResults8(file, strings, p); // (P8: rows 5-8)
            return ok;
        }

        // a text item with another text item's colours, state by state (the rest of its style kept)
        private static MenuItem Recolour(MenuItem item, MenuItem? from)
        {
            if (from == null) return item;
            var states = item.States.Select(st =>
            {
                MenuTextStyle? c = from.States.FirstOrDefault(f => f.Code == st.Code)?.Text;
                if (st.Text == null || c == null) return st;
                MenuTextStyle t = st.Text;
                return new MenuItemState
                {
                    Code = st.Code, Kind = st.Kind, Field2 = st.Field2, WidgetIndex = st.WidgetIndex, WidgetFlags = st.WidgetFlags,
                    Text = new MenuTextStyle
                    {
                        StringId = t.StringId, StringField = t.StringField, StartColor = c.StartColor, EndColor = c.EndColor,
                        WrapWidth = t.WrapWidth, Duration = t.Duration, Size = t.Size, Format1 = t.Format1, Format2 = t.Format2,
                        Align = t.Align, Flags = t.Flags, Field15 = t.Field15, Field16 = t.Field16
                    }
                };
            }).ToList();
            return new MenuItem
            {
                Index = item.Index, Delay = item.Delay, Field18 = item.Field18, Field1C = item.Field1C, X = item.X, Y = item.Y,
                StretchX = item.StretchX, StretchDown = item.StretchDown, Depth = item.Depth, Field30 = item.Field30,
                Field34 = item.Field34, Field35 = item.Field35, Flags = item.Flags, Field39 = item.Field39, Field3A = item.Field3A,
                States = states, Links = item.Links, Actions = item.Actions
            };
        }

        private static int ModeId(GameMode mode) => Modes.FirstOrDefault(m => m.Solo == mode || m.Teams == mode)?.Id ?? 2;

        private (Stat Stat, string Header)[] ResultColumns(MatchResults r)
        {
            int id = ModeId(r.Mode);
            // friend kills: team play with friendly fire only
            return StatColumns(id).Where(c => c.Stat != Stat.FriendKills || r.Teams && r.FriendlyFire).ToArray();
        }

        private string StatValue(MatchResults.Player p, Stat stat)
        {
            switch (stat)
            {
            case Stat.Time:
                if (p.Time < 0) return Word("max");
                int seconds = (int)p.Time;
                return $"{seconds / 60}:{seconds % 60:00}";
            case Stat.Efficiency:
                return p.Efficiency.ToString(CultureInfo.InvariantCulture) + "%";
            case Stat.BestWeapon:
                return Word(WeaponWords[Math.Clamp(p.BestWeapon, 0, WeaponWords.Length - 1)]);
            }
            int value = stat switch
            {
                Stat.Points => p.Points,
                Stat.Kills => p.Kills,
                Stat.Deaths => p.Deaths,
                Stat.SelfDestructs => p.SelfDestructs,
                Stat.DamageDealt => p.DamageDealt,
                Stat.AltFormKills => p.AltFormKills,
                Stat.HeadshotKills => p.HeadshotKills,
                Stat.FriendKills => p.FriendKills,
                Stat.Interceptions => p.Interceptions,
                Stat.Field260 => p.Field260,
                _ => p.Field268,
            };
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // a name in the menu font (lower case = its small capitals): a bot by the ROM's bot name, a player by the nickname
        private string MenuName(MatchResults.Player p, bool mine) =>
            p.Bot && p.Character != null ? p.Character.ToLowerInvariant() // D7: a TROOPER / DARK SAMUS bot by its character
            : p.Bot && BotNames.TryGetValue(p.Hunter, out string? bot) ? Word(bot)
            : StreamerPrivacy.Name(p.Nickname, mine).ToLowerInvariant(); // (STREAMING: mine = the local seat)

        private void Results(MenuEngine menu, bool entering = false)
        {
            MatchResults? r = MatchResults.Last;
            if (!_resultsBound || r == null) return;
            if (entering)
            {
                _resultsScroll = 0;
                _selectWhenIn = -1;
                LayoutResults8(menu, r.Rows.Count > ResultsRowsShown); // (P8: 5-8 players, every row at once)
            }
            MenuStrings s = _strings!;
            (Stat Stat, string Header)[] columns = ResultColumns(r);
            _resultsScroll = Math.Clamp(_resultsScroll, 0, Math.Max(0, columns.Length - 3));
            // rows: team play with the first two rows on different teams leaves row 2 empty, so each team gets its own
            // half under the divider (0x213720C); P8: 5-8 rows (MultiplayerMenu.ResultsRows.cs)
            MatchResults.Player?[] shown = ResultsShown(r);
            int[] col = { 0, _resultsScroll + 1, _resultsScroll + 2 };
            for (int c = 0; c < 3; c++)
            {
                s.Fill(_resultHeader[c], col[c] < columns.Length ? Word(columns[col[c]].Header) : "");
            }
            for (int row = 0; row < shown.Length; row++)
            {
                MatchResults.Player? p = shown[row];
                PutString(s, ResultNameString(row), p == null ? "" : MenuName(p, p.Seat == r.LocalSeat));
                // the white name without team play, else the team's colour; the local player's is Selected
                int keep = p == null ? -1 : !r.Teams ? 0 : 1 + Math.Clamp(p.Team, 0, 1);
                for (int k = 0; k < 3; k++)
                {
                    int item = ResultNameItems(row)[k];
                    if (!entering) continue;
                    ShowAtEntry(menu, item, k == keep);
                    if (k == keep && p!.Seat == r.LocalSeat) _selectWhenIn = item;
                }
                for (int c = 0; c < 3; c++)
                {
                    PutString(s, ResultValueString(menu.Page!, row, c),
                        p == null || col[c] >= columns.Length ? "" : StatValue(p, columns[col[c]].Stat));
                }
            }
            bool back = _resultsScroll > 0, more = _resultsScroll + 3 < columns.Length;
            foreach (int item in _arrowsBack) Visible(item, back);
            foreach (int item in _arrowsMore) Visible(item, more);
            void Visible(int item, bool visible)
            {
                if (entering) ShowAtEntry(menu, item, visible);
                else Show(menu, item, visible);
            }
            if (!entering) return;
            // the top screen: the mode, the local player's name and place; the place's medal; the lightning unless last
            MatchResults.Player? local = r.Local;
            ModeInfo? mode = Modes.FirstOrDefault(m => m.Id == ModeId(r.Mode));
            s.Set(_resultsMode, mode == null ? "" : Word(mode.Name));
            s.Fill(_resultsPlayer, local == null ? "" : MenuName(local, mine: true));
            int place = local == null ? -1 : Math.Clamp(local.Place, 0, 3);
            bool tied = local != null && r.Rows.Any(q => q.Seat != local.Seat && q.Place == local.Place && (!r.Teams || q.Team != local.Team));
            string placeText = place < 0 ? "" : Word(tied && place < 3 ? TiedWords[place] : PlaceWords[place]);
            int medal = place;
            bool lightning = place >= 0 && place < r.Rows.Count - 1;
            if (local != null && r.Rows.Count > ResultsRowsShown) (placeText, medal, lightning) = ResultsPlaceWide(r, local, tied); // (P8)
            s.Fill(_resultsPlace, placeText);
            foreach ((int m, int item) in _medal) ShowAtEntry(menu, item, m == medal);
            ShowAtEntry(menu, _lightning, lightning);
            if (lightning) menu.Host?.OnSound(menu, LightningSound, "results lightning");
            ShowAtEntry(menu, _teamDivide, r.Teams); // (P8: 5-8 rows, one team above it, the other below)
            Log?.Invoke($"multiplayer: results {r.Mode}, {r.Rows.Count} players, local place {place + 1}{(tied ? " (tied)" : "")}");
        }

        // At page entry no item has started (each waits out its delay, then takes its initial state): one that should be
        // hidden but starts shown is started hidden now; one that should show but starts hidden is shown now; the rest
        // come in by themselves.
        private static void ShowAtEntry(MenuEngine menu, int item, bool visible)
        {
            if (item < 0) return;
            bool startsShown = (MenuState)menu.Page!.Items[item].Field34 != MenuState.Hidden;
            if (visible != startsShown) menu.SetState(item, visible ? MenuState.Idle : MenuState.Hidden);
        }

        // the local player's name: Selected once it has come in (its colour then pulses, as vanilla's)
        private int _selectWhenIn = -1;

        private void ResultsTick(MenuEngine menu)
        {
            if (_selectWhenIn < 0) return;
            if (menu.Page?.Index != ResultsPage || menu.PendingPage != -1)
            {
                _selectWhenIn = -1;
                return;
            }
            if (menu.ItemState(_selectWhenIn) == MenuState.Idle && !MenuStateCode.IsTransition(menu.ItemCode(_selectWhenIn)))
            {
                menu.SetState(_selectWhenIn, MenuState.Selected);
                _selectWhenIn = -1;
            }
        }

        private bool OnResultsCall(MenuEngine menu, int b)
        {
            switch (b)
            {
            case 194: // R / the right arrow: the next column (at the end the data's flash of the arrow is all)
            case 195: // L / the left arrow
                MatchResults? r = MatchResults.Last;
                if (r == null) return true;
                int count = ResultColumns(r).Length, scroll = _resultsScroll + (b == 194 ? 1 : -1);
                if (scroll < 0 || scroll + 3 > count) return true;
                _resultsScroll = scroll;
                Results(menu);
                return true;
            case 196 or 197 when s_onlineResultsReturn >= 0: // an online match's: PLAY AGAIN = its lobby again, QUIT = ONLINE PLAY
            {
                int to = s_onlineResultsReturn;
                s_onlineResultsReturn = -1;
                if (OnlineResultsDone != null) OnlineResultsDone(menu, b == 196);
                else menu.GoTo(to);
                return true;
            }
            case 196: // PLAY AGAIN: SELECT HUNTER, then the same lobby (the bots stay seated); a joiner goes back to its host
                if (_lastWasJoiner && _lastHost != null) Rejoin(menu);
                else menu.GoTo(_lastWasJoiner ? CreateJoinPage : HunterPage);
                return true;
            case 197: // QUIT: as back on the setup pages
                AskEndSession(menu);
                return true;
            }
            return false;
        }

        // ---- LAN (M3) ----
        //
        // One LanSession at a time, LanSession.Current (the match screen plays it from there): the host's beacons while its
        // lobby (38) can take a player; a joiner's listening on CREATE OR JOIN (26), its JOIN from SELECT HUNTER (36), and
        // its wait on the "please wait" lobby (39) for the host's START GAME. Every mode, team play and bots: up to three
        // joiners, each in the next free seat as it comes (the last bot gives way when none is free); the lobby beacons
        // while a seat could take one. The match's state is the host's (Core Multiplayer/Net/NetObjective). Leaving these
        // pages (other than into the match) ends the session: the other devices hear BYE.

        // Android: hearing the hosts' broadcast beacons needs Wi-Fi's multicast lock (the shell hands one out)
        public static Func<IDisposable?>? MulticastLock { get; set; }
        private static IDisposable? _multicast;
        private static LanHostInfo? _joinTarget; // the AVAILABLE GAMES row chosen
        private static volatile int _joinState; // 0 none, 1 asking the host, 2 seated, -1 refused or no answer
        private static string _joinFailure = "";
        private static bool _setupFromHost; // the setup shows a host's game (a joiner): seat 0 is the host's
        private static bool _lastWasJoiner; // RESULTS' PLAY AGAIN goes back to the host (Rejoin)
        private static LanHostInfo? _lastHost; // the host of the joiner's last match
        private static bool _rejoin; // the joiner's SELECT HUNTER after PLAY AGAIN: ask that host until its lobby answers
        private bool _joinShown;
        private int _joinLobbyVersion; // the LanSession.LobbyVersion the joiner's lobby shows
        private int _lanTick, _hostRetry;
        // CREATE OR JOIN's AVAILABLE GAMES rows: the red bars (calls 95-98) and their texts ("game1".."game4")
        private readonly int[] _rowBar = { -1, -1, -1, -1 }, _rowText = { -1, -1, -1, -1 };
        private LanHostInfo[] _rows = Array.Empty<LanHostInfo>();

        private static LanSession? Lan => LanSession.Current;
        private static bool Hosting => Lan is { IsHost: true };
        private static string RomKey => LanSession.RomKeyFor(Paths.MphKey);

        // a lobby a joiner can come into: a seat that is free or a bot's (above)
        private static bool LanAllowed() => _setup.Seats.Skip(1).Any(s => !s.Used || s.Bot);

        private static int JoinerSeat() => Array.FindIndex(_setup.Seats, s => s.Used && s.Remote);

        private static void EndLan()
        {
            LanSession? lan = Lan;
            if (lan != null)
            {
                lan.Dispose(); // (BYE to the other device; Current = null)
                LanSession.Current = null;
            }
            _multicast?.Dispose();
            _multicast = null;
            _joinState = 0;
            if (_setupFromHost) RestoreOwnSeats();
            for (int s = 1; s < _setup.Seats.Length; s++)
            {
                if (_setup.Seats[s].Remote) _setup.Seats[s] = new Seat();
            }
        }

        // after a host's game: this player in seat 0 again, the other seats empty
        private static void RestoreOwnSeats()
        {
            _setupFromHost = false;
            _setup.Seats[0] = new Seat { Used = true, Ready = true, Hunter = _setup.MyHunter, Skin = _setup.MySkin, Character = _setup.MyCharacter,
                ColourPick = _setup.MyColour };
            for (int s = 1; s < _setup.Seats.Length; s++) _setup.Seats[s] = new Seat();
        }

        private void StartBrowse()
        {
            if (Lan is { IsHost: false } && _joinState == 0) return; // still listening
            EndLan();
            try
            {
                _multicast = MulticastLock?.Invoke();
                LanSession.Current = LanSession.Browse(RomKey);
                Log?.Invoke("multiplayer: lan: " + LanSession.Current.Status);
            }
            catch (Exception ex)
            {
                Log?.Invoke("multiplayer: lan: can't listen: " + ex.Message);
            }
        }

        private void HostLan()
        {
            if (_hostRetry > 0) return;
            try
            {
                LanSession.Current = LanSession.Host(BuildMatch(), RomKey, _setup.Nickname, seatLimit: _setup.Seats.Length); // (P8: up to 8)
                LanSession.Current.ArenaCheck = JoinArenaCheck; // (N27: no seat for a joiner without the lobby's arena)
                Log?.Invoke("multiplayer: lan: " + LanSession.Current.Status);
            }
            catch (Exception ex)
            {
                _hostRetry = 300; // (no network: try again in 10 s)
                Log?.Invoke("multiplayer: lan: can't host: " + ex.Message);
            }
        }

        // the AVAILABLE GAMES rows from the beacons heard (up to 4); the names as the font writes them (lower case), two
        // hosts with the same nickname told apart by their address's last number
        // (the last row is the address row where that's on: BindAddress)
        private void Rows(MenuEngine menu, bool entering = false)
        {
            LanSession? lan = Lan;
            int max = _addressRow >= 0 ? _addressRow : 4;
            // (a match already running is listed only for the players whose seats it holds: theirs to rejoin, release test T1; First Hunt games are listed only in First Hunt: FhLobby)
            LanHostInfo[] hosts = lan == null || lan.IsHost ? Array.Empty<LanHostInfo>()
                : lan.Hosts.Where(h => h.RomKey != MphRecomp.FirstHunt.FhLobby.RomKey && (!h.Running || h.HeldFor(_setup.Nickname))).Take(max).ToArray();
            _rows = hosts;
            for (int k = 0; k < 4; k++)
            {
                bool address = k == _addressRow;
                bool shown = address || k < hosts.Length;
                if (shown && _rowText[k] >= 0)
                {
                    _strings!.Fill(StringOf(menu.Page!, _rowText[k]), address ? "enter address" : RowName(hosts, k));
                }
                if (entering)
                {
                    ShowAtEntry(menu, _rowBar[k], shown);
                    ShowAtEntry(menu, _rowText[k], shown);
                }
                else
                {
                    Show(menu, _rowBar[k], shown);
                    Show(menu, _rowText[k], shown);
                }
            }
        }

        private static string RowName(LanHostInfo[] hosts, int k)
        {
            string name = hosts[k].Name.ToLowerInvariant();
            if (name.Length > 10) name = name[..10];
            bool twin = hosts.Where((h, i) => i != k && h.Name.Equals(hosts[k].Name, StringComparison.OrdinalIgnoreCase)).Any();
            if (hosts[k].Running) return "rejoin " + (name.Length > 6 ? name[..6] : name); // (your seat in its match)
            if (!twin) return name;
            // two hosts of one name: told apart by their address's last number, or (STREAMING: addresses hidden) by their
            // place in the list among that name's
            if (!StreamerPrivacy.HideAddress) return $"{name} {hosts[k].Address.Address.GetAddressBytes()[^1]}";
            int place = hosts.Take(k).Count(h => h.Name.Equals(hosts[k].Name, StringComparison.OrdinalIgnoreCase)) + 1;
            return $"{name} #{place}";
        }

        // a row chosen: the GAME MODE box shows the host's mode until its lobby answers with the whole game
        private static void LoadBeacon(LanHostInfo host)
        {
            ModeInfo? m = Modes.FirstOrDefault(x => x.Solo == host.Mode || x.Teams == host.Mode);
            if (m != null)
            {
                _setup.Mode = m.Id;
                _setup.TeamPlay = host.Mode == m.Teams && m.Teams != m.Solo;
                _defaultsSet = true;
            }
            _setup.ModeChosen = m != null;
            _setup.RulesSeen = false;
            _setupFromHost = true;
            HostExtraRules(host.ExtraPickups, host.ExtraCharacters);
        }

        // the host's game (WELCOME / START) into the setup, so the top screen's boxes, the preview and the seats show it
        private static void LoadSetup(MatchSettings s)
        {
            ModeInfo? m = Modes.FirstOrDefault(x => x.Solo == s.Mode || x.Teams == s.Mode);
            if (m == null) return;
            _setup.Mode = m.Id;
            _defaultsSet = true;
            _setup.TeamPlay = s.Mode == m.Teams && m.Teams != m.Solo;
            _setup.ModeChosen = _setup.RulesSeen = true;
            foreach (Rule rule in m.Rules)
            {
                switch (rule)
                {
                case Rule.PointGoal:
                case Rule.NodesGoal:
                case Rule.OctolithGoal:
                case Rule.Lives:
                    if (s.PointGoal is int g) _setup.Goal = ClampTo(GoalTable(rule), g);
                    break;
                case Rule.TimeGoal:
                    if (s.TimeGoalSeconds is float tg) _setup.TimeGoal = ClampTo(TimeGoalSeconds, (int)MathF.Round(tg));
                    break;
                case Rule.AutoReset:
                    _setup.AutoReset = s.OctolithReset ?? _setup.AutoReset;
                    break;
                }
            }
            if (s.TimeLimitSeconds is float tl) _setup.TimeLimit = ClampTo(TimeLimitMinutes, (int)MathF.Round(tl / 60));
            _setup.Damage = s.DamageLevel ?? 1;
            _setup.Radar = s.RadarPlayers ?? false;
            _setup.Affinity = s.AffinityWeapons ?? false;
            _setup.FriendlyFire = s.FriendlyFire ?? false;
            HostExtraRules(s.PirateBlaster != false, s.ExtraCharacters != false);
            RoomMetadata? room = MatchArenas.Find(s.Arena);
            int arena = room == null ? -1 : Array.IndexOf(ArenaRooms, room.Id);
            if (arena >= 0) _setup.Arena = arena;
            for (int i = 0; i < _setup.Seats.Length; i++)
            {
                MatchPlayer? p = i < s.Players.Count ? s.Players[i] : null;
                bool mine = i == s.LocalSeat;
                _setup.Seats[i] = p == null ? new Seat() : new Seat
                {
                    Used = true, Ready = true, Bot = p.Bot, Hunter = p.Hunter, Level = p.BotLevel, Team = Math.Max(0, p.Team),
                    Remote = !mine, Name = mine ? null : p.Nickname, Skin = p.Skin, Character = p.Character,
                    ColourPick = mine ? _setup.MyColour : p.ColourPick, // (this player's pick shows before the host's LOBBY has it)
                };
            }
            _setupFromHost = true;
        }

        // the joiner's SELECT HUNTER (36): the same calls as the host's page; a pick asks the host for a seat (JOIN, a few
        // seconds at most) while the "please wait" lobby comes in
        private bool OnJoinHunterCall(MenuEngine menu, int b)
        {
            if (b is >= 139 and <= 145) return true; // a tap on an icon: the data shows its portrait
            if (b is < 132 or > 138) return false;
            Hunter hunter = HunterOrder[b - 132];
            if (!HunterOpen(hunter)) return true;
            JoinAs(menu, hunter, null, null);
            return true;
        }

        // a pick on the joiner's SELECT HUNTER (a hunter, or the picker's character + skin): ask the host for a seat
        private void JoinAs(MenuEngine menu, Hunter hunter, string? character, string? skin)
        {
            _setup.MyHunter = hunter;
            LanSession? lan = Lan;
            LanHostInfo? target = _joinTarget;
            if (lan == null || lan.IsHost || target == null)
            {
                Popup?.Inform("that game is gone.");
                menu.GoTo(CreateJoinPage);
                return;
            }
            _joinState = 1;
            string nickname = _setup.Nickname;
            int rank = _setup.Rank;
            int colour = _setup.MyColour;
            bool rejoin = _rejoin;
            _rejoin = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool ok = rejoin ? RejoinHost(lan, target, hunter, nickname, rank, character, skin, colour)
                    : lan.Join(target.Address, hunter, nickname, rankLevel: rank, character: character, skin: skin, colourPick: colour);
                _joinFailure = ok ? "" : lan.Status;
                _joinState = ok ? 2 : -1;
            });
            Log?.Invoke($"multiplayer: lan: joining {target.Address} as {hunter}");
            menu.GoTo(JoinLobbyPage);
        }

        // RESULTS' PLAY AGAIN for a joiner (owner report R2, 2026-10-07: "guest is kicked out of lobby after match"): as
        // vanilla's (ov0 0x2139E10: PLAY AGAIN -> 36 for a joiner), its SELECT HUNTER and then the "please wait" lobby of
        // the same host, not the list. Each match screen ends its LanSession (BYE), so the joiner listens again and asks
        // that host for a seat until its lobby takes players again (RejoinHost)
        private void Rejoin(MenuEngine menu)
        {
            EndLan();
            StartBrowse();
            if (Lan == null)
            {
                menu.GoTo(CreateJoinPage);
                return;
            }
            _joinTarget = _lastHost;
            _joinFromKeypad = false;
            _rejoin = true;
            LoadBeacon(_lastHost!);
            Log?.Invoke($"multiplayer: lan: back to {_lastHost!.Name} at {_lastHost.Address}");
            menu.GoTo(JoinHunterPage);
        }

        // the host's lobby takes players only once its own player is back in it (after its RESULTS and SELECT HUNTER), so
        // ask again until it answers, up to 3 minutes, following its beacons if its new lobby listens on another port. B on
        // the "please wait" lobby ends the session (EndLan), and with it the asking.
        private static bool RejoinHost(LanSession lan, LanHostInfo target, Hunter hunter, string nickname, int rank,
            string? character, string? skin, int colour)
        {
            long end = Environment.TickCount64 + 180_000;
            IPEndPoint address = target.Address;
            while (Environment.TickCount64 < end && ReferenceEquals(LanSession.Current, lan))
            {
                LanHostInfo? heard = lan.Hosts.FirstOrDefault(h => h.Address.Address.Equals(address.Address));
                if (heard != null) address = heard.Address;
                if (lan.Join(address, hunter, nickname, timeoutMs: 3000, rankLevel: rank, character: character, skin: skin, colourPick: colour)) return true;
                if (lan.Status.StartsWith("refused", StringComparison.Ordinal)) return false;
            }
            return false;
        }

        // the joiner's lobby (39): the host's seats once it answered; nothing to edit
        private void JoinLobby(MenuEngine menu, bool entering = false)
        {
            if (menu.PendingPage != -1) return;
            if (entering || _seats == null) BindLobby(menu.Page!);
            bool seated = _joinState == 2;
            void Vis(int item, bool visible)
            {
                if (entering) ShowAtEntry(menu, item, visible);
                else Show(menu, item, visible);
            }
            for (int s = 0; s < _seats!.Length; s++)
            {
                Seat seat = _setup.Seats[s];
                SeatItems si = _seats![s];
                bool used = seated && seat.Used;
                foreach ((Hunter h, int item) in si.Portrait) Vis(item, used && seat.Hunter == h);
                foreach (int item in new[] { si.ArrowFrame, si.Left, si.Right, si.Check, si.Remove }) Vis(item, false);
                for (int k = 0; k < 3; k++) Vis(si.Stars[k], used && seat.Bot && seat.Level == k);
                Vis(si.TeamA, used && _setup.TeamPlay && seat.Team == 0);
                Vis(si.TeamB, used && _setup.TeamPlay && seat.Team == 1);
                Vis(si.Name, used);
            }
            LayoutSeats8(menu, Vis); // (P8: two rows past 4)
            FillStrings();
        }

        private void LanTick(MenuEngine menu)
        {
            if (!Bound || menu.Page == null || menu.PendingPage != -1 || Popup?.IsOpen == true) return;
            if (_hostRetry > 0) _hostRetry--;
            switch (menu.Page.Index)
            {
            case CreateJoinPage:
                if (++_lanTick % 15 == 0) Rows(menu);
                break;
            case LobbyPage:
                HostTick(menu);
                break;
            case JoinLobbyPage:
                JoinTick(menu);
                break;
            }
        }

        // the host's lobby: seat each joiner when it arrives (the next free seat, else the last bot's), empty its seat when
        // it leaves, keep the beacons' match current, and beacon only while the lobby can take one more
        private void HostTick(MenuEngine menu)
        {
            LanSession? lan = Lan;
            if (JoinerSeat() >= 0 && (lan == null || !lan.IsHost))
            {
                Log?.Invoke("multiplayer: lan: the session ended");
                EndLan(); // (empties the joiners' seats)
                Lobby(menu);
                lan = null;
            }
            else if (lan is { IsHost: true })
            {
                bool changed = false;
                foreach (LanJoinerInfo j in lan.Joiners)
                {
                    int seated = Array.FindIndex(_setup.Seats, s => s.Used && s.Remote && s.JoinerId == j.Id);
                    if (j.Left)
                    {
                        if (seated > 0)
                        {
                            Log?.Invoke($"multiplayer: lan: {j.Player.Nickname} left, seat {seated + 1}");
                            _setup.Seats[seated] = new Seat();
                            CompactSeats(); // (P8)
                            changed = true;
                        }
                        continue;
                    }
                    if (seated > 0)
                    {
                        // a seated joiner's colour pick changed in its lobby (LanSession LOOK, protocol 24)
                        if (_setup.Seats[seated].ColourPick != j.Player.ColourPick)
                        {
                            _setup.Seats[seated].ColourPick = j.Player.ColourPick;
                            changed = true;
                        }
                        continue;
                    }
                    int seat = Array.FindIndex(_setup.Seats, 1, s => !s.Used);
                    if (seat < 0) seat = Array.FindLastIndex(_setup.Seats, s => s.Used && s.Bot);
                    if (seat <= 0) continue; // (LanSession takes no more joiners than the seats)
                    MatchPlayer p = j.Player;
                    _setup.Seats[seat] = new Seat
                    {
                        Used = true, Ready = true, Remote = true, Hunter = p.Hunter, Name = p.Nickname, Rank = p.RankLevel,
                        Skin = p.Skin, Character = p.Character, JoinerId = j.Id, ColourPick = p.ColourPick,
                        Team = FewerTeam() // (team games only)
                    };
                    Log?.Invoke($"multiplayer: lan: {p.Nickname} ({p.Hunter}) joined, seat {seat + 1}");
                    changed = true;
                }
                if (changed) Lobby(menu);
            }
            if (++_lanTick % 30 != 0) return;
            bool allowed = LanAllowed();
            if (allowed && lan == null) HostLan();
            else if (lan is { IsHost: true })
            {
                if (!allowed && JoinerSeat() < 0) EndLan();
                else
                {
                    MatchSettings lobby = BuildMatch();
                    lan.UpdateTemplate(lobby); // (the beacons show the lobby's mode and arena)
                    lan.SendLobby(lobby, JoinerSeats()); // (each joiner's lobby shows this one's seats)
                }
            }
            HostHint(menu.Page!);
        }

        // each joiner's seat in the match (the used seats in order, as BuildMatch adds them): joiner id -> seat
        private static Dictionary<int, int> JoinerSeats()
        {
            var joiners = new Dictionary<int, int>();
            int index = 0;
            foreach (Seat s in _setup.Seats.Where(s => s.Used))
            {
                if (s.Remote && s.JoinerId > 0) joiners[s.JoinerId] = index;
                index++;
            }
            return joiners;
        }

        // a joiner's team in a team game: the side with fewer players
        private static int FewerTeam()
        {
            int orange = _setup.Seats.Count(s => s.Used && s.Team == 0), green = _setup.Seats.Count(s => s.Used && s.Team == 1);
            return green < orange ? 1 : 0;
        }

        // the joiner's lobby: the host's answer, its START, or its BYE
        private void JoinTick(MenuEngine menu)
        {
            LanSession? lan = Lan;
            if (_joinState == -1)
            {
                string why = _joinFailure.Length > 0 ? StreamerPrivacy.Scrub(_joinFailure).ToLowerInvariant() : "no answer from that game.";
                Log?.Invoke("multiplayer: lan: join failed: " + _joinFailure);
                EndLan();
                Popup?.Inform(why);
                menu.GoTo(_joinFromKeypad && _keypad ? AddressPage : CreateJoinPage); // (an address typed: back to the keypad)
                return;
            }
            if (_joinState != 2 || lan == null || lan.IsHost) return;
            if (lan.PeerLeft)
            {
                Log?.Invoke("multiplayer: lan: the host left");
                EndLan();
                Popup?.Inform("the host left.");
                menu.GoTo(CreateJoinPage);
                return;
            }
            if (!_joinShown && lan.Settings != null)
            {
                _joinShown = true;
                _joinLobbyVersion = lan.LobbyVersion;
                LoadSetup(lan.LobbySettings ?? lan.Settings);
                JoinLobby(menu);
                Preview(menu, menu.Page!);
                Log?.Invoke($"multiplayer: lan: seated as seat {lan.Settings.LocalSeat + 1} of {lan.Settings.Players.Count}");
            }
            else if (_joinShown && !lan.Started && lan.LobbyVersion != _joinLobbyVersion && lan.LobbySettings is MatchSettings lobby)
            {
                // the host's lobby changed (LanSession.SendLobby: bots, seats, mode, arena)
                _joinLobbyVersion = lan.LobbyVersion;
                int before = _setup.Seats.Count(s => s.Used);
                LoadSetup(lobby);
                JoinLobby(menu);
                Preview(menu, menu.Page!);
                if (_setup.Seats.Count(s => s.Used) != before)
                {
                    Log?.Invoke($"multiplayer: lan: the host's lobby: seat {lobby.LocalSeat + 1} of {lobby.Players.Count}");
                }
            }
            if (_joinShown && !lan.Started && ++_lanTick % 30 == 0 && lan.LobbySettings is MatchSettings mine
                && mine.LocalSeat >= 0 && mine.LocalSeat < mine.Players.Count && mine.Players[mine.LocalSeat].ColourPick != _setup.MyColour)
            {
                lan.SendLook(_setup.MyColour); // (the badge's pick until the host's LOBBY shows it: UDP)
            }
            if (lan.Started && _pendingInto == -1)
            {
                // the host's START GAME: the same way into the match as the host's (loading page, then the fade)
                _pendingInto = -2;
                menu.GoTo(LoadingPage);
            }
        }

        // ---- JOIN BY ADDRESS (decision 6; owner 2026-10-07 12:40: the ROM's keypad) ----
        //
        // AVAILABLE GAMES' last row reads "enter address" (every platform, JoinByAddress; owner 13:26: Android too, the way in
        // when a network blocks the hosts' beacons) and opens ENTER NEW CODE
        // (61, the Wi-Fi friend-code keypad) as ours: YOUR CODE shows this device's addresses, NEW CODE the address being
        // typed, which starts as the last one joined or this network's first three numbers; OK shows once it's a whole
        // address and joins it the way a row does (SELECT HUNTER, then the "please wait" lobby; a failed join comes back
        // here). The page is a recomp copy at index 61 (MenuFile.ReplacePage: its sounds and music stay the ROM's) whose back
        // goes to CREATE OR JOIN instead of the friend roster (60), with our texts as new strings (the ROM's are shared with
        // the other Wi-Fi pages) and a "." key in the keypad's empty bottom-middle spot: a copy of the "2" key that sounds
        // like it. Each key's digit is the digit text in the same row and column. The host's lobby (38) adds its address to
        // its hint while it's listed on the network (every platform: anyone may be joining from Windows).
        public const int AddressPage = 61;
        public static bool JoinByAddress { get; set; } = true;
        private const int DotCall = 950; // ours: the "." key (the menu's own calls stay under 300, the recomp's from 1000)
        private static bool _keypad; // page 61 is the keypad (Owns)
        private int _addressRow = -1; // the AVAILABLE GAMES row that opens it, -1 = none
        private readonly Dictionary<int, char> _keyDigits = new(); // a key's call -> its digit
        private int _deleteCall = -1, _okCall = -1, _okItem = -1, _okHighlight = -1;
        private int[] _signals = Array.Empty<int>(); // the Wi-Fi strength icons: none for a LAN
        // our strings on the copy: YOUR CODE's name line and three rows, NEW CODE's
        private int _sYourName = -1, _sHostName = -1;
        private readonly int[] _sYours = { -1, -1, -1 }, _sTyped = { -1, -1, -1 };
        private static string _typed = "", _lastAddress = "";
        private static bool _joinFromKeypad;
        private string? _hintRom;

        private void BindAddress(MenuFile file, MenuStrings strings, MenuPage create)
        {
            _keypad = false;
            _addressRow = -1;
            const int row = 3;
            if (!JoinByAddress || file.Pages.Count <= AddressPage || _rowBar[row] < 0 || _rowText[row] < 0) return;
            MenuPage rom = file.Pages[AddressPage];
            string TextOf(MenuItem it) => strings[it.States[0].Text!.StringId];
            int[] Texts(Func<string, bool> match) => Enumerable.Range(0, rom.Items.Count)
                .Where(i => rom.Items[i].IsText && match(TextOf(rom.Items[i]))).OrderByDescending(i => rom.Items[i].Y).ToArray();
            int Single(Func<string, bool> match) => Texts(match) is { Length: 1 } t ? t[0] : -1;
            int[] keys = Enumerable.Range(0, rom.Items.Count)
                .Where(i => HasWidget(file, rom.Items[i], "numkey") && Calls(rom.Items[i]).Any()).ToArray();
            int[] digits = Texts(s => s.Length == 1 && s[0] is >= '0' and <= '9');
            int delete = FindItem(rom, it => HasWidget(file, it, "numback") && Calls(it).Any());
            int okHighlight = FindItem(rom, it => HasWidget(file, it, "small_highlight") && Calls(it).Any());
            int ok = FindItem(rom, it => HasWidget(file, it, "ok"));
            int prompt = Single(s => s.StartsWith("enter the code of someone", StringComparison.Ordinal));
            int title = Single(s => s == "ENTER NEW CODE"), yours = Single(s => s == "YOUR CODE"), theirs = Single(s => s == "NEW CODE");
            int yourName = Single(s => s == "yourname"), hostName = Single(s => s == "xxxxxxxxxx");
            int[] yourRows = Texts(s => s is "1234" or "2345" or "3456"), typedRows = Texts(s => s == "XXXX");
            int[] needed = { delete, okHighlight, ok, prompt, title, yours, theirs, yourName, hostName };
            if (keys.Length != 10 || digits.Length != 10 || yourRows.Length != 3 || typedRows.Length != 3 || needed.Any(i => i < 0))
            {
                Log?.Invoke("multiplayer: no join by address (this ROM's ENTER NEW CODE keypad isn't as expected)");
                return;
            }
            // the digit on each key: the keys and the digit texts each form 4 rows and 3 columns (both y up)
            int[] keyRow = Ranks(keys.Select(i => rom.Items[i].Y), descending: true);
            int[] keyCol = Ranks(keys.Select(i => rom.Items[i].X), descending: false);
            int[] digitRow = Ranks(digits.Select(i => rom.Items[i].Y), descending: true);
            int[] digitCol = Ranks(digits.Select(i => rom.Items[i].X), descending: false);
            var keyOf = new Dictionary<char, int>();
            _keyDigits.Clear();
            for (int k = 0; k < keys.Length; k++)
            {
                int d = Enumerable.Range(0, digits.Length).FirstOrDefault(j => digitRow[j] == keyRow[k] && digitCol[j] == keyCol[k], -1);
                if (d < 0) break;
                char digit = TextOf(rom.Items[digits[d]])[0];
                keyOf[digit] = keys[k];
                _keyDigits[Calls(rom.Items[keys[k]]).First()] = digit;
            }
            if (keyOf.Count != 10 || !keyOf.ContainsKey('0') || !keyOf.ContainsKey('2'))
            {
                Log?.Invoke("multiplayer: no join by address (the keypad's keys and digits don't line up)");
                return;
            }
            _deleteCall = Calls(rom.Items[delete]).First();
            _okCall = Calls(rom.Items[okHighlight]).First();
            _okItem = ok;
            _okHighlight = okHighlight;
            _signals = Enumerable.Range(0, rom.Items.Count).Where(i => Enumerable.Range(1, 4)
                .Any(n => HasWidget(file, rom.Items[i], "wifi" + n) || HasWidget(file, rom.Items[i], "wireless" + n))).ToArray();

            // the copy: every item in the ROM's order (so the data's links and actions still reach them), back -> 26
            if (!rom.Items.Any(it => Calls(it).Contains(DotCall)))
            {
                var ours = new Dictionary<int, int>
                {
                    [prompt] = strings.Add("enter the address of the game you want to join. the host's lobby shows its address."),
                    [title] = strings.Add("ENTER ADDRESS"),
                    [yours] = strings.Add("YOUR ADDRESS"),
                    [theirs] = strings.Add("HOST ADDRESS"),
                    [yourName] = strings.Add(""),
                    [hostName] = strings.Add(""),
                };
                foreach (int i in yourRows.Concat(typedRows)) ours[i] = strings.Add("");
                MenuAction Back(MenuAction a) => a.TargetPage == 0xFF ? a : CopyAction(a, target: CreateJoinPage);
                MenuPage copy = file.ReplacePage(AddressPage, index => new MenuPage
                {
                    Index = index, Field0 = rom.Field0, Field14 = rom.Field14, Field16 = rom.Field16, Field18 = rom.Field18,
                    Field1D = rom.Field1D, Field1E = rom.Field1E, Flags = rom.Flags, Items = Array.Empty<MenuItem>(),
                    Actions = rom.Actions.Select(Back).ToArray(), Timers = rom.Timers
                });
                foreach (MenuItem it in rom.Items)
                {
                    copy.AddItem(i => RecompMenus.Clone(it, i, 0, 0, stringId: ours.TryGetValue(i, out int s) ? s : -1,
                        links: it.Links.ToList(), actions: it.Actions.Select(Back).ToList()));
                }
                // the "." key under the "2", level with the "0", and its label
                MenuItem two = rom.Items[keyOf['2']], twoText = rom.Items[digits[Array.FindIndex(digits, i => TextOf(rom.Items[i]) == "2")]];
                MenuItem zeroText = rom.Items[digits[Array.FindIndex(digits, i => TextOf(rom.Items[i]) == "0")]];
                float dy = rom.Items[keyOf['0']].Y - two.Y;
                MenuAction press = two.Actions.First(a => a.Calls.Count > 0);
                copy.AddItem(i => SoundsLike(RecompMenus.Clone(two, i, 0, dy, links: Array.Empty<MenuLink>(),
                    actions: new[] { CopyAction(press, calls: new[] { (0, DotCall) }, item: i) }), two.Index));
                copy.AddItem(i => RecompMenus.Clone(twoText, i, 0, zeroText.Y - twoText.Y, stringId: strings.Add(".")));
                rom = copy;
            }
            _sYourName = StringOf(rom, yourName);
            _sHostName = StringOf(rom, hostName);
            for (int k = 0; k < 3; k++)
            {
                _sYours[k] = StringOf(rom, yourRows[k]);
                _sTyped[k] = StringOf(rom, typedRows[k]);
            }
            // the last AVAILABLE GAMES row: its bar and its join highlight open the keypad instead of SELECT HUNTER
            for (int i = 0; i < create.Items.Count; i++)
            {
                MenuItem it = create.Items[i];
                if (!it.Actions.Any(a => a.TargetPage == JoinHunterPage && a.Calls.Any(c => c.B == 95 + row))) continue;
                create.ReplaceItem(i, n => RecompMenus.Clone(it, n, 0, 0, links: it.Links.ToList(), actions: it.Actions
                    .Select(a => a.TargetPage == JoinHunterPage ? CopyAction(a, target: AddressPage) : a).ToList()));
            }
            _addressRow = row;
            _keypad = true;
        }

        // the rows (or columns) a set of positions makes, a few pixels apart counting as one: each position's rank
        private static int[] Ranks(IEnumerable<float> values, bool descending)
        {
            float[] v = values.ToArray();
            var levels = new List<float>();
            foreach (float x in descending ? v.OrderByDescending(x => x) : v.OrderBy(x => x))
            {
                if (levels.Count == 0 || MathF.Abs(levels[^1] - x) > 4) levels.Add(x);
            }
            return v.Select(x => levels.FindIndex(l => MathF.Abs(l - x) <= 4)).ToArray();
        }

        // an action with other calls, item or target page
        private static MenuAction CopyAction(MenuAction a, IReadOnlyList<(int A, int B)>? calls = null, int item = -1,
            int target = -1) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = a.RectX, RectY = a.RectY, RectW = a.RectW,
            RectH = a.RectH, Calls = calls ?? a.Calls, Item = item >= 0 ? (ushort)item : a.Item,
            TargetPage = target >= 0 ? (byte)target : a.TargetPage, Field17 = a.Field17
        };

        // An item's Index is its entry in the page's sound list (MenuEngine.ActionSound, MenuSounds): a copy that sounds like
        // the ROM item at `soundOf`
        private static MenuItem SoundsLike(MenuItem it, int soundOf) => new()
        {
            Index = soundOf, Delay = it.Delay, Field18 = it.Field18, Field1C = it.Field1C, X = it.X, Y = it.Y, StretchX = it.StretchX,
            StretchDown = it.StretchDown, Depth = it.Depth, Field2C = it.Field2C, Field30 = it.Field30, Field34 = it.Field34,
            Field35 = it.Field35, Flags = it.Flags, Field39 = it.Field39, Field3A = it.Field3A, States = it.States, Links = it.Links,
            Actions = it.Actions
        };

        private void EnterAddress(MenuEngine menu)
        {
            if (Lan == null) StartBrowse(); // (back after a failed join: the session that asks)
            // (STREAMING: addresses hidden = nothing typed, masked, and the keys only say why; owner 10-28)
            _typed = StreamerPrivacy.HideAddress ? "" : _lastAddress.Length > 0 ? _lastAddress : NetworkPrefix();
            foreach (int item in _signals) ShowAtEntry(menu, item, false);
            RefreshAddress(menu, entering: true);
        }

        private bool OnAddressCall(MenuEngine menu, int b)
        {
            if (StreamerPrivacy.HideAddress && (_keyDigits.ContainsKey(b) || b == DotCall || b == _deleteCall || b == _okCall))
            {
                Popup?.Inform(StreamerPrivacy.AddressLocked);
                return true;
            }
            if (_keyDigits.TryGetValue(b, out char digit)) Type(digit);
            else if (b == DotCall) Type('.');
            else if (b == _deleteCall) _typed = _typed.Length > 0 ? _typed[..^1] : "";
            else if (b == _okCall)
            {
                JoinAddress(menu);
                return true;
            }
            else if (b is 17 or 294) return true; // back (the copy's data goes to CREATE OR JOIN); the keyboard mask arriving
            else return false;
            RefreshAddress(menu);
            return true;
        }

        // a digit or "."; a number that can't take the digit (three digits, over 255, a leading 0) gets its "." first
        private static void Type(char c)
        {
            string[] parts = _typed.Split('.');
            string last = parts[^1];
            if (c == '.')
            {
                if (last.Length > 0 && parts.Length < 4) _typed += ".";
                return;
            }
            bool fits = last.Length < 3 && last != "0" && Int32.Parse(last + c, CultureInfo.InvariantCulture) <= 255;
            if (fits) _typed += c;
            else if (last.Length > 0 && parts.Length < 4) _typed += "." + c;
        }

        private static bool TryAddress(string text, out IPAddress? address)
        {
            address = null;
            string[] parts = text.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length > 0) && IPAddress.TryParse(text, out address);
        }

        // the boxes' texts, and OK once a whole address is typed; a game heard at that address puts its name over it
        private void RefreshAddress(MenuEngine menu, bool entering = false)
        {
            MenuStrings s = _strings!;
            List<string> mine = LocalAddresses();
            string[] rows = mine.Count switch
            {
                0 => new[] { "", "offline", "" },
                1 => new[] { "", mine[0], "" },
                2 => new[] { mine[0], mine[1], "" },
                _ => mine.Take(3).ToArray(),
            };
            s.Set(_sYourName, StreamerPrivacy.Name(_setup.Nickname, mine: true).ToLowerInvariant());
            for (int k = 0; k < 3; k++)
            {
                // STREAMING: addresses hidden = this device's and the typed one read "hidden"
                s.Set(_sYours[k], rows[k].Contains('.') ? StreamerPrivacy.Address(rows[k]) : rows[k]);
                s.Set(_sTyped[k], k != 1 ? "" : StreamerPrivacy.HideAddress ? StreamerPrivacy.AddressMask : _typed);
            }
            bool whole = TryAddress(_typed, out IPAddress? ip);
            LanHostInfo? heard = whole ? Lan?.Hosts.FirstOrDefault(h => h.Address.Address.Equals(ip)) : null;
            string name = heard?.Name.ToLowerInvariant() ?? "";
            s.Set(_sHostName, name.Length > 10 ? name[..10] : name);
            foreach (int item in new[] { _okItem, _okHighlight })
            {
                if (entering) ShowAtEntry(menu, item, whole);
                else Show(menu, item, whole);
            }
        }

        // OK: that address's game (as heard, if it was) for SELECT HUNTER, then the same join as a row's
        private void JoinAddress(MenuEngine menu)
        {
            if (!TryAddress(_typed, out IPAddress? ip) || ip == null) return;
            _lastAddress = _typed;
            if (_license.LastAddress != _typed)
            {
                _license.LastAddress = _typed;
                _license.Save(_profileDir);
            }
            if (Lan == null) StartBrowse();
            LanHostInfo? heard = Lan?.Hosts.FirstOrDefault(h => h.Address.Address.Equals(ip));
            _joinTarget = heard ?? new LanHostInfo { Address = new IPEndPoint(ip, LanSession.GamePort) };
            _joinFromKeypad = true;
            if (heard != null)
            {
                LoadBeacon(heard);
            }
            else
            {
                // not heard: the boxes stay empty until its lobby answers with the game
                _setup.ModeChosen = false;
                _setup.RulesSeen = false;
                _setupFromHost = true;
                HostExtraRules(true, true);
            }
            Log?.Invoke($"multiplayer: lan: join by address {ip}" + (heard != null ? $" (heard: {heard.Name})" : ""));
            menu.GoTo(JoinHunterPage);
        }

        // this network's first three numbers, to start the address from
        private static string NetworkPrefix()
        {
            string? mine = LocalAddresses().FirstOrDefault();
            return mine == null ? "" : mine[..(mine.LastIndexOf('.') + 1)];
        }

        // this device's IPv4 addresses: those on a network with a gateway (the Wi-Fi or Ethernet the other players are on)
        // first, then the rest (a VPN); no loopback or self-assigned (169.254) ones
        public static List<string> LocalAddresses()
        {
            var found = new List<(string Address, bool Gateway)>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool gateway = false;
                    try
                    {
                        gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                            && !g.Address.Equals(IPAddress.Any));
                    }
                    catch (Exception)
                    {
                        // (not every platform reports gateways)
                    }
                    foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                        byte[] b = a.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        found.Add((a.Address.ToString(), gateway));
                    }
                }
            }
            catch (Exception)
            {
                // no network information: nothing to show
            }
            return found.OrderByDescending(f => f.Gateway).Select(f => f.Address).Distinct().ToList();
        }

        // the host's lobby: "you can wait for more players or add bots." plus this device's address while it's listed
        private void HostHint(MenuPage p)
        {
            if (_hint < 0 || p.Index != LobbyPage) return;
            int id = StringOf(p, _hint);
            _hintRom ??= _strings![id];
            string? address = Hosting ? LocalAddresses().FirstOrDefault() : null;
            _strings!.Fill(id, address == null ? _hintRom : $"{_hintRom}\naddress: {StreamerPrivacy.Address(address)}");
        }

        // ---- START GAME ----

        private void Start(MenuEngine menu)
        {
            if (!(UsedSeats > 1 && _setup.Seats.All(s => !s.Used || s.Ready) && TeamsLegal())) return;
            if (LackingArena() is { Count: > 0 } lacking)
            {
                // (owner 2026-10-10, N27: a player without the arena would fail to load it and the match start without them)
                string arena = ExtraArenas.Label(Metadata.GetRoomById(ArenaRooms[_setup.Arena])!.Name);
                Popup?.Inform($"{string.Join(", ", lacking).ToLowerInvariant()} {(lacking.Count == 1 ? "doesn't" : "don't")} have {arena}.\n\npick another arena.");
                return;
            }
            _pendingInto = -2;
            menu.GoTo(LoadingPage);
        }

        // LAN host: the joiners who don't have the lobby's arena (RANDOM: nobody, it picks around them)
        private static List<string> LackingArena()
        {
            if (Lan is not { IsHost: true } lan || ArenaRooms[_setup.Arena] == RandomArena) return new List<string>();
            return Metadata.GetRoomById(ArenaRooms[_setup.Arena], noThrow: true) is RoomMetadata room ? lan.Lacking(room.Name) : new List<string>();
        }

        // LanSession.ArenaCheck: a JOIN for the lobby's arena (RANDOM: any joiner, it picks around them)
        private static string? JoinArenaCheck(IReadOnlyCollection<string>? arenas)
        {
            int[] rooms = ArenaRooms;
            int at = _setup.Arena;
            if (at < 0 || at >= rooms.Length || rooms[at] == RandomArena) return null;
            return Metadata.GetRoomById(rooms[at], noThrow: true) is RoomMetadata room ? ExtraArenas.Refusal(arenas, room.Name) : null;
        }

        // the setup as the match screen takes it
        public static MatchSettings BuildMatch()
        {
            ModeInfo m = CurrentMode;
            int arena = _setup.Arena;
            if (ArenaRooms[arena] == RandomArena)
            {
                // (N27: only arenas every LAN joiner has)
                int[] allowed = Enumerable.Range(0, ArenaRooms.Length).Where(i => ArenaRooms[i] != RandomArena && ArenaAllowed(i)
                    && (Lan is not { IsHost: true } lan || Metadata.GetRoomById(ArenaRooms[i], noThrow: true) is not RoomMetadata r || lan.AllHave(r.Name))).ToArray();
                arena = allowed[_random.Next(allowed.Length)];
            }
            RoomMetadata room = Metadata.GetRoomById(ArenaRooms[arena])!;
            var settings = new MatchSettings
            {
                Mode = _setup.TeamPlay ? m.Teams : m.Solo,
                Arena = room.Name,
                LocalSeat = 0,
                TimeLimitSeconds = ClampTo(TimeLimitMinutes, _setup.TimeLimit) * 60,
                DamageLevel = _setup.Damage,
                RadarPlayers = _setup.Radar,
                AffinityWeapons = _setup.Affinity,
                Abilities = true, // the Trooper's Wall Cling + Scout Drone in LAN matches too (T5 N31; LanSession.Build / NetMatchSession already do)
            };
            if (m.Solo != GameMode.PrimeHunter) settings.FriendlyFire = _setup.FriendlyFire;
            foreach (Rule rule in m.Rules)
            {
                switch (rule)
                {
                case Rule.PointGoal:
                case Rule.NodesGoal:
                case Rule.OctolithGoal:
                case Rule.Lives:
                    settings.PointGoal = ClampTo(GoalTable(rule), _setup.Goal);
                    break;
                case Rule.TimeGoal:
                    settings.TimeGoalSeconds = ClampTo(TimeGoalSeconds, _setup.TimeGoal);
                    break;
                case Rule.AutoReset:
                    settings.OctolithReset = _setup.AutoReset;
                    break;
                }
            }
            bool teams = MatchSettings.IsTeamMode(settings.Mode);
            foreach (Seat s in _setup.Seats.Where(s => s.Used))
            {
                settings.Players.Add(new MatchPlayer
                {
                    Hunter = s.Hunter,
                    Bot = s.Bot,
                    Remote = s.Remote,
                    BotLevel = s.Level,
                    Team = teams ? s.Team : -1,
                    // (the match's own HUD text)
                    Nickname = s.Bot ? BotNickname(s) : s.Remote ? s.Name : _setup.Nickname,
                    RankLevel = s.Bot ? -1 : s.Remote ? s.Rank : _setup.Rank,
                    // the same hunter twice: tell them apart (as LanSession's WELCOME does)
                    Recolor = s.Remote ? Math.Min(settings.Players.Count(p => p.Hunter == s.Hunter), 3) : 0,
                    Skin = s.Skin,
                    Character = s.Character,
                    ColourPick = s.ColourPick,
                });
            }
            ApplyExtraRules(settings);
            return settings;
        }

        // an online match's way in: the same loading page and fade LAN's START takes, then `start` instead of a LAN setup
        private Action? _intoStart;
        public void BeginIntoGame(MenuEngine menu, Action start)
        {
            _intoStart = start;
            _pendingInto = -2;
            menu.GoTo(LoadingPage);
        }

        private void StartMatch(MenuEngine menu)
        {
            if (_intoStart is { } online)
            {
                _intoStart = null;
                online();
                return;
            }
            try
            {
                LanSession? lan = Lan;
                if (lan is { IsHost: false, Settings: not null })
                {
                    // a joiner: the host's match (its START), played through LanSession.Current
                    MatchSettings joined = lan.Settings;
                    MatchResults.Last = null;
                    PendingReturn = _resultsBound ? ResultsPage : CreateJoinPage;
                    _lastWasJoiner = true;
                    _lastHost = _joinTarget;
                    ArmMatch(joined, IsInternet(_joinTarget?.Address.Address));
                    Log?.Invoke($"multiplayer: lan: start {joined.Mode} in {joined.Arena} as seat {joined.LocalSeat}");
                    Request?.Invoke(new FrontendRequest(FrontendRequestKind.StartMatch, Arg: MatchSetupText.Write(joined, "client")));
                    RestoreOwnSeats();
                    return;
                }
                MatchSettings settings = BuildMatch();
                settings.Validate();
                Dictionary<int, int> joiners = JoinerSeats();
                string? net = null;
                if (lan is { IsHost: true } && joiners.Count > 0)
                {
                    lan.SendStart(settings, joiners); // the joiners' lobbies start on it
                    net = "host";
                }
                else if (lan != null)
                {
                    EndLan(); // (a bot match: nobody joined)
                }
                MatchResults.Last = null; // the match screen leaves this one's (MatchSession)
                PendingReturn = _resultsBound ? ResultsPage : LobbyPage;
                _lastWasJoiner = false;
                ArmMatch(settings, net != null && IsInternet(lan?.Peer?.Address));
                // the confirmed bots stay seated for the next match, as vanilla's PLAY AGAIN keeps them; the joiner doesn't
                // (the next game is hosted again)
                for (int i = 1; i < _setup.Seats.Length; i++) if (_setup.Seats[i].Remote) _setup.Seats[i] = new Seat();
                Log?.Invoke($"multiplayer: start {settings.Mode} in {settings.Arena}, {settings.Players.Count} players{(net != null ? ", lan host" : "")}");
                Request?.Invoke(new FrontendRequest(FrontendRequestKind.StartMatch, Arg: MatchSetupText.Write(settings, net)));
            }
            catch (Exception ex)
            {
                Log?.Invoke("multiplayer: can't start: " + ex.Message);
                IntoGameDim = -1;
                // (2026-10-21 hotfix: this runs on the black fade page; staying there left a softlock behind the popup)
                menu.GoTo(Lan is { IsHost: false } ? JoinLobbyPage : LobbyPage);
                Popup?.Inform(ex.Message);
            }
        }

        // ---- HUNTER LICENSE (M6, decision 5): the nickname in a player profile, typed on the ROM's name keyboard ----
        //
        // The profile is HunterLicense (ProfileDir: the folder of recomp_settings.json, set by FrontendSession; none = in
        // memory only, and no notice). Arriving on CHOOSE MULTIPLAYER TYPE (call 89) shows vanilla's notice once, its
        // "edit friends and rivals menu" pointed at our door (owner 14:55). The HUNTER LICENSE door opens the ROM's
        // keyboard page 62 in its "new name" use (vanilla's: "enter the new name you wish to use for METROID PRIME
        // HUNTERS."), US keys only (owner 14:55: the EURO and Japanese switches hidden); OK / ENTER keep the name, B /
        // CANCEL leave it, both back to the doors. The license card and the roster (pages 57/60) come later.
        // Vanilla's rules (rev 1 overlay 0, read 2026-10-07: type-char 0x0211B91C, ENTER 0x02129784): at most 10
        // characters, kept as typed (the font draws lower case as small capitals); CAPS turns letters only, SHIFT one key's
        // letter or symbol; an empty or all-spaces name answers "invalid name. ..." and stays on the keyboard.
        public const int NamePage = 62;
        private const int FirstKeyCall = 297, CapsCall = 342, BackspaceCall = 343, EnterCall = 344, ShiftCall = 345, SpaceCall = 346;
        private const int NameOkCall = 6, NameCancelCall = 5;
        // the US keys' characters by call (297-341), in page 62's order (rows 1-4, then ; ' [ ]): plain and SHIFTed
        private const string KeysLower = "1234567890-=qwertyuiopasdfghjklzxcvbnm,./;'[]";
        private const string KeysShift = "!@#$%^&*()_+QWERTYUIOPASDFGHJKLZXCVBNM<>?:~{}";
        private static bool _keyboard; // page 62 is the name keyboard (Owns)
        private int _nameText = -1, _namePrompt = -1, _lowerItem = -1, _capsItem = -1, _upperItem = -1;
        private int _keyMask = -1, _keyDrop = -1; // the keyboard's own screen and the panel behind its keys (KeyboardOverCard)
        private bool _cardSnapshot;
        private int[] _nameHide = Array.Empty<int>();
        private int _sName = -1, _sNoticeHead = -1, _sNoticeTail = -1, _sInvalidName = -1;
        private string _name = "";
        private bool _caps, _shift;
        private HunterLicense _license = new();
        private string? _profileDir;
        private static string? s_profileHd;

        public string? ProfileDir
        {
            get => _profileDir;
            set
            {
                _profileDir = value;
                s_profileHd = value == null ? null : System.IO.Path.Combine(value, "hd"); // (OnlineBotExtras)
                _license = HunterLicense.Load(value);
                LoadExtraRules(value);
                _setup.Nickname = _license.Name;
                _setup.Rank = _license.RankLevel;
                _lastAddress = TryAddress(_license.LastAddress, out _) ? _license.LastAddress : "";
            }
        }

        public HunterLicense License => _license;

        // page 62's pieces, found by text and widget: the new-name text and prompt, the three US layouts, every US key's call
        private void BindKeyboard(MenuFile file, MenuStrings strings)
        {
            _keyboard = false;
            if (file.Pages.Count <= NamePage) return;
            MenuPage page = file.Pages[NamePage];
            string TextOf(MenuItem it) => strings[it.States[0].Text!.StringId];
            int Widget(string name) => FindItem(page, it => HasWidget(file, it, name));
            _nameText = FindItem(page, it => it.IsText && TextOf(it) == "newname here");
            _namePrompt = FindItem(page, it => it.IsText && TextOf(it).StartsWith("enter the new name", StringComparison.Ordinal));
            _lowerItem = Widget("US_lower");
            _capsItem = Widget("US_caps");
            _upperItem = Widget("US_upper");
            _keyMask = Widget("keyboardmask");
            _keyDrop = Widget("blackdrop");
            int[] calls = Enumerable.Range(FirstKeyCall, SpaceCall - FirstKeyCall + 1).Append(NameOkCall).Append(NameCancelCall).ToArray();
            if (_nameText < 0 || _namePrompt < 0 || _lowerItem < 0 || _capsItem < 0 || _upperItem < 0 || !PageCalls(page, calls))
            {
                Log?.Invoke("multiplayer: no name keyboard (page 62 isn't the expected layout)");
                return;
            }
            _sName = page.Items[_nameText].States[0].Text!.StringId;
            // hidden in the new-name use: the chat and a friend's temporary name (their texts and prompts), the layout
            // switches (their icons and keys: EURO, Japanese) and the Wi-Fi strength icons
            _nameHide = Enumerable.Range(0, page.Items.Count).Where(i =>
            {
                MenuItem it = page.Items[i];
                if (it.IsText)
                {
                    string t = TextOf(it);
                    return t is "chat here" or "tempname here" || t.StartsWith("enter a message", StringComparison.Ordinal)
                        || t.StartsWith("enter a temporary name", StringComparison.Ordinal);
                }
                return HasWidget(file, it, "keytoggle") || Calls(it).Any(c => c is 295 or 296)
                    || Enumerable.Range(1, 4).Any(n => HasWidget(file, it, "wifi" + n) || HasWidget(file, it, "wireless" + n));
            }).ToArray();
            _sNoticeHead = Text(strings, t => t == "your nickname is\n");
            _sNoticeTail = Text(strings, t => t.StartsWith("\nyou can change it", StringComparison.Ordinal));
            _sInvalidName = Text(strings, t => t.StartsWith("invalid name.", StringComparison.Ordinal));
            HunterLicense.MaxName = 10;
            _keyboard = true;
        }

        // vanilla's "your nickname is X. you can change it in the edit friends and rivals menu.", once per profile
        private void NicknameNotice()
        {
            if (_profileDir == null || _license.NoticeSeen || Popup == null) return;
            _license.NoticeSeen = true;
            _license.Save(_profileDir);
            string head = _sNoticeHead >= 0 ? _strings![_sNoticeHead] : "your nickname is\n";
            string tail = _sNoticeTail >= 0 ? _strings![_sNoticeTail] : "\nyou can change it in the hunter license.";
            tail = tail.Replace("the edit friends and rivals menu", "the hunter license", StringComparison.Ordinal);
            Log?.Invoke($"multiplayer: nickname notice ({_license.Name})");
            Popup.Inform(head + StreamerPrivacy.Name(_license.Name, mine: true).ToUpperInvariant() + tail);
        }

        // The name keyboard for another screen (Replay mode's RENAME, owner 2026-10-08 "go ahead with that hook"): `start` already
        // typed, up to `max` letters, the ROM's prompt without its "for METROID PRIME HUNTERS."; done gets the text (OK; blank
        // refused as for the nickname) or null (CANCEL / B), then the menu goes back to `returnPage` (the usual page change)
        public void EditText(MenuEngine menu, string start, int max, int returnPage, Action<string?> done)
        {
            if (!_keyboard)
            {
                done(null);
                return;
            }
            _textMax = Math.Max(1, max);
            _textStart = start.Length > _textMax ? start[.._textMax] : start;
            _textDone = done;
            _nameReturn = returnPage;
            menu.GoTo(NamePage);
        }

        private Action<string?>? _textDone; // EditText's caller while its keyboard is up (null: the nickname)
        private string _textStart = "";
        private int _textMax;
        private string? _promptRom; // the ROM's prompt, as the nickname shows it
        private int NameMax => _textDone != null ? _textMax : HunterLicense.MaxName;

        private void EnterName(MenuEngine menu)
        {
            _name = _textDone != null ? _textStart : ""; // (vanilla's box starts empty: BizHawk, EDIT NAME)
            _caps = _shift = false;
            if (_namePrompt >= 0)
            {
                int id = menu.Page!.Items[_namePrompt].States[0].Text!.StringId;
                _promptRom ??= _strings![id];
                // (kept on two lines, broken as the ROM's: one line would sit above the keyboard layout's band and not show)
                int cut = _promptRom.IndexOf(" for", StringComparison.Ordinal), you = _promptRom.IndexOf(" you ", StringComparison.Ordinal);
                int metroid = _promptRom.IndexOf("METROID", StringComparison.Ordinal);
                string text = _promptRom;
                if (_textDone != null && you > 0 && cut > you && metroid > cut + 4)
                {
                    text = _promptRom[..you] + _promptRom[(cut + 4)..metroid] + _promptRom[(you + 1)..cut] + ".";
                }
                _strings!.Fill(id, text);
            }
            foreach (int item in _nameHide) ShowAtEntry(menu, item, false);
            ShowAtEntry(menu, _namePrompt, true);
            ShowAtEntry(menu, _nameText, true);
            Layout(menu, entering: true);
            RefreshName();
        }

        // The name keyboard opened from the license's EDIT NAME floats over the card (owner 2026-10-08: "when you hit edit name
        // theres some screen that is behind the keyboard can we just get the keyboard floating over the license"): its own
        // screen isn't drawn (ReplacePreview) and FrontendSession draws the card as it was when EDIT NAME was pressed,
        // dimmed, behind it. Opened from the doors (no card), the keyboard keeps its own screen.
        public bool KeyboardOverCard(int page) => page == NamePage && _keyboard && _card && _nameReturn == LicensePage;

        // true once, right after EDIT NAME: FrontendSession keeps the last frame it drew (the card) to draw under the keyboard
        public bool TakeCardSnapshot()
        {
            bool take = _cardSnapshot;
            _cardSnapshot = false;
            return take;
        }

        // the US layout on show: SHIFT's, else CAPS', else the lower case one
        private void Layout(MenuEngine menu, bool entering = false)
        {
            foreach ((int item, bool on) in new[] { (_lowerItem, !_caps && !_shift), (_capsItem, _caps && !_shift), (_upperItem, _shift) })
            {
                if (entering) ShowAtEntry(menu, item, on);
                else Show(menu, item, on);
            }
        }

        // the name as typed, with a cursor while there's room
        private void RefreshName() => _strings!.Fill(_sName, _name + (_name.Length < NameMax ? "_" : ""));

        private bool OnNameCall(MenuEngine menu, int b)
        {
            if (b >= FirstKeyCall && b < FirstKeyCall + KeysLower.Length)
            {
                int k = b - FirstKeyCall;
                char c = _typedChar ?? (_shift ? KeysShift[k] : KeysLower[k]);
                if (_typedChar == null && _caps && !_shift && char.IsLetter(c)) c = char.ToUpperInvariant(c);
                Type(c);
                if (_shift && _typedChar == null)
                {
                    _shift = false; // (one key)
                    Layout(menu);
                }
            }
            else if (b == SpaceCall)
            {
                Type(' ');
            }
            else if (b == BackspaceCall)
            {
                if (_name.Length > 0) _name = _name[..^1];
            }
            else if (b == CapsCall)
            {
                _caps = !_caps;
                _shift = false;
                Layout(menu);
            }
            else if (b == ShiftCall)
            {
                _shift = !_shift;
                Layout(menu);
            }
            else if (b is NameOkCall or EnterCall)
            {
                if (_name.Trim().Length == 0)
                {
                    // vanilla: "invalid name. please enter a name that has characters other than just spaces."
                    if (_sInvalidName >= 0) Popup?.Inform(_strings![_sInvalidName]);
                    return true;
                }
                if (_textDone != null) // EditText: the caller's text, not the nickname
                {
                    Action<string?> done = _textDone;
                    _textDone = null;
                    LeaveName(menu);
                    done(_name.Trim());
                    return true;
                }
                _license.Name = HunterLicense.Clean(_name);
                _license.Save(_profileDir);
                _setup.Nickname = _license.Name;
                Log?.Invoke($"multiplayer: nickname {_license.Name}");
                LeaveName(menu);
                return true;
            }
            else if (b == NameCancelCall)
            {
                Action<string?>? done = _textDone;
                _textDone = null;
                LeaveName(menu);
                done?.Invoke(null);
                return true;
            }
            else
            {
                return b is 295 or 296; // (the hidden layout switches)
            }
            RefreshName();
            return true;

            void Type(char c)
            {
                if (_name.Length < NameMax) _name += c;
            }
        }

        // ---- A PC keyboard on the name keyboard and the address keypad (owner 2026-10-16: "actual keyboard support for
        //      Windows") ----
        //
        // A typed character is a tap on the on-screen key that makes it (its sound and call, the key focused as a tap
        // leaves it); the character itself goes in as typed, whatever CAPS / SHIFT show (a key the screen doesn't have, e.g.
        // a " or an accented letter, is refused). Backspace is the screen's backspace / delete key, Enter its ENTER / OK
        // (the keypad's OK only once it shows: a whole address). B / CANCEL stay where they were (Esc on Windows).

        public bool TypingPage(int page) => page == NamePage && _keyboard || page == AddressPage && _keypad;

        private char? _typedChar; // OnNameCall: the character typed on a PC keyboard, in place of the key's own

        public bool TypeChar(MenuEngine menu, char c)
        {
            if (menu.Page == null || !TypingPage(menu.Page.Index)) return false;
            if (menu.Page.Index == AddressPage)
            {
                if (c == '.') return TouchCall(menu, DotCall);
                foreach ((int call, char digit) in _keyDigits) if (digit == c) return TouchCall(menu, call);
                return false;
            }
            int k = KeysLower.IndexOf(c);
            if (k < 0) k = KeysShift.IndexOf(c);
            if (k < 0 && c != ' ') return false;
            _typedChar = c;
            try
            {
                return TouchCall(menu, c == ' ' ? SpaceCall : FirstKeyCall + k);
            }
            finally
            {
                _typedChar = null;
            }
        }

        // Backspace (true) or Enter (false)
        public bool TypeKey(MenuEngine menu, bool backspace)
        {
            if (menu.Page == null || !TypingPage(menu.Page.Index)) return false;
            if (menu.Page.Index == AddressPage) return TouchCall(menu, backspace ? _deleteCall : _okCall);
            return TouchCall(menu, backspace ? BackspaceCall : EnterCall);
        }

        // a tap on the page's shown item that makes `call`
        private static bool TouchCall(MenuEngine menu, int call)
        {
            if (call < 0) return false;
            IReadOnlyList<MenuItem> items = menu.Page!.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (Calls(items[i]).Contains(call) && menu.TouchItem(i)) return true;
            }
            return false;
        }

        // OK / CANCEL: back where the keyboard was opened from; to the card at once, whole and on the same tab (it was behind
        // the keyboard all along), else the usual page change
        private void LeaveName(MenuEngine menu)
        {
            if (_nameReturn != LicensePage)
            {
                menu.GoTo(_nameReturn);
                return;
            }
            _cardKeepTab = true;
            _enterNext = LicensePage;
            _settleNext = true;
        }

        // a page switched to at once (no exit animation) on the next tick, not inside the press that asked for it (whose
        // key would otherwise go on to the new page's own actions); Settle: its items already in, as left
        private int _enterNext = -1;
        private bool _settleNext;

        private void EnterNext(MenuEngine menu)
        {
            if (_enterNext < 0) return;
            int page = _enterNext;
            bool settle = _settleNext;
            _enterNext = -1;
            _settleNext = false;
            menu.Enter(page);
            if (settle) menu.Settle();
        }

        // ---- THE HUNTER LICENSE CARD (M6; owner 2026-10-07 17:15 "card now, roster later") ----
        //
        // The HUNTER LICENSE door opens the player's own card as vanilla shows it: EDIT FRIENDS AND RIVALS (page 60; BizHawk
        // US rev 1: NINTENDO WFC -> EDIT FRIENDS AND RIVALS, kit m6/license/vanilla). The top screen is the card (ov0
        // 0x02119EC0): the name, the rank's stars and title, five tabs of three stats (L / R, ov0 0x0211A3C8), the hunter of
        // choice, the medal for total wins, the frame for 500 matches, the alimbic swirl or octolith (the game beaten / 100%),
        // the ranking points and the next rank's. The touch screen (owner 17:2x: "your part only"; 2026-10-08: "completely
        // eliminating the you are part"): EDIT NAME, always shown, which opens the name keyboard (62) and comes back here;
        // back goes to the doors. "you are:", its panel and the touch screen's backdrop are hidden (the name is on the card),
        // and the ranking points' box is narrowed to its words (owner: "it doesnt need that big black and gray area"). The
        // roster (friends and rivals panels, add friend, delete player) stays hidden until online play. The
        // page is a recomp copy at index 60 (MenuFile.ReplacePage) whose back goes to the doors instead of the Wi-Fi page and
        // whose filled texts are new strings (the ROM's are shared with page 57).
        public const int LicensePage = 60;
        private const int CardTabCall = 3, CardTabNextCall = 4, CardBackCall = 17, CardNameCall = 255, CardEditCall = 281;
        // ours: a tap on a tab picks it (owner 2026-10-08: "can the different tabs on the license be tappable to switch straight to
        // it"; vanilla has only L / R); the action's item is the tab
        private const int CardTabPickCall = 951;
        // a tab's click, L / R and a tap alike (owner 2026-10-24): the option box arrows' sound, as the online and replay
        // license cards' tabs (their items borrow it)
        private const int CardTabSound = 2;
        // the five tabs under the card (top screen, DS px, y up): 25 px apart from x 38, between the L and R icons, y 53..72
        internal const int CardTabX0 = 38, CardTabWidth = 25, CardTabY0 = 53, CardTabY1 = 72;
        private const double MenuTickSeconds = 2 * 560190 / 33513982.0; // FrontendSession's tick
        private static bool _card; // page 60 is the card (Owns)
        private int _cardTab; // vanilla's [0x02147D04 + 0x74] bits 0-2
        private bool _cardKeepTab; // back from EDIT NAME's keyboard: the tab it was left on
        private int _nameReturn = TypePage;
        private int[] _cardHide = Array.Empty<int>(), _cardTabs = Array.Empty<int>(), _cardShow = Array.Empty<int>();
        private int[] _rankTitle = Array.Empty<int>(), _rankStar = Array.Empty<int>(), _medals = Array.Empty<int>();
        private int[] _portraits = Array.Empty<int>(), _edit = Array.Empty<int>();
        private int _headerGold = -1, _frame = -1, _frameGold = -1, _swirl = -1, _octolith = -1, _nameBlack = -1, _nameGold = -1;
        private int _sCardName = -1, _sPoints = -1, _sNext = -1;
        // the ranking points' box (two plain quads, x 0..191 and 192..256) narrowed by this much, its words moved with its
        // right half: "ranking points:" then sits 6 px in from the box's left edge, as the numbers do from its right
        private const float RankBoxNarrower = 96;
        // ... and its top brought down by this much (y 38 -> 27: 4 px over "ranking points:", as under "next rank:")
        private const float RankBoxLower = 11;
        private readonly int[] _sTitle = { -1, -1, -1 }, _sNum = { -1, -1, -1 };
        private double _unsavedSeconds;

        // the card's words, found by text (rev 1's ids differ from the menu dump's: never by number)
        private static readonly string[][] CardTitles =
        {
            new[] { "wi-fi win record", "wireless win record", "connection history" },
            new[] { "win ratio", "win streak", "lucky arena" },
            new[] { "favorite weapon", "headshot kills", "favorite mode" },
            new[] { "biped kills", "alt-form kills", "kill streak" },
            new[] { "wi-fi play time", "wireless play time", "total game time" },
        };
        private static readonly string[] WeaponNames =
            { "power beam", "volt driver", "missile", "battlehammer", "imperialist", "judicator", "magmaul", "shock coil", "omega cannon" };
        private static readonly string[] CardModes = { "battle", "bounty", "capture", "defender", "nodes", "prime hunter", "survival" };
        private static readonly string[] RankTitles = { "bounty hunter", "super hunter", "elite hunter", "master hunter", "legendary hunter" };
        private static readonly string[] RankStars = { "redstar", "bronzestar", "silverstar", "goldstar", "blackstar" };
        // the hunter of choice's picture, by Hunter
        private static readonly string[] Portraits = { "samusmost", "kandenmost", "tracemost", "syluxmost", "noxmost", "spiremost", "weavelmost" };

        private void BindCard(MenuFile file, MenuStrings strings)
        {
            _card = false;
            if (!_keyboard || file.Pages.Count <= LicensePage) return;
            foreach (string t in CardTitles.SelectMany(x => x).Concat(WeaponNames).Append("of").Append("-"))
            {
                _text[t] = Text(strings, s => s == t);
            }
            MenuPage rom = file.Pages[LicensePage];
            string TextOf(MenuItem it) => strings[it.States[0].Text!.StringId];
            int Widget(string name) => FindItem(rom, it => HasWidget(file, it, name));
            int[] Widgets(Func<MenuItem, bool> match) => Enumerable.Range(0, rom.Items.Count).Where(i => match(rom.Items[i])).ToArray();
            int Txt(string text) => FindItem(rom, it => it.IsText && TextOf(it) == text);
            int[] Texts(Func<string, bool> match) => Widgets(it => it.IsText && match(TextOf(it)));
            // the card
            int[] tabs = Enumerable.Range(1, 5).Select(n => Widget("tab" + n)).ToArray();
            int[] titles = Enumerable.Range(1, 3).Select(n => Txt("gc title " + n)).ToArray();
            int[] nums = { Txt("gc num 1"), Txt("gc num 2"), Txt("gcnum3") };
            int[] names = Texts(t => t == "HNTR NAME");
            int ColourOf(int i) => (int)(rom.Items[i].States[0].Text!.StartColor & 0xFFFFFF);
            _nameBlack = names.FirstOrDefault(i => ColourOf(i) == 0, -1);
            _nameGold = names.FirstOrDefault(i => ColourOf(i) == 0x1F1C09, -1);
            int nameOther = names.FirstOrDefault(i => i != _nameBlack && i != _nameGold, -1);
            _rankTitle = RankTitles.Select(Txt).ToArray();
            _rankStar = RankStars.Select(Widget).ToArray();
            _medals = new[] { Widget("bronzemedal"), Widget("silvermedal"), Widget("goldmedal") };
            _portraits = Portraits.Select(Widget).ToArray();
            _headerGold = Widget("cardheadergold");
            _frame = Widget("frame200");
            _frameGold = FindItem(rom, it => HasWidget(file, it, "frame200gold") && !HasWidget(file, it, "frame200"));
            _swirl = Widget("alimbicswirl");
            _octolith = Widget("octolith");
            int points = Txt("99"), next = Txt("89"), pointsLabel = Txt("ranking points:"), nextLabel = Txt("next rank:");
            int[] regions = { Widget("cardbgUS"), Widget("cardbgJAP"), Widget("cardbgEURO") };
            // the touch screen: "you are:", the name and its panel and button (hidden), EDIT NAME (its icon, label and
            // highlight: in the ROM the one that opens the keyboard's mask, no call of its own; EditNow here)
            int youAre = Txt("YourNamE"), youAreLabel = Txt("you are:"), yourPanel = Widget("yourpanel"), backdrop = Widget("blackdrop");
            int[] nameButtons = Widgets(it => Calls(it).Contains(CardNameCall));
            int rankBox = Widget("textarea");
            int keyboardMask = FindItem(rom, it => it.Actions.Any(a => a.Calls.Any(c => c.B == CardEditCall)));
            int editHighlight = FindItem(rom, it => HasWidget(file, it, "small_highlight") && it.Actions.Any(a => a.Item == keyboardMask && a.Calls.Count == 0));
            _edit = new[] { Widget("edit"), Txt("edit name"), editHighlight };
            int[] needed = tabs.Concat(titles).Concat(nums).Concat(_rankTitle).Concat(_rankStar).Concat(_medals).Concat(_portraits).Concat(regions)
                .Concat(_edit).Concat(new[] { _nameBlack, _nameGold, nameOther, _headerGold, _frame, _frameGold, _swirl, _octolith, points,
                    next, pointsLabel, nextLabel, youAre, youAreLabel, keyboardMask, yourPanel, backdrop, rankBox }).ToArray();
            if (needed.Any(i => i < 0) || !rom.Actions.Any(a => a.Calls.Any(c => c.B == CardTabCall)) || !PageCalls(rom, CardNameCall))
            {
                Log?.Invoke("multiplayer: no hunter license card (page 60 isn't the expected layout): the door opens the name keyboard");
                return;
            }
            _cardTabs = tabs;
            // shown as vanilla's draw leaves them (their own Idle), hidden: the friend's card lines, the roster and its buttons,
            // the Wi-Fi icons, the other regions' backgrounds and everything the card shows only for some licenses
            _cardShow = new[] { regions[0], pointsLabel, nextLabel, points, next, rankBox };
            var conditional = new HashSet<int>(_rankTitle.Concat(_rankStar).Concat(_medals).Concat(_portraits).Concat(_edit)
                .Concat(new[] { _headerGold, _frame, _frameGold, _swirl, _octolith, _nameBlack, _nameGold }));
            string[] rosterWidgets = { "bigpanel", "rivalbar_long", "friendbar_long", "uparrow", "downarrow", "unlocked", "locked",
                "pending", "delete", "adbot", "cardmessage" };
            _cardHide = Widgets(it => !conditional.Contains(it.Index) && (
                rosterWidgets.Any(w => HasWidget(file, it, w))
                || Enumerable.Range(1, 4).Any(n => HasWidget(file, it, "wifi" + n) || HasWidget(file, it, "wireless" + n))
                || HasWidget(file, it, "small_highlight") && it.Index != editHighlight
                || it.IsText && TextOf(it) is "XXXX" or "your last encounter result:" or "DEFEAT" or "VICTORY" or "TIE"
                    or "total rounds against:" or "victories against:" or "F/88" or "C/88" or "FRIENDS" or "RIVALS" or "delete player"
                    or "add friend"
                || it.IsText && (TextOf(it).StartsWith("BORING", StringComparison.Ordinal) || TextOf(it).StartsWith("LAME", StringComparison.Ordinal)
                    || TextOf(it).StartsWith("if you still see this screen", StringComparison.Ordinal))))
                .Append(regions[1]).Append(regions[2]).Append(nameOther)
                .Concat(new[] { youAre, youAreLabel, yourPanel, backdrop }).Concat(nameButtons).Distinct().ToArray();
            // the copy: every item in the ROM's order, back -> the doors, our strings on the texts this fills
            var ours = new Dictionary<int, int>();
            int cardName = strings.Add("");
            foreach (int i in names) ours[i] = cardName;
            ours[points] = strings.Add("");
            ours[next] = strings.Add("");
            for (int k = 0; k < 3; k++)
            {
                ours[titles[k]] = strings.Add("");
                ours[nums[k]] = strings.Add("");
            }
            MenuAction Back(MenuAction a) => a.TargetPage == WifiPage ? CopyAction(a, target: TypePage) : a;
            MenuPage copy = file.ReplacePage(LicensePage, index => new MenuPage
            {
                Index = index, Field0 = rom.Field0, Field14 = rom.Field14, Field16 = rom.Field16, Field18 = rom.Field18,
                Field1D = rom.Field1D, Field1E = rom.Field1E, Flags = rom.Flags, Items = Array.Empty<MenuItem>(),
                Actions = rom.Actions.Select(Back).ToArray(), Timers = rom.Timers
            });
            var rankRight = new HashSet<int> { pointsLabel, nextLabel, points, next };
            foreach (MenuItem it in rom.Items)
            {
                float dx = rankRight.Contains(it.Index) ? -RankBoxNarrower / 2 : 0;
                // the box moved down and its bottom half back up: only its top comes down
                float dy = it.Index == rankBox ? -RankBoxLower : 0;
                List<MenuAction> actions = it.Actions.Select(a => it.Index == editHighlight && a.Item == keyboardMask && a.Calls.Count == 0
                    ? EditNow(a) : Back(a)).ToList();
                int tab = Array.IndexOf(tabs, it.Index);
                if (tab >= 0) actions.Add(TabTap(it, tab));
                copy.AddItem(i => RecompMenus.Clone(it, i, dx, dy, stringId: ours.TryGetValue(i, out int s) ? s : -1,
                    links: it.Links.ToList(), actions: actions,
                    stretchX: it.Index == rankBox ? -RankBoxNarrower : 0, stretchDown: it.Index == rankBox ? -RankBoxLower : 0));
            }
            _sCardName = cardName;
            _sPoints = ours[points];
            _sNext = ours[next];
            for (int k = 0; k < 3; k++)
            {
                _sTitle[k] = ours[titles[k]];
                _sNum[k] = ours[nums[k]];
            }
            _card = true;
        }

        // EDIT NAME straight to the keyboard: vanilla's press brings the keyboard's mask in over the card first (its arrival
        // calls 281); the keyboard now floats over the card (KeyboardOverCard), so the press calls 281 itself
        private static MenuAction EditNow(MenuAction a) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = a.RectX, RectY = a.RectY, RectW = a.RectW,
            RectH = a.RectH, Calls = new[] { (0, CardEditCall) }, Item = a.Item, TargetPage = 0xFF, Field17 = 0
        };

        // a tab's tap: its strip under the card, in touch space (the menu's y + 192 on the top screen) relative to the item
        private static MenuAction TabTap(MenuItem tab, int k) => new()
        {
            Kind = (ushort)MenuKeys.A, RectX = (short)MathF.Round(CardTabX0 + CardTabWidth * k - tab.X),
            RectW = (short)MathF.Round(CardTabX0 + CardTabWidth * (k + 1) - 1 - tab.X),
            RectY = (short)MathF.Round(CardTabY0 + 192 - tab.Y), RectH = (short)MathF.Round(CardTabY1 + 192 - tab.Y),
            Calls = new[] { (0, CardTabPickCall) }, Item = (ushort)tab.Index, TargetPage = 0xFF, Field17 = 0 // (not k: MenuEngine.Run takes Field17 for a state and forced the tapped tab Hidden / Idle /
                                           // Selected / Focused, the double-tap bug of 2026-10-23; the tab is the item)
        };

        private void EnterCard(MenuEngine menu)
        {
            HunterLicense l = _license;
            foreach (int item in _cardHide) ShowAtEntry(menu, item, false);
            foreach (int item in _cardShow) ShowAtEntry(menu, item, true);
            foreach (int item in _edit) ShowAtEntry(menu, item, true); // (vanilla shows it after a tap on "you are:")
            int level = Math.Clamp(l.RankLevel, 0, 4);
            for (int r = 0; r < 5; r++)
            {
                ShowAtEntry(menu, _rankTitle[r], r == level);
                ShowAtEntry(menu, _rankStar[r], r == level);
            }
            // legendary: the gold header and the black name; else the name in its usual colour
            ShowAtEntry(menu, _headerGold, level == 4);
            ShowAtEntry(menu, _nameBlack, level == 4);
            ShowAtEntry(menu, _nameGold, level != 4);
            // the medal by total wins (25 / 100 / 200), the frame by total matches (500; gold for a legendary hunter)
            int wins = l.Wins, medal = wins >= 200 ? 2 : wins >= 100 ? 1 : wins >= 25 ? 0 : -1;
            for (int k = 0; k < 3; k++) ShowAtEntry(menu, _medals[k], k == medal);
            bool frame = l.Matches >= 500;
            ShowAtEntry(menu, _frameGold, frame && level == 4);
            ShowAtEntry(menu, _frame, frame && level != 4);
            ShowAtEntry(menu, _swirl, l.BeatGame && !l.Completed);
            ShowAtEntry(menu, _octolith, l.BeatGame && l.Completed);
            for (int h = 0; h < 7; h++) ShowAtEntry(menu, _portraits[h], h == Math.Clamp(l.FavoriteHunter, 0, 6));
            Put(_sCardName, StreamerPrivacy.Name(l.Name, mine: true)); // (this device's license)
            Put(_sPoints, l.RankPoints.ToString(CultureInfo.InvariantCulture));
            Put(_sNext, level < 4 ? HunterLicense.RankThresholds[level].ToString(CultureInfo.InvariantCulture) : "--");
            if (!_cardKeepTab) _cardTab = 0; // (vanilla opens on the first tab every time: BizHawk; back from EDIT NAME: as left)
            _cardKeepTab = false;
            CardTab(menu, entering: true);
            SaveTime();
        }

        // the current tab: its widget lit (tab 1's is the card's own outline: the others' Selected states), its three stats
        private void CardTab(MenuEngine menu, bool entering = false)
        {
            for (int k = 1; k < 5; k++)
            {
                MenuState want = k == _cardTab ? MenuState.Selected : MenuState.Idle;
                if (entering || menu.ItemState(_cardTabs[k]) != want) menu.SetState(_cardTabs[k], want);
            }
            (string Title, string Value)[] lines = CardLines(_cardTab, _license);
            for (int k = 0; k < 3; k++)
            {
                Put(_sTitle[k], lines[k].Title);
                Put(_sNum[k], lines[k].Value);
            }
        }

        // a tab's three lines as vanilla writes them (ov0 0x0211A3C8), for this player's license or another hunter's (EmitCard)
        private (string, string)[] CardLines(int tab, HunterLicense l)
        {
            string W(string text) => Word(text);
            string N(long n) => n.ToString(CultureInfo.InvariantCulture);
            string Pct(double v) => Math.Min(v, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            string Time(double seconds)
            {
                long s = (long)seconds;
                return s == 0 ? "-- -- --" : $"{s / 3600}h {s / 60 % 60:00}m {s % 60:00}s";
            }
            bool played = l.Matches > 0;
            string[] t = CardTitles[tab].Select(W).ToArray();
            return tab switch
            {
                0 => new[]
                {
                    (t[0], $"{N(l.WifiWins)} {W("of")} {N(l.OnlineMatches)}"),
                    (t[1], $"{N(l.WirelessWins)} {W("of")} {N(l.LocalMatches)}"),
                    (t[2], Pct(l.Disconnects == 0 ? 100 : 100.0 * l.Matches / ((long)l.Matches + l.Disconnects))),
                },
                1 => new[]
                {
                    (t[0], Pct(played ? 100.0 * l.Wins / l.Matches : 0)),
                    (t[1], N((long)l.WifiBestWinStreak + l.WirelessBestWinStreak)),
                    (t[2], played ? ArenaNameOfRoom(l.LuckyArena) : W("-")),
                },
                2 => new[]
                {
                    (t[0], played ? W(WeaponNames[Math.Clamp(l.FavoriteWeapon, 0, 8)]) : W("-")),
                    (t[1], N(l.HeadshotKills)),
                    (t[2], played ? W(CardModes[Math.Clamp(l.FavoriteMode, 0, 6)]) : W("-")),
                },
                3 => new[]
                {
                    (t[0], N(l.BipedKills)),
                    (t[1], N(l.AltFormKills)),
                    (t[2], N((long)l.WifiBestKillStreak + l.WirelessBestKillStreak)),
                },
                _ => new[]
                {
                    (t[0], Time(l.WifiSeconds)),
                    (t[1], Time(l.WirelessSeconds)),
                    (t[2], Time(l.TotalSeconds)),
                },
            };
        }

        // ONLINE PLAY's HUNTER LICENSE view (OnlineMenus.License.cs, 2026-10-09): another hunter's card
        // as this page draws it, from their uploaded license: the top screen's items (the card and its tabs) shown and filled as
        // EnterCard / CardTab would for `l` (no EDIT NAME), each at its look's first frame, into `output` in menu space, far to
        // near. `name` is the server's (word-filtered) name, never the license's. False when page 60 isn't the card.
        public bool EmitCard(MenuEngine menu, HunterLicense l, string name, int tab, List<WidgetTri> output)
        {
            if (!_card || menu.File.Pages.Count <= LicensePage) return false;
            MenuPage page = menu.File.Pages[LicensePage];
            tab = Math.Clamp(tab, 0, 4);
            int level = Math.Clamp(l.RankLevel, 0, 4);
            var shown = new Dictionary<int, bool>();
            foreach (int item in _cardHide) shown[item] = false;
            foreach (int item in _cardShow) shown[item] = true;
            foreach (int item in _edit) shown[item] = false;
            for (int r = 0; r < 5; r++)
            {
                shown[_rankTitle[r]] = r == level;
                shown[_rankStar[r]] = r == level;
            }
            shown[_headerGold] = level == 4;
            shown[_nameBlack] = level == 4;
            shown[_nameGold] = level != 4;
            int wins = l.Wins, medal = wins >= 200 ? 2 : wins >= 100 ? 1 : wins >= 25 ? 0 : -1;
            for (int k = 0; k < 3; k++) shown[_medals[k]] = k == medal;
            bool frame = l.Matches >= 500;
            shown[_frameGold] = frame && level == 4;
            shown[_frame] = frame && level != 4;
            shown[_swirl] = l.BeatGame && !l.Completed;
            shown[_octolith] = l.BeatGame && l.Completed;
            for (int h = 0; h < 7; h++) shown[_portraits[h]] = h == Math.Clamp(l.FavoriteHunter, 0, 6);
            var texts = new Dictionary<int, string>
            {
                [_sCardName] = name,
                [_sPoints] = Math.Clamp(l.RankPoints, 0, HunterLicense.RankThresholds[4] - 1).ToString(CultureInfo.InvariantCulture),
                [_sNext] = level < 4 ? HunterLicense.RankThresholds[level].ToString(CultureInfo.InvariantCulture) : "--",
            };
            (string Title, string Value)[] lines = CardLines(tab, l);
            for (int k = 0; k < 3; k++)
            {
                texts[_sTitle[k]] = lines[k].Title;
                texts[_sNum[k]] = lines[k].Value;
            }
            var items = new List<WidgetTri>();
            var order = new List<(float Z, int Seq, WidgetTri Tri)>();
            foreach (MenuItem it in page.Items)
            {
                if (!(shown.TryGetValue(it.Index, out bool show) ? show : (MenuState)it.Field34 != MenuState.Hidden)) continue;
                // the lit tab: its Selected look (tab 1's lit look is the card's own outline, as CardTab)
                int k = Array.IndexOf(_cardTabs, it.Index);
                MenuItemState? look = (k >= 1 && k == tab ? it.GetState((int)MenuState.Selected) : null) ?? it.GetState((int)MenuState.Idle);
                if (look == null) continue;
                items.Clear();
                if (look.Text != null)
                {
                    MenuTextStyle style = look.Text;
                    (int r, int g, int b, int a) = MenuTextStyle.Channels(style.StartColor);
                    if (a <= 0) continue;
                    string text = texts.TryGetValue(style.StringId, out string? t) ? t : menu.Strings[style.StringId];
                    menu.Font.Emit(text, it.X, it.Y, style.Align, style.WrapWidth, style.Size == 0 ? 8 : style.Size, r / 31f, g / 31f, b / 31f,
                        a / 31f, MenuEngine.TextDepthZ(it.Depth), items);
                }
                else if (look.WidgetIndex >= 0)
                {
                    menu.Widgets.Evaluate(look.WidgetIndex, 0, loop: true, alpha: 1, items);
                    if (it.StretchX != 0 || it.StretchDown != 0) MenuEngine.Stretch(items, 0, it.StretchX, it.StretchDown);
                    for (int i = 0; i < items.Count; i++)
                    {
                        WidgetTri tri = items[i];
                        tri.A.X += it.X; tri.B.X += it.X; tri.C.X += it.X;
                        tri.A.Y += it.Y; tri.B.Y += it.Y; tri.C.Y += it.Y;
                        tri.Z += it.Depth;
                        items[i] = tri;
                    }
                }
                if (items.Count == 0) continue;
                // the top screen's only (y up from 0): the touch screen's rank box and buttons stay on page 60; and no plain fill
                // over a whole screen (a fade or backdrop: FrontendSession spreads those over the canvas, they aren't the card)
                float y0 = items.Min(t => MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y))), y1 = items.Max(t => MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
                if ((y0 + y1) / 2 < 0) continue;
                float x0 = items.Min(t => MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X))), x1 = items.Max(t => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)));
                bool flat = items.All(t => t.TextureId < 0 || menu.Widgets.Textures[t.TextureId] is { Width: <= 16, Height: <= 16 });
                if (flat && x1 - x0 >= 250 && y1 - y0 >= 185) continue;
                foreach (WidgetTri tri in items) order.Add((tri.Z, order.Count, tri));
            }
            // far to near, ties in item order (as MenuEngine.Collect)
            output.AddRange(order.OrderBy(o => o.Z).ThenBy(o => o.Seq).Select(o => o.Tri));
            return order.Count > 0;
        }

        private string ArenaNameOfRoom(int room)
        {
            int index = Array.IndexOf(ArenaRooms, room);
            if (index >= 0) return ArenaName(index);
            RoomMetadata? meta = Metadata.GetRoomById(room, noThrow: true);
            return meta == null ? Word("-") : ArenaWord(meta.InGameName ?? "");
        }

        private void Put(int id, string text)
        {
            if (id >= _strings!.RomCount) _strings.Set(id, text);
            else _strings.Fill(id, text);
        }

        private bool OnCardCall(MenuEngine menu, MenuAction action, int b)
        {
            switch (b)
            {
            case CardBackCall: // B / the back arrow: the doors. Rev 1's B and both revs' arrow have no page of their own (the
                               // game's code leaves for the Wi-Fi page); rev 0's B carries it (retargeted in BindCard)
                if (action.TargetPage == 0xFF && menu.PendingPage == -1) menu.GoTo(TypePage);
                return true;
            case CardTabCall: // L / R: the tabs, both ways round
            case CardTabNextCall:
                _cardTab = (_cardTab + (b == CardTabNextCall ? 1 : 4)) % 5;
                CardTab(menu);
                Sound(menu, CardTabSound, "card tab key"); // (a page key has no item sound)
                return true;
            case CardTabPickCall: // a tap on a tab: that tab
            {
                int pick = Array.IndexOf(_cardTabs, (int)action.Item);
                if (pick >= 0) _cardTab = pick;
                CardTab(menu); // (also for the tab already picked: the tap's own focus look is put back)
                Sound(menu, CardTabSound, "card tab tap");
                return true;
            }
            case CardNameCall: // "you are:" chosen: EDIT NAME shows (vanilla; hidden here, EDIT NAME always shows)
                foreach (int item in _edit) Show(menu, item, true);
                return true;
            case CardEditCall: // EDIT NAME (its highlight opens the keyboard's mask, which calls this): the name keyboard
                // straight in, no page exit: the card stays (dimmed) behind the keyboard (owner 2026-10-08: "the license still
                // disappears for a moment like youre paging away both when you click edit name and then go back")
                if (StreamerPrivacy.HideName)
                {
                    // STREAMING (owner 10-28): the keyboard would show the name; a notice to turn SHOW NAME on instead
                    Popup?.Inform(StreamerPrivacy.NameLocked);
                    return true;
                }
                _nameReturn = LicensePage;
                _textDone = null; // (the nickname, not a RENAME left some other way)
                _cardSnapshot = true;
                _enterNext = NamePage;
                return true;
            }
            return true; // (the roster's calls: hidden)
        }

        // the license's total game time: the menus' time here (matches add theirs when counted), saved now and then
        private void TickTime()
        {
            if (_profileDir == null) return;
            _license.TotalSeconds += MenuTickSeconds;
            _unsavedSeconds += MenuTickSeconds;
        }

        private void SaveTime()
        {
            if (_unsavedSeconds == 0) return;
            _unsavedSeconds = 0;
            _license.Save(_profileDir);
        }

        // ---- UNLOCKS (M6, decision 11): vanilla's locked hunters and arenas, or everything (RECOMP SETTINGS > UNLOCKS) ----
        //
        // The state is the Hunter License's (HunterLicense: vanilla's cart-wide bits, a new cart's 9 arenas and Samus,
        // Kanden, Spire). What opens them, as vanilla (US rev 1, docs/MULTIPLAYER_MENU.md M6): the campaign's files (hunters
        // defeated, each planet's gateway room; read as CHOOSE MULTIPLAYER TYPE opens, vanilla writes them as they happen),
        // the good ending (Oubliette; its results page) and every match (RESULTS: one locked hunter beaten, every second
        // match the next arena, Head Shot, Stasis Bunker). Locked arenas leave the ARENA arrows (and RANDOM's pick);
        // a locked hunter's icon on SELECT HUNTER (35, the joiner's 36) shows its dark "off" picture and can't be picked, and
        // the lobby's arrows and ADD BOT skip it. No profile (the PC tools) or UNLOCKS = ALL: everything open; matches
        // still count, so switching back to VANILLA shows what was earned.
        public string? SavesDir { get; set; }
        public Func<bool>? UnlockEverything { get; set; }
        private static HunterLicense? _unlocks; // the locks in force; null = everything open
        private static MatchResults? _counted;
        // per hunter page: each locked-able hunter's (in HunterOrder) icon, its highlight (A / tap: calls 132-145) and dark icon
        private readonly Dictionary<int, (Hunter Hunter, int Icon, int Highlight, int Off)[]> _hunterLocks = new();

        private static bool HunterOpen(Hunter hunter) => _unlocks?.HunterOpen(hunter) ?? true;

        private void UpdateLocks() => _unlocks = _profileDir == null || UnlockEverything?.Invoke() == true ? null : _license;

        // A bot's star bars (1, 2, 3 stars: calls 150-152, 158-160, 166-168) are a ring in the data: each one hiding shows
        // the next (that's how a tap steps the level). Hiding all three for an emptied seat therefore always left one up
        // (owner report R9, 2026-10-07: stars over empty seats after the bots were removed). The lobbies (38, 39) get the
        // bars without those links; Lobby / JoinLobby show the one bar of the bot's level themselves.
        private static void BindStarBars(MenuFile file)
        {
            foreach (int index in new[] { LobbyPage, JoinLobbyPage })
            {
                if (file.Pages.Count <= index) continue;
                MenuPage page = file.Pages[index];
                var bars = new HashSet<int>();
                for (int i = 0; i < page.Items.Count; i++)
                {
                    if (Calls(page.Items[i]).Any(c => c is >= 150 and <= 152 or >= 158 and <= 160 or >= 166 and <= 168)) bars.Add(i);
                }
                foreach (int i in bars)
                {
                    MenuItem it = page.Items[i];
                    List<MenuLink> kept = it.Links.Where(l => !(bars.Contains(l.Target) && l.State == MenuState.Idle)).ToList();
                    if (kept.Count == it.Links.Count) continue;
                    page.ReplaceItem(i, n => RecompMenus.Clone(it, n, 0, 0, links: kept, actions: it.Actions));
                }
            }
        }

        private void BindHunterLocks(MenuFile file)
        {
            _hunterLocks.Clear();
            foreach (int index in new[] { HunterPage, JoinHunterPage })
            {
                if (file.Pages.Count <= index) continue;
                MenuPage page = file.Pages[index];
                var found = new List<(Hunter, int, int, int)>();
                for (int k = 1; k < HunterOrder.Length; k++)
                {
                    Hunter h = HunterOrder[k];
                    string name = h.ToString().ToLowerInvariant();
                    int icon = FindItem(page, it => HasWidget(file, it, name));
                    int off = FindItem(page, it => HasWidget(file, it, name + "off"));
                    int highlight = FindItem(page, it => Calls(it).Contains(139 + k));
                    if (icon >= 0 && off >= 0 && highlight >= 0) found.Add((h, icon, highlight, off));
                }
                if (found.Count == HunterOrder.Length - 1) _hunterLocks[index] = found.ToArray();
                else Log?.Invoke($"multiplayer: page {index}: hunter lock items not found ({found.Count}/6): locked hunters show as open");
            }
        }

        private void ShowHunterLocks(MenuEngine menu, int page)
        {
            if (!_hunterLocks.TryGetValue(page, out var items)) return;
            foreach ((Hunter hunter, int icon, int highlight, int off) in items)
            {
                bool open = HunterOpen(hunter);
                ShowAtEntry(menu, icon, open);
                ShowAtEntry(menu, highlight, open);
                ShowAtEntry(menu, off, !open);
            }
        }

        // the campaign's files A/B/C (CampaignSaves slots 1-3): anything they've done that vanilla would have opened
        private void RefreshUnlocks()
        {
            if (_profileDir == null || SavesDir == null) return;
            bool opened = false;
            var saves = new MphRecomp.Campaign.CampaignSaves(SavesDir);
            for (int slot = 1; slot <= 3; slot++)
            {
                try
                {
                    MphRecomp.Campaign.CampaignSaveFile? file = saves.Load(slot);
                    if (file?.Story != null) opened |= _license.FromStory(file.Story);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"multiplayer: unlocks: file {slot} not read: {ex.Message}");
                }
            }
            if (!opened) return;
            _license.Save(_profileDir);
            Log?.Invoke($"multiplayer: unlocks from the campaign files: arenas {_license.Arenas:X7}, hunters {_license.Hunters:X2}");
        }

        // the ending's completion (FrontendSession: the results page's percentage)
        public Func<int?>? EndingPercent { get; set; }

        private void GoodEnding()
        {
            if (_profileDir == null) return;
            bool opened = _license.GoodEnding(EndingPercent?.Invoke());
            _license.Save(_profileDir);
            Log?.Invoke($"multiplayer: the good ending: the license shows the game beaten{(_license.Completed ? " at 100%" : "")}"
                + (opened ? "; oubliette opened" : ""));
        }

        // ---- counting a match for the license: vanilla's after-match code (arm9 0x020562A4) and its live kill counts ----
        //
        // Vanilla's two buckets (one flag: Nintendo WFC or not): owner 2026-10-07, Wi-Fi = internet battles, wireless =
        // everything else (LAN with people, matches against bots on this device). A match with another device is an internet
        // one when that device's address is a public one.
        private static HunterLicense.MatchKills? _kills; // the running match's (null: nothing to count)
        private static bool _lastWifi;
        private static int _lastArenaRoom = -1;
        private static readonly int[] _seatRanks = Enumerable.Repeat(-1, MphRead.Entities.PlayerEntity.SlotCapacity).ToArray(); // each human's rank level (-1 = not known; P8: 8 seats)

        private void ArmMatch(MatchSettings settings, bool wifi)
        {
            s_onlineResultsReturn = -1; // (ArmOnlineMatch sets it again after)
            _kills = new HunterLicense.MatchKills();
            MphRead.Entities.PlayerEntity.HostLicenseKill = (flags, beam) =>
                _kills?.Kill(flags.TestFlag(MphRead.Entities.DamageFlags.FromAlt), flags.TestFlag(MphRead.Entities.DamageFlags.Headshot), (int)beam);
            MphRead.Entities.PlayerEntity.HostLicenseStreakEnd = () => _kills?.StreakEnd();
            GameState.HostLicenseTime = frame =>
            {
                if (_kills != null && GameState.MatchState == MatchState.InProgress) _kills.Seconds += frame;
            };
            _lastWifi = wifi;
            _lastArenaRoom = MatchArenas.Find(settings.Arena)?.Id ?? -1;
            for (int s = 0; s < _seatRanks.Length; s++)
            {
                MatchPlayer? p = s < settings.Players.Count ? settings.Players[s] : null;
                _seatRanks[s] = p == null || p.Bot ? -1 : s == settings.LocalSeat ? _license.RankLevel : p.RankLevel;
            }
        }

        // TRY CONTROLS (RecompMenus.ZoomAim.cs): nothing counted for the license -- a match armed and left without RESULTS
        // keeps its counter hooked, so the try run's kills and time would land in it; every real match arms its own anew
        public static void DisarmMatch() => _kills = null;

        // a public address: not this device, not a private, link-local or shared (CGNAT) network
        private static bool IsInternet(IPAddress? ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal);
            }
            byte[] b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168
                || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] == 0);
        }

        // RESULTS of a match not counted yet: the license's counts, unlocks and rank, saved
        private void CountMatch()
        {
            MatchResults? r = MatchResults.Last;
            if (r == null || ReferenceEquals(r, _counted)) return;
            _counted = r;
            HunterLicense.MatchKills? kills = _kills;
            _kills = null;
            MatchResults.Player? local = r.Local;
            if (local == null) return;
            double seconds = kills?.Seconds ?? 0;
            var others = r.Rows.Where(p => p.Seat != r.LocalSeat).OrderBy(p => p.Seat)
                .Select(p => new HunterLicense.Opponent(p.Hunter, p.Place, p.Bot, p.Team, p.Bot || p.Seat >= _seatRanks.Length ? -1 : _seatRanks[p.Seat]))
                .ToList();
            var match = new HunterLicense.MatchRecord(_lastWifi, r.Mode, _lastArenaRoom, seconds, local.Hunter, local.Place, r.Teams,
                local.Team, others);
            int points = _license.RankPoints;
            string opened = _license.AfterMatch(match, kills);
            _setup.Rank = _license.RankLevel;
            _license.Save(_profileDir);
            _unsavedSeconds = 0;
            Log?.Invoke($"multiplayer: match counted ({(_lastWifi ? "wi-fi" : "wireless")}, {seconds:0} s; {(local.Place == 0 ? "win" : "loss")}, "
                + $"{_license.LocalMatches} wireless, {_license.OnlineMatches} wi-fi; kills {kills?.Biped ?? 0} biped + {kills?.AltForm ?? 0} alt-form, "
                + $"{kills?.Headshots ?? 0} headshots, beams {String.Join(",", kills?.Weapons ?? new int[9])}, streak {String.Concat(kills?.Streak.Select(k => k ? '+' : '|') ?? "")}; "
                + $"rank points {points} -> {_license.RankPoints} (others' levels {String.Join(",", others.Select(o => o.RankLevel))})"
                + $"){(opened.Length > 0 ? ": opened " + opened : "")}");
        }
    }
}
