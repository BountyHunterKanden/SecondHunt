using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// MOVIES (file select -> pages 19 and 20): the game's movie gallery, the code the pages' data only names (USA rev 1 ov0,
// 2026-10-07; rev 0 has the same calls). Each page is a sheet of thumbnails (movieplayer/thumbnails1, 2) with a hotspot
// item per tile (A or a tap: call 18-32 on page 19, 33-43 on page 20) and a "?" cover item over each one.
//  - The tiles: page 19's handler (0x213a6dc) and page 20's (0x213a598) run one small function per call that sets the
//    menus' way back (entry word 0x020CBD90+0x74 |= 0x2000 for page 19, 0x4000 for page 20) and starts the movie mode on
//    that movie (0x21356dc -> 0x2063ae0(movie, 2, 30, 0, 1)); after it the menus open on that page again.
//  - A movie is open once it has played anywhere: the movie mode's start (ov1 0x21040b8) sets its bit in the cart-wide
//    options (cfg 0x020E8318 +0x0C bits 0-31, +0x10 bits 0-3 for movies 32-35), skipped or not; a new cart has none.
//  - The pages' entry (0x2120ed0 page 19, 0x2120e3c page 20; tables 0x2143c74, 0x2143aa0 {hotspot, cover, movie}): an
//    open movie's cover is hidden, a closed one's hotspot is hidden (so its "?" takes no press), and the focus goes to
//    the first open tile (else the page arrow).
// Ours: the open movies in movies_seen.json beside recomp_settings.json (MoviePlayer.Opened marks every movie the app
// plays: the boot's opening, the campaign's and the ship's); RECOMP SETTINGS > UNLOCKS ALL opens them all.
namespace MphRecomp.Frontend
{
    public sealed class MovieGallery
    {
        public const int FirstPage = 19, SecondPage = 20;
        public const string FileName = "movies_seen.json";
        public const int FirstCall = 18, LastCall = 43;

        // each tile's call (18..43) -> MphRead Movie (ov0 rev 1 0x212eb58..0x212eedc, r0 of each)
        private static readonly int[] CallMovies =
        {
            0, 1, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, // page 19: opening, story intro, the bosses
            28, 29, 30, 14, 11, 12, 31, 32, 35, 33, 2,                // page 20: last bosses, octolith, oubliette, endings
        };

        public static int MovieOfCall(int call) => call is >= FirstCall and <= LastCall ? CallMovies[call - FirstCall] : -1;

        // the movie's file name for MoviePlayer.Open(string): MphRead's table names movie n "n+1" ("16" = movies/16_top.vx)
        public static string FileOf(int movie) => (movie + 1).ToString("00");

        private sealed class Record
        {
            public List<int> Seen { get; set; } = new();
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private readonly HashSet<int> _seen = new();
        private string? _dir;

        public Func<bool>? UnlockEverything { get; set; }
        public Action<string>? Log { get; set; }
        // starts a movie by its file name (FrontendRequestKind.PlayMovie)
        public Action<string>? Play { get; set; }
        // the page to open again once the gallery's movie is over (-1: none)
        public int ReturnPage { get; private set; } = -1;

        // the profile folder (null: the PC tools, kept in memory only)
        public string? ProfileDir
        {
            get => _dir;
            set
            {
                _dir = value;
                _seen.Clear();
                Reload();
            }
        }

        // the file again: another session of the app (the campaign's pause menu has its own) may have added to it
        private void Reload()
        {
            string? path = _dir == null ? null : Path.Combine(_dir, FileName);
            try
            {
                if (path != null && File.Exists(path))
                {
                    foreach (int m in JsonSerializer.Deserialize<Record>(File.ReadAllText(path))?.Seen ?? new())
                    {
                        if (m is >= 0 and < 36) _seen.Add(m);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"movies: {path}: {ex.Message}");
            }
        }

        public bool Open(int movie) => UnlockEverything?.Invoke() == true || _seen.Contains(movie);

        // a movie has started (MoviePlayer.Opened: its top screen's file, "movies/16_top.vx" or "movies/04.vx")
        public void MarkPlayed(string topPath)
        {
            string name = Path.GetFileName(topPath);
            if (name.Length < 2 || !int.TryParse(name.AsSpan(0, 2), out int n) || n < 1 || n > 36) return;
            Reload();
            if (!_seen.Add(n - 1)) return;
            Log?.Invoke($"movies: {n - 1} seen");
            if (_dir == null) return;
            try
            {
                Directory.CreateDirectory(_dir);
                var record = new Record { Seen = _seen.OrderBy(m => m).ToList() };
                File.WriteAllText(Path.Combine(_dir, FileName), JsonSerializer.Serialize(record, Options));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"movies: not saved: {ex.Message}");
            }
        }

        // the page's tiles: (hotspot item, cover item, movie), found by content -- the hotspot's A action calls a tile call,
        // its cover is the "cover" widget at the same place (rev 0 and rev 1 number the items differently)
        private static List<(int Hotspot, int Cover, int Movie)> Tiles(MenuEngine menu)
        {
            var tiles = new List<(int, int, int)>();
            IReadOnlyList<MenuItem> items = menu.Page!.Items;
            for (int i = 0; i < items.Count; i++)
            {
                int movie = -1;
                foreach (MenuAction a in items[i].Actions)
                {
                    foreach ((int _, int b) in a.Calls)
                    {
                        if (MovieOfCall(b) >= 0) movie = MovieOfCall(b);
                    }
                }
                if (movie < 0) continue;
                int cover = -1;
                for (int j = 0; j < items.Count && cover < 0; j++)
                {
                    if (items[j].X == items[i].X && items[j].Y == items[i].Y && IsCover(menu, items[j])) cover = j;
                }
                tiles.Add((i, cover, movie));
            }
            return tiles;
        }

        private static bool IsCover(MenuEngine menu, MenuItem item)
        {
            foreach (MenuItemState s in item.States)
            {
                if (s.WidgetIndex >= 0 && menu.File.Widgets[s.WidgetIndex].ModelPath
                    .Contains("\\cover_", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public void OnPageEntered(MenuEngine menu, int page)
        {
            if (page is not (FirstPage or SecondPage)) return;
            ReturnPage = -1;
            Reload();
            int focus = -1;
            foreach ((int hotspot, int cover, int movie) in Tiles(menu))
            {
                if (Open(movie))
                {
                    if (cover >= 0) menu.SetState(cover, MenuState.Hidden);
                    if (focus < 0) focus = hotspot;
                }
                else
                {
                    menu.SetState(hotspot, MenuState.Hidden);
                }
            }
            if (focus >= 0) menu.SetState(focus, MenuState.Focused);
        }

        // a tile's call: play its movie (only an open tile's hotspot is there to press), then this page again
        public bool OnCall(MenuEngine menu, int call)
        {
            int page = menu.Page?.Index ?? -1;
            if (page is not (FirstPage or SecondPage)) return false;
            int movie = MovieOfCall(call);
            if (movie < 0) return false;
            if (!Open(movie)) return true;
            ReturnPage = page;
            Log?.Invoke($"movies: play {movie} ({FileOf(movie)}) from page {page}");
            Play?.Invoke(FileOf(movie));
            return true;
        }

        // the movie is over (ended or skipped): the page comes in again, as vanilla's menus restart on it
        public void OnMovieDone(MenuEngine menu)
        {
            if (ReturnPage < 0) return;
            int page = ReturnPage;
            ReturnPage = -1;
            menu.Enter(page);
        }
    }
}
