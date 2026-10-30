using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.Online;
using MphRecomp.Online.Voice;

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
        private SectionPage? _gamePage, _lookPage, _soundPage, _fhPage, _appPage, _devPage;
        // ONLINE SETTINGS as a section page (thread D 10-24, M8b, owner: "convert the online settings menu to the look of the
        // other settings tabs and make a tab for it in the second hunt settings but also keep the page where it is now"): the
        // ONLINE tab here, and ONLINE PLAY's own page (its hub's SETTINGS, no tabs, back to the hub: OnlineMenus.Settings.cs)
        private SectionPage? _onlinePage, _onlineHubPage;
        private readonly List<SectionPage> _settingSections = new();

        // each section page's headers and the rows under them (SettingRows' labels); a row no page names (the music rows)
        // goes with the row above it
        // (owner 2026-10-28: GAME in two sub-tabs like CONTROLS, CAMPAIGN = SAMUS through MAP, MULTIPLAYER = MULTIPLAYER
        // through STREAMING)
        private static readonly (string Header, string[] Labels)[] CampaignSections =
        {
            ("SAMUS", new[] { "samus\nsuit", "import\ngames", "camera" }),
            ("TROOPER", new[] { "campaign trooper\ntype" }), // owner 10-28: the campaign Trooper's type, once he has a file
            ("MAP", new[] { "map\nlook", "map\nmode" }),
            ("SAVES", new[] { "import/export\nds save" }), // the one DS SAVE box row (RecompMenus.DsSave.cs)
        };
        private static readonly (string Header, string[] Labels)[] MultiplayerSections =
        {
            ("MULTIPLAYER", new[] { "match\ncamera", "unlocks", "record\nreplays" }),
            ("STREAMING", new[] { "show ip\naddress", "show\nname", "show friend\ncode" }), // owner 10-28
        };
        // (owner 2026-10-21: a FIRST HUNT tab when it's imported, for that game's own settings; vanilla by default, each
        // piece swappable for Second Hunt's)
        private static readonly (string Header, string[] Labels)[] FirstHuntSections =
        {
            ("PLAY", new[] { "extras" }),
            ("PIECES", new[] { "first hunt\npieces", "fh menu\nmusic", "fh training\nmusic", "fh match\nmusic", "fh\nobjects" }),
            ("MENUS", new[] { "fh menu\nscreens" }),
        };
        private static readonly (string Header, string[] Labels)[] LookSections =
        {
            ("MENUS", new[] { "menu\nart" }),
            ("HUD", new[] { "campaign\nradar", "radar\npickups", "hud", "hud\nsway", "shot\nindicator", "helmet\nopacity", "visor\nopacity", "readout\nopacity", "lock readouts\nto visor", "line\nart" }),
            ("VIEW", new[] { "field of\nview" }),
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
            ("STARTUP", new[] { "skip boot\nlogos", "skip\nintro" }), // owner 2026-10-29
            ("UPDATES", new[] { "update\nalerts", "updates" }),
            ("HELP", new[] { "crash\nlog", "share\nlog", DiscordLabel, "credits" }), // owner 10-27: Discord above credits
        };
        private static readonly (string Header, string[] Labels)[] DevSections =
        {
            ("TESTING", new[] { "controls\npage", "status\ntext" }),
            ("TOOLS", new[] { "developer", "dev\ntools" }),
        };
        // the rows placed elsewhere: CONTROLS' gyro and touch sections, the page's own CONTROLS and IMPORT GAMES buttons
        private static readonly HashSet<string> PlacedRows = new(
            new[] { CampaignSections, MultiplayerSections, LookSections, SoundSections, FirstHuntSections, AppSections, DevSections }.SelectMany(s => s).SelectMany(s => s.Labels)
                .Concat(new[] { "controls", "reset\ngyro speed", "gyro\naim", "gyro\nspeed", "gyro\naxis", "gyro y\nspeed", "invert gyro\npitch", "invert gyro\nyaw", "gyro\ndeadband", "gyro\nsmoothing", "gyro\nacceleration", "imperialist\ngyro speed", "auto imp\ngyro", "touch\ncontrols", "touch\naim", "touch aim\nspeed", "touch aim\ny speed", "imperialist\ntouch speed", "auto imp\ntouch", "touch invert\ny", "touch invert\nx", "aim while\nfiring" }),
            StringComparer.Ordinal);

        // a row that runs something: a button with this text (the menu art row keeps its value: it opens a chooser)
        private static string? ButtonText(RowModel row) => !row.Action ? null : row.Label switch
        {
            "dev\ntools" => "dev tools",
            "extras" => "first hunt",
            "credits" => "credits",
            DiscordLabel => "", // the button draws Discord's logo instead (RecompMenus.Discord.cs)
            "crash\nlog" => RecompSettings.DesktopHost ? "open crash log" : "share crash log",
            "share\nlog" => RecompSettings.DesktopHost ? "save log" : "share log",
            "updates" => "check for updates",
            "song\nlist" => "song list",
            "import\ngames" => "import games",
            "export ds\nsave" => "export ds save",
            _ => null
        };

        private void InstallSettingsPages(Func<string, SectionPage> add)
        {
            _gamePage = add("GAME");
            _gamePage.Source = GameEntries;
            _lookPage = add("LOOK");
            _lookPage.Source = () => SettingsEntries(LookSections);
            _soundPage = add("SOUND");
            _soundPage.Source = () => SettingsEntries(SoundSections);
            _fhPage = add("FIRST HUNT");
            _fhPage.Source = () => SettingsEntries(FirstHuntSections);
            _appPage = add("APP");
            _appPage.Source = () => SettingsEntries(AppSections);
            _devPage = add("DEVELOPER");
            _devPage.Source = () => SettingsEntries(DevSections);
            _onlinePage = add("ONLINE");
            _onlinePage.Source = () => OnlineEntries(_onlinePage);
            foreach (SectionPage sp in new[] { _gamePage, _lookPage, _soundPage, _fhPage, _appPage, _devPage, _onlinePage })
            {
                sp.Tabs = () => true;
                _settingSections.Add(sp);
            }
            _onlineHubPage = add("ONLINE SETTINGS");
            _onlineHubPage.Source = () => OnlineEntries(_onlineHubPage);
            _settingSections.Add(_onlineHubPage); // (a settings page's look; Tabs stays off)
            _onlinePage.Entered = OnlineSectionEntered;
            _onlineHubPage.Entered = () =>
            {
                OnlineSectionEntered();
                if (_lastPage != s_onHub) return; // (back from the keyboard: where it was)
                _onlineHubPage.PageNo = 0;
                _onlineHubPage.Scroll = 0;
                _onlineHubPage.Focus = -1;
            };
        }

        // ONLINE SETTINGS' rows (both pages): the backup code hidden again, the friend code read from the identity file
        private void OnlineSectionEntered()
        {
            if (_lastPage == MultiplayerMenu.NamePage) return; // (back from typing a backup code)
            _stShowBackup = false;
            _stBackupText = null;
            _stCodeText = OnlineIdentity.Load(ProfileDir)?.FriendCode is string c ? FriendCodes.Format(c) : null;
            StreamerPrivacy.SetOwnCode(_stCodeText?.Replace("-", "")); // (STREAMING)
        }

        // ONLINE SETTINGS' rows under headers (what the old one-page list had, OnlineMenus.Settings.cs runs them); each
        // switch's hint the value it's on (SettingHints' "online.<label>.<on|off>")
        private List<Entry> OnlineEntries(SectionPage sp)
        {
            var e = new List<Entry>();
            void Header(string name) => e.Add(new Entry { Kind = EntryKind.Header, Label = name });
            void Row(string label, Func<string> value, Func<string> hint, int run, bool action)
            {
                var row = new RowModel
                {
                    Label = label,
                    Value = value,
                    Describe = hint,
                    Action = action,
                    Step = _ => { if (_stepMenu is { } m) OnlineRow(m, run, sp.Page); },
                };
                e.Add(new Entry { Kind = EntryKind.Setting, Label = label, Row = row });
            }
            void Toggle(string label, Func<bool> on, string own, int run) =>
                Row(label, () => on() ? "ON" : "OFF", () => SettingHints.Online(label, on() ? "on" : "off", own), run, false);
            static VoiceSettings V() => VoiceSettings.Current;
            Header("FINDING");
            Toggle("findable by name", () => OnlineSettings.Findable, "others can find you when they search your name.", StFindable);
            Header("VOICE AND CHAT");
            Toggle("voice chat", () => V().Mode != VoiceMode.Off, "talk and listen in online lobbies and matches.", StVoice);
            Row("push to talk", () => V().OnMode == VoiceMode.PushToTalk ? "ON" : "OFF",
                () => SettingHints.Online("push to talk", V().OnMode == VoiceMode.PushToTalk ? "on" : "off",
                        V().OnMode == VoiceMode.PushToTalk ? "hold r3 or tap talk to speak." : "off: open mic, your voice goes out when you speak.")
                    + (V().Mode == VoiceMode.Off ? " (voice chat is off)" : ""), StPtt, false);
            Toggle("chat", () => V().Chat, "see and send chat messages in online lobbies and matches.", StChat);
            Toggle("duck music", () => V().DuckMusic, "the music fades down while others talk, and comes back after.", StDuck);
            Header("GAME FILTERS");
            Toggle("no extra pickup games", () => OnlineSettings.NoExtraPickups, "don't list or join games with extra pickups (the pirate blaster).", StNoPickups);
            Toggle("no extra character games", () => OnlineSettings.NoExtraCharacters, "don't list or join games with the trooper or dark samus.", StNoCharacters);
            Toggle("no extra arena games", () => OnlineSettings.NoExtraArenas, "don't list, join or search games in imported arenas (echoes).", StNoArenas);
            Header("PROFILE");
            Row("your friend code", () => StreamerPrivacy.HideCode && (MeCard is { Code.Length: > 0 } || _stCodeText != null) ? StreamerPrivacy.CodeMask
                : MeCard is { Code.Length: > 0 } me ? FriendCodes.Format(me.Code) : _stCodeText ?? "--",
                () => "give it to a friend: enter each other's to be friends.", StCode, true);
            Row("backup code", () => _stShowBackup ? "HIDE" : "SHOW",
                () => _stShowBackup ? "your backup code: " + (_stBackupText ?? "") : "moves your online profile to another device. keep it secret.", StBackup, true);
            Row("restore from a backup code", () => "ENTER", () => "play as your hunter from another device.", StRestore, true);
            Row("about online play", () => "READ", () => "who sees what, and what the server keeps.", StAbout, true);
            Row("delete online profile", () => "DELETE", () => "the server forgets your name, friend code, friends and license."
                + (sp == _onlineHubPage ? "" : " (in online play's settings)"), StDelete, true);
            return e;
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
            if (_fhPage != null && SettingsEntries(FirstHuntSections).Count > 0) tabs.Add(_fhPage); // (First Hunt imported)
            if (_onlinePage != null) tabs.Add(_onlinePage);
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
            _tabNext = tabs[tab].Page;
            _tabMenu = menu;
        }

        // a tab switched to at once, as a sub-tab is (owner 2026-10-24: "as instant as navigating the sub tabs", not the
        // pages' play-out and fade): the next tick, not inside the press that asked (whose key would go on to the new
        // page's own actions), its items already in (Settle)
        private int _tabNext = -1;
        private MenuEngine? _tabMenu;

        private void TickTabs()
        {
            if (_tabNext < 0 || _tabMenu is not { } menu) return;
            int page = _tabNext;
            _tabNext = -1;
            _tabMenu = null;
            if (menu.PendingPage != -1 || SectionFor(menu.Page?.Index ?? -1) == null) return; // (left meanwhile)
            menu.Enter(page);
            menu.Settle();
        }

        // back: OPTIONS (the pause menu, paused: FrontendSession's Redirect); ONLINE PLAY's own page: its hub
        private void SettingsBack(MenuEngine menu, SectionPage sp) => menu.GoTo(sp == _onlineHubPage ? s_onHub : OptionsPage);

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
        // a PC: a controller with a gyroscope has been seen (its GYRO rows show then; SecondHunt.Desktop DesktopGyro)
        public static Func<bool> PadGyro { get; set; } = () => false;
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
            PadKey("l1", leftArrow - 2 - TabIconW, TabTop - 1, TabBottom + 1, tag);
            PadKey("r1", rightArrow + SecArrowW + 2, TabTop - 1, TabBottom + 1, tag);
        }

        // a controller button beside what it moves: a small dark key with its name
        private void PadKey(string name, float x0, float top, float bottom, int tag)
        {
            Fill(x0, top, x0 + TabIconW, bottom, 0, 0, 0, 0.6f, tag);
            Box(x0, top, x0 + TabIconW, bottom, tag);
            float size = Math.Clamp((TabIconW - 4) / MathF.Max(_drawMenu!.Font.Measure(name), 1), SizeX, SizeM);
            TextS(name, x0 + TabIconW / 2, top - (top - bottom - 8 * size) / 2, 2, Pale, tag, size);
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

        // GAME's entries: the CAMPAIGN / MULTIPLAYER strip (as CONTROLS' sub-tabs), then the chosen sub-tab's sections (a
        // sub-tab with no rows on this build and device goes; one left, no strip)
        private List<Entry> GameEntries()
        {
            SectionPage sp = _gamePage!;
            var subs = new List<(string Name, List<Entry> Rows)>();
            foreach ((string name, (string Header, string[] Labels)[] sections) in new[] { ("CAMPAIGN", CampaignSections), ("MULTIPLAYER", MultiplayerSections) })
            {
                List<Entry> rows = SettingsEntries(sections);
                if (rows.Count > 0) subs.Add((name, rows));
            }
            sp.SubNames = subs.Select(s => s.Name).ToArray();
            if (subs.Count == 0) return new List<Entry>();
            sp.SubTab = Math.Clamp(sp.SubTab, 0, subs.Count - 1);
            var e = new List<Entry>();
            if (subs.Count > 1) e.Add(new Entry { Kind = EntryKind.SubTabs, Label = subs[sp.SubTab].Name, Loose = true });
            e.AddRange(subs[sp.SubTab].Rows);
            return e;
        }

        // SettingRows as the section pages show them: GYRO SPEED with its own RESET (C6; the old RESET GYRO SPEED row
        // goes), each row's hint the option it's on (C17: ControlHints' "<label>.<value>", else "<label>", else the row's own)
        private List<RowModel> SectionRows()
        {
            List<RowModel> rows = SettingRows();
            rows.Add(DiscordRow()); // HELP's Discord invite (RecompMenus.Discord.cs)
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
                // SettingHints' (F2: every non-controls row) else ControlHints' (".public": a public build's own wording,
                // e.g. the hud without the mini map; ".desktop": the PC's); "{name}" the picked suit's or song's
                Func<string>? name = key switch
                {
                    "samussuit" => () => SuitLabel(ShownSuit()).ToLowerInvariant(),
                    "menuart" => () => SuitLabel(MenuArtChoice()).ToLowerInvariant(),
                    "matchmusic" => () => MusicWords(_settings.MatchMusic, "each arena's own"),
                    "menumusic" => () => MusicWords(_settings.MenuMusic, "the game's own"),
                    _ => null
                };
                row.Describe = () =>
                {
                    string v = value();
                    // a DS body as menu art isn't "samus in <suit>": the hunter's DS model, or the low-poly Dark Samus
                    if (key == "menuart" && StandinPictures.MenuArtLabel(MenuArtChoice()) is { } ds)
                    {
                        return (StandinPictures.MenuArtHunter(MenuArtChoice()) != null ? ds + "'s ds model" : "the " + ds)
                            .ToLowerInvariant() + " behind the menus.";
                    }
                    string h = SettingHints.For(SettingHints.All, key, v, RecompSettings.PublicBuild, RecompSettings.DesktopHost)
                        ?? SettingHints.For(ControlHints.All, key, v, RecompSettings.PublicBuild, RecompSettings.DesktopHost) ?? own;
                    return name != null ? h.Replace("{name}", name()) : h;
                };
            }
            return rows;
        }

        // F2's PC check (MphRead.Tools -hintdump): each row's section, label, and every value it steps through with that
        // value's hint ("section|label|value|hint"); steps the live settings, so only on a throwaway profile
        public List<string> HintDump()
        {
            var lines = new List<string>();
            string section = "";
            foreach (RowModel row in SectionRows())
            {
                if (row.Section != null) section = row.Section;
                string label = row.Label.Replace('\n', ' ');
                string first = row.Value();
                if (row.Action || first == "")
                {
                    lines.Add($"{section}|{label}||{row.Hint}");
                    continue;
                }
                for (int i = 0; i < 64; i++)
                {
                    lines.Add($"{section}|{label}|{row.Value()}|{row.Hint}");
                    row.Step(1);
                    if (row.Value() == first) break;
                }
            }
            return lines;
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
