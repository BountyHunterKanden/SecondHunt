using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Multiplayer;

// The character + skin picker (D9, docs/CHARACTERS_AND_SKINS.md; owner 2026-10-08: SELECT HUNTER option 2 + lobby option
// 2, mockup extract_out/characters_mockups/picker/PICK_select_hunter_flow.png, picks A2 smooth HD icons + B1 greyed "?").
//
// SELECT HUNTER (35, and the joiner's 36): nine icons in vanilla's row -- the seven hunters, then TROOPER and DARK SAMUS
// (Characters.All) -- the row closed up to fit (vanilla's pieces moved, the icons drawn a little smaller). The new ones are
// copies of Weavel's pieces (icon, dark icon, big picture, name and ALT-FORM texts, highlight with its focus links) with
// their own calls; their icons are drawn as the ROM's round rim around a picture of the skin's head (A2), their big
// picture is the skin's whole body. Vanilla's info box is untouched. SELECTING a character with more than one skin on
// this device (Samus with imported suits, the Trooper, Dark Samus with both games) opens the skin chooser over the info
// box: the skins' round icons, the focused one's name, "tap outside to close"; the big picture shows the focused skin. A
// character with no skin on this device keeps its icon greyed with a "?" (B1) and can't be picked.
//
// The lobby (38; the joiner's 39 shows the host's seats): the hunter arrows step all nine; a seat in a skin shows that
// skin's whole body in place of the ROM's picture, with a round badge of its skin on the picture's corner when its
// character has more than one; a tap on the badge opens the same chooser for that seat (this player's and the bots'). A
// TROOPER or DARK SAMUS bot is named after its character (D7).
//
// The pick sets the seat's MatchPlayer look: Hunter = the character's base, Skin (null for ORIGINAL), Character
// (Characters.IdFor); a joiner sends it with its JOIN. The last pick is remembered like vanilla's hunter (Setup.MySkin).
namespace MphRecomp.Frontend
{
    public sealed partial class MultiplayerMenu
    {
        // the row: slot k's icon centre, DS touch px (vanilla's: 26 + 34k)
        private const int PickerSlots = 9, TrooperSlot = 7, DarkSamusSlot = 8, SyluxSlot = 5; // (SyluxSlot: HunterOrder's Sylux)
        private const float SlotStep = 28.4f, SlotMid = 128, IconRowY = 134, VanillaFirstX = 26, VanillaStep = 34;
        private const float IconScale = 0.93f;
        // calls of our own (the ROM's are 0-461, the recomp's 950-2241): select slot k, and its focus companion
        private const int PickCall = 2300, PickFocusCall = 2310;

        private static float SlotX(int slot) => SlotMid + (slot - 4) * SlotStep;

        // where a test taps slot k's icon / the big picture (DS touch px, y down)
        public static (float X, float Y) HunterIconTap(int slot) => (SlotX(slot), IconRowY);
        public static (float X, float Y) HunterPortraitTap => (70, 77);

        // tests: the open chooser's skins with each one's tap (DS touch px, y down), and a lobby seat's badge (R 0 = none)
        public (string Title, List<string> Skins, int Focus, (float X, float Y)[] Taps)? ChooserProbe =>
            _chooser == null ? null : (_chooser.Title, _chooser.Skins, _chooser.Focus, _chooser.Centres.Select(c => (c.X, -c.Y)).ToArray());
        public (float X, float Y, float R) BadgeTap(int seat) => (_badges[seat].X, -_badges[seat].Y, _badges[seat].R);

        // tools: the HD models and the pictures' folder (else <profile>/hd and <profile>/cache/characters)
        public static string? CharacterHdRoot { get; set; }
        public static string? CharacterPicturesDir { get; set; }

        private string? PickerHdRoot => CharacterHdRoot ?? (_profileDir == null ? null : Path.Combine(_profileDir, "hd"));
        private string? PickerPicturesDir => CharacterPicturesDir ?? (_profileDir == null ? null : CharacterPictures.Dir(_profileDir));

        private static Character? SlotCharacter(int slot) => slot switch
        {
            0 => Characters.Samus, SyluxSlot => Characters.Sylux, TrooperSlot => Characters.Trooper, DarkSamusSlot => Characters.DarkSamus, _ => null
        };

        private static Hunter SlotHunter(int slot) => slot < HunterOrder.Length ? HunterOrder[slot] : SlotCharacter(slot)!.Base;

        // a seat's slot: its character's, else its hunter's
        private static int SlotOf(Hunter hunter, string? skin)
        {
            Character? c = Characters.Of(hunter, skin);
            if (c == Characters.Trooper) return TrooperSlot;
            if (c == Characters.DarkSamus) return DarkSamusSlot;
            return Math.Max(0, Array.IndexOf(HunterOrder, hunter));
        }

        // ---- the skins this device has (looked at on each page entry) ----

        private readonly Dictionary<string, List<string>> _skinLists = new();

        private void RefreshSkins()
        {
            _skinLists.Clear();
            string? hd = PickerHdRoot;
            foreach (Character c in Characters.All)
            {
                // no device to look at (a tool without a profile): ORIGINAL only, as before the picker
                List<string> list = hd == null ? (c.HasOriginal ? new List<string> { Characters.Original } : new List<string>())
                    : Characters.Skins(c, hd);
                _skinLists[c.Id] = list;
            }
        }

        private List<string> SkinsOf(Character c)
        {
            if (!_skinLists.ContainsKey(c.Id)) RefreshSkins();
            return _skinLists[c.Id];
        }

        // (an extra character also needs the ADVANCED box's EXTRA CHARACTERS: MultiplayerMenu.ExtraRules.cs)
        private bool SlotOpen(int slot) => slot < HunterOrder.Length ? HunterOpen(HunterOrder[slot])
            : (SlotCharacter(slot)!.HasOriginal || ShownCharacters) && SkinsOf(SlotCharacter(slot)!).Count > 0;

        // the skin a slot shows before (or without) a choice: this player's last one for that character, else its first
        private string? DisplaySkin(int slot)
        {
            Character? c = SlotCharacter(slot);
            if (c == null) return null;
            List<string> skins = SkinsOf(c);
            if (skins.Count == 0) return null;
            if (Characters.Of(_setup.MyHunter, _setup.MySkin) == c && _setup.MySkin != null
                && skins.Contains(_setup.MySkin, StringComparer.OrdinalIgnoreCase))
            {
                return _setup.MySkin;
            }
            return skins[0];
        }

        // ---- binding: SELECT HUNTER's pieces, moved and copied (indexes per page; kept per menu file) ----

        private sealed class PickerItems
        {
            public readonly int[] Icon = Filled(), Off = Filled(), Highlight = Filled(), Portrait = Filled(), Name = Filled(), Alt = Filled();
            public int Layer = -1; // the chooser's triangles' item (an empty one of ours)
            private static int[] Filled() => Enumerable.Repeat(-1, PickerSlots).ToArray();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, Dictionary<int, PickerItems>> s_pickerBound = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, Dictionary<int, int[]>> s_lobbyBound = new();
        private Dictionary<int, PickerItems> _picker = new();
        private Dictionary<int, int[]> _lobbyLayers = new(); // lobby page -> one item per seat's badge (8: P8), then the chooser's

        private static int AddTo(MenuPage page, Func<int, MenuItem> make)
        {
            int at = page.Items.Count;
            page.AddItem(make);
            return at;
        }

        private void BindPicker(MenuFile file, MenuStrings strings)
        {
            if (s_pickerBound.TryGetValue(file, out var bound) && s_lobbyBound.TryGetValue(file, out var lobbies))
            {
                _picker = bound;
                _lobbyLayers = lobbies;
                return;
            }
            _picker = new Dictionary<int, PickerItems>();
            _lobbyLayers = new Dictionary<int, int[]>();
            foreach (int index in new[] { HunterPage, JoinHunterPage })
            {
                if (file.Pages.Count <= index) continue;
                PickerItems? items = BindPickerPage(file, strings, file.Pages[index]);
                if (items != null) _picker[index] = items;
                else Log?.Invoke($"multiplayer: page {index}: hunter pieces not found: no TROOPER / DARK SAMUS");
            }
            foreach (int index in new[] { LobbyPage, JoinLobbyPage })
            {
                if (file.Pages.Count <= index) continue;
                MenuPage page = file.Pages[index];
                _lobbyLayers[index] = Enumerable.Range(0, 9).Select(_ => AddTo(page, n => new MenuItem { Index = n })).ToArray();
            }
            s_pickerBound.AddOrUpdate(file, _picker);
            s_lobbyBound.AddOrUpdate(file, _lobbyLayers);
        }

