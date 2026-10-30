using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;

// Typeable rule values (owner 2026-10-26: "can we have the values be typeable as well?", then "on pc you should just be
// able to click it like a text field and type freely, unlike our other typing areas. on android it should also act like
// a text field but bring up the numpad in a little pop up next to the rules with a check or x to accept or cancel").
//
// A click / tap on a numeric rule's number box on the rules page (or a digit key on a keyboard) turns the box into a text
// field: the value is selected, so the first key replaces it, then Backspace, digits (and "." for the time goal's tenths of a
// minute) edit it freely; Enter or a click elsewhere sets it (kept inside the rule's range), Esc cancels; it is drawn on
// the page's art layer item (AddArtLayer). On Android (NumpadPopup; owner: "i just want the num pad to be in one of those
// notice pop ups with the check and the x") a tap opens the game's own question box (MenuPopup, Body) holding the ENTER
// NEW CODE page's keypad (page 61's numkey / numback items, their digits, and our "." under the "2" as BindAddress adds
// it), with the rule's name, the field and the box's check (sets) and X (cancels) beside it.
namespace MphRecomp.Frontend
{
    public sealed partial class MultiplayerMenu
    {
        private sealed class ValueEdit
        {
            public Rule Rule;
            public int Page, Layer;
            public float RowY; // menu space (y up): the row's middle
            public string Text = "";
            public bool Fresh; // the shown value is selected: the next key replaces it
            public int Tick;
            public bool InPopup; // edited in the question box (NumpadPopup)
            public int Lit = -1, LitTicks; // the pad key just pressed (its lit look for a few frames)
        }

        private ValueEdit? _valEdit;

        // the numpad pop-up (a phone has no keyboard to type with); a PC edits the box in place
        public static bool NumpadPopup = OperatingSystem.IsAndroid();

        // the box (DS touch px, y down)
        private const float VeBoxX0 = 192.5f, VeBoxX1 = 227.5f, VeBoxHalf = 8.5f;

        // a key of page 61's keypad as the pop-up draws it: the ROM's key item (Dy: ours, the "." is the "2" key moved down
        // level with the "0") and its digit text item, or none (delete)
        private sealed class PadKey
        {
            public char Label; // '0'-'9', '.', or '<' (delete)
            public MenuItem Key = null!;
            public float Dy;
            public MenuItem? Text;
            public float TextDy;
            public (float X0, float Y0, float X1, float Y1) Rect; // menu space, y up, unshifted (PadLayout)
        }

        private readonly List<PadKey> _padKeys = new();
        private (float X0, float Y0, float X1, float Y1) _padBounds; // all of it, menu space; zero size until PadLayout

        // page 61 (the ROM's, before BindAddress copies it): the 10 digit keys, delete, and the "." made from the "2"
        private void BindValuePad(MenuFile file, MenuStrings strings)
        {
            _padKeys.Clear();
            _padBounds = default;
            if (file.Pages.Count <= AddressPage) return;
            MenuPage rom = file.Pages[AddressPage];
            string TextOf(MenuItem it) => strings[it.States[0].Text!.StringId];
            int[] keys = Enumerable.Range(0, rom.Items.Count)
                .Where(i => HasWidget(file, rom.Items[i], "numkey") && Calls(rom.Items[i]).Any() && !Calls(rom.Items[i]).Contains(DotCall)).ToArray();
            int[] digits = Enumerable.Range(0, rom.Items.Count)
                .Where(i => rom.Items[i].IsText && TextOf(rom.Items[i]) is { Length: 1 } s && s[0] is >= '0' and <= '9').ToArray();
            int delete = FindItem(rom, it => HasWidget(file, it, "numback") && Calls(it).Any());
            if (keys.Length != 10 || digits.Length != 10 || delete < 0) return;
            int[] keyRow = Ranks(keys.Select(i => rom.Items[i].Y), descending: true);
            int[] keyCol = Ranks(keys.Select(i => rom.Items[i].X), descending: false);
            int[] digitRow = Ranks(digits.Select(i => rom.Items[i].Y), descending: true);
            int[] digitCol = Ranks(digits.Select(i => rom.Items[i].X), descending: false);
            var keyOf = new Dictionary<char, PadKey>();
            for (int k = 0; k < keys.Length; k++)
            {
                int d = Enumerable.Range(0, digits.Length).FirstOrDefault(j => digitRow[j] == keyRow[k] && digitCol[j] == keyCol[k], -1);
                if (d < 0) return;
                MenuItem text = rom.Items[digits[d]];
                keyOf[TextOf(text)[0]] = new PadKey { Label = TextOf(text)[0], Key = rom.Items[keys[k]], Text = text };
            }
            if (keyOf.Count != 10) return;
            PadKey two = keyOf['2'], zero = keyOf['0'];
            _padKeys.AddRange(keyOf.Values);
            _padKeys.Add(new PadKey { Label = '.', Key = two.Key, Dy = zero.Key.Y - two.Key.Y, Text = two.Text, TextDy = zero.Text!.Y - two.Text!.Y });
            _padKeys.Add(new PadKey { Label = '<', Key = rom.Items[delete] });
        }

