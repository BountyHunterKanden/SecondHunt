using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.PrimeMusic
{
    // The songs a player can pick for matches and the menus (docs/PRIME_MUSIC.md): every Prime song imported from the
    // user's own discs (TrackCatalog), less the ones hidden in RECOMP SETTINGS. A choice is "" (the game's own music),
    // "shuffle" (another shown song each time) or a song's id ("tracks:MP1PhendranaDrifts"); a song no longer imported
    // reads as "".
    public static class MusicLibrary
    {
        public const string Shuffle = "shuffle";

        // the game names the lists show
        public static string GameName(string game) => game switch
        {
            "mp1" => "PRIME",
            "mp2" => "ECHOES",
            "mp3" => "CORRUPTION",
            _ => game.ToUpperInvariant(),
        };

        // the Prime songs aren't in the next release (owner 2026-10-17): a public build has none, so
        // the menus and matches keep the game's own music; dev builds have them
        public static bool On => !MphRecomp.Config.RecompSettings.PublicBuild;

        // every imported song, in catalog order
        public static List<Track> Imported(string? importRoot) =>
            importRoot == null || !On ? new List<Track>() : TrackCatalog.All.Where(t => TrackCatalog.Usable(importRoot, t)).ToList();

        // the imported songs the player keeps in the lists and shuffles
        public static List<Track> Shown(string? importRoot, ICollection<string>? hidden) =>
            Imported(importRoot).Where(t => hidden == null || !hidden.Contains(t.Id)).ToList();

        // a title short enough for a small box, in the menus' lower case (their small capitals): a room's own name after
        // its area's ("Chozo Ruins - Hall of the Elders" -> "hall of the elders")
        public static string ShortTitle(Track t)
        {
            string title = t.Title;
            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) title = title[(dash + 3)..];
            return title.ToLowerInvariant();
        }

        // a named-by-place song's letter ("Tallon Overworld Music B" -> "b"), else ""
        public static string Letter(Track t)
        {
            string title = ShortTitle(t);
            int at = title.LastIndexOf(" music ", StringComparison.Ordinal);
            return at > 0 && at + 8 == title.Length ? title[^1..] : "";
        }

        // the pickers' short name: a lettered song without its "music" ("tallon overworld b")
        public static string PickerTitle(Track t)
        {
            string title = ShortTitle(t), letter = Letter(t);
            return letter.Length == 0 ? title : title[..title.LastIndexOf(" music ", StringComparison.Ordinal)] + " " + letter;
        }

        public static Track? Find(string id) =>
            id.StartsWith(TrackCatalog.Kind + ":", StringComparison.Ordinal) ? TrackCatalog.Find(id[(TrackCatalog.Kind.Length + 1)..]) : null;

        // the song a choice plays, or null for the game's own music; a shuffle avoids the last song when it can
        public static Track? Resolve(string? choice, string? importRoot, ICollection<string>? hidden, string? last, Random rng)
        {
            if (String.IsNullOrEmpty(choice) || importRoot == null || !On) return null;
            if (choice == Shuffle)
            {
                List<Track> shown = Shown(importRoot, hidden);
                if (shown.Count > 1) shown.RemoveAll(t => t.Id == last);
                return shown.Count == 0 ? null : shown[rng.Next(shown.Count)];
            }
            Track? t = Find(choice);
            return t != null && TrackCatalog.Usable(importRoot, t) ? t : null;
        }
    }

    // A match's music (docs/PRIME_MUSIC.md): MphRead's room-music hook (Music.IRoomMusicHost) in front of Echoes' arena
    // music, so the song the player picked plays in any arena, MPH's own or Echoes'. Outside matches, and with the
    // arena's own music chosen, the room goes on to Echoes' host or MPH's tables as before.
    public sealed class MatchMusic : MphRead.Music.IRoomMusicHost
    {
        // the player's choice for the next match ("" / "shuffle" / a song id), the hidden songs and the import root,
        // set by the app's audio set-up from RECOMP SETTINGS
        public static string Choice { get; set; } = "";
        public static ICollection<string>? Hidden { get; set; }
        public static string? ImportRoot { get; set; }

        static readonly Random s_rng = new();
        static string? s_last;   // the last shuffled song, so the next match plays another

        readonly IMusicOutput _out;
        readonly MphRead.Music.IRoomMusicHost? _inner;
        readonly Action<string>? _log;
        int _room = -1;
        Track? _playing;

        MatchMusic(IMusicOutput output, MphRead.Music.IRoomMusicHost? inner, Action<string>? log)
        {
            _out = output;
            _inner = inner;
            _log = log;
        }

        // after ArenaMusic.Attach: wraps whatever room host is there
        public static void Attach(IMusicOutput output, Action<string>? log = null)
        {
            MphRead.Music.RoomHost = new MatchMusic(output, MphRead.Music.RoomHost is MatchMusic m ? m._inner : MphRead.Music.RoomHost, log);
        }

        public bool PlayRoom(int roomId, int track)
        {
            if (MphRead.GameState.Multiplayer)
            {
                // the mode's music changes (Capture, Defender: tracks 1 and 2) keep the song playing
                if (roomId == _room && _playing != null) return true;
                Track? song = MusicLibrary.Resolve(Choice, ImportRoot, Hidden, s_last, s_rng);
                if (song != null && Play(song))
                {
                    _room = roomId;
                    if (Choice == MusicLibrary.Shuffle) s_last = song.Id;
                    return true;
                }
            }
            if (_playing != null) Stop(0);
            return _inner?.PlayRoom(roomId, track) == true;
        }

        bool Play(Track song)
        {
            try
            {
                IMusicStream stream = TrackCatalog.Open(ImportRoot!, song);
                _out.Start(stream, song.Gain, fadeIn: 0.01f, delay: 0, oldFadeOut: 0, repeat: true);
                _playing = song;
                _log?.Invoke($"match music: {song.Title} ({MusicLibrary.GameName(song.Game)}, {song.Id})");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"match music: {song.Id} won't open ({ex.Message}); the arena's own plays");
                return false;
            }
        }

        public void Stop(float fadeTime)
        {
            if (_playing != null)
            {
                _out.Stop(fadeTime);
                _playing = null;
                _room = -1;
            }
            _inner?.Stop(fadeTime);
        }

        // the last-minute tempo-up: a picked song carries on as it is
        public void Tempo(ushort tempo, float time)
        {
            if (_playing == null) _inner?.Tempo(tempo, time);
        }
    }
}
