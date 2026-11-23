using System;
using System.Collections.Generic;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using MphRecomp.Config;

// CONTROLS > TOUCH > EDIT TOUCH LAYOUT (owner 2026-11-15, wave 2, for finRatman): the touch controls' layout editor, a
// full-screen view on top of the paused game (CampaignActivity: the pause menu's row) or the main menu (GameActivity,
// with a MATCH / CAMPAIGN switch). Every button of the layout shows, the ones that come and go (the drone's LASER, TALK,
// CHAT, OMEGA, ZOOM, SPIDER) with a caption saying when.
//
// Drag a button to move it (it snaps to a 1/48-height grid when let go); tap it to pick it, then drag a corner handle
// or the SIZE slider to size it, OPACITY to fade it, HIDE to take it off the screen (a hidden button shows here as a
// dashed ghost, so it can come back) and RESET BUTTON to put it back as shipped. ALL BUTTONS fades the whole layout.
// The panel holding these moves by its title bar and gets out of the way while a button is dragged. SAVE writes the
// edits to RecompSettings.TouchLayouts / TouchOpacity (MatchTouchOverlay.ReloadSettings applies them); CANCEL (or Back)
// drops them, asking once if anything changed.
//
// A moved button is stored as an offset from the screen's nearest anchor (corner, edge or centre) in screen heights
// (Config/LayoutEdit.cs), so a layout made on one screen shape stays sensible on another.
namespace MphRecomp.App;

internal sealed class TouchLayoutEditor : View
{
    enum Part { None, Header, MatchTab, CampaignTab, Size, Opacity, Hide, Reset, All, ResetAll, Cancel, Save }
    enum Drag { None, Pending, Button, Handle, Panel, Slider, Tap }

    static readonly Color Accent = Color.Argb(255, 255, 208, 64);

    readonly string _settingsPath;
    readonly bool _allowSwitch;
    readonly Action<bool> _closed;
    // working copies of the two layouts' edits ([0] match, [1] campaign) and ALL BUTTONS (0.1-1)
    readonly Dictionary<string, LayoutEdit>[] _layouts = { new(), new() };
    float _all;
    bool _campaign;
    MatchTouchOverlay.Spot[] _spots;
    int _selected = -1;
    bool _dirty, _confirmCancel, _confirmResetAll, _closedOnce, _saveFailed;

    int _pointer = -1;
    Drag _drag;
    Part _part;
    float _downX, _downY, _grabX, _grabY, _startDist, _startScale;
    float _panelX = float.NaN, _panelY;
    readonly RectF _panel = new();
    RectF _nameRow = new();
    readonly Dictionary<Part, RectF> _rects = new();

    readonly float _dp;
    readonly Paint _fill = new() { AntiAlias = true };
    readonly Paint _ring = new() { AntiAlias = true, StrokeWidth = 4 };
    readonly Paint _ghost = new() { AntiAlias = true };
    readonly Paint _line = new() { AntiAlias = true };
    readonly Paint _text = new() { AntiAlias = true, TextAlign = Paint.Align.Center, Color = Color.White };
    readonly Paint _left = new() { AntiAlias = true, TextAlign = Paint.Align.Left, Color = Color.White };

    public TouchLayoutEditor(Context context, bool campaign, string settingsPath, bool allowSwitch, Action<bool> closed)
        : base(context)
    {
        _settingsPath = settingsPath;
        _allowSwitch = allowSwitch;
        _closed = closed;
        _campaign = campaign;
        _spots = MatchTouchOverlay.Spots(campaign);
        _dp = context.Resources?.DisplayMetrics?.Density ?? 2;
        RecompSettings settings = RecompSettings.Load(settingsPath);
        for (int i = 0; i < 2; i++)
        {
            if (settings.TouchLayouts != null && settings.TouchLayouts.TryGetValue(MatchTouchOverlay.LayoutKey(i == 1),
                out Dictionary<string, LayoutEdit>? edits) && edits != null)
            {
                foreach ((string label, LayoutEdit edit) in edits)
                {
                    if (edit != null) _layouts[i][label] = edit.Copy();
                }
            }
        }
        _all = Math.Clamp(settings.TouchOpacity, 10, 100) / 100f;
        _ring.SetStyle(Paint.Style.Stroke);
        _ghost.SetStyle(Paint.Style.Stroke);
        _ghost.StrokeWidth = 2 * _dp;
        _ghost.SetPathEffect(new DashPathEffect(new[] { 6 * _dp, 5 * _dp }, 0));
        _line.SetStyle(Paint.Style.Stroke);
        _text.SetTypeface(Typeface.DefaultBold);
        _left.SetTypeface(Typeface.DefaultBold);
    }

