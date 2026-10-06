using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRecomp.Import;
using MphRecomp.Import.Retro;

namespace MphRecomp.Arenas
{
    // Echoes' multiplayer music in the imported arenas (docs/MP2_MULTIPLAYER_IMPORT.md). The music is streamed from the
    // disc's Audio folder (Retro RS03 streams, Import/Retro/RetroStream.cs: clean room D, docs/cleanroom/) and played
    // the way the arenas' own script objects play it, under MPH's match flow: MphRead's Music offers the room
    // (Music.IRoomMusicHost) when the match starts, its last-minute tempo-up becomes Echoes' one-minute warning, and
    // its stops (time out, quit, the match end) stop the stream. The app's stream player is the output (IOutput).
    // The files are copied from the user's disc into <MPH file system>/_archives/echoes_music/ by the import.
    public sealed class ArenaMusic : Music.IRoomMusicHost
    {
        // one StreamedAudio object of the arenas (all six carry the same ones): the file, the object's name, its volume
        // (0..127) and fade times in seconds
        public sealed record Track(string File, string Name, int Volume, float FadeIn, float FadeOut);

        // what the arenas' "Play Selected Music" relay (SpecialFunction MusicSelectionRelay) starts, in its connection
        // order. Echoes picks one by its multiplayer music choice (MusicChoice / UnlockMusic%d in the game's options);
        // the first, "Multiplayer Default", is the one there from the start.
        public static readonly IReadOnlyList<Track> Selection = new[]
        {
            new Track("multi-defbgm32.dsp", "Multiplayer Default", 83, 0.01f, 0.5f),
            new Track("Moth-Temple-open32-2.dsp", "Moth Temple Open 2", 105, 1.2f, 0.5f),
            new Track("pirates_ato.dsp", "Pirate Encounter Finale", 104, 0.01f, 0.5f),
            new Track("swamp3-32.dsp", "Swamp World", 80, 1.7f, 0.5f),
            new Track("Cliff-vocal32.dsp", "Cliffside Ambient", 95, 0.25f, 0.5f),
            new Track("DarkSamus.dsp", "Dark Samus Encounter", 105, 0.01f, 0.5f),
            new Track("multiplay9-32-3.dsp", "Multiplayer Ambient (Sidehopper)", 90, 0.01f, 0.25f),
        };

        // the arenas' One Minute Warning / Frag Limit Warning sequence timers: at 0 s they fade the selected track out
        // (its own fade-out time), at 1 s they stop it and start this one
        public static readonly Track OneMinute = new("multi-defbgm-speed-loop32.dsp", "Multiplayer One-Minute Looped", 83, 0.01f, 0.25f);
        const float OneMinuteDelay = 1;

        public const string Archive = "echoes_music";

        public static string Folder(string fsRoot) => Path.Combine(fsRoot, "_archives", Archive);

        public static IEnumerable<string> Files => Selection.Select(t => t.File).Append(OneMinute.File).Distinct();

        // the app's stream player
        public interface IOutput
        {
            // plays stream (replacing what plays now, which fades out over oldFadeOut) at gain, after delay seconds,
            // fading in over fadeIn
            void Start(RetroStream stream, float gain, float fadeIn, float delay, float oldFadeOut);
            void Stop(float fadeOut);
        }

        // index into Selection (0 = Echoes' default)
        public static int Choice { get; set; }

        // Echoes' streams against MPH's own music level: the object volume is applied as volume / 127 on top of this
        public static float MixGain { get; set; } = 1;

        static ArenaMusic? _host;

        // the campaign's audio set-up installs its stream player here, next to MusicPlayer.Host
        public static void Attach(IOutput output, Action<string>? log = null)
        {
            _host = new ArenaMusic(output, log);
            Music.RoomHost = _host;
        }

        readonly IOutput _out;
        readonly Action<string>? _log;
        readonly Dictionary<string, RetroStream> _open = new();
        readonly HashSet<string> _missing = new();
        int _room = -1;
        Track? _playing;
        bool _warned;

        ArenaMusic(IOutput output, Action<string>? log)
        {
            _out = output;
            _log = log;
        }

        public bool PlayRoom(int roomId, int track)
        {
            if (!EchoesArena.All.Any(d => d.Id == roomId))
            {
                return false;
            }
            // MPH's mode music changes (Capture, Defender: tracks 1 and 2) keep Echoes' track playing
            if (roomId == _room && _playing != null)
            {
                return true;
            }
            _room = roomId;
            _warned = false;
            Track t = Selection[Math.Clamp(Choice, 0, Selection.Count - 1)];
            Play(t, delay: 0, oldFadeOut: 0);
            return true;
        }

        public void Stop(float fadeTime)
        {
            if (_playing != null)
            {
                _out.Stop(fadeTime);
            }
            _playing = null;
            _room = -1;
        }

        // GameState's multiplayer "less than a minute left" (Music.UpdateTempo(307, 30 s)); 256 is the normal tempo
        public void Tempo(ushort tempo, float time)
        {
            if (_playing == null || _warned || tempo <= 256)
            {
                return;
            }
            _warned = true;
            Play(OneMinute, OneMinuteDelay, _playing.FadeOut);
        }

        void Play(Track t, float delay, float oldFadeOut)
        {
            RetroStream? stream = Open(t.File);
            if (stream == null)
            {
                _out.Stop(oldFadeOut);
                _playing = null;
                return;
            }
            _out.Start(stream, t.Volume / 127f * MixGain, t.FadeIn, delay, oldFadeOut);
            _playing = t;
            _log?.Invoke($"Echoes music: {t.Name} ({t.File}){(delay > 0 ? $" in {delay:0.#} s" : "")}");
        }

        RetroStream? Open(string file)
        {
            if (_open.TryGetValue(file, out RetroStream? s))
            {
                return s;
            }
            string path;
            try
            {
                path = Path.Combine(Folder(Paths.FileSystem), file);
            }
            catch
            {
                return null;
            }
            try
            {
                s = new RetroStream(File.ReadAllBytes(path));
                _open[file] = s;
                return s;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                if (_missing.Add(file))
                {
                    _log?.Invoke($"Echoes music: {path} not usable ({ex.Message}); the arena plays without it");
                }
                return null;
            }
        }

        // copies the arenas' tracks from the user's Echoes disc (Audio/<file>) into the MPH file system; returns how
        // many were written (files already there with the same size are kept)
        public static int Import(IFileSource disc, string fsRoot, Action<string>? log = null)
        {
            string dir = Folder(fsRoot);
            Directory.CreateDirectory(dir);
            int written = 0;
            foreach (string file in Files)
            {
                string src = "Audio/" + file;
                if (!disc.Contains(src))
                {
                    log?.Invoke($"  music: {src} is not on this disc");
                    continue;
                }
                string dst = Path.Combine(dir, file);
                if (File.Exists(dst) && new FileInfo(dst).Length == disc.Length(src))
                {
                    continue;
                }
                byte[] data = disc.ReadFile(src);
                if (!RetroStream.IsRetroStream(data))
                {
                    log?.Invoke($"  music: {src} is not an RS03 stream");
                    continue;
                }
                File.WriteAllBytes(dst, data);
                written++;
            }
            log?.Invoke($"  music: {written} of {Files.Count()} tracks written to {dir}");
            return written;
        }
    }
}
