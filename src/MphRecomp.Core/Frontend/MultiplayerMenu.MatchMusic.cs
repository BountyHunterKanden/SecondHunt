using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Config;
using MphRecomp.PrimeMusic;

namespace MphRecomp.Frontend
{
    // The pre-match MUSIC row (owner 2026-10-14, docs/PRIME_MUSIC.md): "small and non invasive. built like the map
    // picker", shown only while RECOMP SETTINGS' music picker is on. A copy of the ARENA row (its label, arrows, box and
    // name text), narrower, under the rules box between the back button and OK on every rules page (28-34). It steps
    // through ARENA MUSIC (the arena's own), SHUFFLE and every imported song the player hasn't hidden, and writes
    // RECOMP SETTINGS' match music: the player's own choice, nothing goes to the other players. A (or a tap) on the box
    // opens RECOMP SETTINGS' song list in its MATCH MUSIC pick, the same games and areas (owner 2026-10-29): the arrows
    // still step.
    public sealed partial class MultiplayerMenu
    {
        private const int MusicNextCall = 2343, MusicPrevCall = 2344, MusicOpenCall = 2345;
        // from the ARENA row (text space): down to the back button's height, left of OK; the box 26 narrower (its arrows
        // 13 in each side, the label with the left arrow); measured on -fecanvas 28-34
        private const float MusicDx = -31, MusicDy = -122, MusicStretch = -26, MusicLabelDy = -5, MusicNameWidth = 90;
        private const float ArenaLabelX = 84, ArenaLabelY = 141, ArenaNameX = 167, ArenaNameY = 136;

        // RECOMP SETTINGS (FrontendSession): the match music, the picker switch, the hidden songs; and its save
        public Func<RecompSettings?>? MusicSettings { get; set; }
        public Action? SaveMusicSettings { get; set; }
        // RecompMenus.OpenLobbySongs: the song list, back to this rules page
        public Action<MenuEngine>? OpenSongs { get; set; }

        private sealed class MatchMusicBound
        {
            public int Value = -1; // the row's text (a string id)
            public readonly Dictionary<int, int[]> Items = new(); // rules page -> the row's items
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFile, MatchMusicBound> s_musicBound = new();
        private MatchMusicBound _musicBound = new();
        private MenuFont? _musicFont;

        // (Bind, after BindExtraRules)
        private void BindMatchMusic(MenuFile file, MenuStrings strings)
        {
            if (s_musicBound.TryGetValue(file, out MatchMusicBound? bound))
            {
                _musicBound = bound;
                return;
            }
            _musicBound = new MatchMusicBound { Value = strings.Add("") };
            int label = strings.Add("MUSIC");
            foreach (ModeInfo m in Modes)
            {
                if (file.Pages.Count > m.Page) BindMusicRow(file, file.Pages[m.Page], label);
            }
            s_musicBound.AddOrUpdate(file, _musicBound);
        }

        private void BindMusicRow(MenuFile file, MenuPage page, int label)
        {
            int arrows = FindItem(page, it => HasWidget(file, it, "arrows_type"));
            int box = FindItem(page, it => HasWidget(file, it, "box_type") && Calls(it).Contains(113));
            int left = FindItem(page, it => HasWidget(file, it, "highlight_arrowleft") && Calls(it).Contains(114));
            int right = FindItem(page, it => HasWidget(file, it, "highlight_arrowright") && Calls(it).Contains(113));
            int arenaLabel = TextAt(page, ArenaLabelX, ArenaLabelY), arenaName = TextAt(page, ArenaNameX, ArenaNameY);
            if (new[] { arrows, box, left, right, arenaLabel, arenaName }.Any(i => i < 0))
            {
                Log?.Invoke($"multiplayer: page {page.Index}'s ARENA row not found, no music row");
                return;
            }
            // the ARENA row's calls (113 next, 114 back) as ours; the box's A opens the song list
            MenuAction Our(MenuAction a, int n, int next) => CopyAction(a, item: n,
                calls: a.Calls.Select(c => (c.A, c.B == 113 ? next : c.B == 114 ? MusicPrevCall : c.B)).ToList());
            IReadOnlyList<MenuAction> Ours(MenuItem it, int n, int next = MusicNextCall) => it.Actions.Select(a => Our(a, n, next)).ToList();
            // the right arrow's tap (ROM: x 104..244, the whole box and more, over the left arrow's 80..100) cut to the
            // arrow's own end (its last 24), the box between the arrows opening the list
            const float ArrowTap = 24, LeftArrowTap = 26;
            IReadOnlyList<MenuAction> RightTaps(MenuItem it, int n) => it.Actions.SelectMany(a => a.Calls.Any(c => c.B == 113) && (a.Field4 & 1) == 0
                && a.RectW > a.RectX + ArrowTap + LeftArrowTap
                ? new[]
                {
                    Rect(a, n, a.RectX + LeftArrowTap, a.RectW - ArrowTap, MusicOpenCall),
                    Rect(a, n, a.RectW - ArrowTap, a.RectW, MusicNextCall)
                }
                : new[] { Our(a, n, MusicNextCall) }).ToList();
            static MenuAction Rect(MenuAction a, int n, float x0, float x1, int call) => new()
            {
                Kind = a.Kind, Field2 = a.Field2, Flags = a.Flags, Field4 = a.Field4, RectX = (short)MathF.Round(x0), RectY = a.RectY,
                RectW = (short)MathF.Round(x1), RectH = a.RectH, Calls = a.Calls.Select(c => (c.A, c.B == 113 ? call : c.B)).ToArray(),
                Item = (ushort)n, TargetPage = a.TargetPage, Field17 = a.Field17
            };
            var added = new List<int>();
            void Add(int src, float dx, float dy, int stringId = -1, float stretch = 0, bool act = false)
            {
                MenuItem it = page.Items[src];
                added.Add(page.Items.Count);
                page.AddItem(n => SoundsLike(RecompMenus.Clone(it, n, dx, dy, stringId: stringId, links: it.Links,
                    actions: !act ? Array.Empty<MenuAction>() : src == right ? RightTaps(it, n) : Ours(it, n, src == box ? MusicOpenCall : MusicNextCall),
                    stretchX: stretch), it.Index));
            }
            float side = -MusicStretch / 2; // how far in each arrow comes
            Add(arenaLabel, MusicDx + side, MusicDy + MusicLabelDy, stringId: label);
            Add(arrows, MusicDx, MusicDy, stretch: MusicStretch);
            Add(box, MusicDx, MusicDy, stretch: MusicStretch, act: true);
            Add(left, MusicDx + side, MusicDy, act: true);
            Add(right, MusicDx - side, MusicDy, act: true);
            Add(arenaName, MusicDx, MusicDy, stringId: _musicBound.Value);
            _musicBound.Items[page.Index] = added.ToArray();
        }

