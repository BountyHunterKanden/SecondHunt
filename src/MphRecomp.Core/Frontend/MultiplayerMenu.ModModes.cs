using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Mods;
using MphRecomp.Multiplayer;

namespace MphRecomp.Frontend
{
    // The mod modes' row at the end of the GAME MODE list (owner 2026-10-30, option a: "Add them to the end of the GAME MODE
    // list, after the normal modes. This is simplest for players."). The modes the player's gameplay mods add
    // (LiveMods.Modes, mod.json Modes) in one stepper row under BATTLE, built like the rules pages' MUSIC row from the ARENA
    // row's widgets: the arrows step through the mods' modes, A (or a tap on the name) picks the shown one and opens the
    // rules page of the mode it plays as, with the mod's starting values. Focused, its description shows where the modes'
    // do. Offline only (docs/MOD_API.md): never on LAN or online setups, and a lobby playing one hosts no LAN game.
    public sealed partial class MultiplayerMenu
    {
        private const int ModNextCall = 2350, ModPrevCall = 2351, ModPickCall = 2352;
        // from the ARENA row on page 28 (menu space): under BATTLE, the box 71.5 narrower (its arrows 35.75 in each side);
        // the label from BATTLE's own, 13 lower. Measured on -fedump 27/28: the row x 3..87, y -151..-135
        private const float ModDx = -120.75f, ModDy = -92, ModStretch = -71.5f, ModLabelDy = -13, ModNameWidth = 44;
        private const float BattleLabelX = 44, BattleLabelY = 74;
        // the taps (text x): the left arrow, the name (pick), the right arrow; kept left of PRIME HUNTER's tile (x 85.4)
        private const float ModTapLeft = 0, ModTapPick = 24, ModTapRight = 66, ModTapEnd = 86;

        private sealed class ModModesBound
        {
            public int Label = -1, Name = -1, Desc = -1; // the row's strings
            public int DescItem = -1;
            public int[] Items = Array.Empty<int>(); // the row's items (not the description)
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, ModModesBound> s_modBound = new();
        private ModModesBound _modBound = new();
        private MenuFont? _modFont;
        private static List<LiveMods.ModeChoice> _modChoices = new(); // the mode page's last visit's list (ActiveModMode reads it)
        private int _modAt; // the row's shown choice
        // where the player's mods live (FrontendSession): the row's list between games, when none are attached
        public string? ModsDir { get; set; }

