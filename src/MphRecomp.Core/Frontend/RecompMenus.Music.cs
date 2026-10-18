using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.PrimeMusic;

// The Prime songs' pages (owner 2026-10-14, docs/PRIME_MUSIC.md):
//   - IMPORT GAMES: a game's MUSIC tile (RecompMenus.ImportGamePage.cs) opens its song list: IMPORT ALL, then a row a song
//     (A imports it, its X deletes it), DELETE ALL for every imported song of the game.
//   - RECOMP SETTINGS: match music (ARENA / SHUFFLE / a song), menu music (ORIGINAL / SHUFFLE / a song), the music picker
//     switch (the rules pages' MUSIC row, MultiplayerMenu.MatchMusic.cs) and SONG LIST: which imported songs show in those
//     rows and the shuffles.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private RowsPage? _songRows, _songFilterRows;
        private string _songGame = "mp1";
        private bool _songsReturn; // back from the song list: the game page with the MUSIC tile focused

        // (Install, with the IMPORT pages)
        private void InstallMusicPages(MenuPage controls)
        {
            if (_importGameRows != null)
            {
                _songRows = BuildRowsPage(controls, "MUSIC", _importGameRows.Page, PagingActions(), null,
                    deleteAll: "deletes every song you imported from this game. your disc can import them again.", labelGrow: SongLabelGrow);
                _songRows.Source = SongRows;
                _songRows.DeleteAll = () =>
                {
                    string id = _songGame;
                    List<ImportItemInfo> made = Songs(id).Where(ImportGames.Imported).ToList();
                    return made.Count == 0 ? null : () => Import.Clear(id, made, $"every song from {ImportGames.Row(id).Title.ToLowerInvariant()}");
                };
            }
            _songFilterRows = BuildRowsPage(controls, "SONG LIST", _soundPage?.Page ?? SettingsPage, PagingActions(),
                "no songs imported.\n\nimport games > a game > its music tile\nimports that game's songs from your disc.",
                labelGrow: SongLabelGrow);
            _songFilterRows.Source = SongFilterRows;
        }

        private List<ImportItemInfo> Songs(string gameId) => Import.Game(gameId)?.Items.Where(ImportGames.IsSong).ToList() ?? new();

        // the game page's MUSIC tile
        private void OpenSongList(string gameId)
        {
            if (_songRows == null) return;
            _songGame = gameId;
            _songsReturn = true;
            _stepMenu?.GoTo(_songRows.Page);
        }

        private List<RowModel> SongRows()
        {
            string id = _songGame;
            List<ImportItemInfo> songs = Songs(id);
            var rows = new List<RowModel>();
            if (songs.Count == 0) return rows;
            int done = songs.Count(i => i.Status == ItemImportStatus.Complete);
            rows.Add(new RowModel
            {
                Label = "import\nall",
                Description = $"imports all {songs.Count} songs from your disc. {done} of {songs.Count} imported now. "
                    + "pick them in recomp settings: match music, menu music.",
                Value = () => "START",
                Step = _ => Import.ImportSongs(id),
                Action = true
            });
            foreach (ImportItemInfo item in songs)
            {
                bool made = ImportGames.Imported(item);
                Track? t = TrackCatalog.Find(item.Name);
                string what = t == null ? ImportGames.ItemName(item) : $"{t.Title.ToLowerInvariant()} ({SongWhere(t)})";
                string status = item.Status switch
                {
                    ItemImportStatus.Complete => "imported. a: import it again. x: delete.",
                    ItemImportStatus.Stale => "made an older way. a: import it again. x: delete.",
                    ItemImportStatus.Missing => "a: import just this one.",
                    _ => Import.ItemDescription(id, item),
                };
                string desc = what + ". " + status;
                rows.Add(new RowModel
                {
                    Label = t == null ? ImportGames.ItemLabel(item) : SongLabel(t.Title.ToLowerInvariant()),
                    Description = desc.Length > 130 ? desc[..127] + "..." : desc,
                    Value = () => ImportGames.ItemValue(id, item),
                    Step = _ => Import.ImportOne(id, item),
                    Action = true,
                    Delete = made ? () => Import.Clear(id, new[] { item }, ImportGames.ItemName(item)) : null
                });
            }
            return rows;
        }

        // "prime, chozo ruins" (a song's game and where it plays)
        private static string SongWhere(Track t)
        {
            string game = MusicLibrary.GameName(t.Game).ToLowerInvariant();
            string group = t.Group.ToLowerInvariant();
            return group.Length == 0 || t.Title.StartsWith(t.Group, StringComparison.OrdinalIgnoreCase) ? game : $"{game}, {group}";
        }

        // ---- RECOMP SETTINGS ----

        private List<Track> ShownSongs() => MusicLibrary.Shown(ImportRoot, _settings.HiddenSongs);

        // the next choice from `current`: the game's own, shuffle, then the shown songs
        private string StepMusic(string current, int step)
        {
            var all = new List<string> { "", MusicLibrary.Shuffle };
            all.AddRange(ShownSongs().Select(t => t.Id));
            int at = Math.Max(0, all.IndexOf(current));
            return all[((at + step) % all.Count + all.Count) % all.Count];
        }

        private const float MusicValueWidth = 52; // the value box's text (measured on -fecanvas: "P1 TALLO" fits)

        // the value box (~7 letters, two short lines): SHUFFLE, the game's own (`own`), or the song as "P1 HALL"
        private string MusicValue(string choice, string own)
        {
            if (choice == MusicLibrary.Shuffle) return "SHUFFLE";
            if (MusicLibrary.Find(choice) is not Track t || !TrackCatalog.Usable(ImportRoot, t)) return own;
            string word = MusicLibrary.ShortTitle(t).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(w => w is not ("the" or "vs." or "a")) ?? "";
            word = new string(word.Where(Char.IsLetterOrDigit).Take(5).ToArray()).ToUpperInvariant();
            // a lettered song: its place and letter ("TALLO B"; the description has the game)
            string letter = MusicLibrary.Letter(t).ToUpperInvariant();
            string Value(string w) => letter.Length > 0 ? $"{w} {letter}" : $"P{t.Game[^1]} {w}";
            // the word shortened to the box (wide letters: "P1 PUZZ", not "P1 PUZZL" over its edge)
            while (word.Length > 3 && _drawMenu != null && _drawMenu.Font.Measure(Value(word)) > MusicValueWidth) word = word[..^1];
            return Value(word);
        }

        // the description's words for a choice: "shuffle", the song's full title, or the game's own
        private string MusicWords(string choice, string own)
        {
            if (choice == MusicLibrary.Shuffle) return "shuffle";
            return MusicLibrary.Find(choice) is Track t && TrackCatalog.Usable(ImportRoot, t)
                ? $"{t.Title.ToLowerInvariant()} ({MusicLibrary.GameName(t.Game).ToLowerInvariant()})" : own;
        }

        private List<RowModel> MusicSettingRows()
        {
            int imported = MusicLibrary.Imported(ImportRoot).Count;
            string none = imported == 0 ? " import songs in import games." : "";
            return new List<RowModel>
            {
                new()
                {
                    Label = "match\nmusic",
                    Description = $"your matches: {MusicWords(_settings.MatchMusic, "each arena's own")}. arena: each arena's own music. "
                        + "shuffle: another song each match." + none,
                    Value = () => MusicValue(_settings.MatchMusic, "ARENA"),
                    Step = s => _settings.MatchMusic = StepMusic(_settings.MatchMusic, s)
                },
                new()
                {
                    Label = "menu\nmusic",
                    Description = $"the main menu: {MusicWords(_settings.MenuMusic, "the game's own")}. original: the game's own music. "
                        + "shuffle: another song each time." + none,
                    Value = () => MusicValue(_settings.MenuMusic, "ORIGINAL"),
                    Step = s => _settings.MenuMusic = StepMusic(_settings.MenuMusic, s)
                },
                new()
                {
                    Label = "music\npicker",
                    Description = "on: a small music row under each match's rules, to pick that match's song as you set it up.",
                    Value = () => _settings.MusicPicker ? "ON" : "OFF",
                    Step = _ => _settings.MusicPicker = !_settings.MusicPicker
                },
                new()
                {
                    Label = "song\nlist",
                    Description = imported == 0 ? "the songs you import show here: hide the ones you don't want in the music rows and shuffles."
                        : $"which of your {imported} songs show in the music rows and shuffles. {ShownSongs().Count} shown.",
                    Value = () => "OPEN",
                    Step = _ =>
                    {
                        if (_songFilterRows != null) _stepMenu?.GoTo(_songFilterRows.Page);
                    },
                    Action = true
                },
            };
        }

        // SONG LIST: every imported song, ON (in the rows and the shuffles) or OFF
        private List<RowModel> SongFilterRows() => MusicLibrary.Imported(ImportRoot).Select(t => new RowModel
        {
            Label = SongLabel(MusicLibrary.ShortTitle(t)),
            Description = $"{t.Title.ToLowerInvariant()} ({SongWhere(t)}). on: in the music rows and the shuffles. off: left out.",
            Value = () => _settings.HiddenSongs.Contains(t.Id) ? "OFF" : "ON",
            Step = _ =>
            {
                if (!_settings.HiddenSongs.Remove(t.Id)) _settings.HiddenSongs.Add(t.Id);
            }
        }).ToList();

        // the song lists' label column: the ROM's 64 px label (~11 letters a line: "frigate orpheon music a" took 4 lines and ran
        // into the next row on the Odin, owner 2026-10-16) widened to 128 (-fecanvas 72: still clear of the panel's left
        // edge, and the X box clear of its right)
        private const float SongLabelGrow = 64, SongLabelWidth = 64 + SongLabelGrow;

        // a song's label: one line if it fits the column, else two, split where the wider line is narrowest -- within two
        // letters of that, the most words on the first line ("frigate orpheon / music a", not "frigate / orpheon music a";
        // "metroid prime battle / (exoskeleton)")
        private string SongLabel(string text)
        {
            // the font: the menu of the call that opened or paged the list (else drawn last; else ~6 px a letter)
            float Width(string s) => (_stepMenu ?? _drawMenu)?.Font.Measure(s) ?? s.Length * 6;
            string[] w = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 2 || Width(text) <= SongLabelWidth) return text;
            float[] wide = Enumerable.Range(1, w.Length - 1)
                .Select(i => Math.Max(Width(String.Join(" ", w.Take(i))), Width(String.Join(" ", w.Skip(i))))).ToArray();
            float near = Math.Max(wide.Min(), Math.Min(wide.Min() + 12, SongLabelWidth)); // and never past the column
            int at = Array.FindLastIndex(wide, x => x <= near) + 1;
            return String.Join(" ", w.Take(at)) + "\n" + String.Join(" ", w.Skip(at));
        }
    }
}
