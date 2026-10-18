using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Frontend
{
    // The mode page's descriptions in a box of their own under the mode panel (owner 2026-10-16: "the mode description
    // text shows up in a box below the mode select instead of in the settings box on the left. If you'd set rules and then
    // go back to select a mode the mode description displays over the settings text ... its own horizontal box under the
    // game mode and move the back button more into the corner"). The ROM draws the seven descriptions (items 50-56, one
    // shown per focused mode, 12-frame fades) in the top screen's left box, where FillStrings puts the settings once the
    // rules were seen. Here they move to the touch screen under the panel (y -155), centred and wrapped wider; the setup
    // composition gives that spot a region of its own (MenuLayout.Setup's "description") and the back arrow the bottom
    // left corner, and RecompMenus.DrawModeDescription draws the box behind the shown one. LAN and online alike: both
    // walk the same setup pages.
    public sealed partial class MultiplayerMenu
    {
        // the descriptions' ROM spot (text space) and their new one: centred on the touch screen, the first line's top 3
        // under the panel's bottom edge (menu y -155; text y = menu top + 185)
        private const float RomDescX = 56, RomDescY = 347, DescX = 128;
        public const float DescTop = -159, DescPanelBottom = -155;
        private const int DescWrapGrow = 150; // 100 -> 250: three or four lines, inside a DS screen's width (side by side)

        private int[] _modeDescriptions = Array.Empty<int>();

        // the mode page's description items (moved), for the draw pass's box
        public IReadOnlyList<int> ModeDescriptions => _modeDescriptions;

        private void BindModeDescriptions(MenuFile file)
        {
            if (file.Pages.Count <= ModePage) return;
            MenuPage page = file.Pages[ModePage];
            const float descY = DescTop + 185;
            for (int i = 0; i < page.Items.Count; i++)
            {
                MenuItem it = page.Items[i];
                if (!it.IsText || MathF.Abs(it.X - RomDescX) >= 1 || MathF.Abs(it.Y - RomDescY) >= 2) continue;
                page.ReplaceItem(i, n => SoundsLike(RecompMenus.Clone(it, n, DescX - it.X, descY - it.Y, stringId: it.States[0].Text!.StringId,
                    links: it.Links, actions: it.Actions, wrapGrow: DescWrapGrow), it.Index));
            }
            // (found at their new spot: a file bound before keeps them moved)
            _modeDescriptions = Enumerable.Range(0, page.Items.Count)
                .Where(i => page.Items[i].IsText && MathF.Abs(page.Items[i].X - DescX) < 1 && MathF.Abs(page.Items[i].Y - descY) < 1)
                .ToArray();
            if (_modeDescriptions.Length != Modes.Length)
            {
                Log?.Invoke($"multiplayer: {_modeDescriptions.Length} mode descriptions moved (expected {Modes.Length})");
            }
        }
    }

    public sealed partial class RecompMenus
    {
        // FrontendSession's draw pass: the mode page's description box (MultiplayerMenu.ModeDescription.cs), a dark panel
        // with the options' box outline behind the shown description, as tall as four lines (the longest), drawn before the
        // text and tagged with it (its region, its fade). wide: the setup composition's, as wide as the mode panel there;
        // else the panel's DS width
        public void DrawModeDescription(MenuEngine menu, List<WidgetTri> tris, bool wide)
        {
            if (Multiplayer == null || menu.Page?.Index != MultiplayerMenu.ModePage) return;
            IReadOnlyList<int> items = Multiplayer.ModeDescriptions;
            int first = tris.FindIndex(t => items.Contains(t.Item));
            if (first < 0) return;
            int tag = tris[first].Item;
            float alpha = tris.Where(t => items.Contains(t.Item)).Max(t => t.A.A);
            float half = wide ? MenuLayout.SetupDescWidth / 2 : 131.2f, mid = 126.9f; // (the panel's: x -4.3..258.1)
            const float top = MultiplayerMenu.DescPanelBottom - 1.5f, bottom = top - 52;
            _drawMenu = menu;
            List<WidgetTri> keep = _out;
            var box = new List<WidgetTri>();
            _out = box;
            Fill(mid - half, top, mid + half, bottom, 0, 0, 0, 0.58f * alpha, tag);
            Box(mid - half, top, mid + half, bottom, tag);
            _out = keep;
            tris.InsertRange(first, box);
        }
    }
}