        private PickerItems? BindPickerPage(MenuFile file, MenuStrings strings, MenuPage page)
        {
            var pi = new PickerItems();
            for (int k = 0; k < HunterOrder.Length; k++)
            {
                string name = HunterOrder[k].ToString().ToLowerInvariant();
                int slot = k;
                pi.Icon[k] = FindItem(page, it => HasWidget(file, it, name));
                pi.Off[k] = k == 0 ? -1 : FindItem(page, it => HasWidget(file, it, name + "off"));
                pi.Highlight[k] = FindItem(page, it => Calls(it).Contains(132 + slot) && Calls(it).Contains(139 + slot));
                pi.Portrait[k] = FindItem(page, it => HasWidget(file, it, "big_" + name));
                if (pi.Icon[k] < 0 || pi.Highlight[k] < 0 || pi.Portrait[k] < 0 || k > 0 && pi.Off[k] < 0) return null;
                // the name and ALT-FORM texts its focus shows (the name above)
                int[] texts = page.Items[pi.Highlight[k]].Links.Where(l => l.Event == MenuStateCode.Transition(MenuState.Any, MenuState.Focused)
                    && l.Target >= 0 && l.Target < page.Items.Count && page.Items[l.Target].IsText)
                    .Select(l => l.Target).Distinct().OrderByDescending(i => page.Items[i].Y).ToArray();
                if (texts.Length != 2) return null;
                pi.Name[k] = texts[0];
                pi.Alt[k] = texts[1];
            }
            // vanilla's row closed up to nine
            for (int k = 0; k < HunterOrder.Length; k++)
            {
                float dx = SlotX(k) - (VanillaFirstX + VanillaStep * k);
                foreach (int item in new[] { pi.Icon[k], pi.Off[k], pi.Highlight[k] })
                {
                    if (item < 0) continue;
                    MenuItem it = page.Items[item];
                    page.ReplaceItem(item, n => RecompMenus.Clone(it, n, dx, 0, links: it.Links, actions: it.Actions));
                }
            }
            // TROOPER and DARK SAMUS: Weavel's pieces copied
            const int w = 6;
            MenuItem srcIcon = page.Items[pi.Icon[w]], srcOff = page.Items[pi.Off[w]], srcHi = page.Items[pi.Highlight[w]];
            MenuItem srcBig = page.Items[pi.Portrait[w]], srcName = page.Items[pi.Name[w]], srcAlt = page.Items[pi.Alt[w]];
            (int Slot, string Name, string Alt)[] added =
            {
                (TrooperSlot, Characters.Trooper.Name, "alt-form:\nSCOUT DRONE"),
                (DarkSamusSlot, Characters.DarkSamus.Name, "alt-form:\nMORPH BALL"),
            };
            foreach ((int slot, string nameText, string altText) in added)
            {
                float dx = SlotX(slot) - SlotX(w); // (Weavel's pieces already moved to slot 6)
                int s = slot;
                pi.Portrait[s] = AddTo(page, n => SoundsLike(RecompMenus.Clone(srcBig, n, 0, 0, links: Array.Empty<MenuLink>(),
                    actions: srcBig.Actions.Select(a => CopyAction(a, calls: Recall(a.Calls, s), item: n)).ToList()), srcBig.Index));
                pi.Icon[s] = AddTo(page, n => SoundsLike(RecompMenus.Clone(srcIcon, n, dx, 0), srcIcon.Index));
                pi.Off[s] = AddTo(page, n => SoundsLike(RecompMenus.Clone(srcOff, n, dx, 0), srcOff.Index));
                pi.Name[s] = AddTo(page, n => RecompMenus.Clone(srcName, n, 0, 0, stringId: strings.Add(nameText)));
                pi.Alt[s] = AddTo(page, n => RecompMenus.Clone(srcAlt, n, 0, 0, stringId: strings.Add(altText)));
                var map = new Dictionary<int, int>
                {
                    [pi.Portrait[w]] = pi.Portrait[s], [pi.Icon[w]] = pi.Icon[s], [pi.Name[w]] = pi.Name[s], [pi.Alt[w]] = pi.Alt[s],
                };
                List<MenuLink> links = srcHi.Links.Select(l => map.TryGetValue(l.Target, out int t) ? l with { Target = t } : l).ToList();
                pi.Highlight[s] = AddTo(page, n => SoundsLike(RecompMenus.Clone(srcHi, n, dx, 0, links: links,
                    actions: srcHi.Actions.Select(a => CopyAction(a, calls: Recall(a.Calls, s), item: n)).ToList()), srcHi.Index));
            }
            pi.Layer = AddTo(page, n => new MenuItem { Index = n });
            return pi;
        }

        // Weavel's calls (138 select, 145 focus) as slot s's
        private static IReadOnlyList<(int A, int B)> Recall(IReadOnlyList<(int A, int B)> calls, int s) =>
            calls.Select(c => c.B == 132 + 6 ? (c.A, PickCall + s) : c.B == 139 + 6 ? (c.A, PickFocusCall + s) : c).ToList();

        // ---- page entry ----

        private void EnterPicker(MenuEngine menu, int page)
        {
            _chooser = null;
            if (page is not (HunterPage or JoinHunterPage or LobbyPage or JoinLobbyPage)) return;
            RefreshSkins();
            // the pictures this page can show, drawn now if they aren't yet (the new characters first)
            foreach (Character c in new[] { Characters.Trooper, Characters.DarkSamus, Characters.Samus, Characters.Sylux })
            {
                foreach (string skin in SkinsOf(c)) CharacterPictures.For(PickerHdRoot, PickerPicturesDir, skin);
            }
            if (!_picker.TryGetValue(page, out PickerItems? pi)) return;
            foreach (int slot in new[] { TrooperSlot, DarkSamusSlot })
            {
                bool open = SlotOpen(slot);
                ShowAtEntry(menu, pi.Icon[slot], open);
                ShowAtEntry(menu, pi.Highlight[slot], open);
                ShowAtEntry(menu, pi.Off[slot], !open);
            }
        }

        // ---- the pictures, drawn ahead (at the menus' start and after each import) ----

        private int _picturesWarmed = -1;

        private void WarmPictures(MenuEngine menu)
        {
            if (!CharacterPictures.HostRenders || menu.Page == null || _picturesWarmed == CharacterPictures.Generation) return;
            _picturesWarmed = CharacterPictures.Generation;
            if (PickerHdRoot == null || PickerPicturesDir == null) return;
            RefreshSkins();
            int queued = 0;
            foreach (Character c in new[] { Characters.Trooper, Characters.DarkSamus, Characters.Samus, Characters.Sylux })
            {
                foreach (string skin in SkinsOf(c))
                {
                    if (CharacterPictures.For(PickerHdRoot, PickerPicturesDir, skin) == null && CharacterPictures.Pending(skin)) queued++;
                }
            }
            if (queued > 0) Log?.Invoke($"multiplayer: {queued} character picture(s) queued ahead of SELECT HUNTER");
        }

        // ---- calls (SELECT HUNTER) ----

        private bool OnPickerCall(MenuEngine menu, int b)
        {
            if (!_picker.ContainsKey(menu.Page!.Index)) return false;
            if (b is >= PickFocusCall and < PickFocusCall + PickerSlots) return true; // a tap on an icon: the data shows its picture
            int slot = b is >= 132 and <= 138 ? b - 132 : b is >= PickCall and < PickCall + PickerSlots ? b - PickCall : -1;
            if (slot < 0) return false;
            if (!SlotOpen(slot)) return true; // (its icon is hidden; a focus that still lands there)
            Character? c = SlotCharacter(slot);
            List<string> skins = c == null ? new List<string>() : SkinsOf(c);
            if (skins.Count > 1)
            {
                OpenChooser(menu, seat: 0, c!, slot);
                return true;
            }
            Pick(menu, SlotHunter(slot), skins.Count == 1 ? skins[0] : null);
            return true;
        }

