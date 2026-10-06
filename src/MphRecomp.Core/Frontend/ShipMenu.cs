using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MphRead;
using MphRead.Text;
using MphRecomp.Frontend.Redraw;

// The gunship's menu on a planet, as ONE 16:9 screen: the DS top screen's cockpit (the view out of the window into the
// landing room, the planet banner) widened to the canvas, with the touch screen's buttons recomposed into the console
// below the window. The tab pages (weapon select, options, logbook) are the touch screen as the DS shows it on that
// page, slid in over the cockpit from the tab's side.
//
// Everything comes from the ROM. Graphics: shipTop's BG layers (ship_topBG1/2/3 = frame, console, window reticle) and
// banner (screen.bin); shipGrnd's panel (ship_grnd_main), page layer (ship_grnd_lyr2), logbook layer (ship_grnd_log)
// and sprites. Text: stringTables/ShipOnGround and ScanLog by record id (the same in both USA revisions). Layout: the
// game's own code in overlay9_0 (load address 0x02102220), decoded 2026-09-30 -- per-page tile-copy commands, BG
// scrolls, sprite/text tables and touch rectangles, the 37-frame page switch, the popup struct. The touch screen is the
// sub engine: BG0 = panel (scroll -4: screen x = panel x + 52), BG1 = logbook layer, BG2 = pages (512x512, written by
// a copy routine that runs 32 tiles per row, so copy rows 32-63 land in the second 256-px block), BG3 = backdrop.
// Panel-layer pieces are blended as the DS does (piece + 6/16 of what's behind). Text colour N = fontcolors bank N.
//
// Space: units of DS pixels, 192 tall; the width is whatever the canvas aspect gives (341 at 16:9). The frame layers
// are 5-sliced (edges and centre 1:1, the two spans between stretch), so the window widens with the screen. The 3D view
// is the host's: it draws the room full-screen first (the frame's window is see-through).
// Not yet: options and weapon select controls (their pages show; only BACK works), the energy / weapon / octolith
// readouts (the vanilla touch screen shows them on the panel we leave out; the banner covers "location").
namespace MphRecomp.Frontend
{
    public enum ShipRequestKind { ExitShip, LaunchShip }

    public sealed class ShipMenu
    {
        private enum Page { Closed, Main, Confirm, Saving, Saved }
        private enum Choice { Save, Exit, Launch }
        private enum Panel { None, WeaponSelect, Options, Logbook }
        private enum LogState { Categories, List, Entry }

        private readonly string _grnd, _top;
        private readonly UiTextureCache _textures;
        private readonly MenuFont _font;
        private readonly int _frame1, _console, _reticle, _main, _lyr2, _log, _banner;
        private readonly int[] _box = new int[5], _yesNo = new int[5];
        // the flash sprites (image 0 = the lit piece, image 1 is empty): button corners, tab brackets, BACK, the popup's
        // button brackets and the bar under its text
        private readonly int _flashCorner, _flashCornerRed, _flashBracket, _flashBack;
        private readonly int[] _flashYesNo = new int[5], _flashBar = new int[5];
        private readonly int _ok;
        private readonly int[] _progress = new int[9], _sensBar = new int[9];
        private readonly int[,] _logIcons = new int[4, 2];
        private readonly int[] _logArrow = new int[2], _controls = new int[2], _onOff = new int[2];
        private readonly int _sensMarker;
        private readonly int[] _smallIcons = new int[7], _bigIcons = new int[7], _readWeapon = new int[7], _crystal = new int[2];
        private readonly int _etank, _ring;
        // the octolith ring (0x21448e0): crystal sprite positions in panel space, 8x16 each, around the ring art
        private static readonly (int X, int Y)[] CrystalAt = { (168, 8), (183, 15), (194, 30), (183, 47), (168, 52), (153, 47), (142, 30), (153, 15) };
        private readonly DsSprite _colours;

        private Page _page = Page.Closed;
        private Choice _choice;
        private int _focus;          // main: 0 save, 1 exit, 2 launch, 3-5 tabs; popups: 0 yes, 1 no
        private bool _keyFocus;      // the D-pad has been used: show where the focus is
        private int _frame, _pageFrame, _saveProgress;
        private bool _saveOk;
        private float _scale = 1, _w = 256;
        // where the pieces being drawn go: unit = (_tx + x * _ts); the cockpit draws 1:1, a page's card is moved/scaled
        private float _tx, _ty, _ts = 1;
        private readonly (float X, float Y, float W, float H)[] _buttons = new (float, float, float, float)[3];
        private readonly (float X, float Y, float W, float H)[] _tabRects = new (float, float, float, float)[3];
        private (float X, float Y, float W, float H) _yes, _no, _okRect;

        // the open page and the switch: 37 frames each way, the ROM's own step profile
        private Panel _panel;
        private int _slideFrame, _slideDir;
        private static readonly float[] SlideCurve = BuildSlideCurve();

        // options: focused row (0-3 control types, 4 sensitivity, 5 look invert) and the sensitivity drag
        private int _optRow;
        private bool _sensDrag;
        // weapon select: the affinity weapons in slot order (icon k), the D-pad pick, a drag in progress
        private static readonly BeamType[] Affinity =
        {
            BeamType.VoltDriver, BeamType.Battlehammer, BeamType.Imperialist, BeamType.Judicator,
            BeamType.Magmaul, BeamType.ShockCoil, BeamType.OmegaCannon
        };
        private int _weaponSel = -1, _dragSlot = -1;
        private float _dragX, _dragY;

        // logbook
        // the header types itself in (0x21097d4 -> the text typer 0x204b198; LETTER_BLIP 0x1E0, a plain PlaySfx, on a
        // frame the typed cursor moved and the list isn't sliding), measured in BizHawk on USA rev 1:
        // - "SELECT A CATEGORY" (site 0x210993c): a character every 4 frames, a blip for each but the spaces, from 43
        //   frames after the logbook enters (tab touch f20 -> blips f94..f158);
        // - "<CATEGORY>: N%" (sites 0x2109d74 / 0x2109e64): the 2nd character 3 frames after the 1st, then every 4; no
        //   blip on the character after a space, one more blip a step after the last character; from 23 frames after
        //   the first category's touch (f160 -> f183..f214 for "LORE: 0%"), 40-41 after another's (f300 -> f341..)
        // - BACK with a category header up: one more blip 19 frames after the touch (f720 -> f739; none without one)
        private const int LetterBlip = 0x1E0, TypeEvery = 4, TypeAfterOpen = 43, TypeAfterFirstCategory = 24,
            TypeAfterNextCategory = 41, BlipAfterBack = 19;
        private int _backBlipAt = -1;
        private string _typeText = "";
        private int _typeShown, _typeNext = -1;
        private bool _typeCategory;
        private LogState _logState;
        private LogCategory _logCategory;
        private List<LogEntry> _logEntries = new();
        private int _logTop, _logSel, _logTextPage;

        public UiDrawList DrawList { get; }
        public bool IsOpen => _page != Page.Closed;
        public string PlanetName { get; set; } = "";
        // the story save the pages read (logbook bits, weapons); the host sets it
        public StorySave? Story { get; set; }
        // saves the game; true if it worked (SAVING... then SAVE COMPLETE, or the ROM's "could not be written")
        public Func<bool>? SaveGame;
        public Action<ShipRequestKind>? Request;
        // LAUNCH SHIP's YES locks the menu (the popup stays up) while the host runs the take-off; Fade = the host's fade
        // to black over the whole screen (0..1)
        public bool Locked { get; private set; }
        public float Fade { get; set; }
        public Action<string>? Log;
        // ROM sounds (MphRead SfxId values, 0x4000 = a script) as overlay9_0 plays them (read from the code, traced on
        // BizHawk with USA 1.1, 2026-10-01), all on touch-down. The DS has ONE free-script slot: every script and every
        // popup YES/NO/OK sample is preceded by StopFreeSfxScripts(1), which cuts the previous script short.
        public Action<int>? PlaySound;
        public Action? StopScripts;
        // a looping sample (OPTIONS_SENSITIVITY_LOOP, the logbook's scroll loops): start -> handle (-1 none), stop, and
        // its pitch in DS units (0x2000 = x1; a host without per-handle pitch leaves it null)
        public Func<int, int>? PlayLoop;
        public Action<int>? StopLoop;
        public Action<int, int>? SetLoopPitch;
        private void Sound(SfxId id) => PlaySound?.Invoke((int)id);
        // StopFreeSfxScripts(1), then the sound
        private void SlotSound(SfxId id)
        {
            StopScripts?.Invoke();
            Sound(id);
        }
        // PC previews: a texture drawn full-screen where the host would draw the 3D view (-1: none, it's the host's)
        public int PreviewTexture { get; set; } = -1;
        // the window reticle redrawn as clean geometry (PixelRedraw) at the screen's own scale; off = the DS pixels
        public bool RedrawArt { get; set; } = true;
        private DsImage? _reticleSrc;
        private Task<DsImage>? _reticleJob;
        private float _reticleJobScale;
        private int _reticleHi = -1;
        public bool RedrawReady => !RedrawArt || _reticleHi >= 0;
        // options page state (display only for now): 0 stylus right, 1 stylus left, 2 dual right, 3 dual left
        public int ControlType { get; set; }
        // OPTIONS without the DS's four control types (owner 2026-10-05; ClassicControls brings them back in dev builds):
        // the control customizer's button in their column -- two on a PC (KeyboardControls: controller, keyboard & mouse)
        // -- drawn with the control-type box and font; A or a tap opens the customizer over the ship (OpenControls(keys))
        public bool ClassicControls { get; set; }
        public bool KeyboardControls { get; set; }
        public Action<bool>? OpenControls;
        private int ControlButtons => KeyboardControls ? 2 : 1;
        // a customizer button's slot in the boxes' column (0..3, 29 apart; the panel art has the four dark slots): from
        // the top, under the column's title
        private static float ControlSlot(int i) => i;
        // option rows in use: the control types 0-3 (classic) or the customizer buttons, then 4 sensitivity, 5 invert
        // (not the mouse's: it has none)
        private bool OptionRowUsed(int row) => row == 4 || (row == 5 ? !MouseTab : ClassicControls ? row <= 3 : row < ControlButtons);
        public int Sensitivity { get; set; } = 8; // 1..15
        public bool LookInvert { get; set; }
        // a PC mouse's own sensitivity (OPTIONS > CONTROLS' KEYBOARD & MOUSE column): on a PC the customizer buttons are
        // tabs -- the lit one (the control-type box's "on" look) is the device the slider shows and changes; LOOK INVERT
        // is the controller's only (owner 2026-10-05: no mouse invert), hidden on the mouse's tab
        public int MouseSensitivity { get; set; } = 8;
        private int _controlDevice; // 0 the controller, 1 the keyboard & mouse
        private bool MouseTab => !ClassicControls && KeyboardControls && _controlDevice == 1;
        private int OptSensitivity
        {
            get => MouseTab ? MouseSensitivity : Sensitivity;
            set { if (MouseTab) MouseSensitivity = value; else Sensitivity = value; }
        }
        // the options page changed ControlType / Sensitivity / LookInvert (the host saves them)
        public Action? OptionsChanged;
        // weapon select equipped a weapon into Story.WeaponSlots[2]
        public Action? WeaponsChanged;

