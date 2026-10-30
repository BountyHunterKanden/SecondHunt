using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import;
using MphRecomp.PrimeMusic;

// The Prime songs' pages (owner 2026-10-14, reworked 2026-10-29, docs/PRIME_MUSIC.md):
//   - IMPORT GAMES: a game's MUSIC tile (RecompMenus.ImportGamePage.cs) opens its song list: IMPORT ALL, then a row a song
//     by area (A imports it, its X deletes it), DELETE ALL for every imported song of the game.
//   - SECOND HUNT SETTINGS > SOUND: MATCH MUSIC and MENU MUSIC open the song browser to pick ARENA / ORIGINAL, SHUFFLE or a
//     song; the music picker switch (the rules pages' MUSIC row, MultiplayerMenu.MatchMusic.cs); SONG LIST opens the same
//     browser to turn songs off: a game, an area (MusicCatalog) or one song. An off song never shows in a picker or a shuffle.
//   The browser: games -> a game's areas -> an area's songs, the same three pages for the three uses. Turning off is
//   checkboxes (owner 2026-10-29: not a left / right picker), and each song has a play / pause box to hear it first.
namespace MphRecomp.Frontend
{
    public sealed partial class RecompMenus
    {
        private RowsPage? _songRows, _songFilterRows, _songGameRows, _songAreaRows;
        private string _songGame = "mp1";
        private bool _songsReturn; // back from the song list: the game page with the MUSIC tile focused

        // what the browser is open for: turning songs on and off, or picking the match or menu music
        private enum SongPick { Hide, Match, Menu }
        private SongPick _songPick;
        // opened from a rules page's MUSIC box (owner 2026-10-29: the lobby's picker is this same list): the page to go back to
        private int _songLobbyBack = -1;
        private const int SongsBack = 1017; // SONG LIST's back: SOUND, or that rules page
        private string _browseGame = "mp1", _browseArea = "";

        // the song a song list is playing to hear it (its play / pause box; FrontendRenderer.UpdateMenuMusic plays it in
        // place of the menu music): null = none. Leaving the songs page stops it (OnPageEntered).
        public string? PreviewSong { get; private set; }

