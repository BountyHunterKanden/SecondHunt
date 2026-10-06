using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MphRead; // SfxId (Metadata/SoundMeta.cs) -- not MphRead.Formats.Sound or MphRead.Sound, neither of which has it

// The whole front end as one object a platform drives: the ROM's menu graph (MenuEngine) ticking at the game's rate,
// the game-code parts the data only names (callbacks, backgrounds, the tint cycle), and the layout that puts both DS
// screens on one screen of any shape. Each frame the platform calls Update(seconds), Build(width, height) and draws
// DrawList; input comes in as Press / Navigate / TouchCanvas. Anything that leaves the menus (starting Adventure...)
// comes out through Request.
namespace MphRecomp.Frontend
{
    public enum FrontendRequestKind { StartAdventure, PlayMovie, OpenDeveloperMenu, Resume, QuitToMenu, Credits, ShareCrashLog, Multiplayer, ControlsDone }

    public readonly record struct FrontendRequest(FrontendRequestKind Kind, int Slot = -1, string? Arg = null);

    public sealed class FrontendSession : IMenuHost
    {
        public MenuEngine Menu { get; }
        public UiTextureCache Textures { get; }
        public UiDrawList DrawList { get; }
        public Action<FrontendRequest>? Request { get; set; }
        public Action<string>? Log { get; set; }
        public long Frame { get; private set; }

        private readonly string _root;
        private readonly List<WidgetTri> _tris = new();
        private readonly List<WidgetTri> _grooveTris = new();
        private MenuLayout _layout = MenuLayout.Overlay();
        private int _layoutPage = -1;
        private double _clock;
        private int _hostGoTo = -1, _hostDelay;
        private int _slot;
        // BEGIN GAME's real callback id (page 14's "BEGIN GAME" text item, see FindBeginGameCall in Load): read from
        // this ROM's own data rather than assumed -- USA rev 1 is confirmed to renumber file select's callbacks
        // relative to USA rev 0, so this one isn't hardcoded as a literal switch case either
        private readonly int _beginGameCall;
        private readonly int[] _background = { -1, -1 }; // main1 (top) / main2 (bottom) texture ids

        // the recomp's own screens under Options (RECOMP SETTINGS, MODS) and the settings they edit
        public RecompMenus Recomp { get; }
        public Config.RecompSettings Settings => Recomp.Settings;

        // the vanilla file select's game code (files A/B/C from the campaign's saves)
        public FileSelect Files { get; }

        // OPTIONS > AUDIO's game code (volumes, speakers, the sound test, our music quality row); null if this ROM's page
        // 22 isn't the layout it knows
        public AudioOptions? Audio { get; }

        // the game's message box (COPY / DELETE questions, SAVING TO THE GAME CARD...); null without the frontend2d art.
        // While it's up it takes every key and touch.
        public MenuPopup? Popup { get; }

        private FrontendSession(string root, MenuEngine menu, UiTextureCache textures, RecompMenus recomp, FileSelect files, int beginGameCall,
            MenuPopup? popup, AudioOptions? audio)
        {
            _root = root;
            Files = files;
            Audio = audio;
            if (audio != null) audio.Log = message => Log?.Invoke(message);
            Popup = popup;
            Menu = menu;
            Textures = textures;
            DrawList = new UiDrawList(textures);
            Recomp = recomp;
            _beginGameCall = beginGameCall;
            menu.Host = this;
            recomp.Requested += kind => Request?.Invoke(new FrontendRequest(kind));
            menu.Redirect = page => Paused && (page == 21 || page == Recomp.ModsPage) ? Recomp.PausePage : page;
            files.Popup = popup;
            files.Log = message => Log?.Invoke(message);
            if (popup != null) popup.Sound = (id, why) => OnSound(Menu, id, why);
        }

        private bool PopupOpen => Popup != null && Popup.IsOpen;

        // ---- the in-game pause menu ----

        // While paused the session is an overlay on the running game: the PAUSED list, RECOMP SETTINGS and MOD
        // SETTINGS, drawn without the menu backgrounds or the logo over a dimmed game. Resume / quit come back through
        // Request (Resume, QuitToMenu).
        public bool Paused { get; private set; }

        public void OpenPause()
        {
            Paused = true;
            _bootTicks = -1;
            Menu.Enter(Recomp.PausePage);
        }

        public void ClosePause() => Paused = false;

        // the control customizer on its own over the game (the ship's OPTIONS): its back raises ControlsDone
        public void OpenControls(bool keys)
        {
            Paused = true;
            _bootTicks = -1;
            Recomp.OpenControls(Menu, keys);
        }