    // the host's Back key: as CANCEL
    public void Back()
    {
        Act(Part.Cancel);
        Invalidate();
    }

    Dictionary<string, LayoutEdit> Edits => _layouts[_campaign ? 1 : 0];

    LayoutEdit? EditOf(int i) => Edits.TryGetValue(_spots[i].Label, out LayoutEdit? e) ? e : null;

    LayoutEdit Edit(int i)
    {
        if (!Edits.TryGetValue(_spots[i].Label, out LayoutEdit? e))
        {
            Edits[_spots[i].Label] = e = new LayoutEdit();
        }
        _dirty = true;
        return e;
    }

    // an edit back to "as shipped" leaves no entry
    void Prune(int i)
    {
        if (EditOf(i) is { IsDefault: true }) Edits.Remove(_spots[i].Label);
    }

    (float cx, float cy, float r) PlaceOf(int i)
    {
        MatchTouchOverlay.Spot s = _spots[i];
        return MatchTouchOverlay.Place(s.X, s.Y, s.R, EditOf(i), Width, Height);
    }

    void SetCentre(int i, float px, float py, bool snap)
    {
        LayoutEdit e = Edit(i);
        float r = _spots[i].R * Height * LayoutEdit.ClampScale(e.Scale);
        if (snap)
        {
            float grid = Height / 48f;
            px = MathF.Round(px / grid) * grid;
            py = MathF.Round(py / grid) * grid;
        }
        // kept whole on the screen
        px = Math.Clamp(px, MathF.Min(r, Width / 2f), MathF.Max(Width - r, Width / 2f));
        py = Math.Clamp(py, MathF.Min(r, Height / 2f), MathF.Max(Height - r, Height / 2f));
        string anchor = LayoutEdit.NearestAnchor(px, py, Width, Height);
        (float x, float y) = LayoutEdit.Unplace(anchor, px, py, Width, Height);
        e.Anchor = anchor;
        e.X = MathF.Round(x, 4);
        e.Y = MathF.Round(y, 4);
    }

    static float Snap20(float v) => MathF.Round(v * 20) / 20; // 5% steps

    void SetScale(int i, float scale)
    {
        Edit(i).Scale = Snap20(LayoutEdit.ClampScale(scale));
        Prune(i);
    }

    void SetOpacity(int i, float opacity)
    {
        Edit(i).Opacity = Snap20(Math.Clamp(opacity, 0.1f, 1f));
        Prune(i);
    }

    int ButtonAt(float x, float y)
    {
        for (int i = _spots.Length - 1; i >= 0; i--) // the one drawn on top first
        {
            (float cx, float cy, float r) = PlaceOf(i);
            r = MathF.Max(r * 1.1f, 20 * _dp);
            if (Sq(x - cx) + Sq(y - cy) <= r * r) return i;
        }
        return -1;
    }

    bool HandleAt(float x, float y)
    {
        if (_selected == -1) return false;
        (float cx, float cy, float r) = PlaceOf(_selected);
        if (Sq(x - cx) + Sq(y - cy) < Sq(r * 0.9f)) return false; // inside the button: a move, even on a small one
        float reach = Sq(22 * _dp);
        return Sq(x - (cx - r)) + Sq(y - (cy - r)) <= reach || Sq(x - (cx + r)) + Sq(y - (cy - r)) <= reach
            || Sq(x - (cx - r)) + Sq(y - (cy + r)) <= reach || Sq(x - (cx + r)) + Sq(y - (cy + r)) <= reach;
    }

