using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;

// TRY CONTROLS' pop-up (owner 2026-10-17: "instead of the Hunter select menu from the match where you
// can tap mode and arena just have a little pop up with the hunter icons", "in the same pop up under a divider there
// should be the bots idle/active switch"): over CONTROLS, the skin chooser's look (a dark box, the ROM's gold border and
// title bar) with SELECT HUNTER's characters as round icons -- the seven hunters in the ROM's own icons, then TROOPER and
// DARK SAMUS when this device has a skin for them, and ZERO SUIT SAMUS when imported (owner 10-17: "make sure that all
// extra hunters are available if imported"; not in public builds: she's out of this release) -- whatever the EXTRA
// CHARACTERS rule says, the focused one's name, a divider, PRACTICE BOTS IDLE / ACTIVE with
// the selected option's hint, and "tap outside to close". A character with more than one skin here opens the skin chooser
// over it (its pick, or B back to the icons). A pick starts the run (RecompMenus.ZoomAim.cs StartTryControls).
// Keys: the D-pad moves over the icons and down to the switch (Left / Right flip it), A picks or flips, B closes.
// Drawn after CONTROLS' own triangles (FrontendSession: DrawTryPicker), on the page's art layer item (AddArtLayer).
namespace MphRecomp.Frontend
{
    public sealed partial class MultiplayerMenu
    {
        private sealed class TryPicker
        {
            public int Page, Layer, Focus, LastIcon, Tick;
            public List<int> Slots = new(); // SELECT HUNTER's slots shown, in order
            public (float X, float Y)[] Centres = Array.Empty<(float, float)>(); // menu space
            public Func<bool> Active = () => false;
            public Action<bool> SetActive = _ => { };
            public Action<(Hunter Hunter, string? Skin, string? Character)> Pick = _ => { };
            public int Rows;
        }

        // the box (DS touch px, y down) and its pieces
        private const float TpX0 = 40, TpX1 = 216, TpY0 = 14, TpTitle = 10, TpIconTop = 29, TpGap = 30, TpDiameter = 24;
        private const int TpPerRow = 5;
        private const float TpValueX0 = 136, TpValueX1 = 206, TpRowH = 12, TpHintWidth = 164;

        private TryPicker? _try;

        // the pop-up's slots: SELECT HUNTER's nine, then ZERO SUIT SAMUS (not on SELECT HUNTER)
        private const int TryZeroSlot = PickerSlots;
        private static Character? TryCharacter(int slot) => slot == TryZeroSlot ? Characters.ZeroSuit : SlotCharacter(slot);
        private static Hunter TryHunter(int slot) => slot == TryZeroSlot ? Characters.ZeroSuit.Base : SlotHunter(slot);
        private string? TrySkin(int slot) => slot == TryZeroSlot ? SkinsOf(Characters.ZeroSuit).FirstOrDefault() : DisplaySkin(slot);

        private int BotsRow => _try!.Slots.Count; // the focus index of the bots' switch
        private float TpNameY => TpIconTop + _try!.Rows * TpGap + 6;
        private float TpDividerY => TpNameY + 8;
        private float TpBotsY => TpDividerY + 10; // the switch row's middle
        private float TpY1 => TpBotsY + TpRowH / 2 + 31;

        // RecompMenus' TRY CONTROLS press: the pop-up over the page (needs the page's art layer)
        public void OpenTryPicker(MenuEngine menu, Func<bool> active, Action<bool> setActive,
            Action<(Hunter Hunter, string? Skin, string? Character)> pick)
        {
            if (menu.Page == null || !_artLayers.TryGetValue(menu.Page.Index, out int layer)) return;
            RefreshSkins(); // an import may have ended since
            var t = new TryPicker { Page = menu.Page.Index, Layer = layer, Active = active, SetActive = setActive, Pick = pick };
            for (int slot = 0; slot <= TryZeroSlot; slot++)
            {
                bool open = slot < HunterOrder.Length ? HunterOpen(HunterOrder[slot])
                    : (slot != TryZeroSlot || !MphRecomp.Config.RecompSettings.PublicBuild) && SkinsOf(TryCharacter(slot)!).Count > 0;
                if (open) t.Slots.Add(slot);
            }
            if (t.Slots.Count == 0) return;
            t.Rows = (t.Slots.Count + TpPerRow - 1) / TpPerRow;
            t.Centres = new (float, float)[t.Slots.Count];
            for (int i = 0; i < t.Slots.Count; i++)
            {
                int row = i / TpPerRow, inRow = Math.Min(TpPerRow, t.Slots.Count - row * TpPerRow), col = i % TpPerRow;
                t.Centres[i] = (128 + (col - (inRow - 1) / 2f) * TpGap, -(TpIconTop + (row + 0.5f) * TpGap));
            }
            // on this player's last hunter (SELECT HUNTER's pick)
            t.Focus = Math.Max(0, t.Slots.IndexOf(SlotOf(_setup.MyHunter, _setup.MySkin)));
            t.LastIcon = t.Focus;
            _try = t;
            Sound(menu, MenuSounds.Neutral, "try controls pop-up");
            Log?.Invoke($"try controls pop-up: {t.Slots.Count} hunters, on slot {t.Slots[t.Focus]}");
        }

