using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Text;

// The new game's mission briefing (vanilla: right after starting a new file, before the Story Intro movie), from the
// ROM: shipSpace/story_top (the top screen's grid panel), stringTables/ShipInSpace S001-S014, and the game's own logic
// (overlay9_0 state 0: enter 0x21052c4, process 0x21050c8, draw 0x2104cfc; page table 0x2143b30), decoded 2026-10-01
// and checked against the oracle. Five pages: a header drawn whole (centred at x 128, y 29) over body paragraphs typed
// one character per game tick (30 Hz); x 28, wrap 200, paragraphs 16 px below the previous one's last line; the last
// page types "UPLOADING KNOWN COORDINATES_" centred after a 56-tick lead-in. Orange text (RGB555 0x165d). SKIP (S014,
// red, blinking every 5 ticks) ends the briefing; the Story Intro movie still plays after it.
// Timing (BizHawk USA rev 1, 2026-10-01: enter 0x2105d54, process 0x2105b58): ONE page timer, +1 a tick, +2 while A or
// the touch screen is held. Each page's lines have deadlines built at enter from a static table (overlay9_0 0x2144630
// rev 1 / 0x2143b30 rev 0, `Extra` below) and the ROM strings: d[0] = Extra[0] + 30, d[k] = Extra[k] + d[k-1] + 2 x the
// length of line k. Body line k types from d[k-1], one character per timer step; the page turns (INTRO_PAGE_TURN) when
// the timer passes its last deadline + 45 (0x2144208). LETTER_BLIP once on a tick a visible character appeared (the text
// draw's cursor moved): a space alone makes none. B does nothing; only SKIP skips.
//
// One 16:9 screen: the top screen's panel at full height, its grid widened to the canvas; SKIP at the canvas's
// bottom-right (the touch screen's emblem is left out).
namespace MphRecomp.Frontend
{
    public sealed class ShipBriefing
    {
        private static readonly int[][] Pages = { new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 }, new[] { 13 } };
        private const int HeaderY = 29, BodyX = 28, BodyWrap = 200, PageHold = 45, FirstLine = 30, LastPageLeadIn = 56;
        // the static per-line extra of each page's deadlines (overlay9_0 0x2144630 +6; the list items get 25 ticks more)
        private static readonly int[][] Extra = { new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 25, 25, 0 }, new[] { 0, 0, 0, 0 }, new[] { 56 } };
        private static readonly (float R, float G, float B) Orange = (239 / 255f, 150 / 255f, 44 / 255f);
        private const int LetterBlip = 480, PageTurn = 0x5b; // SfxId.LETTER_BLIP; INTRO_PAGE_TURN (91)
        // TELEPATHIC_MESSAGE (script 96), started with the briefing (0x21053e8: id 0x80000060 -- bit 31 makes the DS
        // script player run it at half its tick rate, i.e. its delays count 15 Hz ticks here; a host that can't do that
        // plays it twice as fast) and cut when the briefing ends or is skipped
        // played with bit 31 on the DS (0x206eda8): its script runs at half the screen's script clock
        public const int Telepathic = 96 | 0x4000;

        private readonly UiTextureCache _textures;
        private readonly MenuFont _font;
        private readonly int _panel;
        private int _page, _tick, _frame, _seen;
        private bool _done;
        private float _scale = 1, _w = 256, _ox;

        public UiDrawList DrawList { get; }
        public Action<int>? PlaySound;
        public Action? StopScripts;
        // the briefing ended (all pages, or SKIP)
        public Action? Finished;
        public bool Fast { get; set; }

        public ShipBriefing(string fileSystemRoot, UiTextureCache textures)
        {
            _textures = textures;
            DrawList = new UiDrawList(textures);
            _font = new MenuFont(textures);
            string dir = Path.Combine(fileSystemRoot, "_archives", "shipSpace");
            _panel = textures.GetOrAdd("ship/story_top", () =>
            {
                DsImage im = DsGraphics.LoadBg(Path.Combine(dir, "story_top.bin"));
                return (im.Width, im.Height, im.Rgba);
            }).Id;
        }

        private static string S(int id) => Strings.GetMessage('S', id, StringTables.ShipInSpace);

        public void Start()
        {
            _page = _tick = _frame = _seen = 0;
            _done = false;
            // the enter (0x21053e4): the page sound, then the telepathic message
            PlaySound?.Invoke(PageTurn);
            PlaySound?.Invoke(Telepathic);
        }

        public void Skip()
        {
            if (_done) return;
            _done = true;
            StopScripts?.Invoke(); // 0x2105258: StopFreeSfxScripts(1) as the briefing exits (last page or SKIP)
            Finished?.Invoke();
        }

        // the page's typed strings (pages 0-3: all but the header)
        private int[] Body => _page < 4 ? Pages[_page][1..] : Pages[_page];

        // the page's line deadlines (0x2105d54): see the top
        private static int[] Deadlines(int page)
        {
            int[] ids = Pages[page], d = new int[ids.Length];
            for (int k = 0; k < ids.Length; k++)
            {
                d[k] = Extra[page][k] + (k == 0 ? FirstLine : d[k - 1] + 2 * S(ids[k]).Length);
            }
            return d;
        }

        // characters shown of body line i (Body[i]): from the previous line's deadline, one per timer step; the last page's
        // one line after its lead-in
        private int Shown(int i)
        {
            int len = S(Body[i]).Length;
            int from = _page < 4 ? Deadlines(_page)[i] : LastPageLeadIn;
            return Math.Clamp(_tick - from, 0, len);
        }

        private int VisibleGlyphs()
        {
            int n = 0;
            for (int i = 0; i < Body.Length; i++)
            {
                string t = S(Body[i]);
                int shown = Shown(i);
                for (int c = 0; c < shown; c++) if (t[c] != ' ') n++;
            }
            return n;
        }

        // once per displayed frame (60 Hz); the briefing runs on the game's 30 Hz tick (0x2105b58)
        public void Tick()
        {
            if (_done) return;
            if (++_frame % 2 != 0) return;
            if (Fast) _tick++;
            _tick++;
            if (_tick > Deadlines(_page)[^1] + PageHold)
            {
                if (_page == Pages.Length - 1)
                {
                    Skip();
                    return;
                }
                _page++;
                _tick = _seen = 0;
                PlaySound?.Invoke(PageTurn);
                return;
            }
            // the draw's blip (0x2104e4c): the typed text's end moved this tick -- at most one, whatever A typed
            int seen = VisibleGlyphs();
            if (seen != _seen)
            {
                _seen = seen;
                PlaySound?.Invoke(LetterBlip);
            }
        }

        // a tap at canvas pixels: SKIP (touch rect (215, 175, 40, 17), at the canvas's right edge here)
        public void Touch(float x, float y)
        {
            float ux = (x - _ox) / _scale, uy = y / _scale;
            float right = 256 + (_w - 256) / 2;
            if (ux >= right - 41 && ux < right && uy >= 175 && uy < 192) Skip();
        }

        public void Build(int width, int height)
        {
            DrawList.Clear();
            _scale = height / 192f;
            _w = width / _scale;
            _ox = (width - 256 * _scale) / 2;
            // the panel, its outer 8 px columns stretched across the extra width
            UiTexture t = _textures[_panel];
            float side = (_w - 256) / 2;
            Quad(_panel, -side, 0, 8, 192, 0, 0, 8f / t.Width, 192f / t.Height, 1);
            Quad(_panel, 8, 0, 248, 192, 8f / t.Width, 0, 248f / t.Width, 192f / t.Height, 1);
            Quad(_panel, 248, 0, 256 + side, 192, 248f / t.Width, 0, 256f / t.Width, 192f / t.Height, 1);
            if (_page < 4)
            {
                Text(S(Pages[_page][0]), 128, HeaderY, 2, 0, Orange, 1);
            }
            float y = _page < 4 ? HeaderY + 16 : HeaderY;
            for (int bi = 0; bi < Body.Length; bi++)
            {
                string full = S(Body[bi]);
                int left = Shown(bi);
                List<string> lines = _font.Lines(full, _page < 4 ? BodyWrap : 0);
                foreach (string line in lines)
                {
                    if (left > 0)
                    {
                        string shown = line.Length <= left ? line : line[..left];
                        if (_page < 4) Text(shown, BodyX, y, 0, 0, Orange, 1);
                        else Text(shown, 128 - _font.Measure(line) / 2, y, 0, 0, Orange, 1); // centred on the whole line
                    }
                    left -= line.Length + 1; // the space the wrap took
                    y += 8;
                }
                y += 8;
            }
            if (!_done && (_tick / 5) % 2 == 0)
            {
                Text(S(14), 256 + side - 11, 180, 1, 0, (197 / 255f, 8 / 255f, 8 / 255f), 1);
            }
        }

        private void Quad(int tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, float a)
        {
            DrawList.Quad(tex, _ox + x0 * _scale, y0 * _scale, _ox + x1 * _scale, y1 * _scale, u0, v0, u1, v1, 1, 1, 1, a);
        }

        private readonly List<WidgetTri> _glyphs = new();

        private void Text(string text, float x, float top, int align, int wrap, (float R, float G, float B) c, float alpha)
        {
            if (string.IsNullOrEmpty(text)) return;
            _glyphs.Clear();
            _font.Emit(text, x, 192 - top - 8, align, wrap, 8, c.R, c.G, c.B, alpha, 0, _glyphs);
            foreach (WidgetTri g in _glyphs)
            {
                DrawList.Triangle(g.TextureId, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, Map(g.A), Map(g.B), Map(g.C));
            }
        }

        private UiVertex Map(UiVertex v) => new UiVertex(_ox + v.X * _scale, -v.Y * _scale, v.U, v.V, v.R, v.G, v.B, v.A);
    }
}
