using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRecomp.Config;

// CONTROLS (owner 2026-10-16/17): ONE controls menu in place of OPTIONS > CONTROLS, its RECOMP
// SETTINGS copy and the control customizer -- "organised in sections with breaks, more breathing room", no old/new
// switch (the dev-only CLASSIC controls page still shows the ROM's own). Sections: AIM (sensitivity, imperialist
// sensitivity + pair with aim, look invert), ZOOM, PRACTICE (TRY CONTROLS), the buttons (one function a row: label |
// PRIMARY | SECONDARY | CLEAR | RESET), the keyboard's WEAPON KEYS (C9: keyboard only), SCOUT DRONE, then RESET TO DEFAULTS. A
// sensitivity has a RESET beside its value (C6). A or a tap on a cell: PRIMARY / SECONDARY wait for the new button (or
// combo), CLEAR empties both, RESET puts that one function's own defaults back (owner 2026-10-26); a button another
// function has stays on it too, with a heads-up (C11). A PC lists one device at a time (its first row switches
// CONTROLLER / KEYBOARD & MOUSE).
// Built as REPLAYS' pages (RecompMenus.Replays.cs): the CREATE OR JOIN frame, input items with only a focused look (the
// lobby glow), everything else drawn by DrawControls; MenuLayout.Replays places it (in the pause menu too). The page is
// a "section page": one list of entries scrolled eight rows at a time (owner 2026-10-17: "nix the controls pages and make
// it one scrolling page"), the section's header kept at the top while its rows scroll under it, a bar at the right
// edge showing where the view is (a tap above or below its thumb scrolls a screen). The D-pad moves cell to cell itself
// (Up / Down a row, scrolling as it goes; Left / Right a cell, or a value's step); L / R go to the next settings section
// (SECOND HUNT SETTINGS' pages, RecompMenus.SettingsPages.cs); Y resets (or clears) the row.
// The ship's OPTIONS opens it on its own (OpenControls: its back closes it).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private enum EntryKind { Header, Setting, Bind, Button, Note, SubTabs } // SubTabs: the sub-tab strip, always the first entry

        private sealed class Entry
        {
            public EntryKind Kind;
            public string Label = "";
            public RowModel? Row; // Setting / Button
            public string? Bind; // Bind: the function's id
            public string Description = ""; // Bind: the function's; Note: its text
            public bool Loose; // not part of the section before it (the device row, RESET TO DEFAULTS)
            public bool Columns; // Header: its section has buttons (PRIMARY / SECONDARY over their cells)
        }

        // one sectioned page's items and state
        private sealed class SectionPage
        {
            public int Page = -1, Panel = -1, TitleItem = -1, TitleString = -1, Prev = -1, Next = -1, Back = -1;
            public int CallBase;
            public string Name = "";
            public readonly int[] Cell0 = Unset(SecSlots), Cell1 = Unset(SecSlots), Cell2 = Unset(SecSlots), Value = Unset(SecSlots),
                Clear = Unset(SecSlots), Button = Unset(SecSlots), ArrowL = Unset(SecSlots), ArrowR = Unset(SecSlots);
            public Func<List<Entry>> Source = () => new();
            public List<Entry> Entries = new();
            public List<int[]> Pages = new(); // the view's entries, slot by slot (one: the rows scrolled into it)
            public int PageNo, Focus = -1, Col; // (always 0); the focused entry and its cell column (0..3; 3 = the RESET column)
            public int Scroll; // the first entry in the view (its section's header is shown above it)
            public int ScrollUp = -1, ScrollDown = -1; // the bar's touch halves
            public Func<bool> Tabs = () => false; // the settings' tabs over the rows (L / R, the arrows or a tap: another section)
            public readonly int[] Tab = Unset(SecMaxTabs); // each tab's touch item
            public readonly MenuAction[][] TabRect = new MenuAction[SecMaxTabs][]; // (its touch rectangle, laid out by Rebuild)
            public int TabCount;
            // the value boxes' and the buttons' column (owner 2026-10-17: "lets get the odd one out lined up with the others"):
            // on a settings tab the fewer kind takes the other's place (Rebuild: AlignColumns)
            public float ValueX0 = SecC0X0, ValueX1 = SecC1X1, ButtonX0 = 128 - SecButtonW / 2, ButtonX1 = 128 + SecButtonW / 2;
            public Action? Entered; // runs as its page comes in (TRY CONTROLS' SELECT HUNTER coming back)
            // sub-tabs (thread D 10-24, M8, owner: "sub tabs in some tabs ... the controls page is getting long"): a strip of
            // names pinned over the rows, its Source gives the one picked
            public string[] SubNames = Array.Empty<string>();
            public int SubTab;
            public readonly int[] Sub = Unset(SecMaxSub); // each sub-tab's touch item (base + its place * 10 + CellSubTab)
            public readonly MenuAction[][] SubRect = new MenuAction[SecMaxSub][];
            public bool SubPad; // the chips' touch rects were laid with L2 / R2 at the ends (a controller connected)
        }

        private static int s_controlsPage = -1;
        private static readonly HashSet<int> s_sectionPages = new(); // CONTROLS and SECOND HUNT SETTINGS' pages
        public static bool IsSectionMenuPage(MenuPage page) => page.Index >= 0 && s_sectionPages.Contains(page.Index);
        public int ControlsMenuPage => s_controlsPage;
        private SectionPage? _ctl;
        private int _ctlBackPage = OptionsPage; // CONTROLS' back
        private bool _ctlInSettings; // CONTROLS opened as a SECOND HUNT SETTINGS tab (it shows the tabs)
        private int _captureSlot; // the cell waiting for a button (0 PRIMARY, 1 SECONDARY)
        private MenuSounds? _sectionSounds;
        private static readonly RecompSettings DefaultSettings = new();

        // our callbacks: CONTROLS 3000-3099, SECOND HUNT SETTINGS' sections 3100-3899 (RecompMenus.SettingsPages.cs: GAME .. DEVELOPER,
        // then ONLINE and ONLINE PLAY's own ONLINE SETTINGS);
        // a slot's cell: base + slot * 10 + the cell (CellPrimary.. CellArrowR); the page's keys: base + 90..
        private const int ControlsCallBase = 3000, SectionCallsEnd = 3899;
        private const int CellPrimary = 0, CellSecondary = 1, CellReset = 2, CellValue = 3, CellButton = 4, CellArrowL = 5, CellArrowR = 6;
        private const int CellClear = 8; // a button's CLEAR (its RESET is CellReset, in the settings' RESET column)
        private const int SecUp = 90, SecDown = 91, SecLeft = 92, SecRight = 93, SecScrollUp = 94, SecPrev = 95, SecNext = 96, SecBack = 97,
            SecReset = 98, SecScrollDown = 99;
        private const int SecSlots = 8, SecTab0 = 80, SecMaxTabs = 8; // a tab's call: base + SecTab0 + its place
        private const int SecSubPrev = 88, SecSubNext = 89; // L2 / R2 (Z / C): the previous / next sub-tab
        private const int SecMaxSub = 4, CellSubTab = 7; // a sub-tab's call: base + its place * 10 + CellSubTab

        // ---- geometry (menu units, Y up; REPLAYS' panel x -52..308 y -24.6..-170) ----
        private const float SecTop0 = -31, SecPitch = 14.4f, SecH = 10.8f, SecLabelX = -40;
        private const float SecC0X0 = 116, SecC0X1 = 176, SecC1X0 = 182, SecC1X1 = 242, SecC2X0 = 248, SecC2X1 = 298;
        // a button's row (owner 2026-10-26: "each individual control bind needs a reset button"): PRIMARY | SECONDARY | CLEAR
        // | RESET, CLEAR's right edge on the value boxes', RESET in the settings' RESET column
        private const float SecB0X0 = 64, SecB0X1 = 122, SecB1X0 = 128, SecB1X1 = 186, SecBClrX0 = 192, SecBClrX1 = 242;
        private const float SecButtonW = 124, SecArrowW = 5.6f, SecArrowH = 7.1f, SecDescTop = -147.5f, SecDescW = 338;
        private const float SecBarX0 = 302.5f, SecBarX1 = 304.5f; // the scroll bar, right of the CLEAR / RESET cells
        // the tabs (owner 2026-10-17: "change the pages to tabs", "tabs should be clickable/touchable too"): one row over the
        // panel, an arrow at each end with L1 / R1 beside it while a controller is connected
        private const float TabTop = -10.4f, TabBottom = -21.4f, TabX0 = -22, TabX1 = 278, TabGap = 3;
        private const float TabIconL0 = -52, TabIconR1 = 308, TabIconW = 15, TabArrowGap = 2.5f;
        private static float SlotTop(int slot) => SecTop0 - SecPitch * slot;

        private void InstallSectionMenus()
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
            _controlsKeys = RecompSettings.DesktopHost; // (a PC opens on its keyboard & mouse)
            _ctl = InstallSectionPage("CONTROLS", ControlsCallBase, frame, panel, backIcon, backButton, title, glow);
            _ctl.Source = ControlEntries;
            _ctl.Tabs = () => _ctlInSettings && SettingsPage >= 0 && !_controlsFromHost;
            s_controlsPage = _ctl.Page;
            // SECOND HUNT SETTINGS and its sections, as CONTROLS (RecompMenus.SettingsPages.cs)
            InstallSettingsPages(name => InstallSectionPage(name, _nextSectionCalls += 100, frame, panel, backIcon, backButton, title, glow));
            foreach (SectionPage sp in SectionPages()) s_sectionPages.Add(sp.Page);
        }

        private int _nextSectionCalls = ControlsCallBase;

        private SectionPage InstallSectionPage(string name, int callBase, MenuPage frame, MenuItem panel, MenuItem backIcon, MenuItem backButton,
            MenuItem title, MenuItemState glow)
        {
            var sp = new SectionPage { Name = name, CallBase = callBase };
            MenuPage page = _file.AddPage(index => new MenuPage
            {
                Index = index, Field0 = frame.Field0, Field14 = frame.Field14, Field16 = frame.Field16, Field18 = frame.Field18,
                Field1D = frame.Field1D, Field1E = frame.Field1E, Flags = frame.Flags, Items = Array.Empty<MenuItem>(),
                Actions = new[]
                {
                    Call(MenuKeys.B, callBase + SecBack), Call(MenuKeys.L, callBase + SecPrev), Call(MenuKeys.R, callBase + SecNext),
                    Call(MenuKeys.Y, callBase + SecReset),
                    Call(MenuKeys.L2, callBase + SecSubPrev), Call(MenuKeys.R2, callBase + SecSubNext),
                },
                Timers = Array.Empty<MenuTimer>()
            });
            sp.Page = page.Index;
            sp.Panel = page.AddItem(i => Clone(panel, i, PanelDx, 0, stretchX: PanelStretchX, stretchDown: PanelStretchDown)).Index;
            page.AddItem(i => Clone(backIcon, i, 0, 0));
            sp.Back = page.AddItem(i => Clone(backButton, i, 0, 0, actions: backButton.Actions.Select(a => WithCall(a, callBase + SecBack)).ToList())).Index;
            sp.TitleString = _strings.Add(name);
            sp.TitleItem = page.AddItem(i => Clone(title, i, 0, 0, stringId: sp.TitleString)).Index;

            // an input: no look until focused (the lobby glow, shaped round it by DrawControls); A or a tap runs `call`;
            // the D-pad and Y are its own keys (the page moves its focus itself, see SectionCall)
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
                    },
                    Call(MenuKeys.Up, callBase + SecUp), Call(MenuKeys.Down, callBase + SecDown),
                    Call(MenuKeys.Left, callBase + SecLeft), Call(MenuKeys.Right, callBase + SecRight),
                }
            };
            MenuItem Touch(int index, float x0, float yTop, float x1, float yBottom, int call) => new()
            {
                Index = index, Field34 = (byte)MenuState.Hidden, Field35 = (byte)MenuState.Hidden, States = Array.Empty<MenuItemState>(),
                Links = Array.Empty<MenuLink>(), Actions = new[] { TouchRect(x0, yBottom + 192, x1, yTop + 192, call) }
            };

            for (int s = 0; s < SecSlots; s++)
            {
                float top = SlotTop(s), bottom = top - SecH;
                int b = callBase + s * 10;
                sp.Cell0[s] = page.AddItem(i => Input(i, SecB0X0, top, SecB0X1, bottom, b + CellPrimary)).Index;
                sp.Cell1[s] = page.AddItem(i => Input(i, SecB1X0, top, SecB1X1, bottom, b + CellSecondary)).Index;
                sp.Clear[s] = page.AddItem(i => Input(i, SecBClrX0, top, SecBClrX1, bottom, b + CellClear)).Index;
                sp.Cell2[s] = page.AddItem(i => Input(i, SecC2X0, top, SecC2X1, bottom, b + CellReset)).Index;
                sp.Value[s] = page.AddItem(i => Input(i, SecC0X0, top, SecC1X1, bottom, b + CellValue)).Index;
                sp.Button[s] = page.AddItem(i => Input(i, 128 - SecButtonW / 2, top, 128 + SecButtonW / 2, bottom, b + CellButton)).Index;
            }
            // after the cells, so a tap on a value's arrow is the arrow's (the engine tries the last items first)
            for (int s = 0; s < SecSlots; s++)
            {
                float top = SlotTop(s), bottom = top - SecH;
                int b = callBase + s * 10;
                sp.ArrowL[s] = page.AddItem(i => Touch(i, SecC0X0 - 3, top + 1.5f, SecC0X0 + 14, bottom - 1.5f, b + CellArrowL)).Index;
                sp.ArrowR[s] = page.AddItem(i => Touch(i, SecC1X1 - 14, top + 1.5f, SecC1X1 + 3, bottom - 1.5f, b + CellArrowR)).Index;
            }
            float barTop = SlotTop(0) + 1, barBottom = SlotTop(SecSlots - 1) - SecH - 1, barMid = (barTop + barBottom) / 2;
            sp.ScrollUp = page.AddItem(i => Touch(i, SecBarX0 - 4, barTop, SecBarX1 + 4, barMid, callBase + SecScrollUp)).Index;
            sp.ScrollDown = page.AddItem(i => Touch(i, SecBarX0 - 4, barMid, SecBarX1 + 4, barBottom, callBase + SecScrollDown)).Index;
            sp.Prev = page.AddItem(i => Touch(i, TabIconL0, TabTop + 3, TabX0 - 1, TabBottom - 3, callBase + SecPrev)).Index;
            sp.Next = page.AddItem(i => Touch(i, TabX1 + 1, TabTop + 3, TabIconR1, TabBottom - 3, callBase + SecNext)).Index;
            for (int t = 0; t < SecMaxTabs; t++)
            {
                MenuAction[] rect = { TouchRect(0, 0, 0, 0, callBase + SecTab0 + t) };
                sp.TabRect[t] = rect;
                sp.Tab[t] = page.AddItem(i => new MenuItem
                {
                    Index = i, Field34 = (byte)MenuState.Hidden, Field35 = (byte)MenuState.Hidden, States = Array.Empty<MenuItemState>(),
                    Links = Array.Empty<MenuLink>(), Actions = rect
                }).Index;
            }
            // the sub-tabs' chips, last: a tap on one is its own (not the strip slot's value cell under it)
            for (int t = 0; t < SecMaxSub; t++)
            {
                MenuAction[] rect = { TouchRect(0, 0, 0, 0, callBase + t * 10 + CellSubTab) };
                sp.SubRect[t] = rect;
                sp.Sub[t] = page.AddItem(i => new MenuItem
                {
                    Index = i, Field34 = (byte)MenuState.Hidden, Field35 = (byte)MenuState.Hidden, States = Array.Empty<MenuItemState>(),
                    Links = Array.Empty<MenuLink>(), Actions = rect
                }).Index;
            }
            return sp;
        }

        private IEnumerable<SectionPage> SectionPages()
        {
            if (_ctl != null) yield return _ctl;
            foreach (SectionPage sp in _settingSections) yield return sp;
        }

        private SectionPage? SectionFor(int page) => SectionPages().FirstOrDefault(p => p.Page == page && page >= 0);

        // OPTIONS > CONTROLS (and its RECOMP SETTINGS copy) go to CONTROLS (FrontendSession's MenuEngine.Redirect), except
        // with the dev-only CLASSIC controls page
        public int Redirect(int page) => s_controlsPage >= 0 && !ClassicControls && (page == ControlsPage || page == _controlsCopyPage && page >= 0)
            ? s_controlsPage : page;

        // the game opens CONTROLS itself (the ship's OPTIONS): its back closes it (FrontendRequestKind.ControlsDone)
        public void OpenControls(MenuEngine menu, bool keys)
        {
            if (_ctl == null) return;
            _controlsFromHost = true;
            _controlsKeys = keys;
            _captureId = null;
            _ctl.PageNo = 0;
            _ctl.Scroll = 0;
            _ctl.Focus = -1;
            _ctl.SubTab = 0;
            menu.Enter(_ctl.Page);
        }

        // ---- CONTROLS' entries ----

        private List<Entry> ControlEntries()
        {
            bool keys = _controlsKeys;
            var inv = CultureInfo.InvariantCulture;
            RecompSettings d = DefaultSettings;
            var e = new List<Entry>();
            void Header(string label) => e.Add(new Entry { Kind = EntryKind.Header, Label = label });
            void Setting(RowModel row, bool loose = false) => e.Add(new Entry
            {
                Kind = row.Action && row.Check == null ? EntryKind.Button : EntryKind.Setting, Label = row.Label.Replace('\n', ' '), Row = row, Loose = loose
            });
            void Binds(IEnumerable<ControlBinds.Function> functions)
            {
                bool scan = false, zoom = false;
                foreach (ControlBinds.Function f in functions)
                {
                    e.Add(new Entry { Kind = EntryKind.Bind, Label = f.Label.Replace('\n', ' '), Bind = f.Id, Description = f.Description });
                    // (C25, owner 2026-10-17: a small hint after scan and zoom, that the two can share a button)
                    bool both = scan && zoom;
                    scan |= f.Id == ControlBinds.Scan;
                    zoom |= f.Id == ControlBinds.Zoom;
                    if (!both && scan && zoom) e.Add(new Entry { Kind = EntryKind.Note, Description = ControlHints.For("scanzoomnote") });
                    if (f.Id == ControlBinds.Chat) // (owner 2026-10-11: hold or toggle, the row under CHAT; MatchChat.cs)
                    {
                        Setting(new RowModel
                        {
                            Label = "chat mode",
                            Describe = () => ControlHints.For(_settings.ChatToggle ? "chatmode.toggle" : "chatmode.hold"),
                            Value = () => _settings.ChatToggle ? "TOGGLE" : "HOLD",
                            Step = _ => _settings.ChatToggle = !_settings.ChatToggle
                        });
                    }
                }
            }

            // the sub-tabs (thread D 10-24, M8): BUTTONS and AIM, GYRO and TOUCH where the device has them (a PC: GYRO
            // once a controller with a gyroscope has been seen, D6b; no TOUCH), each with its own reset of everything on it
            string[] gyroLabels = { "gyro\naim", "gyro\nspeed", "imperialist\ngyro speed", "auto imp\ngyro", "gyro\naxis", "gyro y\nspeed", "invert gyro\npitch", "invert gyro\nyaw", "gyro\ndeadband", "gyro\nsmoothing", "gyro\nacceleration" };
            List<RowModel> device = keys ? new List<RowModel>()
                : RecompSettings.DesktopHost ? (PadGyro() ? SectionRows().Where(r => gyroLabels.Contains(r.Label)).ToList() : new List<RowModel>())
                : SectionRows(); // (RecompMenus.SettingsPages.cs: gyro speed's RESET, the hints)
            RowModel? DeviceRow(string label) => device.FirstOrDefault(r => r.Label == label);
            string[] touchLabels = { "touch\ncontrols", "touch\naim", "touch aim\nspeed", "imperialist\ntouch speed", "auto imp\ntouch", "touch aim\ny speed", "touch invert\ny", "touch invert\nx", "aim while\nfiring" };
            // each imperialist speed sits right under the speed it follows, boxed with it while paired like the AIM tab's
            // (owner 2026-10-26: "the box that shows when its locked like for the other control schemes")
            if (DeviceRow("gyro\nspeed") is { } gyroSpeed) gyroSpeed.GroupWithNext = () => _settings.GyroZoomPaired;
            if (DeviceRow("touch aim\nspeed") is { } touchSpeed) touchSpeed.GroupWithNext = () => _settings.TouchZoomPaired;
            var names = new List<string> { "BUTTONS", "AIM" };
            if (gyroLabels.Any(l => DeviceRow(l) != null)) names.Add("GYRO");
            if (touchLabels.Any(l => DeviceRow(l) != null)) names.Add("TOUCH");
            SectionPage sp = _ctl!;
            sp.SubNames = names.ToArray();
            sp.SubTab = Math.Clamp(sp.SubTab, 0, names.Count - 1);
            string sub = names[sp.SubTab];
            e.Add(new Entry { Kind = EntryKind.SubTabs, Label = sub, Loose = true });

            // a PC: the controller's or the keyboard & mouse's
            if (RecompSettings.DesktopHost)
            {
                Setting(new RowModel
                {
                    Label = "controls for",
                    Describe = () => ControlHints.For(_controlsKeys ? "controlsfor.keys" : "controlsfor.controller"),
                    Value = () => _controlsKeys ? "KEYBOARD & MOUSE" : "CONTROLLER",
                    Step = _ =>
                    {
                        _controlsKeys = !_controlsKeys;
                        _captureId = null;
                    }
                }, loose: true);
            }
            void Practice()
            {
                List<RowModel> practice = TryRows(); // (none over a game: the ship's OPTIONS, the pause menu)
                if (practice.Count == 0) return;
                Header("PRACTICE");
                foreach (RowModel row in practice) Setting(row);
            }
            void ResetAll(string label, string hint, string question, Action reset, Func<bool> atDefault) => Setting(new RowModel
            {
                Label = label,
                Description = ControlHints.For(hint, keys),
                Step = _ => ConfirmReset(question, reset),
                Disabled = atDefault,
                Action = true
            }, loose: true);

            switch (sub)
            {
            case "AIM":
                Header("AIM");
                Setting(keys
                    ? new RowModel
                    {
                        Label = "mouse\nsensitivity",
                        Description = ControlHints.For("sensitivity", keys: true),
                        Value = () => MouseScale.Text(_settings.MouseSens),
                        Step = s => _settings.MouseSens = MouseScale.Step(_settings.MouseSens, s), // (the ship's slider has the same steps)
                        Reset = () => _settings.MouseSens = d.MouseSens,
                        AtDefault = () => _settings.MouseSens == d.MouseSens,
                        GroupWithNext = () => _settings.ZoomSensPaired
                    }
                    : new RowModel
                    {
                        Label = "sensitivity",
                        Description = ControlHints.For("sensitivity"),
                        Value = () => Math.Clamp(_settings.GameSensitivity, 1, 15).ToString(inv),
                        Step = s => _settings.GameSensitivity = Math.Clamp(_settings.GameSensitivity + s, 1, 15),
                        Reset = () => _settings.GameSensitivity = d.GameSensitivity,
                        AtDefault = () => _settings.GameSensitivity == d.GameSensitivity,
                        GroupWithNext = () => _settings.ZoomSensPaired
                    });
                foreach (RowModel row in ZoomSensRows(keys)) Setting(row); // imperialist sensitivity, pair with aim (RecompMenus.ZoomAim.cs)
                // up/down speed apart from left/right, the controller's or the mouse's own (owner 2026-10-26)
                Setting(new RowModel
                {
                    Label = "y\nspeed",
                    Describe = () => ControlHints.For(AimYPercent(keys) == 100 ? "yspeed.100%" : "yspeed", keys),
                    Value = () => AimYPercent(keys) + "%",
                    Step = s => { int[] all = MphRecomp.Input.StylusAim.YPercents; int i = Math.Max(0, Array.IndexOf(all, AimYPercent(keys))); int v = all[((i + s) % all.Length + all.Length) % all.Length]; if (keys) _settings.MousePitchPercent = v; else _settings.AimPitchPercent = v; },
                    Reset = () => { if (keys) _settings.MousePitchPercent = d.MousePitchPercent; else _settings.AimPitchPercent = d.AimPitchPercent; },
                    AtDefault = () => AimYPercent(keys) == (keys ? d.MousePitchPercent : d.AimPitchPercent)
                });
                // look invert, up/down and left/right apart, the controller's or the mouse's own (owner 2026-10-26)
                Setting(new RowModel
                {
                    Label = "invert\ny",
                    Describe = () => ControlHints.For(LookInvertY(keys) ? "lookinvert.on" : "lookinvert.off", keys),
                    Value = () => LookInvertY(keys) ? "ON" : "OFF",
                    Step = _ => { if (keys) _settings.MouseInvertY = !_settings.MouseInvertY; else _settings.GameLookInvert = !_settings.GameLookInvert; },
                    Reset = () => { if (keys) _settings.MouseInvertY = d.MouseInvertY; else _settings.GameLookInvert = d.GameLookInvert; },
                    AtDefault = () => LookInvertY(keys) == (keys ? d.MouseInvertY : d.GameLookInvert)
                });
                Setting(new RowModel
                {
                    Label = "invert\nx",
                    Describe = () => ControlHints.For(LookInvertX(keys) ? "lookinvertx.on" : "lookinvertx.off", keys),
                    Value = () => LookInvertX(keys) ? "ON" : "OFF",
                    Step = _ => { if (keys) _settings.MouseInvertX = !_settings.MouseInvertX; else _settings.GameLookInvertX = !_settings.GameLookInvertX; },
                    Reset = () => { if (keys) _settings.MouseInvertX = d.MouseInvertX; else _settings.GameLookInvertX = d.GameLookInvertX; },
                    AtDefault = () => LookInvertX(keys) == (keys ? d.MouseInvertX : d.GameLookInvertX)
                });
                Header("ZOOM");
                foreach (RowModel row in ZoomLookRows(keys)) Setting(row);
                Practice();
                ResetAll("reset\naim", "resetaim", "reset every aim setting to default?", ResetAim, () => AimAtDefault(keys));
                break;
            case "GYRO":
            case "TOUCH":
                // the gyro and the touch controls (owner 2026-10-17: every control setting on CONTROLS; SettingRows has them)
                Header(sub);
                foreach (RowModel row in (sub == "GYRO" ? gyroLabels : touchLabels).Select(DeviceRow).OfType<RowModel>()) Setting(row);
                Practice();
                if (sub == "GYRO") ResetAll("reset\ngyro", "resetgyro", "reset every gyro setting to default?\n\ngyro aim stays as it is.", ResetGyro, GyroAtDefault);
                else ResetAll("reset\ntouch", "resettouch", "reset every touch setting to default?", ResetTouch, TouchAtDefault);
                break;
            default:
                {
                    ControlBinds.Function[] all = ControlBinds.Functions(keys);
                    Header(keys ? "KEYS AND MOUSE BUTTONS" : "CONTROLLER BUTTONS");
                    // a PC pad's face buttons by letter or by position (thread D 10-24, D6c: DesktopInput reads it)
                    if (!keys && RecompSettings.DesktopHost)
                    {
                        Setting(new RowModel
                        {
                            Label = "nintendo layout",
                            Describe = () => ControlHints.For("nintendolayout." + _settings.PadLayout),
                            Value = () => _settings.PadLayout.ToUpperInvariant(),
                            Step = s =>
                            {
                                string[] layouts = RecompSettings.PadLayouts;
                                int i = Math.Max(0, Array.IndexOf(layouts, _settings.PadLayout));
                                _settings.PadLayout = layouts[((i + s) % layouts.Length + layouts.Length) % layouts.Length];
                            },
                            Reset = () => _settings.PadLayout = d.PadLayout,
                            AtDefault = () => _settings.PadLayout == d.PadLayout
                        });
                    }
                    Binds(all.Where(f => f.Group != ControlBinds.Drone && !(keys && ControlBinds.IsWeapon(f.Id))));
                    if (keys)
                    {
                        // (owner 2026-10-15: a key for each weapon; C9: the keyboard only)
                        Header("WEAPON KEYS");
                        Binds(all.Where(f => ControlBinds.IsWeapon(f.Id)));
                    }
                    Header("SCOUT DRONE");
                    Binds(all.Where(f => f.Group == ControlBinds.Drone));
                    // (2026-10-19: the laser and burst waits' dials trade sides on screen, the buttons stay put)
                    Setting(new RowModel
                    {
                        Label = "swap\ndrone dials",
                        Describe = () => ControlHints.For(_settings.DroneDialsSwapped ? "dronedials.on" : "dronedials.off"),
                        Value = () => _settings.DroneDialsSwapped ? "ON" : "OFF",
                        Step = _ => _settings.DroneDialsSwapped = !_settings.DroneDialsSwapped
                    });
                    Practice();
                    ResetAll("reset\nbuttons", "resetbuttons",
                    (keys ? "reset every key and mouse button to default?" : "reset every button to default?")
                    + "\n\nchat mode and the drone dials go back too.", () => ResetButtons(keys), () => ButtonsAtDefault(keys));
                }
                break;
            }

            // a header shows the cells' columns when its section has buttons
            for (int i = 0; i < e.Count; i++)
            {
                if (e[i].Kind != EntryKind.Header) continue;
                for (int j = i + 1; j < e.Count && e[j].Kind != EntryKind.Header && !e[j].Loose; j++)
                {
                    if (e[j].Kind == EntryKind.Bind) e[i].Columns = true;
                }
            }
            return e;
        }

        // each sub-tab's reset (its RESET row): every setting on it back to the defaults
        private void ResetButtons(bool keys)
        {
            RecompSettings d = DefaultSettings;
            ControlBinds.Reset(_settings, keys);
            _settings.ChatToggle = d.ChatToggle;
            _settings.DroneDialsSwapped = d.DroneDialsSwapped;
            if (!keys && RecompSettings.DesktopHost) _settings.PadLayout = d.PadLayout;
        }
        private bool ButtonsAtDefault(bool keys)
        {
            RecompSettings d = DefaultSettings;
            return ControlBinds.IsDefault(_settings, keys) && _settings.ChatToggle == d.ChatToggle && _settings.DroneDialsSwapped == d.DroneDialsSwapped
                && (keys || !RecompSettings.DesktopHost || _settings.PadLayout == d.PadLayout);
        }
        private void ResetAim()
        {
            RecompSettings d = DefaultSettings, s = _settings;
            if (_controlsKeys) (s.MouseSens, s.ZoomMouseSens, s.MouseInvertY, s.MouseInvertX, s.MousePitchPercent) = (d.MouseSens, d.ZoomMouseSens, d.MouseInvertY, d.MouseInvertX, d.MousePitchPercent);
            else (s.GameSensitivity, s.ZoomSensitivity, s.GameLookInvert, s.GameLookInvertX, s.AimPitchPercent) = (d.GameSensitivity, d.ZoomSensitivity, d.GameLookInvert, d.GameLookInvertX, d.AimPitchPercent);
            (s.ZoomSensPaired, s.ZoomStep, s.ZoomReadyIcon) = (d.ZoomSensPaired, d.ZoomStep, d.ZoomReadyIcon);
        }
        private bool AimAtDefault(bool keys)
        {
            RecompSettings d = DefaultSettings, s = _settings;
            bool own = keys ? s.MouseSens == d.MouseSens && s.ZoomMouseSens == d.ZoomMouseSens && s.MouseInvertY == d.MouseInvertY && s.MouseInvertX == d.MouseInvertX
                    && s.MousePitchPercent == d.MousePitchPercent
                : s.GameSensitivity == d.GameSensitivity && s.ZoomSensitivity == d.ZoomSensitivity && s.GameLookInvert == d.GameLookInvert
                    && s.GameLookInvertX == d.GameLookInvertX && s.AimPitchPercent == d.AimPitchPercent;
            return own && s.ZoomSensPaired == d.ZoomSensPaired && s.ZoomStep == d.ZoomStep && s.ZoomReadyIcon == d.ZoomReadyIcon;
        }
        // the AIM tab's look invert: the mouse's (KEYBOARD & MOUSE) or the controller's
        private bool LookInvertY(bool keys) => keys ? _settings.MouseInvertY : _settings.GameLookInvert;
        private bool LookInvertX(bool keys) => keys ? _settings.MouseInvertX : _settings.GameLookInvertX;
        private int AimYPercent(bool keys) => keys ? _settings.MousePitchPercent : _settings.AimPitchPercent;

        // the gyro axis row's setting: a controller has its own (PadGyroAxis, "player" by default): always on a PC,
        // on Android while a controller with a gyro is connected (MphRead.Android CampaignGyro.cs PadGyros)
        private static bool PadGyroAxisRow => RecompSettings.DesktopHost || PadGyro();
        private string GyroAxisSetting
        {
            get => PadGyroAxisRow ? _settings.PadGyroAxis : _settings.GyroAxis;
            set
            {
                if (PadGyroAxisRow) _settings.PadGyroAxis = value;
                else _settings.GyroAxis = value;
            }
        }

        // (gyro aim itself stays on or off: the reset is its tuning)
        private void ResetGyro()
        {
            RecompSettings d = DefaultSettings, s = _settings;
            (s.GyroSensitivity, s.GyroAxis, s.PadGyroAxis, s.GyroPitchSensitivity, s.GyroInvertPitch, s.GyroInvertYaw) =
                (d.GyroSensitivity, d.GyroAxis, d.PadGyroAxis, d.GyroPitchSensitivity, d.GyroInvertPitch, d.GyroInvertYaw);
            (s.GyroDeadband, s.GyroSmoothing, s.GyroAccel) = (d.GyroDeadband, d.GyroSmoothing, d.GyroAccel);
            (s.GyroZoomPaired, s.GyroZoomSensitivity) = (d.GyroZoomPaired, d.GyroZoomSensitivity);
        }
        private bool GyroAtDefault()
        {
            RecompSettings d = DefaultSettings, s = _settings;
            return s.GyroSensitivity == d.GyroSensitivity && s.GyroAxis == d.GyroAxis && s.PadGyroAxis == d.PadGyroAxis
                && s.GyroPitchSensitivity == d.GyroPitchSensitivity
                && s.GyroInvertPitch == d.GyroInvertPitch && s.GyroInvertYaw == d.GyroInvertYaw
                && s.GyroDeadband == d.GyroDeadband && s.GyroSmoothing == d.GyroSmoothing
                && s.GyroAccel == d.GyroAccel && s.GyroZoomPaired == d.GyroZoomPaired && s.GyroZoomSensitivity == d.GyroZoomSensitivity;
        }
        private void ResetTouch()
        {
            RecompSettings d = DefaultSettings, s = _settings;
            (s.TouchControls, s.TouchAim, s.TouchSens, s.TouchFireAim) = (d.TouchControls, d.TouchAim, d.TouchSens, d.TouchFireAim);
            (s.TouchInvertY, s.TouchInvertX) = (d.TouchInvertY, d.TouchInvertX);
            (s.TouchZoomPaired, s.TouchZoomSens, s.TouchPitchPercent) = (d.TouchZoomPaired, d.TouchZoomSens, d.TouchPitchPercent);
        }
        private bool TouchAtDefault()
        {
            RecompSettings d = DefaultSettings, s = _settings;
            return s.TouchControls == d.TouchControls && s.TouchAim == d.TouchAim && s.TouchSens == d.TouchSens && s.TouchFireAim == d.TouchFireAim
                && s.TouchInvertY == d.TouchInvertY && s.TouchInvertX == d.TouchInvertX
                && s.TouchZoomPaired == d.TouchZoomPaired && s.TouchZoomSens == d.TouchZoomSens && s.TouchPitchPercent == d.TouchPitchPercent;
        }

        // ---- scrolling ----

        // the header of the section entry `at` is in (-1: none, or a loose entry ended it)
        private static int SectionHeader(List<Entry> entries, int at)
        {
            if (at >= entries.Count || entries[at].Kind == EntryKind.Header || entries[at].Loose) return -1;
            for (int j = at - 1; j >= 0; j--)
            {
                if (entries[j].Kind == EntryKind.Header) return j;
                if (entries[j].Loose) return -1;
            }
            return -1;
        }

        // the view from entry `scroll`: its section's header first (when the view starts inside a section), then the
        // entries in order, eight slots in all
        private static int[] View(List<Entry> entries, int scroll)
        {
            var view = new List<int>(SecSlots);
            if (entries.Count > 0 && entries[0].Kind == EntryKind.SubTabs)
            {
                view.Add(0); // (the sub-tabs' strip stays over the rows)
                scroll = Math.Max(scroll, 1);
            }
            int header = SectionHeader(entries, scroll);
            if (header >= 0) view.Add(header);
            for (int i = scroll; i < entries.Count && view.Count < SecSlots; i++) view.Add(i);
            // a header never last, its rows out of sight (its slot left empty)
            if (view.Count == SecSlots && entries[view[^1]].Kind == EntryKind.Header) view.RemoveAt(view.Count - 1);
            return view.ToArray();
        }

        // the last scroll that still fills the view (the list's end at its foot)
        private static int MaxScroll(List<Entry> entries)
        {
            int s = 0;
            while (s < entries.Count - 1 && !View(entries, s).Contains(entries.Count - 1)) s++;
            return s;
        }

        // scrolls the least that brings entries lo..hi into the view (a header just above lo comes with it; nothing to
        // focus above: the very top)
        private static void ScrollTo(SectionPage sp, int lo, int hi)
        {
            List<Entry> e = sp.Entries;
            if (e.Count == 0) return;
            lo = Math.Clamp(lo, 0, e.Count - 1);
            hi = Math.Clamp(hi, lo, e.Count - 1);
            if (lo > 0 && e[lo - 1].Kind == EntryKind.Header) lo--;
            if (!e.Take(lo).Any(Focusable)) lo = 0;
            if (lo < sp.Scroll) sp.Scroll = lo;
            while (sp.Scroll < lo && !View(e, sp.Scroll).Contains(hi)) sp.Scroll++;
            sp.Scroll = Math.Clamp(sp.Scroll, 0, MaxScroll(e));
        }
        private static bool Focusable(Entry e) => e.Kind is not (EntryKind.Header or EntryKind.Note) && e.Row?.Disabled?.Invoke() != true;

        private static bool HasReset(Entry e) => e.Kind == EntryKind.Setting && e.Row?.Reset != null;

        // the column a focus lands on in an entry, from the one it came from
        private static int ColFor(Entry e, int col) => e.Kind switch
        {
            EntryKind.Bind => Math.Clamp(col, 0, 3),
            EntryKind.Setting => col == 3 && HasReset(e) && e.Row!.Disabled?.Invoke() != true ? 3 : 0,
            _ => 0
        };

        private static int ItemFor(SectionPage sp, int slot, Entry e, int col) => e.Kind switch
        {
            EntryKind.Bind => col == 0 ? sp.Cell0[slot] : col == 1 ? sp.Cell1[slot] : col == 2 ? sp.Clear[slot] : sp.Cell2[slot],
            EntryKind.Setting => col == 3 && HasReset(e) ? sp.Cell2[slot] : sp.Value[slot],
            EntryKind.Button => sp.Button[slot],
            EntryKind.SubTabs => sp.Value[slot],
            _ => -1
        };

        // the entries again (a value's Disabled, the device switch), the focus kept on its entry when it still can be, the
        // view scrolled to it; `keepView`: the view stays (the bar scrolled it) and the focus moves into it
        private void Rebuild(SectionPage sp, bool keepView = false)
        {
            sp.Entries = sp.Source();
            sp.PageNo = 0;
            sp.Scroll = Math.Clamp(sp.Scroll, 0, MaxScroll(sp.Entries));
            if (sp.Focus >= sp.Entries.Count || sp.Focus >= 0 && !Focusable(sp.Entries[sp.Focus])) sp.Focus = -1;
            int[] view = View(sp.Entries, sp.Scroll);
            if (sp.Focus < 0 || keepView && !view.Contains(sp.Focus))
            {
                bool Row(Entry x) => Focusable(x) && x.Kind != EntryKind.SubTabs;
                sp.Focus = view.FirstOrDefault(i => Row(sp.Entries[i]), -1);
                if (sp.Focus < 0) sp.Focus = sp.Entries.FindIndex(Row);
                if (sp.Focus < 0) sp.Focus = sp.Entries.FindIndex(Focusable);
                sp.Col = sp.Focus >= 0 ? ColFor(sp.Entries[sp.Focus], 0) : 0;
            }
            if (sp.Focus >= 0)
            {
                sp.Col = ColFor(sp.Entries[sp.Focus], sp.Col);
                // the focus in the view, with the row it pairs with (C3's box)
                bool pairNext = sp.Entries[sp.Focus].Row?.GroupWithNext != null;
                bool pairPrev = sp.Focus > 0 && sp.Entries[sp.Focus - 1].Row?.GroupWithNext != null;
                if (!keepView) ScrollTo(sp, pairPrev ? sp.Focus - 1 : sp.Focus, pairNext ? sp.Focus + 1 : sp.Focus);
            }
            sp.Pages = new List<int[]> { View(sp.Entries, sp.Scroll) };
            bool tabs = sp.Tabs();
            _strings.Set(sp.TitleString, tabs ? "" : sp.Name); // (the tabs name it)
            List<(SectionPage Page, float X0, float X1)> layout = tabs ? TabLayout() : new();
            sp.TabCount = layout.Count;
            for (int t = 0; t < layout.Count; t++)
            {
                sp.TabRect[t][0] = TouchRect(layout[t].X0, TabBottom + 192, layout[t].X1, TabTop + 192, sp.CallBase + SecTab0 + t);
            }
            LaySubRects(sp);
            AlignColumns(sp);
        }

        // the sub-tabs' touch rects, where the chips are drawn
        private void LaySubRects(SectionPage sp)
        {
            sp.SubPad = _padShown;
            List<(float X0, float X1)> subs = SubLayout(sp);
            for (int t = 0; t < subs.Count; t++)
            {
                sp.SubRect[t][0] = TouchRect(subs[t].X0, SlotTop(0) - SecH + 192, subs[t].X1, SlotTop(0) + 192, sp.CallBase + t * 10 + CellSubTab);
            }
        }

        // the strip's ends, and the L2 / R2 keys there while a controller is connected (owner 2026-10-24: "l2/r2 for the
        // sub tabs on the sides like for the main tabs")
        private const float SubX0 = SecLabelX - 4, SubX1 = SecC2X1 + 2, SubKeyGap = 3;

        // the sub-tabs' chips across the strip's slot, all the same width (owner 2026-10-29: name-sized chips looked uneven,
        // most on PC's BUTTONS / AIM) (none: the page has no strip)
        private List<(float X0, float X1)> SubLayout(SectionPage sp)
        {
            var layout = new List<(float, float)>();
            if (sp.Entries.Count == 0 || sp.Entries[0].Kind != EntryKind.SubTabs) return layout;
            int count = Math.Min(sp.SubNames.Length, SecMaxSub);
            float keys = _padShown ? TabIconW + SubKeyGap : 0;
            float x0 = SubX0 + keys, x1 = SubX1 - keys;
            float w = (x1 - x0 - TabGap * (count - 1)) / count, x = x0;
            for (int t = 0; t < count; t++)
            {
                layout.Add((x, x + w));
                x += w + TabGap;
            }
            return layout;
        }

        // a settings tab's lone kind lines up with the rest: one switch among buttons (APP's UPDATE ALERTS) goes in the buttons'
        // column, one button among switches (GAME's IMPORT GAMES) in the value boxes'; CONTROLS keeps both columns as they are
        private void AlignColumns(SectionPage sp)
        {
            float valueW = SecC1X1 - SecC0X0;
            (sp.ValueX0, sp.ValueX1, sp.ButtonX0, sp.ButtonX1) = (SecC0X0, SecC1X1, 128 - SecButtonW / 2, 128 + SecButtonW / 2);
            if (_settingSections.Contains(sp))
            {
                int values = sp.Entries.Count(e => e.Kind == EntryKind.Setting && e.Row?.Check == null);
                int buttons = sp.Entries.Count(e => e.Kind == EntryKind.Button);
                if (values > 0 && buttons > values) (sp.ValueX0, sp.ValueX1) = (128 - valueW / 2, 128 + valueW / 2);
                else if (buttons > 0 && values >= buttons) (sp.ButtonX0, sp.ButtonX1) = (SecC0X0, SecC1X1);
            }
            MenuPage page = _file.Pages[sp.Page];
            for (int s = 0; s < SecSlots; s++)
            {
                MoveX(page, sp.Value[s], sp.ValueX0, sp.ValueX1);
                MoveX(page, sp.Button[s], sp.ButtonX0, sp.ButtonX1);
                MoveX(page, sp.ArrowL[s], sp.ValueX0 - 3, sp.ValueX0 + 14);
                MoveX(page, sp.ArrowR[s], sp.ValueX1 - 14, sp.ValueX1 + 3);
            }
        }

        // an item's touch rectangle (its first action) to x0..x1, kept at its height
        private static void MoveX(MenuPage page, int item, float x0, float x1)
        {
            if (item < 0 || page.Items[item].Actions is not MenuAction[] acts || acts.Length == 0) return;
            MenuAction a = acts[0];
            short rx = (short)MathF.Floor(x0), rw = (short)MathF.Ceiling(x1);
            if (a.RectX == rx && a.RectW == rw) return;
            acts[0] = new MenuAction
            {
                Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = rx, RectY = a.RectY, RectW = rw,
                RectH = a.RectH, Calls = a.Calls, Item = a.Item, TargetPage = a.TargetPage, Field17 = a.Field17
            };
        }

        // the page's items for the entries shown (a state is only set when it changes: a glow keeps its pulse)
        private static void ShowSection(MenuEngine menu, SectionPage sp)
        {
            void Want(int item, MenuState state)
            {
                if (item >= 0 && menu.ItemState(item) != state) menu.SetState(item, state);
            }
            int[] slots = sp.Pages[sp.PageNo];
            int focusItem = -1;
            for (int s = 0; s < SecSlots; s++)
            {
                Entry? e = s < slots.Length ? sp.Entries[slots[s]] : null;
                bool off = e?.Row?.Disabled?.Invoke() == true;
                MenuState live = off ? MenuState.State5 : MenuState.Idle;
                bool setting = e?.Kind == EntryKind.Setting, bind = e?.Kind == EntryKind.Bind, strip = e?.Kind == EntryKind.SubTabs;
                bool arrows = setting && !e!.Row!.Action && e.Row.Check == null && !off;
                if (e != null && slots[s] == sp.Focus) focusItem = ItemFor(sp, s, e, sp.Col);
                int[] items = { sp.Cell0[s], sp.Cell1[s], sp.Cell2[s], sp.Value[s], sp.Button[s], sp.Clear[s] };
                MenuState[] states =
                {
                    bind ? live : MenuState.Hidden,
                    bind ? live : MenuState.Hidden,
                    bind ? live : setting && HasReset(e!) ? live : MenuState.Hidden,
                    setting || strip ? live : MenuState.Hidden,
                    e?.Kind == EntryKind.Button ? live : MenuState.Hidden,
                    bind ? live : MenuState.Hidden,
                };
                for (int k = 0; k < items.Length; k++)
                {
                    if (items[k] == focusItem && states[k] == MenuState.Idle) continue; // set Focused below
                    Want(items[k], states[k]);
                }
                Want(sp.ArrowL[s], arrows ? MenuState.Idle : MenuState.Hidden);
                Want(sp.ArrowR[s], arrows ? MenuState.Idle : MenuState.Hidden);
            }
            bool tabs = sp.Tabs();
            Want(sp.Prev, tabs ? MenuState.Idle : MenuState.Hidden);
            Want(sp.Next, tabs ? MenuState.Idle : MenuState.Hidden);
            for (int t = 0; t < SecMaxTabs; t++) Want(sp.Tab[t], tabs && t < sp.TabCount ? MenuState.Idle : MenuState.Hidden);
            int subs = sp.Entries.Count > 0 && sp.Entries[0].Kind == EntryKind.SubTabs ? sp.SubNames.Length : 0;
            for (int t = 0; t < SecMaxSub; t++) Want(sp.Sub[t], t < subs ? MenuState.Idle : MenuState.Hidden);
            bool scrolls = MaxScroll(sp.Entries) > 0;
            Want(sp.ScrollUp, scrolls && sp.Scroll > 0 ? MenuState.Idle : MenuState.Hidden);
            Want(sp.ScrollDown, scrolls && sp.Scroll < MaxScroll(sp.Entries) ? MenuState.Idle : MenuState.Hidden);
            if (focusItem >= 0) Want(focusItem, MenuState.Focused);
        }

        // ---- runtime ----

        // RecompMenus.OnPageEntered
        private void EnterSections(MenuEngine menu, int page)
        {
            SectionPage? sp = SectionFor(page);
            if (sp == null) return;
            ApplySectionSounds(menu.Sounds);
            // from OPTIONS, SECOND HUNT SETTINGS or another section (L / R): from the top; from a page it opened (TRY
            // CONTROLS' run, a song list, IMPORT GAMES): as it was
            bool fromSettings = _settingSections.Any(s => s.Page == _lastPage) || _lastPage == s_controlsPage && sp != _ctl;
            bool fresh = _lastPage == OptionsPage || _lastPage == PausePage || _lastPage == -1 || fromSettings;
            if (sp == _ctl)
            {
                ControlBinds.Tidy(_settings);
                // from a settings tab: one of the tabs (back is OPTIONS either way)
                if (_lastPage == OptionsPage || _lastPage == PausePage) _ctlInSettings = false;
                else if (fromSettings) _ctlInSettings = true;
                _ctlBackPage = OptionsPage;
                if (_controlsFromHost) fresh = false;
                _captureId = null;
            }
            if (fresh)
            {
                sp.PageNo = 0;
                sp.Scroll = 0;
                sp.Focus = -1;
                sp.SubTab = 0;
            }
            sp.Entered?.Invoke();
            if (menu.Page?.Index != sp.Page) return; // (TRY CONTROLS' run starting)
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        // our items' sounds: the back arrow as CREATE OR JOIN's; the cells make the option box's sound themselves (on A
        // only: a borrowed sound plays for every action of an item, the D-pad ones too)
        private void ApplySectionSounds(MenuSounds sounds)
        {
            if (ReferenceEquals(sounds, _sectionSounds)) return;
            _sectionSounds = sounds;
            foreach (SectionPage sp in SectionPages())
            {
                if (sp.Back >= 0 && _soundBack >= 0) sounds.ItemLike(sp.Page, sp.Back, MultiplayerMenu.CreateJoinPage, _soundBack);
            }
        }

        private void ToggleSound(MenuEngine menu)
        {
            if (_soundToggle < 0) return;
            int sound = menu.Sounds.Item(OptionBoxPage, _soundToggle).Sound;
            if (sound != 0) menu.Host?.OnSound(menu, sound, "controls cell");
        }

        // Returns true when the call was a sectioned page's
        private bool SectionCall(MenuEngine menu, int call)
        {
            if (call < ControlsCallBase || call > SectionCallsEnd) return false;
            SectionPage? sp = SectionPages().FirstOrDefault(p => call >= p.CallBase && call < p.CallBase + 100);
            if (sp == null || menu.Page?.Index != sp.Page) return true;
            int k = call - sp.CallBase;
            if (k == SecBack)
            {
                SectionBack(menu, sp);
                return true;
            }
            SyncFocus(menu, sp);
            switch (k)
            {
            case SecUp:
            case SecDown:
                MoveRow(menu, sp, k == SecUp ? -1 : 1);
                return true;
            case SecLeft:
            case SecRight:
                MoveCell(menu, sp, k == SecLeft ? -1 : 1);
                return true;
            case >= SecTab0 and < SecTab0 + SecMaxTabs:
                if (sp.Tabs()) GoToTab(menu, sp, k - SecTab0); // (RecompMenus.SettingsPages.cs)
                return true;
            case SecPrev:
            case SecNext:
                if (sp.Tabs()) NextSection(menu, sp, k == SecNext ? 1 : -1); // (RecompMenus.SettingsPages.cs)
                else if (sp.SubNames.Length > 1) GoToSubTab(menu, sp, sp.SubTab + (k == SecNext ? 1 : -1));
                return true;
            case SecSubPrev:
            case SecSubNext:
                // L2 / R2 (Z / C on a keyboard): the sub-tabs, also where L / R go through the tabs (owner 2026-10-24)
                if (sp.SubNames.Length > 1) GoToSubTab(menu, sp, sp.SubTab + (k == SecSubNext ? 1 : -1));
                return true;
            case SecScrollUp:
            case SecScrollDown:
                // a screen up or down, the focus onto the view's first row it can take
                sp.Scroll = Math.Clamp(sp.Scroll + (k == SecScrollDown ? SecSlots - 2 : 2 - SecSlots), 0, MaxScroll(sp.Entries));
                _captureId = null;
                Rebuild(sp, keepView: true);
                ShowSection(menu, sp);
                return true;
            case SecReset:
                // Y: the focused row's RESET, or a button's CLEAR (as before it had a RESET cell)
                if (sp.Focus >= 0 && sp.Entries[sp.Focus] is Entry y && (y.Kind == EntryKind.Bind || HasReset(y)))
                {
                    Run(menu, sp, y, y.Kind == EntryKind.Bind ? CellClear : CellReset);
                }
                return true;
            }
            int slot = k / 10, cell = k % 10;
            if (cell == CellSubTab)
            {
                if (slot < sp.SubNames.Length && slot != sp.SubTab) GoToSubTab(menu, sp, slot);
                return true;
            }
            int[] slots = sp.Pages[sp.PageNo];
            if (slot >= slots.Length || cell > CellArrowR && cell != CellClear) return true;
            int at = slots[slot];
            Entry e = sp.Entries[at];
            if (!Focusable(e)) return true;
            if (e.Kind == EntryKind.SubTabs)
            {
                GoToSubTab(menu, sp, sp.SubTab + 1); // (A on the strip: the next one)
                return true;
            }
            sp.Focus = at;
            sp.Col = cell is CellSecondary ? 1 : cell is CellClear ? 2 : cell is CellReset ? 3 : 0;
            Run(menu, sp, e, cell);
            return true;
        }

        // a cell's A (or tap): PRIMARY / SECONDARY wait for a button, CLEAR / RESET, a value steps, a button runs
        private void Run(MenuEngine menu, SectionPage sp, Entry e, int cell)
        {
            bool keys = _controlsKeys;
            if (e.Kind == EntryKind.Bind)
            {
                if (cell is CellPrimary or CellSecondary)
                {
                    ToggleSound(menu);
                    BeginCapture(e.Bind!, cell);
                }
                else if (cell == CellClear)
                {
                    ToggleSound(menu);
                    _captureId = null;
                    ControlBinds.Clear(_settings, keys, e.Bind!);
                    _settings.Save(_settingsPath);
                }
                else if (cell == CellReset)
                {
                    // RESET: this function's own default buttons (no question: it's one row)
                    if (ControlBinds.IsDefaultOne(_settings, keys, e.Bind!)) return;
                    ToggleSound(menu);
                    _captureId = null;
                    ControlBinds.ResetOne(_settings, keys, e.Bind!);
                    _settings.Save(_settingsPath);
                }
                return;
            }
            RowModel row = e.Row!;
            if (row.Disabled?.Invoke() == true) return;
            switch (cell)
            {
            case CellReset:
                if (row.Reset == null || row.AtDefault?.Invoke() == true) return;
                row.Reset();
                break;
            case CellArrowL:
            case CellArrowR:
                if (row.Action) return;
                row.Step(cell == CellArrowL ? -1 : 1);
                break;
            default:
                row.Step(1);
                break;
            }
            ToggleSound(menu);
            _settings.Save(_settingsPath);
            if (menu.Page?.Index != sp.Page) return; // (the step changed page: TRY CONTROLS)
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        // the focus as the engine has it (a touch focuses the tapped cell itself)
        private static void SyncFocus(MenuEngine menu, SectionPage sp)
        {
            int item = menu.FocusedItem();
            if (item < 0) return;
            int[] slots = sp.Pages[sp.PageNo];
            for (int s = 0; s < slots.Length && s < SecSlots; s++)
            {
                int col = item == sp.Cell0[s] || item == sp.Value[s] || item == sp.Button[s] ? 0 : item == sp.Cell1[s] ? 1 : item == sp.Clear[s] ? 2 : item == sp.Cell2[s] ? 3 : -1;
                if (col < 0) continue;
                sp.Focus = slots[s];
                sp.Col = col;
                return;
            }
        }

        private void MoveRow(MenuEngine menu, SectionPage sp, int dir)
        {
            int from = sp.Focus;
            int to = -1;
            for (int j = from + dir; j >= 0 && j < sp.Entries.Count; j += dir)
            {
                if (Focusable(sp.Entries[j]))
                {
                    to = j;
                    break;
                }
            }
            if (to < 0) return;
            _captureId = null;
            sp.Focus = to;
            sp.Col = ColFor(sp.Entries[to], sp.Col);
            // the row after it too (or the one before, going up): what's next is always in sight
            ScrollTo(sp, dir < 0 ? to - 1 : to, dir > 0 ? to + 1 : to);
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        private void MoveCell(MenuEngine menu, SectionPage sp, int dx)
        {
            if (sp.Focus < 0) return;
            Entry e = sp.Entries[sp.Focus];
            if (e.Kind == EntryKind.SubTabs)
            {
                GoToSubTab(menu, sp, sp.SubTab + dx);
                return;
            }
            if (e.Kind == EntryKind.Bind)
            {
                sp.Col = Math.Clamp(sp.Col + dx, 0, 3);
            }
            else if (e.Kind == EntryKind.Setting)
            {
                RowModel row = e.Row!;
                if (sp.Col == 3) sp.Col = dx < 0 ? 0 : 3;
                else if (!row.Action && row.Check == null)
                {
                    row.Step(dx); // a value: the D-pad steps it, as the game's option rows
                    _settings.Save(_settingsPath);
                }
                else if (dx > 0 && HasReset(e)) sp.Col = 3;
            }
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        // a big RESET row (a sub-tab's RESET BUTTONS / AIM / GYRO / TOUCH) asks first (owner 2026-10-24); YES resets and
        // saves, NO leaves everything as it is
        private void ConfirmReset(string question, Action reset)
        {
            if (Import.Popup == null)
            {
                reset();
                return;
            }
            Import.Popup.Ask(question, yes =>
            {
                if (!yes) return;
                reset();
                _settings.Save(_settingsPath);
                RefreshSettingsPages(); // (the page the box was over: its rows as they are now)
            });
        }

        // another sub-tab (round from the last to the first): its rows from the top, the focus on the strip
        private void GoToSubTab(MenuEngine menu, SectionPage sp, int t)
        {
            int n = sp.SubNames.Length;
            if (n == 0) return;
            sp.SubTab = (t % n + n) % n;
            sp.PageNo = 0;
            sp.Scroll = 0;
            sp.Focus = 0;
            sp.Col = 0;
            _captureId = null;
            ToggleSound(menu);
            Rebuild(sp);
            ShowSection(menu, sp);
        }

        private void SectionBack(MenuEngine menu, SectionPage sp)
        {
            _captureId = null;
            if (sp != _ctl)
            {
                SettingsBack(menu, sp); // (RecompMenus.SettingsPages.cs)
                return;
            }
            if (_controlsFromHost)
            {
                _controlsFromHost = false;
                Requested?.Invoke(FrontendRequestKind.ControlsDone);
            }
            else menu.GoTo(_ctlBackPage);
        }

        // ---- waiting for a button ----

        // CONTROLS is waiting for a button: "pad" (a controller's) or "keys" (a PC's keys and mouse buttons), else null. The
        // host sends its presses (and, for combos, its releases) to CaptureInput instead of the menu.
        public string? CaptureDevice => _captureId == null ? null : _controlsKeys ? "keys" : "pad";

        private void BeginCapture(string id, int slot)
        {
            _captureId = id;
            _captureSlot = slot;
            _capturePressed.Clear();
            _captureHeld.Clear();
            _captureStart = Environment.TickCount64;
        }

        // the host's presses while CaptureDevice is set, as ControlBinds button names, and its releases as "-" + the name.
        // What's held together is the answer, taken when the last of it is let go (one button, or a combo of up to three);
        // a fixed button keeps it waiting; a mouse wheel step answers at once; a whole combo ("A+B": a test's) at once.
        public void CaptureInput(string button)
        {
            if (_captureId == null) return;
            if (button.StartsWith('-'))
            {
                _captureHeld.Remove(button[1..]);
                if (_capturePressed.Count > 0 && _captureHeld.Count == 0) CommitCapture();
                return;
            }
            if (ControlBinds.IsCombo(button))
            {
                if (!ControlBinds.Allowed(_controlsKeys, button)) return;
                _capturePressed.Clear();
                _capturePressed.AddRange(ControlBinds.Parts(button));
                CommitCapture();
                return;
            }
            if (!ControlBinds.Allowed(_controlsKeys, button)) return;
            if (button.StartsWith("Wheel.", StringComparison.Ordinal))
            {
                if (_capturePressed.Count > 0) return;
                _capturePressed.Add(button);
                CommitCapture();
                return;
            }
            if (!_capturePressed.Contains(button) && _capturePressed.Count < ControlBinds.MaxCombo) _capturePressed.Add(button);
            if (!_captureHeld.Contains(button)) _captureHeld.Add(button);
        }

        // the answer into its cell (C11: never refused, never taken from another function), then the heads-up when the
        // button also does something else
        private void CommitCapture()
        {
            string? id = _captureId;
            string button = string.Join("+", _capturePressed);
            if (id == null || button.Length == 0 || !ControlBinds.Allowed(_controlsKeys, button))
            {
                EndCapture();
                return;
            }
            bool keys = _controlsKeys;
            IReadOnlyList<string> shared = ControlBinds.SetSlot(_settings, keys, id, _captureSlot, button);
            _settings.Save(_settingsPath);
            EndCapture();
            string name = ControlBinds.Short(ControlBinds.Normalize(keys, button));
            if (shared.Count > 0)
            {
                Import.Popup?.Inform($"{name} is also assigned to {string.Join(" and ", shared)}.");
            }
            else if (ControlBinds.IsCombo(button))
            {
                // a combo on a button that already does something on its own: it still does that for the moment before the
                // combo's last button goes down
                foreach ((string part, string label) in ControlBinds.ComboOverlaps(_settings, keys, id, button))
                {
                    Import.Popup?.Inform($"combo set.\n\nheads up: {ControlBinds.Short(part)} also does {label} on its own, and still does it for a moment when it's pressed first.");
                    break;
                }
            }
        }

        public void CancelCapture()
        {
            if (_captureId != null) EndCapture();
        }

        private void EndCapture()
        {
            _captureId = null;
            _capturePressed.Clear();
            _captureHeld.Clear();
        }

        // ---- drawing (FrontendSession.Build, after the page's own triangles) ----

        public void DrawControls(MenuEngine menu, List<WidgetTri> tris)
        {
            MenuPage? page = menu.Page;
            SectionPage? sp = page == null ? null : SectionFor(page.Index);
            if (sp == null || page == null) return;
            _drawMenu = menu;
            _out = tris;
            bool settled = menu.PendingPage == -1 && menu.ItemState(sp.Panel) == MenuState.Idle && menu.ItemCode(sp.Panel) == (int)MenuState.Idle;
            var glows = new Dictionary<int, List<WidgetTri>>();
            for (int t = tris.Count - 1; t >= 0; t--)
            {
                int item = tris[t].Item;
                if (item < 0 || item >= page.Items.Count || page.Items[item].States.Count != 1) continue;
                if (!glows.TryGetValue(item, out List<WidgetTri>? g)) glows[item] = g = new List<WidgetTri>();
                g.Add(tris[t]);
                tris.RemoveAt(t);
            }
            if (!settled || sp.Pages.Count == 0) return;
            try
            {
                DrawSection(sp, glows);
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                Console.Error.WriteLine("controls: can't draw (" + ex.Message + ")");
            }
        }

        private void DrawSection(SectionPage sp, Dictionary<int, List<WidgetTri>> glows)
        {
            int[] slots = sp.Pages[Math.Clamp(sp.PageNo, 0, sp.Pages.Count - 1)];
            bool keys = _controlsKeys;
            for (int s = 0; s < slots.Length && s < SecSlots; s++)
            {
                Entry e = sp.Entries[slots[s]];
                float top = SlotTop(s), bottom = top - SecH;
                float textTop = top - (SecH - 8 * SizeM) / 2;
                int tag = e.Kind switch
                {
                    EntryKind.Bind => sp.Cell0[s],
                    EntryKind.Setting => sp.Value[s],
                    EntryKind.Button => sp.Button[s],
                    EntryKind.SubTabs => sp.Value[s],
                    _ => sp.TitleItem
                };
                bool off = e.Row?.Disabled?.Invoke() == true;
                float alpha = off ? 0.4f : 1;
                int from = _out.Count;
                switch (e.Kind)
                {
                case EntryKind.Header:
                    TextS(e.Label, SecLabelX, top - 1.2f, 0, TitleColour, tag, SizeM);
                    Fill(SecLabelX - 4, top - 8.6f, SecC2X1 + 2, top - 9.2f, HeaderBar.R, HeaderBar.G, HeaderBar.B, 0.7f, tag);
                    if (e.Columns)
                    {
                        TextS("primary", (SecB0X0 + SecB0X1) / 2, top - 2.2f, 2, Pale, tag, SizeX);
                        TextS("secondary", (SecB1X0 + SecB1X1) / 2, top - 2.2f, 2, Pale, tag, SizeX);
                    }
                    break;
                case EntryKind.Setting:
                    {
                        RowModel row = e.Row!;
                        TextS(e.Label, SecLabelX, textTop, 0, Orange, tag, SizeM);
                        if (row.Check != null)
                        {
                            // a checkbox (C3): ticked = a lit square inside, the lock beside it (the row it pairs is locked)
                            bool on = row.Check();
                            float bx1 = SecC0X0 + SecH;
                            Fill(SecC0X0, top, bx1, bottom, 0, 0, 0, 0.43f, tag);
                            Box(SecC0X0, top, bx1, bottom, tag);
                            Glow(glows, sp.Value[s], SecC0X0, top, bx1, bottom, tag);
                            if (on)
                            {
                                Fill(SecC0X0 + 2.6f, top - 2.6f, bx1 - 2.6f, bottom + 2.6f, FrameOrange.R, FrameOrange.G, FrameOrange.B, 1, tag);
                                Picture(ImportCardPictures.Lock, bx1 + 3, top + 0.5f, SecH + 1, off: false, tag);
                            }
                            TextS(on ? "on" : "off", bx1 + (on ? SecH + 7 : 4), textTop, 0, White, tag, SizeM);
                        }
                        else
                        {
                            Cell(sp.ValueX0, top, sp.ValueX1, bottom, row.Value(), White, sp.Value[s], glows, tag);
                            if (!row.Action && !off)
                            {
                                SmallArrow(false, sp.ValueX0 + 3, top - (SecH - SecArrowH) / 2, tag);
                                SmallArrow(true, sp.ValueX1 - 3 - SecArrowW, top - (SecH - SecArrowH) / 2, tag);
                            }
                        }
                        if (row.Reset != null)
                        {
                            bool dim = row.AtDefault?.Invoke() == true;
                            int from2 = _out.Count;
                            Cell(SecC2X0, top, SecC2X1, bottom, "reset", Orange, sp.Cell2[s], glows, sp.Cell2[s]);
                            if (dim) Dim(from2, 0.5f);
                        }
                        // C3: the box round a row and the next (aim sensitivity + imperialist, while paired)
                        if (row.GroupWithNext?.Invoke() == true && s + 1 < slots.Length)
                        {
                            Box(SecLabelX - 5, top + 2.4f, SecC2X1 + 3, top - SecPitch - SecH - 2.4f, tag);
                        }
                    }
                    break;
                case EntryKind.Bind:
                    {
                        TextS(e.Label, SecLabelX, textTop, 0, Orange, tag, SizeM);
                        string id = e.Bind!;
                        for (int c = 0; c < 2; c++)
                        {
                            bool waiting = _captureId == id && _captureSlot == c;
                            string now = ControlBinds.Slot(_settings, keys, id, c);
                            string text = waiting ? "press" : now.Length > 0 ? ControlBinds.Short(now) : "-";
                            int item = c == 0 ? sp.Cell0[s] : sp.Cell1[s];
                            Cell(c == 0 ? SecB0X0 : SecB1X0, top, c == 0 ? SecB0X1 : SecB1X1, bottom, text,
                                waiting ? Cyan : now.Length > 0 ? White : OnGrey, item, glows, item);
                        }
                        bool none = ControlBinds.Get(_settings, keys, id).All(b => b.Length == 0);
                        int from2 = _out.Count;
                        Cell(SecBClrX0, top, SecBClrX1, bottom, "clear", Orange, sp.Clear[s], glows, sp.Clear[s]);
                        if (none) Dim(from2, 0.5f);
                        bool dflt = ControlBinds.IsDefaultOne(_settings, keys, id);
                        from2 = _out.Count;
                        Cell(SecC2X0, top, SecC2X1, bottom, "reset", Orange, sp.Cell2[s], glows, sp.Cell2[s]);
                        if (dflt) Dim(from2, 0.5f);
                    }
                    break;
                case EntryKind.SubTabs:
                    {
                        // the sub-tabs: the one shown lit (as the tabs over the panel), the lobby glow round it when focused
                        if (!sp.Tabs() && ++_padAsked % 60 == 0) _padShown = PadConnected(); // (no tabs to ask: pause / ship CONTROLS)
                        if (sp.SubPad != _padShown) LaySubRects(sp); // (a controller came or went)
                        List<(float X0, float X1)> chips = SubLayout(sp);
                        for (int t = 0; t < chips.Count; t++)
                        {
                            (float x0, float x1) = chips[t];
                            bool on = t == sp.SubTab;
                            if (on) Fill(x0, top, x1, bottom, FrameOrange.R, FrameOrange.G, FrameOrange.B, 0.55f, tag);
                            else Fill(x0, top, x1, bottom, 0, 0, 0, 0.43f, tag);
                            Box(x0, top, x1, bottom, tag);
                            if (on) Glow(glows, sp.Value[s], x0, top, x1, bottom, tag);
                            float room = x1 - x0 - 5, width = MathF.Max(_drawMenu!.Font.Measure(sp.SubNames[t]), 1);
                            float size = Math.Clamp(room / width, SizeX * 0.7f, SizeM);
                            int from2 = _out.Count;
                            TextS(sp.SubNames[t], (x0 + x1) / 2, top - (top - bottom - 8 * size) / 2, 2, on ? White : Orange, tag, size);
                            if (!on) Dim(from2, 0.75f);
                        }
                        if (_padShown && chips.Count > 1)
                        {
                            PadKey("l2", SubX0, top, bottom, tag);
                            PadKey("r2", SubX1 - TabIconW, top, bottom, tag);
                        }
                    }
                    break;
                case EntryKind.Note:
                    // (C25) a small pale line in its own slot, under the rows it's about
                    TextS(e.Description, SecLabelX, top - 2.6f, 0, Pale, tag, SizeX);
                    break;
                case EntryKind.Button:
                    {
                        Cell(sp.ButtonX0, top, sp.ButtonX1, bottom, e.Label, Orange, sp.Button[s], glows, tag);
                        if (e.Row?.Label == DiscordLabel) DrawDiscordLogo(sp.ButtonX0, top, sp.ButtonX1, bottom, tag); // (RecompMenus.Discord.cs)
                    }
                    break;
                }
                if (off) Dim(from, alpha);
            }
            if (sp.Tabs()) DrawTabs(sp);
            // the scroll bar: the list's length, the thumb the rows in view
            int count = sp.Entries.Count, max = MaxScroll(sp.Entries);
            if (max > 0 && count > 0)
            {
                float barTop = SlotTop(0), barBottom = SlotTop(SecSlots - 1) - SecH, length = barTop - barBottom;
                float thumb = MathF.Max(length * SecSlots / (count + SecSlots - 1), 8);
                float at = barTop - (length - thumb) * sp.Scroll / max;
                Fill(SecBarX0, barTop, SecBarX1, barBottom, 0, 0, 0, 0.43f, sp.TitleItem);
                Fill(SecBarX0, at, SecBarX1, at - thumb, FrameOrange.R, FrameOrange.G, FrameOrange.B, 0.9f, sp.TitleItem);
            }
            // the focused entry's description, under the rows
            string desc = "";
            if (sp.Focus >= 0 && sp.Focus < sp.Entries.Count)
            {
                Entry f = sp.Entries[sp.Focus];
                if (f.Kind == EntryKind.SubTabs) desc = ControlHints.For(sp == _gamePage ? "settingsubtabs" : "subtabs", keys);
                else if (f.Kind == EntryKind.Bind)
                {
                    desc = _captureId == f.Bind ? ControlHints.For("waiting", keys)
                        : f.Description + (sp.Col == 2 ? " " + ControlHints.For("clearcell", keys) : sp.Col == 3 ? " " + ControlHints.For("resetcell", keys) : "");
                }
                else if (f.Row != null)
                {
                    desc = f.Row.Hint;
                    if (HasReset(f) && !keys) desc += " " + ControlHints.For("resetline");
                }
            }
            DescriptionLines(desc, sp.TitleItem);
        }

        // a cell: the setup's value box (dark, orange edge) with its text centred, the lobby glow round it when focused;
        // text that won't fit is drawn smaller
        private void Cell(float x0, float top, float x1, float bottom, string text, (float R, float G, float B) colour, int item,
            Dictionary<int, List<WidgetTri>> glows, int tag)
        {
            Fill(x0, top, x1, bottom, 0, 0, 0, 0.43f, tag);
            Box(x0, top, x1, bottom, tag);
            Glow(glows, item, x0, top, x1, bottom, tag);
            float room = x1 - x0 - 6, width = MathF.Max(_drawMenu!.Font.Measure(text), 1);
            float size = Math.Clamp(room / width, SizeX * 0.8f, SizeM);
            TextS(text, (x0 + x1) / 2, top - (top - bottom - 8 * size) / 2, 2, colour, tag, size);
        }

        // the setup's option arrows (gameoptions arrows_type), small, inside a value box's ends
        private void SmallArrow(bool right, float x, float top, int tag)
        {
            List<WidgetTri> model = Still(@"gameoptions\arrows_type");
            if (model.Count == 0) return;
            float cx = (model.Min(t => MinX(t)) + model.Max(t => MaxX(t))) / 2;
            List<WidgetTri> half = model.Where(t => (MinX(t) + MaxX(t)) / 2 > cx == right).ToList();
            Fit(half, x, top, SecArrowW, SecArrowH, tag, 1);
        }

        // what was drawn since `from`, fainter
        private void Dim(int from, float alpha)
        {
            for (int t = from; t < _out.Count; t++)
            {
                WidgetTri tri = _out[t];
                tri.A.A *= alpha;
                tri.B.A *= alpha;
                tri.C.A *= alpha;
                _out[t] = tri;
            }
        }

        // a description in up to three lines under the rows (the panel's foot)
        private void DescriptionLines(string text, int tag)
        {
            if (text.Length == 0) return;
            const float size = 0.66f, pitch = 6.6f;
            var lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && _drawMenu!.Font.Measure(next) * size > SecDescW)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
            for (int i = 0; i < lines.Count && i < 3; i++)
            {
                TextS(lines[i], 128, SecDescTop - pitch * i, 2, Pale, tag, size);
            }
        }
    }
}