    static float Sq(float v) => v * v;

    float Row => Height * 0.075f;

    void LayoutPanel()
    {
        float row = Row, pad = row * 0.25f, gap = pad * 0.6f;
        float w = MathF.Min(Height * 0.7f, Width - 2 * pad), h = row * 7 + pad * 2;
        if (float.IsNaN(_panelX))
        {
            // left of centre: clear of the shipped layouts' buttons (the right-hand cluster, the top row)
            _panelX = Width * 0.40f - w / 2;
            _panelY = (Height - h) / 2;
        }
        _panelX = Math.Clamp(_panelX, 0, MathF.Max(0, Width - w));
        _panelY = Math.Clamp(_panelY, 0, MathF.Max(0, Height - h));
        _panel.Set(_panelX, _panelY, _panelX + w, _panelY + h);
        float l = _panelX + pad, r = _panelX + w - pad, t = _panelY + pad;
        RectF RowRect(int n) => new(l, t + n * row, r, t + (n + 1) * row - gap);
        _rects.Clear();
        RectF header = RowRect(0);
        if (_allowSwitch)
        {
            float mid = l + (r - l) * 0.4f, half = (r - mid) / 2;
            _rects[Part.Header] = new RectF(l, header.Top, mid - gap, header.Bottom);
            _rects[Part.MatchTab] = new RectF(mid, header.Top, mid + half - gap / 2, header.Bottom);
            _rects[Part.CampaignTab] = new RectF(mid + half + gap / 2, header.Top, r, header.Bottom);
        }
        else
        {
            _rects[Part.Header] = header;
        }
        _nameRow = RowRect(1);
        _rects[Part.Size] = RowRect(2);
        _rects[Part.Opacity] = RowRect(3);
        RectF buttons = RowRect(4);
        _rects[Part.Hide] = new RectF(l, buttons.Top, (l + r) / 2 - gap / 2, buttons.Bottom);
        _rects[Part.Reset] = new RectF((l + r) / 2 + gap / 2, buttons.Top, r, buttons.Bottom);
        _rects[Part.All] = RowRect(5);
        RectF bottom = RowRect(6);
        float third = (r - l + gap) / 3;
        _rects[Part.ResetAll] = new RectF(l, bottom.Top, l + third - gap, bottom.Bottom);
        _rects[Part.Cancel] = new RectF(l + third, bottom.Top, l + 2 * third - gap, bottom.Bottom);
        _rects[Part.Save] = new RectF(l + 2 * third, bottom.Top, r, bottom.Bottom);
    }

    Part PartAt(float x, float y)
    {
        foreach ((Part part, RectF rect) in _rects)
        {
            if (rect.Contains(x, y)) return part;
        }
        return Part.None;
    }

    bool Enabled(Part part) => part is not (Part.Size or Part.Opacity or Part.Hide or Part.Reset) || _selected != -1;

    bool PanelShown => _drag is not (Drag.Button or Drag.Handle);

    // a slider's track: right of its label
    (float x0, float x1) Track(RectF r)
    {
        float knob = r.Height() * 0.28f;
        return (r.Left + r.Width() * 0.5f + knob, r.Right - knob);
    }

    void SetSlider(float x)
    {
        (float x0, float x1) = Track(_rects[_part]);
        float t = Math.Clamp((x - x0) / MathF.Max(x1 - x0, 1), 0, 1);
        switch (_part)
        {
        case Part.Size:
            SetScale(_selected, LayoutEdit.MinScale + t * (LayoutEdit.MaxScale - LayoutEdit.MinScale));
            break;
        case Part.Opacity:
            SetOpacity(_selected, 0.1f + t * 0.9f);
            break;
        case Part.All:
            _all = Snap20(0.1f + t * 0.9f);
            _dirty = true;
            break;
        }
    }

