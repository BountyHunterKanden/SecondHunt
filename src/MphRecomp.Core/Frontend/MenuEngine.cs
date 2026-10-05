using System;
using System.Linq;
using System.Collections.Generic;

// Runs the ROM's menu graph (MenuFile) the way the data describes it: pages of items, each item moving between
// steady states (Hidden / Idle / Selected / Focused) through the transition animations the file names, links that
// move other items when one changes state, button/touch actions, timed page actions, and numbered game callbacks
// that the host implements (starting Adventure, paging, save files...). Everything here is simulation -- Collect()
// turns the current look into menu-space triangles for the layout to place.
//
// Rules marked "(?)" are inferred from the data (2026-09-29) and are to be confirmed against the real game.
namespace MphRecomp.Frontend
{
    // The menu data's key bits (an action's Kind) are the game's own menu encoding, not the DS KEYINPUT order: ov0 USA
    // rev 0 0x214253c builds them from the pad as A 1, B 2, X 4, Y 8, L 0x10, R 0x20, Start 0x40, Select 0x80, Up 0x100,
    // Down 0x200, Left 0x400, Right 0x800 (and 0x213f9dc reads Up/Down/Right/Left = 0x100/0x200/0x800/0x400). The data
    // agrees: the results page's Kind 0x400/0x800 run the same callbacks as its left/right arrows, its page actions 0x10/
    // 0x20 the same pair (L/R), and the sound test's Kind 8 the same as its left arrow (Y = previous).
    [Flags]
    public enum MenuKeys : ushort
    {
        None = 0,
        A = 1, B = 2, X = 4, Y = 8, L = 0x10, R = 0x20, Start = 0x40, Select = 0x80,
        Up = 0x100, Down = 0x200, Left = 0x400, Right = 0x800,
        Any = 4095
    }

    public interface IMenuHost
    {
        // A numbered game callback from an action (the (A, B) pairs in the file; A has always been 0 so far). The
        // action's Field17 is the callback's argument where one is needed. Return true if handled. `item` is -1 for
        // page and timer actions.
        bool OnCall(MenuEngine menu, MenuAction action, int a, int b, int item);
        void OnPageEntered(MenuEngine menu, int page) { }
        void OnLog(string message) { }
        // the focus ring moved (D-pad/stick navigate, or a touch that focuses an item), and an action ran (button
        // press or touch on an item/page action)
        void OnFocusChanged(MenuEngine menu, int item) { }
        void OnAction(MenuEngine menu, MenuAction action, int item) { }
        // play a front-end sound: a plain sample id or 0x4000 | script id, chosen by the engine the way the game's
        // menu engine chooses it (MenuSounds); `why` is for logs
        void OnSound(MenuEngine menu, int sfxId, string why) { }
    }

    public sealed class MenuEngine
    {
        // the menu ticks at 30 Hz (like the game's 3D): measured on the real game -- the ESRB page's 90-frame timer
        // shows it for 180 display frames, and the white screen's 121-frame fade-in ends at display frame ~250
        public const int FramesPerSecond = 30;

        public MenuFile File { get; }
        public MenuStrings Strings { get; }
        public MenuWidgets Widgets { get; }
        public MenuFont Font { get; }
        public IMenuHost? Host { get; set; }
        // the ROM's per-page/per-item button sounds (data/sound/METROIDHUNTERS_IDS.DAT); Empty = only the fixed ones
        public MenuSounds Sounds { get; set; } = MenuSounds.Empty;

        public MenuPage? Page { get; private set; }
        // the host can send a page change somewhere else (the pause menu sends "back to Options" to itself)
        public Func<int, int>? Redirect { get; set; }
        public int PageFrame { get; private set; }
        private ItemRuntime[] _items = Array.Empty<ItemRuntime>();
        private int _pendingPage = -1;
        private int _exitFrames;

        public MenuEngine(MenuFile file, MenuStrings strings, MenuWidgets widgets, MenuFont font)
        {
            File = file;
            Strings = strings;
            Widgets = widgets;
            Font = font;
        }

        private sealed class ItemRuntime
        {
            public MenuState State = MenuState.Hidden; // steady state reached or being moved to
            public int Code = (int)MenuState.Hidden; // animation code playing (transition or steady)
            public int Frame;
            public float Delay;
            public bool Started;
        }