        // this player's pick: remembered, then the lobby (the host) or the host asked for a seat (a joiner)
        private void Pick(MenuEngine menu, Hunter hunter, string? skin)
        {
            string? s = Characters.IsOriginal(skin) ? null : skin;
            string? character = Characters.IdFor(hunter, s);
            _setup.MyHunter = hunter;
            _setup.MySkin = s;
            _setup.MyCharacter = character;
            Log?.Invoke($"multiplayer: picked {hunter}{(s != null ? " in " + s : "")}{(character != null ? " (" + character + ")" : "")}");
            if (menu.Page?.Index == JoinHunterPage)
            {
                JoinAs(menu, hunter, character, s);
                return;
            }
            Seat seat = _setup.Seats[0];
            seat.Hunter = hunter;
            seat.Skin = s;
            seat.Character = character;
            OnlineHunterPicked = true;
            menu.GoTo(AfterHunter); // (the lobby; ONLINE PLAY's CREATE goes on to its own page)
        }

        // ---- the lobby: the arrows over all nine, and the characters' bot names (D7) ----

        private void StepSlot(int seat, int step)
        {
            Seat s = _setup.Seats[seat];
            if (!s.Used) return;
            int i = SlotOf(s.Hunter, s.Skin);
            for (int n = 0; n < PickerSlots; n++)
            {
                i = Wrap(i, step, PickerSlots);
                if (SlotOpen(i)) break;
            }
            Character? c = SlotCharacter(i);
            Hunter h = SlotHunter(i);
            string? skin = null;
            if (c != null && !c.HasOriginal)
            {
                List<string> skins = SkinsOf(c);
                skin = skins.Contains(s.Skin ?? "", StringComparer.OrdinalIgnoreCase) ? s.Skin : skins.FirstOrDefault();
            }
            else if (c != null && Characters.Of(s.Hunter, s.Skin) == c)
            {
                skin = s.Skin; // (Samus keeps her suit)
            }
            s.Hunter = h;
            s.Skin = skin;
            s.Character = Characters.IdFor(h, skin);
        }

        // a bot's name in the PLAYERS list and the lobby: its character's for TROOPER / DARK SAMUS (lower case: the font's
        // small capitals, as "sambot"), else the ROM's
        private string BotWord(Seat seat) => CharacterOf(seat) is { HasOriginal: false } c ? c.Name.ToLowerInvariant() : Word(BotNames[seat.Hunter]);

        // ... and in the match (its HUD text)
        private static string BotNickname(Seat seat) => CharacterOf(seat) is { HasOriginal: false } c ? c.Name : BotNames[seat.Hunter].ToUpperInvariant();

        private static Character? CharacterOf(Seat seat) => Characters.Of(seat.Hunter, seat.Skin);

        // ---- the skin chooser ----

        private sealed class Chooser
        {
            public int Page, Seat, Slot;
            public Character Character = Characters.Samus;
            public List<string> Skins = new();
            public int Focus;
            public string Title = "";
            // its box (DS touch px, y down) and icon size; each skin's centre (menu space)
            public float X0, Y0, X1, Y1, Diameter, Gap;
            public int PerRow;
            public bool Hint = true; // "tap outside to close" (left out when the rows need its room)
            public (float X, float Y)[] Centres = Array.Empty<(float, float)>();
            public int Tick;
            // RECOMP SETTINGS "menu art" (OpenArtChooser): the pick goes here instead of a seat, drawn on this item
            public Action<string>? OnPick;
            public int Layer = -1;
        }

        private Chooser? _chooser;

        private void OpenChooser(MenuEngine menu, int seat, Character c, int slot)
        {
            List<string> skins = SkinsOf(c);
            Seat st = _setup.Seats[seat];
            string? current = seat == 0 && menu.Page!.Index is HunterPage or JoinHunterPage
                ? (Characters.Of(_setup.MyHunter, _setup.MySkin) == c ? _setup.MySkin ?? Characters.Original : null)
                : Characters.Of(st.Hunter, st.Skin) == c ? st.Skin ?? Characters.Original : null;
            int focus = current == null ? 0 : Math.Max(0, skins.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase)));
            bool lobby = menu.Page!.Index is LobbyPage or JoinLobbyPage;
            string who = lobby ? (st.Bot ? BotWord(st) : seat == 0 ? _setup.Nickname.ToLowerInvariant() : (st.Name ?? "").ToLowerInvariant())
                : c.Name.ToLowerInvariant();
            var ch = new Chooser
            {
                Page = menu.Page.Index, Seat = seat, Slot = slot, Character = c, Skins = skins, Focus = focus,
                Title = who + "  skin",
            };
            // the mockup's boxes: over SELECT HUNTER's info box; across the lobby's top
            (ch.X0, ch.Y0, ch.X1) = lobby ? (41f, 28f, 217f) : (118f, 47f, 232f);
            (ch.Diameter, ch.Gap) = skins.Count <= 4 ? (21f, 24.8f) : (14.6f, 17.3f);
            const float title = 10, top = 4, bottom = 22, labelOnly = 13;
            // SELECT HUNTER's box stays clear of the icon row: a third row (many suits) drops the hint and shrinks the icons
            float maxY1 = lobby ? 160 : 118;
            int rows;
            while (true)
            {
                ch.PerRow = Math.Max(1, (int)((ch.X1 - ch.X0 - 8) / ch.Gap));
                rows = (skins.Count + ch.PerRow - 1) / ch.PerRow;
                ch.Hint = ch.Y0 + title + top + rows * ch.Gap + bottom <= maxY1;
                if (ch.Hint || ch.Y0 + title + top + rows * ch.Gap + labelOnly <= maxY1 || ch.Gap <= 10) break;
                ch.Gap -= 0.25f;
                ch.Diameter = ch.Gap * (14.6f / 17.3f);
            }
            ch.Y1 = ch.Y0 + title + top + rows * ch.Gap + (ch.Hint ? bottom : labelOnly);
            ch.Centres = new (float, float)[skins.Count];
            for (int i = 0; i < skins.Count; i++)
            {
                int row = i / ch.PerRow, inRow = Math.Min(ch.PerRow, skins.Count - row * ch.PerRow), col = i % ch.PerRow;
                float x = (ch.X0 + ch.X1) / 2 + (col - (inRow - 1) / 2f) * ch.Gap;
                float y = ch.Y0 + title + top + (row + 0.5f) * ch.Gap;
                ch.Centres[i] = (x, -y);
            }
            _chooser = ch;
            Log?.Invoke($"multiplayer: skin chooser for seat {seat}: {c.Name} ({skins.Count} skins, on {skins[focus]})");
        }

        private void CloseChooser(MenuEngine menu, bool sound)
        {
            _chooser = null;
            if (sound) Sound(menu, MenuSounds.Back, "skin chooser closed");
        }

        private void ChooserPick(MenuEngine menu)
        {
            Chooser ch = _chooser!;
            string skin = ch.Skins[ch.Focus];
            _chooser = null;
            if (ch.OnPick != null)
            {
                Sound(menu, (int)SfxId.CREATE_CONFIRM_SCR, "menu art picked");
                ch.OnPick(skin);
                return;
            }
            if (ch.Page is HunterPage or JoinHunterPage)
            {
                Sound(menu, SelectSound(menu, ch.Slot), "skin picked");
                Pick(menu, ch.Character.Base, skin);
                return;
            }
            Seat seat = _setup.Seats[ch.Seat];
            if (!seat.Used) return;
            string? s = Characters.IsOriginal(skin) ? null : skin;
            seat.Skin = s;
            seat.Character = Characters.IdFor(seat.Hunter, s);
            Sound(menu, (int)SfxId.CREATE_CONFIRM_SCR, "skin picked");
            Log?.Invoke($"multiplayer: seat {ch.Seat} in {skin}");
            if (menu.Page?.Index == LobbyPage) Lobby(menu);
        }

        // the sound the slot's highlight makes on A (vanilla's hunter pick)
        private int SelectSound(MenuEngine menu, int slot)
        {
            if (!_picker.TryGetValue(menu.Page!.Index, out PickerItems? pi) || pi.Highlight[slot] < 0) return MenuSounds.Neutral;
            int sound = menu.Sounds.Item(menu.Page.Index, menu.Page.Items[pi.Highlight[slot]].Index).Sound;
            return sound != 0 ? sound : MenuSounds.Neutral;
        }

        private static void Sound(MenuEngine menu, int id, string why) => menu.Host?.OnSound(menu, id, why);

