using System;
using System.Collections.Generic;

// The online play rules, out of the way (owner 2026-10-09: no pop-ups; "a small subtle text" in the main menu's
// settings): on Options (page 21) one dim line along the bottom edge, "privacy, code of conduct, ages 13+". Lower right
// corner (owner 2026-10-10, was under the back arrow, lower left).
// A tap (touch or mouse) asks the host for the rules page (FrontendRequestKind.OnlineRules: the credits viewer showing
// docs/ONLINE_RULES_IN_APP.md, embedded as "online.md"). Social O7; hooks in RecompMenus.cs (AddOptionsButtons, the call
// switch, DrawOptions). ONLINE PLAY's hub carries the age line itself (OnlineMenus.DrawHub).
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private const int OpenRulesCall = 1030;
        private const string RulesText = "privacy, code of conduct, ages 13+";
        // menu units: right-aligned along the bottom edge in Options' footer-right region (anchored to the screen's
        // bottom-right, the back arrow's mirror: its right end as far in as the arrow's left end), below the description box
        // at every aspect. The line (88 wide) reaches left of the ROM footer's x 176, so Options' footer-right region
        // starts further left (MenuLayout.Options, OptionsFooterRightX0): a tap only counts inside its region
        // (RulesWidth: the line's measured width, for the tap box: no font yet when the item is made)
        private const float RulesRight = 246, RulesTop = -183, RulesSize = 0.45f, RulesWidth = 89;
        private int _rulesLink = -1;

        private void AddRulesLink(MenuPage options)
        {
            float x0 = RulesRight - RulesWidth, x1 = RulesRight;
            _rulesLink = options.AddItem(i => new MenuItem
            {
                Index = i, Field34 = (byte)MenuState.Idle, Field35 = (byte)MenuState.Idle, States = Array.Empty<MenuItemState>(),
                Links = Array.Empty<MenuLink>(),
                // the whole line with a finger's margin, from the bottom edge up to above the text (owner 2026-10-10: the
                // old 8-unit-high box under the back arrow missed taps)
                Actions = new[] { TouchRect(MathF.Max(x0 - 6, MenuLayout.OptionsFooterRightX0), 0, MathF.Min(x1 + 6, 256), RulesTop + 14 + 192, OpenRulesCall) }
            }).Index;
        }

        private void DrawRulesLink(MenuEngine menu, List<WidgetTri> tris)
        {
            if (_rulesLink < 0 || menu.Page?.Index != OptionsPage) return;
            _drawMenu = menu;
            _out = tris;
            float x0 = RulesRight - menu.Font.Measure(RulesText) * RulesSize;
            int start = _out.Count;
            Text(RulesText, x0, RulesTop, 0, (0.75f, 0.8f, 0.8f), _rulesLink, 0.55f);
            for (int t = start; t < _out.Count; t++)
            {
                WidgetTri tri = _out[t];
                tri.A = Scale(tri.A); tri.B = Scale(tri.B); tri.C = Scale(tri.C);
                _out[t] = tri;
            }
            UiVertex Scale(UiVertex v)
            {
                v.X = x0 + (v.X - x0) * RulesSize;
                v.Y = RulesTop + (v.Y - RulesTop) * RulesSize;
                return v;
            }
        }
    }
}