        // the page GoTo is leaving for while the current page's items play out (-1 = none): the game's SetPage makes the new
        // page current (state 0) at once, so its page music starts here, not when it is entered
        public int PendingPage => _pendingPage;

        // every item has started (its delay ran out) and none is mid-transition: the page is built (vanilla page state 2)
        public bool Settled => Page != null && _pendingPage == -1
            && _items.All(r => r.Started && !MenuStateCode.IsTransition(r.Code));

        // the page's one focused item (vanilla page+0x14): whatever puts an item in Focused -- a touch, the D-pad, an
        // action or another item's link -- first drops the previous one to Idle (BizHawk USA rev 1, file select: the
        // slot's highlight goes Focused->Idle as the create dialog's link focuses its YES; NO then closes the dialog
        // with YES Idle->Hidden, not Focused->Hidden, whose links would create the file)
        private int _focus = -1;

        public MenuState ItemState(int item) => _items[item].State;
        public int ItemCode(int item) => _items[item].Code;

        // ---- page flow ----

        public void Enter(int page)
        {
            Page = File.Pages[page];
            PageFrame = 0;
            _pendingPage = -1;
            _exitFrames = 0;
            _focus = -1;
            _items = new ItemRuntime[Page.Items.Count];
            for (int i = 0; i < _items.Length; i++)
            {
                _items[i] = new ItemRuntime { Delay = Page.Items[i].Delay };
            }
            Host?.OnPageEntered(this, page);
            StartItems();
        }

        // Leave for another page: every item plays its way out (to Hidden), then the next page comes in.
        public void GoTo(int page)
        {
            if (Redirect != null) page = Redirect(page);
            if (Page == null)
            {
                Enter(page);
                return;
            }
            _pendingPage = page;
            _exitFrames = 0;
            for (int i = 0; i < _items.Length; i++)
            {
                SetState(i, MenuState.Hidden);
            }
        }