        // settingsPath: the recomp settings file (null = defaults, nothing saved); modsDir: where installed mods live
        // savesDir: the campaign's save folder (CampaignSaves; files A/B/C = slots 1/2/3), null = every file empty
        public static FrontendSession Load(string fileSystemRoot, string lang = "en", string? settingsPath = null, string? modsDir = null,
            string? savesDir = null)
        {
            MenuFile file = MenuFile.Load(Path.Combine(fileSystemRoot, "frontend", "metroidhunters.bin"));
            var textures = new UiTextureCache();
            MenuStrings strings = MenuStrings.Load(fileSystemRoot, lang);
            // Options' RUMBLE PAK / SHOW MY STATS values: the game writes its own ON/OFF text over these placeholders
            // (found by text -- the string ids differ between revisions)
            int off = IndexOf(strings, "OFF");
            if (off >= 0)
            {
                foreach (string placeholder in new[] { "rumb", "priv" })
                {
                    int id = IndexOf(strings, placeholder);
                    if (id >= 0) strings.Fill(id, strings[off]);
                }
            }
            var recomp = new RecompMenus(file, strings, Config.RecompSettings.Load(settingsPath), settingsPath, modsDir);
            try
            {
                recomp.Install();
            }
            catch (Exception ex)
            {
                // a ROM whose menus we can't build on: the game's own menus still work, without MODS / RECOMP
                Console.Error.WriteLine("recomp menus not installed: " + ex.Message);
            }
            AudioOptions? audio = AudioOptions.Install(file, strings, fileSystemRoot, recomp.Settings, recomp.SaveSettings);
            var engine = new MenuEngine(file, strings, new MenuWidgets(file, textures), new MenuFont(textures));
            engine.Sounds = MenuSounds.Load(fileSystemRoot); // the button sounds, per page and item, from this ROM
            // USA rev 1's file select list is out of step with its page: give it rev 0's sounds (owner, 2026-10-03)
            if (engine.Sounds.RealignFileSelect(file)) Console.WriteLine("menu sounds: file select list realigned (rev 1 order)");
            var files = new FileSelect(savesDir);
            if (!files.Bind(file, strings)) Console.Error.WriteLine("file select: this ROM's page 24 isn't the expected layout");
            else if (files.CopyCall < 0) Console.Error.WriteLine("file select: no COPY / DELETE buttons found on page 24");
            return new FrontendSession(fileSystemRoot, engine, textures, recomp, files, FindBeginGameCall(file, strings),
                MenuPopup.Load(fileSystemRoot, textures), audio);
        }

        private static int IndexOf(MenuStrings strings, string text)
        {
            for (int i = 0; i < strings.RomCount; i++)
            {
                if (strings[i] == text) return i;
            }
            return -1;
        }

        // BEGIN GAME's callback id: page 14's "BEGIN GAME" text item fires it once its fade-out finishes (found by
        // its text, like the rest of the file select pipeline is found by content -- see FileSelect.Bind). Falls back
        // to the USA rev 0 value (14, also the case label below relied on before this) if the text isn't there.
        private static int FindBeginGameCall(MenuFile file, MenuStrings strings)
        {
            if (file.Pages.Count <= FileSelect.BeginGamePage) return 14;
            foreach (MenuItem it in file.Pages[FileSelect.BeginGamePage].Items)
            {
                bool isBeginGame = false;
                foreach (MenuItemState s in it.States)
                {
                    if (s.Text != null && strings[s.Text.StringId] == "BEGIN GAME") { isBeginGame = true; break; }
                }
                if (!isBeginGame) continue;
                foreach (MenuAction a in it.Actions)
                {
                    if (a.Calls.Count > 0) return a.Calls[0].B;
                }
            }
            return 14;
        }

        // Booting at page 0 starts like the game: the screens are held fully white (master brightness) for 28 ticks,
        // then the white fades off over 15 while the white Nintendo screen begins (measured on the real game: white
        // until display frame ~62, clear by ~92).
        private const int BootWhiteHold = 28, BootWhiteFade = 15;
        private int _bootTicks = -1;

        // The logo pages' load pauses: the game makes the next logo page current (page state 0) and draws nothing while
        // its models load, then starts it -- the page music (DRONE, from P2) keeps running, so without these the logos
        // drift ahead of it (the owner: the music "ends late" against the logos). BizHawk USA rev 1 from power-on
        // (2026-10-01), DS frames in state 0: P2 17 (P1's ESRB fade-out, which our engine plays before entering P2), P3 4,
        // P4 7, P5 10. In ticks, set so each page starts as long after the music as vanilla's (our page bodies run a tick
        // or two longer than vanilla's state 1): P2 +0.267 s, P3 +8.06 (8.040), P4 +14.54 (14.542), P5 +21.06 (21.078),
        // the movie +29.285 (29.285).
        private static readonly Dictionary<int, int> LogoLoadHold = new() { [2] = 1, [3] = 2, [4] = 2, [5] = 3 };
        private bool _bootLogos;
        private int _loadHold;

        // one menu tick = two DS frames (59.8261 Hz: 560190 of the 33513982 Hz bus clock each), not exactly 1/30 s: the
        // logos' 29 s ran 0.08 s short of the DS (and of their music) at 30.000 Hz
        private const double TickSeconds = 2 * 560190 / 33513982.0;

        public void Start(int page = 0)
        {
            if (page == 0)
            {
                _bootTicks = 0;
                _bootLogos = true;
                return;
            }
            Menu.Enter(page);
        }

        // Advance by real time at the menu's fixed rate; returns the ticks run (capped so a stall doesn't fast-forward).
        public int Update(double seconds)
        {
            _clock += seconds;
            int ticks = 0;
            double step = TickSeconds;
            while (_clock >= step && ticks < 8)
            {
                _clock -= step;
                Tick();
                ticks++;
            }
            if (ticks == 8) _clock = 0;
            return ticks;
        }

        public void Tick()
        {
            Frame++;
            if (_bootTicks >= 0)
            {
                _bootTicks++;
                if (_bootTicks == BootWhiteHold) Menu.Enter(0);
                if (_bootTicks >= BootWhiteHold + BootWhiteFade) _bootTicks = -1;
                if (Menu.Page == null) return;
            }
            if (_loadHold > 0)
            {
                _loadHold--;
                return;
            }
            Menu.Tick();
            Popup?.Tick();
            Recomp.Tick();
            TickGrooves();
            if (_hostGoTo != -1 && --_hostDelay <= 0)
            {
                int page = _hostGoTo;
                _hostGoTo = -1;
                Menu.GoTo(page);
            }
        }

        private void GoToLater(int page, int frames)
        {
            _hostGoTo = page;
            _hostDelay = frames;
        }

        // ---- input ----

