using System;
using System.Collections.Generic;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.FirstHunt
{
    // Where First Hunt's menus (FhFrontend, menu space: top screen y 0..192, touch screen y -192..0) go on one canvas.
    // Two-screen: the DS's screens side by side, the top one on the left. One screen (Wide; owner's pick A3b, 2026-10-14):
    // the top screen's picture fills the canvas, dimmed; the touch screen's hex grid tiles it faintly, with soft patches of
    // it fading in and out at random spots; the touch screen's controls in the middle, without their own background; the
    // top screen's description band (the page's 3D up there) at the bottom; the frame's lightning on the canvas's edges.
    // The stylus trail lights the grid anywhere on the canvas. Pages whose top screen is the content (the score tables,
    // the lobby's cards, the control type, the name editor's help) show it as a panel beside the controls. The title is one
    // picture (owner, 2026-10-14: combined, not a panel): its logo picture large, centred, undimmed and on the canvas's foot, faded into
    // the grid, the touch screen's button and copyright under it.
    public sealed class FhScreenLayout
    {
        public bool Wide { get; set; } = true;
        // the title as a panel beside its controls, as the other content pages (before the combined title)
        public bool TitlePanel { get; set; }

        private float _w = 1, _h = 1;
        // where the touch screen's pixels were drawn: canvas px of its top left, px per DS pixel (the touch input's map)
        private Vector2 _touchAt;
        private float _touchScale = 1;
        private int _gridTexture = -1;

        private static readonly HashSet<string> PanelPages = new()
        {
            "title", "records", "host_start", "configuration", "edit_name_2", "training_hi_score", "multi_hi_score",
        };

        // pages whose top-screen picture is its content (the control types' drawings and words, the name editor's help, the
        // logo): the canvas takes the main menu's picture instead of that one blown up behind its own panel
        private static readonly HashSet<string> BorrowArt = new() { "title", "configuration", "edit_name_2" };
        private readonly List<WidgetTri> _art = new();

        public void Resize(float width, float height)
        {
            _w = Math.Max(1, width);
            _h = Math.Max(1, height);
            Place(null);
        }

        // canvas px -> touch-screen px (0..256, 0..192 down). Two-screen: a touch that lands off the touch screen is
        // clamped (a drag off its edge still lifts), and a press there is dropped (false). One screen: anywhere, unclamped
        // (the trail goes on over the grid; buttons only take presses inside their boxes).
        public bool ToTouch(float x, float y, bool down, out float sx, out float sy)
        {
            sx = (x - _touchAt.X) / _touchScale;
            sy = (y - _touchAt.Y) / _touchScale;
            if (Wide) return true;
            bool inside = sx >= 0 && sx < 256 && sy >= 0 && sy < 192;
            sx = Math.Clamp(sx, 0, 255);
            sy = Math.Clamp(sy, 0, 191);
            return inside || !down;
        }

        // the frame's layout for a page (null: the last one's), sets the touch map
        private Rect _top, _bottom, _band;
        private bool _panel, _title;
        private Rect _titleArt;

        private readonly struct Rect
        {
            public readonly float X, Y, S; // canvas px of the screen's top left, px per DS pixel
            public Rect(float x, float y, float s) { X = x; Y = y; S = s; }
            public UiRect Clip => new(X, Y, 256 * S, 192 * S);
        }

        // nearest-sampled DS pixels look even at a whole scale: take it when it's within 10%
        private static float Snap(float s) => MathF.Floor(s) >= 1 && MathF.Floor(s) >= 0.9f * s ? MathF.Floor(s) : s;

        private string? _placed;

        private void Place(string? page)
        {
            page ??= _placed;
            _placed = page;
            if (!Wide)
            {
                // 256 x 192 each, side by side with a gap of a few DS pixels
                const float gap = 8;
                float s = Math.Max(1, MathF.Min(_w / (512 + gap), _h / 192f));
                float left = (_w - (512 + gap) * s) / 2, top = (_h - 192 * s) / 2;
                _top = new Rect(left, top, s);
                _bottom = new Rect(left + (256 + gap) * s, top, s);
                _panel = false;
            }
            else
            {
                // one unit = a DS pixel of a 240-tall canvas (320 wide at the least)
                float u = MathF.Min(_h / 240, _w / 320);
                _title = page == "title" && !TitlePanel;
                _panel = page != null && PanelPages.Contains(page) && !_title;
                if (_title)
                {
                    // the controls smaller, the copyright's foot (row 181) a few units off the canvas's; the logo picture
                    // centred, its foot on the canvas's so Samus's legs reach it as on the cart's top screen, as large as
                    // puts its logo's top (row 62.5; the logo spans columns 18-240) 0.14 of the way down, well under the
                    // top line, the logo no wider than the canvas inside the frame's side lines (owner, 2026-10-14); the
                    // controls smaller where the button's top (row 58, with its glow) would cover HUNTERS (its glow's foot at the
                    // picture's row 126, 66 rows over the canvas's foot), as on 4:3
                    float p = MathF.Min(0.86f * _h / (192 - 62.5f), (_w - 44 * u) / 222);
                    float c = MathF.Min(Snap(0.75f * u), (66 * p - 8 * u) / 123);
                    _bottom = new Rect((_w - 256 * c) / 2, _h - 6 * u - 181 * c, c);
                    _titleArt = new Rect((_w - 256 * p) / 2, _h - 192 * p, p);
                    _band = _bottom;
                }
                else if (_panel)
                {
                    float gap = 10 * u, s = Snap(MathF.Min(1.1f * u, (_w - 3 * gap) / 512));
                    float left = (_w - 512 * s - gap) / 2, top = (_h - 192 * s) / 2;
                    _top = new Rect(left, top, s);
                    _bottom = new Rect(left + 256 * s + gap, top, s);
                }
                else
                {
                    float c = Snap(0.95f * u), b = Snap(u);
                    _bottom = new Rect((_w - 256 * c) / 2, 0, c);
                    // the band's middle (row 93: the description bars, borderBigTop/Bottom) a few units off the bottom
                    _band = new Rect((_w - 256 * b) / 2, _h - 3 * u - 35 * b - 93 * b, b);
                }
            }
            _touchAt = new Vector2(_bottom.X, _bottom.Y);
            _touchScale = _bottom.S;
        }

        public void Build(FhFrontend menus, List<WidgetTri> tris, UiDrawList list, double seconds)
        {
            if (!Wide)
            {
                Place(null);
                foreach (bool topScreen in new[] { true, false })
                {
                    Rect r = topScreen ? _top : _bottom;
                    list.Clip = r.Clip;
                    foreach (WidgetTri t in tris)
                    {
                        if (OnTop(t) == topScreen) Draw(list, t, r, topScreen, UiBlend.Alpha);
                    }
                }
                list.Clip = UiRect.None;
                return;
            }
            Place(menus.PageName);
            float u = MathF.Min(_h / 240, _w / 320);
            // the picture: the top screen's backdrop over the whole canvas, dimmed
            float a = MathF.Max(_w / 256, _h / 192);
            var art = new Rect((_w - 256 * a) / 2, (_h - 192 * a) / 2, a);
            List<WidgetTri> picture = tris;
            if (_title) TitleArt(list, tris);
            else if (BorrowArt.Contains(menus.PageName))
            {
                _art.Clear();
                menus.EvaluateBackdrop("main_menu", _art);
                picture = _art;
            }
            if (!_title)
            {
                foreach (WidgetTri t in picture)
                {
                    if (Tag(t) == FhFrontend.TagBackdrop && OnTop(t)) Draw(list, t, art, true, UiBlend.Alpha);
                }
                Fill(list, 0, 0, 0, 0.45f);
            }
            // the grid, faint, with patches fading in and out
            int grid = GridTexture(menus, tris);
            if (grid >= 0)
            {
                GridQuad(list, grid, GridBase);
                Patches(list, grid, seconds, u);
            }
            Borders(menus, list, tris, u);
            Header(menus, list, tris);
            // the touch screen's controls, and the stylus trail anywhere
            foreach (WidgetTri t in tris)
            {
                int tag = Tag(t);
                if (OnTop(t) && tag != FhFrontend.TagTrail) continue;
                if (tag == FhFrontend.TagBottom || tag == FhFrontend.TagBackdrop) continue;
                if (tag == FhFrontend.TagFrame)
                {
                    string node = menus.NodeOf(t.Item).Node;
                    if (node.StartsWith("frameBar") || node.StartsWith("lightning") || node.StartsWith("headerBar")) continue;
                }
                list.Clip = tag == FhFrontend.TagTrail ? UiRect.None : _bottom.Clip;
                Draw(list, t, _bottom, false, UiBlend.Alpha);
            }
            list.Clip = UiRect.None;
            // the top screen: its page's 3D as the band at the bottom, or the whole screen as the panel
            foreach (WidgetTri t in tris)
            {
                if (!OnTop(t)) continue;
                int tag = Tag(t);
                if (tag == FhFrontend.TagTrail) continue;
                if (_panel)
                {
                    list.Clip = _top.Clip;
                    Draw(list, t, _top, true, UiBlend.Alpha);
                }
                else if (tag != FhFrontend.TagBackdrop && tag != FhFrontend.TagFrame)
                {
                    Draw(list, t, _band, true, UiBlend.Alpha);
                }
            }
            list.Clip = UiRect.None;
            Edges(menus, list, tris, u);
        }

        private static int Tag(in WidgetTri t) => t.Item >> 12;
        private static bool OnTop(in WidgetTri t) => (t.A.Y + t.B.Y + t.C.Y) / 3 >= 0;

        private static void Draw(UiDrawList list, WidgetTri t, Rect r, bool topScreen, UiBlend blend)
        {
            float yTop = topScreen ? 192 : 0;
            UiVertex Map(UiVertex v)
            {
                v.X = r.X + v.X * r.S;
                v.Y = r.Y + (yTop - v.Y) * r.S;
                return v;
            }
            list.Triangle(t.TextureId, t.WrapS, t.WrapT, blend, Map(t.A), Map(t.B), Map(t.C));
        }

        // the combined title's picture: the canvas a dark red, the logo picture over it, its edges fading into that (the grid
        // goes over it all, as on the other pages)
        private void TitleArt(UiDrawList list, List<WidgetTri> tris)
        {
            const float r = 0.07f, g = 0.01f, b = 0.01f;
            Fill(list, r, g, b, 1);
            foreach (WidgetTri t in tris)
            {
                if (Tag(t) == FhFrontend.TagBackdrop && OnTop(t)) Draw(list, t, _titleArt, true, UiBlend.Alpha);
            }
            Rect a = _titleArt;
            float x0 = a.X, x1 = a.X + 256 * a.S, y0 = a.Y, y1 = a.Y + 192 * a.S, side = 32 * a.S, head = 16 * a.S;
            // (each edge's band from opaque at the edge to clear inside; none at the foot, on the canvas's)
            Fade(list, x0, y0, x0 + side, y1, r, g, b, 1, 0, 1, 0);
            Fade(list, x1 - side, y0, x1, y1, r, g, b, 0, 1, 0, 1);
            Fade(list, x0, y0, x1, y0 + head, r, g, b, 1, 1, 0, 0);
        }

        // a box in one colour, its corners' alphas (top left, top right, bottom left, bottom right)
        private static void Fade(UiDrawList list, float x0, float y0, float x1, float y1, float r, float g, float b,
            float tl, float tr, float bl, float br)
        {
            var v0 = new UiVertex(x0, y0, 0, 0, r, g, b, tl);
            var v1 = new UiVertex(x1, y0, 0, 0, r, g, b, tr);
            var v2 = new UiVertex(x1, y1, 0, 0, r, g, b, br);
            var v3 = new UiVertex(x0, y1, 0, 0, r, g, b, bl);
            list.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, v0, v1, v2);
            list.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, v0, v2, v3);
        }

        private void Fill(UiDrawList list, float r, float g, float b, float alpha)
        {
            var v0 = new UiVertex(0, 0, 0, 0, r, g, b, alpha);
            var v1 = new UiVertex(_w, 0, 0, 0, r, g, b, alpha);
            var v2 = new UiVertex(_w, _h, 0, 0, r, g, b, alpha);
            var v3 = new UiVertex(0, _h, 0, 0, r, g, b, alpha);
            list.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, v0, v1, v2);
            list.Triangle(-1, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, v0, v2, v3);
        }

        // The touch screen's hex grid as one repeating cell: its lattice is 8 px wide and 14 rows high (two rows of cells,
        // the second half a cell over; the trail's snap, 0x2049b84), so an 8 x 14 tile averaged over the background's
        // middle (bottom_bg; clear of the frame's corners) holds the lines, which the layout adds over the picture. Its
        // origin is the touch screen's top left, so the trail's cells sit on it anywhere on the canvas.
        private const int CellW = 8, CellH = 14;

        private int GridTexture(FhFrontend menus, List<WidgetTri> tris)
        {
            if (_gridTexture >= 0) return _gridTexture;
            var bottom = tris.FindAll(t => Tag(t) == FhFrontend.TagBottom);
            if (bottom.Count == 0) return -1;
            UiTexture tex = menus.Textures.GetOrAdd("fh/gridlines", () =>
            {
                var sum = new float[CellW * CellH * 3];
                var count = new int[CellW * CellH];
                for (int y = 14; y < 182; y++)
                {
                    for (int x = 16; x < 240; x++)
                    {
                        if (!Sample(menus.Textures, bottom, x + 0.5f, -(y + 0.5f), out float r, out float g, out float b)) continue;
                        int k = y % CellH * CellW + x % CellW;
                        sum[k * 3] += r;
                        sum[k * 3 + 1] += g;
                        sum[k * 3 + 2] += b;
                        count[k]++;
                    }
                }
                var lum = new float[CellW * CellH];
                float lo = float.MaxValue, hi = 0;
                for (int k = 0; k < lum.Length; k++)
                {
                    if (count[k] == 0) continue;
                    for (int c = 0; c < 3; c++) sum[k * 3 + c] /= count[k];
                    lum[k] = 0.3f * sum[k * 3] + 0.59f * sum[k * 3 + 1] + 0.11f * sum[k * 3 + 2];
                    lo = MathF.Min(lo, lum[k]);
                    hi = MathF.Max(hi, lum[k]);
                }
                // the lines alone: the cells' level to black, the brightest line to full
                var rgba = new byte[CellW * CellH * 4];
                for (int k = 0; k < lum.Length; k++)
                {
                    float t = hi > lo ? Math.Clamp((lum[k] - lo) / (hi - lo) * 1.25f - 0.25f, 0, 1) : 0;
                    float peak = MathF.Max(1e-3f, MathF.Max(sum[k * 3], MathF.Max(sum[k * 3 + 1], sum[k * 3 + 2])));
                    for (int c = 0; c < 3; c++) rgba[k * 4 + c] = (byte)Math.Clamp(sum[k * 3 + c] / peak * t * 255, 0, 255);
                    rgba[k * 4 + 3] = 255;
                }
                return (CellW, CellH, rgba);
            });
            _gridTexture = tex.Id;
            return _gridTexture;
        }

        // a menu-space point's colour from the triangles over it (the flat backgrounds: nearest texel, no blending)
        private static bool Sample(UiTextureCache textures, List<WidgetTri> tris, float x, float y, out float r, out float g, out float b)
        {
            r = g = b = 0;
            foreach (WidgetTri t in tris)
            {
                float d(UiVertex p, UiVertex q) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
                float d1 = d(t.A, t.B), d2 = d(t.B, t.C), d3 = d(t.C, t.A);
                if (!(d1 >= 0 && d2 >= 0 && d3 >= 0 || d1 <= 0 && d2 <= 0 && d3 <= 0) || d1 + d2 + d3 == 0) continue;
                float sum = d1 + d2 + d3, w1 = d2 / sum, w2 = d3 / sum, w3 = d1 / sum;
                if (t.TextureId < 0) return false;
                UiTexture tex = textures[t.TextureId];
                float u = t.A.U * w1 + t.B.U * w2 + t.C.U * w3, v = t.A.V * w1 + t.B.V * w2 + t.C.V * w3;
                int ix = Math.Clamp((int)MathF.Floor(u * tex.Width), 0, tex.Width - 1);
                int iy = Math.Clamp((int)MathF.Floor(v * tex.Height), 0, tex.Height - 1);
                int o = (iy * tex.Width + ix) * 4;
                r = tex.Rgba[o] / 255f;
                g = tex.Rgba[o + 1] / 255f;
                b = tex.Rgba[o + 2] / 255f;
                return true;
            }
            return false;
        }

        // a canvas point's texcoords on the grid tile (in the touch screen's pixels from its top left)
        private UiVertex GridVertex(float x, float y, float alpha)
        {
            float sx = (x - _touchAt.X) / _touchScale, sy = (y - _touchAt.Y) / _touchScale;
            return new UiVertex(x, y, sx / CellW, sy / CellH, 1, 1, 1, alpha);
        }

        // the grid's level over the picture, and its patches' at their brightest
        public static float GridBase = 0.07f, GridPatch = 0.5f;

        private void GridQuad(UiDrawList list, int grid, float alpha)
        {
            UiVertex v0 = GridVertex(0, 0, alpha), v1 = GridVertex(_w, 0, alpha), v2 = GridVertex(_w, _h, alpha), v3 = GridVertex(0, _h, alpha);
            list.Triangle(grid, UiWrap.Repeat, UiWrap.Repeat, UiBlend.Additive, v0, v1, v2);
            list.Triangle(grid, UiWrap.Repeat, UiWrap.Repeat, UiBlend.Additive, v0, v2, v3);
        }

        // soft patches of grid: each fades in and out over its own few seconds, then comes back somewhere else
        private const int PatchCount = 5;

        private void Patches(UiDrawList list, int grid, double seconds, float u)
        {
            for (int i = 0; i < PatchCount; i++)
            {
                double period = 4.5 + i * 0.85, at = seconds / period + i * 0.37;
                long cycle = (long)Math.Floor(at);
                float phase = (float)(at - cycle);
                uint h = Hash((uint)i * 7919u + (uint)cycle * 104729u);
                float x = (0.06f + 0.88f * Unit(h)) * _w, y = (0.08f + 0.84f * Unit(h >> 8 ^ h * 31)) * _h;
                float radius = (40 + 34 * Unit(h * 2654435761u)) * u;
                float strength = GridPatch * (0.55f + 0.45f * Unit(h ^ 0x5bd1e995u)) * (0.5f - 0.5f * MathF.Cos(2 * MathF.PI * phase));
                Disc(list, grid, x, y, radius, strength);
            }
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        private static float Unit(uint h) => (h & 0xffff) / 65535f;

        // a fan with a falloff ring: full in the middle, a third at half the radius, none at the rim
        private void Disc(UiDrawList list, int grid, float x, float y, float radius, float strength)
        {
            if (strength <= 0.003f) return;
            const int n = 28;
            UiVertex centre = GridVertex(x, y, strength);
            for (int k = 0; k < n; k++)
            {
                float a0 = 2 * MathF.PI * k / n, a1 = 2 * MathF.PI * (k + 1) / n;
                UiVertex m0 = GridVertex(x + MathF.Cos(a0) * radius * 0.5f, y + MathF.Sin(a0) * radius * 0.5f, strength * 0.45f);
                UiVertex m1 = GridVertex(x + MathF.Cos(a1) * radius * 0.5f, y + MathF.Sin(a1) * radius * 0.5f, strength * 0.45f);
                UiVertex o0 = GridVertex(x + MathF.Cos(a0) * radius, y + MathF.Sin(a0) * radius, 0);
                UiVertex o1 = GridVertex(x + MathF.Cos(a1) * radius, y + MathF.Sin(a1) * radius, 0);
                list.Triangle(grid, UiWrap.Repeat, UiWrap.Repeat, UiBlend.Additive, centre, m0, m1);
                list.Triangle(grid, UiWrap.Repeat, UiWrap.Repeat, UiBlend.Additive, m0, o0, o1);
                list.Triangle(grid, UiWrap.Repeat, UiWrap.Repeat, UiBlend.Additive, m0, o1, m1);
            }
        }

        // the touch screen's (or the top screen's) frame nodes of one name, and their box in menu space
        private static List<WidgetTri> Node(FhFrontend menus, List<WidgetTri> tris, string name, out float x0, out float x1, out float y0, out float y1, bool top = false)
        {
            x0 = float.MaxValue; x1 = float.MinValue; y0 = float.MaxValue; y1 = float.MinValue;
            var mine = new List<WidgetTri>();
            foreach (WidgetTri t in tris)
            {
                if (Tag(t) != FhFrontend.TagFrame || OnTop(t) != top || menus.NodeOf(t.Item).Node != name) continue;
                mine.Add(t);
                foreach (UiVertex v in new[] { t.A, t.B, t.C })
                {
                    x0 = MathF.Min(x0, v.X); x1 = MathF.Max(x1, v.X);
                    y0 = MathF.Min(y0, v.Y); y1 = MathF.Max(y1, v.Y);
                }
            }
            return mine;
        }

        private static void Draw(UiDrawList list, WidgetTri t, Func<UiVertex, UiVertex> map)
        {
            list.Triangle(t.TextureId, t.WrapS, t.WrapT, UiBlend.Alpha, map(t.A), map(t.B), map(t.C));
        }

        // the frame's header strip across the whole canvas, as the cart's runs across its touch screen (headerBar1 left
        // of the page's own header plate, headerBar5 right of it; their texture is the same down every column, so they
        // stretch): from the canvas's left edge (on a panel page the touch panel's) to its right
        private void Header(FhFrontend menus, UiDrawList list, List<WidgetTri> tris)
        {
            Rect b = _bottom;
            float canvasLeft = _panel ? b.X : 0;
            foreach ((string name, float from, float to) in new[] { ("headerBar1", canvasLeft, b.X + 16 * b.S), ("headerBar5", b.X + 128 * b.S, _w) })
            {
                List<WidgetTri> mine = Node(menus, tris, name, out float x0, out float x1, out _, out _);
                if (mine.Count == 0 || x1 <= x0) continue;
                list.Clip = UiRect.None;
                foreach (WidgetTri t in mine)
                {
                    Draw(list, t, v =>
                    {
                        v.X = from + (v.X - x0) / (x1 - x0) * (to - from);
                        v.Y = b.Y - v.Y * b.S;
                        return v;
                    });
                }
            }
        }

        // a bolt's place on the canvas: larger than on the touch screen, its box a little off the canvas's edge (the cart's
        // hangs off its screen's), its middle at the canvas's
        private bool BoltRect(FhFrontend menus, List<WidgetTri> tris, int k, float u, out Rect r, out List<WidgetTri> mine)
        {
            float s = 1.35f * u;
            mine = Node(menus, tris, k == 0 ? "lightningL" : "lightningR", out float x0, out float x1, out float y0, out float y1);
            r = default;
            if (mine.Count == 0) return false;
            float left = k == 0 ? 2 * u - (x1 - x0) * s * 0.3f : _w - 2 * u - (x1 - x0) * s * 0.7f;
            // (bolt's box x0 at `left`, its middle at the canvas's)
            r = new Rect(left - x0 * s, _h / 2 - ((0 - (y0 + y1) / 2)) * s, s);
            return true;
        }

        // The border round the canvas, drawn from the top screen's frame (frameT; its pieces are clear textures, unlike the
        // touch screen's border, which is painted into bottom_bg with the grid): its corner (FrameT2's top 16 rows, mirrored
        // for the right side as the cart mirrors it, flipped for the foot), its top line's cross-section (FrameT3, a 3 px
        // pink core) stretched along the top and the foot, and its corner's side line's (FrameT2 below the turn, a 2 px pink
        // core, as the top line; FrameT1 further down is orange) stretched down
        // each side. The side lines stand on the touch screen's border column (x 6-7 and 248-249, bottom_bg), on which each
        // bolt is centred, and break for the bolt as that does, a crossbar capping each end (rows 46 and 139). At the bolts'
        // scale (the top line still clear of the header strip).
        private void Borders(FhFrontend menus, UiDrawList list, List<WidgetTri> tris, float u)
        {
            UiTexture? corner = null, top = null;
            foreach (WidgetTri t in tris)
            {
                if (Tag(t) != FhFrontend.TagFrame || !OnTop(t) || t.TextureId < 0 || menus.NodeOf(t.Item).Node != "frameT") continue;
                UiTexture tex = menus.Textures[t.TextureId];
                if (tex.Width == 16 && tex.Height == 64) corner = tex;
                else if (tex.Width == 128 && tex.Height == 16) top = tex;
            }
            // (a page that hides the top screen's frame, the hi score ones: its textures as interned, or the pieces made
            // from them earlier)
            if (corner == null) menus.Textures.TryGet("fhmenu/frame|2|2", out corner);
            if (top == null) menus.Textures.TryGet("fhmenu/frame|3|3", out top);
            if ((corner == null || top == null) && !menus.Textures.TryGet("fh/frame/cap_down", out _)) return;
            UiTexture sideLine = menus.Textures.GetOrAdd("fh/frame/side", () => (16, 1, Crop(corner!, 0, 12, 16, 1)));
            UiTexture topLine = menus.Textures.GetOrAdd("fh/frame/top", () => (1, 16, Crop(top!, 40, 0, 1, 16)));
            UiTexture cornerTex = menus.Textures.GetOrAdd("fh/frame/corner", () => (16, 16, Crop(corner!, 0, 0, 16, 16)));
            UiTexture capUp = menus.Textures.GetOrAdd("fh/frame/cap_up", () => (16, 16, Cap(corner!, top!, up: true)));
            UiTexture capDown = menus.Textures.GetOrAdd("fh/frame/cap_down", () => (16, 16, Cap(corner!, top!, up: false)));
            float f = 1.35f * u;
            // (the textures' cores: the side line's centre at x 6, the top line's at row 6.5; the top line 2.5 px into the
            // canvas, the foot line 2.5 px off its foot)
            float topY = -4 * f, footY = _h - 12 * f;
            var cols = new float[2];
            list.Clip = UiRect.None;
            for (int k = 0; k < 2; k++)
            {
                if (!BoltRect(menus, tris, k, u, out Rect r, out _)) return;
                cols[k] = r.X + (k == 0 ? 7 : 249) * r.S;
            }
            // the corners and the lines between them along the top and the foot
            float l0 = cols[0] - 6 * f, l1 = l0 + 16 * f, r1 = cols[1] + 6 * f, r0 = r1 - 16 * f;
            Quad(list, cornerTex, l0, topY, l1, topY + 16 * f, false, false);
            Quad(list, cornerTex, r0, topY, r1, topY + 16 * f, true, false);
            Quad(list, cornerTex, l0, footY, l1, footY + 16 * f, false, true);
            Quad(list, cornerTex, r0, footY, r1, footY + 16 * f, true, true);
            if (r0 > l1)
            {
                Quad(list, topLine, l1, topY, r0, topY + 16 * f, false, false);
                Quad(list, topLine, l1, footY, r0, footY + 16 * f, false, true);
            }
            // each side: the line from the corner to the crossbar over the bolt, and from the one under it to the foot
            for (int k = 0; k < 2; k++)
            {
                BoltRect(menus, tris, k, u, out Rect r, out _);
                bool flip = k == 1;
                float x0 = flip ? cols[k] - 10 * f : cols[k] - 6 * f, x1 = x0 + 16 * f;
                float upper = r.Y + 47 * r.S, lower = r.Y + 139 * r.S;
                // (a cap's bar at its row 8.5)
                float capA = upper - 8.5f * f, capB = lower - 8.5f * f;
                if (capA > topY + 16 * f) Quad(list, sideLine, x0, topY + 16 * f, x1, capA, flip, false);
                Quad(list, capUp, x0, capA, x1, capA + 16 * f, flip, false);
                Quad(list, capDown, x0, capB, x1, capB + 16 * f, flip, false);
                if (footY > capB + 16 * f) Quad(list, sideLine, x0, capB + 16 * f, x1, footY, flip, false);
            }
        }

        private static byte[] Crop(UiTexture tex, int x0, int y0, int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Array.Copy(tex.Rgba, ((y0 + y) * tex.Width + x0) * 4, rgba, y * w * 4, w * 4);
            return rgba;
        }

        // the side line's end under (up) or over a crossbar of the top line's cross-section (10 px long, at row 8.5), each
        // texel the stronger of the two (no seam where they meet)
        private static byte[] Cap(UiTexture side, UiTexture top, bool up)
        {
            var rgba = new byte[16 * 16 * 4];
            // (the cross-sections by distance from the core's middle: the side line's at x 6, the top line's at row 6.5)
            int Side(float d) => (int)MathF.Floor(6 + d);
            int Top(float d) => (int)MathF.Floor(6.5f + d);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float bar = MathF.Sqrt(MathF.Pow(px - Math.Clamp(px, 2, 10), 2) + MathF.Pow(py - 8.5f, 2));
                    float cy = up ? Math.Clamp(py, 0, 8.5f) : Math.Clamp(py, 8.5f, 16);
                    float col = MathF.Sqrt(MathF.Pow(px - 6, 2) + MathF.Pow(py - cy, 2));
                    int bi = Top(bar), ci = Side(col);
                    int bo = bi < 16 ? (bi * top.Width + 40) * 4 : -1, co = ci < 16 ? (12 * side.Width + ci) * 4 : -1;
                    int ba = bo >= 0 ? top.Rgba[bo + 3] : 0, ca = co >= 0 ? side.Rgba[co + 3] : 0;
                    if (ba == 0 && ca == 0) continue;
                    byte[] src = ba > ca ? top.Rgba : side.Rgba;
                    Array.Copy(src, ba > ca ? bo : co, rgba, (y * 16 + x) * 4, 4);
                }
            }
            return rgba;
        }

        // a texture over a canvas box, mirrored across and/or flipped
        private static void Quad(UiDrawList list, UiTexture tex, float x0, float y0, float x1, float y1, bool mirror, bool flip)
        {
            float u0 = mirror ? 1 : 0, u1 = 1 - u0, v0 = flip ? 1 : 0, v1 = 1 - v0;
            var p0 = new UiVertex(x0, y0, u0, v0, 1, 1, 1, 1);
            var p1 = new UiVertex(x1, y0, u1, v0, 1, 1, 1, 1);
            var p2 = new UiVertex(x1, y1, u1, v1, 1, 1, 1, 1);
            var p3 = new UiVertex(x0, y1, u0, v1, 1, 1, 1, 1);
            list.Triangle(tex.Id, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, p0, p1, p2);
            list.Triangle(tex.Id, UiWrap.Clamp, UiWrap.Clamp, UiBlend.Alpha, p0, p2, p3);
        }

        // The top screen's moving line (frameBarTT: 1 px across it 15.5 rows under its top, a step up at x 34-90, its node
        // animation bobbing it a few rows) across the canvas, edge to edge as the cart's runs across its screen: its step at
        // the bolts' scale from the frame's left side line (Borders), the plain run after it stretched to the right edge.
        // Where the header strip is up top (the one-screen pages) it rests just under the page's header plate (controls x
        // 16-128, its art down to row 21), unbroken, its step going up behind the strip; on a panel page 15.5 rows under the
        // frame's top line, as the cart's. Drawn with the edges, over the pages' art.
        private void TopBar(FhFrontend menus, UiDrawList list, List<WidgetTri> tris, float u)
        {
            List<WidgetTri> bar = Node(menus, tris, "frameBarTT", out _, out _, out _, out _, top: true);
            if (bar.Count == 0 || !BoltRect(menus, tris, 0, u, out Rect r, out _)) return;
            float left = r.X + 7 * r.S;
            Rect b = _bottom;
            float f = 1.35f * u, strip = b.Y + 16 * b.S, step = 91;
            // (its rest row 176.5; it bobs up to 1.1 rows over it)
            bool header = !_panel && Node(menus, tris, "headerBar1", out _, out _, out _, out _).Count > 0;
            float rest = !header ? -4 * f + (192 - 176.5f) * f : b.Y + 21 * b.S + 2 * f;
            float stepEnd = left + (step - 6) * f;
            list.Clip = !header ? UiRect.None : new UiRect(0, strip, _w, _h - strip);
            foreach (WidgetTri t in bar)
            {
                Draw(list, t, v =>
                {
                    v.X = v.X <= 6 ? v.X / 6 * left
                        : v.X <= step ? left + (v.X - 6) * f
                        : stepEnd + (v.X - step) / (256 - step) * (_w - stepEnd);
                    v.Y = rest + (176.5f - v.Y) * f;
                    return v;
                });
            }
            list.Clip = UiRect.None;
        }

        // the frame's lightning bolts on the canvas's left and right edges (on the static border, Borders) and the frame's
        // moving border lines beside them (the cart's frameBarBL/BR, their node animation sliding them about), from the
        // canvas's top to its foot; and its signal icon (the LAN pages) in the top right corner
        private void Edges(FhFrontend menus, UiDrawList list, List<WidgetTri> tris, float u)
        {
            TopBar(menus, list, tris, u);
            Rect b = _bottom;
            float stripTop = b.Y + 8 * b.S, stripFoot = b.Y + 16 * b.S, stripLeft = _panel ? b.X : 0;
            bool header = Node(menus, tris, "headerBar1", out _, out _, out _, out _).Count > 0;
            for (int k = 0; k < 2; k++)
            {
                if (!BoltRect(menus, tris, k, u, out Rect r, out List<WidgetTri> mine)) continue;
                float s = r.S;
                // (the canvas's top to its foot, as the cart's run its screen's; behind the header strip where it crosses it,
                // as the cart's go behind its strip)
                List<WidgetTri> bar = Node(menus, tris, k == 0 ? "frameBarBL" : "frameBarBR", out float bx0, out _, out float by0, out float by1);
                if (bar.Count > 0 && by1 > by0)
                {
                    UiRect[] parts = !header || r.X + bx0 * s < stripLeft ? new[] { UiRect.None }
                        : new[] { new UiRect(0, 0, _w, stripTop), new UiRect(0, stripFoot, _w, _h - stripFoot) };
                    foreach (UiRect part in parts)
                    {
                        list.Clip = part;
                        foreach (WidgetTri t in bar)
                        {
                            Draw(list, t, v =>
                            {
                                v.X = r.X + v.X * s;
                                v.Y = (by1 - v.Y) / (by1 - by0) * _h;
                                return v;
                            });
                        }
                    }
                }
                list.Clip = UiRect.None;
                foreach (WidgetTri t in mine) Draw(list, t, r, false, UiBlend.Alpha);
            }
            if (_panel) return;
            foreach (WidgetTri t in tris)
            {
                if (Tag(t) != FhFrontend.TagFrame || !OnTop(t) || !menus.NodeOf(t.Item).Node.StartsWith("icon")) continue;
                // (its top-right corner, 256 x 0 of the top screen, to the canvas's)
                float si = Snap(u);
                Draw(list, t, new Rect(_w - 256 * si - 2 * u, 2 * u, si), true, UiBlend.Alpha);
            }
        }
    }
}
