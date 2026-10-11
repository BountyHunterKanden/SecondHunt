using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Multiplayer;

namespace MphRecomp.Frontend
{
    // The ADVANCED box's two recomp rules (owner 2026-10-10): EXTRA PICKUPS (the Pirate Blaster) and EXTRA CHARACTERS
    // (TROOPER, DARK SAMUS), ExtraRules. Two rows copied from the box's FRIENDLY FIRE row, added once per menu file on every
    // rules page (28-34): the box's rows move up into a 20 px pitch (radar 132, damage 112, ff 92, pickups 72, characters
    // 52), AVAILABLE WEAPONS under them and its OK to the box's right. The top screen's ADVANCED summary gets a 5th line.
    // The rules are this player's own (kept in extra_rules.json); a joiner sees the host's.
    // EXTRA ARENAS (owner 2026-10-10, N28): a third row under them, only on a device that has imported arenas (ExtraArenas) when
    // the menus are bound. Five rows fill the box, so with it the title and rows go up 16 px and AVAILABLE WEAPONS 4 down
    // (Prime Hunter's box, one row short, has the room as it is).
    public sealed partial class MultiplayerMenu
    {
        private const int ExtraPickupsCall = 2340, ExtraCharactersCall = 2341, ExtraArenasCall = 2342;
        // the box's rows' new box centres (text space, y up) and the ROM's (radar 132, damage 108.2, ff 82.8)
        private const float DamageBoxY = 112, FriendlyFireBoxY = 92, PickupsBoxY = 72, CharactersBoxY = 52;
        private const float RomDamageBoxY = 108.2f, RomFriendlyFireBoxY = 82.8f;
        private const float WeaponsLabelDy = -26, WeaponsValueDy = -29, BoxOkDx = 103, ArenasLift = 16;
        private const float RomBoxTitleX = 128, RomBoxTitleY = 150;
        // the top screen's ADVANCED lines (create-page y): the ROM's four, closer, and ours under them
        private static readonly float[] AdvancedRomY = { 233, 221, 210, 199 };
        private static readonly float[] AdvancedY = { 236, 226.5f, 217, 207.5f, 198 };

        private sealed class ExtraRulesBound
        {
            public int PickupsValue = -1, CharactersValue = -1, ArenasValue = -1, Summary = -1; // string ids
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, ExtraRulesBound> s_extraBound = new();
        private ExtraRulesBound _extraBound = new();

        private static ExtraRules s_extraRules = new();
        private static string? s_extraRulesDir;
        // a joiner's view of the host's rules (LoadBeacon, LoadSetup)
        private static bool _hostPickups = true, _hostCharacters = true;

        // this player's rules (OnlineMenus, tests)
        public static ExtraRules OwnExtraRules => s_extraRules;

        private static void LoadExtraRules(string? dir)
        {
            s_extraRulesDir = dir;
            s_extraRules = ExtraRules.Load(dir);
        }

        // the rules the setup shows: the host's for a joiner, else this player's
        private static bool ShownPickups => _setupFromHost ? _hostPickups : s_extraRules.Pickups;
        private static bool ShownCharacters => ShownPickups && (_setupFromHost ? _hostCharacters : s_extraRules.Characters);
        // (a joiner: whether the host's game is in one; the beacon doesn't carry the host's rule)
        private static bool ShownArenas => _setupFromHost
            ? ArenaRooms.Length > 0 && ExtraArenas.IsExtra(MphRead.Metadata.GetRoomById(ArenaRooms[Math.Clamp(_setup.Arena, 0, ArenaRooms.Length - 1)], noThrow: true)?.Name)
            : s_extraRules.Arenas;

        private static void HostExtraRules(bool pickups, bool characters)
        {
            _hostPickups = pickups;
            _hostCharacters = characters && pickups;
        }

        // BuildMatch: the rules on the match, banned characters off its seats
        private static void ApplyExtraRules(MatchSettings settings)
        {
            s_extraRules.ApplyTo(settings);
            Characters.ApplyExtraRules(settings);
        }

        private static bool SeatIsExtra(Seat s) => s.Character != null || Characters.Of(s.Hunter, s.Skin) is { HasOriginal: false };

        // characters off: every seat playing one (and this player's last pick) back to its base hunter's own look
        private static void ScrubExtraSeats()
        {
            foreach (Seat s in _setup.Seats.Where(SeatIsExtra))
            {
                s.Character = null;
                s.Skin = null;
            }
            if (_setup.MyCharacter != null || Characters.Of(_setup.MyHunter, _setup.MySkin) is { HasOriginal: false })
            {
                _setup.MyCharacter = null;
                _setup.MySkin = null;
            }
        }