        // the focus frame shows once the player uses buttons, and goes away when they touch the screen
        private bool _showFocus;

        public void Press(MenuKeys keys)
        {
            _showFocus = true;
            if (Recomp.CaptureDevice != null) return; // the control customizer is waiting: the host sends it the press
            if (PopupOpen)
            {
                Popup!.Press(keys);
                return;
            }
            // "create a new game in this slot?": its check / cross are touch buttons in the data; A takes the check
            if (keys == MenuKeys.A && Menu.Page == Menu.File.Pages[FileSelect.Page] && Files.DialogOpen(Menu))
            {
                // the game's A here is the check button's own action, so its sound (CREATE_CONFIRM_SCR on both revs)
                int confirm = CreateConfirmSound();
                if (Files.CreateFile(Menu, Menu.Strings))
                {
                    Log?.Invoke($"file select: created file {(char)('A' + Files.FocusedSlot)}");
                    if (confirm != 0) OnSound(Menu, confirm, "create file (A)");
                }
                return;
            }
            Menu.Press(keys);
        }

        public void Navigate(int dx, int dy)
        {
            _showFocus = true;
            if (Recomp.CaptureDevice != null) return;
            if (PopupOpen)
            {
                Popup!.Navigate(dx);
                return;
            }
            Menu.Direction(dx, dy);
        }

        // a touch at canvas pixel (x, y) for the canvas last passed to Build
        public void TouchCanvas(float x, float y)
        {
            if (_placements == null) return;
            if (Recomp.CaptureDevice != null)
            {
                Recomp.CancelCapture(); // a touch while the customizer waits: keep the buttons as they are
                return;
            }
            if (PopupOpen)
            {
                int region = _layout.RegionOf(PopupCentreX, PopupCentreY);
                if (region >= 0)
                {
                    (float mx, float my) = _placements[region].ToMenu(x, y);
                    Popup!.Touch(mx, -my);
                }
                return;
            }
            for (int i = 0; i < _placements.Length; i++)
            {
                (float mx, float my) = _placements[i].ToMenu(x, y);
                if (_layout.Regions[i].Contains(mx, my))
                {
                    _showFocus = false;
                    Menu.Touch(mx, my + 192);
                    return;
                }
            }
        }

        // a touch at DS touch-screen pixel (x, y), y down, as the DS reads it (tools and tests: no layout involved)
        public void TouchDs(float x, float y)
        {
            _showFocus = false;
            if (PopupOpen) Popup!.Touch(x, y);
            else Menu.Touch(x, 192 - y);
        }

        // ---- game callbacks the data names (inferred per page from the data + the real game, 2026-09-29) ----

        public bool OnCall(MenuEngine menu, MenuAction action, int a, int b, int item)
        {
            if (menu.Page?.Index == AudioOptions.Page && Audio != null && Audio.OnCall(menu, b)) return true;
            if (Recomp.OnCall(menu, b, Paused)) return true;
            // file select: a slot's highlight was started or took focus. Checked ahead of the switch below because
            // these ids are read per-ROM in FileSelect.Bind, not fixed literals a switch case could name (USA rev 1
            // renumbers them relative to USA rev 0 -- confirmed on device: rev 1 sends 76/77 where rev 0 sends 78/79).
            int startSlot = Files.SlotOfStart(b);
            if (startSlot >= 0)
            {
                _slot = startSlot;
                return true;
            }
            int focusSlot = Files.SlotOfFocus(b);
            if (focusSlot >= 0)
            {
                Files.OnFocused(menu, menu.Strings, focusSlot);
                return true;
            }
            // file select COPY / DELETE (ids read per ROM too): the game's question box, then the change
            if (b == Files.CopyCall && Files.CopyCall >= 0)
            {
                Files.Copy(menu, menu.Strings);
                return true;
            }
            if (b == Files.DeleteCall && Files.DeleteCall >= 0)
            {
                Files.Delete(menu, menu.Strings);
                return true;
            }
            if (b == _beginGameCall) // BEGIN GAME: leave the menus (id read from the data too, see FindBeginGameCall)
            {
                Log?.Invoke($"request StartAdventure slot {_slot}"); // so a device log shows the request actually fired
                Request?.Invoke(new FrontendRequest(FrontendRequestKind.StartAdventure, _slot));
                return true;
            }
            switch (b)
            {
            case 7: // the white Nintendo screen has finished coming in: on to the ESRB notice
                Menu.GoTo(1);
                return true;
            case 8: // logos done or skipped: the intro movie, then the title -- straight in, no exit animation: the
                    // movie covers the change, and the title starts its own entrance as soon as the movie ends
                Request?.Invoke(new FrontendRequest(FrontendRequestKind.PlayMovie, Arg: "01"));
                Menu.Enter(13);
                return true;
            case 9: // (logo texts: their item/state change is the generic one every action has)
            case 10:
                return true;
            case 11: // credits skipped
            case 12: // credits over
                Menu.GoTo(21);
                return true;
            case 13: // touch to start / back to the title: the page change is in the data
                return true;
            case 15: // Adventure picked on the main menu (the data opens file select)
            case 17: // back out of a submenu (the data has the page change)
                return true;
            case 44: // Options' SAVE: keep the options and go back to the main menu
                Menu.GoTo(18);
                return true;
            case 46: // Options' CREDITS: the credits pages (6..12 run themselves; the end goes back to Options)
                Menu.GoTo(6);
                return true;
            case 16: // Multiplayer picked: the app's own lobby (MatchLobbyActivity) for now, in place of the ROM's
                     // multiplayer pages (page 25 on); its sound is the item's own, played with the action
                Request?.Invoke(new FrontendRequest(FrontendRequestKind.Multiplayer));
                return true;
            case 78: // file select: a file's highlight was chosen (the companion call alongside start/focus above)
                return true;
            case 85: // B while "create a new game in this slot?" is up
                Files.CloseDialog(menu);
                return true;
            case 86: // YES, create it: vanilla writes the new file here (SAVING TO THE GAME CARD...) and the file select
            case 87: // then shows it as a used file at 0%; starting it later runs the new-game intro
            case 88: // (its sound, CREATE_CONFIRM_SCR, is the check item's own: the engine played it with the action)
                if (Files.CreateFile(menu, menu.Strings))
                {
                    Log?.Invoke($"file select: created file {(char)('A' + Files.FocusedSlot)}");
                }
                return true;
            }
            return false;
        }