        // ---------------------------------------------------------------- ROM layout (overlay9_0)

        // the console buttons, cut from ship_grnd_main (layer pixels) with their labels (text record position relative
        // to the piece, align, wrap, palette): SAVE GAME 0x2144d00, EXIT SHIP 0x2144d0c, LAUNCH SHIP 0x2144d18
        private static readonly (int X, int Y, int W, int H)[] ButtonArt = { (9, 2, 84, 39), (10, 143, 82, 38), (98, 121, 100, 62) };
        private static readonly (int Id, float X, float Y, int Align, int Wrap, int Pal)[] ButtonText =
        {
            (1, 41, 11, 2, 40, 6), (2, 41, 10, 2, 40, 0), (3, 94, 7, 1, 60, 4)
        };
        private const float ButtonGap = 10, ConsoleTop = 128, PopupTop = 112;

        // The flash a touched control plays before it acts (overlay9_0, traced on BizHawk with USA 1.1, 2026-10-01): the
        // sprite anim (1, 32, 32) (0x203c170 / 0x203c118: tick 0..31, then stopped) on an ani_* sprite of 8 frames x 4
        // ticks alternating image 0 (the lit piece) and image 1 (empty), stepped once a frame from the touch's frame on:
        // lit on ticks 1-3, 8-11, 16-19, 24-27. The action comes on the frame after tick 31 (popup / page enter on frame
        // T+31, a slide's first step on T+32); the sound is played at the touch. All flashes are lime (sprite bank 0, the
        // popup's own bank for its pieces) except the tab brackets and LAUNCH's corners, red like its popup (bank 4;
        // BizHawk rev 1, 2026-10-03: SAVE / EXIT lime, LAUNCH red).
        private const int FlashTicks = 31;
        private static bool FlashLit(int tick) => tick >= 1 && tick <= FlashTicks && tick / 4 % 2 == 0;
        // SAVE / EXIT / LAUNCH: ani_corners (32x32, its L in the top-left) x4 -- TL, TR h-flipped, BR h+v, BL v-flipped --
        // in button-art pixels (main draw 0x210dae8; screen table 0x2144e3a minus the art's screen origin, panel x + 52)
        private static readonly (int X, int Y)[][] CornerAt =
        {
            new[] { (3, 3), (48, 3), (48, 4), (3, 4) },
            new[] { (2, 2), (48, 2), (48, 4), (2, 4) },
            new[] { (4, 4), (64, 4), (64, 26), (4, 26) },
        };
        // the tabs: ani_brackets (32x8) x4 in tab-piece pixels -- (x, -2) v-flipped, (x + dx, -2) h+v, (x + dx, 23)
        // h-flipped, (x, 23) -- (0x210dc58; screen positions 0x2144e6a). Drawn with OBJ palette 2 (set at 0x210f498),
        // which the game shows as RGB (60, 113, 134) (measured): the file's bank 2 (90, 238, 255) tinted to it
        private static readonly (int X, int Dx)[] BracketAt = { (1, 10), (0, 10), (7, 16) };
        private const float BracketR = 60 / 90f, BracketG = 113 / 238f, BracketB = 134 / 255f;
        // BACK: ani_back (16x8) on the page's touch screen: weapon (222, 101), options (222, 17), logbook (12, 45)
        // h-flipped (page descriptors +0x38 / +0x2e / +0x26; draws 0x210cb78 / 0x210bb38 / 0x2109b4c)
        private static readonly (int X, int Y, bool Flip)[] BackFlashAt = { (222, 101, false), (222, 17, false), (12, 45, true) };
        private enum Flash { None, Button, Tab, Back, Yes, No, Ok }
        private Flash _flash;
        private int _flashTick, _flashOf;
        private Action? _flashAction;
        // a popup's opening blink (popup enter 0x2108c58: the bar + its button brackets); its input waits until it ends
        // (+0x25: the draw of tick 31 frees it, so a touch on the frame after is taken)
        private int _openTick = int.MaxValue;
        private bool PopupOpening => _openTick < FlashTicks;
        // boarding: the game asks "do you want to SAVE GAME?" (P009) by itself, centred, with no corner flash (see Open)
        private bool _autoAsk;
        private int _autoAskIn;

        // a tile copy into BG2 (0x21444b4.., 14-byte commands): dst tile, size, src tile in ship_grnd_lyr2
        private readonly record struct Copy(int DstX, int DstY, int W, int H, int SrcX, int SrcY);
        private readonly record struct PageText(char Type, int Id, float X, float Y, int Align, int Wrap, int Pal);

        private sealed class PageDef
        {
            public float ScrollX, ScrollY;          // BG2 at rest on this page
            public Copy[] Copies = Array.Empty<Copy>();
            public float TabX, TabY;                // where the page's own tab piece shows on it (screen)
            public (int X, int Y, int W, int H) Back; // BACK touch rect (screen)
            public PageText[] Texts = Array.Empty<PageText>(); // positions in BG2 space (screen = pos - scroll)
        }

        // the three tabs: the lyr2 piece (src px) and the label's place in it (text records 0x2144d48.. with the
        // panel's +4)
        private static readonly (int SrcX, int SrcY, int W, int Id, float LabelX, float LabelY)[] Tabs =
        {
            (200, 152, 48, 9, 22, 7),   // WEAPON SELECT
            (200, 264, 48, 10, 22, 12), // OPTIONS
            (0, 584, 56, 11, 32, 12)    // LOGBOOK
        };

        private static readonly PageDef[] Pages =
        {
            new PageDef(),
            new PageDef   // weapon select (0x2144bf4)
            {
                ScrollX = 0, ScrollY = 0, TabX = 200, TabY = 144, Back = (192, 80, 56, 32),
                Copies = new[] { new Copy(0, 0, 31, 24, 0, 1), new Copy(0, 28, 31, 4, 0, 33), new Copy(18, 53, 7, 5, 0, 73), new Copy(24, 10, 7, 8, 1, 96) },
                Texts = new[] { new PageText('M', 8, 199, 90, 0, 0, 0) }
            },
            new PageDef   // options (0x2144adc)
            {
                ScrollX = 0, ScrollY = 76, TabX = 200, TabY = 36, Back = (192, 0, 56, 32),
                Copies = new[] { new Copy(0, 14, 31, 19, 0, 33), new Copy(0, 1, 31, 9, 0, 16), new Copy(18, 39, 7, 5, 0, 73), new Copy(24, 9, 7, 5, 9, 96) },
                Texts = new[]
                {
                    new PageText('O', 1, 50, 122, 2, 80, 2), new PageText('O', 2, 134, 122, 2, 0, 2), new PageText('O', 9, 140, 224, 2, 0, 2),
                    new PageText('M', 8, 199, 82, 0, 0, 0)
                }
            },
            new PageDef   // logbook (0x21455d0)
            {
                ScrollX = 200, ScrollY = 0, TabX = 0, TabY = 72, Back = (0, 24, 56, 32),
                Copies = new[] { new Copy(0, 33, 25, 22, 7, 65), new Copy(0, 6, 6, 5, 25, 19), new Copy(0, 16, 6, 5, 25, 33), new Copy(25, 9, 7, 6, 0, 73), new Copy(25, 3, 7, 6, 17, 96) },
                Texts = new[] { new PageText('M', 8, 252, 34, 1, 0, 0) }
            },
        };

        // the page switch (0x210bbb8 / 0x210cc08 / 0x2109bcc): BG2 moves 5 px a frame (+2 every 3rd) for 30 frames,
        // 5 (+1 every 4th) for 4, then 3 for 3 -- 200 px in 37 frames; the card follows the same curve
        private static float[] BuildSlideCurve()
        {
            var steps = new List<int>();
            for (int f = 1; f <= 37; f++)
            {
                steps.Add(f <= 30 ? 5 + (f % 3 == 0 ? 2 : 0) : f <= 34 ? 5 + (f % 4 == 0 ? 1 : 0) : 3);
            }
            float total = 0;
            foreach (int s in steps) total += s;
            var curve = new float[steps.Count + 1];
            float sum = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                sum += steps[i];
                curve[i + 1] = sum / total;
            }
            return curve;
        }

        // the logbook (0x214523c rects, 0x2145480 sprites, 0x2144f98 text; log layer BG1 at HOFS -48, VOFS 32 for the
        // entry list and -64 for an entry)
        private static readonly (float X, float Y)[] LogIconAt = { (73, 30), (108, 30), (143, 30), (202, 30) };
        private const int LogRows = 5;
        private const float LogRowX = 70, LogRowY = 86, LogRowPitch = 15;