        // (Bind, after BindModeDescriptions: its description joins theirs)
        private void BindModModes(MenuFile file, MenuStrings strings)
        {
            if (s_modBound.TryGetValue(file, out ModModesBound? bound))
            {
                _modBound = bound;
                if (bound.DescItem >= 0 && !_modeDescriptions.Contains(bound.DescItem)) _modeDescriptions = _modeDescriptions.Append(bound.DescItem).ToArray();
                return;
            }
            _modBound = new ModModesBound();
            s_modBound.AddOrUpdate(file, _modBound);
            if (file.Pages.Count <= 28 || _modeDescriptions.Length == 0) return;
            MenuPage modes = file.Pages[ModePage], arena = file.Pages[28];
            int arrows = FindItem(arena, it => HasWidget(file, it, "arrows_type"));
            int box = FindItem(arena, it => HasWidget(file, it, "box_type") && Calls(it).Contains(113));
            int left = FindItem(arena, it => HasWidget(file, it, "highlight_arrowleft") && Calls(it).Contains(114));
            int right = FindItem(arena, it => HasWidget(file, it, "highlight_arrowright") && Calls(it).Contains(113));
            int name = TextAt(arena, ArenaNameX, ArenaNameY);
            int label = TextAt(modes, BattleLabelX, BattleLabelY);
            int sound = FindItem(modes, it => Calls(it).Contains(Modes[0].Call)); // (BATTLE's highlight: a pick sounds like one)
            if (new[] { arrows, box, left, right, name, label, sound }.Any(i => i < 0))
            {
                Log?.Invoke("multiplayer: the ARENA row or BATTLE not found, no mod mode row");
                return;
            }
            _modBound.Label = strings.Add("");
            _modBound.Name = strings.Add("");
            _modBound.Desc = strings.Add("");

            // the description: a copy of the first mode's, where they all are, shown while the box has focus
            MenuItem desc0 = modes.Items[_modeDescriptions[0]];
            _modBound.DescItem = modes.Items.Count;
            modes.AddItem(n => SoundsLike(RecompMenus.Clone(desc0, n, 0, 0, stringId: _modBound.Desc,
                init: MenuState.Hidden, links: Array.Empty<MenuLink>(), actions: Array.Empty<MenuAction>()), desc0.Index));
            _modeDescriptions = _modeDescriptions.Append(_modBound.DescItem).ToArray();

            // a tap on an arrow's clone: text x0..x1 (absolute) as its own rectangle, the source's height
            float side = -ModStretch / 2;
            IReadOnlyList<MenuAction> Taps(MenuItem it, float itemDx, params (float X0, float X1, int Call)[] taps)
            {
                MenuAction? a = it.Actions.FirstOrDefault(x => (x.Field4 & 1) == 0 && x.RectW > x.RectX);
                if (a == null) return Array.Empty<MenuAction>();
                float at = it.X + itemDx;
                return taps.Select(t => new MenuAction
                {
                    Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = (short)MathF.Round(t.X0 - at),
                    RectY = a.RectY, RectW = (short)MathF.Round(t.X1 - at), RectH = a.RectH, Calls = new[] { (0, t.Call) },
                    Item = 0xFFFF, TargetPage = 0xFF, Field17 = a.Field17
                }).ToList();
            }
            int focusIn = MenuStateCode.Transition(MenuState.Any, MenuState.Focused);
            int focusOut = MenuStateCode.Transition(MenuState.Focused, MenuState.Any);
            var added = new List<int>();
            void Add(MenuItem src, float dx, float dy, int stringId = -1, float stretch = 0, IReadOnlyList<MenuLink>? links = null,
                IReadOnlyList<MenuAction>? actions = null)
            {
                added.Add(modes.Items.Count);
                modes.AddItem(n => SoundsLike(RecompMenus.Clone(src, n, dx, dy, stringId: stringId,
                    links: links ?? Array.Empty<MenuLink>(), actions: actions ?? Array.Empty<MenuAction>(), stretchX: stretch), sound));
            }
            Add(modes.Items[label], 0, ModLabelDy, stringId: _modBound.Label);
            Add(arena.Items[arrows], ModDx, ModDy, stretch: ModStretch);
            Add(arena.Items[box], ModDx, ModDy, stretch: ModStretch,
                links: new[] { new MenuLink(focusIn, MenuState.Idle, _modBound.DescItem), new MenuLink(focusOut, MenuState.Hidden, _modBound.DescItem) },
                actions: new[]
                {
                    RecompMenus.Call(MenuKeys.A, ModPickCall), RecompMenus.Call(MenuKeys.Y, ModPrevCall), RecompMenus.Call(MenuKeys.X, ModNextCall),
                    RecompMenus.Call(MenuKeys.Left, ModPrevCall), RecompMenus.Call(MenuKeys.Right, ModNextCall)
                });
            Add(arena.Items[left], ModDx + side, ModDy, actions: Taps(arena.Items[left], ModDx + side, (ModTapLeft, ModTapPick, ModPrevCall)));
            Add(arena.Items[right], ModDx - side, ModDy,
                actions: Taps(arena.Items[right], ModDx - side, (ModTapPick, ModTapRight, ModPickCall), (ModTapRight, ModTapEnd, ModNextCall)));
            Add(arena.Items[name], ModDx, ModDy, stringId: _modBound.Name);
            _modBound.Items = added.ToArray();
        }

        // mod modes are offline: not a LAN joiner's setup or a LAN game with joiners, not ONLINE PLAY's
        private static bool ModModesOffline => !InOnlineSetup && !s_onlineEdit && !_setupFromHost && JoinerSeat() < 0
            && Lan is not { IsHost: false };

        // the picked mod mode while it still applies (its mod running, the setup offline), else null
        private static LiveMods.ModeChoice? ActiveModMode()
        {
            if (_setup.ModMode is not string key || !ModModesOffline) return null;
            return _modChoices.FirstOrDefault(c => c.Key == key);
        }