        // the main menu's OPTIONS page (rumble, stats upload, audio / controls / credits): part of the menu graph's fixed
        // layout, like FileSelect.Page
        public const int OptionsPage = 21;

        public void OnPageEntered(MenuEngine menu, int page)
        {
            Log?.Invoke($"page {page}");
            Audio?.OnPageEntered(menu, page);
            if (_bootLogos)
            {
                if (LogoLoadHold.TryGetValue(page, out int hold)) _loadHold = hold;
                if (page > 5) _bootLogos = false; // past the logos (the movie's title page, or a skip)
            }
            if (page != FileSelect.Page) Popup?.Close();
            if (page == FileSelect.Page) Files.OnEnter(menu, menu.Strings);
            if (page == OptionsPage)
            {
                // no Rumble Pak here: the rumble row in its greyed State5 look, as the game sets it when its slot-2 check
                // finds none (BizHawk USA rev 1: the row's label, value and arrows are state 5 from the page's first
                // frame and a touch on them does nothing) -- the only items of the page with a State5 look
                for (int i = 0; i < menu.Page!.Items.Count; i++)
                {
                    if (menu.Page.Items[i].GetState((int)MenuState.State5) != null) menu.SetState(i, MenuState.State5);
                }
            }
            HideTrademarkText(menu);
            Recomp.OnPageEntered(menu, page);
        }