        public ShipMenu(string fileSystemRoot, UiTextureCache textures)
        {
            _grnd = Path.Combine(fileSystemRoot, "_archives", "shipGrnd");
            _top = Path.Combine(fileSystemRoot, "_archives", "shipTop");
            _textures = textures;
            DrawList = new UiDrawList(textures);
            _font = new MenuFont(textures);
            _frame1 = Bg(_top, "ship_topBG1");
            _console = Bg(_top, "ship_topBG2");
            _reticle = Bg(_top, "ship_topBG3");
            _main = Bg(_grnd, "ship_grnd_main");
            _lyr2 = Bg(_grnd, "ship_grnd_lyr2");
            _log = Bg(_grnd, "ship_grnd_log");
            _banner = Sprite(_top, "screen", 0, 0);
            _colours = DsGraphics.LoadSprite(Path.Combine(_grnd, "fontcolors.bin"));
            foreach (int bank in new[] { 0, 4 })
            {
                _box[bank] = Sprite(_grnd, "ani_textbox", 0, bank);
                _yesNo[bank] = Sprite(_grnd, "ani_yesno", 0, bank);
            }
            _ok = Sprite(_grnd, "ani_ok", 0, 0);
            _flashCorner = Sprite(_grnd, "ani_corners", 0, 0);
            _flashCornerRed = Sprite(_grnd, "ani_corners", 0, 4);
            _flashBracket = Sprite(_grnd, "ani_brackets", 0, 2);
            _flashBack = Sprite(_grnd, "ani_back", 0, 0);
            foreach (int bank in new[] { 0, 4 })
            {
                _flashYesNo[bank] = Sprite(_grnd, "ani_yesno_flash", 0, bank);
                _flashBar[bank] = Sprite(_grnd, "ani_textbox_flash", 0, bank);
            }
            for (int i = 0; i < 9; i++)
            {
                _progress[i] = Sprite(_grnd, "ani_progressbar", i, 0);
                _sensBar[i] = Sprite(_grnd, "options_sens_bar", i, 0);
            }
            string[] icons = { "log_lore", "log_enemy", "log_object", "log_equipment" };
            for (int c = 0; c < 4; c++)
            {
                _logIcons[c, 0] = Sprite(_grnd, icons[c], 0, 0);
                _logIcons[c, 1] = Sprite(_grnd, icons[c], 1, 0);
            }
            for (int i = 0; i < 2; i++)
            {
                _logArrow[i] = Sprite(_grnd, "log_arrow", i, 0);
                _controls[i] = Sprite(_grnd, "options_controls", i, 0);
                _onOff[i] = Sprite(_grnd, "options_onoff", i, 0);
            }
            _sensMarker = Sprite(_grnd, "options_sens_marker", 0, 0);
            _etank = Sprite(_grnd, "save_etank", 0, 0);
            // the octolith ring's art: a circle cut from the panel layer (54 px around layer (124, 39)), without the
            // panel box behind it
            _ring = _textures.GetOrAdd("ship/ring", () =>
            {
                UiTexture m = _textures[_main];
                var rgba = new byte[54 * 54 * 4];
                for (int y = 0; y < 54; y++)
                {
                    for (int x = 0; x < 54; x++)
                    {
                        float dx = x + 0.5f - 27, dy = y + 0.5f - 27;
                        if (dx * dx + dy * dy > 27 * 27) continue;
                        Array.Copy(m.Rgba, ((12 + y) * m.Width + 97 + x) * 4, rgba, (y * 54 + x) * 4, 4);
                    }
                }
                return (54, 54, rgba);
            }).Id;
            _crystal[0] = Sprite(_grnd, "save_crystal", 0, 0);
            _crystal[1] = Sprite(_grnd, "save_crystal", 1, 0);
            for (int i = 0; i < 7; i++)
            {
                _readWeapon[i] = Sprite(_grnd, "save_weapon", i, 0);
                _smallIcons[i] = Sprite(_grnd, "weapon_smallicons", i, 0);
                _bigIcons[i] = Sprite(_grnd, "weapon_bigicons", i, 0);
            }
        }