        // OnRulesCall: a tap or A / Y on one of the two rows' boxes
        private bool OnExtraRulesCall(int b)
        {
            if (b is not (ExtraPickupsCall or ExtraCharactersCall or ExtraArenasCall)) return false;
            if (_setupFromHost) return true; // (the host's game)
            if (b == ExtraArenasCall)
            {
                s_extraRules.Arenas = !s_extraRules.Arenas;
                s_extraRules.Save(s_extraRulesDir);
                RefreshArenas(); // (in or out of the arena list; a chosen one that left: the next open arena)
                if (_setup.Mode >= 2 && !ArenaAllowed(_setup.Arena)) StepArena(+1);
                return true;
            }
            if (b == ExtraPickupsCall) s_extraRules.SetPickups(!s_extraRules.Pickups);
            else s_extraRules.SetCharacters(!s_extraRules.Characters);
            s_extraRules.Save(s_extraRulesDir);
            if (!s_extraRules.Characters) ScrubExtraSeats();
            return true;
        }

        // FillStrings: the two rows' values and the top screen's 5th ADVANCED line
        private void FillExtraRules(MenuStrings s, bool rules)
        {
            string On(bool on) => (on ? Word("on") : Word("off")).ToUpperInvariant();
            s.Set(_extraBound.PickupsValue, On(ShownPickups));
            s.Set(_extraBound.CharactersValue, On(ShownCharacters));
            if (_extraBound.ArenasValue >= 0) s.Set(_extraBound.ArenasValue, On(ShownArenas));
            string summary = ShownCharacters ? "extras: all" : ShownPickups ? "extras: pickups" : "extras: off";
            s.Set(_extraBound.Summary, rules ? summary : "");
        }

        // (Bind, after BindSeats8)
        private void BindExtraRules(MenuFile file, MenuStrings strings)
        {
            if (s_extraBound.TryGetValue(file, out ExtraRulesBound? bound))
            {
                _extraBound = bound;
                return;
            }
            bool arenas = ExtraArenas.AnyOwned;
            _extraBound = new ExtraRulesBound
            {
                PickupsValue = strings.Add(""), CharactersValue = strings.Add(""), ArenasValue = arenas ? strings.Add("") : -1,
                Summary = strings.Add(""),
            };
            int pickupsLabel = strings.Add("extra pickups"), charactersLabel = strings.Add("extra characters");
            int arenasLabel = arenas ? strings.Add("extra arenas") : -1;
            foreach (ModeInfo m in Modes)
            {
                if (file.Pages.Count > m.Page) BindExtraBox(file, strings, file.Pages[m.Page], pickupsLabel, charactersLabel, arenasLabel);
            }
            for (int index = CreateJoinPage; index < file.Pages.Count && index <= JoinLobbyPage; index++)
            {
                MenuPage page = file.Pages[index];
                if (IsSetupPage(page)) BindAdvancedLines(page);
            }
            s_extraBound.AddOrUpdate(file, _extraBound);
        }

        // move a ROM item (its touch rects are relative to its offset, so they move with it)
        private static void Move(MenuPage page, int item, float dx, float dy)
        {
            if (item < 0) return;
            MenuItem it = page.Items[item];
            page.ReplaceItem(item, n => SoundsLike(RecompMenus.Clone(it, n, dx, dy, links: it.Links, actions: it.Actions), it.Index));
        }