        // USA rev 1 moved the logo's "®" out of the logo texture into its own item (toplogoR) and added a "TM" text item
        // right before it, on all 17 pages with the logo; the game then hides the "TM" wherever the "®" is: every frame,
        // ov0 0x2124ed0 runs for four (toplogoR, text before it) item pairs (calls at 0x2124c80..0x2124ca4) and hides the
        // second if the first is on the current page (BizHawk USA rev 1, 2026-10-03: the "TM" item is Hidden from each
        // page's first frame, only the ® shows). No link or action ever shows it again, so hiding it as the page comes in
        // is the same. Rev 0 has neither item (its ® is drawn in the logo texture), so nothing is hidden there.
        private static void HideTrademarkText(MenuEngine menu)
        {
            IReadOnlyList<MenuItem> items = menu.Page!.Items;
            for (int i = 1; i < items.Count; i++)
            {
                if (!items[i - 1].IsText) continue;
                foreach (MenuItemState s in items[i].States)
                {
                    if (s.WidgetIndex >= 0 && menu.File.Widgets[s.WidgetIndex].ModelPath
                        .EndsWith("\\toplogoR_Model.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        menu.SetState(i - 1, MenuState.Hidden);
                        break;
                    }
                }
            }
        }

        public void OnLog(string message) => Log?.Invoke(message);

        // ---- audio (the platform supplies the backend; set PlaySound to actually hear these) ----

        // sfxId is a raw MphRead SfxId, already flagged (0x4000) for a script id where the ROM uses one (e.g.
        // TOUCH_TO_START_SCR) -- the same convention MphRead.Sound.Sfx's own dispatch uses.
        public Action<int>? PlaySound;

        // The menu theme should be playing: past the boot/logo sequence and the intro movie's page (13 is the
        // title / TOUCH TO START screen, the first page with the theme in the real game) and not mid-boot (Page is
        // null before the first Enter). The host silences it separately while the intro movie ("01") is showing.
        public bool WantMenuMusic => Menu.Page != null && Menu.Page.Index >= 13;

        // The engine picks every button sound the way the game does (MenuEngine + MenuSounds: the ROM's own per-item
        // sounds, else TOUCH_NEUTRAL for focus moves and directions, TOUCH_BACK_SCR for B, the title's page sound for
        // A) -- checked against BizHawk on USA rev 1, 2026-10-01. MENU_CURSOR / MENU_CONFIRM / MENU_CANCEL are never
        // played by the front end.
        public void OnSound(MenuEngine menu, int sfxId, string why)
        {
            Log?.Invoke($"ui sound: {why} (page {menu.Page?.Index}) -> sfx 0x{sfxId:X}, PlaySound {(PlaySound == null ? "unset" : "set")}");
            PlaySound?.Invoke(sfxId);
        }

        // the open create dialog's check button sound: the visible item whose A action creates the file (calls 86-88,
        // the same ids OnCall handles) -- read from the data rather than assumed
        private int CreateConfirmSound()
        {
            MenuPage page = Menu.Page!;
            for (int i = 0; i < page.Items.Count; i++)
            {
                if (Menu.ItemState(i) == MenuState.Hidden) continue;
                foreach (MenuAction a in page.Items[i].Actions)
                {
                    if ((a.Kind & (ushort)MenuKeys.A) == 0) continue;
                    foreach ((int _, int b) in a.Calls)
                    {
                        if (b is 86 or 87 or 88) return Menu.Sounds.Item(page.Index, page.Items[i].Index).Sound;
                    }
                }
            }
            return (int)SfxId.CREATE_CONFIRM_SCR;
        }

        // ---- drawing ----

        private Placement[]? _placements;
        private string? _layoutSetting;
        private bool _layoutPaused;

        // (?) the game hides the Wi-Fi / wireless signal icons unless connected
        private bool Hidden(int item)
        {
            MenuItemState? s = Menu.Page?.Items[item].GetState(Menu.ItemCode(item));
            if (s == null) return false;
            if (Paused && s.Text != null && Menu.Page!.Items[item].Y >= 300) return true; // the logo's rev-1 "TM"
            if (s.WidgetIndex < 0) return false;
            string path = Menu.File.Widgets[s.WidgetIndex].ModelPath;
            if (Paused && (path.EndsWith("toplogo_Model.bin", StringComparison.Ordinal) || path.EndsWith("toplogoR_Model.bin", StringComparison.Ordinal)))
            {
                return true; // no logo over the game
            }
            return path.StartsWith(@"main menu\wifi", StringComparison.Ordinal) || path.StartsWith(@"main menu\wireless", StringComparison.Ordinal);
        }

        public void Build(float width, float height)
        {
            DrawList.Clear();
            MenuPage? page = Menu.Page;
            if (page == null)
            {
                if (_bootTicks >= 0) DrawList.Quad(-1, 0, 0, width, height, 0, 0, 0, 0, 1, 1, 1, 1);
                return;
            }
            string layoutSetting = Settings.MenuLayout;
            if (_layoutPage != page.Index || _layoutSetting != layoutSetting || _layoutPaused != Paused)
            {
                _layout = Paused ? MenuLayout.MainFamily() : MenuLayout.For(Menu.File, page, dual: layoutSetting == "sidebyside");
                _layoutPage = page.Index;
                _layoutSetting = layoutSetting;
                _layoutPaused = Paused;
            }
            _placements = _layout.Resolve(width, height);
            DrawBackground(page.Index, width, height);
            DrawGrooves(page.Index, width, height);

            _tris.Clear();
            Menu.Collect(_tris);
            AddFocusFrame();
            // Each item goes wholly to the region holding the centre of its bounds, so no widget or label is cut in two
            // -- except a model drawn across BOTH screens (the boot logos' oval-on-top + swirl-below), whose triangles
            // each go to their own screen's region. A triangle that itself covers a whole DS screen (a backdrop, flash
            // or fade) covers the whole canvas instead.
            var itemRegion = new Dictionary<int, int>();
            var bounds = new Dictionary<int, (float X0, float Y0, float X1, float Y1)>();
            foreach (WidgetTri t in _tris)
            {
                var tb = Bounds(t);
                bounds[t.Item] = bounds.TryGetValue(t.Item, out var b)
                    ? (MathF.Min(b.X0, tb.X0), MathF.Min(b.Y0, tb.Y0), MathF.Max(b.X1, tb.X1), MathF.Max(b.Y1, tb.Y1))
                    : tb;
            }
            // a plain-colour item covering a whole screen or both (the fade into a game, the white flash) covers the
            // canvas, whatever its triangles look like
            var flatItems = new HashSet<int>(bounds.Keys);
            foreach (WidgetTri t in _tris)
            {
                if (!IsFlatFill(t)) flatItems.Remove(t.Item);
            }
            foreach ((int item, var b) in bounds)
            {
                itemRegion[item] = Hidden(item) ? -1
                    : flatItems.Contains(item) && IsFullScreen(b) ? FullScreenItem
                    : b.Y1 - b.Y0 > 200 ? PerTriangle
                    : _layout.RegionOf((b.X0 + b.X1) / 2, (b.Y0 + b.Y1) / 2);
            }
            // the title's TOUCH TO START bar end to end across the canvas, as it spans the DS touch screen (always, owner
            // 2026-10-04: no "touch bar" setting any more); the side-by-side layout draws whole DS screens, where it
            // already does
            var wideBars = new HashSet<int>();
            if (_layout.Kind == LayoutKind.MainFamily)
            {
                foreach (int item in bounds.Keys)
                {
                    if (Menu.ItemModelPath(item).Contains("touch_bg", StringComparison.OrdinalIgnoreCase)) wideBars.Add(item);
                }
            }
            foreach (WidgetTri t in _tris)
            {
                int region = itemRegion[t.Item];
                if (region == -1) continue;
                if (region == FullScreenItem)
                {
                    var ib = bounds[t.Item];
                    DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha,
                        Stretch(ib, t.A, width, height), Stretch(ib, t.B, width, height), Stretch(ib, t.C, width, height));
                    continue;
                }
                var tb = Bounds(t);
                bool fill = IsFlatFill(t);
                if (fill && IsFullScreen(tb))
                {
                    DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha,
                        Stretch(tb, t.A, width, height), Stretch(tb, t.B, width, height), Stretch(tb, t.C, width, height));
                    continue;
                }
                // the logo pages fade whole screens with planes cut into halves: a flat fill spanning a screen's full
                // width or height is part of a screen-sized fill, so its screen maps onto the whole canvas
                if (fill && _layout.Kind is LayoutKind.Overlay or LayoutKind.Stacked && (tb.X1 - tb.X0 >= 250 || tb.Y1 - tb.Y0 >= 185))
                {
                    var screen = (tb.Y0 + tb.Y1) / 2 >= 0 ? (0f, 0f, 256f, 192f) : (0f, -192f, 256f, 0f);
                    DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha,
                        Stretch(screen, t.A, width, height), Stretch(screen, t.B, width, height), Stretch(screen, t.C, width, height));
                    continue;
                }
                if (region == PerTriangle) region = _layout.RegionOf((tb.X0 + tb.X1) / 2, (tb.Y0 + tb.Y1) / 2);
                if (region < 0) continue;
                Placement p = _placements[region];
                if (wideBars.Contains(t.Item) && EmitWideBar(t, p, width)) continue;
                DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, Place(p, t.A), Place(p, t.B), Place(p, t.C));
            }
            if (PopupOpen) DrawPopup(width, height);
            if (_bootTicks >= 0)
            {
                float white = 1 - Math.Clamp((_bootTicks - BootWhiteHold) / (float)BootWhiteFade, 0, 1);
                DrawList.Quad(-1, 0, 0, width, height, 0, 0, 0, 0, 1, 1, 1, white);
            }
        }

        // The message box sits where the touch screen's controls go (its centre picks the region, so it stays whole). The
        // game's 2box layer stipples the rest of the touch screen at 50%; on the one combined screen that's an even
        // half-black over the whole canvas (darkening only the touch screen's scattered parts reads as a patchwork).
        private const float PopupCentreX = 128, PopupCentreY = -88;
        private readonly List<WidgetTri> _popupTris = new();

        private void DrawPopup(float width, float height)
        {
            int region = _layout.RegionOf(PopupCentreX, PopupCentreY);
            if (region < 0 || _placements == null) return;
            DrawList.Quad(-1, 0, 0, width, height, 0, 0, 0, 0, 0, 0, 0, 0.5f);
            _popupTris.Clear();
            Popup!.Collect(_popupTris, Menu.Font);
            Placement p = _placements[region];
            foreach (WidgetTri t in _popupTris)
            {
                DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, Place(p, t.A), Place(p, t.B), Place(p, t.C));
            }
        }

        // A frame in the menus' own orange (the label colour 31,20,8) around the focused item's touch area, pulsing
        // like the game's highlights; part of that item, so the layout moves it with it.
        private void AddFocusFrame()
        {
            if (!_showFocus || !Menu.TryFocusFrame(out float x0, out float y0, out float x1, out float y1, out bool highlighted) || highlighted)
            {
                return;
            }
            int item = Menu.FocusedItem();
            float pulse = 0.55f + 0.45f * MathF.Abs(MathF.Sin(Frame * MathF.PI / 30));
            const float r = 1f, g = 20 / 31f, b = 8 / 31f, t = 1.5f;
            void Bar(float ax, float ay, float bx, float by)
            {
                var p0 = new UiVertex(ax, ay, 0, 0, r, g, b, pulse);
                var p1 = new UiVertex(bx, ay, 0, 0, r, g, b, pulse);
                var p2 = new UiVertex(bx, by, 0, 0, r, g, b, pulse);
                var p3 = new UiVertex(ax, by, 0, 0, r, g, b, pulse);
                _tris.Add(new WidgetTri { A = p0, B = p1, C = p2, Z = 2000, TextureId = -1, Item = item });
                _tris.Add(new WidgetTri { A = p0, B = p2, C = p3, Z = 2000, TextureId = -1, Item = item });
            }
            x0 -= 2; y0 -= 2; x1 += 2; y1 += 2;
            Bar(x0, y1 - t, x1, y1);
            Bar(x0, y0, x1, y0 + t);
            Bar(x0, y0, x0 + t, y1);
            Bar(x1 - t, y0, x1, y1);
        }

        private const int PerTriangle = -2, FullScreenItem = -3;

        // a plain colour (untextured, or a tiny tiling texture like the white flash's 8x8) -- safe to stretch to any
        // shape; real artwork (the ACTIMAGINE credit is a full-screen picture) is placed like everything else
        private bool IsFlatFill(WidgetTri t)
        {
            if (t.TextureId < 0) return true;
            UiTexture tex = Textures[t.TextureId];
            return tex.Width <= 16 && tex.Height <= 16;
        }

        private static (float X0, float Y0, float X1, float Y1) Bounds(WidgetTri t) => (
            MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)),
            MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)), MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));

        private static bool IsFullScreen((float X0, float Y0, float X1, float Y1) b) => b.X1 - b.X0 >= 250 && b.Y1 - b.Y0 >= 185;

        private static UiVertex Stretch((float X0, float Y0, float X1, float Y1) b, UiVertex v, float width, float height)
        {
            v.X = (v.X - b.X0) / (b.X1 - b.X0) * width;
            v.Y = (b.Y1 - v.Y) / (b.Y1 - b.Y0) * height;
            return v;
        }

        // The TOUCH TO START bar (touchtostart_2/touch_bg: one 256-wide quad, its striped ends in the texture) cut where
        // the ends meet the plain middle, WideBarCap menu pixels in from each side: the ends keep their size at the
        // canvas's left and right edges, the middle stretches between them. The cut is in menu space, so the bar's own
        // in/out animations keep working. False (draw it as usual) when the canvas is no wider than the bar.
        private const float WideBarCap = 32;

        private bool EmitWideBar(WidgetTri t, Placement p, float width)
        {
            float s = p.Scale, a = WideBarCap, b = 256 - WideBarCap;
            float mid = (width - 2 * a * s) / (b - a);
            if (mid <= s) return false;
            UiVertex Map(UiVertex v)
            {
                float x = v.X <= a ? v.X * s : v.X >= b ? width - (256 - v.X) * s : a * s + (v.X - a) * mid;
                v = Place(p, v);
                v.X = x;
                return v;
            }
            foreach ((float lo, float hi) in new[] { (float.NegativeInfinity, a), (a, b), (b, float.PositiveInfinity) })
            {
                List<UiVertex> poly = ClipX(ClipX(new List<UiVertex> { t.A, t.B, t.C }, lo, keepAbove: true), hi, keepAbove: false);
                for (int i = 1; i + 1 < poly.Count; i++)
                {
                    DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, Map(poly[0]), Map(poly[i]), Map(poly[i + 1]));
                }
            }
            return true;
        }

        // the part of a polygon at x >= edge (keepAbove) or x <= edge, its corners interpolated where it is cut
        private static List<UiVertex> ClipX(List<UiVertex> poly, float edge, bool keepAbove)
        {
            if (float.IsInfinity(edge)) return poly;
            var output = new List<UiVertex>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                UiVertex v = poly[i], w = poly[(i + 1) % poly.Count];
                bool vIn = keepAbove ? v.X >= edge : v.X <= edge, wIn = keepAbove ? w.X >= edge : w.X <= edge;
                if (vIn) output.Add(v);
                if (vIn != wIn)
                {
                    float f = (edge - v.X) / (w.X - v.X);
                    output.Add(new UiVertex(edge, v.Y + (w.Y - v.Y) * f, v.U + (w.U - v.U) * f, v.V + (w.V - v.V) * f,
                        v.R + (w.R - v.R) * f, v.G + (w.G - v.G) * f, v.B + (w.B - v.B) * f, v.A + (w.A - v.A) * f));
                }
            }
            return output;
        }

        private static UiVertex Place(Placement p, UiVertex v)
        {
            (float x, float y) = p.ToCanvas(v.X, v.Y);
            v.X = x;
            v.Y = y;
            return v;
        }

        // ---- backgrounds ----

        // The menus behind the logo sit on the game's own Samus art (sourceimages/bg/main1 = top screen, main2 = touch
        // screen) under a translucent colour that cycles green -> teal -> orange. The cycle was measured on the real
        // game: 1020 display frames (510 menu ticks), three keys 170 ticks apart, blended linearly, drawn at 21/32 over
        // the art.
        private static readonly (float R, float G, float B)[] TintKeys = { (0, 31, 0), (4, 16, 16), (19, 9, 6) };
        private const int TintKeyFrames = 170;
        private const float TintWeight = 21f / 32;

        private long _tintStart = -1; // the cycle starts (green) when the backdrop first shows, as on the real title

        public (float R, float G, float B) Tint()
        {
            if (_tintStart < 0) _tintStart = Frame;
            long f = (Frame - _tintStart) % (TintKeyFrames * TintKeys.Length);
            int k = (int)(f / TintKeyFrames);
            float t = f % TintKeyFrames / (float)TintKeyFrames;
            var c0 = TintKeys[k];
            var c1 = TintKeys[(k + 1) % TintKeys.Length];
            return ((c0.R + (c1.R - c0.R) * t) / 31, (c0.G + (c1.G - c0.G) * t) / 31, (c0.B + (c1.B - c0.B) * t) / 31);
        }

        // The HD backdrop (MenuSamus, rendered on the device from the user's imported suit): the two halves of one picture
        // in place of main1 / main2 while RECOMP SETTINGS "menu art" is HD; the DS art until it is ready or when it is off
        private readonly int[] _hdBackground = { -1, -1 };
        private int _hdVersion;

        public bool HasHdBackdrop => _hdBackground[0] != -1;

        // rgba: the picture (two DS screens tall, top row first); the top half is the top screen
        public void SetHdBackdrop(int width, int height, byte[] rgba)
        {
            int half = height / 2, rowBytes = width * 4;
            _hdVersion++;
            for (int i = 0; i < 2; i++)
            {
                byte[] part = new byte[rowBytes * half];
                Buffer.BlockCopy(rgba, i * half * rowBytes, part, 0, part.Length);
                _hdBackground[i] = Textures.GetOrAdd($"bg/hd_samus_{_hdVersion}_{i}", () => (width, half, part)).Id;
            }
        }

        private int BackgroundTexture(int which)
        {
            if (Settings.MenuArtHd && _hdBackground[which] != -1) return _hdBackground[which];
            if (_background[which] != -1) return _background[which];
            string name = which == 0 ? "main1" : "main2";
            _background[which] = Textures.GetOrAdd("bg/" + name, () => LoadBitmap(Path.Combine(_root, "sourceimages", "bg", name + ".bin"))).Id;
            return _background[which];
        }

        // sourceimages/bg: a 256-colour RGB555 palette followed by a 256x192 8-bit image
        public static (int Width, int Height, byte[] Rgba) LoadBitmap(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            byte[] rgba = new byte[256 * 192 * 4];
            for (int i = 0; i < 256 * 192; i++)
            {
                ushort c = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data[512 + i] * 2));
                rgba[i * 4] = (byte)((c & 31) * 255 / 31);
                rgba[i * 4 + 1] = (byte)(((c >> 5) & 31) * 255 / 31);
                rgba[i * 4 + 2] = (byte)(((c >> 10) & 31) * 255 / 31);
                rgba[i * 4 + 3] = 255;
            }
            return (256, 192, rgba);
        }

        private void DrawBackground(int page, float width, float height)
        {
            if (Paused)
            {
                DrawList.Quad(-1, 0, 0, width, height, 0, 0, 0, 0, 0, 0, 0, 0.55f); // the game, dimmed
                return;
            }
            if (page <= 12) return; // logos and credits are on black (the white Nintendo screen is its own backdrop)
            int top = BackgroundTexture(0), bottom = BackgroundTexture(1);
            if (_layout.Kind == LayoutKind.MainFamily)
            {
                // the two images are one continuous picture (helmet above chest): cover the canvas with it, top-aligned
                float scale = MathF.Max(width / 256, height / 384);
                float x0 = (width - 256 * scale) / 2;
                DrawList.Quad(top, x0, 0, x0 + 256 * scale, 192 * scale, 0, 0, 1, 1, 1, 1, 1, 1);
                DrawList.Quad(bottom, x0, 192 * scale, x0 + 256 * scale, 384 * scale, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            else if (_placements != null)
            {
                // each DS screen's image under that screen's region
                for (int i = 0; i < _layout.Regions.Count && i < 2; i++)
                {
                    LayoutRegion r = _layout.Regions[i];
                    (float ax, float ay) = _placements[i].ToCanvas(r.X0, r.Y1);
                    (float bx, float by) = _placements[i].ToCanvas(r.X1, r.Y0);
                    DrawList.Quad(r.Y0 >= 0 ? top : bottom, ax, ay, bx, by, 0, 0, 1, 1, 1, 1, 1, 1);
                }
            }
            var (tr, tg, tb) = Tint();
            DrawList.Quad(-1, 0, 0, width, height, 0, 0, 0, 0, tr, tg, tb, TintWeight);
        }

        // ---- the grooves ----

        // Behind every menu page the game's code draws two models of its own (ov0: loaded by 0x212622c, drawn by
        // 0x2125c08), over both screens in menu space: frontend2d/slots, the dark channels, at alpha 30, and
        // frontend2d/lines, the light running through them (its 151-frame Idle animation), at an alpha a page change
        // fades: the counter (0x2124d40) jumps to 10 when a page starts leaving and counts down, starts at 0 once the
        // page has changed and counts up, alpha = counter * 3 (at least 1; 30 before the first page change). The
        // animation steps once a menu tick, twice while that counter runs (0x2124dec / 0x2124e04). Not on the logos or
        // credits (pages <= 12, no backdrop there either) nor pages 49, 50, 65 (0x2125c34..). Owner queue #33.
        private const string SlotsModel = @"_archives\frontend2d\slots_Model.bin", SlotsAnim = @"_archives\frontend2d\slots_Idle_Anim.bin";
        private const string LinesModel = @"_archives\frontend2d\lines_Model.bin", LinesAnim = @"_archives\frontend2d\lines_Idle_Anim.bin";
        private const int GrooveFadeTicks = 10;
        private int _grooveCounter = -1, _grooveFrame;
        private bool _groovesFailed;

        private void TickGrooves()
        {
            if (Menu.Page == null) return;
            bool leaving = Menu.PendingPage != -1;
            if (_grooveCounter < 0) _grooveCounter = leaving ? GrooveFadeTicks : 0;
            else _grooveCounter = leaving ? Math.Max(0, _grooveCounter - 1) : Math.Min(GrooveFadeTicks, _grooveCounter + 1);
            _grooveFrame += 2;
        }

        private void DrawGrooves(int page, float width, float height)
        {
            if (Paused || _groovesFailed || page <= 12 || page is 49 or 50 or 65) return;
            _grooveTris.Clear();
            try
            {
                Menu.Widgets.Evaluate(Menu.Widgets.Instance(SlotsModel, SlotsAnim), 0, loop: true, 30 / 31f, _grooveTris);
                int alpha = _grooveCounter < 0 ? 30 : Math.Clamp(_grooveCounter * 3, 1, 30);
                Menu.Widgets.Evaluate(Menu.Widgets.Instance(LinesModel, LinesAnim), _grooveFrame, loop: true, alpha / 31f, _grooveTris);
            }
            catch (Exception ex)
            {
                _groovesFailed = true; // an older extraction without _archives/frontend2d: the menus go on without them
                Log?.Invoke("menu grooves unavailable: " + ex.Message);
                return;
            }
            if (_layout.Kind == LayoutKind.MainFamily)
            {
                // placed like the backdrop art (DrawBackground): one continuous picture over the canvas, top-aligned
                float scale = MathF.Max(width / 256, height / 384);
                float x0 = (width - 256 * scale) / 2;
                UiVertex At(UiVertex v)
                {
                    v.X = x0 + v.X * scale;
                    v.Y = (192 - v.Y) * scale;
                    return v;
                }
                foreach (WidgetTri t in _grooveTris)
                {
                    DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, At(t.A), At(t.B), At(t.C));
                }
            }
            else if (_placements != null)
            {
                // each DS screen's part under that screen's region, cut at the screen's edges
                for (int i = 0; i < _layout.Regions.Count && i < _placements.Length; i++)
                {
                    LayoutRegion r = _layout.Regions[i];
                    Placement p = _placements[i];
                    foreach (WidgetTri t in _grooveTris)
                    {
                        ClipY(t, r.Y0, r.Y1, (a, b, c) =>
                            DrawList.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, Place(p, a), Place(p, b), Place(p, c)));
                    }
                }
            }
        }

        // the part of a triangle between y0 and y1 (menu space), as triangles
        private static void ClipY(WidgetTri t, float y0, float y1, Action<UiVertex, UiVertex, UiVertex> emit)
        {
            var poly = new List<UiVertex> { t.A, t.B, t.C };
            poly = ClipEdge(poly, v => v.Y - y0);
            poly = ClipEdge(poly, v => y1 - v.Y);
            for (int i = 1; i + 1 < poly.Count; i++) emit(poly[0], poly[i], poly[i + 1]);
        }

        private static List<UiVertex> ClipEdge(List<UiVertex> poly, Func<UiVertex, float> inside)
        {
            var output = new List<UiVertex>();
            for (int i = 0; i < poly.Count; i++)
            {
                UiVertex a = poly[i], b = poly[(i + 1) % poly.Count];
                float da = inside(a), db = inside(b);
                if (da >= 0) output.Add(a);
                if ((da >= 0) != (db >= 0))
                {
                    float k = da / (da - db);
                    output.Add(new UiVertex(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.U + (b.U - a.U) * k, a.V + (b.V - a.V) * k,
                        a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k, a.A + (b.A - a.A) * k));
                }
            }
            return output;
        }
    }
}