    void Act(Part part)
    {
        bool cancel = part == Part.Cancel, resetAll = part == Part.ResetAll;
        if (!cancel) _confirmCancel = false;
        if (!resetAll) _confirmResetAll = false;
        switch (part)
        {
        case Part.MatchTab:
        case Part.CampaignTab:
            bool campaign = part == Part.CampaignTab;
            if (campaign != _campaign)
            {
                _campaign = campaign;
                _spots = MatchTouchOverlay.Spots(campaign);
                _selected = -1;
            }
            break;
        case Part.Hide:
            if (_selected != -1)
            {
                LayoutEdit e = Edit(_selected);
                e.Hidden = !e.Hidden;
                Prune(_selected);
            }
            break;
        case Part.Reset:
            if (_selected != -1 && Edits.Remove(_spots[_selected].Label)) _dirty = true;
            break;
        case Part.ResetAll:
            if (!_confirmResetAll)
            {
                _confirmResetAll = true;
                break;
            }
            _confirmResetAll = false;
            Edits.Clear();
            _all = 1;
            _selected = -1;
            _dirty = true;
            break;
        case Part.Cancel:
            if (_dirty && !_confirmCancel)
            {
                _confirmCancel = true;
                break;
            }
            Close(false);
            break;
        case Part.Save:
            _saveFailed = !Save();
            if (!_saveFailed) Close(true);
            break;
        }
    }

    void Close(bool saved)
    {
        if (_closedOnce) return;
        _closedOnce = true;
        _closed(saved);
    }

