using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// The HUD editor (owner 2026-11-15, wave 2): LOOK > HUD > CUSTOMIZE HUD in the pause menu. Over the paused game (the
// world dimmed, CampaignRenderer.HudLayout.cs), each part of the HUD gets a box: tap one to pick it, drag it to move it
// (snapped to a 1/48 screen grid), drag a corner to size it; the panel sets its size and opacity, hides it or puts it
// back. Like the HUD edit modes of the popular mobile shooters and the touch layout editor (MphRead.Android
// TouchLayoutEditor.cs): one panel, draggable by its title, out of the way while a part is dragged. A pad works too:
// L / R pick, the D-pad moves, the right stick or the wheel sizes, L2 / R2 fade, X hides, Y resets, Select resets all,
// B cancels, Start saves. The edits show live on the HUD itself (HudLayout.Edits is the working copy while open).
internal sealed class HudEditor
{
    static readonly float[] Yellow = { 1f, 0.816f, 0.251f }; // #FFD040, the touch layout editor's ring
    static readonly float[] Cyan = { 0.35f, 0.75f, 0.95f };

    readonly HudLayout _layout;
    readonly Dictionary<string, LayoutEdit> _saved;
    readonly Action<bool> _done;
    readonly UiDrawList _list;
    static MenuFont? s_font;
    static UiTextureCache? s_fontCache;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;
    string? _selected;
    int _width, _height;

    // the drag in progress
    enum Drag { None, Pending, Element, Resize, Panel, Size, Opacity, Button }
    Drag _drag;
    float _downX, _downY, _startCx, _startCy, _startScale, _startDist;
    string _button = "";
    // the panel's top left once the player moved it (else it keeps clear of the picked part by itself)
    bool _panelMoved;
    float _panelX, _panelY;
    // the right stick's last push (ScrollStick), sizing while held
    float _stick;
    double _stickAt;
    string _toast = "";
    double _toastUntil;

