using System;
using System.Collections.Generic;
using System.IO;
using MphRead; // SfxId

// The front end's message box (ov0 USA rev 1: open 0x21263fc, per-frame 0x21266e0; rev 0 0x2125970 / 0x2125c54): a box
// over the touch screen with a message and either a YES / NO pair, the busy spinner, or one check button (Inform). The game's code draws it, not the
// menu data, all from the ROM's own 2D art in _archives/frontend2d: 2box (a BG layer: a 50% stipple over the touch
// screen and the box with its orange border), yes / no (32x32 buttons; image 1 = lit; their frame list is the press
// flash), waiting (the spinner, 5 images) and fontcolors (bank 5 colour 2 = the text's orange).
//
// Measured on BizHawk USA rev 1 (file select COPY / DELETE / create, 2026-10-04): the box comes up whole the frame after
// the touch, no animation. A touched button (or A on the focused one, or B for NO) plays its sprite's frame list -- lit
// on ticks 1-3 and 7-9 -- and acts on the 10th tick; YES then shows the busy box in its place. Keys: A with nothing
// focused, or Left / Right, focus a button first (TOUCH_NEUTRAL); the focused one is lit 2 ticks of every 5. Sounds: YES
// CREATE_CONFIRM_SCR, NO CREATE_CANCEL_SCR (table 0x214798c). The busy box runs its spinner at 5 ticks per image and
// closes by itself when the card write ends (COPY 89 frames, DELETE 85 after the box appears).
namespace MphRecomp.Frontend
{
    public sealed class MenuPopup
    {
        // where the DS draws it: touch-screen pixels, y down (BizHawk; the buttons' x from 0x2127100: 0x5c / 0x84, one
        // centred button 0x70)
        private const int BoxX0 = 41, BoxY0 = 27, BoxX1 = 215, BoxY1 = 149;
        private const int ButtonY = 108, YesX = 92, NoX = 132, SpinnerX = 112, ButtonSize = 32;
        // the message: centred on the box, first line's capitals from y 40, 9 px per line, broken to fit this width (the
        // game's breaks: "do not touch the game / card or the power / button.")
        private const float TextX = 128, TextY = 143, LineHeight = 9;
        private const int WrapWidth = 140;
        private const float Z = 3000;

        private enum Mode { Closed, Ask, Busy, Info }

        private readonly int _box;
        private readonly int[,] _buttons; // [button, image]
        private readonly (int Image, int Ticks)[] _press;
        private readonly int[] _spinner;
        private readonly (int Image, int Ticks)[] _spin;
        private readonly (float R, float G, float B) _textColour;

        private Mode _mode;
        private string _text = "";
        private Action<bool>? _answered;
        private Action? _done;
        private int _tick, _busyTicks;
        private int _focus = -1, _focusTick;
        private int _pressed = -1, _pressTick;

        // the menu's sound player (raw MphRead sfx ids, scripts flagged 0x4000)
        public Action<int, string>? Sound { get; set; }

        public bool IsOpen => _mode != Mode.Closed;

        private MenuPopup(int box, int[,] buttons, (int, int)[] press, int[] spinner, (int, int)[] spin, (float, float, float) textColour)
        {
            _box = box;
            _buttons = buttons;
            _press = press;
            _spinner = spinner;
            _spin = spin;
            _textColour = textColour;
        }