        // OnPageEntered (the mode page): the row while there are mod modes to offer, else none of it
        private void ShowModModes(MenuEngine menu, int page)
        {
            if (page != ModePage || _modBound.Items.Length == 0) return;
            _modFont = menu.Font;
            _modChoices = new List<LiveMods.ModeChoice>();
            if (ModModesOffline && !MphRecomp.Config.RecompSettings.PublicBuild) // gated for later in public (owner 2026-11-07)
            {
                try
                {
                    _modChoices = LiveMods.Modes(ModsDir, MusicSettings?.Invoke());
                }
                catch (Exception ex)
                {
                    Log?.Invoke("multiplayer: no mod modes: " + ex.Message);
                }
            }
            int now = _modChoices.FindIndex(c => c.Key == _setup.ModMode);
            _modAt = now >= 0 ? now : Math.Clamp(_modAt, 0, Math.Max(0, _modChoices.Count - 1));
            bool on = _modChoices.Count > 0;
            foreach (int item in _modBound.Items)
            {
                if (!on) menu.SetState(item, MenuState.Hidden);
                else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
            }
            if (menu.ItemState(_modBound.DescItem) != MenuState.Hidden && !(on && menu.FocusedItem() == _modBound.Items[2]))
            {
                menu.SetState(_modBound.DescItem, MenuState.Hidden);
            }
        }

        // OnCall (the mode page): the arrows step, A or a tap on the name picks
        private bool OnModModeCall(MenuEngine menu, int b)
        {
            if (b is not (ModNextCall or ModPrevCall or ModPickCall)) return false;
            if (_modChoices.Count == 0) return true;
            if (b != ModPickCall)
            {
                _modAt = ((_modAt + (b == ModNextCall ? 1 : -1)) % _modChoices.Count + _modChoices.Count) % _modChoices.Count;
                FillStrings();
                return true;
            }
            LiveMods.ModeChoice c = _modChoices[Math.Clamp(_modAt, 0, _modChoices.Count - 1)];
            ModeInfo? m = Modes.FirstOrDefault(x => x.Solo == c.Base) ?? Modes.FirstOrDefault(x => x.Teams == c.Base);
            if (m == null)
            {
                Log?.Invoke($"multiplayer: mod mode {c.Key} plays as {c.Base}, not a match mode");
                return true;
            }
            bool fresh = _setup.ModMode != c.Key;
            if (fresh) _defaultsSet = false; // (the mod's starting values, even on the mode it plays as)
            RefreshArenas();
            PickMode(m.Id);
            if (fresh) // (a mod's values brought into the rows' ranges: they step by 1 between each table's ends)
            {
                if (c.PointGoal is int goal) _setup.Goal = ClampTo(GoalTable(GoalRule(m)), goal);
                if (c.TimeLimitMinutes is int minutes) _setup.TimeLimit = ClampTo(TimeLimitMinutes, minutes);
                if (c.TimeGoalSeconds is int seconds) _setup.TimeGoal = ClampTo(TimeGoalSeconds, seconds);
                if (c.Base == m.Teams && m.Teams != m.Solo) _setup.TeamPlay = true;
            }
            _setup.ModMode = c.Key;
            _setup.ModeChosen = true;
            _setup.RulesSeen = true;
            Log?.Invoke($"multiplayer: mod mode {c.Key} ({c.Name}), plays as {m.Name}");
            menu.GoTo(m.Page);
            return true;
        }

        // FillStrings: the row's label, name and description
        private void FillModModes(MenuStrings s)
        {
            if (_modBound.Name < 0) return;
            int count = _modChoices.Count;
            LiveMods.ModeChoice? c = count > 0 ? _modChoices[Math.Clamp(_modAt, 0, count - 1)] : null;
            s.Set(_modBound.Label, count > 1 ? $"mod mode {_modAt + 1}/{count}" : "mod mode");
            s.Set(_modBound.Name, c == null ? "" : FitMod(c.Name.ToLowerInvariant()));
            string base_ = Modes.FirstOrDefault(x => x.Solo == c?.Base || x.Teams == c?.Base)?.Name ?? "";
            s.Set(_modBound.Desc, c == null ? ""
                : $"{c.Name.ToUpperInvariant()}: {(String.IsNullOrWhiteSpace(c.Description) ? "" : c.Description!.Trim() + " ")}"
                + $"({c.ModId} mod, plays as {base_})");
        }

        private string FitMod(string text)
        {
            if (_modFont == null || _modFont.Measure(text) <= ModNameWidth) return text;
            for (int n = text.Length - 1; n > 0; n--)
            {
                string cut = text[..n].TrimEnd() + "...";
                if (_modFont.Measure(cut) <= ModNameWidth) return cut;
            }
            return text;
        }
    }
}
