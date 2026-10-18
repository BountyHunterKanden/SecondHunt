using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;

// SECOND HUNT SETTINGS as CONTROLS is (owner 2026-10-17: "reorganize the whole second hunt settings menu in the same
// way/style ... have each section of the settings that we are organizing, including controls now, be its own page"; then
// "i dont want the settings to open on a page selector i just want it to open on the first page", "change the pages to
// tabs", "tabs should be clickable/touchable too", L1 / R1 by the arrows with a controller): tabs GAME, CONTROLS, LOOK AND
// SOUND, APP, DEVELOPER over the rows, opening on GAME; each section page scrolls its rows under headers, the hint of the
// option the row is on under them (ControlHints.cs). The rows are SettingRows' (RecompMenus.cs), placed by their labels.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private SectionPage? _gamePage, _lookPage, _soundPage, _appPage, _devPage;
        private readonly List<SectionPage> _settingSections = new();

        // each section page's headers and the rows under them (SettingRows' labels); a row no page names (the music rows)
        // goes with the row above it
        private static readonly (string Header, string[] Labels)[] GameSections =
        {
            ("SAMUS", new[] { "samus\nsuit", "import\ngames", "camera" }),
            ("MAP", new[] { "map\nlook", "map\nmode" }),
            ("MULTIPLAYER", new[] { "match\ncamera", "unlocks", "record\nreplays" }),
            ("EXTRAS", new[] { "extras" }),
        };
        private static readonly (string Header, string[] Labels)[] LookSections =
        {
            ("MENUS", new[] { "menu\nscreens", "menu\nart", "smooth\nmenus" }),
            ("HUD", new[] { "hud", "line\nart" }),
            ("DISPLAY", new[] { "frame\nrate", "smooth\nframes" }),
        };
        // (owner 2026-10-17: "look and sound should be split. look should be menus/hud and sound should be its own tab")
        private static readonly (string Header, string[] Labels)[] SoundSections =
        {
            ("VOLUME", new[] { "sfx\nvolume", "music\nvolume" }),
            ("MUSIC", new[] { "music\nquality" }),
        };
        private static readonly (string Header, string[] Labels)[] AppSections =
        {
            ("UPDATES", new[] { "update\nalerts", "updates" }),
            ("HELP", new[] { "crash\nlog", "credits" }),
        };
        private static readonly (string Header, string[] Labels)[] DevSections =
        {
            ("TESTING", new[] { "controls\npage", "status\ntext" }),
            ("TOOLS", new[] { "developer", "dev\ntools" }),
        };
        // the rows placed elsewhere: CONTROLS' gyro and touch sections, the page's own CONTROLS and IMPORT GAMES buttons
        private static readonly HashSet<string> PlacedRows = new(
            new[] { GameSections, LookSections, SoundSections, AppSections, DevSections }.SelectMany(s => s).SelectMany(s => s.Labels)
                .Concat(new[] { "controls", "reset\ngyro speed", "gyro\naim", "gyro\nspeed", "gyro\naxis", "touch\ncontrols", "touch\naim" }),
            StringComparer.Ordinal);

        // a row that runs something: a button with this text (the menu art row keeps its value: it opens a chooser)
        private static string? ButtonText(RowModel row) => !row.Action ? null : row.Label switch
        {
            "dev\ntools" => "dev tools",
            "extras" => "first hunt",
            "credits" => "credits",
            "crash\nlog" => RecompSettings.DesktopHost ? "open crash log" : "share crash log",
            "updates" => "check for updates",
            "song\nlist" => "song list",
            "import\ngames" => "import games",
            _ => null
        };

        private void InstallSettingsPages(Func<string, SectionPage> add)
        {
            _gamePage = add("GAME");
            _gamePage.Source = () => SettingsEntries(GameSections);
            _lookPage = add("LOOK");
            _lookPage.Source = () => SettingsEntries(LookSections);
            _soundPage = add("SOUND");
            _soundPage.Source = () => SettingsEntries(SoundSections);
            _appPage = add("APP");
            _appPage.Source = () => SettingsEntries(AppSections);
            _devPage = add("DEVELOPER");
            _devPage.Source = () => SettingsEntries(DevSections);
            foreach (SectionPage sp in new[] { _gamePage, _lookPage, _soundPage, _appPage, _devPage })
            {
                sp.Tabs = () => true;
                _settingSections.Add(sp);
            }
        }

        // the section pages L / R go through, in the hub's order (CONTROLS unless it's the game's classic page; DEVELOPER
        // while it has rows)
        private List<SectionPage> SettingsTabs()
        {
            var tabs = new List<SectionPage>();
            if (_gamePage != null) tabs.Add(_gamePage);
            if (_ctl != null && !ClassicControls) tabs.Add(_ctl);
            if (_lookPage != null) tabs.Add(_lookPage);
            if (_soundPage != null) tabs.Add(_soundPage);
            if (_appPage != null) tabs.Add(_appPage);
            if (_devPage != null && SettingsEntries(DevSections).Count > 0) tabs.Add(_devPage);
            return tabs;
        }

        private void NextSection(MenuEngine menu, SectionPage sp, int dir)
        {
            List<SectionPage> tabs = SettingsTabs();
            int at = tabs.IndexOf(sp);
            if (at >= 0 && tabs.Count >= 2) GoToTab(menu, sp, (at + dir + tabs.Count) % tabs.Count);
        }

        private void GoToTab(MenuEngine menu, SectionPage sp, int tab)
        {
            List<SectionPage> tabs = SettingsTabs();
            if (tab < 0 || tab >= tabs.Count || tabs[tab] == sp) return;
            _captureId = null;
            ToggleSound(menu);
            menu.GoTo(tabs[tab].Page);
        }

        // back: OPTIONS (the pause menu, paused: FrontendSession's Redirect)
        private void SettingsBack(MenuEngine menu, SectionPage sp) => menu.GoTo(OptionsPage);

        // the tabs' places: each as wide as its name wants (its letters + a margin), across TabX0..TabX1
        private List<(SectionPage Page, float X0, float X1)> TabLayout()
        {
            List<SectionPage> tabs = SettingsTabs().Take(SecMaxTabs).ToList();
            var layout = new List<(SectionPage, float, float)>();
            float units = tabs.Sum(t => t.Name.Length + 4f), room = TabX1 - TabX0 - TabGap * (tabs.Count - 1), x = TabX0;
            foreach (SectionPage t in tabs)
            {
                float w = room * (t.Name.Length + 4f) / units;
                layout.Add((t, x, x + w));
                x += w + TabGap;
            }
            return layout;
        }

        // a controller is connected (the platform says; L1 / R1 then shown by the tabs' arrows), asked once a second
        public static Func<bool> PadConnected { get; set; } = () => false;
        private bool _padShown;
        private int _padAsked = -1;

        // the tabs over the panel: this section's lit, the others plain; an arrow at each end, L1 / R1 beside them with a pad
        private void DrawTabs(SectionPage sp)
        {
            if (++_padAsked % 60 == 0) _padShown = PadConnected();
            int tag = sp.TitleItem;
            foreach ((SectionPage page, float x0, float x1) in TabLayout())
            {
                bool on = page == sp;
                if (on) Fill(x0, TabTop, x1, TabBottom, FrameOrange.R, FrameOrange.G, FrameOrange.B, 0.55f, tag);
                else Fill(x0, TabTop, x1, TabBottom, 0, 0, 0, 0.43f, tag);
                Box(x0, TabTop, x1, TabBottom, tag);
                float room = x1 - x0 - 5, width = MathF.Max(_drawMenu!.Font.Measure(page.Name), 1);
                float size = Math.Clamp(room / width, SizeX * 0.7f, SizeM);
                int from = _out.Count;
                TextS(page.Name, (x0 + x1) / 2, TabTop - (TabTop - TabBottom - 8 * size) / 2, 2, on ? White : Orange, tag, size);
                if (!on) Dim(from, 0.75f);
            }
            float arrowTop = TabTop - (TabTop - TabBottom - SecArrowH) / 2;
            float leftArrow = TabX0 - TabArrowGap - SecArrowW, rightArrow = TabX1 + TabArrowGap;
            SmallArrow(false, leftArrow, arrowTop, sp.Prev);
            SmallArrow(true, rightArrow, arrowTop, sp.Next);
            if (!_padShown) return;
            // the shoulder buttons: a small dark key with its name
            void Key(string name, float x0)
            {
                Fill(x0, TabTop - 1, x0 + TabIconW, TabBottom + 1, 0, 0, 0, 0.6f, tag);
                Box(x0, TabTop - 1, x0 + TabIconW, TabBottom + 1, tag);
                float size = Math.Clamp((TabIconW - 4) / MathF.Max(_drawMenu!.Font.Measure(name), 1), SizeX, SizeM);
                TextS(name, x0 + TabIconW / 2, TabTop - 1 - (TabTop - TabBottom + 2 - 8 * size) / 2, 2, Pale, tag, size);
            }
            Key("l1", leftArrow - 2 - TabIconW);
            Key("r1", rightArrow + SecArrowW + 2);
        }

        // a section page's entries: each header with the rows it names (the ones this build and device have)
        private List<Entry> SettingsEntries((string Header, string[] Labels)[] sections)
        {
            List<RowModel> rows = SectionRows();
            var e = new List<Entry>();
            foreach ((string header, string[] labels) in sections)
            {
                var picked = new List<RowModel>();
                foreach (string label in labels)
                {
                    int i = rows.FindIndex(r => r.Label == label);
                    if (i < 0) continue;
                    picked.Add(rows[i]);
                    for (int j = i + 1; j < rows.Count && !PlacedRows.Contains(rows[j].Label); j++) picked.Add(rows[j]);
                }
                if (picked.Count == 0) continue;
                e.Add(new Entry { Kind = EntryKind.Header, Label = header });
                foreach (RowModel row in picked)
                {
                    e.Add(ButtonText(row) is string text
                        ? new Entry { Kind = EntryKind.Button, Label = text, Row = row }
                        : new Entry { Kind = EntryKind.Setting, Label = row.Label.Replace('\n', ' '), Row = row });
                }
            }
            return e;
        }

        // SettingRows as the section pages show them: GYRO SPEED with its own RESET (C6; the old RESET GYRO SPEED row
        // goes), each row's hint the option it's on (C17: ControlHints' "<label>.<value>", else "<label>", else the row's own)
        private List<RowModel> SectionRows()
        {
            List<RowModel> rows = SettingRows();
            if (rows.Find(r => r.Label == "gyro\nspeed") is { } speed)
            {
                speed.Reset = () => _settings.GyroSensitivity = DefaultSettings.GyroSensitivity;
                speed.AtDefault = () => _settings.GyroSensitivity == DefaultSettings.GyroSensitivity;
            }
            rows.RemoveAll(r => r.Label == "reset\ngyro speed");
            foreach (RowModel row in rows)
            {
                if (row.Describe != null) continue;
                string key = row.Label.Replace("\n", "").Replace(" ", ""), own = row.Description;
                Func<string> value = row.Value;
                // (".public": a public build's own wording, e.g. the hud without the mini map)
                row.Describe = () => RecompSettings.PublicBuild
                    && ControlHints.All.TryGetValue(key + "." + value().ToLowerInvariant() + ".public", out string? h) ? h
                    : ControlHints.All.TryGetValue(key + "." + value().ToLowerInvariant(), out h) ? h
                    : ControlHints.All.TryGetValue(key, out h) ? h : own;
            }
            return rows;
        }

        // a settings change made off the row (the menu art's chooser, UNLOCKS' yes): the page shown again
        private void RefreshSettingsPages()
        {
            if (_stepMenu is not { } menu || menu.Page == null || SectionFor(menu.Page.Index) is not { } sp) return;
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        // every section page (FrontendSession: the menu art chooser's layer over each)
        public IEnumerable<int> SectionMenuPages => SectionPages().Select(sp => sp.Page);
    }
}
