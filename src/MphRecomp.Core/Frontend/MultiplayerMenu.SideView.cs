using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Frontend
{
    // Side view P0 (owner 2026-11-12, docs/SIDE_VIEW_MODE.md): a dev-only row under GAME MODE's panel that plays BATTLE on
    // Data Shrine side-on. Built like the mod-mode row (MultiplayerMenu.ModModes.cs) from the ARENA row's box and BATTLE's
    // label, without arrows (one stage so far): A or a tap on it picks BATTLE with Data Shrine as the arena and opens its
    // rules page; START GAME's setup then carries sv=1 (MatchSettings.SideView) and the match side turns it on
    // (SideViewArena.cs). Offline only, never in a public build. Any other pick on the page, or a new CREATE GAME, drops it.
    public sealed partial class MultiplayerMenu
    {
        private const int SideViewPickCall = 2360;
        private const int SideViewRoom = 93; // MP1 SANCTORUS (Data Shrine), the one side-view stage so far
        // one row under the panel, centred (clear of the back icon at x 10..30 on a DS-shaped screen): the label on one
        // line (BATTLE's wrap 48 wider), the box 40 wider than the mod-mode row's so "data shrine" fits. Measured on
        // -mpmenu 27: the label y -165..-157 under the panel's edge (-155), the box x 87..167, y -184..-168
        private const float SideRowDx = 82, SideRowDy = -33, SideStretch = ModStretch + 40;
        private const int SideLabelWrap = 48;

        private sealed class SideViewBound
        {
            public int Label = -1, Name = -1, Desc = -1; // the row's strings
            public int DescItem = -1;
            public int Box = -1;
            public int[] Items = Array.Empty<int>(); // the row's items (not the description)
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, SideViewBound> s_sideBound = new();
        private SideViewBound _sideBound = new();
        private static bool s_sideView; // picked on the mode page (SideViewPicked)

        private static bool SideViewOffered => ModModesOffline && !MphRecomp.Config.RecompSettings.PublicBuild;

        // the settings' MatchSettings.SideView: picked, and still an offline setup in a dev build
        private static bool SideViewPicked() => s_sideView && SideViewOffered;

        // (Bind, after BindModModes)
        private void BindSideView(MenuFile file, MenuStrings strings)
        {
            if (s_sideBound.TryGetValue(file, out SideViewBound? bound))
            {
                _sideBound = bound;
                if (bound.DescItem >= 0 && !_modeDescriptions.Contains(bound.DescItem)) _modeDescriptions = _modeDescriptions.Append(bound.DescItem).ToArray();
                return;
            }
            _sideBound = new SideViewBound();
            s_sideBound.AddOrUpdate(file, _sideBound);
            if (file.Pages.Count <= 28 || _modeDescriptions.Length == 0) return;
            MenuPage modes = file.Pages[ModePage], arena = file.Pages[28];
            int box = FindItem(arena, it => HasWidget(file, it, "box_type") && Calls(it).Contains(113));
            int right = FindItem(arena, it => HasWidget(file, it, "highlight_arrowright") && Calls(it).Contains(113));
            int name = TextAt(arena, ArenaNameX, ArenaNameY);
            int label = TextAt(modes, BattleLabelX, BattleLabelY);
            int sound = FindItem(modes, it => Calls(it).Contains(Modes[0].Call));
            if (new[] { box, right, name, label, sound }.Any(i => i < 0))
            {
                Log?.Invoke("multiplayer: the ARENA row or BATTLE not found, no side-view row");
                return;
            }
            _sideBound.Label = strings.Add("");
            _sideBound.Name = strings.Add("");
            _sideBound.Desc = strings.Add("");

            MenuItem desc0 = modes.Items[_modeDescriptions[0]];
            _sideBound.DescItem = modes.Items.Count;
            modes.AddItem(n => SoundsLike(RecompMenus.Clone(desc0, n, 0, 0, stringId: _sideBound.Desc,
                init: MenuState.Hidden, links: Array.Empty<MenuLink>(), actions: Array.Empty<MenuAction>()), desc0.Index));
            _modeDescriptions = _modeDescriptions.Append(_sideBound.DescItem).ToArray();

            // a tap anywhere on the box: the right arrow's touch rectangle (its height), across the row's width
            float dx = ModDx + SideRowDx, dy = ModDy + SideRowDy;
            MenuItem src = arena.Items[box];
            MenuAction? a = arena.Items[right].Actions.FirstOrDefault(x => (x.Field4 & 1) == 0 && x.RectW > x.RectX);
            float at = src.X + dx, rise = arena.Items[right].Y - src.Y;
            var tap = new List<MenuAction>
            {
                RecompMenus.Call(MenuKeys.A, SideViewPickCall)
            };
            if (a != null)
            {
                tap.Add(new MenuAction
                {
                    Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4,
                    RectX = (short)MathF.Round(ModTapLeft + SideRowDx - at), RectW = (short)MathF.Round(ModTapEnd + SideRowDx - at),
                    RectY = (short)MathF.Round(a.RectY + rise), RectH = (short)MathF.Round(a.RectH + rise),
                    Calls = new[] { (0, SideViewPickCall) }, Item = 0xFFFF, TargetPage = 0xFF, Field17 = a.Field17
                });
            }
            int focusIn = MenuStateCode.Transition(MenuState.Any, MenuState.Focused);
            int focusOut = MenuStateCode.Transition(MenuState.Focused, MenuState.Any);
            var added = new List<int>();
            void Add(MenuItem from, float x, float y, int stringId = -1, float stretch = 0, IReadOnlyList<MenuLink>? links = null,
                IReadOnlyList<MenuAction>? actions = null, int wrap = 0)
            {
                added.Add(modes.Items.Count);
                modes.AddItem(n => SoundsLike(RecompMenus.Clone(from, n, x, y, stringId: stringId,
                    links: links ?? Array.Empty<MenuLink>(), actions: actions ?? Array.Empty<MenuAction>(), stretchX: stretch,
                    wrapGrow: wrap), sound));
            }
            Add(modes.Items[label], SideRowDx, ModLabelDy + SideRowDy, stringId: _sideBound.Label, wrap: SideLabelWrap);
            _sideBound.Box = modes.Items.Count;
            Add(src, dx, dy, stretch: SideStretch,
                links: new[] { new MenuLink(focusIn, MenuState.Idle, _sideBound.DescItem), new MenuLink(focusOut, MenuState.Hidden, _sideBound.DescItem) },
                actions: tap);
            Add(arena.Items[name], dx, dy, stringId: _sideBound.Name);
            _sideBound.Items = added.ToArray();
        }

        // OnPageEntered (the mode page): the row in an offline dev setup, else none of it; a new CREATE GAME drops the pick
        private void ShowSideView(MenuEngine menu, int page)
        {
            if (page != ModePage || _sideBound.Items.Length == 0) return;
            if (!_setup.ModeChosen) s_sideView = false;
            bool on = SideViewOffered;
            foreach (int item in _sideBound.Items)
            {
                if (!on) menu.SetState(item, MenuState.Hidden);
                else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
            }
            if (menu.ItemState(_sideBound.DescItem) != MenuState.Hidden && !(on && menu.FocusedItem() == _sideBound.Box))
            {
                menu.SetState(_sideBound.DescItem, MenuState.Hidden);
            }
        }

        // OnCall (the mode page), ahead of the mod modes and the modes: the row's pick; any other mode pick drops it
        private bool OnSideViewCall(MenuEngine menu, int b)
        {
            if (b != SideViewPickCall)
            {
                if (b == ModPickCall || Modes.Any(m => m.Call == b)) s_sideView = false;
                return false;
            }
            if (!SideViewOffered) return true;
            if (_setup.ModMode != null)
            {
                _setup.ModMode = null;
                _defaultsSet = false;
            }
            ModeInfo m = Modes[0]; // BATTLE
            RefreshArenas();
            PickMode(m.Id);
            int arena = Array.IndexOf(ArenaRooms, SideViewRoom);
            if (arena >= 0) _setup.Arena = arena;
            _setup.ModeChosen = true;
            _setup.RulesSeen = true;
            s_sideView = true;
            Log?.Invoke("multiplayer: side view (dev): Battle on Data Shrine, side-on");
            menu.GoTo(m.Page);
            return true;
        }

        // FillStrings: the row's label, name and description
        private void FillSideView(MenuStrings s)
        {
            if (_sideBound.Name < 0) return;
            s.Set(_sideBound.Label, "side view (dev)");
            s.Set(_sideBound.Name, "data shrine");
            s.Set(_sideBound.Desc, "SIDE VIEW (dev builds): BATTLE on Data Shrine seen from the side, the bots standing still. "
                + "The reticle follows the mouse; hold L2 or right mouse to stand and aim (laser). In the match: [ ] jump, ; ' run "
                + "speed, - = zoom, 9 0 KO gap, \\ resets.");
        }
    }
}
