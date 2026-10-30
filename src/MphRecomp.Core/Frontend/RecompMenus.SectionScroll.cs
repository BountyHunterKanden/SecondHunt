using System;

namespace MphRecomp.Frontend
{
    // Section pages (CONTROLS, SECOND HUNT SETTINGS' tabs) scrolled by a drag, the mouse wheel and the right stick (owner
    // 2026-10-17: "I want the setting pages to be scrolls with scroll wheel, right stick, and with finger scrolling on touch
    // screen and by grabbing the scroll bar with finger or mouse"). The view still moves a whole row at a time, as the D-pad
    // scrolls it. FrontendSession holds a touch that lands on the rows or the bar (SectionGrab) until it moves, a drag
    // (SectionDrag: the rows follow the finger; on the bar, the thumb does), or lifts: then it is the tap it always was.
    public sealed partial class RecompMenus
    {
        private int _dragKind; // what the touch took: 0 nothing, 1 the rows, 2 the scroll bar
        private float _dragY0, _dragGrab; // where it landed (menu units, y up); on the bar: the thumb's top above the finger
        private int _dragScroll0;

        // the section page on screen, when its list is longer than the view
        private SectionPage? ScrollingSection(MenuEngine menu)
            => menu.Page is MenuPage page && SectionFor(page.Index) is SectionPage sp && MaxScroll(sp.Entries) > 0 ? sp : null;

        // what a touch at (x, y) (menu units, y up) takes on the page on screen: 0 nothing (the tap goes at once, as before),
        // 1 the rows, 2 the scroll bar (on its thumb: held where it was taken; elsewhere on the bar: by the thumb's middle)
        public int SectionGrab(MenuEngine menu, float x, float y)
        {
            _dragKind = 0;
            if (ScrollingSection(menu) is not SectionPage sp) return 0;
            (float barTop, float barBottom, float thumb, float at) = ScrollBar(sp);
            if (x >= SecBarX0 - 4 && x <= SecBarX1 + 6 && y <= barTop + 2 && y >= barBottom - 2)
            {
                _dragKind = 2;
                _dragGrab = y <= at && y >= at - thumb ? at - y : thumb / 2;
            }
            else if (x >= TabIconL0 && x < SecBarX0 - 4 && y <= SlotTop(0) && y >= SlotTop(SecSlots - 1) - SecH)
            {
                _dragKind = 1;
            }
            _dragY0 = y;
            _dragScroll0 = sp.Scroll;
            return _dragKind;
        }

        // the held touch now at y (menu units, y up): the rows move with it a row per row's pitch, or the thumb follows it
        public void SectionDrag(MenuEngine menu, float y)
        {
            if (_dragKind == 0 || ScrollingSection(menu) is not SectionPage sp) return;
            int to;
            if (_dragKind == 1)
            {
                to = _dragScroll0 + (int)MathF.Round((y - _dragY0) / SecPitch);
            }
            else
            {
                (float barTop, float barBottom, float thumb, _) = ScrollBar(sp);
                float travel = barTop - barBottom - thumb;
                to = travel <= 0 ? 0 : (int)MathF.Round((barTop - (y + _dragGrab)) / travel * MaxScroll(sp.Entries));
            }
            ScrollSectionTo(menu, sp, to);
        }

        // the wheel's and the right stick's rows (+ down the list)
        public void SectionScrollBy(MenuEngine menu, int rows)
        {
            if (rows != 0 && ScrollingSection(menu) is SectionPage sp) ScrollSectionTo(menu, sp, sp.Scroll + rows);
        }

        // the view from entry `to`, the focus moved into it when it scrolled out (as the bar's taps)
        private void ScrollSectionTo(MenuEngine menu, SectionPage sp, int to)
        {
            to = Math.Clamp(to, 0, MaxScroll(sp.Entries));
            if (to == sp.Scroll) return;
            sp.Scroll = to;
            _captureId = null;
            Rebuild(sp, keepView: true);
            ShowSection(menu, sp);
        }

        // the scroll bar as DrawSection draws it: its top and bottom, the thumb's length and top
        private static (float Top, float Bottom, float Thumb, float At) ScrollBar(SectionPage sp)
        {
            int count = sp.Entries.Count, max = MaxScroll(sp.Entries);
            float top = SlotTop(0), bottom = SlotTop(SecSlots - 1) - SecH, length = top - bottom;
            float thumb = MathF.Max(length * SecSlots / Math.Max(count + SecSlots - 1, 1), 8);
            float at = max > 0 ? top - (length - thumb) * sp.Scroll / max : top;
            return (top, bottom, thumb, at);
        }
    }
}