        private bool TryLive(MenuEngine menu)
        {
            if (_try == null) return false;
            if (menu.Page?.Index != _try.Page || menu.PendingPage != -1)
            {
                _try = null;
                return false;
            }
            return true;
        }

        private void CloseTry(MenuEngine menu)
        {
            _try = null;
            Sound(menu, MenuSounds.Back, "try controls pop-up closed");
        }

        private void FlipTryBots(MenuEngine menu)
        {
            _try!.SetActive(!_try.Active());
            Sound(menu, MenuSounds.Neutral, "try controls bots " + (_try.Active() ? "active" : "idle"));
        }

        // an icon's press: its character's skin chooser first when it has more than one here, else the run
        private void PickTry(MenuEngine menu, int index)
        {
            TryPicker t = _try!;
            int slot = t.Slots[index];
            Character? c = TryCharacter(slot);
            List<string> skins = c == null ? new List<string>() : SkinsOf(c);
            if (c != null && skins.Count > 1)
            {
                OpenTrySkins(menu, c, slot);
                return;
            }
            FinishTry(menu, slot, skins.Count == 1 ? skins[0] : null);
        }

        private void FinishTry(MenuEngine menu, int slot, string? skin, bool sound = true)
        {
            TryPicker t = _try!;
            Hunter hunter = TryHunter(slot);
            string? s = Characters.IsOriginal(skin) ? null : skin;
            _try = null;
            if (sound) Sound(menu, slot < PickerSlots ? SelectSound(menu, slot) : MenuSounds.Neutral, "try controls hunter");
            Log?.Invoke($"try controls: {hunter}{(s != null ? " in " + s : "")}");
            t.Pick((hunter, s, Characters.IdFor(hunter, s)));
        }