        private void BindExtraBox(MenuFile file, MenuStrings strings, MenuPage page, int pickupsLabel, int charactersLabel, int arenasLabel)
        {
            int ByText(string text) => FindItem(page, it => it.IsText && strings[it.States[0].Text!.StringId] == text);
            int ByCall(int call) => FindItem(page, it => it.Actions.Any(a => a.Kind == 9 && a.Calls.Any(c => c.B == call)));
            int popup = FindItem(page, it => HasWidget(file, it, "popup"));
            int ok = FindItem(page, it => Calls(it).Contains(117));
            (int Label, int Value, int Box) damage = (ByText("damage level"), ByText("dam"), ByCall(119));
            (int Label, int Value, int Box) ff = (ByText("friendly fire"), ByText("ff"), ByCall(121));
            (int Label, int Value, int Box) weapons = (ByText("available weapons"), ByText("gun"), ByCall(120));
            (int Label, int Value, int Box) radar = (FindItem(page, it => it.IsText && strings[it.States[0].Text!.StringId].StartsWith("show hunters")),
                ByText("rad"), ByCall(118));
            // the box's own title, by its ROM spot (a text match also finds the side panel's ADVANCED heading)
            int heading = TextAt(page, RomBoxTitleX, RomBoxTitleY);
            if (popup < 0 || ok < 0 || damage.Label < 0 || damage.Value < 0 || damage.Box < 0)
            {
                Log?.Invoke($"multiplayer: page {page.Index}'s ADVANCED box not found, no extra rules rows");
                return;
            }
            bool hasFf = ff.Label >= 0 && ff.Value >= 0 && ff.Box >= 0;
            // the new rows: copies of the FRIENDLY FIRE row (DAMAGE LEVEL's where there's none), taken before anything moves
            var source = hasFf ? ff : damage;
            float sourceY = hasFf ? RomFriendlyFireBoxY : RomDamageBoxY;
            var added = new List<int>();
            void AddRow(int label, int value, int call, float boxY)
            {
                float dy = boxY - sourceY;
                MenuItem l = page.Items[source.Label], v = page.Items[source.Value], bx = page.Items[source.Box];
                added.Add(page.Items.Count);
                page.AddItem(n => SoundsLike(RecompMenus.Clone(l, n, 0, dy, stringId: label), l.Index));
                added.Add(page.Items.Count);
                page.AddItem(n => SoundsLike(RecompMenus.Clone(v, n, 0, dy, stringId: value), v.Index));
                added.Add(page.Items.Count);
                page.AddItem(n => SoundsLike(RecompMenus.Clone(bx, n, 0, dy, links: bx.Links,
                    actions: bx.Actions.Select(a => CopyAction(a, calls: new[] { (0, call) }, item: n)).ToList()), bx.Index));
            }
            // (Prime Hunter's box has no FRIENDLY FIRE row: everything under it one row higher)
            float up = hasFf ? 0 : FriendlyFireBoxY - PickupsBoxY;
            // EXTRA ARENAS: one row more; a full box makes room above (the title and rows up) and below (the weapons down)
            const float pitch = FriendlyFireBoxY - PickupsBoxY;
            bool lifted = arenasLabel >= 0 && hasFf && radar.Label >= 0 && radar.Value >= 0 && radar.Box >= 0 && heading >= 0;
            bool arenas = arenasLabel >= 0 && (lifted || !hasFf);
            if (arenasLabel >= 0 && !arenas) Log?.Invoke($"multiplayer: page {page.Index}'s ADVANCED box: no room found for extra arenas");
            float lift = lifted ? ArenasLift : 0;
            float down = arenas ? pitch - lift : 0; // (the weapons' extra move down)
            AddRow(pickupsLabel, _extraBound.PickupsValue, ExtraPickupsCall, PickupsBoxY + up + lift);
            AddRow(charactersLabel, _extraBound.CharactersValue, ExtraCharactersCall, CharactersBoxY + up + lift);
            if (arenas) AddRow(arenasLabel, _extraBound.ArenasValue, ExtraArenasCall, CharactersBoxY - pitch + up + lift);
            // the ROM's rows, up into the new pitch
            foreach (int item in new[] { damage.Label, damage.Value, damage.Box }) Move(page, item, 0, DamageBoxY - RomDamageBoxY + lift);
            if (hasFf)
            {
                foreach (int item in new[] { ff.Label, ff.Value, ff.Box }) Move(page, item, 0, FriendlyFireBoxY - RomFriendlyFireBoxY + lift);
            }
            if (lifted)
            {
                foreach (int item in new[] { radar.Label, radar.Value, radar.Box, heading }) Move(page, item, 0, lift);
            }
            Move(page, weapons.Label, 0, WeaponsLabelDy + up - down);
            Move(page, weapons.Value, 0, WeaponsValueDy + up - down);
            Move(page, weapons.Box, 0, WeaponsValueDy + up - down);
            Move(page, ok, BoxOkDx, 0);
            // the box shows and hides the new rows with its own (ev 2 / ev A)
            MenuItem box = page.Items[popup];
            var links = box.Links.ToList();
            foreach (int item in added) links.Add(new MenuLink(2, MenuState.Idle, item));
            foreach (int item in added) links.Add(new MenuLink(0xA, MenuState.Hidden, item));
            page.ReplaceItem(popup, n => SoundsLike(RecompMenus.Clone(box, n, 0, 0, links: links, actions: box.Actions), box.Index));
        }

        // a page's ADVANCED line r, moved or not
        private static int AdvancedLine(MenuPage page, int r)
        {
            int item = TextAt(page, 57, AdvancedRomY[r]);
            return item >= 0 ? item : TextAt(page, 57, AdvancedY[r]);
        }

        // the top screen's ADVANCED box: its four lines closer, a 5th (the extra rules) under them
        private void BindAdvancedLines(MenuPage page)
        {
            int[] rom = AdvancedRomY.Select(y => TextAt(page, 57, y)).ToArray();
            if (rom.Any(i => i < 0)) return;
            MenuItem last = page.Items[rom[^1]];
            int summary = _extraBound.Summary;
            page.AddItem(n => SoundsLike(RecompMenus.Clone(last, n, 0, AdvancedY[^1] - AdvancedRomY[^1], stringId: summary,
                links: Array.Empty<MenuLink>(), actions: Array.Empty<MenuAction>()), last.Index));
            for (int r = 0; r < rom.Length; r++) Move(page, rom[r], 0, AdvancedY[r] - AdvancedRomY[r]);
        }
    }
}