        // a key's look (lit: its Selected / Focused one) and its digit, unshifted, into `output`
        private static void PadKeyTris(MenuEngine menu, PadKey k, bool lit, float alpha, List<WidgetTri> output, bool text)
        {
            if (!text)
            {
                MenuItemState? look = (lit ? k.Key.GetState((int)MenuState.Selected) ?? k.Key.GetState((int)MenuState.Focused) : null)
                    ?? k.Key.GetState((int)MenuState.Idle);
                if (look == null || look.WidgetIndex < 0) return;
                var items = new List<WidgetTri>();
                menu.Widgets.Evaluate(look.WidgetIndex, 0, loop: true, alpha: alpha, items);
                if (k.Key.StretchX != 0 || k.Key.StretchDown != 0) MenuEngine.Stretch(items, 0, k.Key.StretchX, k.Key.StretchDown);
                Shift(items, k.Key.X, k.Key.Y + k.Dy);
                output.AddRange(items);
                return;
            }
            if (k.Text?.GetState((int)MenuState.Idle)?.Text is not MenuTextStyle style) return;
            (int r, int g, int b, int a) = MenuTextStyle.Channels(style.StartColor);
            int start = output.Count;
            menu.Font.Emit(k.Label.ToString(), k.Text.X, k.Text.Y + k.TextDy, style.Align, style.WrapWidth, style.Size == 0 ? 8 : style.Size,
                r / 31f, g / 31f, b / 31f, a / 31f * alpha, MenuEngine.TextDepthZ(k.Text.Depth), output);
            for (int i = start; i < output.Count; i++)
            {
                WidgetTri t = output[i];
                t.Item = -1;
                output[i] = t;
            }
        }

        private static void Shift(List<WidgetTri> tris, float dx, float dy, int from = 0)
        {
            for (int i = from; i < tris.Count; i++)
            {
                WidgetTri t = tris[i];
                t.A.X += dx; t.B.X += dx; t.C.X += dx;
                t.A.Y += dy; t.B.Y += dy; t.C.Y += dy;
                t.Item = -1;
                tris[i] = t;
            }
        }

