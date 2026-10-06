using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.Mods;

// The recomp's own screens, built INTO the game's menu graph out of the game's own menu pieces so they look and move
// exactly like the vanilla ones:
//   - Options (page 21): MODS and RECOMP SETTINGS in SHOW MY STATS ON NINTENDOWIFI.COM's place (clones of its label,
//     box and value; the stats row is hidden: there's no Nintendo WFC)
//   - no SAVE on Options, OPTIONS > AUDIO or OPTIONS > CONTROLS (owner 2026-10-05): every change is saved at once
//   - RECOMP SETTINGS, MODS and MOD SETTINGS: the Controls page's frame (backdrop, logo, back button) plus rows cloned
//     from its SENSITIVITY row (arrows, value box, label, value, touch arrows, a description in the top band while
//     focused); six rows a page, L / R page through more (the hunter license's L / R icons at the header's ends and
//     "n/m" after the title show it); rows that run something (credits, ...) have no arrows
//   - PAUSED (the in-game pause menu, see FrontendSession.OpenPause): the Controls page's control-type boxes as a list
//     -- resume, recomp settings, mod settings, quit to the main menu
// Everything new is appended (items at the end of a page, pages after the ROM's), so every ROM index stays valid.
// Values change with A / Start / D-pad left-right / the touch arrows and are saved at once. On the MODS page, X on a
// mod opens that mod's own settings (the ones it declares in its mod.json).
namespace MphRecomp.Frontend
{
    public sealed class RecompMenus
    {
        // our callback ids (the game's are below 1000)
        private const int OpenMods = 1000, OpenSettings = 1001;
        private const int PauseResume = 1010, PauseSettings = 1011, PauseModSettings = 1012, PauseQuit = 1013;
        // RowNext / RowPrev: A / Start and the touch arrows; RowRight / RowLeft: the D-pad (a value row steps, an
        // action row ignores it); RowTap: a tap on the value box (an action row runs; a value row ignores it)
        private const int RowNext = 1100, RowPrev = 1200, RowAlt = 1300, PageNext = 1400, PagePrev = 1401;
        private const int RowRight = 1500, RowLeft = 1600, RowTap = 1700;
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

        public int SettingsPage => _settingsRows?.Page ?? -1;
        public int ModsPage => _modsRows?.Page ?? -1;
        public int ModSettingsPage => _modSettingRows?.Page ?? -1;
        public int PausePage { get; private set; } = -1;

        private RowsPage? _settingsRows, _modsRows, _modSettingRows, _controlRows;
        // the control customizer: the keyboard & mouse (else the controller); opened by the game (the ship's OPTIONS),
        // so its back closes it; the function waiting for a button, as its first or (add) second, since when
        private bool _controlsKeys, _controlsFromHost, _captureAdd;
        private int _controlsBackPage = ControlsPage; // where the customizer's back goes (CONTROLS or RECOMP SETTINGS)
        private MenuEngine? _stepMenu; // the menu of the call running a row's Step
        private string? _captureId;
        private long _captureStart;
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

        private sealed class RowModel
        {
            public string Label = "";
            public string Description = "";
            public Func<string> Value = () => "";
            public Action<int> Step = _ => { };
            public Action? Alt; // X
            public bool Action; // runs something (credits, ...) instead of cycling a value: no arrows, D-pad ignored
        }

