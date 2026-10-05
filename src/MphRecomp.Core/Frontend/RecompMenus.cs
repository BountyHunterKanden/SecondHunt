using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.Mods;

// The recomp's own screens, built INTO the game's menu graph out of the game's own menu pieces so they look and move
// exactly like the vanilla ones:
//   - Options (page 21) gains MODS and RECOMP boxes (clones of its SHOW MY STATS box) in a column to the right of the
//     touch screen's controls -- the combined layout has room there; the DS screens themselves are unchanged
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

        private readonly MenuFile _file;
        private readonly MenuStrings _strings;
        private readonly RecompSettings _settings;
        private readonly string? _settingsPath;
        private readonly List<ModEntry> _mods = new();

        public int SettingsPage => _settingsRows?.Page ?? -1;
        public int ModsPage => _modsRows?.Page ?? -1;
        public int ModSettingsPage => _modSettingRows?.Page ?? -1;
        public int PausePage { get; private set; } = -1;

        private RowsPage? _settingsRows, _modsRows, _modSettingRows;
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
            if (gunsRoot != null && System.IO.Directory.Exists(gunsRoot))
            {
                foreach (string d in System.IO.Directory.GetDirectories(gunsRoot))
                    if (System.IO.File.Exists(System.IO.Path.Combine(d, "gun.bin")) && !NotSuits.Contains(System.IO.Path.GetFileName(d)))
                        _suits.Add(System.IO.Path.GetFileName(d));
            }
            // (Campaign mode assembly) every other HD model on Samus's own DS rig, hd/<Id>/ with its .dae beside guns/:
            // Brawl's Samus, Dark Samus, Zero Suit, ... The campaign wears the suit's body (third person, morph ball) and
            // keeps MPH's gun for one with no exported gun. <Id>Rest / <Id>Tpose folders are other rigs of a listed model.
            string? hdRoot = settingsPath != null
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath) ?? "", "hd") : null;
            if (hdRoot != null && System.IO.Directory.Exists(hdRoot))
            {
                foreach (string d in System.IO.Directory.GetDirectories(hdRoot))
                {
                    string id = System.IO.Path.GetFileName(d);
                    if (_suits.Contains(id, StringComparer.OrdinalIgnoreCase) || id.EndsWith("Rest", StringComparison.OrdinalIgnoreCase)
                        || id.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase) || NotSuits.Contains(id)) continue;
                    if (!MphRecomp.Anim.TrophyRigs.TryHunterFor(id, out MphRead.Hunter rig) || rig != MphRead.Hunter.Samus) continue;
                    if (System.IO.Directory.EnumerateFiles(d, "*.dae").Any()) _suits.Add(id);
                }
            }
            _suits.Sort((x, y) => string.Compare(SuitLabel(x), SuitLabel(y), StringComparison.OrdinalIgnoreCase));
            _suits.Add("original"); // MPH's own DS gun, no HD
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

        // write the settings file now (OPTIONS > AUDIO's SAVE, AudioOptions)
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
                OptBox = Find(options, "SHOW MY STATS box", it => HasWidget(it, "box_arrows") && HasCall(it, 47)),
                OptLabel = Find(options, "RUMBLE PAK label", it => TextAt(it, 106, 84)),
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
                "no mods installed.\n\ncopy each mod's folder (with its mod.json) into\nandroid/data/" + RecompSettings.AppId + "/files/mods\nand come back here.");
            _modsRows.Source = ModRows;

            _modSettingRows = BuildRowsPage(controls, "MOD SETTINGS", -1, PagingActions(),
                "no settings.\n\nturn on a mod that has settings (see its mod.json) to change them here.");
            _modSettingRows.Source = ModSettingRows;

            PausePage = BuildPausePage(controls, options);
            AddOptionsButtons(options);
            foreach (RowsPage p in AllRowPages()) Refresh(p);
        }

        private IEnumerable<RowsPage> AllRowPages() => new[] { _settingsRows, _modsRows, _modSettingRows }.Where(p => p != null)!;

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
                    Label = "music",
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
                    Description = "if the app has crashed, share the newest crash report (it holds no personal data) with the developer.",
                    Value = () => "SHARE",
                    Step = _ => Requested?.Invoke(FrontendRequestKind.ShareCrashLog),
                    Action = true
                },
            };
            // a public build leaves out the rows for what it doesn't ship: imported HD suits, the HD menu art, imported
            // music and the developer tools
            if (RecompSettings.PublicBuild) rows.RemoveAll(r => HiddenInPublic.Contains(r.Label));
            if (!_settings.DeveloperMode) rows.RemoveAll(r => r.Label == "dev\ntools");
            return rows;
        }

        private static readonly HashSet<string> HiddenInPublic = new(StringComparer.Ordinal)
        {
            "music", "menu\nart", "developer", "dev\ntools", "samus\nsuit",
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
        // button and B return to (-1 = MODS); `empty` = text shown when the page has no rows.
        private RowsPage BuildRowsPage(MenuPage controls, string title, int back, IEnumerable<MenuAction> extraActions, string? empty)
        {
            int BackItem = _p.Frame.Length + 1; // after the frame items and the header
            int backPage = back == -1 ? ModsPage : back;
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
                Actions = new[] { new MenuAction { Kind = (ushort)MenuKeys.B, TargetPage = (byte)backPage, Item = (ushort)BackItem, Field17 = 4 } }
                    .Concat(extraActions).ToArray(),
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
                actions: backButton.Actions.Select(a => WithTarget(a, backPage)).ToList())).Index;
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
                page.AddItem(i => Clone(_p.Arrows, i, dx, dy, stretchX: ValueGrow));
                int row = r;
                // the first row starts focused, so its description shows and the D-pad has somewhere to start
                int box = page.AddItem(i => Clone(_p.Box, i, dx, dy, init: row == 0 ? MenuState.Focused : null, stretchX: ValueGrow,
                    links: new[] { new MenuLink(39, MenuState.Idle, descItem), new MenuLink(60, MenuState.Hidden, descItem) },
                    actions: new[]
                    {
                        Call(MenuKeys.A, RowNext + row), Call(MenuKeys.Start, RowPrev + row),
                        Call(MenuKeys.Right, RowRight + row), Call(MenuKeys.Left, RowLeft + row), Call(MenuKeys.X, RowAlt + row)
                    })).Index;
                page.AddItem(i => Clone(_p.Label, i, dx - ValueGrow / 2, dy, stringId: label));
                // a tap on the value box (what an action row has instead of arrows): a touch-only rect (kind 0, the
                // ROM's own "on arrive None" + rect) on the value text, not on the box -- a box with a rect would get
                // the host's focus frame. The box as drawn is x 134..190 y 147..163 (-fecanvas FE_TRIS), the value
                // item sits at (163, 151); the touch arrows come later in the list, so they win where they overlap it.
                page.AddItem(i => Clone(_p.Value, i, dx, dy, stringId: value, wrapGrow: (int)ValueGrow,
                    actions: new[] { TouchRect(-31, -6, 29, 14, RowTap + row) }));
                page.AddItem(i => Clone(_p.Right, i, dx + ValueGrow / 2, dy, actions: _p.Right.Actions.Select(a => WithCall(a, RowNext + row)).ToList()));
                page.AddItem(i => Clone(_p.Left, i, dx - ValueGrow / 2, dy, actions: _p.Left.Actions.Select(a => WithCall(a, RowPrev + row)).ToList()));
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

        // Options: MODS and RECOMP boxes, cloned from its SHOW MY STATS box + label + value, in the column right of the
        // touch screen (menu x 280..330; only the wide layouts show that column)
        private void AddOptionsButtons(MenuPage options)
        {
            MenuItem box = _p.OptBox, label = _p.OptLabel, value = _p.OptValue;
            void Button(string text, string valueText, float dy, int call, out int valueString)
            {
                const float dx = 100; // box 188..220 -> 288..320
                int labelString = _strings.Add(text);
                valueString = _strings.Add(valueText);
                int vs = valueString;
                options.AddItem(i => Clone(box, i, dx, dy, actions: new[] { CallRect(box.Actions[0], MenuKeys.A | MenuKeys.Start, call) }));
                options.AddItem(i => Clone(label, i, dx + 99, dy, stringId: labelString));
                options.AddItem(i => Clone(value, i, dx, dy, stringId: vs));
            }
            Button("mods", "", 46, OpenMods, out _modsValueString);
            Button("recomp\nsettings", "open", -8, OpenSettings, out _);
            UpdateModsCount();
        }

        // ---- runtime ----

        private RowsPage? RowsFor(int page) => AllRowPages().FirstOrDefault(p => p.Page == page);

        public void OnPageEntered(MenuEngine menu, int page)
        {
            RowsPage? rows = RowsFor(page);
            if (rows != null)
            {
                rows.Offset = 0;
                Refresh(rows);
                ShowRows(menu, rows);
            }
            UpdateModsCount();
        }

        // Returns true when the call was ours. `inPause`: opened from the in-game pause menu.
        public bool OnCall(MenuEngine menu, int call, bool inPause)
        {
            if (call < 1000) return false;
            switch (call)
            {
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
                menu.GoTo(ModSettingsPage);
                return true;
            }
            if (kind == RowTap && !model.Action) return true; // a value row's box: the touch arrows step it
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