        private bool ChooserLive(MenuEngine menu)
        {
            if (_chooser == null) return false;
            if (menu.Page?.Index != _chooser.Page || menu.PendingPage != -1 || _chooser.Seat > 0 && !_setup.Seats[_chooser.Seat].Used)
            {
                _chooser = null;
                return false;
            }
            return true;
        }

        // FrontendSession's input, before the page's: true when the chooser (or a lobby badge) took it
        public bool CapturePress(MenuEngine menu, MenuKeys keys)
        {
            if (!ChooserLive(menu)) return TryPress(menu, keys); // (TRY CONTROLS' pop-up: MultiplayerMenu.TryPicker.cs)
            if ((keys & MenuKeys.A) != 0) ChooserPick(menu);
            else if ((keys & MenuKeys.B) != 0) CloseChooser(menu, sound: true);
            return true;
        }

        public bool CaptureNavigate(MenuEngine menu, int dx, int dy)
        {
            if (!ChooserLive(menu)) return TryNavigate(menu, dx, dy);
            Chooser ch = _chooser!;
            int f = ch.Focus;
            if (dx != 0) f = Wrap(f, Math.Sign(dx), ch.Skins.Count);
            else if (dy != 0)
            {
                int next = f - Math.Sign(dy) * ch.PerRow; // up = the row above
                if (next >= 0 && next < ch.Skins.Count) f = next;
            }
            if (f != ch.Focus)
            {
                ch.Focus = f;
                Sound(menu, MenuSounds.Neutral, "skin chooser focus");
            }
            return true;
        }

        // (x, y): the engine's text space (y up from the touch screen's bottom)
        public bool CaptureTouch(MenuEngine menu, float x, float y)
        {
            float my = y - 192;
            if (ChooserLive(menu))
            {
                Chooser ch = _chooser!;
                for (int i = 0; i < ch.Centres.Length; i++)
                {
                    float r = ch.Diameter / 2 + 1.5f;
                    if (MathF.Abs(x - ch.Centres[i].X) <= r && MathF.Abs(my - ch.Centres[i].Y) <= r)
                    {
                        ch.Focus = i;
                        ChooserPick(menu);
                        return true;
                    }
                }
                if (x < ch.X0 || x > ch.X1 || -my < ch.Y0 || -my > ch.Y1) CloseChooser(menu, sound: true);
                return true;
            }
            if (TryTouch(menu, x, my)) return true;
            // a lobby seat's badge: that seat's skins (this player's and the bots'; the joiner's lobby only shows them)
            if (menu.Page?.Index == LobbyPage && menu.PendingPage == -1)
            {
                for (int s = 0; s < _setup.Seats.Length; s++)
                {
                    (float bx, float by, float br) = _badges[s];
                    if (br <= 0 || MathF.Abs(x - bx) > br + 2 || MathF.Abs(my - by) > br + 2) continue;
                    Seat seat = _setup.Seats[s];
                    if (!seat.Used || seat.Remote || !(s == 0 || seat.Bot) || CharacterOf(seat) is not Character c) continue;
                    Sound(menu, MenuSounds.Neutral, "skin badge");
                    OpenChooser(menu, s, c, SlotOf(seat.Hunter, seat.Skin));
                    return true;
                }
            }
            return false;
        }

        // ---- drawing (FrontendSession's draw pass, through ReplacePreview) ----

        private readonly (float X, float Y, float R)[] _badges = new (float, float, float)[8];
        private readonly Dictionary<string, (int Id, float U0, float V0, float U1, float V1)> _bodyTextures = new();
        private (byte[] Rgba, int W, int H, float U0, float V0, float U1, float V1)? _rim, _greyRim;

        private void DrawPicker(MenuEngine menu, List<WidgetTri> tris, int start)
        {
            if (menu.Page == null) return;
            int page = menu.Page.Index;
            if (_picker.TryGetValue(page, out PickerItems? pi)) DrawHunterPage(menu, tris, start, pi);
            else if (page is LobbyPage or JoinLobbyPage && _lobbyLayers.TryGetValue(page, out int[]? layers)) DrawLobby(menu, tris, start, layers);
            else if (_chooser is { OnPick: not null, Layer: >= 0 } && _try?.Layer != _chooser.Layer && !RecompMenus.IsSectionMenuPage(menu.Page)
                && ChooserLive(menu)) // (over a section page's rows, CONTROLS' or SECOND HUNT SETTINGS': DrawTryPicker)
            {
                EnsureRims(menu);
                DrawChooser(menu, tris, _chooser.Layer, _chooser);
            }
        }

        // ---- RECOMP SETTINGS "menu art" (owner 2026-10-13: "a pop up menu with the multiplayer lobby icons with name but
        // maybe just a little bigger but still high res ... Samus low res lobby icon to represent original and then a
        // shuffle icon so it's different every time you go load the game") ----

        public const string ShuffleChoice = "shuffle";
        private readonly Dictionary<int, int> _artLayers = new();

        // an empty item of ours on the settings page for the chooser's triangles (before the page is ever entered: the
        // engine sizes its items as a page comes in)
        public void AddArtLayer(MenuPage page)
        {
            if (!_artLayers.ContainsKey(page.Index)) _artLayers[page.Index] = AddTo(page, n => new MenuItem { Index = n });
        }