        // null when this extraction has no _archives/frontend2d (the file select then works without its message boxes)
        public static MenuPopup? Load(string fileSystemRoot, UiTextureCache textures)
        {
            string dir = Path.Combine(fileSystemRoot, "_archives", "frontend2d");
            try
            {
                DsImage bg = DsGraphics.LoadBg(Path.Combine(dir, "2box.bin"));
                int box = textures.GetOrAdd("frontend2d/2box", () => Crop(bg, BoxX0, BoxY0, BoxX1 - BoxX0, BoxY1 - BoxY0)).Id;
                DsSprite yes = DsGraphics.LoadSprite(Path.Combine(dir, "yes.bin"));
                DsSprite no = DsGraphics.LoadSprite(Path.Combine(dir, "no.bin"));
                var buttons = new int[2, 2];
                for (int image = 0; image < 2; image++)
                {
                    buttons[0, image] = Texture(textures, "yes", yes, image);
                    buttons[1, image] = Texture(textures, "no", no, image);
                }
                DsSprite waiting = DsGraphics.LoadSprite(Path.Combine(dir, "waiting.bin"));
                var spinner = new int[waiting.ImageCount];
                for (int image = 0; image < spinner.Length; image++)
                {
                    spinner[image] = Texture(textures, "waiting", waiting, image);
                }
                (byte r, byte g, byte b) = DsGraphics.LoadSprite(Path.Combine(dir, "fontcolors.bin")).Colour(5, 2);
                return new MenuPopup(box, buttons, yes.Frames, spinner, waiting.Frames, (r / 255f, g / 255f, b / 255f));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
            {
                Console.Error.WriteLine("menu popup: no frontend2d art (" + ex.Message + ")");
                return null;
            }
        }

        private static int Texture(UiTextureCache textures, string name, DsSprite sprite, int image)
            => textures.GetOrAdd($"frontend2d/{name}/{image}", () =>
            {
                DsImage im = sprite.Image(image, 0);
                return (im.Width, im.Height, im.Rgba);
            }).Id;

        private static (int, int, byte[]) Crop(DsImage image, int x0, int y0, int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(image.Rgba, ((y0 + y) * image.Width + x0) * 4, rgba, y * w * 4, w * 4);
            }
            return (w, h, rgba);
        }

        // a question with YES / NO; `answered` runs once the pressed button's flash ends (true = YES)
        public void Ask(string text, Action<bool> answered)
        {
            Open(Mode.Ask, text);
            _answered = answered;
        }

        // a message with the game's single centred check button (its x 0x70, the one-button layout): A, B or a tap on the
        // check closes it after the press flash, then runs `closed`
        public void Inform(string text, Action? closed = null)
        {
            Open(Mode.Info, text);
            _done = closed;
        }

        // a message with the spinner that closes itself after `ticks` menu ticks, then runs `done`
        public void Busy(string text, int ticks, Action done)
        {
            Open(Mode.Busy, text);
            _busyTicks = ticks;
            _done = done;
        }

        private void Open(Mode mode, string text)
        {
            _mode = mode;
            _text = text;
            _tick = 0;
            _focus = -1;
            _pressed = -1;
            _answered = null;
            _done = null;
        }

        // new text in the open box, buttons and state kept (the update download's progress in its busy box)
        public void SetText(string text)
        {
            if (_mode != Mode.Closed) _text = text;
        }

        // closed from outside (the page went away): no callbacks
        public void Close() => _mode = Mode.Closed;

        public void Tick()
        {
            if (_mode == Mode.Closed) return;
            _tick++;
            _focusTick++;
            if (_mode == Mode.Ask && _pressed >= 0 && ++_pressTick >= PressTicks)
            {
                bool yes = _pressed == 0;
                Action<bool>? answered = _answered;
                _mode = Mode.Closed;
                answered?.Invoke(yes); // may open the busy box straight away
            }
            else if (_mode == Mode.Info && _pressed >= 0 && ++_pressTick >= PressTicks)
            {
                Action? closed = _done;
                _mode = Mode.Closed;
                closed?.Invoke();
            }
            else if (_mode == Mode.Busy && _tick >= _busyTicks)
            {
                Action? done = _done;
                _mode = Mode.Closed;
                done?.Invoke();
            }
        }

        private int PressTicks
        {
            get
            {
                int total = 0;
                foreach ((int _, int ticks) in _press) total += ticks;
                return Math.Max(1, total);
            }
        }

        // ---- input: while open the box takes every key and touch ----

        public void Press(MenuKeys keys)
        {
            if (_mode == Mode.Info && _pressed < 0 && (keys & (MenuKeys.A | MenuKeys.B)) != 0)
            {
                PressButton(0); // the check is the only button: A or B takes it
                return;
            }
            if (_mode != Mode.Ask || _pressed >= 0) return;
            if ((keys & MenuKeys.B) != 0) PressButton(1);
            else if ((keys & MenuKeys.A) != 0)
            {
                if (_focus < 0) SetFocus(0);
                else PressButton(_focus);
            }
            else if ((keys & MenuKeys.Left) != 0) Navigate(-1);
            else if ((keys & MenuKeys.Right) != 0) Navigate(1);
        }