    bool Save()
    {
        try
        {
            // the file as it is now (everything else in it stays), with these two settings replaced
            RecompSettings settings = RecompSettings.Load(_settingsPath);
            settings.TouchLayouts ??= new();
            for (int i = 0; i < 2; i++)
            {
                string key = MatchTouchOverlay.LayoutKey(i == 1);
                var keep = new Dictionary<string, LayoutEdit>();
                foreach ((string label, LayoutEdit edit) in _layouts[i])
                {
                    if (!edit.IsDefault) keep[label] = edit;
                }
                if (keep.Count == 0) settings.TouchLayouts.Remove(key);
                else settings.TouchLayouts[key] = keep;
            }
            settings.TouchOpacity = (int)MathF.Round(_all * 100);
            settings.Save(_settingsPath);
            Log.Info("MPHTouch", $"touch layout saved: match {_layouts[0].Count} edits, campaign {_layouts[1].Count} edits, " +
                $"all buttons {settings.TouchOpacity}%");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("MPHTouch", "touch layout save: " + ex);
            return false;
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return true;
        switch (e.ActionMasked)
        {
        case MotionEventActions.Down:
            Down(e.GetPointerId(0), e.GetX(0), e.GetY(0));
            break;
        case MotionEventActions.Move:
            if (_pointer != -1 && e.FindPointerIndex(_pointer) is int index and >= 0)
            {
                MoveTo(e.GetX(index), e.GetY(index));
            }
            break;
        case MotionEventActions.PointerUp:
        case MotionEventActions.Up:
            if (_pointer != -1 && e.GetPointerId(e.ActionIndex) == _pointer)
            {
                Up(e.GetX(e.ActionIndex), e.GetY(e.ActionIndex));
            }
            break;
        case MotionEventActions.Cancel:
            _drag = Drag.None;
            _pointer = -1;
            break;
        }
        Invalidate();
        return true; // nothing reaches the game or the menu underneath
    }

    void Down(int pointer, float x, float y)
    {
        _pointer = pointer;
        _downX = x;
        _downY = y;
        _part = Part.None;
        _drag = Drag.None;
        LayoutPanel();
        if (_panel.Contains(x, y))
        {
            _part = PartAt(x, y);
            if (_part == Part.Header)
            {
                _drag = Drag.Panel;
                _grabX = x - _panelX;
                _grabY = y - _panelY;
            }
            else if ((_part is Part.Size or Part.Opacity or Part.All) && Enabled(_part))
            {
                _drag = Drag.Slider;
                _confirmCancel = _confirmResetAll = false;
                SetSlider(x);
            }
            else if (_part != Part.None && Enabled(_part))
            {
                _drag = Drag.Tap; // acted on when let go over it
            }
            return;
        }
        _confirmCancel = _confirmResetAll = false;
        if (HandleAt(x, y))
        {
            (float cx, float cy, _) = PlaceOf(_selected);
            _drag = Drag.Handle;
            _startDist = MathF.Max(MathF.Sqrt(Sq(x - cx) + Sq(y - cy)), 1);
            _startScale = EditOf(_selected)?.Scale ?? 1;
            return;
        }
        int hit = ButtonAt(x, y);
        _selected = hit;
        if (hit != -1)
        {
            (float cx, float cy, _) = PlaceOf(hit);
            _drag = Drag.Pending;
            _grabX = x - cx;
            _grabY = y - cy;
        }
    }

    void MoveTo(float x, float y)
    {
        switch (_drag)
        {
        case Drag.Pending:
            if (Sq(x - _downX) + Sq(y - _downY) < Sq(8 * _dp)) break;
            _drag = Drag.Button;
            goto case Drag.Button;
        case Drag.Button:
            SetCentre(_selected, x - _grabX, y - _grabY, snap: false);
            break;
        case Drag.Handle:
            (float cx, float cy, _) = PlaceOf(_selected);
            SetScale(_selected, _startScale * MathF.Sqrt(Sq(x - cx) + Sq(y - cy)) / _startDist);
            break;
        case Drag.Panel:
            _panelX = x - _grabX;
            _panelY = y - _grabY;
            break;
        case Drag.Slider:
            SetSlider(x);
            break;
        }
    }

    void Up(float x, float y)
    {
        if (_drag == Drag.Button)
        {
            SetCentre(_selected, x - _grabX, y - _grabY, snap: true);
        }
        else if (_drag == Drag.Tap)
        {
            LayoutPanel();
            if (PartAt(x, y) == _part) Act(_part);
        }
        _drag = Drag.None;
        _pointer = -1;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width, h = Height;
        // the dim: lighter over the paused game, so the arena's still there to judge against
        canvas.DrawColor(Color.Argb(_allowSwitch ? 190 : 102, 0, 0, 0));
        _line.StrokeWidth = 1 * _dp;
        for (int i = 1; i < 12; i++)
        {
            _line.Color = Color.Argb(i == 6 ? 45 : 22, 255, 255, 255);
            canvas.DrawLine(0, h * i / 12, w, h * i / 12, _line);
        }
        for (float x = w / 2, step = h / 12; x < w; x += step)
        {
            _line.Color = Color.Argb(x == w / 2 ? 45 : 22, 255, 255, 255);
            canvas.DrawLine(x, 0, x, h, _line);
            if (x > w / 2) canvas.DrawLine(w - x, 0, w - x, h, _line);
        }
        for (int i = 0; i < _spots.Length; i++)
        {
            DrawButton(canvas, i, captionAbove: SharesPlace(i));
        }
        if (_selected != -1)
        {
            (float cx, float cy, float r) = PlaceOf(_selected);
            _ring.Color = Accent;
            _ring.StrokeWidth = 3 * _dp;
            canvas.DrawCircle(cx, cy, r + 3 * _dp, _ring);
            _fill.Color = Accent;
            float s = 6 * _dp;
            foreach ((float hx, float hy) in new[] { (cx - r, cy - r), (cx + r, cy - r), (cx - r, cy + r), (cx + r, cy + r) })
            {
                canvas.DrawRect(hx - s, hy - s, hx + s, hy + s, _fill);
            }
        }
        if (PanelShown)
        {
            DrawPanel(canvas);
        }
    }

    // drawn where an earlier button already is (the match's LASER and SPIDER share a place): its caption goes above
    bool SharesPlace(int i)
    {
        (float cx, float cy, _) = PlaceOf(i);
        for (int j = 0; j < i; j++)
        {
            (float jx, float jy, _) = PlaceOf(j);
            if (Sq(jx - cx) + Sq(jy - cy) < 4) return true;
        }
        return false;
    }

    void DrawButton(Canvas canvas, int i, bool captionAbove)
    {
        MatchTouchOverlay.Spot s = _spots[i];
        LayoutEdit? e = EditOf(i);
        (float cx, float cy, float r) = PlaceOf(i);
        if (e is { Hidden: true })
        {
            _ghost.Color = Color.Argb(150, 170, 170, 170);
            canvas.DrawCircle(cx, cy, r, _ghost);
            _text.Color = Color.Argb(150, 170, 170, 170);
        }
        else
        {
            // as the overlay draws it (MatchTouchOverlay.OnDraw), the ring kept findable when faded right down
            float alpha = MatchTouchOverlay.Alpha(e, _all);
            _fill.Color = Color.Argb((int)(55 * alpha), 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _fill);
            _ring.StrokeWidth = 4;
            _ring.Color = Color.Argb((int)MathF.Max(150 * alpha, 60), 255, 255, 255);
            canvas.DrawCircle(cx, cy, r, _ring);
            _text.Color = Color.Argb((int)MathF.Max(255 * alpha, 90), 255, 255, 255);
        }
        _text.TextSize = Height * 0.03f * (e == null ? 1 : LayoutEdit.ClampScale(e.Scale));
        float tw = _text.MeasureText(s.Label);
        if (tw > r * 1.7f) _text.TextSize = _text.TextSize * r * 1.7f / tw;
        canvas.DrawText(s.Label, cx, cy + _text.TextSize / 3, _text);
        if (s.When != null)
        {
            _text.TextSize = Height * 0.022f;
            _text.Color = Color.Argb(190, 255, 208, 64);
            canvas.DrawText(s.When, cx, captionAbove ? cy - r - _text.TextSize * 0.5f : cy + r + _text.TextSize * 1.2f, _text);
        }
    }

    void DrawPanel(Canvas canvas)
    {
        LayoutPanel();
        float corner = 8 * _dp, text = Row * 0.34f;
        _fill.Color = Color.Argb(235, 18, 22, 30);
        canvas.DrawRoundRect(_panel, corner, corner, _fill);
        _ring.StrokeWidth = 1.5f * _dp;
        _ring.Color = Color.Argb(120, 255, 255, 255);
        canvas.DrawRoundRect(_panel, corner, corner, _ring);

        // the title bar (drag it to move the panel), with a grip
        RectF header = _rects[Part.Header];
        _fill.Color = Color.Argb(40, 255, 255, 255);
        canvas.DrawRoundRect(header, corner / 2, corner / 2, _fill);
        _fill.Color = Color.Argb(150, 255, 255, 255);
        float gx = header.Left + 10 * _dp, gy = header.CenterY();
        for (int col = 0; col < 2; col++)
        {
            for (int row = -1; row <= 1; row++)
            {
                canvas.DrawCircle(gx + col * 5 * _dp, gy + row * 5 * _dp, 1.5f * _dp, _fill);
            }
        }
        string title = _allowSwitch ? "TOUCH LAYOUT" : _campaign ? "TOUCH LAYOUT: CAMPAIGN" : "TOUCH LAYOUT: MATCH";
        DrawLeft(canvas, title, header.Left + 22 * _dp, header, text, Color.White, header.Right - 4 * _dp);
        if (_allowSwitch)
        {
            DrawBox(canvas, Part.MatchTab, "MATCH", text, lit: !_campaign);
            DrawBox(canvas, Part.CampaignTab, "CAMPAIGN", text, lit: _campaign);
        }

        LayoutEdit? edit = _selected == -1 ? null : EditOf(_selected);
        if (_selected == -1)
        {
            DrawLeft(canvas, "TAP A BUTTON TO PICK IT. DRAG A BUTTON TO MOVE IT.", _nameRow.Left, _nameRow, text * 0.85f,
                Color.Argb(170, 255, 255, 255), _nameRow.Right);
        }
        else
        {
            MatchTouchOverlay.Spot s = _spots[_selected];
            string name = s.Label + (edit is { Hidden: true } ? " (HIDDEN)" : "") + (s.When != null ? "  -  SHOWS WITH " + s.When : "");
            DrawLeft(canvas, name, _nameRow.Left, _nameRow, text, Accent, _nameRow.Right);
        }
        float scale = edit == null ? 1 : LayoutEdit.ClampScale(edit.Scale);
        float opacity = edit == null ? 1 : Math.Clamp(edit.Opacity, 0.1f, 1f);
        DrawSlider(canvas, Part.Size, $"SIZE {MathF.Round(scale * 100)}%",
            (scale - LayoutEdit.MinScale) / (LayoutEdit.MaxScale - LayoutEdit.MinScale), text);
        DrawSlider(canvas, Part.Opacity, $"OPACITY {MathF.Round(opacity * 100)}%", (opacity - 0.1f) / 0.9f, text);
        DrawBox(canvas, Part.Hide, edit is { Hidden: true } ? "SHOW" : "HIDE", text);
        DrawBox(canvas, Part.Reset, "RESET BUTTON", text);
        DrawSlider(canvas, Part.All, $"ALL BUTTONS {MathF.Round(_all * 100)}%", (_all - 0.1f) / 0.9f, text);
        DrawBox(canvas, Part.ResetAll, _confirmResetAll ? "RESET ALL?" : "RESET ALL", text);
        DrawBox(canvas, Part.Cancel, _confirmCancel ? "DISCARD?" : "CANCEL", text);
        DrawBox(canvas, Part.Save, _saveFailed ? "SAVE FAILED" : "SAVE", text, lit: true);
    }

    void DrawLeft(Canvas canvas, string s, float x, RectF row, float size, Color color, float right)
    {
        _left.TextSize = size;
        float tw = _left.MeasureText(s);
        if (tw > right - x && tw > 0) _left.TextSize = size * (right - x) / tw;
        _left.Color = color;
        canvas.DrawText(s, x, row.CenterY() + _left.TextSize / 3, _left);
    }

    void DrawBox(Canvas canvas, Part part, string label, float size, bool lit = false)
    {
        RectF r = _rects[part];
        bool enabled = Enabled(part);
        bool held = _drag == Drag.Tap && _part == part;
        _fill.Color = lit ? Color.Argb(held ? 255 : 210, 255, 208, 64) : Color.Argb(!enabled ? 20 : held ? 110 : 55, 255, 255, 255);
        canvas.DrawRoundRect(r, 6 * _dp, 6 * _dp, _fill);
        _text.TextSize = size;
        float tw = _text.MeasureText(label);
        if (tw > r.Width() * 0.9f) _text.TextSize = size * r.Width() * 0.9f / tw;
        _text.Color = lit ? Color.Argb(255, 20, 20, 20) : enabled ? Color.White : Color.Argb(90, 255, 255, 255);
        canvas.DrawText(label, r.CenterX(), r.CenterY() + _text.TextSize / 3, _text);
    }

    void DrawSlider(Canvas canvas, Part part, string label, float t, float size)
    {
        RectF r = _rects[part];
        bool enabled = Enabled(part);
        Color color = enabled ? Color.White : Color.Argb(90, 255, 255, 255);
        DrawLeft(canvas, label, r.Left, r, size, color, r.Left + r.Width() * 0.5f - 4 * _dp);
        (float x0, float x1) = Track(r);
        float y = r.CenterY(), knob = x0 + Math.Clamp(t, 0, 1) * (x1 - x0);
        _line.StrokeWidth = 4 * _dp;
        _line.StrokeCap = Paint.Cap.Round;
        _line.Color = Color.Argb(enabled ? 70 : 30, 255, 255, 255);
        canvas.DrawLine(x0, y, x1, y, _line);
        if (enabled)
        {
            _line.Color = Accent;
            canvas.DrawLine(x0, y, knob, y, _line);
            _fill.Color = _drag == Drag.Slider && _part == part ? Color.White : Accent;
            canvas.DrawCircle(knob, y, r.Height() * 0.28f, _fill);
        }
        _line.StrokeCap = Paint.Cap.Butt;
    }
}