    struct Rect
    {
        public float X0, Y0, X1, Y1;
        public Rect(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public bool Contains(float x, float y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
        public float W => X1 - X0;
        public float H => Y1 - Y0;
    }

    // the panel's parts, laid out each frame (Layout)
    Rect _panel, _title, _prev, _next, _sizeTrack, _opacityTrack, _hide, _reset, _resetAll, _cancel, _save;

    public Dictionary<string, LayoutEdit> Work => _layout.Edits;

    public HudEditor(HudLayout layout, UiTextureCache textures, Action<bool> done)
    {
        _layout = layout;
        _done = done;
        _saved = HudLayout.Clone(layout.Edits);
        layout.Edits = HudLayout.Clone(layout.Edits);
        layout.Editing = true;
        _list = new UiDrawList(textures);
        if (s_font == null || !ReferenceEquals(s_fontCache, textures))
        {
            s_font = new MenuFont(textures);
            s_fontCache = textures;
        }
        _selected = layout.Order.Count > 0 ? layout.Order[0] : null;
        _last = _clock.Elapsed.TotalSeconds;
    }

    float U => _height / 360f;
    float Grid => _height / 48f;

    void Finish(bool save)
    {
        if (!save) _layout.Edits = HudLayout.Clone(_saved);
        _layout.Editing = false;
        _done(save);
    }

    LayoutEdit Edit(string id)
    {
        if (!_layout.Edits.TryGetValue(id, out LayoutEdit? edit))
        {
            edit = new LayoutEdit();
            _layout.Edits[id] = edit;
        }
        return edit;
    }

    void Tidy(string id)
    {
        if (_layout.Edits.TryGetValue(id, out LayoutEdit? edit) && edit.IsDefault) _layout.Edits.Remove(id);
    }

    float Scale(string id) => _layout.Edits.TryGetValue(id, out LayoutEdit? e) ? e.Scale : 1;
    float Opacity(string id) => _layout.Edits.TryGetValue(id, out LayoutEdit? e) ? e.Opacity : 1;
    bool Hidden(string id) => _layout.Edits.TryGetValue(id, out LayoutEdit? e) && e.Hidden;

    bool HasSelected => _selected != null && _layout.Homes.ContainsKey(_selected);

    void Toast(string text)
    {
        _toast = text;
        _toastUntil = _clock.Elapsed.TotalSeconds + 2;
    }

    // ---- changes ----

    void MoveTo(string id, float cx, float cy)
    {
        if (!HudLayout.Movable(id))
        {
            Toast("THE RETICLE STAYS ON YOUR AIM");
            return;
        }
        float g = Grid;
        cx = Math.Clamp(MathF.Round(cx / g) * g, 0, _width);
        cy = Math.Clamp(MathF.Round(cy / g) * g, 0, _height);
        LayoutEdit edit = Edit(id);
        string anchor = LayoutEdit.NearestAnchor(cx, cy, _width, _height);
        (float x, float y) = LayoutEdit.Unplace(anchor, cx, cy, _width, _height);
        edit.Anchor = anchor;
        edit.X = x;
        edit.Y = y;
        HudLayout.Box home = _layout.Homes[id];
        // back on its own place: as shipped again (no stored offset)
        if (MathF.Abs(cx - MathF.Round(home.CentreX / g) * g) < 0.5f && MathF.Abs(cy - MathF.Round(home.CentreY / g) * g) < 0.5f)
        {
            edit.X = edit.Y = null;
            edit.Anchor = null;
        }
        Tidy(id);
    }

    void SetScale(string id, float scale)
    {
        Edit(id).Scale = LayoutEdit.ClampScale(MathF.Round(scale * 100) / 100);
        Tidy(id);
    }

    void SetOpacity(string id, float opacity)
    {
        Edit(id).Opacity = Math.Clamp(MathF.Round(opacity * 20) / 20, 0.1f, 1);
        Tidy(id);
    }

    void ToggleHidden(string id)
    {
        LayoutEdit edit = Edit(id);
        edit.Hidden = !edit.Hidden;
        Tidy(id);
    }

    void ResetElement(string id)
    {
        _layout.Edits.Remove(id);
    }

    void Cycle(int step)
    {
        List<string> order = _layout.Order;
        if (order.Count == 0) return;
        int i = _selected == null ? -1 : order.IndexOf(_selected);
        i = ((i < 0 ? (step > 0 ? -1 : 0) : i) + step + order.Count) % order.Count;
        _selected = order[i];
    }

    // ---- input ----

    public void Press(MenuKeys keys)
    {
        if ((keys & MenuKeys.B) != 0) { Finish(false); return; }
        if ((keys & MenuKeys.Start) != 0) { Finish(true); return; }
        if ((keys & MenuKeys.Select) != 0) _layout.Edits.Clear();
        if ((keys & MenuKeys.L) != 0) Cycle(-1);
        if ((keys & MenuKeys.R) != 0) Cycle(1);
        if ((keys & (MenuKeys.Up | MenuKeys.Down | MenuKeys.Left | MenuKeys.Right)) != 0)
        {
            int dx = ((keys & MenuKeys.Right) != 0 ? 1 : 0) - ((keys & MenuKeys.Left) != 0 ? 1 : 0);
            int dy = ((keys & MenuKeys.Up) != 0 ? 1 : 0) - ((keys & MenuKeys.Down) != 0 ? 1 : 0);
            Navigate(dx, dy);
        }
        if (!HasSelected) return;
        string id = _selected!;
        if ((keys & MenuKeys.X) != 0) ToggleHidden(id);
        if ((keys & MenuKeys.Y) != 0) ResetElement(id);
        if ((keys & MenuKeys.L2) != 0) SetOpacity(id, Opacity(id) - 0.1f);
        if ((keys & MenuKeys.R2) != 0) SetOpacity(id, Opacity(id) + 0.1f);
    }

    // one grid step (dy +1 = up)
    public void Navigate(int dx, int dy)
    {
        if (!HasSelected || (dx == 0 && dy == 0)) return;
        string id = _selected!;
        HudLayout.Box box = _layout.Drawn(id);
        MoveTo(id, box.CentreX + dx * Grid, box.CentreY - dy * Grid);
    }

    // the mouse wheel: 5% a notch (up = bigger)
    public void ScrollWheel(float notches)
    {
        if (!HasSelected) return;
        SetScale(_selected!, Scale(_selected!) + 0.05f * notches);
    }

    // the right stick held (negative = up = bigger): sized in Draw while it's pushed
    public void ScrollStick(float y)
    {
        _stick = y;
        _stickAt = _clock.Elapsed.TotalSeconds;
    }

    public void Touch(float x, float y)
    {
        Touch(x, y, 0);
        Touch(x, y, 2);
    }

    // kind 0 down, 1 move, 2 up, 3 cancel (screen pixels)
    public void Touch(float x, float y, int kind)
    {
        if (_width <= 0 || _height <= 0) return;
        switch (kind)
        {
            case 0:
                Down(x, y);
                break;
            case 1:
                MoveDrag(x, y);
                break;
            case 2:
                MoveDrag(x, y);
                if (_drag == Drag.Button && Hit(_button).Contains(x, y)) Click(_button);
                _drag = Drag.None;
                break;
            default:
                _drag = Drag.None;
                break;
        }
    }

    void Down(float x, float y)
    {
        Layout();
        _downX = x;
        _downY = y;
        _drag = Drag.None;
        if (_panel.Contains(x, y))
        {
            foreach (string name in new[] { "prev", "next", "hide", "reset", "resetall", "cancel", "save" })
            {
                if (Hit(name).Contains(x, y))
                {
                    _drag = Drag.Button;
                    _button = name;
                    return;
                }
            }
            if (HasSelected && Grow(_sizeTrack, 4 * U).Contains(x, y))
            {
                _drag = Drag.Size;
                MoveDrag(x, y);
                return;
            }
            if (HasSelected && Grow(_opacityTrack, 4 * U).Contains(x, y))
            {
                _drag = Drag.Opacity;
                MoveDrag(x, y);
                return;
            }
            // the title bar (or anywhere else on the panel): moves the panel
            _drag = Drag.Panel;
            _startCx = _panel.X0;
            _startCy = _panel.Y0;
            return;
        }
        if (HasSelected)
        {
            HudLayout.Box box = _layout.Drawn(_selected!);
            float reach = 14 * U;
            foreach ((float hx, float hy) in Corners(box))
            {
                if (MathF.Abs(x - hx) <= reach && MathF.Abs(y - hy) <= reach)
                {
                    _drag = Drag.Resize;
                    _startScale = Scale(_selected!);
                    _startDist = MathF.Max(Distance(x, y, box.CentreX, box.CentreY), 1);
                    _startCx = box.CentreX;
                    _startCy = box.CentreY;
                    return;
                }
            }
        }
        string? picked = Pick(x, y);
        if (picked == null) return;
        _selected = picked;
        HudLayout.Box drawn = _layout.Drawn(picked);
        _startCx = drawn.CentreX;
        _startCy = drawn.CentreY;
        _drag = Drag.Pending;
    }

    void MoveDrag(float x, float y)
    {
        float u = U;
        switch (_drag)
        {
            case Drag.Pending:
                if (Distance(x, y, _downX, _downY) < 4 * u) return;
                if (!HudLayout.Movable(_selected!))
                {
                    Toast("THE RETICLE STAYS ON YOUR AIM");
                    _drag = Drag.None;
                    return;
                }
                _drag = Drag.Element;
                goto case Drag.Element;
            case Drag.Element:
                if (HasSelected) MoveTo(_selected!, _startCx + x - _downX, _startCy + y - _downY);
                break;
            case Drag.Resize:
                if (HasSelected) SetScale(_selected!, _startScale * Distance(x, y, _startCx, _startCy) / _startDist);
                break;
            case Drag.Panel:
                _panelMoved = true;
                _panelX = _startCx + x - _downX;
                _panelY = _startCy + y - _downY;
                break;
            case Drag.Size:
                if (HasSelected)
                {
                    float f = Math.Clamp((x - _sizeTrack.X0) / _sizeTrack.W, 0, 1);
                    SetScale(_selected!, LayoutEdit.MinScale + f * (LayoutEdit.MaxScale - LayoutEdit.MinScale));
                }
                break;
            case Drag.Opacity:
                if (HasSelected)
                {
                    float f = Math.Clamp((x - _opacityTrack.X0) / _opacityTrack.W, 0, 1);
                    SetOpacity(_selected!, 0.1f + f * 0.9f);
                }
                break;
        }
    }

    void Click(string name)
    {
        switch (name)
        {
            case "prev": Cycle(-1); break;
            case "next": Cycle(1); break;
            case "hide": if (HasSelected) ToggleHidden(_selected!); break;
            case "reset": if (HasSelected) ResetElement(_selected!); break;
            case "resetall": _layout.Edits.Clear(); Toast("EVERY PART AS SHIPPED"); break;
            case "cancel": Finish(false); break;
            case "save": Finish(true); break;
        }
    }

    // the smallest part under (x, y), a little margin around each so small icons can be hit
    string? Pick(float x, float y)
    {
        string? best = null;
        float bestArea = float.MaxValue;
        float pad = 6 * U;
        foreach (string id in _layout.Order)
        {
            if (!_layout.Homes.ContainsKey(id)) continue;
            HudLayout.Box b = _layout.Drawn(id);
            if (x < b.Left - pad || x > b.Right + pad || y < b.Top - pad || y > b.Bottom + pad) continue;
            float area = MathF.Max(b.Width, 1) * MathF.Max(b.Height, 1);
            if (area < bestArea)
            {
                bestArea = area;
                best = id;
            }
        }
        return best;
    }

    static float Distance(float x0, float y0, float x1, float y1) => MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    static (float, float)[] Corners(HudLayout.Box b) =>
        new[] { (b.Left, b.Top), (b.Right, b.Top), (b.Left, b.Bottom), (b.Right, b.Bottom) };

    static Rect Grow(Rect r, float by) => new(r.X0 - by, r.Y0 - by, r.X1 + by, r.Y1 + by);

    Rect Hit(string name) => name switch
    {
        "prev" => _prev,
        "next" => _next,
        "hide" => _hide,
        "reset" => _reset,
        "resetall" => _resetAll,
        "cancel" => _cancel,
        "save" => _save,
        _ => default
    };

    // ---- layout + drawing ----

    void Layout()
    {
        float u = U, w = _width, h = _height;
        float pw = MathF.Min(w - 16 * u, 300 * u), pad = 6 * u;
        float ph = pad + 112 * u + pad;
        float px, py;
        if (_panelMoved)
        {
            px = Math.Clamp(_panelX, 0, MathF.Max(0, w - pw));
            py = Math.Clamp(_panelY, 0, MathF.Max(0, h - ph));
        }
        else
        {
            px = (w - pw) / 2;
            // clear of the picked part: the half of the screen it isn't in
            bool lower = !HasSelected || _layout.Drawn(_selected!).CentreY < h / 2;
            py = lower ? h - ph - 10 * u : 10 * u;
        }
        _panel = new Rect(px, py, px + pw, py + ph);
        float y = py + pad;
        float x0 = px + pad, x1 = px + pw - pad;
        _title = new Rect(x0, y, x1, y + 14 * u);
        _prev = new Rect(x0, y, x0 + 20 * u, y + 14 * u);
        _next = new Rect(x1 - 20 * u, y, x1, y + 14 * u);
        y += 18 * u;
        float trackL = x0 + 66 * u, trackR = x1 - 40 * u;
        _sizeTrack = new Rect(trackL, y + 5 * u, trackR, y + 9 * u);
        y += 17 * u;
        _opacityTrack = new Rect(trackL, y + 5 * u, trackR, y + 9 * u);
        y += 20 * u;
        float gap = 4 * u, half = (x1 - x0 - gap) / 2;
        _hide = new Rect(x0, y, x0 + half, y + 18 * u);
        _reset = new Rect(x1 - half, y, x1, y + 18 * u);
        y += 22 * u;
        float third = (x1 - x0 - 2 * gap) / 3;
        _resetAll = new Rect(x0, y, x0 + third, y + 18 * u);
        _cancel = new Rect(x0 + third + gap, y, x0 + 2 * third + gap, y + 18 * u);
        _save = new Rect(x1 - third, y, x1, y + 18 * u);
    }

    public void Draw(UiGlDrawer drawer, int width, int height)
    {
        _width = width;
        _height = height;
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        if (HasSelected && now - _stickAt < 0.25 && MathF.Abs(_stick) > 0.2f)
        {
            SetScale(_selected!, Scale(_selected!) - _stick * dt * 0.8f);
        }
        if (_selected == null && _layout.Order.Count > 0) _selected = _layout.Order[0];
        Layout();
        float u = U;
        _list.Clear();
        bool dragging = _drag is Drag.Element or Drag.Resize;
        foreach (string id in _layout.Order)
        {
            if (!_layout.Homes.ContainsKey(id) || id == _selected) continue;
            HudLayout.Box b = _layout.Drawn(id);
            float a = _layout.Live.Contains(id) ? 0.55f : 0.3f;
            if (Hidden(id)) Dashed(b, MathF.Max(1, 0.75f * u), 0.8f, 0.8f, 0.8f, a);
            else Frame(b.Left, b.Top, b.Right, b.Bottom, MathF.Max(1, 0.75f * u), 1, 1, 1, a);
        }
        if (HasSelected)
        {
            HudLayout.Box b = _layout.Drawn(_selected!);
            float t = MathF.Max(2, 1.5f * u);
            if (Hidden(_selected!)) Dashed(b, t, Yellow[0], Yellow[1], Yellow[2], 1);
            else Frame(b.Left - t, b.Top - t, b.Right + t, b.Bottom + t, t, Yellow[0], Yellow[1], Yellow[2], 1);
            float hs = 4 * u;
            foreach ((float hx, float hy) in Corners(b))
            {
                Fill(hx - hs, hy - hs, hx + hs, hy + hs, Yellow[0], Yellow[1], Yellow[2], 1);
                Fill(hx - hs + u, hy - hs + u, hx + hs - u, hy + hs - u, 0.1f, 0.1f, 0.1f, 1);
            }
            if (!dragging) Label(b);
        }
        if (!dragging) DrawPanel();
        if (now < _toastUntil && _toast.Length > 0)
        {
            float tw = s_font!.MeasureCanvas(_toast, u) + 12 * u;
            float ty = _panel.Y0 > _height / 2 ? _panel.Y0 - 16 * u : _panel.Y1 + 4 * u;
            Fill((_width - tw) / 2, ty, (_width + tw) / 2, ty + 12 * u, 0, 0, 0, 0.75f);
            Text(_toast, _width / 2f, ty + 2 * u, 2, u, 1, 1, 1, 1);
        }
        drawer.Draw(_list, width, height, false);
    }

    // the picked part's name over (or under) its box
    void Label(HudLayout.Box b)
    {
        float u = U;
        string name = HudLayout.Name(_selected!);
        float tw = s_font!.MeasureCanvas(name, u) + 8 * u;
        float y = b.Top - 15 * u;
        if (y < 2 * u) y = b.Bottom + 4 * u;
        float cx = Math.Clamp(b.CentreX, tw / 2, _width - tw / 2);
        Fill(cx - tw / 2, y, cx + tw / 2, y + 11 * u, Yellow[0], Yellow[1], Yellow[2], 1);
        Text(name, cx, y + 1.5f * u, 2, u, 0.05f, 0.05f, 0.05f, 1);
    }

    void DrawPanel()
    {
        float u = U;
        Rect p = _panel;
        Fill(p.X0, p.Y0, p.X1, p.Y1, 0.03f, 0.06f, 0.1f, 0.9f);
        Frame(p.X0, p.Y0, p.X1, p.Y1, MathF.Max(1, u), Cyan[0], Cyan[1], Cyan[2], 0.9f);
        bool has = HasSelected;
        string id = _selected ?? "";
        // title: < NAME >, draggable
        Button(_prev, "<", true, false);
        Button(_next, ">", true, false);
        string title = _layout.Order.Count == 0 ? "NO HUD ON SCREEN" : has ? HudLayout.Name(id) : "TAP A PART OF THE HUD";
        if (has && Hidden(id)) title += " (HIDDEN)";
        Text(title, (_title.X0 + _title.X1) / 2, _title.Y0 + 3 * u, 2, u, Yellow[0], Yellow[1], Yellow[2], 1);
        Slider(_sizeTrack, "SIZE", has ? (Scale(id) - LayoutEdit.MinScale) / (LayoutEdit.MaxScale - LayoutEdit.MinScale) : 1f / 3,
            has ? Percent(Scale(id)) : "-", has);
        Slider(_opacityTrack, "OPACITY", has ? (Opacity(id) - 0.1f) / 0.9f : 1, has ? Percent(Opacity(id)) : "-", has);
        Button(_hide, has && Hidden(id) ? "SHOW" : "HIDE", has, false);
        Button(_reset, "RESET", has, false);
        Button(_resetAll, "RESET ALL", true, false);
        Button(_cancel, "CANCEL", true, false);
        Button(_save, "SAVE", true, true);
        // the pad's keys (touch: tap, drag, the corners)
        Text("L/R PICK  D-PAD MOVE  R-STICK SIZE  L2/R2 FADE", (p.X0 + p.X1) / 2, _save.Y1 + 3 * u, 2, 0.7f * u, 0.6f, 0.7f, 0.8f, 1);
        Text("X HIDE  Y RESET  B CANCEL  START SAVE", (p.X0 + p.X1) / 2, _save.Y1 + 10 * u, 2, 0.7f * u, 0.6f, 0.7f, 0.8f, 1);
    }

    static string Percent(float f) => (int)MathF.Round(f * 100) + "%";

    void Slider(Rect track, string label, float f, string value, bool enabled)
    {
        float u = U, a = enabled ? 1 : 0.35f;
        Text(label, _panel.X0 + 6 * u, track.Y0 - 2 * u, 0, u, 0.85f, 0.9f, 1, a);
        Fill(track.X0, track.Y0, track.X1, track.Y1, 0.2f, 0.25f, 0.3f, a);
        float fx = track.X0 + Math.Clamp(f, 0, 1) * track.W;
        Fill(track.X0, track.Y0, fx, track.Y1, Cyan[0], Cyan[1], Cyan[2], a);
        float k = 5 * u, cy = (track.Y0 + track.Y1) / 2;
        Fill(fx - k / 2, cy - k, fx + k / 2, cy + k, 1, 1, 1, a);
        Text(value, track.X1 + 36 * u, track.Y0 - 2 * u, 1, u, 1, 1, 1, a);
    }

    void Button(Rect r, string label, bool enabled, bool accent)
    {
        float u = U, a = enabled ? 1 : 0.35f;
        bool down = _drag == Drag.Button && Hit(_button).X0 == r.X0 && Hit(_button).Y0 == r.Y0;
        float shade = down ? 0.32f : 0.14f;
        Fill(r.X0, r.Y0, r.X1, r.Y1, shade * 0.8f, shade * 1.2f, shade * 1.6f, 0.95f * a);
        Frame(r.X0, r.Y0, r.X1, r.Y1, MathF.Max(1, 0.75f * u), Cyan[0], Cyan[1], Cyan[2], 0.6f * a);
        float[] c = accent ? Yellow : new[] { 1f, 1f, 1f };
        Text(label, (r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2 - 4 * u, 2, u, c[0], c[1], c[2], a);
    }

    void Text(string text, float x, float y, int align, float scale, float r, float g, float b, float a)
    {
        s_font!.EmitCanvas(text, x, y, align, scale, r, g, b, a, _list);
    }

    void Fill(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        _list.Quad(-1, x0, y0, x1, y1, 0, 0, 0, 0, r, g, b, a);
    }

    void Frame(float x0, float y0, float x1, float y1, float t, float r, float g, float b, float a)
    {
        Fill(x0, y0, x1, y0 + t, r, g, b, a);
        Fill(x0, y1 - t, x1, y1, r, g, b, a);
        Fill(x0, y0 + t, x0 + t, y1 - t, r, g, b, a);
        Fill(x1 - t, y0 + t, x1, y1 - t, r, g, b, a);
    }

    // a hidden part: a dashed box
    void Dashed(HudLayout.Box box, float t, float r, float g, float b, float a)
    {
        float dash = 4 * U;
        for (float x = box.Left; x < box.Right; x += dash * 2)
        {
            float x1 = MathF.Min(x + dash, box.Right);
            Fill(x, box.Top, x1, box.Top + t, r, g, b, a);
            Fill(x, box.Bottom - t, x1, box.Bottom, r, g, b, a);
        }
        for (float y = box.Top; y < box.Bottom; y += dash * 2)
        {
            float y1 = MathF.Min(y + dash, box.Bottom);
            Fill(box.Left, y, box.Left + t, y1, r, g, b, a);
            Fill(box.Right - t, y, box.Right, y1, r, g, b, a);
        }
    }
}