        // a page of up to six rows fed from a list that may be longer (L / R page through it)
        private sealed class RowsPage
        {
            public int Page;
            public int[] Box = Array.Empty<int>(), Label = Array.Empty<int>(), Value = Array.Empty<int>(), Desc = Array.Empty<int>();
            public int EmptyItem = -1;
            public string Title = "";
            public int Header = -1, Pager = -1; // the title's string; the L / R icons item (-1: the ROM has none)
            public Func<List<RowModel>> Source = () => new();
            public List<RowModel> Rows = new();
            public int Offset;
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
            // guns/ sits beside the settings file (both under the external files dir; see CampaignActivity, MainActivity)
            string? gunsRoot = settingsPath != null
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath) ?? "", "guns") : null;
            // an unreadable folder is skipped, not fatal: on Android, once the app may install its own updates (the "install
            // unknown apps" switch) it can't read folders it didn't create itself (adb-pushed hd/ and guns/; 2026-10-06)
            if (gunsRoot != null && System.IO.Directory.Exists(gunsRoot))
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
            ["Phazonsuit"] = "PHZ", ["MP2VariaSuit"] = "VAR", ["MP2DarkSuit"] = "DRK", ["MP2LightSuit"] = "LGT",
            ["MP3VariaSuit"] = "VAR", ["MP3PEDSuit"] = "PED", ["MP4DefaultSuit"] = "DEF", ["MP4PsychicSuit"] = "PSY",
            ["MP4PsychicGlove"] = "GLV", ["MP4ViolaSuit"] = "VLA", ["MP4ViolaSuitIC"] = "VIC", ["MP4LegacySuit"] = "LEG",
            ["MP2DarkSamus"] = "DSA", ["MP3DarkSamus"] = "DSA", ["MP3ZeroSuit"] = "ZER",
        };

        // left out of the suit row: HD models on Samus's rig the owner dropped (2026-10-01: "ditch mp3 zero suit for now"),
        // and guns/mp1, the first export of P1 PWR's gun (no body of its own: "P1 MP" played as MPH's Samus; the setting
        // reads it as MP1PowerSuit, see RecompSettings.SamusSuit)
        private static readonly HashSet<string> NotSuits = new(StringComparer.OrdinalIgnoreCase) { "MP3ZeroSuit", "mp1" };

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

        // the RECOMP SETTINGS row the cursor is on (its label, e.g. "music"), or null off that page: the pause menu plays
        // the paused music while it's on the music row, so a change is heard live (CampaignAudio.UpdateAudioPause)
        public string? FocusedSetting(MenuEngine menu)
        {
            RowsPage? p = _settingsRows;
            if (p == null || menu.Page?.Index != p.Page) return null;
            for (int r = 0; r < p.Box.Length; r++)
            {
                if (menu.ItemState(p.Box[r]) != MenuState.Focused) continue;
                int i = p.Offset + r;
                return i < p.Rows.Count ? p.Rows[i].Label : null;
            }
            return null;
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

            // 7 rows now that "samus suit" joined the other six -- L / R page through them like MODS / MOD SETTINGS
            _settingsRows = BuildRowsPage(controls, "RECOMP SETTINGS", OptionsPage, PagingActions(), null);
            _settingsRows.Source = SettingRows;

            _modsRows = BuildRowsPage(controls, "MANAGE MODS", OptionsPage, PagingActions(),
                "no mods installed.\n\ncopy each mod's folder (with its mod.json) into\n" + (RecompSettings.DesktopHost
                    ? "the mods folder in the game's data folder" : "android/data/" + RecompSettings.AppId + "/files/mods")
                    + "\nand come back here.");
            _modsRows.Source = ModRows;

            _modSettingRows = BuildRowsPage(controls, "MOD SETTINGS", -1, PagingActions(),
                "no settings.\n\nturn on a mod that has settings (see its mod.json) to change them here.");
            _modSettingRows.Source = ModSettingRows;

            PausePage = BuildPausePage(controls, options);
            AddOptionsButtons(options);

            // the control customizer (owner 2026-10-05): a function a row, A then the new button
            // (value boxes wide enough for two buttons on one line: "LCLICK / MCLICK" is 91 px, -fecanvas FE_MEASURE)
            _controlRows = BuildRowsPage(controls, "CONTROLLER", ControlsPage, PagingActions(), null, backCall: ControlsBack,
                valueGrow: 66);
            _controlRows.Source = ControlRows;
            AddControlsButtons(controls); // last: the pages above are cloned from the Controls page as the ROM has it
            InstallControlsCopy(controls);
            HideSaveButtons();
            foreach (RowsPage p in AllRowPages()) Refresh(p);
        }

        private IEnumerable<RowsPage> AllRowPages() => new[] { _settingsRows, _modsRows, _modSettingRows, _controlRows }.Where(p => p != null)!;

        private static IEnumerable<MenuAction> PagingActions()
            => new[] { Call(MenuKeys.R, PageNext), Call(MenuKeys.L, PagePrev) };

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
                    Description = "hd: samus behind the menus drawn from your own imported prime suit (echoes' varia first). original: the ds picture.",
                    Value = () => _settings.MenuArtHd ? "HD" : "ORIGINAL",
                    Step = _ => _settings.MenuArtHd = !_settings.MenuArtHd
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
                new()
                {
                    Label = "music",
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
                    Label = "dev\ntools",
                    Description = "the developer launcher: trophy viewers, rooms, animations, music a/b.",
                    Value = () => "OPEN",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.OpenDeveloperMenu),
                    Action = true
                },
                new()
                {
                    Label = "samus\nsuit",
                    Description = $"samus's suit in the campaign: {SuitLabel(_settings.SamusSuit).ToLowerInvariant()}. "
                        + "her model (third person and morph ball) and arm cannon; original is mph's own.",
                    Value = () => SuitValue(_settings.SamusSuit),
                    Step = s => _settings.SamusSuit = Next(_suits.ToArray(), _settings.SamusSuit, s)
                },
                new()
                {
                    Label = "camera",
                    Description = "first person is the game's own view. third person follows samus from behind, wearing her suit's body.",
                    Value = () => _settings.ThirdPerson ? "THIRD" : "FIRST",
                    Step = _ => _settings.ThirdPerson = !_settings.ThirdPerson
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
                // OPTIONS > CONTROLS' copy (owner 2026-10-05: "controls" here, the same page inside RECOMP SETTINGS)
                new()
                {
                    Label = "controls",
                    Description = "change which buttons do what, and how quickly you turn and aim.",
                    Value = () => "OPEN",
                    Step = _ =>
                    {
                        if (_controlsCopyPage >= 0) _stepMenu?.GoTo(_controlsCopyPage);
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
                // owner 2026-10-06: checked only when pressed, never on its own, and installed by the game; from the main
                // menu only, not a paused campaign or match (FrontendSession.CheckForUpdates)
                new()
                {
                    Label = "updates",
                    Description = "from the main menu: asks github.com for a newer second hunt and installs it. it goes online only when you press this.",
                    Value = () => "CHECK",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.CheckUpdates),
                    Action = true
                },
            };
            // a public build leaves out the rows for what it doesn't ship: imported HD suits, the HD menu art and the
            // developer tools (the music quality row stays: owner 2026-10-05, all four modes are the game's own music)
            if (RecompSettings.PublicBuild) rows.RemoveAll(r => HiddenInPublic.Contains(r.Label));
            if (RecompSettings.DesktopHost) rows.RemoveAll(r => HiddenOnDesktop.Contains(r.Label));
            if (!_settings.DeveloperMode) rows.RemoveAll(r => r.Label == "dev\ntools");
            return rows;
        }

        private static readonly HashSet<string> HiddenInPublic = new(StringComparer.Ordinal)
        {
            "menu\nart", "developer", "dev\ntools", "samus\nsuit", "controls\npage",
        };

        // a PC has no gyroscope, and its mouse is the pointer (no on-screen controls)
        private static readonly HashSet<string> HiddenOnDesktop = new(StringComparer.Ordinal)
        {
            "gyro\naim", "gyro\nspeed", "gyro\naxis", "touch\ncontrols",
        };


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

        // A page made from the Controls page's frame and six copies of its SENSITIVITY row. `back` = the page its back
        // button and B return to (-1 = MODS); `empty` = text shown when the page has no rows; `backCall` (>= 0): the back
        // button and B run that callback instead of going to `back` (it decides where back is); `valueGrow`: how much wider
        // than SENSITIVITY's the value boxes are.
        private RowsPage BuildRowsPage(MenuPage controls, string title, int back, IEnumerable<MenuAction> extraActions, string? empty,
            int backCall = -1, float valueGrow = ValueGrow)
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
            for (int r = 0; r < RowsPerPage; r++)
            {
                float dx = -60, dy = 74 - RowPitch * r;
                int label = _strings.Add(""), value = _strings.Add(""), desc = _strings.Add("");
                int descItem = page.AddItem(i => Clone(_p.Desc, i, 0, 0, stringId: desc, init: MenuState.Hidden)).Index;
                page.AddItem(i => Clone(_p.Arrows, i, dx, dy, stretchX: valueGrow));
                int row = r;
                // the first row starts focused, so its description shows and the D-pad has somewhere to start
                int box = page.AddItem(i => Clone(_p.Box, i, dx, dy, init: row == 0 ? MenuState.Focused : null, stretchX: valueGrow,
                    links: new[] { new MenuLink(39, MenuState.Idle, descItem), new MenuLink(60, MenuState.Hidden, descItem) },
                    actions: new[]
                    {
                        Call(MenuKeys.A, RowNext + row), Call(MenuKeys.Start, RowPrev + row),
                        Call(MenuKeys.Right, RowRight + row), Call(MenuKeys.Left, RowLeft + row), Call(MenuKeys.X, RowAlt + row)
                    })).Index;
                page.AddItem(i => Clone(_p.Label, i, dx - valueGrow / 2, dy, stringId: label));
                // a tap on the value box (what an action row has instead of arrows): a touch-only rect (kind 0, the
                // ROM's own "on arrive None" + rect) on the value text, not on the box -- a box with a rect would get
                // the host's focus frame. The box as drawn is x 134..190 y 147..163 (-fecanvas FE_TRIS), the value
                // item sits at (163, 151); the touch arrows come later in the list, so they win where they overlap it.
                float wider = (valueGrow - ValueGrow) / 2; // a wider box: a wider tap area
                page.AddItem(i => Clone(_p.Value, i, dx, dy, stringId: value, wrapGrow: (int)valueGrow,
                    actions: new[] { TouchRect(-31 - wider, -6, 29 + wider, 14, RowTap + row) }));
                page.AddItem(i => Clone(_p.Right, i, dx + valueGrow / 2, dy, actions: _p.Right.Actions.Select(a => WithCall(a, RowNext + row)).ToList()));
                page.AddItem(i => Clone(_p.Left, i, dx - valueGrow / 2, dy, actions: _p.Left.Actions.Select(a => WithCall(a, RowPrev + row)).ToList()));
                result.Box[r] = box;
                result.Label[r] = label;
                result.Value[r] = value;
                result.Desc[r] = desc;
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

        // PAUSED: the Controls page's four control-type boxes, centred, as a list of four choices
        private int BuildPausePage(MenuPage controls, MenuPage options)
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
            foreach (MenuItem it in _p.Frame.Where(it => HasWidget(it, "blackdrop") || HasWidget(it, "topdrop"))) // backdrop, top band
            {
                page.AddItem(i => Clone(it, i, 0, 0));
            }
            int title = _strings.Add("PAUSED");
            page.AddItem(i => Clone(_p.OptHeader, i, 0, 0, stringId: title));
            (string Text, int Call)[] choices =
            {
                ("resume", PauseResume), ("recomp\nsettings", PauseSettings), ("mod\nsettings", PauseModSettings), ("quit to\nmain menu", PauseQuit)
            };
            const float dx = 66; // the boxes span x 8..116 on the Controls page; centred on the touch screen
            for (int k = 0; k < choices.Length; k++)
            {
                MenuItem box = _p.ChoiceBoxes[k], text = _p.ChoiceTexts[k];
                MenuAction? rect = box.Actions.FirstOrDefault(a => MenuEngine.TryRect(a, null, out _, out _, out _, out _));
                int call = choices[k].Call;
                page.AddItem(i => Clone(box, i, dx, 0, init: k == 0 ? MenuState.Focused : MenuState.Idle,
                    actions: new[] { rect != null ? CallRect(rect, MenuKeys.A, call) : Call(MenuKeys.A, call) }));
                int s = _strings.Add(choices[k].Text);
                page.AddItem(i => Clone(text, i, dx, 0, stringId: s));
            }
            return page.Index;
        }

        // Options: MODS and RECOMP SETTINGS in SHOW MY STATS ON NINTENDOWIFI.COM's place (owner 2026-10-05; there's no
        // Nintendo WFC to show stats on), hidden as the page comes in: two columns in its spot (x 160..250), each a clone
        // of its label over a clone of its box + value -- the labels' last lines level with RUMBLE PAK's (y 76), the
        // boxes on its row (y 55)
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
            Button("mods", "", 175, 76, OpenMods, out _modsValueString);
            Button("recomp\nsettings", "open", 225, 84, OpenSettings, out _);
            UpdateModsCount();
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
                _strings.Fill(_sensDescString, classic ? _sensDescClassic : "how quickly you turn and aim with the right stick.");
            }
        }

        private void ShowControlValues()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _strings.Set(_sensValueString, Math.Clamp(_settings.GameSensitivity, 1, 15).ToString(inv));
            _strings.Set(_invertValueString, _settings.GameLookInvert ? "ON" : "OFF");
            _strings.Set(_mouseSensValueString, Math.Clamp(_settings.MouseSensitivity, 1, 15).ToString(inv));
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
                _settings.MouseSensitivity = Math.Clamp(_settings.MouseSensitivity + (call == MouseSensUp ? 1 : -1), 1, 15);
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

        // ---- the control customizer ----

        // a row a function (its buttons in the value box), then RESET TO DEFAULTS; A (or a tap) on a row waits for the new
        // button, X for a second one
        private List<RowModel> ControlRows()
        {
            bool keys = _controlsKeys;
            var rows = new List<RowModel>();
            foreach (ControlBinds.Function f in ControlBinds.Functions(keys))
            {
                string id = f.Id;
                rows.Add(new RowModel
                {
                    Label = f.Label,
                    Description = _captureId != id ? f.Description + " a: change. x: add a second button."
                        : (_captureAdd ? "press a second button for this. " : "press the new button for this. ")
                            + (keys ? "esc keeps it as it is." : "wait to keep it as it is."),
                    Value = () => _captureId == id ? "PRESS" : ControlBinds.Value(ControlBinds.Get(_settings, keys, id)),
                    Step = _ => BeginCapture(id, add: false),
                    Alt = () => BeginCapture(id, add: true),
                    Action = true
                });
            }
            rows.Add(new RowModel
            {
                Label = "reset to\ndefaults",
                Description = "puts every " + (keys ? "key and mouse button" : "button") + " back the way it started.",
                Value = () => ControlBinds.IsDefault(_settings, keys) ? "DONE" : "RESET",
                Step = _ => ControlBinds.Reset(_settings, keys),
                Action = true
            });
            return rows;
        }

        // the customizer is waiting for a button: "pad" (a controller's) or "keys" (a PC's keys and mouse buttons), else
        // null. The host sends its next press to CaptureInput instead of the menu.
        public string? CaptureDevice => _captureId == null ? null : _controlsKeys ? "keys" : "pad";

        private void BeginCapture(string id, bool add)
        {
            _captureId = id;
            _captureAdd = add;
            _captureStart = Environment.TickCount64;
            if (_controlRows != null) Refresh(_controlRows);
        }

        // the host's press while CaptureDevice is set, as a ControlBinds button name; a fixed button keeps it waiting
        public void CaptureInput(string button)
        {
            if (_captureId == null || !ControlBinds.Allowed(_controlsKeys, button)) return;
            ControlBinds.Assign(_settings, _controlsKeys, _captureId, button, _captureAdd);
            _settings.Save(_settingsPath);
            EndCapture();
        }

        public void CancelCapture()
        {
            if (_captureId != null) EndCapture();
        }

        private void EndCapture()
        {
            _captureId = null;
            if (_controlRows != null) Refresh(_controlRows);
        }

        // every menu frame
        public void Tick()
        {
            if (_captureId != null && Environment.TickCount64 - _captureStart > CaptureMs) EndCapture();
        }

        // the game opens the customizer itself (the ship's OPTIONS): its back closes it (FrontendRequestKind.ControlsDone)
        public void OpenControls(MenuEngine menu, bool keys)
        {
            if (_controlRows == null) return;
            _controlsFromHost = true;
            ShowControls(menu, keys);
        }

        private void ShowControls(MenuEngine menu, bool keys)
        {
            _controlsKeys = keys;
            _captureId = null;
            _controlRows!.Title = keys ? "KEYBOARD & MOUSE" : "CONTROLLER";
            if (_controlsFromHost) menu.Enter(_controlRows.Page);
            else menu.GoTo(_controlRows.Page);
        }

        // ---- runtime ----

        private RowsPage? RowsFor(int page) => AllRowPages().FirstOrDefault(p => p.Page == page);

        public void OnPageEntered(MenuEngine menu, int page)
        {
            RowsPage? rows = RowsFor(page);
            if (rows != null)
            {
                // back from the customizer to RECOMP SETTINGS: the same page of rows as before
                if (!(rows == _settingsRows && _lastPage == _controlRows?.Page)) rows.Offset = 0;
                Refresh(rows);
                ShowRows(menu, rows);
            }
            if (IsControlsPage(page) && _controlsButtonItems.Count > 0) EnterControls(menu);
            if (_hiddenItems.TryGetValue(page, out List<int>? hide))
            {
                foreach (int i in hide) menu.SetState(i, MenuState.Hidden);
            }
            if (page != _controlRows?.Page) _captureId = null;
            UpdateModsCount();
            _lastPage = page;
        }

        // Returns true when the call was ours. `inPause`: opened from the in-game pause menu.
        public bool OnCall(MenuEngine menu, int call, bool inPause)
        {
            if (menu.Page != null && IsControlsPage(menu.Page.Index) && _controlsButtonItems.Count > 0 && ControlsCall(menu, call)) return true;
            if (call < 1000) return false;
            _stepMenu = menu;
            switch (call)
            {
            case OpenPadControls:
            case OpenKeyControls:
                _controlsFromHost = false;
                _controlsBackPage = menu.Page?.Index ?? ControlsPage; // OPTIONS > CONTROLS or its RECOMP SETTINGS copy
                ShowControls(menu, call == OpenKeyControls);
                return true;
            case ControlsNoop:
                return true;
            case ControlsBack:
                _captureId = null;
                if (_controlsFromHost)
                {
                    _controlsFromHost = false;
                    Requested?.Invoke(FrontendRequestKind.ControlsDone);
                }
                else menu.GoTo(_controlsBackPage);
                return true;
            case OpenMods:
                menu.GoTo(ModsPage);
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
            case PauseQuit:
                Requested?.Invoke(FrontendRequestKind.QuitToMenu);
                return true;
            case PageNext:
            case PagePrev:
                if (menu.Page != null && RowsFor(menu.Page.Index) is RowsPage p && p.Rows.Count > RowsPerPage)
                {
                    // wraps round (R on the last page -> the first), so both icons always do something
                    int pages = PageCount(p);
                    p.Offset = (p.Offset / RowsPerPage + (call == PageNext ? 1 : pages - 1)) % pages * RowsPerPage;
                    Refresh(p);
                    ShowRows(menu, p);
                }
                return true;
            }
            int kind = call / 100 * 100;
            int row = call - kind;
            if (row < 0 || row >= RowsPerPage || menu.Page == null) return false;
            RowsPage? rows = RowsFor(menu.Page.Index);
            if (rows == null || rows.Offset + row >= rows.Rows.Count) return true;
            RowModel model = rows.Rows[rows.Offset + row];
            if (kind == RowAlt)
            {
                if (model.Alt == null) return true;
                model.Alt();
                if (rows == _modsRows) menu.GoTo(ModSettingsPage); // a mod's own settings
                else Refresh(rows);
                return true;
            }
            if (kind == RowTap && !model.Action) return true; // a value row's box: the touch arrows step it
            if (kind == RowPrev && rows == _controlRows) return true; // Start there is the pause menu's own key
            if (kind is RowRight or RowLeft && model.Action) return true; // no arrows: the D-pad doesn't run it
            int count = rows.Rows.Count;
            model.Step(kind is RowPrev or RowLeft ? -1 : 1);
            _settings.Save(_settingsPath);
            Refresh(rows);
            if (rows.Rows.Count != count) ShowRows(menu, rows); // DEVELOPER on/off adds/removes the dev tools row
            UpdateModsCount();
            return true;
        }

        // rows past the end of the list are hidden (the box and the items cloned around it), and so are an action row's
        // arrows (the drawn pair before its box, the two touch arrows after its value); the empty text shows when there
        // are no rows at all, the L / R icons when there's more than one page
        private static void ShowRows(MenuEngine menu, RowsPage p)
        {
            int shown = Math.Max(0, Math.Min(RowsPerPage, p.Rows.Count - p.Offset));
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
        }

        private static int PageCount(RowsPage p) => Math.Max(1, (p.Rows.Count + RowsPerPage - 1) / RowsPerPage);

        private void Refresh(RowsPage p)
        {
            p.Rows = p.Source();
            int pages = PageCount(p);
            if (p.Header >= 0) _strings.Set(p.Header, pages > 1 ? $"{p.Title} {p.Offset / RowsPerPage + 1}/{pages}" : p.Title);
            for (int r = 0; r < RowsPerPage; r++)
            {
                RowModel? m = p.Offset + r < p.Rows.Count ? p.Rows[p.Offset + r] : null;
                _strings.Set(p.Label[r], m?.Label ?? "");
                _strings.Set(p.Value[r], m?.Value() ?? "");
                _strings.Set(p.Desc[r], m?.Description ?? "");
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

        internal static MenuAction WithCall(MenuAction a, int call) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
            RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = new[] { (0, call) }, TargetPage = 0xFF, Item = 0xFFFF
        };

        private static MenuAction WithTarget(MenuAction a, int page) => new()
        {
            Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
            RectX = a.RectX, RectY = a.RectY, RectW = a.RectW, RectH = a.RectH,
            Calls = Array.Empty<(int, int)>(), TargetPage = (byte)page, Item = 0xFFFF
        };

        // stretchX / stretchDown: resize the widget (MenuItem.StretchX / StretchDown); wrapGrow: a wider text wrap
        internal static MenuItem Clone(MenuItem src, int index, float dx, float dy, int stringId = -1, MenuState? init = null,
            IReadOnlyList<MenuLink>? links = null, IReadOnlyList<MenuAction>? actions = null, float stretchX = 0,
            float stretchDown = 0, int wrapGrow = 0)
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