        // the skin chooser over the pop-up (RECOMP SETTINGS' menu art chooser's way: its pick comes back here)
        private void OpenTrySkins(MenuEngine menu, Character c, int slot)
        {
            TryPicker t = _try!;
            List<string> skins = SkinsOf(c);
            string? current = TrySkin(slot);
            var ch = new Chooser
            {
                Page = t.Page, Slot = slot, Character = c, Skins = skins, Title = c.Name.ToLowerInvariant() + "  skin", Layer = t.Layer,
                Focus = Math.Max(0, skins.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase))),
                X0 = 48, X1 = 208, Y0 = 34,
                OnPick = skin => { if (_try != null) FinishTry(menu, slot, skin, sound: false); }, // (the chooser's own sound)
            };
            (ch.Diameter, ch.Gap) = skins.Count <= 4 ? (21f, 24.8f) : (14.6f, 17.3f);
            const float title = 10, top = 4, bottom = 22;
            ch.PerRow = Math.Max(1, (int)((ch.X1 - ch.X0 - 8) / ch.Gap));
            int rows = (skins.Count + ch.PerRow - 1) / ch.PerRow;
            ch.Y1 = ch.Y0 + title + top + rows * ch.Gap + bottom;
            ch.Centres = new (float, float)[skins.Count];
            for (int i = 0; i < skins.Count; i++)
            {
                int row = i / ch.PerRow, inRow = Math.Min(ch.PerRow, skins.Count - row * ch.PerRow), col = i % ch.PerRow;
                ch.Centres[i] = (128 + (col - (inRow - 1) / 2f) * ch.Gap, -(ch.Y0 + title + top + (row + 0.5f) * ch.Gap));
            }
            _chooser = ch;
            Sound(menu, MenuSounds.Neutral, "try controls skins");
        }

        // ---- input (CapturePress / CaptureNavigate / CaptureTouch, after the skin chooser's) ----

        private bool TryPress(MenuEngine menu, MenuKeys keys)
        {
            if (!TryLive(menu)) return false;
            if ((keys & MenuKeys.A) != 0)
            {
                if (_try!.Focus == BotsRow) FlipTryBots(menu);
                else PickTry(menu, _try.Focus);
            }
            else if ((keys & MenuKeys.B) != 0) CloseTry(menu);
            return true;
        }

        private bool TryNavigate(MenuEngine menu, int dx, int dy)
        {
            if (!TryLive(menu)) return false;
            TryPicker t = _try!;
            int n = t.Slots.Count, f = t.Focus;
            if (f == BotsRow)
            {
                if (dx != 0)
                {
                    FlipTryBots(menu);
                    return true;
                }
                if (dy > 0) f = t.LastIcon; // up: back to the icons
            }
            else if (dx != 0) f = Wrap(f, Math.Sign(dx), n);
            else if (dy > 0) f = f >= TpPerRow ? f - TpPerRow : f; // up = the row above
            else if (dy < 0) f = f / TpPerRow + 1 < t.Rows ? Math.Min(f + TpPerRow, n - 1) : BotsRow;
            if (f != t.Focus)
            {
                if (t.Focus != BotsRow) t.LastIcon = t.Focus;
                t.Focus = f;
                Sound(menu, MenuSounds.Neutral, "try controls focus");
            }
            return true;
        }

        // (x, my): menu space
        private bool TryTouch(MenuEngine menu, float x, float my)
        {
            if (!TryLive(menu)) return false;
            TryPicker t = _try!;
            for (int i = 0; i < t.Centres.Length; i++)
            {
                float r = TpDiameter / 2 + 2;
                if (MathF.Abs(x - t.Centres[i].X) <= r && MathF.Abs(my - t.Centres[i].Y) <= r)
                {
                    t.Focus = t.LastIcon = i;
                    PickTry(menu, i);
                    return true;
                }
            }
            float y = -my;
            if (y >= TpBotsY - TpRowH / 2 - 2 && y <= TpBotsY + TpRowH / 2 + 2 && x >= TpX0 && x <= TpX1)
            {
                t.Focus = BotsRow;
                FlipTryBots(menu);
                return true;
            }
            if (x < TpX0 || x > TpX1 || y < TpY0 || y > TpY1) CloseTry(menu);
            return true;
        }

        // ---- drawing (FrontendSession's draw pass, after CONTROLS') ----

        public void DrawTryPicker(MenuEngine menu, List<WidgetTri> tris)
        {
            if (!TryLive(menu))
            {
                // the menu art's chooser over SECOND HUNT SETTINGS' rows (drawn after them, as this pop-up is)
                if (menu.Page != null && RecompMenus.IsSectionMenuPage(menu.Page) && _chooser is { OnPick: not null, Layer: >= 0 } && ChooserLive(menu))
                {
                    EnsureRims(menu);
                    DrawChooser(menu, tris, _chooser.Layer, _chooser);
                }
                return;
            }
            TryPicker t = _try!;
            t.Tick++;
            EnsureRims(menu);
            int layer = t.Layer;
            float x0 = TpX0, x1 = TpX1, y0 = -TpY0, y1 = -TpY1; // menu space
            const float border = 0.7f;
            Fill(tris, layer, x0, y0, x1, y1, 160, 110, 30);
            Fill(tris, layer, x0 + border, y0 - border, x1 - border, y1 + border, 6, 4, 2);
            Fill(tris, layer, x0 + border, y0 - border, x1 - border, y0 - TpTitle, 125, 98, 40);
            Text(menu, tris, layer, "try controls", (x0 + x1) / 2, y0 - TpTitle / 2, 0.84f, 16, 8, 0);
            int halo = HighlightWidget(menu);
            for (int i = 0; i < t.Slots.Count; i++)
            {
                (float cx, float cy) = t.Centres[i];
                float r = TpDiameter / 2;
                if (i == t.Focus && halo >= 0) Halo(menu, tris, layer, halo, cx, cy, TpDiameter * 1.55f, t.Tick);
                DrawTryIcon(menu, tris, layer, t.Slots[i], cx, cy, r);
            }
            int named = t.Focus == BotsRow ? t.LastIcon : t.Focus;
            Text(menu, tris, layer, SlotName(t.Slots[named]), 128, -TpNameY, 0.84f, 255, 247, 156);
            // the divider, then PRACTICE BOTS and its value box (arrows inside its ends), lit while focused
            Fill(tris, layer, x0 + 8, -TpDividerY + 0.35f, x1 - 8, -TpDividerY - 0.35f, 125, 98, 40);
            float by = -TpBotsY;
            bool focused = t.Focus == BotsRow;
            Text(menu, tris, layer, "practice bots", (x0 + 8 + TpValueX0 - 4) / 2, by, 0.84f, 230, 99, 8);
            float pulse = focused ? 0.65f + 0.35f * MathF.Sin(t.Tick * 0.2f) : 0;
            (int br, int bg, int bb) = focused ? ((int)(160 + 95 * pulse), (int)(110 + 110 * pulse), (int)(30 + 60 * pulse)) : (160, 110, 30);
            Fill(tris, layer, TpValueX0, by + TpRowH / 2, TpValueX1, by - TpRowH / 2, br, bg, bb);
            Fill(tris, layer, TpValueX0 + border, by + TpRowH / 2 - border, TpValueX1 - border, by - TpRowH / 2 + border, 6, 4, 2);
            bool active = t.Active();
            Text(menu, tris, layer, active ? "ACTIVE" : "IDLE", (TpValueX0 + TpValueX1) / 2, by, 0.84f, 255, 255, 255);
            TryArrow(tris, layer, TpValueX0 + 4, by, left: true);
            TryArrow(tris, layer, TpValueX1 - 4, by, left: false);
            // the selected option's hint (C17), then how to close
            float hy = by - TpRowH / 2 - 7;
            foreach (string line in WrapLines(menu, ControlHints.For(active ? "practicebots.active" : "practicebots.idle"), 0.64f, TpHintWidth).Take(2))
            {
                Text(menu, tris, layer, line, 128, hy, 0.64f, 255, 247, 156);
                hy -= 7;
            }
            Text(menu, tris, layer, "tap outside to close", 128, y1 + 6.2f, 0.58f, 150, 150, 150);
            // the skin chooser over it
            if (_chooser is { Layer: var l } && l == layer && ChooserLive(menu)) DrawChooser(menu, tris, layer, _chooser);
        }

        // slot's icon: the ROM's own for the seven hunters (SELECT HUNTER's icon item in its Idle look), the skin's round
        // icon for TROOPER / DARK SAMUS
        private void DrawTryIcon(MenuEngine menu, List<WidgetTri> tris, int layer, int slot, float cx, float cy, float r)
        {
            if (slot >= HunterOrder.Length)
            {
                int tex = TrySkin(slot) is string skin ? IconTexture(menu, skin, grey: false) : -1;
                if (tex >= 0) Quad(tris, layer, tex, cx - r, cy + r, cx + r, cy - r, 1, 1, 1, 1);
                return;
            }
            if (!_picker.TryGetValue(HunterPage, out PickerItems? pi) || pi.Icon[slot] < 0) return;
            List<WidgetTri> icon = IdleLook(menu, menu.File.Pages[HunterPage], pi.Icon[slot]);
            if (icon.Count == 0) return;
            float ix0 = float.MaxValue, ix1 = float.MinValue, iy0 = float.MaxValue, iy1 = float.MinValue;
            foreach (WidgetTri tri in icon)
            {
                foreach (UiVertex v in new[] { tri.A, tri.B, tri.C })
                {
                    ix0 = MathF.Min(ix0, v.X); ix1 = MathF.Max(ix1, v.X);
                    iy0 = MathF.Min(iy0, v.Y); iy1 = MathF.Max(iy1, v.Y);
                }
            }
            float s = 2 * r / MathF.Max(1e-3f, MathF.Max(ix1 - ix0, iy1 - iy0)), mx = (ix0 + ix1) / 2, my = (iy0 + iy1) / 2;
            UiVertex Move(UiVertex v)
            {
                v.X = cx + (v.X - mx) * s;
                v.Y = cy + (v.Y - my) * s;
                return v;
            }
            foreach (WidgetTri t0 in icon)
            {
                WidgetTri tri = t0;
                tri.A = Move(tri.A); tri.B = Move(tri.B); tri.C = Move(tri.C);
                tri.Item = layer;
                tri.Z = 5000;
                tris.Add(tri);
            }
        }

        private static string SlotName(int slot) => TryCharacter(slot) is { HasOriginal: false } c ? c.Name.ToUpperInvariant()
            : TryHunter(slot).ToString().ToUpperInvariant();

        // a small gold triangle pointing left or right, its middle at (x, y)
        private static void TryArrow(List<WidgetTri> tris, int item, float x, float y, bool left)
        {
            const float w = 2.6f, h = 3.2f;
            float tip = left ? x - w : x + w, back = left ? x + w : x - w;
            var c = (R: 255 / 255f, G: 220 / 255f, B: 90 / 255f);
            tris.Add(new WidgetTri
            {
                A = new UiVertex(tip, y, 0, 0, c.R, c.G, c.B, 1), B = new UiVertex(back, y + h, 0, 0, c.R, c.G, c.B, 1),
                C = new UiVertex(back, y - h, 0, 0, c.R, c.G, c.B, 1), Z = 5000, TextureId = -1, Item = item,
                WrapS = UiWrap.Clamp, WrapT = UiWrap.Clamp
            });
        }

        // words broken into lines no wider than `width` at `scale`
        private static List<string> WrapLines(MenuEngine menu, string text, float scale, float width)
        {
            var lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && menu.Font.Measure(next) * scale > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }
    }
}