        private int Bg(string dir, string name) => _textures.GetOrAdd("ship/" + name, () =>
        {
            DsImage im = DsGraphics.LoadBg(Path.Combine(dir, name + ".bin"));
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        private int Sprite(string dir, string name, int image, int bank) => _textures.GetOrAdd($"ship/{name}/{image}/{bank}", () =>
        {
            DsImage im = DsGraphics.LoadSprite(Path.Combine(dir, name + ".bin")).Image(image, bank);
            return (im.Width, im.Height, im.Rgba);
        }).Id;

        private static string S(char type, int id) => Strings.GetMessage(type, id, StringTables.ShipOnGround);

        // ---------------------------------------------------------------- flow

        // askSave: boarding from a planet. The game's ship init (0x21108e4) copies e78fc+0xE bit 0x1000 into 0x2147eb4;
        // the bit is set by every game-mode start (arm9 0x2056610: landing, leaving the ship) and only cleared by the star
        // map's LAND (0x2104910), so in practice every boarding asks -- after a NO or a save too (traced).
        public void Open(string planetName, bool askSave = false)
        {
            PlanetName = planetName;
            _page = Page.Main;
            _panel = Panel.None;
            _slideFrame = _slideDir = 0;
            _focus = 0;
            _keyFocus = false;
            _pageFrame = 0;
            _flash = Flash.None;
            _flashAction = null;
            _openTick = int.MaxValue;
            _autoAsk = false;
            _autoAskIn = askSave ? 2 : 0;
            Locked = false;
            Fade = 0;
        }

        public void Close()
        {
            StopSensLoop();
            StopScroll();
            _flash = Flash.None;
            _flashAction = null;
            _autoAskIn = 0;
            _page = Page.Closed;
        }

        // a control's flash; a new one replaces a running one (the main page and the popups keep taking touches)
        private void StartFlash(Flash kind, int of, Action action)
        {
            _flash = kind;
            _flashOf = of;
            _flashTick = 0;
            _flashAction = action;
        }

        private void OpenPopup(Page page)
        {
            GoTo(page);
            _openTick = 0;
        }

        private void GoTo(Page page)
        {
            _page = page;
            _pageFrame = 0;
            Log?.Invoke($"ship menu: {page}{(page == Page.Confirm ? " " + _choice : "")}");
        }

        // once per displayed frame (60 Hz)
        public void Tick()
        {
            _frame++;
            _pageFrame++;
            if (_slideDir != 0)
            {
                _slideFrame = Math.Clamp(_slideFrame + _slideDir, 0, SlideCurve.Length - 1);
                if (_slideFrame == SlideCurve.Length - 1 && _slideDir > 0) _slideDir = 0;
                if (_slideFrame == 0 && _slideDir < 0)
                {
                    _slideDir = 0;
                    _panel = Panel.None;
                }
            }
            if (_scrollHold)
            {
                ScrollHoldTick();
            }
            if (_backBlipAt >= 0 && _frame >= _backBlipAt)
            {
                _backBlipAt = -1;
                Sound((SfxId)LetterBlip);
            }
            if (_typeNext >= 0 && _frame >= _typeNext && _typeShown <= _typeText.Length)
            {
                int k = _typeShown;
                bool blip = !_typeCategory ? k < _typeText.Length && _typeText[k] != ' ' : k == 0 || _typeText[k - 1] != ' ';
                if (blip && (k < _typeText.Length || _typeCategory)) Sound((SfxId)LetterBlip);
                _typeShown = Math.Min(k + 1, _typeText.Length);
                _typeNext = k >= _typeText.Length || (!_typeCategory && k + 1 >= _typeText.Length) ? -1
                    : _frame + (_typeCategory && k == 0 ? TypeEvery - 1 : TypeEvery);
            }
            if (_autoAskIn > 0)
            {
                // boarding (main process 0x210ea80): its first frame plays SMALL_POP_UP_SCR, the next opens the popup
                if (_autoAskIn == 2)
                {
                    SlotSound(SfxId.SMALL_POP_UP_SCR);
                }
                else
                {
                    _autoAsk = true;
                    _choice = Choice.Save;
                    _focus = 0;
                    OpenPopup(Page.Confirm);
                }
                _autoAskIn--;
            }
            if (_flash != Flash.None && ++_flashTick > FlashTicks)
            {
                Action? action = _flashAction;
                _flash = Flash.None;
                _flashAction = null;
                action?.Invoke();
            }
            if (_page == Page.Saving)
            {
                if (_pageFrame == 2)
                {
                    bool ok;
                    try
                    {
                        ok = SaveGame?.Invoke() ?? false;
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke("ship save: " + ex.Message);
                        ok = false;
                    }
                    _saveOk = ok;
                }
                // the progress counter runs 0..100, one a frame (the DS also waits for the card write)
                _saveProgress = Math.Min(100, _saveProgress + 1);
                if (_saveProgress >= 100 && _pageFrame >= 2)
                {
                    OpenPopup(Page.Saved); // SAVE COMPLETE opens like a popup (action 2 -> 0x2108c58): it blinks too
                }
            }
            if (_openTick <= FlashTicks)
            {
                _openTick++;
            }
        }

        private void Activate()
        {
            switch (_page)
            {
            case Page.Main:
                if (_panel != Panel.None) return;
                if (_focus >= 3)
                {
                    OpenPanel((Panel)(_focus - 2));
                    return;
                }
                // the sound at the touch (0x210f1bc / main process), the button's corners flash, the popup opens on the
                // frame after; the main page keeps taking touches meanwhile (another restarts the flash for itself)
                int button = _focus;
                SlotSound(SfxId.SMALL_POP_UP_SCR);
                StartFlash(Flash.Button, button, () =>
                {
                    _choice = (Choice)button;
                    _autoAsk = false;
                    _focus = 0;
                    OpenPopup(Page.Confirm);
                });
                break;
            case Page.Confirm:
                if (PopupOpening) return;
                if (_focus == 1)
                {
                    Back();
                    return;
                }
                if (_choice == Choice.Save)
                {
                    SlotSound(SfxId.SAVE_GAME_YES_SCR); // popup action 0 (0x2108ab8)
                    StartFlash(Flash.Yes, 0, () =>
                    {
                        _saveProgress = 0;
                        GoTo(Page.Saving);
                    });
                }
                else
                {
                    Choice choice = _choice;
                    // LAUNCH (action 4): its own script (0x2108acc); EXIT (action 3): the plain YES sample
                    SlotSound(choice == Choice.Launch ? SfxId.LAUNCH_SHIP_YES_SCR : SfxId.SMALL_POP_UP_YES);
                    StartFlash(Flash.Yes, 0, () =>
                    {
                        _autoAsk = false;
                        if (choice == Choice.Launch)
                        {
                            // the take-off (0x2109210): the popup stays up, untouchable, until the screen fades out
                            Locked = true;
                        }
                        else
                        {
                            _page = Page.Main;
                            _focus = (int)choice;
                        }
                        Request?.Invoke(choice == Choice.Exit ? ShipRequestKind.ExitShip : ShipRequestKind.LaunchShip);
                    });
                }
                break;
            case Page.Saved:
                if (PopupOpening) return;
                SlotSound(SfxId.SMALL_POP_UP_YES); // OK (0x2108c08)
                StartFlash(Flash.Ok, 0, () =>
                {
                    _autoAsk = false;
                    GoTo(Page.Main);
                    _focus = 0;
                });
                break;
            }
        }

        private void Back()
        {
            if (_page == Page.Confirm)
            {
                if (PopupOpening) return;
                SlotSound(SfxId.SMALL_POP_UP_NO); // NO (0x2108b58)
                Choice choice = _choice;
                StartFlash(Flash.No, 0, () =>
                {
                    _autoAsk = false;
                    GoTo(Page.Main);
                    _focus = (int)choice;
                });
            }
            else if (_page == Page.Saved)
            {
                if (PopupOpening) return;
                SlotSound(SfxId.SMALL_POP_UP_YES); // B stands for OK (the DS popup has no B)
                StartFlash(Flash.Ok, 0, () =>
                {
                    _autoAsk = false;
                    GoTo(Page.Main);
                });
            }
        }

        private void OpenPanel(Panel panel)
        {
            if (_page != Page.Main || _panel != Panel.None) return;
            // tab touched (0x210f054..): the sound now, the tab's brackets flash, the page enters on the frame after
            SlotSound(SfxId.BIG_SLIDE_SCR);
            StartFlash(Flash.Tab, (int)panel - 1, () => EnterPanel(panel));
        }

        private void EnterPanel(Panel panel)
        {
            if (_page != Page.Main || _panel != Panel.None) return;
            _panel = panel;
            _slideFrame = 0;
            _slideDir = 1;
            if (panel == Panel.Logbook)
            {
                _logState = LogState.Categories;
                StartTyping(S('L', 2), TypeAfterOpen); // select a category
            }
            _optRow = ClassicControls ? Math.Clamp(ControlType, 0, 3) : 0;
            _sensDrag = false;
            _dragSlot = -1;
            _weaponSel = -1;
            Log?.Invoke($"ship menu: open {panel}");
        }

        private void ClosePanel()
        {
            if (_panel == Panel.None || _slideDir < 0 || _flash == Flash.Back) return;
            StopSensLoop();
            StopScroll();
            SlotSound(SfxId.BIG_SLIDE_BACK_SCR); // BACK touched (weapon 0x210d098, options 0x210c074, logbook 0x210a0d0)
            _typeNext = -1;
            if (_panel == Panel.Logbook && _logState != LogState.Categories) _backBlipAt = _frame + BlipAfterBack;
            // the BACK piece flashes (the page takes no input meanwhile), the slide back starts on the frame after
            StartFlash(Flash.Back, (int)_panel - 1, () => _slideDir = -1);
            Log?.Invoke($"ship menu: close {_panel}");
        }

        private bool PanelSettled => _panel != Panel.None && _slideDir == 0 && _flash != Flash.Back;

        public void Press(MenuKeys key)
        {
            if (!IsOpen || Locked || _page == Page.Saving) return;
            if (_panel != Panel.None)
            {
                if (!PanelSettled) return;
                if (_panel == Panel.Logbook) LogKey(key);
                else if (key == MenuKeys.B) ClosePanel();
                else if (key == MenuKeys.A && _panel == Panel.Options && _optRow < 4) PickControlRow(_optRow);
                else if (key == MenuKeys.A && _panel == Panel.Options && _optRow == 5 && !MouseTab) SetInvert(!LookInvert);
                else if (key == MenuKeys.A && _panel == Panel.WeaponSelect && _weaponSel >= 0) Equip(_weaponSel);
                return;
            }
            if (key == MenuKeys.A) Activate();
            else if (key == MenuKeys.B) Back();
        }

        public void Navigate(int dx, int dy)
        {
            if (!IsOpen || Locked) return;
            _keyFocus = true;
            if (_panel != Panel.None)
            {
                if (!PanelSettled) return;
                if (_panel == Panel.Logbook) LogNavigate(dx, dy);
                else if (_panel == Panel.Options) OptionsNavigate(dx, dy);
                else if (_panel == Panel.WeaponSelect && dx != 0) WeaponNavigate(dx);
                return;
            }
            if (_page == Page.Main && dx != 0)
            {
                // left to right: weapon select, options, save, exit, launch, logbook
                int[] ring = { 3, 4, 0, 1, 2, 5 };
                int at = Math.Max(0, Array.IndexOf(ring, _focus));
                int next = ring[Math.Clamp(at + dx, 0, ring.Length - 1)];
                // no sound: the DS ship menu is touch-only and has no cursor (MENU_CURSOR is the title menus')
                _focus = next;
            }
            else if (_page == Page.Confirm && dx != 0)
            {
                _focus = dx > 0 ? 1 : 0;
            }
        }

        // a tap at canvas pixels (the last Build's size)
        public void Touch(float x, float y)
        {
            if (!IsOpen || Locked || _scale <= 0) return;
            float ux = x / _scale, uy = y / _scale;
            _keyFocus = false;
            if (_panel != Panel.None)
            {
                if (!PanelSettled) return;
                // into the card's own touch-screen pixels
                (float cx, float cy, float cs) = CardAt(1);
                float px = (ux - cx) / cs, py = (uy - cy) / cs;
                if (Inside(Pages[(int)_panel].Back, px, py))
                {
                    ClosePanel();
                }
                else if (_panel == Panel.Logbook)
                {
                    LogTouch(px, py);
                }
                else if (_panel == Panel.Options)
                {
                    OptionsTouch(px, py);
                }
                else if (_panel == Panel.WeaponSelect)
                {
                    WeaponTouch(px, py);
                }
                return;
            }
            if (_page == Page.Main)
            {
                for (int i = 0; i < _tabRects.Length; i++)
                {
                    if (Inside(_tabRects[i], ux, uy))
                    {
                        OpenPanel((Panel)(i + 1));
                        return;
                    }
                }
                for (int i = 0; i < _buttons.Length; i++)
                {
                    if (Inside(_buttons[i], ux, uy))
                    {
                        _focus = i;
                        Activate();
                        return;
                    }
                }
            }
            else if (_page == Page.Confirm)
            {
                if (Inside(_yes, ux, uy)) { _focus = 0; Activate(); }
                else if (Inside(_no, ux, uy)) { _focus = 1; Activate(); }
            }
            else if (_page == Page.Saved && Inside(_okRect, ux, uy))
            {
                Activate();
            }
        }

        private static bool Inside((float X, float Y, float W, float H) r, float x, float y) => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

        // for the PC comparison against vanilla (Tools -sndtrace): a control's centre in canvas pixels (the last Build's
        // size): SAVE, EXIT, LAUNCH, the tabs WEAPON / OPTIONS / LOGBOOK, a popup's YES / NO / OK; null when it isn't up
        public (float X, float Y)? ControlAt(string name)
        {
            (float X, float Y, float W, float H)? r = name.ToUpperInvariant() switch
            {
                "SAVE" => _buttons[0],
                "EXIT" => _buttons[1],
                "LAUNCH" => _buttons[2],
                "WEAPON" => _tabRects[0],
                "OPTIONS" => _tabRects[1],
                "LOGBOOK" => _tabRects[2],
                "YES" when _page == Page.Confirm => _yes,
                "NO" when _page == Page.Confirm => _no,
                "OK" when _page == Page.Saved => _okRect,
                _ => null
            };
            if (r is not { } rect || rect.W <= 0) return null;
            return ((rect.X + rect.W / 2) * _scale, (rect.Y + rect.H / 2) * _scale);
        }

        // a point on the open page in the DS touch screen's own pixels (the page is the DS one, placed on a card) ->
        // canvas pixels; null with no page open
        public (float X, float Y)? CardToCanvas(float px, float py)
        {
            if (_panel == Panel.None) return null;
            (float cx, float cy, float cs) = CardAt(1);
            return ((cx + px * cs) * _scale, (cy + py * cs) * _scale);
        }

        private (float X, float Y) CardPoint(float x, float y)
        {
            (float cx, float cy, float cs) = CardAt(1);
            return ((x / _scale - cx) / cs, (y / _scale - cy) / cs);
        }

        // a finger moving (sensitivity slider, a dragged weapon)
        public void TouchMove(float x, float y)
        {
            if (!PanelSettled) return;
            (float px, float py) = CardPoint(x, y);
            (_touchX, _touchY) = (px, py);
            if (_panel == Panel.Options && _sensDrag) SetSensitivityFromTouch(py);
            if (_panel == Panel.WeaponSelect && _dragSlot >= 0) (_dragX, _dragY) = (px, py);
        }

        public void TouchUp(float x, float y)
        {
            if (!PanelSettled) return;
            (float px, float py) = CardPoint(x, y);
            StopScroll(); // touch released (0x210ace0)
            if (_sensDrag)
            {
                // released (0x210c364): OFF, and the loop stops
                Sound(SfxId.OPTIONS_SENSITIVITY_OFF);
                StopSensLoop();
            }
            _sensDrag = false;
            if (_panel == Panel.WeaponSelect && _dragSlot >= 0)
            {
                // released (0x210d1a8): only rect 8 (the big slot, 0x2145028) counts, tested at touch + 8; a new weapon
                // there is WEAPON_EQUIPPED, anything else (the same weapon, a miss) WEAPON_DOWN
                if (Inside((44, 86, 48, 32), px + 8, py + 8)) Equip(_dragSlot);
                else Sound(SfxId.WEAPON_DOWN);
                _dragSlot = -1;
            }
        }

        // ---------------------------------------------------------------- options / weapon select

        private void SetOption(Action change)
        {
            change();
            OptionsChanged?.Invoke();
        }

        // 0x2144d78: v = touch y + 68 clamped to 122..180 (180 = 1, 122 = 15)
        private void SetSensitivityFromTouch(float py)
        {
            float v = Math.Clamp(py + 68, 122, 180);
            int level = 1 + (int)MathF.Round((180 - v) * 14 / 58);
            if (level != OptSensitivity) SetOption(() => OptSensitivity = level);
            // the loop's pitch follows the marker every frame (0x210c3b0): 0x1800 at the bottom .. 0x4000 at the top
            if (_sensLoop >= 0) SetLoopPitch?.Invoke(_sensLoop, 0x1800 + (int)((180 - v) * 0x2800 / 58));
        }

        private int _sensLoop = -1;

        private void StopSensLoop()
        {
            if (_sensLoop >= 0) StopLoop?.Invoke(_sensLoop);
            _sensLoop = -1;
        }

        // a box in the control-type column: a control type (classic), else a customizer button
        private void PickControlRow(int i)
        {
            if (ClassicControls)
            {
                PickControl(i);
                return;
            }
            if (i >= ControlButtons) return;
            Sound(SfxId.OPTIONS_CONTROL_TYPE);
            _controlDevice = i;
            OpenControls?.Invoke(i == 1);
        }

        // a control type / look invert box: its sound only when the setting changes (0x210c14c..0x210c2e0)
        private void PickControl(int i)
        {
            if (ControlType != i) Sound(SfxId.OPTIONS_CONTROL_TYPE);
            SetOption(() => ControlType = i);
        }

        private void SetInvert(bool on)
        {
            if (LookInvert != on) Sound(SfxId.OPTIONS_LOOK_INVERT);
            SetOption(() => LookInvert = on);
        }

        private void OptionsTouch(float x, float y)
        {
            for (int i = 0; i < (ClassicControls ? 4 : ControlButtons); i++)
            {
                float slot = ClassicControls ? i : ControlSlot(i);
                if (Inside((16, 68 + 29 * slot, 56, 24), x, y))
                {
                    _optRow = i;
                    if (!ClassicControls && _controlDevice != i)
                    {
                        // another device's tab: lit (the slider and LOOK INVERT show its values); tapped again, it opens
                        Sound(SfxId.OPTIONS_CONTROL_TYPE);
                        _controlDevice = i;
                        return;
                    }
                    PickControlRow(i);
                    return;
                }
            }
            if (!MouseTab && Inside((100, 162, 20, 20), x, y)) { _optRow = 5; SetInvert(true); return; }
            if (!MouseTab && Inside((157, 162, 20, 20), x, y)) { _optRow = 5; SetInvert(false); return; }
            if (Inside((100, 38, 34, 74), x, y))
            {
                _optRow = 4;
                if (!_sensDrag)
                {
                    // grabbed (0x210c328): ON, and OPTIONS_SENSITIVITY_LOOP runs (pitch = the marker) until release
                    Sound(SfxId.OPTIONS_SENSITIVITY_ON);
                    StopSensLoop();
                    _sensLoop = PlayLoop?.Invoke((int)SfxId.OPTIONS_SENSITIVITY_LOOP) ?? -1;
                }
                _sensDrag = true;
                SetSensitivityFromTouch(y);
            }
        }

        private void OptionsNavigate(int dx, int dy)
        {
            if (dy != 0)
            {
                // no cursor sound (touch-only on the DS); rows not in use are stepped over
                int row = _optRow;
                do row -= dy; while (row is >= 0 and <= 5 && !OptionRowUsed(row));
                if (row is >= 0 and <= 5) _optRow = row;
                if (!ClassicControls && _optRow < ControlButtons) _controlDevice = _optRow; // the cursor lights a tab
            }
            else if (_optRow == 4) SetOption(() => OptSensitivity = Math.Clamp(OptSensitivity + dx, 1, 15));
            else if (_optRow == 5 && !MouseTab) SetInvert(dx < 0);
        }

        private bool Owns(int slot) => Story != null && (Story.Weapons & (1 << (int)Affinity[slot])) != 0;

        private void WeaponTouch(float x, float y)
        {
            for (int k = 0; k < Affinity.Length; k++)
            {
                if (Owns(k) && Inside((13 + 25 * k, 142, 24, 24), x, y))
                {
                    _dragSlot = _weaponSel = k;
                    (_dragX, _dragY) = (x, y);
                    Sound(SfxId.WEAPON_UP); // picked up (0x210d130)
                    return;
                }
            }
            // a tap on the big slot / arm (rects 8, 9 at touch + 8) plays NEGATIVE_CLICK (0x210d18c), whose sound-table
            // volume is 0: silent, so nothing is played here
        }

        private void WeaponNavigate(int dx)
        {
            for (int i = 1; i <= Affinity.Length; i++)
            {
                int k = ((_weaponSel < 0 ? (dx > 0 ? -1 : 0) : _weaponSel) + dx * i + Affinity.Length * 2) % Affinity.Length;
                if (Owns(k))
                {
                    _weaponSel = k;
                    return;
                }
            }
        }

        private void Equip(int slot)
        {
            if (Story == null || !Owns(slot)) return;
            bool changed = Story.WeaponSlots[2] != (int)Affinity[slot];
            Story.WeaponSlots[2] = (int)Affinity[slot];
            Sound(changed ? SfxId.WEAPON_EQUIPPED : SfxId.WEAPON_DOWN); // 0x210d294
            Log?.Invoke($"weapon select: {Affinity[slot]}");
            WeaponsChanged?.Invoke();
        }

        // ---------------------------------------------------------------- logbook

        private void SelectCategory(LogCategory c)
        {
            // the category already listed: nothing at all (0x210a10c); from its entry it goes back to the list
            if (c == _logCategory && _logState == LogState.List) return;
            StopScroll();
            _logCategory = c;
            _logEntries = Story == null ? new List<LogEntry>() : ShipLogbook.Entries(Story, c);
            // the first category since the page opened, or another (0x210a13c: category 5 = none yet)
            SlotSound(_logState == LogState.Categories ? SfxId.LOGBOOK_1ST_CATEGORY_SCR : SfxId.LOGBOOK_NEXT_CATEGORY_SCR);
            StartTyping(LogHeader(), _logState == LogState.Categories ? TypeAfterFirstCategory : TypeAfterNextCategory, category: true);
            _logTop = _logSel = 0;
            _logState = LogState.List;
            Log?.Invoke($"logbook: {c}, {_logEntries.Count} entries");
        }

        private void StartTyping(string text, int delay, bool category = false)
        {
            _typeCategory = category;
            _typeText = text;
            _typeShown = 0;
            _typeNext = _frame + delay;
        }

        // how much of a header is typed so far (all of it once typed, or for a text that isn't the one being typed); the
        // typed part keeps the whole text's place (a centred header doesn't re-centre as it grows)
        private int Typed(string text) => text == _typeText ? _typeShown : text.Length;

        // "%s: %d%" with the category's name (T001-T004)
        private string LogHeader() => $"{S('T', (int)_logCategory + 1)}: {(Story == null ? 0 : ShipLogbook.Percent(Story, _logCategory))}%";

        private void OpenEntry(int index)
        {
            if (index < 0 || index >= _logEntries.Count) return;
            StopScroll();
            SlotSound(SfxId.CHOOSE_ITEM_SCR); // a row touched (0x210a450)
            _logSel = index;
            _logTextPage = 0;
            _logState = LogState.Entry;
        }

        private void ScrollList(int by)
        {
            _logTop = Math.Clamp(_logTop + by, 0, Math.Max(0, _logEntries.Count - LogRows));
            _logSel = Math.Clamp(_logSel, _logTop, Math.Max(_logTop, Math.Min(_logEntries.Count - 1, _logTop + LogRows - 1)));
        }

        // the scroll arrows (0x214523c rects 6, 5, 7, 8): a press moves one row and starts the arrow's loop; held, the
        // finger's arrow steps again every 3 frames (outer, double arrows) or every 10 (inner) of the hold (0x210a8b0),
        // the loop running on. It stops when the finger lifts or leaves the arrows (0x210ace0) or the list can't move
        // (then a press is silent too); another arrow under the finger switches to its loop.
        private static readonly (float X, float Y, float W, float H, int Dir, int Every, SfxId Loop)[] LogArrows =
        {
            (215, 80, 25, 16, -1, 3, SfxId.FAST_SCROLL_UP_LOOP),
            (215, 96, 25, 24, -1, 10, SfxId.SLOW_SCROLL_UP_LOOP),
            (215, 120, 25, 24, 1, 10, SfxId.SLOW_SCROLL_DOWN_LOOP),
            (215, 144, 25, 16, 1, 3, SfxId.FAST_SCROLL_DOWN_LOOP),
        };
        private bool _scrollHold;
        private int _scrollFrames, _scrollLoop = -1, _scrollLoopArrow = -1;
        private float _touchX, _touchY;

        private int ArrowAt(float x, float y)
        {
            for (int i = 0; i < LogArrows.Length; i++)
            {
                var a = LogArrows[i];
                if (Inside((a.X, a.Y, a.W, a.H), x, y)) return i;
            }
            return -1;
        }

        private void ScrollStep(int arrow)
        {
            int top = _logTop + LogArrows[arrow].Dir;
            if (top < 0 || top > Math.Max(0, _logEntries.Count - LogRows))
            {
                StopScrollLoop(); // at the end: silent (0x210a4b8 / 0x210a9a0)
                return;
            }
            ScrollList(LogArrows[arrow].Dir);
            if (_scrollLoopArrow != arrow)
            {
                StopScrollLoop();
                _scrollLoop = PlayLoop?.Invoke((int)LogArrows[arrow].Loop) ?? -1;
                _scrollLoopArrow = arrow;
            }
        }

        private void ScrollHoldTick()
        {
            if (_panel != Panel.Logbook || _logState != LogState.List)
            {
                StopScroll();
                return;
            }
            _scrollFrames++;
            int arrow = ArrowAt(_touchX, _touchY);
            if (arrow < 0)
            {
                StopScroll();
                return;
            }
            int every = LogArrows[arrow].Every;
            bool step = _scrollFrames % 10 == 0 ? every == 10 : every == 3 && _scrollFrames % 3 == 0;
            if (step) ScrollStep(arrow);
        }

        private void StopScrollLoop()
        {
            if (_scrollLoop >= 0) StopLoop?.Invoke(_scrollLoop);
            _scrollLoop = -1;
            _scrollLoopArrow = -1;
        }

        private void StopScroll()
        {
            _scrollHold = false;
            StopScrollLoop();
        }

        private List<string> EntryLines() => _logSel < _logEntries.Count ? _font.Lines(_logEntries[_logSel].Text, 170) : new List<string>();
        private int EntryPages() => Math.Max(1, (EntryLines().Count + 2) / 3);

        private void TurnEntryPage(int by)
        {
            int page = _logTextPage + by;
            if (page >= 0 && page < EntryPages())
            {
                Sound(SfxId.ITEM_ARROW); // next / prev page of the entry (0x210a7d4 / 0x210a85c)
                _logTextPage = page;
            }
            else if (by > 0 && _logSel + 1 < _logEntries.Count)
            {
                OpenEntry(_logSel + 1);
            }
            else if (by < 0 && _logSel > 0)
            {
                OpenEntry(_logSel - 1);
            }
        }

        private void LogTouch(float x, float y)
        {
            for (int c = 0; c < 4; c++)
            {
                if (Inside((LogIconAt[c].X, LogIconAt[c].Y, 32, 32), x, y))
                {
                    SelectCategory((LogCategory)c);
                    return;
                }
            }
            if (_logState == LogState.List)
            {
                int arrow = ArrowAt(x, y);
                if (arrow >= 0)
                {
                    StopScroll();
                    (_touchX, _touchY) = (x, y);
                    _scrollHold = true;
                    _scrollFrames = 0;
                    ScrollStep(arrow);
                    return;
                }
                for (int i = 0; i < LogRows; i++)
                {
                    if (Inside((67, LogRowY + i * LogRowPitch - 2, 142, 12), x, y))
                    {
                        OpenEntry(_logTop + i);
                        return;
                    }
                }
            }
            else if (_logState == LogState.Entry)
            {
                if (Inside((80, 146, 24, 16), x, y)) TurnEntryPage(-1);
                else if (Inside((200, 146, 24, 16), x, y)) TurnEntryPage(1);
                else if (Inside((134, 146, 30, 16), x, y)) EntryBack();
            }
        }

        // the entry's back button (rect 11, 0x210a8a0)
        private void EntryBack()
        {
            SlotSound(SfxId.ITEM_BACK_SCR);
            _logState = LogState.List;
        }

        private void LogKey(MenuKeys key)
        {
            if (key == MenuKeys.B)
            {
                if (_logState == LogState.Entry) EntryBack();
                else if (_logState == LogState.List) _logState = LogState.Categories;
                else ClosePanel();
            }
            else if (key == MenuKeys.A)
            {
                if (_logState == LogState.Categories) SelectCategory(_logCategory);
                else if (_logState == LogState.List) OpenEntry(_logSel);
                else TurnEntryPage(1);
            }
        }

        private void LogNavigate(int dx, int dy)
        {
            if (dx != 0)
            {
                if (_logState == LogState.Entry) TurnEntryPage(dx);
                else SelectCategory((LogCategory)(((int)_logCategory + dx + 4) % 4));
            }
            else if (dy != 0 && _logState == LogState.List && _logEntries.Count > 0)
            {
                _logSel = Math.Clamp(_logSel - dy, 0, _logEntries.Count - 1);
                if (_logSel < _logTop) _logTop = _logSel;
                if (_logSel >= _logTop + LogRows) _logTop = _logSel - LogRows + 1;
            }
        }

        // ---------------------------------------------------------------- drawing

        public void Build(int width, int height)
        {
            DrawList.Clear();
            _scale = height / 192f;
            _w = width / _scale;
            if (!IsOpen) return;
            bool popup = _page != Page.Main;
            float t = _panel == Panel.None ? 0 : SlideCurve[_slideFrame], keep = 1 - t;
            if (PreviewTexture >= 0)
            {
                Quad(PreviewTexture, 0, 0, _w, 192, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            // the cockpit: frame (roof, pillars, sill) over the 3D view, the window reticle, the console below
            Sliced(_frame1, 0, ConsoleTop, 1);
            Sliced(RedrawArt ? Reticle() : _reticle, 0, ConsoleTop, 1);
            Sliced(_console, ConsoleTop, 192, 1);
            // the planet banner in the roof (screen.bin and its mirror)
            float cx = _w / 2;
            Quad(_banner, cx - 64, 4, cx, 36, 0, 0, 1, 1, 1, 1, 1, 1);
            Quad(_banner, cx, 4, cx + 64, 36, 1, 0, 0, 1, 1, 1, 1, 1);
            Text(PlanetName, cx, 15, 2, 0, 0);
            DrawReadouts(cx, keep);
            // the tabs at the window's edges (on the touch screen: (0,32), (0,112), (200,56)); they slide off while a page
            // is open, except the page's own, which rides on it
            float away = 64 * t;
            _tabRects[0] = (0, 44, 48, 40);
            _tabRects[1] = (0, 84, 48, 40);
            _tabRects[2] = (_w - 56, 44, 56, 40);
            for (int i = 0; i < 3; i++)
            {
                if ((int)_panel == i + 1) continue;
                float tx = _tabRects[i].X + (i == 2 ? away : -away), ty = _tabRects[i].Y;
                Tab(i, tx, ty);
                if (_flash == Flash.Tab && _flashOf == i && FlashLit(_flashTick)) Brackets(i, tx, ty);
            }
            // the buttons in a row on the console
            float total = ButtonGap * (ButtonArt.Length - 1);
            foreach (var a in ButtonArt) total += a.W;
            float s = Math.Min(1, (_w - 12) / total);
            float x = cx - total * s / 2;
            for (int i = 0; i < ButtonArt.Length; i++)
            {
                var a = ButtonArt[i];
                float y = ConsoleTop + (192 - ConsoleTop - a.H * s) / 2;
                _buttons[i] = (x, y, a.W * s, a.H * s);
                BlendedLayer(_main, x, y, a.W * s, a.H * s, a.X, a.Y, a.W, a.H, keep);
                var tx = ButtonText[i];
                Text(S('M', tx.Id), x + tx.X * s, y + tx.Y * s, tx.Align, tx.Wrap, tx.Pal, keep, 12, s);
                if (_flash == Flash.Button && _flashOf == i && FlashLit(_flashTick)) Corners(i, x, y, s);
                x += (a.W + ButtonGap) * s;
            }
            if (!popup && _keyFocus && _panel == Panel.None)
            {
                FocusFrame(_focus < 3 ? _buttons[_focus] : _tabRects[_focus - 3], _focus == 2 ? 4 : _focus >= 3 ? 2 : 0);
            }
            if (_panel != Panel.None)
            {
                // the cockpit steps back and the page comes in over it
                Quad(-1, 0, 0, _w, 192, 0, 0, 0, 0, 0, 0, 0, 0.5f * t);
                DrawCard(t);
            }
            if (popup)
            {
                Quad(-1, 0, 0, _w, 192, 0, 0, 0, 0, 0, 0, 0, 0.55f);
            }
            // the boarding question is centred (the DS puts it at (128, 56), SAVE GAME's over its button at (103, 12))
            var saveAt = _autoAsk ? (_w / 2 - 1, 0f, 2f, 1f) : _buttons[0];
            switch (_page)
            {
            case Page.Confirm:
                Popup(_choice == Choice.Save ? saveAt : _buttons[(int)_choice], _choice == Choice.Launch ? 4 : 0,
                    S('P', _choice switch { Choice.Save => _autoAsk ? 9 : 1, Choice.Exit => 4, _ => 5 }), mode: 0);
                break;
            case Page.Saving:
                Popup(saveAt, 0, S('P', 2), mode: 2);
                break;
            case Page.Saved:
                Popup(saveAt, 0, _saveOk ? S('P', 3) : S('E', 1), mode: 1);
                break;
            }
            if (Fade > 0)
            {
                Quad(-1, 0, 0, _w, 192, 0, 0, 0, 0, 0, 0, 0, Math.Min(1, Fade));
            }
        }

        // a quad of a 1-image sprite with flips
        private void FlipQuad(int tex, float x, float y, float w, float h, bool hflip, bool vflip, float r = 1, float g = 1, float b = 1)
        {
            Quad(tex, x, y, x + w, y + h, hflip ? 1 : 0, vflip ? 1 : 0, hflip ? 0 : 1, vflip ? 0 : 1, r, g, b, 1);
        }

        // SAVE / EXIT / LAUNCH's corners (CornerAt), at the button art drawn at (x, y) scaled s
        private void Corners(int i, float x, float y, float s)
        {
            for (int k = 0; k < 4; k++)
            {
                (int cx, int cy) = CornerAt[i][k];
                FlipQuad(i == 2 ? _flashCornerRed : _flashCorner, x + cx * s, y + cy * s, 32 * s, 32 * s, k == 1 || k == 2, k >= 2);
            }
        }

        // a tab's brackets (BracketAt), at the tab piece drawn at (x, y)
        private void Brackets(int i, float x, float y)
        {
            (int bx, int dx) = BracketAt[i];
            FlipQuad(_flashBracket, x + bx, y - 2, 32, 8, false, true, BracketR, BracketG, BracketB);
            FlipQuad(_flashBracket, x + bx + dx, y - 2, 32, 8, true, true, BracketR, BracketG, BracketB);
            FlipQuad(_flashBracket, x + bx + dx, y + 23, 32, 8, true, false, BracketR, BracketG, BracketB);
            FlipQuad(_flashBracket, x + bx, y + 23, 32, 8, false, false, BracketR, BracketG, BracketB);
        }

        // The touch screen's status readouts (energy tanks (74+9i, 54), owned weapons packed at 19 px (61+19k, 73), the
        // octolith ring with its 8 crystals), moved into the roof either side of the banner: left = energy + weapons,
        // right = the ring (its art cut from ship_grnd_main, scaled to the roof). Lit crystal = found octolith.
        private void DrawReadouts(float cx, float alpha)
        {
            StorySave? story = Story;
            if (story == null || alpha <= 0) return;
            // as vanilla's main page: the tanks in a row under ENERGY, the weapon icons in a row under WEAPONS, both fitted
            // to the roof left of the banner (cx - 64); beside the label a full set of weapons ran into the banner (#34)
            float left = Math.Max(4, cx - 64 - 108), room = cx - 64 - 3 - (left + 2);
            int tanks = Math.Max(0, story.HealthMax / Metadata.PlayerValues[0].EnergyTank);
            Text(S('M', 5), left, 4, 0, 0, 1, alpha, 8);
            float ts = tanks > 1 ? Math.Clamp((room - 8) / (tanks - 1), 1, 9) : 9;
            for (int i = 0; i < tanks; i++)
            {
                float x = left + 2 + ts * i;
                Quad(_etank, x, 13, x + 8, 21, 0, 0, 1, 1, 1, 1, 1, alpha);
            }
            Text(S('M', 6), left, 23, 0, 0, 1, alpha, 8);
            int owned = 0;
            for (int k = 0; k < Affinity.Length; k++)
            {
                if ((story.Weapons & (1 << (int)Affinity[k])) != 0) owned++;
            }
            float ws = owned > 1 ? Math.Clamp((room - 12) / (owned - 1), 1, 13) : 13, wsize = Math.Min(12, ws);
            for (int k = 0, n = 0; k < Affinity.Length; k++)
            {
                if ((story.Weapons & (1 << (int)Affinity[k])) == 0) continue;
                float x = left + 2 + ws * n++;
                Quad(_readWeapon[k], x, 32 + (12 - wsize) / 2, x + wsize, 32 + (12 + wsize) / 2, 0, 0, 1, 1, 1, 1, 1, alpha);
            }
            // the ring: art (panel layer around (124, 39), 54 px) and crystals, at 0.6 so it fits the roof
            const float rs = 0.6f;
            float rx = Math.Min(_w - 4 - 54 * rs, cx + 64 + 30), ry = 2;
            Quad(_ring, rx, ry, rx + 54 * rs, ry + 54 * rs, 0, 0, 1, 1, 1, 1, 1, alpha);
            int found = story.CountFoundOctoliths();
            for (int i = 0; i < 8; i++)
            {
                // crystal positions are panel space (screen - 4); the ring's centre is panel (172, 39) = layer (124, 39),
                // which is (27, 27) of the art cut
                float x = rx + (27 + CrystalAt[i].X - 172) * rs, y = ry + (27 + CrystalAt[i].Y - 39) * rs;
                Quad(_crystal[i < found ? 0 : 1], x, y, x + 8 * rs, y + 16 * rs, 0, 0, 1, 1, 1, 1, 1, alpha);
            }
        }

        // The redrawn reticle: traced once on a worker thread at the canvas scale (about half a second), the DS pixels
        // until it's ready. Linear filtering ("bg/" key): it's drawn at about 1:1, stretched only by the 5-slice.
        private int Reticle()
        {
            float scale = MathF.Ceiling(_scale * 4) / 4;
            if (_reticleJob == null || _reticleJobScale != scale)
            {
                _reticleSrc ??= DsGraphics.LoadBg(Path.Combine(_top, "ship_topBG3.bin"));
                DsImage src = _reticleSrc;
                _reticleJobScale = scale;
                _reticleHi = -1;
                _reticleJob = Task.Run(() => PixelRedraw.Redraw(src, scale));
            }
            if (_reticleHi < 0 && _reticleJob.IsCompletedSuccessfully)
            {
                DsImage hi = _reticleJob.Result;
                _reticleHi = _textures.GetOrAdd($"bg/redraw/ship_topBG3@{scale}", () => (hi.Width, hi.Height, hi.Rgba)).Id;
                Log?.Invoke($"ship menu: reticle redrawn at {scale}x ({hi.Width}x{hi.Height})");
            }
            else if (_reticleJob.IsFaulted && _reticleHi < 0)
            {
                Log?.Invoke("ship menu: reticle redraw failed: " + _reticleJob.Exception?.GetBaseException().Message);
                _reticleHi = _reticle; // keep the pixels from now on
            }
            return _reticleHi >= 0 ? _reticleHi : _reticle;
        }

        // a tab: its lyr2 piece and label
        private void Tab(int i, float x, float y)
        {
            var tab = Tabs[i];
            BlendedLayer(_lyr2, x, y, tab.W, 40, tab.SrcX, tab.SrcY, tab.W, 40, 1);
            Text(S('M', tab.Id), x + tab.LabelX, y + tab.LabelY, 2, 40, 2, 1, 9);
        }

        // where the page's card is at progress t: from its tab on the cockpit's tab (1:1) to centred under the banner
        private (float X, float Y, float S) CardAt(float t)
        {
            PageDef d = Pages[(int)_panel];
            (float ex, float ey, _, _) = _tabRects[(int)_panel - 1];
            float x0 = ex - d.TabX, y0 = ey - d.TabY;
            const float s1 = 0.8f;
            float x1 = (_w - 256 * s1) / 2, y1 = 38 + (154 - 192 * s1) / 2;
            return (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, 1 + (s1 - 1) * t);
        }

        private void DrawCard(float t)
        {
            (_tx, _ty, _ts) = CardAt(t);
            UiRect clip = DrawList.Clip;
            DrawList.Clip = new UiRect(_tx * _scale, _ty * _scale, 256 * _ts * _scale, 192 * _ts * _scale);
            PageDef d = Pages[(int)_panel];
            // BG2 at the page's rest scroll: each copy lands at its hardware spot (copy rows 32+ go to the second block)
            foreach (Copy c in d.Copies)
            {
                int block = c.DstY / 32;
                float hx = c.DstX * 8 + ((block & 1) != 0 ? 256 : 0), hy = c.DstY % 32 * 8 + ((block & 2) != 0 ? 256 : 0);
                BlendedLayer(_lyr2, hx - d.ScrollX, hy - d.ScrollY, c.W * 8, c.H * 8, c.SrcX * 8, c.SrcY * 8, c.W * 8, c.H * 8, 1);
            }
            var tab = Tabs[(int)_panel - 1];
            Text(S('M', tab.Id), d.TabX + tab.LabelX, d.TabY + tab.LabelY, 2, 40, 2, 1, 9);
            foreach (PageText pt in d.Texts)
            {
                string text = pt.Type == 'O' ? Strings.GetMessage('O', pt.Id, StringTables.ShipOnGround) : S(pt.Type, pt.Id);
                // OPTIONS' CONTROL TYPE column holds the customizer's buttons instead (ClassicControls: the game's)
                if (_panel == Panel.Options && pt.Type == 'O' && pt.Id == 1 && !ClassicControls) text = "controls";
                if (_panel == Panel.Options && pt.Type == 'O' && pt.Id == 9 && MouseTab) continue; // LOOK INVERT: the mouse has none
                Text(text, pt.X - d.ScrollX, pt.Y - d.ScrollY, pt.Align, pt.Wrap, pt.Pal);
            }
            switch (_panel)
            {
            case Panel.Logbook:
                DrawLogbook();
                break;
            case Panel.Options:
                DrawOptions();
                break;
            case Panel.WeaponSelect:
                DrawWeapons();
                break;
            }
            if (_flash == Flash.Back && FlashLit(_flashTick))
            {
                var b = BackFlashAt[(int)_panel - 1];
                FlipQuad(_flashBack, b.X, b.Y, 16, 8, b.Flip, false);
            }
            DrawList.Clip = clip;
            _tx = _ty = 0;
            _ts = 1;
        }

        private void DrawLogbook()
        {
            StorySave? story = Story;
            bool picked = _logState != LogState.Categories;
            for (int c = 0; c < 4; c++)
            {
                int image = picked && (int)_logCategory == c ? 1 : 0;
                Quad(_logIcons[c, image], LogIconAt[c].X, LogIconAt[c].Y, LogIconAt[c].X + 32, LogIconAt[c].Y + 32, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            if (story != null)
            {
                Text($"{ShipLogbook.Percent(story, LogCategory.Lore, LogCategory.Bioform, LogCategory.Object)}%", 124, 62, 2, 0, 2);
                Text($"{ShipLogbook.Percent(story, LogCategory.Equipment)}%", 218, 62, 2, 0, 2);
            }
            if (!picked)
            {
                Text(S('L', 2), 152, 20, 2, 0, 0, shown: Typed(S('L', 2)));   // select a category
                return;
            }
            string header = LogHeader();
            Text(header, 152, 20, 2, 0, 5, shown: Typed(header));
            if (_logState == LogState.List)
            {
                // the list box and scroll column (log layer rows 112+ at VOFS 32, HOFS -48)
                BlendedLayer(_log, 48, 80, 256, 80, 0, 112, 256, 80, 1);
                if (_logEntries.Count == 0)
                {
                    Text(S('L', 5), LogRowX, LogRowY - 1, 0, 0, 5);   // NO ENTRIES FOUND
                    return;
                }
                for (int i = 0; i < LogRows && _logTop + i < _logEntries.Count; i++)
                {
                    int index = _logTop + i;
                    Text(_logEntries[index].Name, LogRowX, LogRowY + i * LogRowPitch - 1, 0, 0, _keyFocus && index == _logSel ? 2 : 5);
                }
            }
            else
            {
                // an entry: title bar, text box and prev / back / next (log layer rows 0-111 at VOFS -64, HOFS -48)
                BlendedLayer(_log, 48, 64, 256, 112, 0, 0, 256, 112, 1);
                LogEntry entry = _logEntries[_logSel];
                Text(entry.Name, 66, 80, 0, 0, 0);
                List<string> lines = EntryLines();
                for (int i = 0; i < 3 && _logTextPage * 3 + i < lines.Count; i++)
                {
                    Text(lines[_logTextPage * 3 + i], 153, 104 + i * 11, 2, 0, 2);
                }
                bool prev = _logTextPage > 0 || _logSel > 0, next = _logTextPage + 1 < EntryPages() || _logSel + 1 < _logEntries.Count;
                Quad(_logArrow[prev ? 1 : 0], 80, 144, 112, 160, 1, 0, 0, 1, 1, 1, 1, 1);   // mirrored
                Quad(_logArrow[next ? 1 : 0], 192, 144, 224, 160, 0, 0, 1, 1, 1, 1, 1, 1);
                Text(S('M', 8), 153, 148, 2, 0, 2);   // back
            }
        }

        // weapon select (0x2145028 rects, 0x2144b80 sprites): owned affinity weapons in their slots (9+25k, 136), the
        // equipped one big at (52, 88) with its name above (WeaponNames, (30, 46) wrap 100, growing upward 8 px a line),
        // "drag weapon to slot to equip" / "no weapons to equip" at (99, 175); a dragged icon follows the finger (-16)
        private void DrawWeapons()
        {
            bool any = false;
            for (int k = 0; k < Affinity.Length; k++)
            {
                if (!Owns(k)) continue;
                any = true;
                if (k == _dragSlot) continue;
                Quad(_smallIcons[k], 9 + 25 * k, 136, 41 + 25 * k, 168, 0, 0, 1, 1, 1, 1, 1, 1);
                if (_keyFocus && k == _weaponSel) FocusFrame((13 + 25 * k, 142, 24, 24), 2);
            }
            int equipped = Story == null ? -1 : Array.IndexOf(Affinity, (BeamType)Story.WeaponSlots[2]);
            int named = _dragSlot >= 0 ? _dragSlot : _weaponSel >= 0 ? _weaponSel : equipped;
            if (equipped >= 0)
            {
                Quad(_bigIcons[equipped], 52, 88, 84, 120, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            if (named >= 0)
            {
                string name = Strings.GetMessage('W', (int)Affinity[named] + 1, StringTables.WeaponNames);
                int lines = _font.Lines(name, 100).Count;
                Text(name, 30, 46 - (lines - 1) * 8, 0, 100, 2, 1, 8);
            }
            Text(S('W', any ? 1 : 2), 99, 175, 2, 0, 2);
            if (_dragSlot >= 0)
            {
                Quad(_smallIcons[_dragSlot], _dragX - 16, _dragY - 16, _dragX + 16, _dragY + 16, 0, 0, 1, 1, 1, 1, 1, 1);
            }
        }

        // options (0x2144d78 rects, 0x2144e80 sprites, 0x21450c8 text)
        private void DrawOptions()
        {
            const float sy = 76; // BG2 scroll on this page
            for (int i = 0; i < (ClassicControls ? 4 : ControlButtons); i++)
            {
                bool on = ClassicControls ? ControlType == i : KeyboardControls && _controlDevice == i;
                float slot = ClassicControls ? i : ControlSlot(i);
                Quad(_controls[on ? 0 : 1], 16, 129 - sy + slot * 29, 80, 161 - sy + slot * 29, 0, 0, 1, 1, 1, 1, 1, 1);
                // these labels are centred on their row's y (8-px lines; matches the real screen's 2- and 3-line labels)
                string label = ClassicControls ? Strings.GetMessage('O', 5 + i, StringTables.ShipOnGround)
                    : KeyboardControls ? (i == 0 ? "controller" : "keyboard\n& mouse") : "customize\ncontrols";
                int lines = _font.Lines(label, 56).Count;
                Text(label, 48, 146 - sy + slot * 29 - lines * 4, 2, 56, on ? 2 : 3, 1, 8);
            }
            if (!MouseTab)
            {
                bool invert = LookInvert;
                Text(Strings.GetMessage('O', 10, StringTables.ShipOnGround), 110, 230 - sy, 2, 56, invert ? 2 : 3);
                Text(Strings.GetMessage('O', 11, StringTables.ShipOnGround), 170, 230 - sy, 2, 56, invert ? 3 : 2);
                Quad(_onOff[invert ? 0 : 1], 106, 240 - sy, 114, 248 - sy, 0, 0, 1, 1, 1, 1, 1, 1);
                Quad(_onOff[invert ? 1 : 0], 165, 240 - sy, 173, 248 - sy, 0, 0, 1, 1, 1, 1, 1, 1);
            }
            // sensitivity: v in 122..180 (180 = 1), marker at (103, v - 76), bar 64 px tall at (130, 115) filled 180 - v of 58
            float v = 180 - (Math.Clamp(OptSensitivity, 1, 15) - 1) * 58f / 14;
            Quad(_sensMarker, 103, v - 76, 135, v - 44, 0, 0, 1, 1, 1, 1, 1, 1);
            // the value in the grab tab (owner 2026-10-05), centred on the tab's body (x 103..122 of the 32-px marker)
            Text(OptSensitivity.ToString(System.Globalization.CultureInfo.InvariantCulture), 112, v - 64, 2, 0, 2);
            // the bar fills the slider track in the panel art (screen y 58..122), lit from the bottom up to the marker
            Bar(_sensBar, 180 - v, 58, 64, 130, 58, vertical: true);
            if (_keyFocus)
            {
                float slot = ClassicControls ? _optRow : ControlSlot(_optRow);
                var row = _optRow < 4 ? (16f, 68f + 29 * slot, 56f, 24f) : _optRow == 4 ? (100f, v - 76, 34f, 24f) : (100f, 162f, 77f, 20f);
                FocusFrame(row, 2);
            }
        }

        // 0x2107a80: a bar of 8-px cells (images 0-8 = full..empty), filled value/max of its length from the start (the
        // bottom for a vertical bar)
        private void Bar(int[] cells, float value, float max, float length, float x, float y, bool vertical)
        {
            float lit = Math.Clamp(value / max, 0, 1) * length;
            int n = (int)(length / 8);
            for (int i = 0; i < n; i++)
            {
                int fill = (int)Math.Clamp(lit - i * 8, 0, 8);
                float cx = vertical ? x : x + i * 8, cy = vertical ? y + (n - 1 - i) * 8 : y;
                Quad(cells[8 - fill], cx, cy, cx + 8, cy + 8, 0, 0, 1, 1, 1, 1, 1, 1);
            }
        }

        // a 256-wide BG layer's rows [y0, y1) across the canvas: edges and centre 1:1, the two spans between stretched
        private void Sliced(int tex, float y0, float y1, float alpha)
        {
            float[] slices = { 0, 56, 96, 160, 200, 256 }; // the shipTop layers are 256x256 (a redrawn one is scaled)
            float extra = (_w - 256) / 2;
            float x = 0;
            for (int i = 0; i < slices.Length - 1; i++)
            {
                float srcW = slices[i + 1] - slices[i];
                float dstW = i == 1 || i == 3 ? Math.Max(0, srcW + extra) : srcW;
                Quad(tex, x, y0, x + dstW, y1, slices[i] / 256f, y0 / 256f, slices[i + 1] / 256f, y1 / 256f, 1, 1, 1, alpha);
                x += dstW;
            }
        }

        // the popup struct (0x2144a7c): box = ani_textbox at (cx-64, top) and mirrored at (cx, top); message at (cx, top+9)
        // wrap 80; YES (cx-48, top+56) / NO (cx+16, top+56) with "yes"/"no" at (cx-+32, top+60); OK (cx-16, top+62) with
        // "ok" at (cx, top+64); SAVING's progress bar 40 wide at (cx-20, top+36). Mode 0 yes/no, 1 ok, 2 none.
        private void Popup((float X, float Y, float W, float H) over, int bank, string text, int mode)
        {
            float cx = Math.Clamp(over.X + over.W / 2, 50, _w - 50), top = PopupTop;
            Quad(_box[bank], cx - 64, top, cx, top + 64, 0, 0, 1, 1, 1, 1, 1, 1);
            Quad(_box[bank], cx, top, cx + 64, top + 64, 1, 0, 0, 1, 1, 1, 1, 1);
            Text(text, cx, top + 9, 2, 80, bank, 1, 12);
            if (mode == 0)
            {
                _yes = (cx - 48, top + 56, 32, 16);
                _no = (cx + 16, top + 56, 32, 16);
                Quad(_yesNo[bank], _yes.X, _yes.Y, _yes.X + 32, _yes.Y + 16, 0, 0, 1, 1, 1, 1, 1, 1);
                Quad(_yesNo[bank], _no.X, _no.Y, _no.X + 32, _no.Y + 16, 1, 0, 0, 1, 1, 1, 1, 1);
                Text(S('P', 6), cx - 32, top + 60, 2, 0, bank);
                Text(S('P', 7), cx + 32, top + 60, 2, 0, bank);
                if (_keyFocus) FocusFrame(_focus == 0 ? _yes : _no, bank);
            }
            else if (mode == 1)
            {
                _okRect = (cx - 16, top + 62, 32, 16);
                Quad(_ok, _okRect.X, _okRect.Y, _okRect.X + 32, _okRect.Y + 16, 0, 0, 1, 1, 1, 1, 1, 1);
                Text(S('P', 8), cx, top + 64, 2, 0, bank);
            }
            // popup sprites 2 and 6-9 (0x214534c, drawn by 0x21081d8 in the popup's bank): when it opens, the bar under the
            // text (ani_textbox_flash 32x8 at (cx-16, top+52)) and its buttons' brackets (ani_yesno_flash 8x8: YES's
            // (cx-50, top+58), NO's (cx+42, top+58) h-flipped; OK's (cx-18, top+64) and (cx+10, top+64) h-flipped) blink;
            // a touched button blinks its own bracket(s)
            bool opening = FlashLit(_openTick);
            if (opening && mode != 2) FlipQuad(_flashBar[bank], cx - 16, top + 52, 32, 8, false, false);
            if (mode == 0)
            {
                if (opening || _flash == Flash.Yes && FlashLit(_flashTick)) FlipQuad(_flashYesNo[bank], cx - 50, top + 58, 8, 8, false, false);
                if (opening || _flash == Flash.No && FlashLit(_flashTick)) FlipQuad(_flashYesNo[bank], cx + 42, top + 58, 8, 8, true, false);
            }
            else if (mode == 1 && (opening || _flash == Flash.Ok && FlashLit(_flashTick)))
            {
                FlipQuad(_flashYesNo[bank], cx - 18, top + 64, 8, 8, false, false);
                FlipQuad(_flashYesNo[bank], cx + 10, top + 64, 8, 8, true, false);
            }
            else
            {
                Bar(_progress, _saveProgress, 100, 40, cx - 20, top + 36, vertical: false);
            }
        }

        private void FocusFrame((float X, float Y, float W, float H) r, int pal)
        {
            (byte R, byte G, byte B) c = _colours.Colour(pal, 2);
            float pulse = 0.55f + 0.45f * MathF.Sin(_frame * 0.15f);
            const float t = 1;
            float r0 = c.R / 255f, g0 = c.G / 255f, b0 = c.B / 255f;
            float x0 = r.X - 2, y0 = r.Y - 2, x1 = r.X + r.W + 2, y1 = r.Y + r.H + 2;
            Quad(-1, x0, y0, x1, y0 + t, 0, 0, 0, 0, r0, g0, b0, pulse);
            Quad(-1, x0, y1 - t, x1, y1, 0, 0, 0, 0, r0, g0, b0, pulse);
            Quad(-1, x0, y0, x0 + t, y1, 0, 0, 0, 0, r0, g0, b0, pulse);
            Quad(-1, x1 - t, y0, x1, y1, 0, 0, 0, 0, r0, g0, b0, pulse);
        }

        // a BG layer region drawn the way the DS blends the panel over what's behind (panel + 6/16 of it)
        private void BlendedLayer(int tex, float x, float y, float w, float h, float sx, float sy, float sw, float sh, float alpha)
        {
            UiTexture t = _textures[tex];
            float u0 = sx / t.Width, v0 = sy / t.Height, u1 = (sx + sw) / t.Width, v1 = (sy + sh) / t.Height;
            Quad(tex, x, y, x + w, y + h, u0, v0, u1, v1, 0, 0, 0, alpha * 10 / 16f);
            Quad(tex, x, y, x + w, y + h, u0, v0, u1, v1, 1, 1, 1, alpha, UiBlend.Additive);
        }

        // units -> canvas, through the current card transform
        private void Quad(int tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
            float r, float g, float b, float a, UiBlend blend = UiBlend.Alpha)
        {
            DrawList.Quad(tex, (_tx + x0 * _ts) * _scale, (_ty + y0 * _ts) * _scale, (_tx + x1 * _ts) * _scale, (_ty + y1 * _ts) * _scale,
                u0, v0, u1, v1, r, g, b, a, blend);
        }

        private readonly List<WidgetTri> _glyphs = new();

        // a text record: (x, top) is the first line's 8-px cell top; align 0 left, 1 right, 2 centred; colour = fontcolors
        // bank `pal`; s scales about (x, top)
        // shown: only the first `shown` characters (a text typing itself in), laid out as the whole text
        private void Text(string text, float x, float top, int align, int wrap, int pal, float alpha = 1, float lineHeight = 12, float s = 1,
            int shown = int.MaxValue)
        {
            if (string.IsNullOrWhiteSpace(text) || alpha <= 0 || shown <= 0) return;
            (byte R, byte G, byte B) c = _colours.Colour(pal, 2);
            _glyphs.Clear();
            // MenuFont works in menu space (Y up, touch screen -192..0): see MenuFont.Emit
            _font.Emit(text, x, 192 - top - lineHeight, align, wrap, lineHeight, c.R / 255f, c.G / 255f, c.B / 255f, alpha, 0, _glyphs);
            // two triangles per drawn (non-space) glyph, in text order
            int keep = shown >= text.Length ? _glyphs.Count : 2 * System.Linq.Enumerable.Count(text[..shown], ch => ch != ' ' && ch != '\n');
            for (int k = 0; k < keep && k < _glyphs.Count; k++)
            {
                WidgetTri t = _glyphs[k];
                DrawList.Triangle(t.TextureId, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, Map(t.A, x, top, s), Map(t.B, x, top, s), Map(t.C, x, top, s));
            }
        }

        private UiVertex Map(UiVertex v, float ax, float ay, float s) =>
            new UiVertex((_tx + (ax + (v.X - ax) * s) * _ts) * _scale, (_ty + (ay + (-v.Y - ay) * s) * _ts) * _scale, v.U, v.V, v.R, v.G, v.B, v.A);
    }
}