        private void PreviewToggle(Track t) => PreviewSong = PreviewSong == t.Id ? null : t.Id;

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
                labelGrow: SongLabelGrow, backCall: SongsBack);
            _songFilterRows.Source = BrowseGameRows;
            _songGameRows = BuildRowsPage(controls, "SONG LIST", _songFilterRows.Page, PagingActions(), "no songs here.", labelGrow: SongLabelGrow);
            _songGameRows.Source = BrowseAreaRows;
            _songAreaRows = BuildRowsPage(controls, "SONG LIST", _songGameRows.Page, PagingActions(), "no songs here.", labelGrow: SongLabelGrow, checkRight: true);
            _songAreaRows.Source = BrowseSongRows;
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
            // the songs by area (Section: the title reads "MUSIC - CHOZO RUINS"), as the browser has them
            List<ImportItemInfo> songs = Songs(id).OrderBy(i => TrackCatalog.Find(i.Name) is Track t ? MusicCatalog.SortKey(t) : int.MaxValue).ToList();
            var rows = new List<RowModel>();
            if (songs.Count == 0) return rows;
            int done = songs.Count(i => i.Status == ItemImportStatus.Complete);
            // IMPORT ALL in the first area's section: with none of its own it was a page by itself and the songs hid behind
            // the page turn (owner 2026-10-30: "you absolutely cannot see and import individual songs")
            string? first = TrackCatalog.Find(songs[0].Name) is Track t0 ? MusicCatalog.Of(t0).Category.ToUpperInvariant() : null;
            rows.Add(new RowModel
            {
                Label = "import\nall",
                Description = $"imports the songs not imported yet from your disc. {done} of {songs.Count} imported now. "
                    + "pick them in settings > sound: match music, menu music.",
                Value = () => "START",
                Step = _ => Import.ImportSongs(id),
                Action = true,
                Section = first
            });
            foreach (ImportItemInfo item in songs)
            {
                bool made = ImportGames.Imported(item);
                Track? t = TrackCatalog.Find(item.Name);
                string status = item.Status switch
                {
                    ItemImportStatus.Complete => "imported. a: again. x: delete.",
                    ItemImportStatus.Stale => "made an older way. a: again. x: delete.",
                    ItemImportStatus.Missing => "a: import just this one.",
                    _ => Import.ItemDescription(id, item),
                };
                string desc = t == null ? ImportGames.ItemName(item) + ". " + status : status + " " + MusicCatalog.Of(t).Where;
                rows.Add(new RowModel
                {
                    Label = t == null ? ImportGames.ItemLabel(item) : SongLabel(MusicCatalog.Of(t).Label),
                    Description = Cut(desc),
                    Value = () => ImportGames.ItemValue(id, item),
                    Step = _ => Import.ImportOne(id, item),
                    Action = true,
                    Delete = made ? () => Import.Clear(id, new[] { item }, ImportGames.ItemName(item)) : null,
                    Section = t == null ? null : MusicCatalog.Of(t).Category.ToUpperInvariant()
                });
            }
            return rows;
        }

        private static string Cut(string desc) => desc.Length > 130 ? desc[..127] + "..." : desc;

        // ---- SECOND HUNT SETTINGS ----

        private List<Track> ShownSongs() => MusicLibrary.Shown(ImportRoot, _settings.HiddenSongs);

        private const float MusicValueWidth = 52; // the value box's text (measured on -fecanvas: "P1 TALLO" fits)

        // the value box (~7 letters, two short lines): SHUFFLE, the game's own (`own`), or the song as "P1 CHOZO"
        private string MusicValue(string choice, string own)
        {
            if (choice == MusicLibrary.Shuffle) return "SHUFFLE";
            if (MusicLibrary.Find(choice) is not Track t || !TrackCatalog.Usable(ImportRoot, t)) return own;
            string word = MusicLibrary.ShortTitle(t).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(w => w is not ("the" or "a" or "g.f.s.")) ?? "";
            word = new string(word.Where(Char.IsLetterOrDigit).Take(5).ToArray()).ToUpperInvariant();
            string Value(string w) => $"P{t.Game[^1]} {w}";
            // the word shortened to the box (wide letters: "P1 PUZZ", not "P1 PUZZL" over its edge)
            while (word.Length > 3 && _drawMenu != null && _drawMenu.Font.Measure(Value(word)) > MusicValueWidth) word = word[..^1];
            return Value(word);
        }

        // the description's words for a choice: "shuffle", the song's name and game, or the game's own
        private string MusicWords(string choice, string own)
        {
            if (choice == MusicLibrary.Shuffle) return "shuffle";
            return MusicLibrary.Find(choice) is Track t && TrackCatalog.Usable(ImportRoot, t)
                ? $"{MusicLibrary.ShortTitle(t)} ({MusicLibrary.GameName(t.Game).ToLowerInvariant()})" : own;
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
                    Description = $"your matches: {MusicWords(_settings.MatchMusic, "each arena's own")}. a: pick arena music, shuffle or a song."
                        + none,
                    Value = () => MusicValue(_settings.MatchMusic, "ARENA"),
                    Step = _ => OpenSongBrowser(SongPick.Match),
                    Action = true
                },
                new()
                {
                    Label = "menu\nmusic",
                    Description = $"the main menu: {MusicWords(_settings.MenuMusic, "the game's own")}. a: pick its own music, shuffle or a song."
                        + none,
                    Value = () => MusicValue(_settings.MenuMusic, "ORIGINAL"),
                    Step = _ => OpenSongBrowser(SongPick.Menu),
                    Action = true
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
                    Description = imported == 0 ? "the songs you import show here: turn off the ones you never want in the pickers and shuffles."
                        : $"turn off songs, areas or games you never want in the pickers and shuffles. {ShownSongs().Count} of {imported} on.",
                    Value = () => "OPEN",
                    Step = _ => OpenSongBrowser(SongPick.Hide),
                    Action = true
                },
            };
        }

        // ---- the song browser: games, a game's areas, an area's songs ----

        private void OpenSongBrowser(SongPick pick)
        {
            if (_songFilterRows == null) return;
            _songPick = pick;
            _songLobbyBack = -1;
            _songFilterRows.Title = pick switch { SongPick.Match => "MATCH MUSIC", SongPick.Menu => "MENU MUSIC", _ => "SONG LIST" };
            _stepMenu?.GoTo(_songFilterRows.Page);
        }

        private string Choice => _songPick == SongPick.Menu ? _settings.MenuMusic : _settings.MatchMusic;

        // the pick: saved, and back to SOUND (its rows read it as it comes in)
        private void Pick(string choice)
        {
            if (_songPick == SongPick.Menu) _settings.MenuMusic = choice;
            else _settings.MatchMusic = choice;
            _settings.Save(_settingsPath);
            SongsBackTo(_stepMenu);
        }

        // a rules page's MUSIC box (MultiplayerMenu.OpenSongs): MATCH MUSIC's list, picked or backed out of to that page
        public void OpenLobbySongs(MenuEngine menu)
        {
            if (_songFilterRows == null || menu.Page == null) return;
            _stepMenu = menu;
            _songPick = SongPick.Match;
            _songLobbyBack = menu.Page.Index;
            _songFilterRows.Title = "MATCH MUSIC";
            menu.GoTo(_songFilterRows.Page);
        }

        private void SongsBackTo(MenuEngine? menu)
        {
            int back = _songLobbyBack >= 0 ? _songLobbyBack : _soundPage?.Page ?? SettingsPage;
            _songLobbyBack = -1;
            menu?.GoTo(back);
        }

        // the song pages opened from a rules page (ONLINE PLAY's hosted game stays up there: OnlineMenus.EnterOnline)
        private bool LobbySongsPage(int page) => _songLobbyBack >= 0
            && (page == _songFilterRows?.Page || page == _songGameRows?.Page || page == _songAreaRows?.Page);

        // the songs a page lists: every imported one to turn off, the ones still on to pick from
        private List<Track> BrowseSongs() => _songPick == SongPick.Hide ? MusicLibrary.Imported(ImportRoot) : ShownSongs();

        private string OnCount(IEnumerable<Track> songs)
        {
            List<Track> list = songs.ToList();
            return $"{list.Count(t => !_settings.HiddenSongs.Contains(t.Id))}/{list.Count}";
        }

        // ON, OFF or SOME for a group, and its switch: anything on turns it all off, else all on (every song of the
        // group, imported or not, so one imported later keeps the group's choice)
        private string GroupValue(List<Track> songs)
        {
            int on = songs.Count(t => !_settings.HiddenSongs.Contains(t.Id));
            return on == songs.Count ? "ON" : on == 0 ? "OFF" : "SOME";
        }

        private void GroupStep(List<Track> songs, IEnumerable<string> ids)
        {
            bool anyOn = songs.Any(t => !_settings.HiddenSongs.Contains(t.Id));
            foreach (string id in ids.ToList()) SetHidden(id, anyOn);
        }

        // one song on or off. Never a repeat in HiddenSongs: a group switch used to add a song already hidden again, and
        // one Remove then left it hidden (owner 2026-10-29, "toggling the all ... breaks the rest of the checkmarks");
        // a repeat saved by that build is cleared by the RemoveAll
        private void SetHidden(string id, bool hidden)
        {
            _settings.HiddenSongs.RemoveAll(x => x == id);
            if (hidden) _settings.HiddenSongs.Add(id);
        }

        // the first page: (picking) the game's own music and shuffle, then a row a game
        private List<RowModel> BrowseGameRows()
        {
            var rows = new List<RowModel>();
            List<Track> songs = BrowseSongs();
            if (_songPick != SongPick.Hide)
            {
                bool menu = _songPick == SongPick.Menu;
                string none = MusicLibrary.Imported(ImportRoot).Count == 0 ? " import songs in import games to pick one." : "";
                rows.Add(new RowModel
                {
                    Label = menu ? "original\nmusic" : "arena\nmusic",
                    Description = (menu ? "the main menu plays its own music." : "each arena plays its own music.") + none,
                    Value = () => Choice == "" || MusicLibrary.Find(Choice) == null && Choice != MusicLibrary.Shuffle ? "PICKED" : "PICK",
                    Step = _ => Pick(""),
                    Action = true
                });
                rows.Add(new RowModel
                {
                    Label = "shuffle",
                    Description = (menu ? "another song each time" : "another song each match") + ", from the songs you have on in song list.",
                    Value = () => Choice == MusicLibrary.Shuffle ? "PICKED" : "PICK",
                    Step = _ => Pick(MusicLibrary.Shuffle),
                    Disabled = () => ShownSongs().Count == 0,
                    Action = true
                });
            }
            foreach (string game in MusicCatalog.Games)
            {
                List<Track> of = songs.Where(t => t.Game == game).ToList();
                if (of.Count == 0) continue;
                string name = GameLongName(game);
                rows.Add(new RowModel
                {
                    Label = SongLabel(name),
                    Description = _songPick == SongPick.Hide
                        ? $"{of.Count} songs {(game == HuntersMusic.Kind ? "from your " + name + " rom" : "imported from " + name)}, {OnCount(of).Split('/')[0]} on. a: go through them by area."
                        : $"{of.Count} songs from {name}. a: go through them by area."
                            + (MusicLibrary.Find(Choice) is Track p && p.Game == game ? $" picked now: {MusicLibrary.ShortTitle(p)}." : ""),
                    Value = () => _songPick == SongPick.Hide ? OnCount(of) : MusicLibrary.Find(Choice)?.Game == game ? "PICKED" : "OPEN",
                    Step = _ => OpenBrowseGame(game),
                    Action = true
                });
            }
            return rows;
        }

        private void OpenBrowseGame(string game)
        {
            if (_songGameRows == null) return;
            _browseGame = game;
            _songGameRows.Title = MusicLibrary.GameName(game);
            _stepMenu?.GoTo(_songGameRows.Page);
        }

        // a game's page: (turning off) the whole game, then a row an area
        private List<RowModel> BrowseAreaRows()
        {
            var rows = new List<RowModel>();
            string game = _browseGame, name = GameLongName(game);
            List<Track> songs = BrowseSongs().Where(t => t.Game == game).ToList();
            if (songs.Count == 0) return rows;
            if (_songPick == SongPick.Hide)
                rows.Add(new RowModel
                {
                    Label = "all\nsongs",
                    Description = $"every song from {name} at once. off: none of them in the pickers or shuffles"
                        + (game == HuntersMusic.Kind ? "." : ", songs you import later too."),
                    Value = () => GroupValue(songs),
                    Step = _ => GroupStep(songs, MusicCatalog.Ids(game)),
                    Check = () => GroupValue(songs) == "ON",
                    Partly = () => GroupValue(songs) == "SOME",
                    Action = true
                });
            foreach (string area in MusicCatalog.Categories(game))
            {
                List<Track> of = songs.Where(t => MusicCatalog.Of(t).Category == area).ToList();
                if (of.Count == 0) continue;
                rows.Add(new RowModel
                {
                    Label = SongLabel(area),
                    Description = AreaWords(game, area) + (_songPick == SongPick.Hide
                        ? $" {of.Count} songs, {OnCount(of).Split('/')[0]} on. a: open."
                        : $" {of.Count} songs. a: open."),
                    Value = () => _songPick == SongPick.Hide ? OnCount(of) : of.Any(t => t.Id == Choice) ? "PICKED" : "OPEN",
                    Step = _ => OpenBrowseArea(area),
                    Action = true
                });
            }
            return rows;
        }

        private void OpenBrowseArea(string area)
        {
            if (_songAreaRows == null) return;
            _browseArea = area;
            _songAreaRows.Title = area.ToUpperInvariant();
            _stepMenu?.GoTo(_songAreaRows.Page);
        }

        // an area's page: (turning off) the whole area, then a row a song
        private List<RowModel> BrowseSongRows()
        {
            var rows = new List<RowModel>();
            string game = _browseGame, area = _browseArea;
            List<Track> songs = BrowseSongs().Where(t => t.Game == game && MusicCatalog.Of(t).Category == area).ToList();
            if (songs.Count == 0) return rows;
            if (_songPick == SongPick.Hide && songs.Count > 1)
                rows.Add(new RowModel
                {
                    Label = "all\nsongs",
                    Description = $"every {area} song at once. off: none of them in the pickers or shuffles, songs you import later too.",
                    Value = () => GroupValue(songs),
                    Step = _ => GroupStep(songs, MusicCatalog.Ids(game, area)),
                    Check = () => GroupValue(songs) == "ON",
                    Partly = () => GroupValue(songs) == "SOME",
                    Action = true
                });
            foreach (Track t in songs)
            {
                MusicCatalog.Entry e = MusicCatalog.Of(t);
                rows.Add(_songPick == SongPick.Hide
                    ? new RowModel
                    {
                        Label = SongLabel(e.Label),
                        Description = Cut($"{e.Where} a: on or off. x: play."),
                        Value = () => _settings.HiddenSongs.Contains(t.Id) ? "OFF" : "ON",
                        Step = _ =>
                        {
                            SetHidden(t.Id, !_settings.HiddenSongs.Contains(t.Id));
                        },
                        Check = () => !_settings.HiddenSongs.Contains(t.Id),
                        Action = true,
                        Play = () => PreviewToggle(t),
                        Playing = () => PreviewSong == t.Id
                    }
                    : new RowModel
                    {
                        Label = SongLabel(e.Label),
                        Description = Cut($"{e.Where} a: pick it. x: play."),
                        Value = () => Choice == t.Id ? "PICKED" : "PICK",
                        Step = _ => Pick(t.Id),
                        Action = true,
                        Play = () => PreviewToggle(t),
                        Playing = () => PreviewSong == t.Id
                    });
            }
            return rows;
        }

        // the rows pages' drawn marks (DrawOptions), owner 2026-10-29: a RowModel.Check row's value box narrowed to a square
        // (its own ROM box, focus look and all) with a tick in it when on, or a dash when only some of a group is on; and
        // a play triangle or pause bars in a RowModel.Play row's X box. On the songs page (CheckRight) the checkbox goes to
        // the X box's place and the play box just left of it. Marks are tagged with their box's item, so they move and hide
        // with it.
        private void DrawRowMarks(MenuEngine menu, List<WidgetTri> tris)
        {
            if (menu.Page == null || RowsFor(menu.Page.Index) is not RowsPage p) return;
            int shown = Math.Min(RowsShown(p), RowsPerPage);
            if (!p.Rows.Skip(p.Offset).Take(shown).Any(m => m.Check != null || m.Play != null)) return;
            _drawMenu = menu;
            _out = tris;
            // a checkbox doesn't pulse (owner 2026-10-29): its box is drawn from its look's first frame, not the focus loop
            for (int r = 0; r < shown; r++)
            {
                if (p.Rows[p.Offset + r].Check != null) Steady(menu, p.Box[r], tris);
            }
            var bounds = new Dictionary<int, (float X0, float Y0, float X1, float Y1)>();
            for (int r = 0; r < shown; r++)
            {
                bounds[p.Box[r]] = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                bounds[p.Del[r]] = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            }
            int count = tris.Count;
            for (int i = 0; i < count; i++)
            {
                WidgetTri tri = tris[i];
                if (!bounds.TryGetValue(tri.Item, out var b)) continue;
                bounds[tri.Item] = (MathF.Min(b.X0, MinX(tri)), MathF.Min(b.Y0, MinY(tri)), MathF.Max(b.X1, MaxX(tri)), MathF.Max(b.Y1, MaxY(tri)));
            }
            bool Centre(int item, out float cx, out float cy)
            {
                var b = bounds[item];
                cx = (b.X0 + b.X1) / 2;
                cy = (b.Y0 + b.Y1) / 2;
                return b.X1 >= b.X0; // (no triangles: hidden)
            }
            // an item's triangles: each corner pulled `squeeze` towards x = cx, then all moved by dx
            void Reshape(int item, float cx, float squeeze, float dx)
            {
                static float X(float x, float cx, float s, float dx) => (x > cx + 0.01f ? x - s : x < cx - 0.01f ? x + s : x) + dx;
                for (int i = 0; i < count; i++)
                {
                    WidgetTri tri = tris[i];
                    if (tri.Item != item) continue;
                    tri.A.X = X(tri.A.X, cx, squeeze, dx);
                    tri.B.X = X(tri.B.X, cx, squeeze, dx);
                    tri.C.X = X(tri.C.X, cx, squeeze, dx);
                    tris[i] = tri;
                }
            }
            void Line(float x0, float y0, float x1, float y1, float w, float a, int tag)
            {
                float lx = x1 - x0, ly = y1 - y0, len = MathF.Sqrt(lx * lx + ly * ly);
                float nx = -ly / len * w / 2, ny = lx / len * w / 2;
                x0 -= lx / len * w / 4; y0 -= ly / len * w / 4; // a little past each end, so the tick's corner closes
                x1 += lx / len * w / 4; y1 += ly / len * w / 4;
                var c0 = new UiVertex(x0 + nx, y0 + ny, 0, 0, White.R, White.G, White.B, a);
                var c1 = new UiVertex(x1 + nx, y1 + ny, 0, 0, White.R, White.G, White.B, a);
                var c2 = new UiVertex(x1 - nx, y1 - ny, 0, 0, White.R, White.G, White.B, a);
                var c3 = new UiVertex(x0 - nx, y0 - ny, 0, 0, White.R, White.G, White.B, a);
                _out.Add(new WidgetTri { A = c0, B = c1, C = c2, TextureId = -1, Item = tag });
                _out.Add(new WidgetTri { A = c0, B = c2, C = c3, TextureId = -1, Item = tag });
            }
            // the play mark green, the pause bars amber, so a playing song stands out (owner 2026-10-29)
            (float R, float G, float B) Play = (0.35f, 1f, 0.45f), Pause = (1f, 0.65f, 0.2f);
            const float playGap = 22; // the play box's centre, left of the checkbox's (16 wide + 6)
            for (int r = 0; r < shown; r++)
            {
                RowModel m = p.Rows[p.Offset + r];
                float alpha = m.Disabled?.Invoke() == true ? 0.4f : 1;
                if (m.Check != null && Centre(p.Box[r], out float bx, out float by))
                {
                    float dx = p.CheckRight ? p.CheckDx : 0;
                    Reshape(p.Box[r], bx, p.Squeeze, dx);
                    bx += dx;
                    int tag = p.Box[r];
                    if (m.Check())
                    {
                        Line(bx - 3.6f, by + 0.2f, bx - 1.1f, by - 2.8f, 2, alpha, tag);
                        Line(bx - 1.1f, by - 2.8f, bx + 3.8f, by + 3.6f, 2, alpha, tag);
                    }
                    else if (m.Partly?.Invoke() == true)
                        Fill(bx - 3.5f, by + 1, bx + 3.5f, by - 1, White.R, White.G, White.B, alpha, tag);
                }
                if (m.Play != null && Centre(p.Del[r], out float px, out float py))
                {
                    int tag = p.Del[r];
                    if (p.CheckRight && m.Check != null)
                    {
                        Reshape(tag, px, 0, -playGap);
                        px -= playGap;
                    }
                    if (m.Playing?.Invoke() == true)
                    {
                        Fill(px - 3.5f, py + 4, px - 1, py - 4, Pause.R, Pause.G, Pause.B, alpha, tag);
                        Fill(px + 1, py + 4, px + 3.5f, py - 4, Pause.R, Pause.G, Pause.B, alpha, tag);
                    }
                    else
                        _out.Add(new WidgetTri
                        {
                            A = new UiVertex(px - 3, py + 4.5f, 0, 0, Play.R, Play.G, Play.B, alpha),
                            B = new UiVertex(px + 4.5f, py, 0, 0, Play.R, Play.G, Play.B, alpha),
                            C = new UiVertex(px - 3, py - 4.5f, 0, 0, Play.R, Play.G, Play.B, alpha),
                            TextureId = -1,
                            Item = tag
                        });
                }
            }
        }

        // an item's triangles in `tris` swapped for its current look at frame 0 (a still), placed as MenuEngine.Collect
        // places them; left alone mid-transition (a fade in or out plays as the ROM has it)
        private static void Steady(MenuEngine menu, int item, List<WidgetTri> tris)
        {
            int code = menu.ItemCode(item);
            if (MenuStateCode.IsTransition(code)) return;
            if (menu.ItemState(item) == MenuState.Hidden) return;
            int at = tris.FindIndex(x => x.Item == item);
            if (at < 0) at = tris.Count; // (drawn nothing this frame: the focus pulse at its faded end)
            MenuItem it = menu.Page!.Items[item];
            MenuItemState? look = it.GetState(code);
            if (look == null || look.WidgetIndex < 0) return;
            var still = new List<WidgetTri>();
            menu.Widgets.Evaluate(look.WidgetIndex, BrightestFrame(menu, look.WidgetIndex), loop: true, alpha: 1, still);
            if (it.StretchX != 0 || it.StretchDown != 0) MenuEngine.Stretch(still, 0, it.StretchX, it.StretchDown);
            for (int i = 0; i < still.Count; i++)
            {
                WidgetTri tri = still[i];
                tri.A.X += it.X; tri.B.X += it.X; tri.C.X += it.X;
                tri.A.Y += it.Y; tri.B.Y += it.Y; tri.C.Y += it.Y;
                tri.Z += it.Depth;
                still[i] = tri;
            }
            menu.TransformTris(item, still, 0);
            for (int i = 0; i < still.Count; i++)
            {
                WidgetTri tri = still[i];
                tri.Item = item;
                still[i] = tri;
            }
            tris.RemoveAll(x => x.Item == item);
            tris.InsertRange(Math.Min(at, tris.Count), still);
        }

        // a look's frame drawn most solidly: the focus look's pulse fades its box right out (frame 0 drew nothing, so the
        // focused checkbox -- "all songs" as a page opens, a tapped song -- lost its box and its tick, owner 2026-10-29)
        private static readonly Dictionary<(object, int), int> s_brightest = new(); // (by menu file: indexes are its own)

        private static int BrightestFrame(MenuEngine menu, int widget)
        {
            if (s_brightest.TryGetValue((menu.Widgets, widget), out int best)) return best;
            float most = -1;
            var frame = new List<WidgetTri>();
            for (int f = 0, n = Math.Min(menu.Widgets.FrameCount(widget), 240); f < n; f++)
            {
                frame.Clear();
                menu.Widgets.Evaluate(widget, f, loop: true, alpha: 1, frame);
                float sum = frame.Sum(x => x.A.A + x.B.A + x.C.A);
                if (sum > most + 0.001f) { most = sum; best = f; }
            }
            return s_brightest[(menu.Widgets, widget)] = best;
        }

        private static string GameLongName(string game) => game switch
        {
            "mph" => "metroid prime hunters",
            "mp1" => "metroid prime",
            "mp2" => "prime 2 echoes",
            "mp3" => "prime 3 corruption",
            _ => game,
        };

        // what an area's songs are
        private static string AreaWords(string game, string area) => area switch
        {
            MusicCatalog.Battles => $"fights heard all over {GameLongName(game)}.",
            MusicCatalog.MenusEndings => game == HuntersMusic.Kind ? "the intro, menus, results and credits." : "the title screen, menus, ending and credits.",
            MusicCatalog.Other => $"songs heard in many places in {GameLongName(game)}.",
            "multiplayer" => game == HuntersMusic.Kind ? "the multiplayer arenas' own themes." : "echoes' own multiplayer matches.",
            _ => $"songs heard only in {area}, its bosses too.",
        };

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