        private bool MusicPickerOn => MusicLibrary.On && MusicSettings?.Invoke()?.MusicPicker == true; // (none in a public build)

        // OnPageEntered (a rules page): the row while the picker is on, else none of it
        private void ShowMatchMusic(MenuEngine menu, int page)
        {
            _musicFont = menu.Font;
            if (!_musicBound.Items.TryGetValue(page, out int[]? items)) return;
            bool on = MusicPickerOn;
            foreach (int item in items)
            {
                if (!on) menu.SetState(item, MenuState.Hidden);
                else if (menu.ItemState(item) == MenuState.Hidden) menu.SetState(item, MenuState.Idle);
            }
        }

        // the choices in the row's order: the arena's own, shuffle, then the shown songs
        private List<string> MusicChoices(RecompSettings settings)
        {
            var all = new List<string> { "", MusicLibrary.Shuffle };
            all.AddRange(MusicLibrary.Shown(ProfileDir, settings.HiddenSongs).Select(t => t.Id));
            return all;
        }

        // OnRulesCall: an arrow or Y (back) on the row steps it; A or a tap on the box opens the song list
        private bool OnMatchMusicCall(MenuEngine menu, int b)
        {
            if (b == MusicOpenCall)
            {
                OpenSongs?.Invoke(menu);
                return true;
            }
            if (b is not (MusicNextCall or MusicPrevCall)) return false;
            if (MusicSettings?.Invoke() is not RecompSettings settings) return true;
            List<string> all = MusicChoices(settings);
            int at = Math.Max(0, all.IndexOf(settings.MatchMusic));
            settings.MatchMusic = all[((at + (b == MusicNextCall ? 1 : -1)) % all.Count + all.Count) % all.Count];
            SaveMusicSettings?.Invoke();
            return true;
        }

        // FillStrings: the row's text, cut to its box
        private void FillMatchMusic(MenuStrings s)
        {
            if (_musicBound.Value < 0) return;
            RecompSettings? settings = MusicSettings?.Invoke();
            string choice = settings?.MatchMusic ?? "";
            Track? song = MusicLibrary.Find(choice);
            string text = choice == MusicLibrary.Shuffle ? "shuffle"
                : song != null && TrackCatalog.Usable(ProfileDir, song) ? MusicLibrary.PickerTitle(song)
                : "arena music";
            s.Set(_musicBound.Value, Fit(text, song != null ? MusicLibrary.Letter(song) : ""));
        }

        // cut to the box, a lettered song keeping its letter ("tallon over... b")
        private string Fit(string text, string letter)
        {
            if (_musicFont == null || _musicFont.Measure(text) <= MusicNameWidth) return text;
            string tail = letter.Length > 0 && text.EndsWith(" " + letter, StringComparison.Ordinal) ? " " + letter : "";
            text = text[..^tail.Length];
            for (int n = text.Length - 1; n > 0; n--)
            {
                string cut = text[..n].TrimEnd() + "..." + tail;
                if (_musicFont.Measure(cut) <= MusicNameWidth) return cut;
            }
            return text;
        }
    }
}