        // the chooser over the page: Samus's skins on this device (ORIGINAL = the ROM's icon) and SHUFFLE, on `current`
        public void OpenArtChooser(MenuEngine menu, string current, Action<string> pick)
        {
            if (menu.Page == null || !_artLayers.TryGetValue(menu.Page.Index, out int layer)) return;
            RefreshSkins(); // an import may have ended since
            List<string> skins = SkinsOf(Characters.Samus).ToList();
            if (!skins.Contains(Characters.Original)) skins.Insert(0, Characters.Original);
            bool shuffle = skins.Count > 2; // two suits or more to shuffle between (Samus's own; SHUFFLE draws only hers)
            // Dark Samus's imported bodies after Samus's suits (owner 2026-10-13: "get dark Samus in the picker too")
            skins.AddRange(SkinsOf(Characters.DarkSamus).Where(s => !Characters.IsOriginal(s)));
            if (shuffle) skins.Add(ShuffleChoice);
            var ch = new Chooser
            {
                Page = menu.Page.Index, Character = Characters.Samus, Skins = skins, Title = "menu art", OnPick = pick, Layer = layer,
                Focus = Math.Max(0, skins.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase))),
                X0 = 18, Y0 = 22, X1 = 238,
            };
            // the lobby chooser's look, its icons bigger (21 / 14.6 there): as big as the rows let them be on the touch screen
            const float title = 10, top = 5, bottom = 22, maxY1 = 180, maxGap = 34;
            ch.Gap = maxGap;
            int rows;
            while (true)
            {
                ch.PerRow = Math.Max(1, (int)((ch.X1 - ch.X0 - 8) / ch.Gap));
                rows = (skins.Count + ch.PerRow - 1) / ch.PerRow;
                if (ch.Y0 + title + top + rows * ch.Gap + bottom <= maxY1 || ch.Gap <= 12) break;
                ch.Gap -= 0.5f;
            }
            ch.Diameter = ch.Gap * (14.6f / 17.3f);
            // the box only as wide as its widest row (and the title / names)
            float width = Math.Max(Math.Min(skins.Count, ch.PerRow) * ch.Gap + 16, 150);
            float mid = (ch.X0 + ch.X1) / 2;
            (ch.X0, ch.X1) = (Math.Max(ch.X0, mid - width / 2), Math.Min(ch.X1, mid + width / 2));
            ch.Y1 = ch.Y0 + title + top + rows * ch.Gap + bottom;
            float shift = (192 - ch.Y1 - ch.Y0) / 2; // centred on the touch screen
            ch.Y0 += shift;
            ch.Y1 += shift;
            ch.Centres = new (float, float)[skins.Count];
            for (int i = 0; i < skins.Count; i++)
            {
                int row = i / ch.PerRow, inRow = Math.Min(ch.PerRow, skins.Count - row * ch.PerRow), col = i % ch.PerRow;
                float x = mid + (col - (inRow - 1) / 2f) * ch.Gap;
                float y = ch.Y0 + title + top + (row + 0.5f) * ch.Gap;
                ch.Centres[i] = (x, -y);
            }
            _chooser = ch;
            Sound(menu, MenuSounds.Neutral, "menu art chooser");
            Log?.Invoke($"menu art chooser: {skins.Count} choices, on {skins[ch.Focus]}");
        }

        // SHUFFLE's icon: the skins' round rim around two crossing arrows (drawn here, in the chooser's gold)
        private int ShuffleTexture(MenuEngine menu)
        {
            if (_rim is not { } rim) return -1;
            return menu.Widgets.Textures.GetOrAdd("character icon shuffle", () =>
            {
                byte[] px = ComposeIcon(rim, null, grey: false, keepFace: false);
                var lines = new (float X0, float Y0, float X1, float Y1)[]
                {
                    (32, 82, 48, 82), (48, 82, 76, 46), (76, 46, 88, 46),
                    (32, 46, 48, 46), (48, 46, 57, 58), (67, 70, 76, 82), (76, 82, 88, 82),
                };
                var heads = new (float X, float Y)[] { (88, 46), (88, 82) };
                const float half = 3.6f;
                for (int y = 0; y < 128; y++)
                {
                    for (int x = 0; x < 128; x++)
                    {
                        float fx = x + 0.5f, fy = y + 0.5f, d = float.MaxValue;
                        foreach (var l in lines) d = MathF.Min(d, SegmentDistance(fx, fy, l.X0, l.Y0, l.X1, l.Y1) - half);
                        foreach (var h in heads)
                        {
                            // an arrowhead pointing right, its tip 11 px past the line's end, 13 px long
                            float tx = h.X + 11 - fx, dy = MathF.Abs(fy - h.Y);
                            d = MathF.Min(d, MathF.Max(dy - tx * 0.75f, MathF.Max(-tx, tx - 13)));
                        }
                        float a = Math.Clamp(0.5f - d, 0, 1);
                        if (a <= 0) continue;
                        int i = (y * 128 + x) * 4;
                        px[i] = (byte)(px[i] + (255 - px[i]) * a);
                        px[i + 1] = (byte)(px[i + 1] + (247 - px[i + 1]) * a);
                        px[i + 2] = (byte)(px[i + 2] + (156 - px[i + 2]) * a);
                        px[i + 3] = (byte)Math.Max(px[i + 3], 255 * a);
                    }
                }
                return (128, 128, px);
            }).Id;
        }

        private static float SegmentDistance(float px, float py, float x0, float y0, float x1, float y1)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float t = Math.Clamp(((px - x0) * dx + (py - y0) * dy) / (dx * dx + dy * dy), 0, 1);
            float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
            return MathF.Sqrt(ex * ex + ey * ey);
        }

        private void DrawHunterPage(MenuEngine menu, List<WidgetTri> tris, int start, PickerItems pi)
        {
            bool choosing = ChooserLive(menu);
            // the ROM rims the new icons are made of: Samus's (lit) and Weavel's dark one
            _rim ??= RimOf(menu, tris, start, pi.Icon[0]);
            _greyRim ??= RimOf(menu, tris, start, pi.Off[6]);
            _syluxIcon ??= RimOf(menu, tris, start, pi.Icon[SyluxSlot]);
            var icons = new HashSet<int>();
            for (int k = 0; k < PickerSlots; k++)
            {
                icons.Add(pi.Icon[k]);
                if (pi.Off[k] >= 0) icons.Add(pi.Off[k]);
                icons.Add(pi.Highlight[k]);
            }
            ScaleItems(tris, start, icons, IconScale);
            foreach (int slot in new[] { TrooperSlot, DarkSamusSlot })
            {
                string? skin = choosing && _chooser!.Slot == slot ? _chooser.Skins[_chooser.Focus] : DisplaySkin(slot);
                Retexture(tris, start, pi.Icon[slot], IconTexture(menu, skin, grey: false));
                if (_greyRim != null)
                {
                    Retexture(tris, start, pi.Off[slot], IconTexture(menu, null, grey: true));
                    Question(menu, tris, start, pi.Off[slot]);
                }
                Body(menu, tris, start, pi.Portrait[slot], skin, fit: (73.9f, 113.4f, 73, 65));
            }
            // Samus's and Sylux's pictures show the skin the chooser is on (ORIGINAL: the ROM's)
            if (choosing && _chooser!.Slot is 0 or SyluxSlot) Body(menu, tris, start, pi.Portrait[_chooser.Slot], _chooser.Skins[_chooser.Focus], fit: (73.9f, 113.4f, 73, 65));
            if (choosing) DrawChooser(menu, tris, pi.Layer, _chooser!);
        }

        // the rims from SELECT HUNTER's icons in their Idle look, for a lobby reached without that page drawn: back from a
        // match the menus load straight into the lobby, and its badges and chooser had no rim to make icons from (blank;
        // owner 2026-10-08 23:08)
        private void EnsureRims(MenuEngine menu)
        {
            if (_rim != null && _greyRim != null && _syluxIcon != null || !_picker.TryGetValue(HunterPage, out PickerItems? pi)) return;
            MenuPage page = menu.File.Pages[HunterPage];
            _rim ??= RimOf(menu, IdleLook(menu, page, pi.Icon[0]), 0, pi.Icon[0]);
            _greyRim ??= RimOf(menu, IdleLook(menu, page, pi.Off[6]), 0, pi.Off[6]);
            _syluxIcon ??= RimOf(menu, IdleLook(menu, page, pi.Icon[SyluxSlot]), 0, pi.Icon[SyluxSlot]);
            if (_rim == null && !_rimMissing) Log?.Invoke("multiplayer: no SELECT HUNTER icon rim: the skin icons can't be drawn");
            _rimMissing = _rim == null;
        }

        private bool _rimMissing;

        // an item's Idle look (its widget's first frame), tagged with the item
        private static List<WidgetTri> IdleLook(MenuEngine menu, MenuPage page, int item)
        {
            var tris = new List<WidgetTri>();
            if (item < 0 || page.Items[item].GetState((int)MenuState.Idle) is not { WidgetIndex: >= 0 } idle) return tris;
            menu.Widgets.Evaluate(idle.WidgetIndex, 0, loop: true, alpha: 1, tris);
            for (int t = 0; t < tris.Count; t++)
            {
                WidgetTri tri = tris[t];
                tri.Item = item;
                tris[t] = tri;
            }
            return tris;
        }

        private void DrawLobby(MenuEngine menu, List<WidgetTri> tris, int start, int[] layers)
        {
            for (int s = 0; s < _setup.Seats.Length; s++) _badges[s] = default;
            if (_seats == null) return;
            EnsureRims(menu);
            bool joiner = menu.Page!.Index == JoinLobbyPage;
            for (int s = 0; s < _setup.Seats.Length; s++)
            {
                Seat seat = _setup.Seats[s];
                bool shown = seat.Used && !(joiner && _joinState != 2);
                // the pictures on their way out (vanilla's cross-fade to the next hunter) keep the look they showed
                foreach ((Hunter h, int item) in _seats[s].Portrait)
                {
                    if (item < 0 || shown && h == seat.Hunter || !_looks.Shown.TryGetValue(item, out string? was)) continue;
                    if (TriBounds(tris, start, item, out float lx0, out float ly0, out float lx1, out float ly1))
                    {
                        Body(menu, tris, start, item, was, fit: ((lx0 + lx1) / 2, -ly0, lx1 - lx0, ly1 - ly0));
                    }
                }
                if (shown) DrawSeatPicture(menu, tris, start, layers, s, seat);
                DrawGhost(menu, tris, start, s);
            }
            if (ChooserLive(menu)) DrawChooser(menu, tris, layers[^1], _chooser!);
        }

        private void DrawSeatPicture(MenuEngine menu, List<WidgetTri> tris, int start, int[] layers, int s, Seat seat)
        {
            if (!_seats![s].Portrait.TryGetValue(seat.Hunter, out int portrait) || portrait < 0) return;
            if (!TriBounds(tris, start, portrait, out float x0, out float y0, out float x1, out float y1)) return;
            // the look the fades have taken in (TickLooks), so a new one never shows before its fade starts
            string? skin = _looks.Last[s] is { } last && last.Hunter == seat.Hunter ? last.Skin : seat.Skin;
            if (!Characters.IsOriginal(skin))
            {
                // (DS px, y down: the picture's box)
                Body(menu, tris, start, portrait, skin, fit: ((x0 + x1) / 2, -y0, x1 - x0, y1 - y0));
            }
            Character? c = CharacterOf(seat);
            if (c == null || SkinsOf(c).Count <= 1) return;
            float r = 7.5f * SeatScale, bx = x0 + r + SeatScale, by = y1 - r - SeatScale; // (P8: smaller in two rows)
            _badges[s] = (bx, by, r);
            int tex = Characters.IsOriginal(seat.Skin) ? -1 : IconTexture(menu, seat.Skin, grey: false);
            if (tex < 0) tex = RomIconTexture(menu, seat.Hunter);
            if (tex >= 0) Quad(tris, layers[s], tex, bx - r, by + r, bx + r, by - r, 1, 1, 1, 1);
        }

        // ---- the lobby pictures' fades (owner 2026-10-08 22:30: TROOPER / DARK SAMUS fade in like the hunters) ----
        //
        // Vanilla cross-fades a seat's picture when its hunter changes: the old hunter's picture item plays Idle->Hidden
        // (23 frames) while the new one's plays Hidden->Idle (17). TROOPER is drawn over Weavel's item and DARK SAMUS over
        // Samus's, so a step between a character and its base hunter (or between two skins) changes no item and the ROM
        // plays nothing (the new look popped in), and an item on its way out faded as the base hunter (the vanilla picture
        // showed before the new one faded in). So each picture item remembers the look it showed and leaves with it, and a
        // new look on the same item replays that item's fade in, the old look fading out over it from the item's own
        // Idle->Hidden animation (its "ghost").

        private sealed class LobbyLooks
        {
            public int Page = -1;
            public readonly Dictionary<int, string?> Shown = new(); // picture item -> the skin it shows / showed (null = the ROM's)
            public readonly (Hunter Hunter, string? Skin)?[] Last = new (Hunter, string?)?[8];
            public readonly (int Item, string? Skin, int Start)?[] Ghost = new (int, string?, int)?[8];
        }

        private readonly LobbyLooks _looks = new();

        // (MultiplayerMenu.Tick, after the engine's tick: a restarted fade draws its first frame)
        private void TickLooks(MenuEngine menu)
        {
            int page = menu.Page?.Index ?? -1;
            if (page != _looks.Page)
            {
                _looks.Page = page;
                _looks.Shown.Clear();
                Array.Clear(_looks.Last);
                Array.Clear(_looks.Ghost);
            }
            if (page is not (LobbyPage or JoinLobbyPage) || _seats == null || menu.PendingPage != -1) return;
            bool joiner = page == JoinLobbyPage;
            for (int s = 0; s < _setup.Seats.Length; s++)
            {
                Seat seat = _setup.Seats[s];
                if (!seat.Used || joiner && _joinState != 2 || !_seats[s].Portrait.TryGetValue(seat.Hunter, out int item) || item < 0)
                {
                    _looks.Last[s] = null;
                    continue;
                }
                string? skin = Characters.IsOriginal(seat.Skin) ? null : seat.Skin;
                if (_looks.Last[s] is { } last && last.Hunter == seat.Hunter && !string.Equals(last.Skin, skin, StringComparison.OrdinalIgnoreCase)
                    && menu.ItemState(item) != MenuState.Hidden)
                {
                    _looks.Ghost[s] = (item, last.Skin, menu.PageFrame);
                    menu.SetState(item, MenuState.Hidden);
                    menu.SetState(item, MenuState.Idle);
                }
                _looks.Last[s] = (seat.Hunter, skin);
                _looks.Shown[item] = skin;
            }
        }

        // the old look fading out of a picture item that took a new one: the item's Idle->Hidden animation, placed as the
        // engine places the item (MenuEngine.Collect), drawn under the item's new look
        private void DrawGhost(MenuEngine menu, List<WidgetTri> tris, int start, int seat)
        {
            if (_looks.Ghost[seat] is not { } g) return;
            MenuItem it = menu.Page!.Items[g.Item];
            int widget = it.GetState(MenuStateCode.Transition(MenuState.Idle, MenuState.Hidden))?.WidgetIndex ?? -1;
            int f = menu.PageFrame - g.Start;
            if (widget < 0 || f < 0 || f >= menu.Widgets.FrameCount(widget))
            {
                _looks.Ghost[seat] = null;
                return;
            }
            var ghost = new List<WidgetTri>();
            menu.Widgets.Evaluate(widget, f, loop: false, alpha: 1, ghost);
            if (ghost.Count == 0) return;
            for (int t = 0; t < ghost.Count; t++)
            {
                WidgetTri tri = ghost[t];
                tri.A.X += it.X; tri.B.X += it.X; tri.C.X += it.X;
                tri.A.Y += it.Y; tri.B.Y += it.Y; tri.C.Y += it.Y;
                tri.Z += it.Depth;
                tri.Item = g.Item;
                ghost[t] = tri;
            }
            menu.TransformTris(g.Item, ghost, 0); // (P8: the seat's scale and place in two rows)
            if (g.Skin != null)
            {
                TriBounds(ghost, 0, g.Item, out float x0, out float y0, out float x1, out float y1);
                var quad = new List<WidgetTri>();
                if (!BodyQuad(menu, g.Item, g.Skin, ((x0 + x1) / 2, -y0, x1 - x0, y1 - y0), ghost[0].A.A, ghost[0].Z, quad)) return;
                ghost = quad;
            }
            int at = tris.FindIndex(start, t => t.Item == g.Item);
            tris.InsertRange(at < 0 ? tris.Count : at, ghost);
        }

        // a skin's round icon for another page (ONLINE PLAY's lobby seats: TROOPER / DARK SAMUS, not Weavel's or Samus's
        // ROM icon); -1 = an original skin or one this device has no picture of (yet)
        public int SkinIcon(MenuEngine menu, string? skin)
        {
            if (Characters.IsOriginal(skin)) return -1;
            EnsureRims(menu);
            if (CharacterPictures.For(PickerHdRoot, PickerPicturesDir, skin) == null) return -1;
            return IconTexture(menu, skin, grey: false);
        }

        // a hunter's own round icon (SELECT HUNTER's, for ORIGINAL in a lobby badge): Samus's is the lit rim; Sylux's his
        // own icon (a hunter with skins: Prime 4's)
        private (byte[] Rgba, int W, int H, float U0, float V0, float U1, float V1)? _syluxIcon;

        private int RomIconTexture(MenuEngine menu, Hunter hunter)
        {
            var icon = hunter == Hunter.Samus ? _rim : hunter == Hunter.Sylux ? _syluxIcon : null;
            if (icon is not { } rim) return -1;
            string key = "character icon rom " + hunter.ToString().ToLowerInvariant();
            return menu.Widgets.Textures.GetOrAdd(key, () => (128, 128, ComposeIcon(rim, null, grey: false, keepFace: true))).Id;
        }

        // the chooser: the lobby chooser's look (mockup): a dark box with the ROM's gold border and title bar, the skins'
        // round icons (the focused one with the ROM's highlight), its name, and how to close it
        private void DrawChooser(MenuEngine menu, List<WidgetTri> tris, int layer, Chooser ch)
        {
            ch.Tick++;
            float x0 = ch.X0, x1 = ch.X1, y0 = -ch.Y0, y1 = -ch.Y1; // menu space
            const float border = 0.7f, title = 10;
            Fill(tris, layer, x0, y0, x1, y1, 160, 110, 30);
            Fill(tris, layer, x0 + border, y0 - border, x1 - border, y1 + border, 6, 4, 2);
            Fill(tris, layer, x0 + border, y0 - border, x1 - border, y0 - title, 125, 98, 40);
            Text(menu, tris, layer, ch.Title, (x0 + x1) / 2, y0 - title / 2, 0.84f, 16, 8, 0);
            int halo = HighlightWidget(menu);
            for (int i = 0; i < ch.Skins.Count; i++)
            {
                (float cx, float cy) = ch.Centres[i];
                float r = ch.Diameter / 2;
                if (i == ch.Focus && halo >= 0) Halo(menu, tris, layer, halo, cx, cy, ch.Diameter * 1.55f, ch.Tick);
                int tex = ch.Skins[i] == ShuffleChoice ? ShuffleTexture(menu)
                    : Characters.IsOriginal(ch.Skins[i]) ? RomIconTexture(menu, ch.Character.Base) : IconTexture(menu, ch.Skins[i], grey: false);
                if (tex >= 0) Quad(tris, layer, tex, cx - r, cy + r, cx + r, cy - r, 1, 1, 1, 1);
            }
            string label = ch.Skins[ch.Focus] == ShuffleChoice ? "SHUFFLE" : Characters.Label(ch.Skins[ch.Focus]);
            // a long name ("CORRUPTION DARK SAMUS") shrinks to stay inside the box (owner 2026-10-18: it ran past both sides)
            float room = x1 - x0 - 2 * border - 4, wide = menu.Font.Measure(label);
            float scale = wide > 0 ? Math.Min(0.84f, room / wide) : 0.84f;
            Text(menu, tris, layer, label, (x0 + x1) / 2, y1 + (ch.Hint ? 14.6f : 4.6f), scale, 255, 247, 156);
            if (ch.Hint) Text(menu, tris, layer, "tap outside to close", (x0 + x1) / 2, y1 + 6.2f, 0.58f, 150, 150, 150);
        }

        // ---- drawing helpers ----

        private static bool TriBounds(List<WidgetTri> tris, int start, int item, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = float.MaxValue;
            x1 = y1 = float.MinValue;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != item) continue;
                foreach (UiVertex v in new[] { tris[t].A, tris[t].B, tris[t].C })
                {
                    x0 = MathF.Min(x0, v.X); x1 = MathF.Max(x1, v.X);
                    y0 = MathF.Min(y0, v.Y); y1 = MathF.Max(y1, v.Y);
                }
            }
            return x1 > x0;
        }

        // each item's triangles scaled about the middle of its own (the closed-up row's icons and highlights)
        private static void ScaleItems(List<WidgetTri> tris, int start, HashSet<int> items, float scale)
        {
            var centres = new Dictionary<int, (float X, float Y)>();
            foreach (int item in items)
            {
                if (TriBounds(tris, start, item, out float x0, out float y0, out float x1, out float y1)) centres[item] = ((x0 + x1) / 2, (y0 + y1) / 2);
            }
            for (int t = start; t < tris.Count; t++)
            {
                WidgetTri tri = tris[t];
                if (!centres.TryGetValue(tri.Item, out var c)) continue;
                tri.A = Scaled(tri.A, c.X, c.Y, scale); tri.B = Scaled(tri.B, c.X, c.Y, scale); tri.C = Scaled(tri.C, c.X, c.Y, scale);
                tris[t] = tri;
            }
        }

        private static UiVertex Scaled(UiVertex v, float cx, float cy, float s)
        {
            v.X = cx + (v.X - cx) * s;
            v.Y = cy + (v.Y - cy) * s;
            return v;
        }

        // an item's triangles drawn with another texture, its UVs spread over all of it (the ROM icon's quad -> our 128 px icon)
        private static void Retexture(List<WidgetTri> tris, int start, int item, int texture)
        {
            if (texture < 0) return;
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != item) continue;
                foreach (UiVertex v in new[] { tris[t].A, tris[t].B, tris[t].C })
                {
                    u0 = MathF.Min(u0, v.U); u1 = MathF.Max(u1, v.U);
                    v0 = MathF.Min(v0, v.V); v1 = MathF.Max(v1, v.V);
                }
            }
            if (u1 - u0 < 1e-6f || v1 - v0 < 1e-6f) return;
            UiVertex Spread(UiVertex v)
            {
                v.U = (v.U - u0) / (u1 - u0);
                v.V = (v.V - v0) / (v1 - v0);
                return v;
            }
            for (int t = start; t < tris.Count; t++)
            {
                WidgetTri tri = tris[t];
                if (tri.Item != item) continue;
                tri.A = Spread(tri.A); tri.B = Spread(tri.B); tri.C = Spread(tri.C);
                tri.TextureId = texture;
                tri.WrapS = tri.WrapT = UiWrap.Clamp;
                tris[t] = tri;
            }
        }

        // the ROM icon's texture inside its UVs (an icon item's first triangle), to build ours from
        private static (byte[] Rgba, int W, int H, float U0, float V0, float U1, float V1)? RimOf(MenuEngine menu, List<WidgetTri> tris, int start, int item)
        {
            if (item < 0) return null;
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            int tex = -1;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != item || tris[t].TextureId < 0) continue;
                tex = tris[t].TextureId;
                foreach (UiVertex v in new[] { tris[t].A, tris[t].B, tris[t].C })
                {
                    u0 = MathF.Min(u0, v.U); u1 = MathF.Max(u1, v.U);
                    v0 = MathF.Min(v0, v.V); v1 = MathF.Max(v1, v.V);
                }
            }
            if (tex < 0) return null;
            UiTexture t0 = menu.Widgets.Textures[tex];
            return (t0.Rgba, t0.Width, t0.Height, u0, v0, u1, v1);
        }

        // a skin's round icon (A2: the ROM rim around its head picture, 128 px) or B1's greyed one (the dark rim, dark
        // inside; the "?" is drawn over it in the font); without its picture yet, the rim around a dark inside
        private int IconTexture(MenuEngine menu, string? skin, bool grey)
        {
            var rim = grey ? _greyRim : _rim;
            if (rim == null) return -1;
            (string Full, string Head)? pics = grey ? null : CharacterPictures.For(PickerHdRoot, PickerPicturesDir, skin);
            string key = grey ? "character icon grey" : pics is { } p ? "character icon " + p.Head : "character icon empty";
            try
            {
                return menu.Widgets.Textures.GetOrAdd(key, () =>
                {
                    (byte[] Px, int W, int H)? head = null;
                    if (pics is { } q)
                    {
                        byte[] px = MphRecomp.Import.PngOut.Read(q.Head, out int w, out int h);
                        head = (px, w, h);
                    }
                    return (128, 128, ComposeIcon(rim.Value, head, grey, keepFace: false));
                }).Id;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log?.Invoke($"multiplayer: no icon for {skin}: {ex.Message}");
                return -1;
            }
        }

        // 128 x 128: a black disc (r 54.5) with the head picture's top square (106 px at 11, 11) in it, and the ROM icon's
        // rim (its texture scaled up, the ring r 50.5..66) over it; keepFace: the ROM icon whole (a hunter's own)
        private static byte[] ComposeIcon((byte[] Rgba, int W, int H, float U0, float V0, float U1, float V1) rim, (byte[] Px, int W, int H)? head,
            bool grey, bool keepFace)
        {
            const int size = 128;
            var px = new byte[size * size * 4];
            int inside = grey ? 16 : 0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - 64, dy = y + 0.5f - 64, r = MathF.Sqrt(dx * dx + dy * dy);
                    float cr = inside, cg = inside, cb = inside, ca = Math.Clamp(55f - r, 0, 1); // the disc, edge smoothed
                    if (head is { } hp && r < 55 && !keepFace)
                    {
                        // the head picture's top square, starting HeadCropTop down, onto 106 px at (11, 11)
                        float side = hp.W, top = hp.H * CharacterPictures.HeadCropTop;
                        float sx = (x + 0.5f - 11) / 106 * side, sy = top + (y + 0.5f - 11) / 106 * side;
                        (float pr, float pg, float pb, float pa) = Sample(hp.Px, hp.W, hp.H, sx / hp.W, sy / hp.H, 1.2f);
                        cr = cr * (1 - pa) + pr * pa; cg = cg * (1 - pa) + pg * pa; cb = cb * (1 - pa) + pb * pa;
                    }
                    if (keepFace || r >= 50.5f && r < 66)
                    {
                        float u = rim.U0 + (x + 0.5f) / size * (rim.U1 - rim.U0), v = rim.V0 + (y + 0.5f) / size * (rim.V1 - rim.V0);
                        (float rr, float rg, float rb, float ra) = Sample(rim.Rgba, rim.W, rim.H, u, v, 0);
                        float a = ra / 255f;
                        cr = cr * (1 - a) + rr * a; cg = cg * (1 - a) + rg * a; cb = cb * (1 - a) + rb * a;
                        ca = ca + a * (1 - ca);
                    }
                    int i = (y * size + x) * 4;
                    px[i] = (byte)Math.Clamp(cr, 0, 255); px[i + 1] = (byte)Math.Clamp(cg, 0, 255);
                    px[i + 2] = (byte)Math.Clamp(cb, 0, 255); px[i + 3] = (byte)Math.Clamp(ca * 255, 0, 255);
                }
            }
            return px;
        }

        // bilinear (blur > 0: also averaged over that many source pixels around, for a picture shrunk to the icon), u/v 0..1;
        // colour 0..255 and alpha 0..1 (alpha 0..255 for the rim: blur 0)
        private static (float R, float G, float B, float A) Sample(byte[] px, int w, int h, float u, float v, float blur)
        {
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            if (blur > 0)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < 4; k++)
                {
                    float ox = (k % 2 - 0.5f) * blur, oy = (k / 2 - 0.5f) * blur;
                    (float r1, float g1, float b1, float a1) = Bilinear(px, w, h, fx + ox, fy + oy);
                    r += r1 * a1; g += g1 * a1; b += b1 * a1; a += a1;
                }
                return a <= 0 ? (0, 0, 0, 0) : (r / a, g / a, b / a, a / 4 / 255);
            }
            return Bilinear(px, w, h, fx, fy);
        }

        private static (float R, float G, float B, float A) Bilinear(byte[] px, int w, int h, float fx, float fy)
        {
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            float r = 0, g = 0, b = 0, a = 0;
            for (int k = 0; k < 4; k++)
            {
                int x = Math.Clamp(x0 + k % 2, 0, w - 1), y = Math.Clamp(y0 + k / 2, 0, h - 1);
                float wgt = (k % 2 == 0 ? 1 - tx : tx) * (k / 2 == 0 ? 1 - ty : ty);
                int i = (y * w + x) * 4;
                r += px[i] * wgt; g += px[i + 1] * wgt; b += px[i + 2] * wgt; a += px[i + 3] * wgt;
            }
            return (r, g, b, a);
        }

        // an item's triangles replaced by the skin's whole-body picture, its body fitted into `fit` (centre x, bottom y in
        // DS touch px; width, height), at the item's own alpha and place in the draw order; nothing without a picture
        private void Body(MenuEngine menu, List<WidgetTri> tris, int start, int item, string? skin, (float X, float Bottom, float W, float H) fit)
        {
            if (item < 0 || Characters.IsOriginal(skin)) return;
            int at = -1;
            float alpha = 1, z = 0;
            for (int t = start; t < tris.Count; t++)
            {
                if (tris[t].Item != item) continue;
                at = t;
                alpha = tris[t].A.A;
                z = tris[t].Z;
                break;
            }
            var quad = new List<WidgetTri>();
            if (at < 0 || !BodyQuad(menu, item, skin!, fit, alpha, z, quad)) return;
            tris.RemoveAll(t => t.Item == item);
            at = Math.Min(at, tris.Count);
            tris.InsertRange(at, quad);
        }

        // the skin's whole-body picture as a quad, its body fitted into `fit` (as Body's), at that alpha and depth
        private bool BodyQuad(MenuEngine menu, int item, string skin, (float X, float Bottom, float W, float H) fit, float alpha, float z,
            List<WidgetTri> into)
        {
            if (BodyTexture(menu, skin) is not { } tex) return false;
            float bw = (tex.U1 - tex.U0) * CharacterPictures.FullW, bh = (tex.V1 - tex.V0) * CharacterPictures.FullH;
            float scale = MathF.Min(fit.W / bw, fit.H / bh);
            float w = bw * scale, h = bh * scale, x0 = fit.X - w / 2, y0 = -fit.Bottom;
            Quad(into, item, tex.Id, x0, y0 + h, x0 + w, y0, 1, 1, 1, alpha, tex.U0, tex.V0, tex.U1, tex.V1, z);
            return true;
        }

        // a skin's whole-body picture as a texture, with the UVs of its body's box (its pixels that aren't clear)
        private (int Id, float U0, float V0, float U1, float V1)? BodyTexture(MenuEngine menu, string skin)
        {
            if (CharacterPictures.For(PickerHdRoot, PickerPicturesDir, skin) is not { } pics) return null;
            if (_bodyTextures.TryGetValue(pics.Full, out var known)) return known;
            try
            {
                byte[] px = MphRecomp.Import.PngOut.Read(pics.Full, out int w, out int h);
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (px[(y * w + x) * 4 + 3] < 24) continue;
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    }
                }
                if (x1 < 0) return null;
                int id = menu.Widgets.Textures.GetOrAdd("character body " + pics.Full, () => (w, h, px)).Id;
                var tex = (id, x0 / (float)w, y0 / (float)h, (x1 + 1) / (float)w, (y1 + 1) / (float)h);
                _bodyTextures[pics.Full] = tex;
                return tex;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log?.Invoke($"multiplayer: no picture for {skin}: {ex.Message}");
                return null;
            }
        }

        // B1's "?" over a greyed icon, in the ROM font, grey
        private static void Question(MenuEngine menu, List<WidgetTri> tris, int start, int item)
        {
            if (!TriBounds(tris, start, item, out float x0, out float y0, out float x1, out float y1)) return;
            float alpha = tris.First(t => t.Item == item).A.A;
            Text(menu, tris, item, "?", (x0 + x1) / 2, (y0 + y1) / 2, 1.5f, 120, 120, 120, alpha);
        }

        // text centred on (x, y) (menu space), scaled about that point
        private static void Text(MenuEngine menu, List<WidgetTri> tris, int item, string text, float x, float y, float scale, int r, int g, int b,
            float alpha = 1)
        {
            var glyphs = new List<WidgetTri>();
            // Emit's y anchors the line's bottom in text space; a line is 8 tall: its middle 4 above
            menu.Font.Emit(text, x, y + 192 - 4, align: 2, wrapWidth: 0, lineHeight: 8, r / 255f, g / 255f, b / 255f, alpha, 5000, glyphs);
            for (int t = 0; t < glyphs.Count; t++)
            {
                WidgetTri tri = glyphs[t];
                tri.A = Scaled(tri.A, x, y, scale); tri.B = Scaled(tri.B, x, y, scale); tri.C = Scaled(tri.C, x, y, scale);
                tri.Item = item;
                tris.Add(tri);
            }
        }

        private static void Fill(List<WidgetTri> tris, int item, float x0, float y0, float x1, float y1, int r, int g, int b) =>
            Quad(tris, item, -1, x0, y0, x1, y1, r / 255f, g / 255f, b / 255f, 1);

        // a quad, (x0, y0) top left and (x1, y1) bottom right in menu space (y up)
        private static void Quad(List<WidgetTri> tris, int item, int texture, float x0, float y0, float x1, float y1, float r, float g, float b,
            float a, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1, float z = 5000)
        {
            var p0 = new UiVertex(x0, y0, u0, v0, r, g, b, a);
            var p1 = new UiVertex(x1, y0, u1, v0, r, g, b, a);
            var p2 = new UiVertex(x1, y1, u1, v1, r, g, b, a);
            var p3 = new UiVertex(x0, y1, u0, v1, r, g, b, a);
            tris.Add(new WidgetTri { A = p0, B = p1, C = p2, Z = z, TextureId = texture, Item = item, WrapS = UiWrap.Clamp, WrapT = UiWrap.Clamp });
            tris.Add(new WidgetTri { A = p0, B = p2, C = p3, Z = z, TextureId = texture, Item = item, WrapS = UiWrap.Clamp, WrapT = UiWrap.Clamp });
        }

        // the ROM's small_highlight in its Focused look (SELECT HUNTER's halo), for the chooser's focused skin
        private int HighlightWidget(MenuEngine menu)
        {
            if (!_picker.TryGetValue(HunterPage, out PickerItems? pi) || pi.Highlight[0] < 0) return -1;
            MenuItem it = menu.File.Pages[HunterPage].Items[pi.Highlight[0]];
            return it.GetState((int)MenuState.Focused)?.WidgetIndex ?? -1;
        }

        private static void Halo(MenuEngine menu, List<WidgetTri> tris, int item, int widget, float cx, float cy, float size, int tick)
        {
            var halo = new List<WidgetTri>();
            menu.Widgets.Evaluate(widget, tick % 25, loop: true, alpha: 1, halo);
            if (halo.Count == 0) return;
            float x0 = halo.Min(t => MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X))), x1 = halo.Max(t => MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)));
            float y0 = halo.Min(t => MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y))), y1 = halo.Max(t => MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)));
            float s = size / MathF.Max(1e-3f, MathF.Max(x1 - x0, y1 - y0)), mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            UiVertex Move(UiVertex v)
            {
                v.X = cx + (v.X - mx) * s;
                v.Y = cy + (v.Y - my) * s;
                return v;
            }
            foreach (WidgetTri t0 in halo)
            {
                WidgetTri t = t0;
                t.A = Move(t.A); t.B = Move(t.B); t.C = Move(t.C);
                t.Item = item;
                tris.Add(t);
            }
        }
    }
}