        public void Navigate(int dx)
        {
            if (_mode != Mode.Ask || _pressed >= 0) return;
            if (_focus < 0) SetFocus(0);
            else if (dx < 0) SetFocus(0);
            else if (dx > 0) SetFocus(1);
        }

        // a touch at DS touch-screen pixel (x, y), y down
        public void Touch(float x, float y)
        {
            if (_mode == Mode.Info)
            {
                if (_pressed < 0 && y >= ButtonY && y < ButtonY + ButtonSize && x >= SpinnerX && x < SpinnerX + ButtonSize) PressButton(0);
                return;
            }
            if (_mode != Mode.Ask || _pressed >= 0 || y < ButtonY || y >= ButtonY + ButtonSize) return;
            if (x >= YesX && x < YesX + ButtonSize) PressButton(0);
            else if (x >= NoX && x < NoX + ButtonSize) PressButton(1);
        }

        private void SetFocus(int button)
        {
            if (_focus == button) return;
            _focus = button;
            _focusTick = 0;
            Sound?.Invoke((int)SfxId.TOUCH_NEUTRAL, "popup focus");
        }

        private void PressButton(int button)
        {
            _pressed = button;
            _pressTick = 0;
            _focus = -1;
            Sound?.Invoke(button == 0 ? (int)SfxId.CREATE_CONFIRM_SCR : (int)SfxId.CREATE_CANCEL_SCR, button == 0 ? "popup YES" : "popup NO");
        }

        // ---- drawing: triangles in menu space (Y up, the touch screen below y = 0), for the host to place ----

        public void Collect(List<WidgetTri> output, MenuFont font)
        {
            if (_mode == Mode.Closed) return;
            Quad(output, _box, BoxX0, BoxY0, BoxX1, BoxY1);
            font.Emit(_text, TextX, TextY, align: 2, WrapWidth, LineHeight, _textColour.R, _textColour.G, _textColour.B, 1, Z + 1, output);
            if (_mode == Mode.Ask)
            {
                for (int button = 0; button < 2; button++)
                {
                    int x = button == 0 ? YesX : NoX;
                    Quad(output, _buttons[button, ButtonImage(button)], x, ButtonY, x + ButtonSize, ButtonY + ButtonSize);
                }
            }
            else if (_mode == Mode.Info)
            {
                Quad(output, _buttons[0, ButtonImage(0)], SpinnerX, ButtonY, SpinnerX + ButtonSize, ButtonY + ButtonSize);
            }
            else
            {
                Quad(output, _spinner[SpinnerImage()], SpinnerX, ButtonY, SpinnerX + ButtonSize, ButtonY + ButtonSize);
            }
        }

        private int ButtonImage(int button)
        {
            if (_pressed == button) return FrameImage(_press, _pressTick, loop: false);
            if (_focus == button) return _focusTick % 5 >= 3 ? 1 : 0;
            return 0;
        }

        private int SpinnerImage() => Math.Clamp(FrameImage(_spin, _tick, loop: true), 0, _spinner.Length - 1);

        private static int FrameImage((int Image, int Ticks)[] frames, int tick, bool loop)
        {
            if (frames.Length == 0) return 0;
            int total = 0;
            foreach ((int _, int ticks) in frames) total += Math.Max(1, ticks);
            if (loop) tick %= total;
            foreach ((int image, int ticks) in frames)
            {
                if (tick < Math.Max(1, ticks)) return image;
                tick -= Math.Max(1, ticks);
            }
            return frames[^1].Image;
        }

        private static void Quad(List<WidgetTri> output, int texture, float x0, float y0, float x1, float y1)
        {
            var p0 = new UiVertex(x0, -y0, 0, 0, 1, 1, 1, 1);
            var p1 = new UiVertex(x1, -y0, 1, 0, 1, 1, 1, 1);
            var p2 = new UiVertex(x1, -y1, 1, 1, 1, 1, 1, 1);
            var p3 = new UiVertex(x0, -y1, 0, 1, 1, 1, 1, 1);
            output.Add(new WidgetTri { A = p0, B = p1, C = p2, Z = Z, TextureId = texture, Item = -1 });
            output.Add(new WidgetTri { A = p0, B = p2, C = p3, Z = Z, TextureId = texture, Item = -1 });
        }
    }
}