        private static (float, float, float, float) Bounds(List<WidgetTri> tris, int from = 0)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = from; i < tris.Count; i++)
            {
                foreach (UiVertex v in new[] { tris[i].A, tris[i].B, tris[i].C })
                {
                    x0 = MathF.Min(x0, v.X); x1 = MathF.Max(x1, v.X);
                    y0 = MathF.Min(y0, v.Y); y1 = MathF.Max(y1, v.Y);
                }
            }
            return (x0, y0, x1, y1);
        }

        // each key's rectangle and the pad's (the widgets need the loaded menu, so on the first pop-up)
        private bool PadLayout(MenuEngine menu)
        {
            if (_padKeys.Count != 12) return false;
            if (_padBounds.X1 > _padBounds.X0) return true;
            var all = new List<WidgetTri>();
            foreach (PadKey k in _padKeys)
            {
                int start = all.Count;
                PadKeyTris(menu, k, lit: false, 1, all, text: false);
                if (all.Count == start) return false;
                k.Rect = Bounds(all, start);
            }
            foreach (PadKey k in _padKeys) PadKeyTris(menu, k, lit: false, 1, all, text: true);
            _padBounds = Bounds(all);
            return _padBounds.X1 > _padBounds.X0;
        }

        // the pad at the box's body (bx, by: DS touch px, y down): keys, then digits, then the field in the side strip
        private void DrawPad(MenuEngine menu, List<WidgetTri> output, int bx, int by, (int X0, int Y0, int X1, int Y1) strip)
        {
            ValueEdit? e = _valEdit;
            if (e == null) return;
            e.Tick++;
            if (e.LitTicks > 0 && --e.LitTicks == 0) e.Lit = -1;
            float dx = bx - _padBounds.X0, dy = -by - _padBounds.Y1;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 0; k < _padKeys.Count; k++)
                {
                    int start = output.Count;
                    PadKeyTris(menu, _padKeys[k], lit: k == e.Lit, PadKeyUsable(e, _padKeys[k]) ? 1 : 0.35f, output, text: pass == 1);
                    Shift(output, dx, dy, start);
                }
            }
            // the field: the typed value as in the PC's box, in the strip under the rule's name
            const float half = 7f, border = 0.7f;
            float cx = (strip.X0 + strip.X1) / 2f, my = -(strip.Y0 + half + 1);
            float fx0 = strip.X0 + 6, fx1 = strip.X1 - 6;
            Fill(output, -1, fx0, my + half, fx1, my - half, 255, 220, 90);
            Fill(output, -1, fx0 + border, my + half - border, fx1 - border, my - half + border, 6, 4, 2);
            DrawField(menu, output, -1, e, cx, my, half - 2.5f, 0.9f, fx1 - fx0 - 4);
        }

        private static bool PadKeyUsable(ValueEdit e, PadKey k) => k.Label != '.' || e.Rule == Rule.TimeGoal; // (only the time goal has tenths)

        // a tap on the pad at (x, y), DS px from its top left
        private void PadTouch(MenuEngine menu, float x, float y)
        {
            ValueEdit? e = _valEdit;
            if (e == null) return;
            float mx = x + _padBounds.X0, my = _padBounds.Y1 - y;            for (int k = 0; k < _padKeys.Count; k++)
            {
                PadKey key = _padKeys[k];
                if (mx < key.Rect.X0 || mx > key.Rect.X1 || my < key.Rect.Y0 || my > key.Rect.Y1 || !PadKeyUsable(e, key)) continue;
                if (key.Label == '<') EditBackspace();
                else EditType(key.Label);
                e.Lit = k;
                e.LitTicks = 6;
                int sound = menu.Sounds.Item(AddressPage, key.Key.Index).Sound;
                Sound(menu, sound != 0 ? sound : MenuSounds.Neutral, "rule value pad key");
                return;
            }
        }

        private static bool IsNumeric(Rule rule) => rule is Rule.PointGoal or Rule.NodesGoal or Rule.OctolithGoal or Rule.Lives
            or Rule.TimeLimit or Rule.TimeGoal;

        private static (int Min, int Max) ValueRange(Rule rule)
        {
            if (rule == Rule.TimeLimit) return (TimeLimitMinutes[0], TimeLimitMinutes[^1]);
            if (rule == Rule.TimeGoal) return (TimeGoalSeconds[0], TimeGoalSeconds[^1]);
            int[] table = GoalTable(rule);
            return (table[0], table[^1]);
        }

        private string ValueText(Rule rule, int v) => rule == Rule.TimeGoal ? Minutes(v) : v.ToString(CultureInfo.InvariantCulture);

        // the typed text as the rule's value, kept inside its range (a time goal: minutes -> tenths of a minute -> seconds)
        private static bool TryValue(Rule rule, string text, out int value)
        {
            value = 0;
            (int min, int max) = ValueRange(rule);
            if (rule == Rule.TimeGoal)
            {
                if (!Double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes)) return false;
                value = Math.Clamp((int)Math.Round(minutes * 10) * TimeGoalStep, min, max);
                return true;
            }
            if (!Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
            value = Math.Clamp(n, min, max);
            return true;
        }

        // ---- state ----

        private bool EditLive(MenuEngine menu)
        {
            if (_valEdit == null) return false;
            if (menu.Page?.Index != _valEdit.Page || menu.PendingPage != -1 || AdvancedOpen(menu))
            {
                if (_valEdit.InPopup && Popup?.IsOpen == true) Popup.Close();
                _valEdit = null;
                return false;
            }
            if (_valEdit.InPopup && Popup?.IsOpen != true)
            {
                _valEdit = null; // (the box went away without an answer)
                return false;
            }
            return true;
        }

        // true while a rule's box is being edited (the shells then send typed characters and Backspace / Enter here)
        public bool Editing(MenuEngine menu) => EditLive(menu);

        private void StartEdit(MenuEngine menu, Rule rule, float rowY, char first = '\0')
        {
            if (menu.Page == null || !_artLayers.TryGetValue(menu.Page.Index, out int layer)) return;
            _valEdit = new ValueEdit
            {
                Rule = rule, Page = menu.Page.Index, Layer = layer, RowY = rowY, Text = first == '\0' ? RuleValue(rule) : first.ToString(),
                Fresh = first == '\0',
            };
            if (NumpadPopup && Popup != null && PadLayout(menu))
            {
                ModeInfo? m = Modes.FirstOrDefault(k => k.Page == menu.Page.Index);
                string label = m == null ? "" : RuleLabels(m)[Array.IndexOf(m.Rules, rule)].Replace('\n', ' ');
                (int min, int max) = ValueRange(rule);
                ValueEdit e = _valEdit;
                e.InPopup = true;
                Popup.Ask($"{label}\n{ValueText(rule, min)} - {ValueText(rule, max)}", new MenuPopup.Body
                {
                    Width = (int)MathF.Ceiling(_padBounds.X1 - _padBounds.X0),
                    Height = (int)MathF.Ceiling(_padBounds.Y1 - _padBounds.Y0),
                    Draw = (tris, _, bx, by, strip) => DrawPad(menu, tris, bx, by, strip),
                    Touch = (x, y) => PadTouch(menu, x, y),
                }, yes =>
                {
                    if (_valEdit != e) return;
                    if (yes) CommitEdit(menu, sound: false); // (the box's check and X make their own sounds)
                    else _valEdit = null;
                });
            }
            Sound(menu, MenuSounds.Neutral, "rule value edit");
        }

        private void CommitEdit(MenuEngine menu, bool sound = true)
        {
            ValueEdit e = _valEdit!;
            _valEdit = null;
            if (TryValue(e.Rule, e.Text, out int v) && ValueText(e.Rule, v) != RuleValue(e.Rule))
            {
                if (e.Rule == Rule.TimeLimit) _setup.TimeLimit = v;
                else if (e.Rule == Rule.TimeGoal) _setup.TimeGoal = v;
                else _setup.Goal = v;
                Log?.Invoke($"multiplayer: typed {e.Rule} = {v}");
                FillStrings();
            }
            if (sound) Sound(menu, MenuSounds.Neutral, "rule value set");
        }

        private void CancelEdit(MenuEngine menu)
        {
            _valEdit = null;
            Sound(menu, MenuSounds.Back, "rule value cancelled");
        }

        private void EditType(char c)
        {
            ValueEdit e = _valEdit!;
            int maxLen = e.Rule == Rule.TimeGoal ? 4 : ValueRange(e.Rule).Max.ToString(CultureInfo.InvariantCulture).Length;
            if (c is < '0' or > '9' && c != '.') return;
            if (e.Fresh)
            {
                e.Text = "";
                e.Fresh = false;
            }
            if (c == '.')
            {
                if (e.Rule == Rule.TimeGoal && e.Text.Length > 0 && !e.Text.Contains('.') && e.Text.Length < maxLen) e.Text += ".";
                return;
            }
            if (e.Text == "0" && e.Rule != Rule.TimeGoal) e.Text = "";
            if (e.Text.Length < maxLen) e.Text += c;
        }

        private void EditBackspace()
        {
            ValueEdit e = _valEdit!;
            e.Text = e.Fresh || e.Text.Length == 0 ? "" : e.Text[..^1];
            e.Fresh = false;
        }

        // ---- input (FrontendSession: TypeChar / TypeKey, CapturePress / CaptureNavigate / CaptureTouch) ----

        // a typed character: into the box being edited, else a digit on a rules page starts editing the focused numeric row's
        // (or the first one's) box with it typed
        public bool TypeEditChar(MenuEngine menu, char c)
        {
            if (EditLive(menu))
            {
                if (c is >= '0' and <= '9' or '.') EditType(c);
                return true;
            }
            if (c is < '0' or > '9' || !_setup.RulesSeen || menu.Page == null || menu.PendingPage != -1) return false;
            ModeInfo? m = Modes.FirstOrDefault(k => k.Page == menu.Page.Index);
            if (m == null || AdvancedOpen(menu)) return false;
            int[] rows = ValueRows(menu.Page);
            Rule? rule = null;
            int f = menu.FocusedItem();
            if (f >= 0 && f < menu.Page.Items.Count)
            {
                foreach (int call in Calls(menu.Page.Items[f]))
                {
                    if (RuleCalls.TryGetValue(call, out var rc) && m.Rules.Contains(rc.Rule)) rule = rc.Rule;
                }
            }
            if (rule == null || !IsNumeric(rule.Value)) rule = m.Rules.Where(IsNumeric).Select(r => (Rule?)r).FirstOrDefault();
            int row = rule == null ? -1 : Array.IndexOf(m.Rules, rule.Value);
            if (row < 0 || row >= rows.Length) return false;
            StartEdit(menu, rule!.Value, RowMenuY(menu.Page, rows[row]), c);
            return true;
        }

        public bool EditKey(MenuEngine menu, bool backspace)
        {
            if (!EditLive(menu)) return false;
            if (backspace) EditBackspace();
            else CommitEdit(menu);
            return true;
        }

        private bool EditPress(MenuEngine menu, MenuKeys keys)
        {
            if (!EditLive(menu)) return false;
            if ((keys & MenuKeys.A) != 0) CommitEdit(menu);
            else if ((keys & MenuKeys.B) != 0) CancelEdit(menu);
            return true;
        }

        // the D-pad does nothing while a box is being typed in (the arrows would step the row under the edit)
        private bool EditNavigate(MenuEngine menu) => EditLive(menu);

        // the text items of the rules page's value column, top to bottom
        private static int[] ValueRows(MenuPage p) => Enumerable.Range(0, p.Items.Count)
            .Where(i => p.Items[i].IsText && MathF.Abs(p.Items[i].X - 222.4f) < 3 && p.Items[i].Y > 0 && p.Items[i].Y < 192)
            .OrderByDescending(i => p.Items[i].Y).ToArray();

        private static float RowMenuY(MenuPage p, int item) => p.Items[item].Y - 188;

        // a tap at (x, my) (menu space): on a numeric row's number box opens it; while editing, the field / pop-up take it
        private bool TryValueTap(MenuEngine menu, float x, float my)
        {
            if (EditLive(menu)) return EditTouch(menu, x, my);
            if (!_setup.RulesSeen || menu.Page == null || menu.PendingPage != -1 || x < VeBoxX0 || x > VeBoxX1) return false;
            ModeInfo? m = Modes.FirstOrDefault(k => k.Page == menu.Page.Index);
            if (m == null || AdvancedOpen(menu)) return false;
            int[] rows = ValueRows(menu.Page);
            for (int r = 0; r < rows.Length && r < m.Rules.Length; r++)
            {
                float y = RowMenuY(menu.Page, rows[r]);
                if (MathF.Abs(my - y) > VeBoxHalf + 1 || !IsNumeric(m.Rules[r])) continue;
                StartEdit(menu, m.Rules[r], y);
                return _valEdit != null;
            }
            return false;
        }

        private bool EditTouch(MenuEngine menu, float x, float my)
        {
            ValueEdit e = _valEdit!;
            if (e.InPopup) return true; // (the box takes its own taps)
            if (MathF.Abs(my - e.RowY) <= VeBoxHalf + 1 && x >= VeBoxX0 && x <= VeBoxX1) return true; // (a click in the field)
            CommitEdit(menu); // a click elsewhere sets it, and does what it was for
            return false;
        }

        // ---- drawing (FrontendSession's draw pass, over the page's own) ----

        public void DrawValueEdit(MenuEngine menu, List<WidgetTri> tris)
        {
            if (!EditLive(menu) || _valEdit!.InPopup) return;
            ValueEdit e = _valEdit;
            e.Tick++;
            int layer = e.Layer;
            const float border = 0.7f;
            // the box as a text field: gold border, dark fill; the selected text lit gold behind its dark letters
            float by = e.RowY;
            Fill(tris, layer, VeBoxX0, by + VeBoxHalf, VeBoxX1, by - VeBoxHalf, 255, 220, 90);
            Fill(tris, layer, VeBoxX0 + border, by + VeBoxHalf - border, VeBoxX1 - border, by - VeBoxHalf + border, 6, 4, 2);
            DrawField(menu, tris, layer, e, (VeBoxX0 + VeBoxX1) / 2, by, VeBoxHalf - 2.5f, 0.84f, VeBoxX1 - VeBoxX0 - 4);
        }

        // the typed text centred on (cx, cy), selected (lit behind) while Fresh, else with a blinking caret after it
        private static void DrawField(MenuEngine menu, List<WidgetTri> tris, int layer, ValueEdit e, float cx, float cy, float half, float scale, float room)
        {
            string text = e.Text;
            float width = text.Length == 0 ? 0 : MathF.Min(room, menu.Font.Measure(text) * scale);
            if (e.Fresh)
            {
                Fill(tris, layer, cx - width / 2 - 1, cy + half, cx + width / 2 + 1, cy - half, 255, 220, 90);
                Text(menu, tris, layer, text, cx, cy, scale, 16, 8, 0);
                return;
            }
            if (text.Length > 0) Text(menu, tris, layer, text, cx, cy, scale, 255, 255, 255);
            if (e.Tick / 18 % 2 == 0)
            {
                float cxCaret = cx + width / 2 + 1;
                Fill(tris, layer, cxCaret, cy + half, cxCaret + 0.9f, cy - half, 255, 255, 255);
            }
        }
    }
}
