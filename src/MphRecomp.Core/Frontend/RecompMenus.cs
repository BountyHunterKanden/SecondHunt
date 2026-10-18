using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.Mods;

// The recomp's own screens, built INTO the game's menu graph out of the game's own menu pieces so they look and move
// exactly like the vanilla ones:
//   - Options (page 21): MODS and RECOMP SETTINGS in SHOW MY STATS ON NINTENDOWIFI.COM's place (clones of its label,
//     box and value; the stats row is hidden: there's no Nintendo WFC), and IMPORT GAMES' own button; the panel widened
//     to hold them (a 4 x 2 grid, see AddOptionsButtons)
//   - no SAVE on Options, OPTIONS > AUDIO or OPTIONS > CONTROLS (owner 2026-10-05): every change is saved at once
//   - RECOMP SETTINGS, MODS and MOD SETTINGS: the Controls page's frame (backdrop, logo, back button) plus rows cloned
//     from its SENSITIVITY row (arrows, value box, label, value, touch arrows, a description in the top band while
//     focused); six rows a page, L / R page through more (the hunter license's L / R icons at the header's ends and
//     "n/m" after the title show it); rows that run something (credits, ...) have no arrows
//   - PAUSED (the in-game pause menu, see FrontendSession.OpenPause): the Controls page's control-type boxes as a list
//     -- resume, recomp settings, mod settings, quit to the main menu
//   - IMPORT GAMES (RECOMP SETTINGS > IMPORT GAMES, owner 2026-10-07): RECOMP SETTINGS' rows again, a game a row, then
//     one game's page (its disc, IMPORT ALL, each item with its status); the game code is ImportGames.cs
// Everything new is appended (items at the end of a page, pages after the ROM's), so every ROM index stays valid.
// Values change with A / Start / D-pad left-right / the touch arrows and are saved at once. On the MODS page, X on a
// mod opens that mod's own settings (the ones it declares in its mod.json).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus // + RecompMenus.Replays.cs (REPLAYS, Replay mode)
    {
        // our callback ids (the game's are below 1000)
        private const int OpenMods = 1000, OpenSettings = 1001;
        private const int PauseResume = 1010, PauseSettings = 1011, PauseModSettings = 1012, PauseQuit = 1013, PauseChatCall = 1014;
        private const int DeleteAllCall = 1020; // a rows page's DELETE ALL button (RowsPage.DeleteAll)
        // RowNext / RowPrev: A / Start and the touch arrows; RowRight / RowLeft: the D-pad (a value row steps, an
        // action row ignores it); RowTap: a tap on the value box (an action row runs; a value row ignores it); RowDelete:
        // a tap on the X box beside it (RowModel.Delete)
        private const int RowNext = 1100, RowPrev = 1200, RowAlt = 1300, PageNext = 1400, PagePrev = 1401;
        private const int RowRight = 1500, RowLeft = 1600, RowTap = 1700, RowDelete = 1800, RowRemove = 2900;
        // D-pad down on a page with DELETE ALL: the next live row, from the last one DELETE ALL; up from DELETE ALL: the
        // last live row (the engine's own D-pad finds neither: the rows' boxes have no rectangle, so it compares their
        // item offsets with the button's touch rectangle)
        private const int RowDown = 1900, DeleteAllUp = 1021, DeleteAllStay = 1022; // Stay: down / left / right there
        private const int RowsPerPage = 6;
        private const float RowPitch = 22;
        // the resize (menu units): value box wider (SENSITIVITY's fits 30px of text, "DETAILED" is 49), the rows' panel
        // taller (a two-line label on the sixth row hung under it), the description band taller (three lines)
        private const float ValueGrow = 24, PanelGrow = 8, DescGrow = 8;
        private const int OptionsPage = 21, ControlsPage = 23;
        // OPTIONS > CONTROLS (owner 2026-10-05): the control customizer's buttons on it and the customizer's back; the
        // page's own callbacks (the ROM's: SAVE, SENSITIVITY right / left, LOOK INVERT), which nothing answered before
        private const int OpenPadControls = 1002, OpenKeyControls = 1003, ControlsBack = 1004, ControlsNoop = 1005;
        private const int MouseSensUp = 1006, MouseSensDown = 1007; // the mouse's SENSITIVITY row (PC)
        // Options' IMPORT GAMES button; IMPORT GAMES' back (to where it was opened from: Options or RECOMP SETTINGS)
        private const int OpenImport = 1008, ImportBack = 1009;
        private const int ControlsSave = 72, SensUp = 73, SensDown = 74, InvertToggle = 75;
        // the game's SAVE callbacks: Options, OPTIONS > AUDIO (AudioOptions), OPTIONS > CONTROLS
        private const int OptionsSave = 44, AudioSave = 49;
        private const int StatsCall = 47; // SHOW MY STATS ON NINTENDOWIFI.COM's box
        private const long CaptureMs = 5000; // a capture nobody answers ends by itself

        private readonly MenuFile _file;
        private readonly MenuStrings _strings;
        private readonly RecompSettings _settings;
        private readonly string? _settingsPath;
        private readonly List<ModEntry> _mods = new();

        public int SettingsPage => _gamePage?.Page ?? -1; // SECOND HUNT SETTINGS: its first tab (RecompMenus.SettingsPages.cs)
        public int ModsPage => _modsRows?.Page ?? -1;
        public int ModSettingsPage => _modSettingRows?.Page ?? -1;
        // the in-game pause menu's page: an online match's has CHAT (PauseChat, OnlineMenus.Chat.cs)
        public int PausePage => PauseChat != null && _pauseChatPage >= 0 ? _pauseChatPage : _pausePage;
        private int _pausePage = -1, _pauseChatPage = -1;
        internal int PauseChatPageForTools => _pauseChatPage; // (MphRead.Tools -feflow FE_START=pausechat)
        public int ImportPage => _importRows?.Page ?? -1;

        private RowsPage? _modsRows, _modSettingRows;
        // IMPORT GAMES: the games, and the game open on the second page
        private RowsPage? _importRows, _importGameRows;
        private string _importGame = "mp1";
        private int _importBackPage = -1; // where IMPORT GAMES' back goes (-1 = RECOMP SETTINGS)
        private bool _inPause; // the call being answered came from the in-game pause menu
        // set by the in-game pause of a LAN / online match: rows that can't apply there (zoom) are hidden
        public bool InNetMatch { get; set; }
        // set by the in-game pause of an online match: the match camera row is greyed (online is first person; LAN isn't, owner 2026-10-17)
        public bool InOnlineMatch { get; set; }
        // CONTROLS (RecompMenus.Controls.cs): the keyboard & mouse's (else the controller's); opened by the game (the ship's
        // OPTIONS), so its back closes it; the function waiting for a button, since when, and the buttons pressed / still
        // held meanwhile (a combo: what was held together, taken when the last of them is let go)
        private bool _controlsKeys, _controlsFromHost;
        private MenuEngine? _stepMenu; // the menu of the call running a row's Step
        private string? _captureId;
        private long _captureStart;
        private readonly List<string> _capturePressed = new(), _captureHeld = new();
        // OPTIONS > CONTROLS: the game's control-type items (boxes, labels, DS pictures, descriptions) and ours (the
        // customizer's buttons with their labels and descriptions); SENSITIVITY / LOOK INVERT values as ours show them
        private readonly List<int> _controlTypeItems = new(), _controlsButtonItems = new();
        private int _firstControlsButton = -1;
        // SENSITIVITY / LOOK INVERT: the game's rows (classic), our copies of them centred (ours), the value copies on the
        // game's rows (classic: the ROM's values are placeholders), the ROM's value items (never shown)
        private readonly List<int> _romRowItems = new(), _centredRowItems = new(), _classicValueItems = new(), _romValueItems = new();
        private int _sensValueString = -1, _invertValueString = -1, _titleString = -1, _sensDescString = -1;
        private int _mouseSensValueString = -1, _controlsCopyPage = -1;
        private string _titleClassic = "", _sensDescClassic = "";
        private int _lastPage = -1;
        // hidden as their page comes in: each page's SAVE (button, highlight, "save"), Options' SHOW MY STATS row
        private readonly Dictionary<int, List<int>> _hiddenItems = new();
        private string? _modSettingsFilter; // one mod's settings (from MODS), or null = every enabled mod's (pause)
        private int _modsValueString = -1;
        // Samus suits exported to <external files>/guns/ (one row cycles through them), "original" always last
        private readonly List<string> _suits = new();

        public event Action<FrontendRequestKind>? Requested;

        // IMPORT GAMES' game code (pickers, the import worker, its answers in the game's box)
        public ImportGames Import { get; }

        private sealed class RowModel
        {
            public string Label = "";
            public string Description = "";
            public Func<string>? Describe; // in place of Description: the hint of the option it's on (CONTROLS, C17)
            public string Hint => Describe?.Invoke() ?? Description;
            public Func<string> Value = () => "";
            public Action<int> Step = _ => { };
            public Action? Alt; // X
            public Action? Remove; // Y (the customizer: take one button away)
            public bool Action; // runs something (credits, ...) instead of cycling a value: no arrows, D-pad ignored
            // greyed (the game's own State5 look, its rumble row without a Rumble Pak): no focus, no touch, no press
            public Func<bool>? Disabled;
            // deletes what the row made (IMPORT GAMES' imported entries): an X in a small orange box beside the value box
            // (owner 2026-10-07), and X on the controller when the row has no Alt; null = no X
            public Action? Delete;
            // CONTROLS' sections (RecompMenus.Controls.cs): a checkbox in place of the value (ticked or not); a box drawn
            // round this row and the next while true (C3: aim sensitivity + imperialist, paired); a RESET beside the value
            // (C6), dim while AtDefault
            public Func<bool>? Check;
            public Func<bool>? GroupWithNext;
            public Action? Reset;
            public Func<bool>? AtDefault;
            // SECOND HUNT SETTINGS' category (owner 2026-10-17): its rows start a page of their own, its name in the title
            public string? Section;
        }

        // a page of up to six rows fed from a list that may be longer (L / R page through it)
        private sealed class RowsPage
        {
            public int Page;
            public int[] Box = Array.Empty<int>(), Label = Array.Empty<int>(), Value = Array.Empty<int>(), Desc = Array.Empty<int>();
            public int[] Del = Array.Empty<int>(), DelX = Array.Empty<int>(); // each row's X box and its "X" (items)
            // the DELETE ALL button and its text (items, -1 = none), and what it runs now (null = nothing: hidden)
            public int DelAll = -1, DelAllText = -1;
            public Func<Action?> DeleteAll = () => null;
            public int EmptyItem = -1;
            public string Title = "";
            public int Header = -1, Pager = -1; // the title's string; the L / R icons item (-1: the ROM has none)
            public Func<List<RowModel>> Source = () => new();
            public List<RowModel> Rows = new();
            public int Offset;
            public List<int> Starts = new() { 0 }; // where each page starts (RowsPages): every six rows and at each new Section
            public Action? Entered; // runs as its page comes in, before the rows are read (REPLAYS reads its folder then)
        }

        public RecompMenus(MenuFile file, MenuStrings strings, RecompSettings settings, string? settingsPath, string? modsDir)
        {
            _file = file;
            _strings = strings;
            _settings = settings;
            _settingsPath = settingsPath;
            if (modsDir != null)
            {
                var manager = new ModManager();
                manager.Discover(modsDir);
                _mods.AddRange(manager.Available);
            }
            // imports go beside the settings file too: hd/ and guns/ are read from there (below, CampaignSuit, HdGun)
            Import = new ImportGames(settingsPath != null ? System.IO.Path.GetDirectoryName(settingsPath) : null);
            Import.Changed = OnImportChanged;
            ScanSuits();
        }

        // the suit row's list: what's in guns/ and hd/ now (again after an import)
        private void ScanSuits()
        {
            _suits.Clear();
            string? settingsPath = _settingsPath;
            // guns/ sits beside the settings file (both under the external files dir; see CampaignActivity, MainActivity)
            string? gunsRoot = settingsPath != null
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath) ?? "", "guns") : null;
            // an unreadable folder is skipped, not fatal: on Android, once the app may install its own updates (the "install
            // unknown apps" switch) it can't read folders it didn't create itself (adb-pushed hd/ and guns/; 2026-10-06)
            // a public build holds the arm cannons: a suit is listed for its body (hd/ below), never for a gun alone
            if (gunsRoot != null && System.IO.Directory.Exists(gunsRoot) && !RecompSettings.PublicBuild)
            {
                try
                {
                    foreach (string d in System.IO.Directory.GetDirectories(gunsRoot))
                        if (System.IO.File.Exists(System.IO.Path.Combine(d, "gun.bin")) && !NotSuits.Contains(System.IO.Path.GetFileName(d)))
                            _suits.Add(System.IO.Path.GetFileName(d));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
                {
                    Console.Error.WriteLine("recomp menus: guns/ unreadable (" + ex.Message + ")");
                }
            }
            // (Campaign mode assembly) every other HD model on Samus's own DS rig, hd/<Id>/ with its .dae beside guns/:
            // Brawl's Samus, Dark Samus, Zero Suit, ... The campaign wears the suit's body (third person, morph ball) and
            // keeps MPH's gun for one with no exported gun. <Id>Rest / <Id>Tpose folders are other rigs of a listed model.
            string? hdRoot = settingsPath != null
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath) ?? "", "hd") : null;
            if (hdRoot != null && System.IO.Directory.Exists(hdRoot))
            {
                foreach (string d in SafeDirectories(hdRoot))
                {
                    string id = System.IO.Path.GetFileName(d);
                    if (_suits.Contains(id, StringComparer.OrdinalIgnoreCase) || id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase)
                        || id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase) || NotSuits.Contains(id)) continue;
                    if (!MphRecomp.Anim.TrophyRigs.TryHunterFor(id, out MphRead.Hunter rig) || rig != MphRead.Hunter.Samus) continue;
                    // only imported, up-to-date models (a public build lists nothing hand-dropped: no Brawl trophies)
                    if (!MphRecomp.Import.ImportedContent.Usable(hdRoot, id)) continue;
                    try
                    {
                        if (System.IO.Directory.EnumerateFiles(d, "*.dae").Any()) _suits.Add(id);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException) { }
                }
            }
            _suits.Sort((x, y) => string.Compare(SuitLabel(x), SuitLabel(y), StringComparison.OrdinalIgnoreCase));
            _suits.Add("original"); // MPH's own DS gun, no HD
        }

        // the suit the campaign will wear: the picked one when it's imported, else MPH's own (the default MP1PowerSuit on a
        // fresh install showed "P1 PWR" with nothing imported; the campaign wore MPH's Samus). The setting itself is kept, so
        // importing that suit later puts it on.
        private string ShownSuit() =>
            _suits.FirstOrDefault(x => x.Equals(_settings.SamusSuit, StringComparison.OrdinalIgnoreCase)) ?? "original";

        // a folder's subfolders, or none when it can't be read (see the guns/ note above)
        private static string[] SafeDirectories(string dir)
        {
            try
            {
                return System.IO.Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
            {
                Console.Error.WriteLine($"recomp menus: {System.IO.Path.GetFileName(dir)}/ unreadable ({ex.Message})");
                return Array.Empty<string>();
            }
        }

        // A device suit folder (MP1PowerSuit, Phazonsuit, MP4ViolaSuitIC, ...) as a friendly name ("PRIME 1 POWER
        // SUIT"), the same convention as MainActivity's dev "First-person gun" picker (kept in sync by hand -- that
        // picker is private to MainActivity, which this project leaves alone).
        private static string SuitLabel(string suit)
        {
            if (suit == "original") return "ORIGINAL";
            if (suit.Equals("Phazonsuit", StringComparison.OrdinalIgnoreCase)) return "PRIME 1 PHAZON SUIT";
            var m = System.Text.RegularExpressions.Regex.Match(suit, @"^MP(\d)(.+)$");
            if (!m.Success) return IsBrawl(suit) ? "BRAWL " + suit.ToUpperInvariant() : suit.ToUpperInvariant();
            // split the rest at camelCase word boundaries, keeping acronym runs together (ViolaSuitIC -> Viola Suit IC)
            string spaced = System.Text.RegularExpressions.Regex.Replace(m.Groups[2].Value,
                "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            return $"PRIME {m.Groups[1].Value} {spaced}".ToUpperInvariant();
        }

        // The Value box itself is the SENSITIVITY value item: centred, 30px wrap (measured with -fecanvas's FE_MEASURE) --
        // "100%" (29px) is about the ceiling for one line, nowhere near SuitLabel's full name. A hand-picked 3-letter
        // code keeps every known suit to two short centred lines ("P1" / "PWR"); the full name lives in the description.
        private static readonly Dictionary<string, string> SuitCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MP1PowerSuit"] = "PWR", ["MP1VariaSuit"] = "VAR", ["MP1GravitySuit"] = "GRA", ["MP1FusionSuit"] = "FUS",
            ["Phazonsuit"] = "PHZ", ["MP1BetaPhazonSuit"] = "BPZ", ["MP2VariaSuit"] = "VAR", ["MP2DarkSuit"] = "DRK", ["MP2LightSuit"] = "LGT",
            ["MP3VariaSuit"] = "VAR", ["MP3PEDSuit"] = "PED", ["MP4DefaultSuit"] = "DEF", ["MP4PsychicSuit"] = "PSY",
            ["MP4PsychicGlove"] = "GLV", ["MP4ViolaSuit"] = "VLA", ["MP4ViolaSuitIC"] = "VIC", ["MP4LegacySuit"] = "LEG",
            ["MP2DarkSamus"] = "DSA", ["MP3DarkSamus"] = "DSA", ["MP3ZeroSuit"] = "ZER",
        };

        // left out of the suit row: HD models on Samus's rig the owner dropped (2026-10-01: "ditch mp3 zero suit for now"),
        // and guns/mp1, the first export of P1 PWR's gun (no body of its own: "P1 MP" played as MPH's Samus; the setting
        // reads it as MP1PowerSuit, see RecompSettings.SamusSuit)
        private static readonly HashSet<string> NotSuits = new(StringComparer.OrdinalIgnoreCase) { "MP3ZeroSuit", "MP3ZeroSuitS", "mp1" };

        // a Brawl trophy on Samus's rig (hd/Samus, ...): no MP<n> prefix, and not Prime 1's Phazonsuit
        private static bool IsBrawl(string suit) =>
            !suit.Equals("Phazonsuit", StringComparison.OrdinalIgnoreCase) && !suit.StartsWith("MP", StringComparison.OrdinalIgnoreCase)
            && MphRecomp.Anim.TrophyRigs.TryHunterFor(suit, out _);

        private static string SuitValue(string suit)
        {
            if (suit == "original") return "ORIG";
            if (IsBrawl(suit)) return "BRL " + new string(suit.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
            var m = System.Text.RegularExpressions.Regex.Match(suit, @"^MP(\d)(.+)$");
            string digit = m.Success ? m.Groups[1].Value : "1"; // Phazonsuit has no MP prefix -- it's Prime 1's
            if (SuitCodes.TryGetValue(suit, out string? code)) return $"P{digit} {code}";
            // an export this project doesn't know by name yet: best-effort code, not guaranteed unique
            string rest = m.Success ? m.Groups[2].Value : suit;
            string guess = new(rest.Where(char.IsLetter).Take(3).ToArray());
            return $"P{digit} {guess.ToUpperInvariant()}";
        }

        public RecompSettings Settings => _settings;

        // ---- "menu art" (owner 2026-10-13): a chooser of the lobby's skin icons (MultiplayerMenu.OpenArtChooser) ----

        // FrontendSession's: the backdrop is drawn again for a new pick
        public Action? MenuArtChanged { get; set; }

        // (the chooser's own: tools point both at their HD models)
        private string? HdRoot => MultiplayerMenu.CharacterHdRoot
            ?? (_settingsPath != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_settingsPath) ?? "", "hd") : null);

        // the chooser's entry for the setting: original, shuffle, the picked suit, else the one the recipe's order gives
        private string MenuArtChoice()
        {
            if (!_settings.MenuArtHd) return MphRecomp.Multiplayer.Characters.Original;
            if (_settings.MenuArtSuit == RecompSettings.MenuArtShuffle) return MultiplayerMenu.ShuffleChoice;
            string? hd = HdRoot;
            if (hd == null) return MphRecomp.Multiplayer.Characters.Original;
            if (_settings.MenuArtSuit != "" && MenuSamus.Has(hd, _settings.MenuArtSuit)) return _settings.MenuArtSuit;
            try { return MenuSamus.Load().PickSuit(hd) ?? MphRecomp.Multiplayer.Characters.Original; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return MphRecomp.Multiplayer.Characters.Original; }
        }

        private string MenuArtValue()
        {
            string c = MenuArtChoice();
            return MphRecomp.Multiplayer.Characters.IsOriginal(c) ? "ORIGINAL" : c == MultiplayerMenu.ShuffleChoice ? "SHUFFLE" : SuitValue(c);
        }

        private void OpenMenuArt()
        {
            if (_stepMenu is not { } menu || Multiplayer == null) return;
            Multiplayer.OpenArtChooser(menu, MenuArtChoice(), pick =>
            {
                bool hd = !MphRecomp.Multiplayer.Characters.IsOriginal(pick);
                _settings.MenuArtHd = hd;
                if (hd) _settings.MenuArtSuit = pick == MultiplayerMenu.ShuffleChoice ? RecompSettings.MenuArtShuffle : pick;
                _settings.Save(_settingsPath);
                RefreshSettingsPages();
                MenuArtChanged?.Invoke();
            });
        }

        // the LOOK or SOUND row the cursor is on (its label, e.g. "music\nquality"), or null off those pages: the pause menu
        // plays the paused music while it's on a music row, so a change is heard live (CampaignAudio.UpdateAudioPause)
        public string? FocusedSetting(MenuEngine menu)
        {
            SectionPage? sp = menu.Page?.Index == _soundPage?.Page ? _soundPage : _lookPage;
            if (sp == null || menu.Page?.Index != sp.Page || sp.Focus < 0 || sp.Focus >= sp.Entries.Count) return null;
            return sp.Entries[sp.Focus].Row?.Label;
        }

        // write the settings file now (OPTIONS > AUDIO's changes, AudioOptions)
        internal void SaveSettings() => _settings.Save(_settingsPath);
        public IReadOnlyList<ModEntry> Mods => _mods;
        public bool IsModEnabled(string id) => !_settings.DisabledMods.Contains(id);

        // ---- the game's pieces the recomp screens are cloned from ----
        // Found by what they are (a widget and its callback, or a text's position), never by index: revisions insert
        // items (USA rev 1 adds a "TM" and an "R" logo piece after the logo, shifting everything after by two).
        private sealed class Pieces
        {
            public MenuItem[] Frame = Array.Empty<MenuItem>(); // backdrop, back icon, top band, logo (+ its rev-1 parts)
            public MenuItem Back = null!, Desc = null!, Arrows = null!, Box = null!, Label = null!, Value = null!, Right = null!, Left = null!;
            public MenuItem[] ChoiceBoxes = Array.Empty<MenuItem>(), ChoiceTexts = Array.Empty<MenuItem>();
            public MenuItem OptBox = null!, OptLabel = null!, OptValue = null!, OptDesc = null!, OptHeader = null!;
            public MenuItem? LrIcons; // the hunter license's L / R shoulder icons (one widget, both icons)
            public MenuItem? EraseAll, EraseAllText; // Options' ERASE ALL DATA button and its text
        }

        private Pieces _p = null!;

        private bool HasWidget(MenuItem it, string name) => it.States.Any(s => s.WidgetIndex >= 0
            && _file.Widgets[s.WidgetIndex].ModelPath.EndsWith("\\" + name + "_Model.bin", StringComparison.OrdinalIgnoreCase));

        internal static bool HasCall(MenuItem it, int call) => it.Actions.Any(a => a.Calls.Any(c => c.B == call));

        internal static bool TextAt(MenuItem it, float x, float y) => it.IsText && MathF.Abs(it.X - x) < 1 && MathF.Abs(it.Y - y) < 1;

        private static MenuItem Find(MenuPage page, string what, Func<MenuItem, bool> match)
            => page.Items.FirstOrDefault(match) ?? throw new InvalidOperationException($"menu page {page.Index} has no {what}");

        private Pieces FindPieces(MenuPage options, MenuPage controls)
        {
            string[] frame = { "blackdrop", "backicon", "topdrop", "toplogo", "toplogoR" };
            return new Pieces
            {
                Frame = controls.Items.Where(it => frame.Any(n => HasWidget(it, n)) || (it.IsText && it.Y >= 300)).ToArray(),
                Back = Find(controls, "back button", it => HasWidget(it, "backhighlight")),
                Desc = Find(controls, "SENSITIVITY description", it => TextAt(it, 128, 228)),
                Arrows = Find(controls, "SENSITIVITY arrows", it => HasWidget(it, "arrows_option") && it.Y > -20),
                Box = Find(controls, "SENSITIVITY box", it => HasWidget(it, "box_arrows") && HasCall(it, 73)),
                Label = Find(controls, "SENSITIVITY label", it => TextAt(it, 154, 76)),
                Value = Find(controls, "SENSITIVITY value", it => TextAt(it, 223, 77)),
                Right = Find(controls, "right arrow", it => HasWidget(it, "highlight_arrowright") && HasCall(it, 73)),
                Left = Find(controls, "left arrow", it => HasWidget(it, "highlight_arrowleft") && HasCall(it, 74)),
                ChoiceBoxes = controls.Items.Where(it => HasWidget(it, "selectAoff")).OrderByDescending(it => it.Y).Take(4).ToArray(),
                // the orange labels (depth -3); white "selected" copies sit on top of them at depth 0
                ChoiceTexts = controls.Items.Where(it => it.IsText && MathF.Abs(it.X - 63) < 1 && it.Y > 40 && it.Depth < 0).OrderByDescending(it => it.Y).Take(4).ToArray(),
                OptBox = Find(options, "SHOW MY STATS box", it => HasWidget(it, "box_arrows") && HasCall(it, StatsCall)),
                OptLabel = Find(options, "SHOW MY STATS label", it => TextAt(it, 205, 94)),
                OptValue = Find(options, "SHOW MY STATS value", it => TextAt(it, 205, 55)),
                OptDesc = Find(options, "Options description", it => TextAt(it, 128, 227)),
                OptHeader = Find(options, "Options header", it => TextAt(it, 128, 172)),
                LrIcons = _file.Pages.SelectMany(p => p.Items).FirstOrDefault(it => HasWidget(it, "lricons")),
                EraseAll = options.Items.FirstOrDefault(it => HasWidget(it, "eraseall")),
                // (217, 147) on USA rev 0, (217, 148) on rev 1
                EraseAllText = options.Items.FirstOrDefault(it => it.IsText && MathF.Abs(it.X - 217) < 1 && MathF.Abs(it.Y - 147.5f) < 2),
            };
        }

        // ---- building ----

        public void Install()
        {
            if (_file.Pages.Count <= ControlsPage) return;
            MenuPage options = _file.Pages[OptionsPage];
            MenuPage controls = _file.Pages[ControlsPage];
            _p = FindPieces(options, controls);
            if (_p.ChoiceBoxes.Length < 4 || _p.ChoiceTexts.Length < 4) throw new InvalidOperationException("Controls page: no control-type boxes");

            // SECOND HUNT SETTINGS' pages and CONTROLS, in sections (RecompMenus.SettingsPages.cs, RecompMenus.Controls.cs):
            // first, as the pages below go back to them
            InstallSectionMenus();

            _modsRows = BuildRowsPage(controls, "MANAGE MODS", OptionsPage, PagingActions(),
                "no mods installed.\n\ncopy each mod's folder (with its mod.json) into\n" + (RecompSettings.DesktopHost
                    ? "the mods folder in the game's data folder" : "android/data/" + RecompSettings.AppId + "/files/mods")
                    + "\nand come back here.");
            _modsRows.Source = ModRows;

            _modSettingRows = BuildRowsPage(controls, "MOD SETTINGS", -1, PagingActions(),
                "no settings.\n\nturn on a mod that has settings (see its mod.json) to change them here.");
            _modSettingRows.Source = ModSettingRows;

            // IMPORT GAMES (owner 2026-10-07): the games, then one game's page; its back is the games
            // DELETE ALL (owner 2026-10-07: "a delete all button somewhere that looks nice for the import games screen and
            // the first page of every game"): everything, or everything from the game open; shown while there's some
            _importRows = BuildRowsPage(controls, "IMPORT GAMES", SettingsPage, PagingActions(), null, backCall: ImportBack,
                deleteAll: "deletes everything you imported from every game. your discs can import it again.");
            _importRows.Source = ImportRows;
            _importRows.DeleteAll = () => Import.AnyImported() ? Import.ClearEverything : null;
            _importGameRows = BuildRowsPage(controls, "IMPORT", _importRows.Page, PagingActions().Concat(ImportGamePageActions()), null,
                deleteAll: "deletes everything you imported from this game. your disc can import it again.");
            _importGameRows.Source = ImportGameRows;
            _importGameRows.DeleteAll = () =>
            {
                string id = _importGame;
                if (_importGameRows.Offset != 0 || !Import.AnyImported(id) || Import.Game(id) is not { } g) return null;
                List<MphRecomp.Import.ImportItemInfo> all = g.Items.ToList();
                string everything = $"everything from {ImportGames.Row(id).Title.ToLowerInvariant()}";
                return () => Import.Clear(id, all, everything);
            };
            InstallMusicPages(controls); // a game's song list, RECOMP SETTINGS' song list (RecompMenus.Music.cs)
            InstallImportCards(); // IMPORT GAMES' page as five game cards (RecompMenus.ImportCards.cs)

            _pausePage = BuildPausePage(controls, options, chat: false);
            _pauseChatPage = BuildPausePage(controls, options, chat: true);
            AddOptionsButtons(options);

            InstallReplays(); // REPLAYS (RecompMenus.Replays.cs; the multiplayer menu's REPLAYS button)
            InstallOnline(); // ONLINE PLAY (OnlineMenus.cs; the multiplayer menu's ONLINE PLAY door)
            AddControlsButtons(controls); // last: the pages above are cloned from the Controls page as the ROM has it
            InstallControlsCopy(controls);
            HideSaveButtons();
            foreach (RowsPage p in AllRowPages()) Refresh(p);
        }

        private IEnumerable<RowsPage> AllRowPages() => new[] { _modsRows, _modSettingRows, _importRows, _importGameRows,
                _songRows, _songFilterRows }
            .Where(p => p != null)!;

        private static IEnumerable<MenuAction> PagingActions()
            => new[] { Call(MenuKeys.R, PageNext), Call(MenuKeys.L, PagePrev) };

        private RowModel VolumeRow(string label, Func<int> get, Action<int> set) => new()
        {
            Label = label,
            Value = () => Math.Clamp(get(), 0, 9).ToString(),
            Step = s => set(Math.Clamp(get() + s, 0, 9)),
            Reset = () => set(9),
            AtDefault = () => get() == 9,
        };

        private List<RowModel> SettingRows()
        {
            T Next<T>(T[] all, T value, int step)
            {
                int i = Array.IndexOf(all, value);
                if (i < 0) i = 0;
                return all[((i + step) % all.Length + all.Length) % all.Length];
            }
            var rows = new List<RowModel>
            {
                new()
                {
                    Label = "menu\nscreens",
                    Description = "one puts both ds screens' menus together on one screen. two shows the two screens side by side.",
                    Value = () => _settings.MenuLayout == "sidebyside" ? "TWO" : "ONE",
                    Step = s => _settings.MenuLayout = Next(RecompSettings.MenuLayouts, _settings.MenuLayout, s)
                },
                new()
                {
                    Label = "menu\nart",
                    Description = "samus behind the menus: one of your imported suits, shuffle (another one each time the game starts), or original (the ds picture).",
                    Value = MenuArtValue,
                    Step = _ => OpenMenuArt(),
                    Action = true
                },
                new()
                {
                    Label = "smooth\nmenus",
                    // shown the other way round from the stored flag (owner 2026-10-04): ON = clean, sharp edges
                    Description = "on draws the menu art clean and sharp. off filters it softer.",
                    Value = () => _settings.SmoothMenus ? "OFF" : "ON",
                    Step = _ => _settings.SmoothMenus = !_settings.SmoothMenus
                },
                new()
                {
                    Label = "line\nart",
                    Description = "smooth redraws the hud's line art as clean lines and shapes, traced from your rom. pixel keeps the ds pixels.",
                    Value = () => _settings.RedrawArt ? "SMOOTH" : "PIXEL",
                    Step = _ => _settings.RedrawArt = !_settings.RedrawArt
                },
                // OPTIONS > AUDIO's volumes here too (owner 2026-10-17: "we should also get all the sound settings from the
                // audio menu in here as well. the sfx volume, music volume, and quality"), steps 0-9 as that page's, heard
                // at once (FrontendRenderer / CampaignAudio read the saved values); RESET puts back 9
                VolumeRow("sfx\nvolume", () => _settings.SfxVolume, v => _settings.SfxVolume = v),
                VolumeRow("music\nvolume", () => _settings.MusicVolume, v => _settings.MusicVolume = v),
                new()
                {
                    Label = "music\nquality",
                    // not the first row (owner 2026-10-05): in the pause menu the paused music plays while the cursor is
                    // on this row (FocusedSetting), and the first row has the cursor as soon as the page opens.
                    // all four are the game's own sequences (GameAudio.cs MusicMode); a change applies live, mid-song
                    // (SetQuality). 3 lines at wrap 230 (FE_WRAP).
                    // the value box holds ~7 letters, so hq + fixes + tone shows as HQ+TONE; its full name is here
                    Description = "orig: the ds. orig+fix: no clicks or clipping. hq+fix: smoother. "
                        + "hq+tone: hq + fixes + the ds's treble. changes live.",
                    Value = () => _settings.Music switch
                    {
                        "plain" => "ORIG",
                        "hq" => "HQ+FIX",
                        "hqtone" => "HQ+TONE",
                        _ => "ORIG+FIX",
                    },
                    Step = s => _settings.Music = Next(RecompSettings.MusicSources, _settings.Music, s)
                },
                // dev builds only (owner 2026-10-05: the DS's control types kept for dual-screen Thor testing)
                new()
                {
                    Label = "controls\npage",
                    Description = "options > controls and the ship's options. new: the control customizer. classic: the ds's stylus and dual mode boxes.",
                    Value = () => _settings.ClassicControlsPage ? "CLASSIC" : "NEW",
                    Step = _ => _settings.ClassicControlsPage = !_settings.ClassicControlsPage
                },
                new()
                {
                    Label = "developer",
                    Description = "adds a dev tools row below: trophy viewers, rooms, animations, music a/b.",
                    Value = () => _settings.DeveloperMode ? "ON" : "OFF",
                    Step = _ => _settings.DeveloperMode = !_settings.DeveloperMode
                },
                new()
                {
                    Label = "status\ntext",
                    Description = "the upper-left text in gameplay: fps, timings, the scoreboard. l3 cycles it.",
                    Value = () => _settings.StatusText ? "ON" : "OFF",
                    Step = _ => _settings.StatusText = !_settings.StatusText
                },
                new()
                {
                    Label = "dev\ntools",
                    Description = "the developer launcher: trophy viewers, rooms, animations, music a/b.",
                    Value = () => "OPEN",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.OpenDeveloperMenu),
                    Action = true
                },
                // IMPORT GAMES (owner 2026-10-07): the player's own Prime discs -> the suits, balls and arm cannons below
                new()
                {
                    Label = "import\ngames",
                    Description = RecompSettings.PublicBuild
                        ? "make hd suits and morph balls from your own metroid prime game discs."
                        : "make hd suits, morph balls and arm cannons from your own metroid prime game discs.",
                    Value = () => "OPEN",
                    Step = _ =>
                    {
                        if (_inPause) Import.Popup?.Inform("games are imported from the main menu. quit to it first.");
                        else if (_importRows != null)
                        {
                            _importBackPage = SettingsPage;
                            _stepMenu?.GoTo(_importRows.Page);
                        }
                    },
                    Action = true
                },
                new()
                {
                    Label = "samus\nsuit",
                    Description = $"samus's suit in the campaign: {SuitLabel(ShownSuit()).ToLowerInvariant()}. "
                        + (RecompSettings.PublicBuild ? "her model (third person and morph ball), mph's arm cannon; original is mph's own."
                            : "her model (third person and morph ball) and arm cannon; original is mph's own."),
                    Value = () => SuitValue(ShownSuit()),
                    Step = s => _settings.SamusSuit = Next(_suits.ToArray(), ShownSuit(), s)
                },
                new()
                {
                    Label = "camera",
                    Description = "the campaign's camera. first person is the game's own view. third person follows samus from behind, wearing her suit's body.",
                    Value = () => _settings.ThirdPerson ? "THIRD" : "FIRST",
                    Step = _ => _settings.ThirdPerson = !_settings.ThirdPerson
                },
                // multiplayer matches' own camera (owner 2026-10-17): bot and LAN matches; online is first person only
                new()
                {
                    Label = "match\ncamera",
                    Description = "the camera in multiplayer matches. third person follows your hunter from behind in bot and lan matches. online matches are always first person.",
                    Value = () => _settings.MatchThirdPerson && !InOnlineMatch ? "THIRD" : "FIRST",
                    Step = _ => _settings.MatchThirdPerson = !_settings.MatchThirdPerson,
                    Disabled = () => InOnlineMatch // an online match's pause: first person only, nothing to change
                },
                // the pause map (Select in the campaign; Core Frontend/PauseMap.cs)
                new()
                {
                    Label = "map\nlook",
                    Description = "map: the game's own map models. detailed: every room drawn from its real shape. y switches it in the map.",
                    Value = () => _settings.MapDetailed ? "DETAILED" : "MAP",
                    Step = _ => _settings.MapDetailed = !_settings.MapDetailed
                },
                new()
                {
                    Label = "map\nmode",
                    Description = "prime: only rooms and corridors you've been in. move freely, the d-pad hops rooms. hunters: the game's own.",
                    Value = () => _settings.MapPrime ? "PRIME" : "HUNTERS",
                    Step = _ => _settings.MapPrime = !_settings.MapPrime
                },
                // the in-play HUD (owner 2026-10-14; MphRead PlayerPrimeHud.cs): live, the game reads it back after the pause
                new()
                {
                    Label = "hud",
                    Description = "prime: in the campaign a radar plus a mini map of the room you're in. ds: the game's own.",
                    Value = () => _settings.PrimeHud ? "PRIME" : "DS",
                    Step = _ => _settings.PrimeHud = !_settings.PrimeHud
                },
                // OPTIONS > CONTROLS' copy (owner 2026-10-05: "controls" here, the same page inside RECOMP SETTINGS)
                new()
                {
                    Label = "controls",
                    Description = "change which buttons do what, and how quickly you turn and aim.",
                    Value = () => "OPEN",
                    Step = _ =>
                    {
                        // CONTROLS in sections (RecompMenus.Controls.cs), or with CLASSIC controls the game's page's copy
                        int to = s_controlsPage >= 0 && !ClassicControls ? s_controlsPage : _controlsCopyPage;
                        if (to >= 0) _stepMenu?.GoTo(to);
                    },
                    Action = true
                },
                // gyro aim (added by the gyro session with the owner's OK; the campaign reads these in CampaignGyro.cs)
                new()
                {
                    Label = "gyro\naim",
                    Description = "turn the device to fine-tune your aim. the right stick still turns as usual. zoom: only while zoomed or scanning.",
                    Value = () => _settings.Gyro switch { "on" => "ON", "zoom" => "ZOOM", _ => "OFF" },
                    Step = s => _settings.Gyro = Next(RecompSettings.GyroModes, _settings.Gyro, s)
                },
                new()
                {
                    Label = "gyro\nspeed",
                    Description = "how far the view turns for each turn of the device. 100% turns it the same angle.",
                    Value = () => _settings.GyroSensitivity + "%",
                    Step = s => _settings.GyroSensitivity = Next(RecompSettings.GyroSensitivities, _settings.GyroSensitivity, s)
                },
                // C6: a reset wherever a sensitivity is (greyed while it's at the default)
                new()
                {
                    Label = "reset\ngyro speed",
                    Description = "gyro speed back to its default, " + DefaultSettings.GyroSensitivity + "%.",
                    Action = true,
                    Value = () => "RESET",
                    Step = _ => _settings.GyroSensitivity = DefaultSettings.GyroSensitivity,
                    Disabled = () => _settings.GyroSensitivity == DefaultSettings.GyroSensitivity
                },
                new()
                {
                    Label = "gyro\naxis",
                    Description = "yaw: the screen's own up axis. player: true up, however you hold it. roll: tilt to steer.",
                    Value = () => _settings.GyroAxis.ToUpperInvariant(),
                    Step = s => _settings.GyroAxis = Next(RecompSettings.GyroAxes, _settings.GyroAxis, s)
                },
                // Beta 1 (public beta audit S29, #30b): touch controls, the credits and the crash log
                new()
                {
                    Label = "touch\ncontrols",
                    Description = "on-screen controls in the campaign and in matches. auto shows them only while no controller is connected.",
                    Value = () => _settings.TouchControls.ToUpperInvariant(),
                    Step = s => _settings.TouchControls = Next(RecompSettings.TouchModes, _settings.TouchControls, s)
                },
                // stylus aim (owner 2026-10-16): the touch controls' right-hand finger aims like the DS stylus
                new()
                {
                    Label = "touch\naim",
                    Description = "how the right-hand finger aims. stylus: drag to turn the view, as on the ds. stick: a floating stick.",
                    Value = () => _settings.TouchAim.ToUpperInvariant(),
                    Step = s => _settings.TouchAim = Next(RecompSettings.TouchAimModes, _settings.TouchAim, s)
                },
                // replays (added by the replay session, board rule 2; docs/REPLAY_MODE.md R2): every local match against
                // bots recorded, the last ten kept (App Campaign/MatchReplay.cs)
                new()
                {
                    Label = "record\nreplays",
                    Description = "on records every match against bots and keeps the last ten. off records nothing.",
                    Value = () => _settings.RecordReplays ? "ON" : "OFF",
                    Step = _ => _settings.RecordReplays = !_settings.RecordReplays
                },
                // multiplayer unlocks (added by the vanilla multiplayer menu session, board rule 2; owner 2026-10-07: vanilla
                // by default, ALL after a confirmation; MultiplayerMenu reads it as its pages open)
                new()
                {
                    Label = "unlocks",
                    Description = "multiplayer hunters and arenas. vanilla: earned as in the original game. all: every one open.",
                    Value = () => _settings.UnlockEverything ? "ALL" : "VANILLA",
                    Step = _ => AskUnlockEverything()
                },
                // First Hunt (added by the First Hunt session, board rule 2; owner 2026-10-14: it lives under EXTRAS): the
                // imported cart's own game, its title screen first; Android dev builds only while the port is in progress
                new()
                {
                    Label = "extras",
                    Description = "first hunt: the demo cart you imported, played as its own game from its title screen.",
                    Value = () => "FIRST HUNT",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.FirstHunt),
                    Action = true
                },
                new()
                {
                    Label = "credits",
                    Description = "who made the tools and libraries this app is built on, and their licences.",
                    Value = () => "VIEW",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.Credits),
                    Action = true
                },
                new()
                {
                    Label = "crash\nlog",
                    Description = RecompSettings.DesktopHost
                        ? "if the game has crashed, opens the folder with the newest crash report (it holds no personal data) to send to the developer."
                        : "if the app has crashed, share the newest crash report (it holds no personal data) with the developer.",
                    Value = () => RecompSettings.DesktopHost ? "OPEN" : "SHARE",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.ShareCrashLog),
                    Action = true
                },
                // owner 2026-10-11: also checked once at launch in the background (ON by default), asking on the main menu
                // only when an update is out (FrontendSession.TickLaunchCheck); OFF = only when CHECK is pressed
                new()
                {
                    Label = "update\nalerts",
                    Description = "on: when the game starts, it asks github.com for a newer second hunt and offers it on the main menu. off: only when you press check.",
                    Value = () => _settings.CheckUpdatesAtLaunch ? "ON" : "OFF",
                    Step = _ => _settings.CheckUpdatesAtLaunch = !_settings.CheckUpdatesAtLaunch
                },
                // owner 2026-10-06: installed by the game; from the main menu only, not a paused campaign or match
                // (FrontendSession.CheckForUpdates)
                new()
                {
                    Label = "updates",
                    Description = "from the main menu: asks github.com for a newer second hunt now and installs it.",
                    Value = () => "CHECK",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.CheckUpdates),
                    Action = true
                },
            };
            // the Prime songs' rows under the music quality row (owner 2026-10-14, RecompMenus.Music.cs); not in a public build
            // (owner 2026-10-17: not in the next release)
            if (!RecompSettings.PublicBuild) rows.InsertRange(rows.FindIndex(r => r.Label == "music\nquality") + 1, MusicSettingRows());
            // a public build leaves out the rows for what it doesn't ship: imported HD suits, the HD menu art and the
            // developer tools (the music quality row stays: owner 2026-10-05, all four modes are the game's own music)
            if (RecompSettings.PublicBuild) rows.RemoveAll(r => HiddenInPublic.Contains(r.Label));
            if (RecompSettings.PublicBuild || RecompSettings.DesktopHost || !MphRecomp.FirstHunt.FirstHuntGame.Available(
                _settingsPath != null ? System.IO.Path.GetDirectoryName(_settingsPath) : null))
            {
                rows.RemoveAll(r => r.Label == "extras");
            }
            if (RecompSettings.DesktopHost) rows.RemoveAll(r => HiddenOnDesktop.Contains(r.Label));
            if (!_settings.DeveloperMode) rows.RemoveAll(r => r.Label == "dev\ntools" || r.Label == "status\ntext");
            // (in categories, each its own page: RecompMenus.SettingsPages.cs)
            return rows;
        }

        // (owner 2026-10-07: IMPORT GAMES, the samus suit and the hd menu art are public now; the arm cannons stay held,
        // out of the importers in every build since Reports R8: see ImportGames.Offered and CampaignRenderer.StartGearLoad)
        private static readonly HashSet<string> HiddenInPublic = new(StringComparer.Ordinal)
        {
            "developer", "dev\ntools", "controls\npage",
            // the campaign's Prime radar + mini map (owner 2026-10-17: map work, not in the next release; a
            // multiplayer match's radar comes with the SHOW HUNTERS ON RADAR rule instead, every build)
            "hud",
        };

        // a PC has no gyroscope, and its mouse is the pointer (no on-screen controls)
        private static readonly HashSet<string> HiddenOnDesktop = new(StringComparer.Ordinal)
        {
            "gyro\naim", "gyro\nspeed", "reset\ngyro speed", "gyro\naxis", "touch\ncontrols",
        };


        // UNLOCKS: back to VANILLA at once (what was earned stays earned); to ALL only after the game's yes/no box (owner
        // 2026-10-07), its answer saving and showing the row itself (the row's own step has already saved by then)
        private void AskUnlockEverything()
        {
            if (_settings.UnlockEverything)
            {
                _settings.UnlockEverything = false;
                return;
            }
            if (Import.Popup == null)
            {
                _settings.UnlockEverything = true;
                return;
            }
            Import.Popup.Ask("open every multiplayer hunter and arena without earning them?\n\nvanilla's unlocks come back if you switch to vanilla again.", yes =>
            {
                if (!yes) return;
                _settings.UnlockEverything = true;
                _settings.Save(_settingsPath);
                RefreshSettingsPages();
            });
        }

        private List<RowModel> ModRows() => _mods.Select(m => new RowModel
        {
            Label = Short(m.Manifest.Name.Length > 0 ? m.Manifest.Name : m.Manifest.Id),
            Description = $"{m.Manifest.Description ?? m.Manifest.Name} (v{m.Manifest.Version})".ToLowerInvariant()
                + (m.Manifest.Settings.Count > 0 ? " -- x: its settings" : ""),
            Value = () => IsModEnabled(m.Manifest.Id) ? "ON" : "OFF",
            Step = _ =>
            {
                if (!_settings.DisabledMods.Remove(m.Manifest.Id)) _settings.DisabledMods.Add(m.Manifest.Id);
            },
            Alt = m.Manifest.Settings.Count == 0 ? null : () => _modSettingsFilter = m.Manifest.Id
        }).ToList();

        private List<RowModel> ModSettingRows()
        {
            var rows = new List<RowModel>();
            foreach (ModEntry m in _mods)
            {
                ModManifest mod = m.Manifest;
                if (_modSettingsFilter != null ? mod.Id != _modSettingsFilter : !IsModEnabled(mod.Id)) continue;
                foreach (ModSetting setting in mod.Settings)
                {
                    if (setting.Values.Count == 0) continue;
                    string Current() => _settings.GetModSetting(mod.Id, setting.Id) ?? setting.DefaultValue;
                    rows.Add(new RowModel
                    {
                        Label = Short(setting.Name.Length > 0 ? setting.Name : setting.Id),
                        Description = $"{mod.Name}: {setting.Description ?? setting.Name}".ToLowerInvariant(),
                        Value = () => Current().ToUpperInvariant(),
                        Step = s =>
                        {
                            int i = Math.Max(0, setting.Values.IndexOf(Current()));
                            int n = setting.Values.Count;
                            _settings.SetModSetting(mod.Id, setting.Id, setting.Values[((i + s) % n + n) % n]);
                        }
                    });
                }
            }
            return rows;
        }

        private static string Short(string s) => (s.Length > 22 ? s[..22] : s).ToLowerInvariant();

        // ---- IMPORT GAMES ----

        // a game a row: how many of its items are imported, what its disc makes; A opens its page
        private List<RowModel> ImportRows()
        {
            var rows = new List<RowModel>();
            foreach (ImportGames.GameRow row in ImportGames.GameRows)
            {
                string id = row.Id;
                if (id == ImportGames.TrilogyId)
                {
                    // one disc, every game on it: A = the picker, then the ask box (ImportGames.ImportTrilogy)
                    (int tDone, int tTotal) = Import.TrilogyCount();
                    rows.Add(new RowModel
                    {
                        Label = row.Label,
                        Description = $"everything from one metroid prime trilogy disc: prime 1, echoes and corruption. {tDone} of {tTotal} imported.",
                        Value = () => $"{tDone}/{tTotal}",
                        Step = _ => Import.ImportTrilogy(),
                        Action = true
                    });
                    continue;
                }
                MphRecomp.Import.ImportGameInfo? g = row.Available ? Import.Game(id) : null;
                if (g == null)
                {
                    rows.Add(new RowModel
                    {
                        Label = row.Label,
                        Description = $"{row.Title.ToLowerInvariant()} can't be imported yet.",
                        Value = () => "SOON",
                        Step = _ => Import.Popup?.Inform(FrontendSession.WorkInProgressText),
                        Action = true
                    });
                    continue;
                }
                int done = g.Items.Count(i => i.Status == MphRecomp.Import.ItemImportStatus.Complete);
                int suits = g.Items.Count(ImportGames.IsSuit);
                int balls = g.Items.Count(ImportGames.IsBall);
                int others = g.Items.Count(i => i.Kind == "hd" && !ImportGames.IsSuit(i) && !ImportGames.IsBall(i));
                int cannons = g.Items.Count(i => i.Kind == "guns");
                int arenas = g.Items.Count(i => i.Kind == "arenas");   // Echoes' multiplayer arenas + music (CR2)
                var makes = new List<string>();
                if (suits > 0) makes.Add($"{suits} suit{(suits == 1 ? "" : "s")}");
                if (balls > 0) makes.Add($"{balls} morph ball{(balls == 1 ? "" : "s")}");
                if (others > 0) makes.Add($"{others} character{(others == 1 ? "" : "s")}");
                if (cannons > 0) makes.Add($"{cannons} arm cannon{(cannons == 1 ? "" : "s")}");
                if (arenas > 0) makes.Add($"{arenas} multiplayer arena{(arenas == 1 ? "" : "s")}");
                if (g.Items.Any(i => i.Kind == "music")) makes.Add("their music");
                string list = makes.Count > 1 ? String.Join(", ", makes.Take(makes.Count - 1)) + " and " + makes[^1] : makes.FirstOrDefault() ?? "nothing yet";
                rows.Add(new RowModel
                {
                    Label = row.Label,
                    Description = $"makes {list} from your {row.Platform} disc. {done} of {g.Items.Count} imported.",
                    Value = () => $"{done}/{g.Items.Count}",
                    Step = _ =>
                    {
                        _importGame = id;
                        if (_importGameRows == null) return;
                        _importGameRows.Title = row.Title;
                        _stepMenu?.GoTo(_importGameRows.Page);
                    },
                    Action = true
                });
            }
            return rows;
        }

        // one game: its disc, IMPORT ALL, (a game taking Wii discs) the Wii key file, then every item with its status
        private List<RowModel> ImportGameRows()
        {
            string id = _importGame;
            var rows = new List<RowModel>();
            MphRecomp.Import.ImportGameInfo? g = Import.Game(id);
            if (g == null) return rows;
            ImportGames.GameRow game = ImportGames.Row(id);
            ImportPicked? src = Import.Source(id);
            int done = g.Items.Count(i => i.Status == MphRecomp.Import.ItemImportStatus.Complete);
            rows.Add(new RowModel
            {
                Label = "your\ndisc",
                Description = src == null
                    ? $"pick your {(game.Discs.Length > 0 ? game.Discs : game.Title.ToLowerInvariant())} disc image ({Import.Extensions(id)}). it's read where it is, never copied."
                    : $"{src.Name.ToLowerInvariant()}. a: pick another disc image.",
                Value = () => src == null ? "PICK" : "CHANGE",
                Step = _ => Import.PickDisc(id),
                Action = true
            });
            rows.Add(new RowModel
            {
                Label = "import\nall",
                Description = $"makes all {g.Items.Count} from your disc. {done} of {g.Items.Count} imported now."
                    + (g.Items.Any(i => i.Kind == "hd") ? " the suits take a few minutes." : ""),
                Value = () => "START",
                Step = _ => Import.ImportAll(id),
                Action = true
            });
            int stale = g.Items.Count(ImportGames.NeedsUpdate);
            rows.Add(new RowModel
            {
                Label = "update\nall",
                Description = stale == 0 ? "nothing needs an update: everything imported is up to date."
                    : $"imports again the {stale} an update changed (the red badge), from your disc.",
                Value = () => stale == 0 ? "NONE" : "START",
                Step = _ => Import.UpdateAll(id),
                Action = true,
                Disabled = () => stale == 0
            });
            if (Import.TakesWiiDiscs(id))   // Prime 3, and Prime 1 / Echoes from a Trilogy disc
            {
                // greyed while the picked disc needs no key (owner 2026-10-07: a GameCube disc; an .rvz / .wia neither)
                bool keyNeeded = Import.WiiKeyNeeded(id);
                rows.Add(new RowModel
                {
                    Label = "wii key\nfile",
                    Description = keyNeeded
                        ? "only a plain .iso or .wbfs needs it (.rvz and .wia don't): your own wii's keys.bin, or a text file with its 32-digit key."
                        : "your disc needs no wii key: only a plain wii .iso or .wbfs does.",
                    Value = () => Import.HasWiiKey ? "SET" : "NONE",
                    Step = _ => Import.PickWiiKey(),
                    Action = true,
                    Disabled = () => !keyNeeded
                });
            }
            // each entry: its X (only once imported: nothing to delete, no X) deletes it after the ask box (owner 2026-10-07:
            // "an X in a small orange box next to each entry", in place of the clear rows)
            foreach (MphRecomp.Import.ImportItemInfo item in g.Items)
            {
                bool made = ImportGames.Imported(item);
                string desc = Import.ItemDescription(id, item);
                string x = made ? " x: delete." : "";
                int room = 130 - x.Length;
                rows.Add(new RowModel
                {
                    Label = ImportGames.ItemLabel(item),
                    Description = (desc.Length > room ? desc[..(room - 3)] + "..." : desc) + x,
                    Value = () => ImportGames.ItemValue(id, item),
                    Step = _ => Import.ImportOne(id, item),
                    Action = true,
                    Delete = made ? () => Import.Clear(id, new[] { item }, ImportGames.ItemName(item)) : null
                });
            }
            return rows;
        }

        // an import ended or a pick changed: the suit row's list and the import pages read things again
        private void OnImportChanged()
        {
            ScanSuits();
            RefreshSettingsPages(); // (the suit row's list)
            foreach (RowsPage? p in new[] { _importRows, _importGameRows, _songRows, _songFilterRows })
            {
                if (p == null) continue;
                Refresh(p);
                // a delete or a new disc changes which rows are greyed (the wii key file) and which have an X
                if (_stepMenu?.Page?.Index == p.Page) ShowDisabled(_stepMenu, p);
            }
        }

        // A page made from the Controls page's frame and six copies of its SENSITIVITY row. `back` = the page its back
        // button and B return to (-1 = MODS); `empty` = text shown when the page has no rows; `backCall` (>= 0): the back
        // button and B run that callback instead of going to `back` (it decides where back is); `valueGrow`: how much wider
        // than SENSITIVITY's the value boxes are.
        private RowsPage BuildRowsPage(MenuPage controls, string title, int back, IEnumerable<MenuAction> extraActions, string? empty,
            int backCall = -1, float valueGrow = ValueGrow, string? deleteAll = null, bool removeKey = false, float labelGrow = 0)
        {
            int BackItem = _p.Frame.Length + 1; // after the frame items and the header
            int backPage = back == -1 ? ModsPage : back;
            MenuAction pageBack = backCall >= 0
                ? new MenuAction { Kind = (ushort)MenuKeys.B, Calls = new[] { (0, backCall) }, TargetPage = 0xFF, Item = (ushort)BackItem, Field17 = 4 }
                : new MenuAction { Kind = (ushort)MenuKeys.B, TargetPage = (byte)backPage, Item = (ushort)BackItem, Field17 = 4 };
            var result = new RowsPage();
            MenuPage page = _file.AddPage(index => new MenuPage
            {
                Index = index,
                Field0 = controls.Field0,
                Field14 = controls.Field14,
                Field16 = controls.Field16,
                Field18 = controls.Field18,
                Field1D = controls.Field1D,
                Field1E = controls.Field1E,
                Flags = controls.Flags,
                Items = Array.Empty<MenuItem>(),
                // B flashes our back button and goes back
                Actions = new[] { pageBack }.Concat(extraActions).ToArray(),
                Timers = Array.Empty<MenuTimer>()
            });
            result.Page = page.Index;
            // frame: backdrop, back icon, top band, logo, the pulsing header, the back button
            foreach (MenuItem it in _p.Frame)
            {
                float grow = HasWidget(it, "blackdrop") ? PanelGrow : HasWidget(it, "topdrop") ? DescGrow : 0;
                page.AddItem(i => Clone(it, i, 0, 0, stretchDown: grow));
            }
            int headerString = _strings.Add(title);
            result.Title = title;
            result.Header = headerString;
            page.AddItem(i => Clone(_p.OptHeader, i, 0, 0, stringId: headerString));
            MenuItem backButton = _p.Back;
            int backItem = page.AddItem(i => Clone(backButton, i, 0, 0,
                actions: backButton.Actions.Select(a => backCall >= 0 ? WithCall(a, backCall) : WithTarget(a, backPage)).ToList())).Index;
            if (backItem != BackItem) throw new InvalidOperationException("recomp page: back button index moved");
            result.Box = new int[RowsPerPage];
            result.Label = new int[RowsPerPage];
            result.Value = new int[RowsPerPage];
            result.Desc = new int[RowsPerPage];
            result.Del = new int[RowsPerPage];
            result.DelX = new int[RowsPerPage];
            int delString = _strings.Add("X");
            for (int r = 0; r < RowsPerPage; r++)
            {
                // labelGrow: a wider label column (long names, e.g. the song lists): the label's wrap grows by it about its
                // centre and the value side (box, arrows, X) moves right by half of it, into the panel's spare room
                float dx = -60 + labelGrow / 2, dy = 74 - RowPitch * r;
                int label = _strings.Add(""), value = _strings.Add(""), desc = _strings.Add("");
                int descItem = page.AddItem(i => Clone(_p.Desc, i, 0, 0, stringId: desc, init: MenuState.Hidden)).Index;
                page.AddItem(i => Clone(_p.Arrows, i, dx, dy, stretchX: valueGrow));
                int row = r;
                // the first row starts focused, so its description shows and the D-pad has somewhere to start
                int box = page.AddItem(i => Clone(_p.Box, i, dx, dy, init: row == 0 ? MenuState.Focused : null, stretchX: valueGrow, greyed: 0,
                    links: new[] { new MenuLink(39, MenuState.Idle, descItem), new MenuLink(60, MenuState.Hidden, descItem) },
                    actions: new[]
                    {
                        Call(MenuKeys.A, RowNext + row), Call(MenuKeys.Start, RowPrev + row),
                        Call(MenuKeys.Right, RowRight + row), Call(MenuKeys.Left, RowLeft + row), Call(MenuKeys.X, RowAlt + row)
                    }.Concat(removeKey ? new[] { Call(MenuKeys.Y, RowRemove + row) } : Array.Empty<MenuAction>()).Concat(deleteAll != null ? new[] { Call(MenuKeys.Down, RowDown + row) } : Array.Empty<MenuAction>()).ToArray())).Index;
                page.AddItem(i => Clone(_p.Label, i, dx - labelGrow / 2 - valueGrow / 2, dy, stringId: label, wrapGrow: (int)labelGrow, greyed: GreyLabel));
                // a tap on the value box (what an action row has instead of arrows): a touch-only rect (kind 0, the
                // ROM's own "on arrive None" + rect) on the value text, not on the box -- a box with a rect would get
                // the host's focus frame. The box as drawn is x 134..190 y 147..163 (-fecanvas FE_TRIS), the value
                // item sits at (163, 151); the touch arrows come later in the list, so they win where they overlap it.
                float wider = (valueGrow - ValueGrow) / 2; // a wider box: a wider tap area
                page.AddItem(i => Clone(_p.Value, i, dx, dy, stringId: value, wrapGrow: (int)valueGrow, greyed: GreyValue,
                    actions: new[] { TouchRect(-31 - wider, -6, 29 + wider, 14, RowTap + row) }));
                page.AddItem(i => Clone(_p.Right, i, dx + valueGrow / 2, dy, actions: _p.Right.Actions.Select(a => WithCall(a, RowNext + row)).ToList()));
                page.AddItem(i => Clone(_p.Left, i, dx - valueGrow / 2, dy, actions: _p.Left.Actions.Select(a => WithCall(a, RowPrev + row)).ToList()));
                // the X box (RowModel.Delete): the value box's own orange outline narrowed to a 16 x 16 square, 4 px right
                // of it (SENSITIVITY's box is 32 wide), and an "X" in it with the tap -- on the text, as for the value. No
                // A action on either, so neither takes the D-pad's focus: the row's box answers X instead (RowAlt).
                float delDx = 16 + valueGrow / 2 + 4 + 8;
                result.Del[r] = page.AddItem(i => Clone(_p.Box, i, dx + delDx, dy, init: MenuState.Hidden, stretchX: -16)).Index;
                result.DelX[r] = page.AddItem(i => Clone(_p.Value, i, dx + delDx, dy, stringId: delString, init: MenuState.Hidden,
                    actions: new[] { TouchRect(-10, -7, 10, 15, RowDelete + row) })).Index;
                result.Box[r] = box;
                result.Label[r] = label;
                result.Value[r] = value;
                result.Desc[r] = desc;
            }
            if (deleteAll != null && _p.EraseAll is MenuItem ea && _p.EraseAllText is MenuItem et)
            {
                // DELETE ALL (`deleteAll` = its description; RowsPage.DeleteAll): Options' ERASE ALL DATA button (touch x
                // 185..249 y 129..161) moved to the footer's right corner, where the game's other pages have SAVE
                // (MenuLayout's footer-right pins it to the canvas corner), and narrowed from 64 to 48 so a 4:3 canvas's
                // description band clears it: x 204..252 y 4..36, level with back's 4..36. Its A + touch action keeps the
                // ROM's rectangle (two corners), narrowed the same; its focused look stays the idle one (the ROM's hides the
                // button, and without a highlight of its own the host draws the focus frame round it).
                const float narrow = 16, edx = 3 + narrow / 2, edy = -125;
                int text = _strings.Add("DELETE ALL"), desc = _strings.Add(deleteAll);
                int descItem = page.AddItem(i => Clone(_p.Desc, i, 0, 0, stringId: desc, init: MenuState.Hidden)).Index;
                MenuAction Press(MenuAction a) => new()
                {
                    Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = (short)(a.RectX + narrow / 2),
                    RectY = a.RectY, RectW = (short)(a.RectW - narrow / 2), RectH = a.RectH,
                    Calls = new[] { (0, DeleteAllCall) }, TargetPage = 0xFF, Item = 0xFFFF
                };
                result.DelAll = page.AddItem(i => Clone(ea, i, edx, edy, init: MenuState.Hidden, steadyFocus: true, stretchX: -narrow,
                    links: new[] { new MenuLink(39, MenuState.Idle, descItem), new MenuLink(60, MenuState.Hidden, descItem) },
                    actions: ea.Actions.Select(Press).Append(Call(MenuKeys.Up, DeleteAllUp))
                        .Append(Call(MenuKeys.Down | MenuKeys.Left | MenuKeys.Right, DeleteAllStay)).ToList())).Index;
                result.DelAllText = page.AddItem(i => Clone(et, i, edx, edy, stringId: text, init: MenuState.Hidden)).Index;
            }
            if (empty != null)
            {
                int s = _strings.Add(empty);
                result.EmptyItem = page.AddItem(i => Clone(_p.OptDesc, i, 0, -130, stringId: s, init: MenuState.Hidden)).Index;
            }
            if (_p.LrIcons is MenuItem lr)
            {
                // the hunter license's L / R icons (16x16 quads centred x 29 / 172.7, y 60: -fecanvas 57 FE_TRIS) spread
                // to the rows panel's ends (centres x 14 / 242) on the header line (centre y -16; text y 176); shown
                // while there's more than one page, and a tap on either pages like L / R
                const float spread = 84, ldx = 27, ldy = -76;
                MenuAction Tap(float cx, int call) => TouchRect(cx - 11 - lr.X - ldx, 176 - 11 - lr.Y - ldy,
                    cx + 11 - lr.X - ldx, 176 + 11 - lr.Y - ldy, call);
                result.Pager = page.AddItem(i => Clone(lr, i, ldx, ldy, init: MenuState.Hidden, stretchX: spread,
                    actions: new[] { Tap(14, PagePrev), Tap(242, PageNext) })).Index;
            }
            return result;
        }

        // PAUSED: the Controls page's four control-type boxes, centred, as a list of four choices. chat: an online match's,
        // CHAT under RESUME (the lobby's CHAT page: OnlineMenus.Chat.cs), five boxes at the same spacing, centred on the old four
        private int BuildPausePage(MenuPage controls, MenuPage options, bool chat)
        {
            MenuPage page = _file.AddPage(index => new MenuPage
            {
                Index = index,
                Field0 = controls.Field0,
                Field14 = controls.Field14,
                Field16 = controls.Field16,
                Field18 = controls.Field18,
                Field1D = controls.Field1D,
                Field1E = controls.Field1E,
                Flags = controls.Flags,
                Items = Array.Empty<MenuItem>(),
                Actions = new[] { Call(MenuKeys.B | MenuKeys.Start, PauseResume) },
                Timers = Array.Empty<MenuTimer>()
            });
            // (five rows: the panel's top goes up a row and it grows back down to where it was; no PAUSED title there: it would
            // sit on the first row (higher is the top screen's strip), and an online match runs on under this menu anyway)
            int n = _p.ChoiceBoxes.Length;
            float step = n >= 2 ? _p.ChoiceBoxes[n - 1].Y - _p.ChoiceBoxes[n - 2].Y : 0; // (one box to the next)
            float up = chat ? -step : 0;
            foreach (MenuItem it in _p.Frame.Where(it => HasWidget(it, "blackdrop") || HasWidget(it, "topdrop"))) // backdrop, top band
            {
                bool panel = HasWidget(it, "blackdrop");
                page.AddItem(i => Clone(it, i, 0, panel ? up : 0, stretchDown: panel ? MathF.Abs(up) : 0));
            }
            if (!chat)
            {
                int title = _strings.Add("PAUSED");
                page.AddItem(i => Clone(_p.OptHeader, i, 0, 0, stringId: title));
            }
            var choices = new List<(string Text, int Call)>
            {
                ("resume", PauseResume), ("second hunt\nsettings", PauseSettings), ("mod\nsettings", PauseModSettings), ("quit to\nmain menu", PauseQuit)
            };
            if (chat) choices.Insert(1, ("chat", PauseChatCall));
            const float dx = 66; // the boxes span x 8..116 on the Controls page; centred on the touch screen
            float shift = up;
            for (int k = 0; k < choices.Count; k++)
            {
                int from = Math.Min(k, n - 1); // past the ROM's four: the last one's, a step further each
                MenuItem box = _p.ChoiceBoxes[from];
                float dy = (k - from) * step + shift;
                MenuAction? rect = box.Actions.FirstOrDefault(a => MenuEngine.TryRect(a, null, out _, out _, out _, out _));
                int call = choices[k].Call;
                page.AddItem(i => Clone(box, i, dx, dy, init: k == 0 ? MenuState.Focused : MenuState.Idle,
                    actions: new[] { rect != null ? CallRect(rect, MenuKeys.A, call) : Call(MenuKeys.A, call) }));
                int s = _strings.Add(choices[k].Text);
                // (a one-line label in a two-line row's place (CHAT): RESUME's text, centred in its box, a row further each)
                int textFrom = chat && !choices[k].Text.Contains('\n') ? 0 : from;
                float textDy = (k - textFrom) * step + shift;
                page.AddItem(i => Clone(_p.ChoiceTexts[textFrom], i, dx, textDy, stringId: s));
            }
            return page.Index;
        }

        // Options: MODS and RECOMP SETTINGS in SHOW MY STATS ON NINTENDOWIFI.COM's place (owner 2026-10-05; there's no
        // Nintendo WFC to show stats on), hidden as the page comes in, each a clone of its label over a clone of its box +
        // value -- the labels' last lines level with RUMBLE PAK's (y 76), the boxes on its row (y 55); and IMPORT GAMES'
        // own button (owner 2026-10-08: "make the options menu box bigger and add a dedicated importer button"): the
        // CONTROLS pill and label cloned, drawing ImportPillPicture's texture (DrawOptions). The panel is widened to
        // x -52..308 as REPLAYS' (MenuLayout.Options shows the wider touch screen) and its pieces go on a 4 x 2 grid:
        // AUDIO, CONTROLS, IMPORT GAMES, ERASE ALL DATA over CREDITS, RUMBLE PAK, MODS, RECOMP SETTINGS.
        private const float OptionsPanelStretchX = 97.6f, OptionsPanelDx = 1.1f;
        private static readonly float[] OptionsColumns = { -7, 83, 173, 263 }; // the grid's column centres
        // the ROM's pieces' centres (x, -fecanvas 21 FE_BOUNDS, both revs): the AUDIO / CONTROLS pills, the CREDITS
        // icon, the RUMBLE PAK box, the ERASE ALL DATA button
        private const float RomAudioX = 36, RomControlsX = 105, RomCreditsX = 33.4f, RomRumbleX = 106, RomEraseX = 217;
        private int _importPill = -1;

        // the Options page has the widened panel (MenuLayout.For)
        public static bool OptionsWidened { get; private set; }

        private void AddOptionsButtons(MenuPage options)
        {
            MenuItem box = _p.OptBox, label = _p.OptLabel, value = _p.OptValue;
            HiddenOn(OptionsPage).AddRange(new[] { box.Index, label.Index, value.Index });
            void Button(string text, string valueText, float cx, float labelY, int call, out int valueString)
            {
                float dx = cx - value.X;
                int labelString = _strings.Add(text);
                valueString = _strings.Add(valueText);
                int vs = valueString;
                options.AddItem(i => Clone(box, i, dx, 0, actions: new[] { CallRect(box.Actions[0], MenuKeys.A | MenuKeys.Start, call) }));
                options.AddItem(i => Clone(label, i, dx, labelY - label.Y, stringId: labelString));
                options.AddItem(i => Clone(value, i, dx, 0, stringId: vs));
            }

            // the game's pieces, by what they are (never by index: rev 1 inserts items)
            MenuItem? Widget(string name) => options.Items.FirstOrDefault(it => HasWidget(it, name));
            MenuItem? Text(string text) => options.Items.FirstOrDefault(it => it.IsText && it.States[0].Text is MenuTextStyle t
                && string.Equals(_strings[t.StringId], text, StringComparison.OrdinalIgnoreCase));
            MenuItem? audio = Widget("audio"), audioText = Text("audio"), controls = Widget("controls"), controlsText = Text("controls"),
                credits = Widget("credits"), creditsText = Text("credits"), divider = Widget("divider"), panel = Widget("blackdrop"),
                rumbleText = Text("rumble\npak"), rumbleValue = options.Items.FirstOrDefault(it => TextAt(it, 106, 55)), // ("rumb")
                rumble = options.Items.FirstOrDefault(it => HasWidget(it, "box_arrows") && HasCall(it, 48));
            MenuItem?[] pieces = { audio, audioText, controls, controlsText, credits, creditsText, divider, panel, rumbleText, rumbleValue, rumble,
                _p.EraseAll, _p.EraseAllText };
            if (pieces.Any(it => it == null))
            {
                // not the expected page: the vanilla places, MODS and RECOMP SETTINGS in the stats row's, no IMPORT GAMES
                Button("mods", "", 175, 76, OpenMods, out _modsValueString);
                Button("second hunt\nsettings", "open",225, 84, OpenSettings, out _);
                UpdateModsCount();
                return;
            }
            void Move(MenuItem it, float dx, float stretchX = 0)
                => options.ReplaceItem(it.Index, i => Clone(it, i, dx, 0, links: it.Links, actions: it.Actions, stretchX: stretchX));
            Move(panel!, OptionsPanelDx, OptionsPanelStretchX);
            float[] c = OptionsColumns;
            foreach (MenuItem it in new[] { audio!, audioText! }) Move(it, c[0] - RomAudioX);
            foreach (MenuItem it in new[] { controls!, controlsText! }) Move(it, c[1] - RomControlsX);
            foreach (MenuItem it in new[] { _p.EraseAll!, _p.EraseAllText!, divider! }) Move(it, c[3] - RomEraseX);
            foreach (MenuItem it in new[] { credits!, creditsText! }) Move(it, c[0] - RomCreditsX);
            foreach (MenuItem it in new[] { rumble!, rumbleText!, rumbleValue! }) Move(it, c[1] - RomRumbleX);

            // IMPORT GAMES: the CONTROLS pill and label in the third column, A or a tap opening IMPORT GAMES the way
            // CONTROLS opens its page (the page change is the action's own, so it makes CONTROLS' sound: the copy's Index is
            // CONTROLS' entry in the page's sound list, MenuSounds); OpenImport only notes where back goes
            float importDx = c[2] - RomControlsX;
            MenuItem pill = controls!, pillText = controlsText!;
            int importPage = ImportPage;
            _importPill = options.Items.Count;
            options.AddItem(_ => Clone(pill, pill.Index, importDx, 0, links: pill.Links,
                actions: pill.Actions.Select(a => WithCall(a, OpenImport, importPage)).ToList()));
            int importString = _strings.Add("import games");
            options.AddItem(i => Clone(pillText, i, importDx, 0, stringId: importString));

            // MODS and RECOMP SETTINGS under IMPORT GAMES and ERASE ALL DATA (a button's box is centred 1 left of its cx)
            Button("mods", "", c[2] + 1, 76, OpenMods, out _modsValueString);
            Button("second hunt\nsettings", "open",c[3] + 1, 84, OpenSettings, out _);
            UpdateModsCount();
            AddRulesLink(options); // the privacy / code of conduct / 13+ line (RecompMenus.OnlineRules.cs)
            OptionsWidened = true;
        }

        // The recomp pages' sounds, borrowed from the ROM pages they're built from (MenuSounds): every rows page's back
        // button sounds like the Controls page's back arrow (a tap on it was silent: owner 2026-10-08, "the back button
        // from the importer doesnt make any noise"); RECOMP SETTINGS' copy of CONTROLS sounds like CONTROLS, item for item.
        public void ApplySounds(MenuSounds sounds)
        {
            if (_modsRows == null) return; // not installed
            int backItem = _p.Frame.Length + 1; // (BuildRowsPage's)
            foreach (RowsPage? rows in new[] { _modsRows, _modSettingRows, _importRows, _importGameRows })
            {
                if (rows != null) sounds.ItemLike(rows.Page, backItem, ControlsPage, _p.Back.Index);
            }
            if (_controlsCopyPage >= 0) sounds.PageLike(_controlsCopyPage, ControlsPage);
        }

        // Options' IMPORT GAMES pill: the cloned CONTROLS pill keeps its triangles, UVs and focus animation, and draws
        // ImportPillPicture's texture (made from the pill it was cloned from) instead of the CONTROLS picture
        public void DrawOptions(MenuEngine menu, List<WidgetTri> tris)
        {
            DrawRulesLink(menu, tris); // (RecompMenus.OnlineRules.cs)
            if (_importPill < 0 || menu.Page?.Index != OptionsPage) return;
            UiTextureCache textures = menu.Widgets.Textures;
            for (int t = 0; t < tris.Count; t++)
            {
                if (tris[t].Item != _importPill || tris[t].TextureId < 0) continue;
                WidgetTri tri = tris[t];
                UiTexture pill = textures[tri.TextureId];
                tri.TextureId = textures.GetOrAdd(ImportPillPicture.Key + "|" + pill.Key, () => ImportPillPicture.Draw(pill)).Id;
                tris[t] = tri;
            }
        }

        private List<int> HiddenOn(int page)
        {
            if (!_hiddenItems.TryGetValue(page, out List<int>? items)) _hiddenItems[page] = items = new List<int>();
            return items;
        }

        // no SAVE on the options pages (owner 2026-10-05: settings save themselves): each page's SAVE button, its
        // highlight (the one with the callback) and its "save" text (x 207, y 15), hidden as the page comes in; B and
        // the back button leave as before
        private void HideSaveButtons()
        {
            foreach ((int page, int call) in new[] { (OptionsPage, OptionsSave), (AudioOptions.Page, AudioSave), (ControlsPage, ControlsSave) })
            {
                if (page >= _file.Pages.Count) continue;
                HiddenOn(page).AddRange(_file.Pages[page].Items
                    .Where(it => HasWidget(it, "ok") || HasCall(it, call) || TextAt(it, 207, 15)).Select(it => it.Index));
            }
            // RECOMP SETTINGS' copy of CONTROLS has the same items
            if (_controlsCopyPage >= 0) HiddenOn(_controlsCopyPage).AddRange(HiddenOn(ControlsPage));
        }

        // OPTIONS > CONTROLS without the DS's four control types (owner 2026-10-05: stylus / dual mode mean nothing on a
        // controller; RecompSettings.ClassicControlsPage brings them back in dev builds for dual-screen testing). Ours is
        // made of the page's own pieces: the control customizer's button(s) -- clones of the first control-type box and
        // its label, each with its own description in the top band like the game's -- with that device's aiming rows
        // under it. On a PC two columns: CONTROLLER (SENSITIVITY / LOOK INVERT, the settings the ship's OPTIONS edits) and
        // KEYBOARD & MOUSE (the mouse's own), each row's label over its arrows (two rows side by side don't fit the
        // panel); on a device one column, the rows laid out as the game has them, centred. Items are found by what they
        // are (revisions shift the indices). RECOMP SETTINGS' CONTROLS opens a copy of the finished page (InstallControlsCopy).
        private void AddControlsButtons(MenuPage controls)
        {
            MenuItem box = _p.ChoiceBoxes[0], text = _p.ChoiceTexts[0];
            // the game's control-type items: the boxes (off / on looks), their labels, the DS pictures, the descriptions
            // the boxes show while focused
            foreach (MenuItem it in controls.Items)
            {
                bool choice = HasWidget(it, "selectAoff") || HasWidget(it, "selectAon")
                    || HasWidget(it, "stylusleft") || HasWidget(it, "stylusright") || HasWidget(it, "dmright") || HasWidget(it, "dmleft")
                    || (it.IsText && MathF.Abs(it.X - 63) < 1 && it.Y > 40);
                if (choice) _controlTypeItems.Add(it.Index);
                if (HasWidget(it, "selectAoff")) _controlTypeItems.AddRange(it.Links.Select(l => l.Target));
            }
            // the title (SELECT CONTROL TYPE) and SENSITIVITY's description (it names the stylus) say other things here
            MenuItem title = Find(controls, "Controls title", it => TextAt(it, 128, 172));
            _titleString = StringOf(title);
            _titleClassic = _strings[_titleString];
            int sensDesc = _p.Box.Links.Select(l => l.Target).FirstOrDefault(t => t >= 0 && t < controls.Items.Count && controls.Items[t].IsText, -1);
            _sensDescString = sensDesc >= 0 ? StringOf(controls.Items[sensDesc]) : -1;
            _sensDescClassic = _strings[_sensDescString];

            // SENSITIVITY / LOOK INVERT: the game's rows stay for classic mode (with our values on them: the ROM's "5" and
            // "OFF" are placeholders, and "OFF" is shared with other pages); ours are copies
            MenuItem sensValue = _p.Value, invertValue = Find(controls, "LOOK INVERT value", it => TextAt(it, 223, 53));
            MenuItem sensLabel = _p.Label, invertLabel = Find(controls, "LOOK INVERT label", it => TextAt(it, 154, 53));
            MenuItem[] arrowPairs = controls.Items.Where(it => HasWidget(it, "arrows_option")).OrderByDescending(it => it.Y).ToArray();
            MenuItem[] sensParts = controls.Items.Where(it => it == arrowPairs.ElementAtOrDefault(0) || HasCall(it, SensUp) || HasCall(it, SensDown)).ToArray();
            MenuItem[] invertParts = controls.Items.Where(it => it == arrowPairs.ElementAtOrDefault(1) || HasCall(it, InvertToggle)).ToArray();
            _romRowItems.AddRange(sensParts.Concat(invertParts).Append(sensLabel).Append(invertLabel).Select(it => it.Index));
            _romValueItems.Add(sensValue.Index);
            _romValueItems.Add(invertValue.Index);
            _sensValueString = _strings.Add("");
            _invertValueString = _strings.Add("");
            _mouseSensValueString = _strings.Add("");
            _classicValueItems.Add(controls.AddItem(i => Clone(sensValue, i, 0, 0, stringId: _sensValueString)).Index);
            _classicValueItems.Add(controls.AddItem(i => Clone(invertValue, i, 0, 0, stringId: _invertValueString)).Index);

            MenuAction? rect = box.Actions.FirstOrDefault(a => MenuEngine.TryRect(a, null, out _, out _, out _, out _));
            int Same(int call) => call;
            if (!RecompSettings.DesktopHost)
            {
                // one column, three rows 35 apart around the panel's centre (y ~95, where the RECOMP SETTINGS rows centre):
                // the button (the first box is centred at x 62, y ~143), then the game's two rows (x ~127..250, y ~76 / ~53)
                // moved left by RECOMP SETTINGS' -60
                Button(0, "customize\ncontrols", "choose which controller button does what.", OpenPadControls, 66, -13);
                Row(sensLabel, sensParts, sensValue, _sensValueString, -60, 19, -60, 19, Same, -1);
                Row(invertLabel, invertParts, invertValue, _invertValueString, -60, 7, -60, 7, Same, -1);
                return;
            }
            // two columns centred at x 64 / 192, each from the top: the button (centre y 142), SENSITIVITY's label (centre
            // 116) over its value box (centre 95), LOOK INVERT's (74 over 53) -- evenly down the panel (y 33..162). The
            // mouse has no LOOK INVERT (owner 2026-10-05), so its column is the button and SENSITIVITY, on the same lines
            // as the controller's. A text item's y is its line's bottom (8 px lines); a value box spans its value's y -4..+12.
            int mouseSensDesc = Description("how quickly you turn and aim with the mouse.");
            foreach ((float cx, bool mouse) in new[] { (64f, false), (192f, true) })
            {
                Button(mouse ? 1 : 0, mouse ? "keyboard\n& mouse" : "controller",
                    mouse ? "choose which key or mouse button does what." : "choose which controller button does what.",
                    mouse ? OpenKeyControls : OpenPadControls, cx - 62, -1);
                Func<int, int> calls = mouse ? c => c switch { SensUp => MouseSensUp, SensDown => MouseSensDown, _ => c } : Same;
                Row(sensLabel, sensParts, sensValue, mouse ? _mouseSensValueString : _sensValueString,
                    cx - sensLabel.X, 112 - sensLabel.Y, cx - sensValue.X, 91 - sensValue.Y, calls, mouse ? mouseSensDesc : -1);
                if (!mouse)
                {
                    Row(invertLabel, invertParts, invertValue, _invertValueString,
                        cx - invertLabel.X, 70 - invertLabel.Y, cx - invertValue.X, 49 - invertValue.Y, calls, -1);
                }
            }

            // a customizer button: a clone of the first control-type box at (dx, dy) from it, its label, its description
            void Button(int k, string label, string descText, int call, float dx, float dy)
            {
                int descString = _strings.Add(descText), labelString = _strings.Add(label);
                int descItem = controls.AddItem(i => Clone(_p.Desc, i, 0, 0, stringId: descString, init: MenuState.Hidden)).Index;
                int boxItem = controls.AddItem(i => Clone(box, i, dx, dy, init: MenuState.Idle,
                    links: new[] { new MenuLink(39, MenuState.Idle, descItem), new MenuLink(60, MenuState.Hidden, descItem) },
                    actions: new[] { rect != null ? CallRect(rect, MenuKeys.A, call) : Call(MenuKeys.A, call) })).Index;
                int textItem = controls.AddItem(i => Clone(text, i, dx, dy, stringId: labelString)).Index;
                _controlsButtonItems.AddRange(new[] { descItem, boxItem, textItem });
                if (k == 0) _firstControlsButton = boxItem;
            }

            // a row's copy: its label (dxl, dyl) from the game's, the rest -- arrows, box, touch arrows, value -- (dxv, dyv);
            // `call` maps the game's callbacks (the mouse's rows have their own); `desc` >= 0: the description its box shows
            void Row(MenuItem label, MenuItem[] parts, MenuItem value, int valueString, float dxl, float dyl, float dxv, float dyv,
                Func<int, int> call, int desc)
            {
                _centredRowItems.Add(controls.AddItem(i => Clone(label, i, dxl, dyl)).Index);
                foreach (MenuItem it in parts)
                {
                    var actions = it.Actions.Select(a => CopyAction(a, call)).ToList();
                    if (HasWidget(it, "box_arrows"))
                    {
                        // the D-pad goes by each focusable item's touch rectangle (text space) and the buttons have one,
                        // so the box gets one too, over its value box as drawn (x value -29..+27, y value -4..+12;
                        // -fecanvas FE_TRIS); a tap there does nothing, as on the game's page (the arrows step the value)
                        float vx = value.X + dxv, vy = value.Y + dyv, ix = it.X + dxv, iy = it.Y + dyv;
                        actions.Add(TouchRect(vx - 29 - ix, vy - 4 - iy, vx + 27 - ix, vy + 12 - iy, ControlsNoop));
                    }
                    var links = desc >= 0 ? it.Links.Select(l => l with { Target = desc }).ToList() : it.Links.ToList();
                    _centredRowItems.Add(controls.AddItem(i => Clone(it, i, dxv, dyv, actions: actions, links: links)).Index);
                }
                _centredRowItems.Add(controls.AddItem(i => Clone(value, i, dxv, dyv, stringId: valueString)).Index);
            }

            // a description of ours in the top band, shown while the box linking to it is focused
            int Description(string s)
            {
                int str = _strings.Add(s);
                return controls.AddItem(i => Clone(_p.Desc, i, 0, 0, stringId: str, init: MenuState.Hidden)).Index;
            }
        }

        // RECOMP SETTINGS' CONTROLS (owner 2026-10-05): a copy of the finished Controls page -- every item in the same
        // order, so the indices its actions and links name still hold -- whose back goes to RECOMP SETTINGS (SAVE too,
        // ControlsCall)
        private void InstallControlsCopy(MenuPage controls)
        {
            int back = SettingsPage;
            MenuAction Retarget(MenuAction a) => a.TargetPage == OptionsPage ? CopyAction(a, target: back) : a;
            MenuPage copy = _file.AddPage(index => new MenuPage
            {
                Index = index,
                Field0 = controls.Field0,
                Field14 = controls.Field14,
                Field16 = controls.Field16,
                Field18 = controls.Field18,
                Field1D = controls.Field1D,
                Field1E = controls.Field1E,
                Flags = controls.Flags,
                Items = Array.Empty<MenuItem>(),
                Actions = controls.Actions.Select(Retarget).ToArray(),
                Timers = controls.Timers
            });
            foreach (MenuItem it in controls.Items)
            {
                copy.AddItem(i => Clone(it, i, 0, 0, actions: it.Actions.Select(Retarget).ToList(), links: it.Links.ToList()));
            }
            _controlsCopyPage = copy.Index;
        }

        // an action with its callbacks mapped and / or another target page
        private static MenuAction CopyAction(MenuAction a, Func<int, int>? call = null, int target = -1) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
            RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = call == null ? a.Calls : a.Calls.Select(c => (c.A, call(c.B))).ToArray(),
            Item = a.Item, TargetPage = target >= 0 ? (byte)target : a.TargetPage, Field17 = a.Field17
        };

        private static int StringOf(MenuItem it) => it.States.Select(s => s.Text?.StringId ?? -1).FirstOrDefault(id => id >= 0, -1);

        private bool ClassicControls => _settings.ClassicControlsPage && !RecompSettings.PublicBuild;

        private bool IsControlsPage(int page) => page == ControlsPage || (page == _controlsCopyPage && page >= 0);

        // OPTIONS > CONTROLS (or its RECOMP SETTINGS copy) coming in: our look (or the game's in classic mode)
        private void EnterControls(MenuEngine menu)
        {
            bool classic = ClassicControls;
            ShowControlValues();
            foreach (int i in _romValueItems) menu.SetState(i, MenuState.Hidden);
            foreach (int i in classic ? _centredRowItems : _romRowItems.Concat(_classicValueItems).Concat(_controlTypeItems))
            {
                menu.SetState(i, MenuState.Hidden);
            }
            for (int k = 0; k < _controlsButtonItems.Count; k++)
            {
                bool desc = k % 3 == 0; // description, box, label per button: a description shows while its box is focused
                menu.SetState(_controlsButtonItems[k], classic || desc ? MenuState.Hidden : MenuState.Idle);
            }
            if (!classic && _firstControlsButton >= 0) menu.SetState(_firstControlsButton, MenuState.Focused);
            // (the ROM's own strings, so Fill; classic puts the game's text back)
            _strings.Fill(_titleString, classic ? _titleClassic : "ADJUST CONTROL OPTIONS");
            if (_sensDescString >= 0)
            {
                _strings.Fill(_sensDescString, classic ? _sensDescClassic : ControlHints.For("sensitivity"));
            }
        }

        private void ShowControlValues()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _strings.Set(_sensValueString, Math.Clamp(_settings.GameSensitivity, 1, 15).ToString(inv));
            _strings.Set(_invertValueString, _settings.GameLookInvert ? "ON" : "OFF");
            _strings.Set(_mouseSensValueString, MouseScale.Text(_settings.MouseSens));
        }

        // the Controls page's own callbacks (and the mouse row's copies of them): each change is saved at once (owner
        // 2026-10-05: no SAVE; vanilla kept the page's copy until SAVE and forgot it on B)
        private bool ControlsCall(MenuEngine menu, int call)
        {
            switch (call)
            {
            case SensUp:
            case SensDown:
                _settings.GameSensitivity = Math.Clamp(_settings.GameSensitivity + (call == SensUp ? 1 : -1), 1, 15);
                break;
            case InvertToggle:
                _settings.GameLookInvert = !_settings.GameLookInvert;
                break;
            case MouseSensUp:
            case MouseSensDown:
                _settings.MouseSens = MouseScale.Step(_settings.MouseSens, call == MouseSensUp ? 1 : -1); // the ship's slider has the same steps
                break;
            case ControlsSave: // hidden (HideSaveButtons); if a ROM still shows it, it just goes back
                menu.GoTo(menu.Page?.Index == _controlsCopyPage ? SettingsPage : OptionsPage);
                return true;
            default:
                return false;
            }
            _settings.Save(_settingsPath);
            ShowControlValues();
            return true;
        }

        // every menu frame
        public void Tick()
        {
            Import.Tick();
            TickReplays(); // REPLAYS: PLAY's launch waits for the file-start sound (RecompMenus.Replays.cs)
            TickOnline(); // ONLINE PLAY: the network's results and the server's pushes (OnlineMenus.cs)
            if (_captureId != null && Environment.TickCount64 - _captureStart > CaptureMs)
            {
                // nobody answered: nothing changes; buttons still held (a release the host never sent) are the answer
                if (_capturePressed.Count > 0) CommitCapture();
                else EndCapture();
            }
        }

        // ---- runtime ----

        private RowsPage? RowsFor(int page) => AllRowPages().FirstOrDefault(p => p.Page == page);

        public void OnPageEntered(MenuEngine menu, int page)
        {
            RowsPage? rows = RowsFor(page);
            if (rows != null)
            {
                rows.Offset = 0;
                rows.Entered?.Invoke();
                Refresh(rows);
                ShowRows(menu, rows);
            }
            EnterReplays(menu, page); // REPLAYS' list and details pages (RecompMenus.Replays.cs)
            EnterOnline(menu, page); // ONLINE PLAY's pages (OnlineMenus.cs)
            EnterImportCards(menu, page); // IMPORT GAMES' cards (RecompMenus.ImportCards.cs)
            EnterSections(menu, page); // CONTROLS (RecompMenus.Controls.cs)
            if (IsControlsPage(page) && _controlsButtonItems.Count > 0) EnterControls(menu);
            if (_hiddenItems.TryGetValue(page, out List<int>? hide))
            {
                foreach (int i in hide) menu.SetState(i, MenuState.Hidden);
            }
            if (page != s_controlsPage) _captureId = null;
            UpdateModsCount();
            _lastPage = page;
        }

        // Returns true when the call was ours. `inPause`: opened from the in-game pause menu.
        public bool OnCall(MenuEngine menu, int call, bool inPause)
        {
            if (menu.Page != null && IsControlsPage(menu.Page.Index) && _controlsButtonItems.Count > 0 && ControlsCall(menu, call)) return true;
            if (call < 1000) return false;
            _stepMenu = menu;
            _inPause = inPause;
            if (ReplayCall(menu, call)) return true; // REPLAYS (RecompMenus.Replays.cs)
            if (OnlineCall(menu, call)) return true; // ONLINE PLAY (OnlineMenus.cs)
            if (ImportCardsCall(menu, call)) return true; // IMPORT GAMES' cards (RecompMenus.ImportCards.cs)
            if (SectionCall(menu, call)) return true; // CONTROLS (RecompMenus.Controls.cs)
            switch (call)
            {
            case OpenPadControls:
            case OpenKeyControls:
                // (the old OPTIONS > CONTROLS buttons: CONTROLS on that device)
                _controlsFromHost = false;
                _controlsKeys = call == OpenKeyControls;
                if (s_controlsPage >= 0) menu.GoTo(s_controlsPage);
                return true;
            case ControlsNoop:
                return true;
            case OpenMods:
                menu.GoTo(ModsPage);
                return true;
            case OpenImport:
                _importBackPage = OptionsPage; // (the action itself goes to IMPORT GAMES)
                return true;
            case ImportBack:
                menu.GoTo(_importBackPage >= 0 ? _importBackPage : SettingsPage);
                return true;
            case OpenRulesCall:
                Requested?.Invoke(FrontendRequestKind.OnlineRules); // (RecompMenus.OnlineRules.cs)
                return true;
            case OpenSettings:
            case PauseSettings:
                menu.GoTo(SettingsPage);
                return true;
            case PauseModSettings:
                _modSettingsFilter = null;
                menu.GoTo(ModSettingsPage);
                return true;
            case PauseResume:
                Requested?.Invoke(FrontendRequestKind.Resume);
                return true;
            case PauseChatCall:
                if (PauseChat != null && s_onChat >= 0) menu.GoTo(s_onChat); // (OnlineMenus.Chat.cs)
                return true;
            case PauseQuit:
                // (asked first, in every pause menu; TRY CONTROLS' Start stays one press)
                if (Import.Popup == null) Requested?.Invoke(FrontendRequestKind.QuitToMenu);
                else Import.Popup.Ask("quit to the main menu?", yes =>
                {
                    if (yes) Requested?.Invoke(FrontendRequestKind.QuitToMenu);
                });
                return true;
            case DeleteAllCall:
                // its ask box; the page updates on Import.Changed
                if (menu.Page != null) RowsFor(menu.Page.Index)?.DeleteAll()?.Invoke();
                return true;
            case DeleteAllStay:
                return true;
            case DeleteAllUp:
                if (menu.Page != null && RowsFor(menu.Page.Index) is RowsPage up)
                {
                    int last = LiveRows(up).LastOrDefault(-1);
                    if (last >= 0) menu.SetState(up.Box[last], MenuState.Focused);
                }
                return true;
            case PageNext:
            case PagePrev:
                if (menu.Page != null && RowsFor(menu.Page.Index) is RowsPage p && PageCount(p) > 1)
                {
                    // wraps round (R on the last page -> the first), so both icons always do something
                    int pages = PageCount(p);
                    p.Offset = p.Starts[(PageIndex(p) + (call == PageNext ? 1 : pages - 1)) % pages];
                    Refresh(p);
                    ShowRows(menu, p);
                }
                return true;
            }
            int kind = call / 100 * 100;
            int row = call - kind;
            if (row < 0 || row >= RowsPerPage || menu.Page == null) return false;
            RowsPage? rows = RowsFor(menu.Page.Index);
            if (rows == null || row >= RowsShown(rows)) return true;
            if (kind == RowDown)
            {
                int next = LiveRows(rows).FirstOrDefault(r => r > row, -1);
                if (next >= 0) menu.SetState(rows.Box[next], MenuState.Focused);
                else if (rows.DelAll >= 0 && menu.ItemState(rows.DelAll) != MenuState.Hidden) menu.SetState(rows.DelAll, MenuState.Focused);
                return true;
            }
            RowModel model = rows.Rows[rows.Offset + row];
            if (model.Disabled?.Invoke() == true) return true; // greyed: nothing to do
            // the X box, or X on a row without an Alt: the row's Delete (its ask box; the page updates on Import.Changed)
            if (kind == RowDelete || (kind == RowAlt && model.Alt == null))
            {
                model.Delete?.Invoke();
                return true;
            }
            if (kind == RowRemove)
            {
                model.Remove?.Invoke();
                Refresh(rows);
                ShowDisabled(menu, rows);
                return true;
            }
            if (kind == RowAlt)
            {
                model.Alt();
                if (rows == _modsRows) menu.GoTo(ModSettingsPage); // a mod's own settings
                else Refresh(rows);
                return true;
            }
            if (kind == RowTap && !model.Action) return true; // a value row's box: the touch arrows step it
            if (kind is RowRight or RowLeft && model.Action) return true; // no arrows: the D-pad doesn't run it
            int count = rows.Rows.Count;
            model.Step(kind is RowPrev or RowLeft ? -1 : 1);
            _settings.Save(_settingsPath);
            Refresh(rows);
            if (rows.Rows.Count != count) ShowRows(menu, rows); // DEVELOPER on/off adds/removes the dev tools row
            else
            {
                ShowDisabled(menu, rows);
                // a row that greys itself (a reset, at the default now): the focus goes up to the live row above (what it reset)
                int up = model.Disabled?.Invoke() == true ? LiveRows(rows).LastOrDefault(r => r < row, -1) : -1;
                if (up >= 0) menu.SetState(rows.Box[up], MenuState.Focused);
            }
            UpdateModsCount();
            return true;
        }

        // rows past the end of the list are hidden (the box and the items cloned around it), and so are an action row's
        // arrows (the drawn pair before its box, the two touch arrows after its value); the empty text shows when there
        // are no rows at all, the L / R icons when there's more than one page
        private static void ShowRows(MenuEngine menu, RowsPage p)
        {
            int shown = RowsShown(p);
            for (int r = 0; r < RowsPerPage; r++)
            {
                int box = p.Box[r];
                bool action = r < shown && p.Rows[p.Offset + r].Action;
                for (int k = box - 1; k <= box + 4; k++)
                {
                    if (k == box && r < shown && menu.ItemState(k) != MenuState.Hidden) continue; // keep it (maybe focused)
                    bool arrow = k == box - 1 || k >= box + 3;
                    // always through SetState: it also marks a not-yet-started item as placed, so the page's own start
                    // doesn't bring a hidden row back
                    menu.SetState(k, r < shown && !(arrow && action)
                        ? (k == box && r == 0 ? MenuState.Focused : MenuState.Idle) : MenuState.Hidden);
                }
                if (r >= shown) menu.SetState(box - 2, MenuState.Hidden); // its description
            }
            if (p.EmptyItem >= 0) menu.SetState(p.EmptyItem, p.Rows.Count == 0 ? MenuState.Idle : MenuState.Hidden);
            if (p.Pager >= 0) menu.SetState(p.Pager, PageCount(p) > 1 ? MenuState.Idle : MenuState.Hidden);
            ShowDisabled(menu, p);
        }

        // the rumble row's State5 text colours (Options, page 21), for rows the cloned SENSITIVITY row greys
        private const uint GreyLabel = 0x1F101317, GreyValue = 0x1F0E0E0E;

        // a disabled row's box, label and value in their State5 (greyed) look, and back once it isn't; when the first row
        // is greyed the focus moves to the first one that isn't; the X boxes of the rows that have one; DELETE ALL
        private static void ShowDisabled(MenuEngine menu, RowsPage p)
        {
            int shown = RowsShown(p);
            int firstOn = -1;
            for (int r = 0; r < shown; r++)
            {
                int box = p.Box[r];
                bool off = p.Rows[p.Offset + r].Disabled?.Invoke() ?? false;
                if (!off && firstOn < 0) firstOn = r;
                for (int k = box; k <= box + 2; k++)
                {
                    if (off) menu.SetState(k, MenuState.State5);
                    else if (menu.ItemState(k) == MenuState.State5) menu.SetState(k, MenuState.Idle);
                }
            }
            if (firstOn > 0 && !Enumerable.Range(0, shown).Any(r => menu.ItemState(p.Box[r]) == MenuState.Focused))
            {
                menu.SetState(p.Box[firstOn], MenuState.Focused);
            }
            // the X boxes: beside a shown row with something to delete (always through SetState, as in ShowRows)
            for (int r = 0; r < p.Del.Length; r++)
            {
                RowModel? m = r < shown ? p.Rows[p.Offset + r] : null;
                MenuState x = m?.Delete != null && m.Disabled?.Invoke() != true ? MenuState.Idle : MenuState.Hidden;
                menu.SetState(p.Del[r], x);
                menu.SetState(p.DelX[r], x);
            }
            // DELETE ALL while there's something to delete (a focused button stays focused; hidden, its focus goes to the
            // first live row)
            if (p.DelAll >= 0)
            {
                bool on = p.DeleteAll() != null, focused = menu.ItemState(p.DelAll) == MenuState.Focused;
                if (!on || menu.ItemState(p.DelAll) == MenuState.Hidden) menu.SetState(p.DelAll, on ? MenuState.Idle : MenuState.Hidden);
                menu.SetState(p.DelAllText, on ? MenuState.Idle : MenuState.Hidden);
                if (!on) menu.SetState(p.DelAll - 1, MenuState.Hidden); // its description
                if (!on && focused && firstOn >= 0) menu.SetState(p.Box[firstOn], MenuState.Focused);
            }
        }

        private static int PageCount(RowsPage p) => p.Starts.Count;
        private static int PageIndex(RowsPage p) => Math.Max(0, p.Starts.IndexOf(p.Offset));

        // how many rows this page shows: up to six, up to the next page's start (a new section)
        private static int RowsShown(RowsPage p)
        {
            int i = PageIndex(p);
            int end = i + 1 < p.Starts.Count ? p.Starts[i + 1] : p.Rows.Count;
            return Math.Max(0, Math.Min(RowsPerPage, end - p.Offset));
        }

        // the pages' starts for the list as it is now; the offset onto the start of the page it was in
        private static void PageStarts(RowsPage p)
        {
            p.Starts.Clear();
            p.Starts.Add(0);
            for (int i = 1; i < p.Rows.Count; i++)
            {
                if (i - p.Starts[^1] >= RowsPerPage || p.Rows[i].Section != p.Rows[i - 1].Section) p.Starts.Add(i);
            }
            if (!p.Starts.Contains(p.Offset)) p.Offset = p.Starts.LastOrDefault(s => s <= p.Offset);
        }

        // the shown rows that aren't greyed, top to bottom
        private static IEnumerable<int> LiveRows(RowsPage p) => Enumerable.Range(0, RowsShown(p))
            .Where(r => p.Rows[p.Offset + r].Disabled?.Invoke() != true);

        private void Refresh(RowsPage p)
        {
            p.Rows = p.Source();
            PageStarts(p);
            int pages = PageCount(p), shown = RowsShown(p);
            string? section = shown > 0 ? p.Rows[p.Offset].Section : null;
            string title = section != null ? $"{p.Title} - {section}" : p.Title;
            if (p.Header >= 0) _strings.Set(p.Header, pages > 1 ? $"{title} {PageIndex(p) + 1}/{pages}" : title);
            for (int r = 0; r < RowsPerPage; r++)
            {
                RowModel? m = r < shown ? p.Rows[p.Offset + r] : null;
                _strings.Set(p.Label[r], m?.Label ?? "");
                _strings.Set(p.Value[r], m?.Value() ?? "");
                _strings.Set(p.Desc[r], m?.Hint ?? "");
            }
        }

        private void UpdateModsCount()
        {
            if (_modsValueString < 0) return;
            int on = _mods.Count(m => IsModEnabled(m.Manifest.Id));
            _strings.Set(_modsValueString, $"{on}/{_mods.Count}");
        }

        // ---- cloning the game's items ----

        internal static MenuAction Call(MenuKeys keys, int call) => new() { Kind = (ushort)keys, Calls = new[] { (0, call) }, TargetPage = 0xFF, Item = 0xFFFF };

        // a touch-only button (kind 0 with a rectangle, two corners in text coordinates relative to its item)
        internal static MenuAction TouchRect(float x0, float y0, float x1, float y1, int call) => new()
        {
            Kind = 0, RectX = (short)MathF.Round(x0), RectY = (short)MathF.Round(y0), RectW = (short)MathF.Round(x1),
            RectH = (short)MathF.Round(y1), Calls = new[] { (0, call) }, TargetPage = 0xFF, Item = 0xFFFF
        };

        // the same touch rectangle, our keys and callback, no page change or item state
        private static MenuAction CallRect(MenuAction a, MenuKeys keys, int call) => new()
        {
            Kind = (ushort)keys, Field4 = a.Field4, RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = new[] { (0, call) }, TargetPage = 0xFF, Item = 0xFFFF
        };

        // `page` (>= 0): the action also goes there, after the callback (MenuEngine.Run)
        internal static MenuAction WithCall(MenuAction a, int call, int page = -1) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
            RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = new[] { (0, call) }, TargetPage = page >= 0 ? (byte)page : (byte)0xFF, Item = 0xFFFF
        };

        private static MenuAction WithTarget(MenuAction a, int page) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
            RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = Array.Empty<(int, int)>(), TargetPage = (byte)page, Item = 0xFFFF
        };

        // stretchX / stretchDown: resize the widget (MenuItem.StretchX / StretchDown); wrapGrow: a wider text wrap
        // greyed: a source without a State5 look gets one copied from its Idle look, in that colour for a text (the rumble
        // row's: label 1F101317, value 1F0E0E0E; a widget keeps its Idle frame, as the rumble row's box does)
        // steadyFocus: the Focused look and every transition to or from Focused are the Idle look
        internal static MenuItem Clone(MenuItem src, int index, float dx, float dy, int stringId = -1, MenuState? init = null,
            IReadOnlyList<MenuLink>? links = null, IReadOnlyList<MenuAction>? actions = null, float stretchX = 0,
            float stretchDown = 0, int wrapGrow = 0, uint? greyed = null, bool steadyFocus = false)
        {
            var states = new List<MenuItemState>();
            foreach (MenuItemState s in src.States)
            {
                if (s.Text != null && stringId >= 0)
                {
                    MenuTextStyle t = s.Text;
                    states.Add(new MenuItemState
                    {
                        Code = s.Code, Kind = s.Kind, Field2 = s.Field2,
                        Text = new MenuTextStyle
                        {
                            StringId = stringId, StringField = t.StringField, StartColor = t.StartColor, EndColor = t.EndColor,
                            WrapWidth = (ushort)(t.WrapWidth + wrapGrow), Duration = t.Duration, Size = t.Size, Format1 = t.Format1,
                            Format2 = t.Format2, Align = t.Align, Flags = t.Flags, Field15 = t.Field15, Field16 = t.Field16
                        }
                    });
                }
                else
                {
                    states.Add(s);
                }
            }
            if (greyed is uint grey && states.All(s => s.Code != (int)MenuState.State5)
                && states.FirstOrDefault(s => s.Code == (int)MenuState.Idle) is MenuItemState idle)
            {
                MenuTextStyle? t = idle.Text;
                states.Add(new MenuItemState
                {
                    Code = (int)MenuState.State5, Kind = idle.Kind, Field2 = idle.Field2, WidgetIndex = idle.WidgetIndex, WidgetFlags = idle.WidgetFlags,
                    Text = t == null ? null : new MenuTextStyle
                    {
                        StringId = t.StringId, StringField = t.StringField, StartColor = grey, EndColor = 0x1F1F1F1F,
                        WrapWidth = t.WrapWidth, Duration = 0, Size = t.Size, Format1 = t.Format1,
                        Format2 = t.Format2, Align = t.Align, Flags = t.Flags, Field15 = t.Field15, Field16 = t.Field16
                    }
                });
            }
            if (steadyFocus && states.FirstOrDefault(s => s.Code == (int)MenuState.Idle) is MenuItemState still)
            {
                const int f = (int)MenuState.Focused; // transitions are to * 8 + from
                for (int k = 0; k < states.Count; k++)
                {
                    int c = states[k].Code;
                    if (c != f && !(c >= 8 && (c / 8 == f || c % 8 == f))) continue;
                    states[k] = new MenuItemState
                    {
                        Code = c, Kind = still.Kind, Field2 = still.Field2, WidgetIndex = still.WidgetIndex,
                        WidgetFlags = still.WidgetFlags, Text = still.Text
                    };
                }
            }
            return new MenuItem
            {
                Index = index,
                Delay = src.Delay,
                Field18 = src.Field18,
                Field1C = src.Field1C,
                X = src.X + dx,
                Y = src.Y + dy,
                StretchX = src.StretchX + stretchX,
                StretchDown = src.StretchDown + stretchDown,
                Depth = src.Depth,
                Field30 = src.Field30,
                Field34 = init.HasValue ? (byte)init.Value : src.Field34,
                Field35 = init.HasValue ? (byte)init.Value : src.Field35,
                Flags = src.Flags,
                Field39 = src.Field39,
                Field3A = src.Field3A,
                States = states,
                Links = links ?? Array.Empty<MenuLink>(),
                Actions = actions ?? Array.Empty<MenuAction>()
            };
        }
    }
}