        private void StartItems()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (!_items[i].Started && _items[i].Delay <= 0)
                {
                    _items[i].Started = true;
                    SetState(i, (MenuState)Page!.Items[i].Field34); // (?) Field34 = initial state
                }
            }
        }

        public void Tick()
        {
            if (Page == null) return;
            PageFrame++;
            for (int i = 0; i < _items.Length; i++)
            {
                ItemRuntime rt = _items[i];
                if (!rt.Started)
                {
                    rt.Delay -= 1;
                    continue;
                }
                rt.Frame++;
                if (MenuStateCode.IsTransition(rt.Code) && rt.Frame >= CodeLength(i, rt.Code))
                {
                    Arrive(i);
                }
            }
            if (_pendingPage == -1)
            {
                StartItems();
                foreach (MenuTimer timer in Page.Timers)
                {
                    if (PageFrame == (int)MathF.Ceiling(timer.Delay))
                    {
                        Run(timer.Action, item: -1);
                    }
                }
            }
            else
            {
                _exitFrames++;
                bool done = true;
                for (int i = 0; i < _items.Length; i++)
                {
                    if (MenuStateCode.IsTransition(_items[i].Code)) done = false;
                }
                if (_exitFrames == 30 && !done)
                {
                    var waiting = new System.Text.StringBuilder();
                    for (int i = 0; i < _items.Length; i++)
                    {
                        ItemRuntime r = _items[i];
                        if (MenuStateCode.IsTransition(r.Code))
                            waiting.Append($" [{i} {MenuStateCode.Name(r.Code)} frame {r.Frame}/{CodeLength(i, r.Code)}]");
                    }
                    Host?.OnLog($"page {Page.Index} -> {_pendingPage}: exit still waiting on{waiting}");
                }
                if (done || _exitFrames > 120)
                {
                    Enter(_pendingPage);
                }
            }
        }

        // ---- item states ----

        private MenuItemState? Visual(int item, int code) => Page!.Items[item].GetState(code);

        // the model an item shows right now ("" for text or nothing)
        public string ItemModelPath(int item)
        {
            if (Page == null || item < 0 || item >= _items.Length) return "";
            MenuItemState? visual = Visual(item, _items[item].Code);
            return visual != null && visual.WidgetIndex >= 0 ? File.Widgets[visual.WidgetIndex].ModelPath : "";
        }

        // how long a code's animation runs, in frames (widgets: the model animation; text: the colour fade)
        private int CodeLength(int item, int code)
        {
            MenuItemState? s = Visual(item, code);
            if (s == null) return 0;
            if (s.Text != null) return (int)MathF.Ceiling(s.Text.Duration);
            return s.WidgetIndex < 0 ? 0 : Widgets.FrameCount(s.WidgetIndex);
        }

        public void SetState(int item, MenuState target)
        {
            ItemRuntime rt = _items[item];
            if (!rt.Started)
            {
                rt.Started = true;
                rt.Delay = 0;
            }
            MenuState from = rt.State;
            if (from == target && !MenuStateCode.IsTransition(rt.Code)) return;
            if (target == MenuState.Focused && _focus != item)
            {
                int old = _focus;
                _focus = item;
                if (old >= 0 && old < _items.Length && _items[old].State == MenuState.Focused) SetState(old, MenuState.Idle);
                from = rt.State;
            }
            else if (from == MenuState.Focused && target != MenuState.Focused && _focus == item)
            {
                _focus = -1;
            }
            int code = MenuStateCode.Transition(from, target);
            if (Visual(item, code) == null && Visual(item, MenuStateCode.Transition(MenuState.Any, target)) != null)
            {
                code = MenuStateCode.Transition(MenuState.Any, target);
            }
            rt.State = target;
            rt.Frame = 0;
            Fire(item, MenuStateCode.Transition(from, target));
            Fire(item, MenuStateCode.Transition(MenuState.Any, target));
            Fire(item, MenuStateCode.Transition(from, MenuState.Any));
            if (Visual(item, code) != null && CodeLength(item, code) > 0)
            {
                rt.Code = code;
            }
            else
            {
                Arrive(item);
            }
        }

        private void Arrive(int item)
        {
            ItemRuntime rt = _items[item];
            int justPlayed = rt.Code; // the transition that just finished (>= 8), or an already-steady code
            rt.Code = (int)rt.State;
            rt.Frame = 0;
            MenuState arrived = rt.State;
            Fire(item, (int)arrived);
            // kind-0 actions run when their item arrives in state Field2 -- the state it arrived in, not one an earlier
            // action has already moved it on to (PRESENTS: arrive Idle -> hold as Selected; arrive Selected -> fade).
            // Field2 can also name a specific FROM->TO transition (file select's BEGIN GAME text: "on arrive
            // Idle->Hidden", the only such case in the ROM) rather than any arrival at the target steady state --
            // matched against the transition that just finished, since by this point rt.Code above already holds
            // the plain steady code and can't tell which path got here.
            foreach (MenuAction action in Page!.Items[item].Actions)
            {
                if (action.Kind == 0 && (action.Field2 == (int)arrived || action.Field2 == justPlayed))
                {
                    Run(action, item);
                }
            }
        }

        private void Fire(int item, int evt)
        {
            foreach (MenuLink link in Page!.Items[item].Links)
            {
                if (link.Event == evt && link.Target < _items.Length && link.Target != item)
                {
                    SetState(link.Target, link.State);
                }
            }
        }

        // ---- input ----

        // Run an action: its item/state change (every action names an item on this page and a state to put it in --
        // Field17, 0 = none: the back button flashes Focused, a control row's "on" marker lights), then its game
        // callbacks, then its page change.
        private void Run(MenuAction action, int item)
        {
            if (action.Field17 != 0 && action.Item < _items.Length)
            {
                // Field17 can be a transition code too (the create dialog's YES/NO B actions: 28 = Focused->Selected),
                // now that the focused item's B action runs before the page's: go to its target state
                SetState(action.Item, MenuStateCode.To(action.Field17));
            }
            foreach ((int a, int b) in action.Calls)
            {
                if (Host == null || !Host.OnCall(this, action, a, b, item))
                {
                    Host?.OnLog($"page {Page!.Index} item {item}: unhandled call ({a}, {b})");
                }
            }
            if (action.TargetPage != 0xFF && action.TargetPage < File.Pages.Count)
            {
                GoTo(action.TargetPage);
            }
        }

        // An action's touch rectangle in text coordinates (Y up from the bottom of the touch screen), moved by its
        // item's offset. Field4 bit 0: centre + half size (the main menu's big icons); otherwise two corners (the
        // control rows, the back button, a whole screen for "touch to start").
        public static bool TryRect(MenuAction a, MenuItem? it, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (a.RectW == 0 && a.RectH == 0 && a.RectX == 0 && a.RectY == 0) return false;
            float ox = it?.X ?? 0, oy = it?.Y ?? 0;
            if ((a.Field4 & 1) != 0)
            {
                x0 = a.RectX - a.RectW; x1 = a.RectX + a.RectW;
                y0 = a.RectY - a.RectH; y1 = a.RectY + a.RectH;
            }
            else
            {
                x0 = Math.Min(a.RectX, a.RectW); x1 = Math.Max(a.RectX, a.RectW);
                y0 = Math.Min(a.RectY, a.RectH); y1 = Math.Max(a.RectY, a.RectH);
            }
            x0 += ox; x1 += ox; y0 += oy; y1 += oy;
            return true;
        }

        // the focused item, if any (the one in Focused state that has an A action)
        public int FocusedItem()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (_items[i].State == MenuState.Focused && HasAction(i, MenuKeys.A)) return i;
            }
            return -1;
        }

        private bool HasAction(int item, MenuKeys key)
        {
            foreach (MenuAction a in Page!.Items[item].Actions)
            {
                if (a.Kind != 0 && ((MenuKeys)a.Kind & key) != 0) return true;
            }
            return false;
        }

        public IEnumerable<int> FocusableItems()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (_items[i].State is not (MenuState.Hidden or MenuState.State5) && Page!.Items[i].GetState((int)MenuState.Focused) != null
                    && HasAction(i, MenuKeys.A))
                {
                    yield return i;
                }
            }
        }

        public void Focus(int item) => Focus(item, byTouch: false);

        // Focus moves make TOUCH_NEUTRAL unless the item's sound flags turn it off: for a touch, flag 1 (ov0 USA rev 0
        // 0x21026a0, from the touch handler 0x2140240); for the D-pad (or A with nothing focused), flag 4 -- and never
        // for item 0, the game's own check (0x213f7a4 -> 0x2102758).
        private void Focus(int item, bool byTouch)
        {
            int current = FocusedItem();
            if (current == item) return;
            if (current != -1) SetState(current, MenuState.Idle);
            SetState(item, MenuState.Focused);
            Host?.OnFocusChanged(this, item);
            int index = Page!.Items[item].Index;
            MenuSoundFlags flags = Sounds.Item(Page.Index, index).Flags;
            bool quiet = byTouch ? (flags & MenuSoundFlags.NoTouchFocusSound) != 0
                : (flags & MenuSoundFlags.NoNavFocusSound) != 0 || index == 0;
            if (!quiet) Sound(MenuSounds.Neutral, $"focus item {item}{(byTouch ? " (touch)" : "")}");
        }

        // ---- sounds (the game's menu sound rules: ov0 USA rev 0 0x21024a0..0x2102920, called from the engine at
        // 0x213f44c / 0x2140240 / 0x2140500 / 0x213f7a4; USA rev 1 has the same code at +0xAC0..+0xB10) ----

        // the data's direction bits (Up/Down/Left/Right, MenuKeys) -- the game tests Kind & 0xF00
        private const ushort DirectionBits = 0xF00;

        // 0 is "no sound" (the game's sound dispatcher, 0x21028d8, returns before playing it); true if one was played
        private bool Sound(int sfxId, string why)
        {
            if (sfxId == 0) return false;
            Host?.OnSound(this, sfxId, why);
            return true;
        }

        // An action that runs on an item makes that item's own sound (0x213f44c -> 0x210280c), unless the item's flag 2
        // keeps it for actions that change page and this one doesn't. Page and timer actions have no item sound.
        private bool ActionSound(MenuAction action, int item)
        {
            if (item < 0) return false;
            (int sound, MenuSoundFlags flags) = Sounds.Item(Page!.Index, Page.Items[item].Index);
            if ((flags & MenuSoundFlags.OnlyWithPageChange) != 0 && action.TargetPage == 0xFF) return false;
            return Sound(sound, $"item {item}");
        }

        // A key action that made no sound of its own (0x2140500): a direction -> TOUCH_NEUTRAL; A on a page action ->
        // the page's sound (the title's TOUCH_TO_START_SCR); B -> TOUCH_BACK_SCR. Touches get no fallback.
        private void FallbackSound(MenuAction action, bool pageAction)
        {
            // The logo pages' "any key skips" (Kind 4095): the game does ask for TOUCH_NEUTRAL here (BizHawk, USA rev 1:
            // PlaySfx(4) on the press), but the intro movie starts two frames later and cuts it -- the ARM7 oracle
            // (2026-09-30) saw no channel start. Left silent.
            if (action.Kind == (ushort)MenuKeys.Any) return;
            if ((action.Kind & DirectionBits) != 0) Sound(MenuSounds.Neutral, "direction");
            else if (pageAction && (action.Kind & (ushort)MenuKeys.A) != 0) Sound(Sounds.PageSound(Page!.Index), "page A");
            else if ((action.Kind & (ushort)MenuKeys.B) != 0) Sound(MenuSounds.Back, "back");
        }

        // An action whose Flags bit 1 is set only runs once its item has settled in Focused (0x213f44c): a first tap on
        // an existing save file focuses it, the second starts it.
        private bool Ready(int item, MenuAction action)
            => (action.Flags & 2) == 0 || (_items[item].State == MenuState.Focused && !MenuStateCode.IsTransition(_items[item].Code));

        // A button press, in the game's order (0x2140500): the focused item's actions for these keys first; the page's
        // own actions only if none of the item's matched (or the one that did has Flags bit 0); with no action at all,
        // A focuses the first control when nothing is focused (the D-pad does the same through Navigate).
        public void Press(MenuKeys keys)
        {
            if (Page == null || _pendingPage != -1) return;
            bool matched = false, alsoPage = false;
            int focused = FocusedItem();
            if (focused != -1)
            {
                foreach (MenuAction action in Page.Items[focused].Actions)
                {
                    if (action.Kind == 0 || ((MenuKeys)action.Kind & keys) == 0) continue;
                    matched = true;
                    alsoPage = (action.Flags & 1) != 0;
                    Host?.OnAction(this, action, focused);
                    if (!Ready(focused, action))
                    {
                        FallbackSound(action, pageAction: false);
                        break;
                    }
                    if (!ActionSound(action, focused)) FallbackSound(action, pageAction: false);
                    Run(action, focused);
                    break;
                }
            }
            if (!matched || (alsoPage && _pendingPage == -1))
            {
                foreach (MenuAction action in Page.Actions)
                {
                    if (((MenuKeys)action.Kind & keys) != 0)
                    {
                        matched = true;
                        Host?.OnAction(this, action, -1);
                        FallbackSound(action, pageAction: true);
                        Run(action, -1);
                        break;
                    }
                }
            }
            if (!matched && focused == -1 && (keys & MenuKeys.A) != 0) FocusFirst();
        }

        // Does a press of `keys` do anything here (a page action, or the focused item's)?
        public bool HasKeyAction(MenuKeys keys)
        {
            if (Page == null) return false;
            foreach (MenuAction action in Page.Actions)
            {
                if (action.Kind != 0 && ((MenuKeys)action.Kind & keys) != 0 && (MenuKeys)action.Kind != MenuKeys.Any) return true;
            }
            int focused = FocusedItem();
            return focused != -1 && HasAction(focused, keys);
        }

        // A D-pad direction: the page's own action for that key if the data has one (paging, value arrows), else
        // move focus. dy = +1 is up.
        public void Direction(int dx, int dy)
        {
            MenuKeys key = dx > 0 ? MenuKeys.Right : dx < 0 ? MenuKeys.Left : dy > 0 ? MenuKeys.Up : MenuKeys.Down;
            if (HasKeyAction(key)) Press(key);
            else Navigate(dx, dy);
        }

        // D-pad / stick: move focus to the nearest focusable item in that direction (a recomp addition -- the DS
        // menus are touch-first; the data has no neighbour table).
        public void Navigate(int dx, int dy)
        {
            int current = FocusedItem();
            if (current == -1)
            {
                FocusFirst();
                return;
            }
            (float cx, float cy) = ActionCentre(current);
            int best = -1;
            float bestScore = float.MaxValue;
            foreach (int i in FocusableItems())
            {
                if (i == current) continue;
                (float x, float y) = ActionCentre(i);
                float ddx = x - cx, ddy = y - cy; // text space: +y is up
                float along = dx * ddx + dy * ddy; // dy = +1 means up
                if (along <= 0) continue;
                float across = MathF.Abs(dx != 0 ? ddy : ddx);
                float score = along + across * 2;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best != -1) Focus(best);
        }

        // nothing focused yet: start at the top-left control (the game takes the first focusable, visible item in
        // list order, 0x213f6d4 -- not changed here)
        private void FocusFirst()
        {
            int first = -1;
            float firstScore = float.MaxValue;
            foreach (int i in FocusableItems())
            {
                (float x, float y) = ActionCentre(i);
                float score = x - y * 2;
                if (score < firstScore)
                {
                    firstScore = score;
                    first = i;
                }
            }
            if (first != -1) Focus(first);
        }

        // The focused item's touch rectangle in MENU space (Y up, seam at 0), and whether the game already draws a
        // highlight for its focus (the main menu's rings, the option arrows) -- if not, the host draws a focus frame so
        // a D-pad player can see where they are (the DS menus are touch-first: many items have no focused look).
        public bool TryFocusFrame(out float x0, out float y0, out float x1, out float y1, out bool hasHighlight)
        {
            x0 = y0 = x1 = y1 = 0;
            hasHighlight = false;
            int focused = FocusedItem();
            if (focused == -1) return false;
            MenuItem it = Page!.Items[focused];
            MenuItemState? look = it.GetState((int)MenuState.Focused);
            if (look != null && look.WidgetIndex >= 0)
            {
                string path = File.Widgets[look.WidgetIndex].ModelPath;
                hasHighlight = path.Contains("highlight", StringComparison.OrdinalIgnoreCase);
            }
            foreach (MenuAction a in it.Actions)
            {
                if (TryRect(a, it, out x0, out y0, out x1, out y1))
                {
                    y0 -= 192;
                    y1 -= 192;
                    return true;
                }
            }
            return false;
        }

        private (float X, float Y) ActionCentre(int item)
        {
            MenuItem it = Page!.Items[item];
            foreach (MenuAction a in it.Actions)
            {
                if (TryRect(a, it, out float x0, out float y0, out float x1, out float y1))
                {
                    return ((x0 + x1) / 2, (y0 + y1) / 2);
                }
            }
            return (it.X, it.Y);
        }

        // A touch at DS text coordinates (x right, y up from the bottom of the touch screen): the topmost visible item
        // whose action rectangle holds it (any kind of action -- kind 0 with a rectangle is a touch-only button), else
        // a page-wide rectangle (touch to start). As in the game (0x2140240, checked in BizHawk on USA rev 1): a
        // focusable item is focused (TOUCH_NEUTRAL unless its flag 1) and its action runs on the same tap -- one tap on
        // OPTIONS or MULTIPLAYER opens it -- except an action with Flags bit 1, which waits for a second tap (save
        // files). The action makes its item's sound; a touch has no fallback sound, and page-level touch actions none.
        public void Touch(float x, float y)
        {
            if (Page == null || _pendingPage != -1) return;
            for (int i = _items.Length - 1; i >= 0; i--)
            {
                // hidden, and disabled (State5, 0x2140d50 skips state 5 and moves to/from it), items take no touch
                if (_items[i].State is MenuState.Hidden or MenuState.State5) continue;
                MenuItem it = Page.Items[i];
                foreach (MenuAction a in it.Actions)
                {
                    if (!TryRect(a, it, out float x0, out float y0, out float x1, out float y1)) continue;
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                    {
                        bool focusable = it.GetState((int)MenuState.Focused) != null && HasAction(i, MenuKeys.A);
                        // settled BEFORE this touch focuses it (EngTouch focuses, then EngAction tests the item's state:
                        // a file the touch just focused is mid-transition, so the first tap only selects it -- BizHawk
                        // USA rev 1: tap 1 = TOUCH_NEUTRAL + focus, tap 2 = BEGIN GAME)
                        bool ready = Ready(i, a);
                        if (focusable) Focus(i, byTouch: true);
                        Host?.OnAction(this, a, i);
                        if (!ready)
                        {
                            // an item that can never be focused can't run it at all: TOUCH_NEGATIVE
                            if (!focusable) Sound(MenuSounds.Negative, $"item {i} not focusable");
                            return;
                        }
                        ActionSound(a, i);
                        Run(a, i);
                        return;
                    }
                }
            }
            foreach (MenuAction a in Page.Actions)
            {
                if (TryRect(a, null, out float x0, out float y0, out float x1, out float y1)
                    && x >= x0 && x <= x1 && y >= y0 && y <= y1)
                {
                    Host?.OnAction(this, a, -1);
                    Run(a, -1);
                    return;
                }
            }
        }

        // ---- drawing ----

        // The page's current look as menu-space triangles, far to near.
        public void Collect(List<WidgetTri> output)
        {
            if (Page == null) return;
            int start = output.Count;
            for (int i = 0; i < _items.Length; i++)
            {
                ItemRuntime rt = _items[i];
                if (!rt.Started) continue;
                MenuItem item = Page.Items[i];
                MenuItemState? visual = Visual(i, rt.Code);
                if (visual == null) continue;
                bool steady = !MenuStateCode.IsTransition(rt.Code);
                int first = output.Count;
                if (visual.Text != null)
                {
                    EmitText(item, visual.Text, rt.Frame, steady, output);
                }
                else if (visual.WidgetIndex >= 0)
                {
                    int before = output.Count;
                    Widgets.Evaluate(visual.WidgetIndex, rt.Frame, loop: steady, alpha: 1, output);
                    if (item.StretchX != 0 || item.StretchDown != 0) Stretch(output, before, item.StretchX, item.StretchDown);
                    if (item.X != 0 || item.Y != 0)
                    {
                        for (int t = before; t < output.Count; t++)
                        {
                            WidgetTri tri = output[t];
                            tri.A.X += item.X; tri.B.X += item.X; tri.C.X += item.X;
                            tri.A.Y += item.Y; tri.B.Y += item.Y; tri.C.Y += item.Y;
                            output[t] = tri;
                        }
                    }
                }
                for (int t = first; t < output.Count; t++)
                {
                    WidgetTri tri = output[t];
                    tri.Item = i;
                    output[t] = tri;
                }
            }
            // far to near; ties keep item order (DS draws in list order)
            var slice = output.GetRange(start, output.Count - start);
            var order = new int[slice.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (p, q) => slice[p].Z != slice[q].Z ? slice[p].Z.CompareTo(slice[q].Z) : p.CompareTo(q));
            for (int i = 0; i < order.Length; i++) output[start + i] = slice[order[i]];
        }

        // An item's resize (MenuItem.StretchX / StretchDown, set by RecompMenus): its widget's vertices move away from
        // the centre of the widget as drawn this frame, so end caps and borders keep their size and the middle grows.
        private static void Stretch(List<WidgetTri> tris, int start, float stretchX, float stretchDown)
        {
            if (start >= tris.Count) return;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            void Bounds(UiVertex v)
            {
                minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
                minY = MathF.Min(minY, v.Y); maxY = MathF.Max(maxY, v.Y);
            }
            for (int t = start; t < tris.Count; t++)
            {
                Bounds(tris[t].A); Bounds(tris[t].B); Bounds(tris[t].C);
            }
            float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            const float Eps = 0.01f;
            UiVertex Move(UiVertex v)
            {
                if (v.X < cx - Eps) v.X -= stretchX / 2;
                else if (v.X > cx + Eps) v.X += stretchX / 2;
                if (v.Y < cy - Eps) v.Y -= stretchDown; // menu space is Y up: below the centre = smaller Y
                return v;
            }
            for (int t = start; t < tris.Count; t++)
            {
                WidgetTri tri = tris[t];
                tri.A = Move(tri.A); tri.B = Move(tri.B); tri.C = Move(tri.C);
                tris[t] = tri;
            }
        }

        // (?) text sits above the widgets
        private const float TextZ = 1000;

        private void EmitText(MenuItem item, MenuTextStyle style, int frame, bool steady, List<WidgetTri> output)
        {
            float t = 0; // no duration: the start colour (checked against the game: an unfocused label keeps its start colour)
            if (style.Duration > 0)
            {
                float f = frame / style.Duration;
                // (?) a steady state with a duration pulses back and forth; a transition fades once
                t = steady ? 1 - MathF.Abs(f % 2 - 1) : MathF.Min(1, f);
            }
            var (sr, sg, sb, sa) = MenuTextStyle.Channels(style.StartColor);
            var (er, eg, eb, ea) = MenuTextStyle.Channels(style.EndColor);
            float Lerp(int s, int e) => (s + (e - s) * t) / 31f;
            float a = Lerp(sa, ea);
            if (a <= 0) return;
            string text = Strings[style.StringId];
            Font.Emit(text, item.X, item.Y, style.Align, style.WrapWidth, lineHeight: style.Size == 0 ? 8 : style.Size,
                Lerp(sr, er), Lerp(sg, eg), Lerp(sb, eb), a, TextZ + item.Depth, output);
        }
    }
}
