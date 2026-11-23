using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Holiday
{
    // HOLIDAY MODE's sleigh bells (owner 2026-11-20; plan and the owner's per-song picks: docs/HOLIDAY_BELLS.md). A bell
    // one-shot played on the song's own beat grid, mixed into the music under the music volume. Bells only play while
    // holiday mode is on and the player is outdoors (not under the falling snow's roof map), fading over a second; a
    // room with no open sky anywhere a player can stand keeps them on (owner: "for maps that are only indoor and dont
    // have any snow at all leave the sleigh bells in"). Cosmetic and local: nothing here touches the sim, netcode or
    // replays.
    //
    // Grids: an MPH song (SSEQ) counts sequence ticks from tick 0 (48 a quarter note, so tempo changes carry the bells
    // along; App GameAudio's overlay); a stream counts stream time from sample 0 with the beat-1 anchor t0 (the reader's
    // Position, which jumps back to the loop start, so every loop lands on the same grid; App StreamMusicPlayer).
    public static class HolidayBells
    {
        // StartTicks (SSEQ only): no bells before this sequence tick, for a song that opens on a long held note
        public sealed record Song(string Pattern, int Bpb, double Bpm, double T0, double NudgeMs, long StartTicks = 0);

        // the owner's picks (artifact picker, db `picks`), by MPH SeqId or by the stream's first file name
        static readonly Dictionary<int, Song> Seqs = new()
        {
            [1] = new("13", 4, 128, 0, 0), // Alinos (MP1)
            [2] = new("b1", 4, 140, 0, 0), // Celestial Archives (MP2)
            [44] = new("e", 3, 67.966, 0, 0), // Vesper Defense Outpost (DILL)
            [48] = new("24", 4, 99.95, 0, 0), // Arcterra (PEPPER)
            [47] = new("q", 4, 156, 0, 0), // Oubliette (GOREA_2)
            // campaign hunter fights (owner 2026-11-22, same pick for all six): one song, each hunter only switches on
            // its own track (Music.PlayEncounterMusic), so the ticks run on through the whole encounter. It opens on a
            // 2-bar held note; the bells wait for the beat at bar 3
            [20] = new("24", 4, 140, 0, 0, StartTicks: 2 * 4 * TicksPerBeat), // Kanden/Spire/Weavel/Sylux/Noxus/Trace (GUMBO)
            [40] = new("b1", 4, 150, 0, 0), // Guardians (GUARDIAN)
        };

        static readonly Dictionary<string, Song> Streams = new()
        {
            ["multi-defbgm32"] = new("q", 4, 148, 1.2432, 0),
            ["multiplay9-32-3"] = new("b1", 4, 140, 1.4286, 0),
            ["multi-defbgm-speed-loop32"] = new("q", 4, 167.916, 0.8586, 0),
            ["moth-temple-open32-2"] = new("24", 4, 90, 0.6667, 0),
            ["swamp3-32"] = new("b1", 4, 140, 1.4286, 0),
            ["cliff-vocal32"] = new("24", 4, 115.0065, 0.052, 0),
            ["darksamus"] = new("24", 4, 125, 1.84, 0),
            ["min_mainl"] = new("q", 4, 74.0003, 0, 0),
            ["lav_lavamainl"] = new("q", 4, 130.0057, 0.012, 0),
            ["ice_worldmainl"] = new("half", 4, 120, 1.975, 0),
            ["gen_shortbattlel"] = new("24", 4, 148, 1.2432, 0),
            ["rui_flaaghral"] = new("d2", 4, 159, 1.0189, 0),
            ["rui_hivetoteml"] = new("24", 4, 126, 1.8095, 0),
            ["rui_monkeylowerl"] = new("24", 4, 126, 1.8095, 0),
            ["cra_mprime1l"] = new("b4", 4, 136, 1.5294, 0),
            ["min_omegapiratel"] = new("13", 4, 96, 0.5, 0),
            ["int_parasitequeenl"] = new("q", 4, 110, 0.1818, 0),
            ["ice_kincyol"] = new("24", 4, 119.94, 0.001, 0),
            ["pir_isogil"] = new("b1", 4, 149.925, 1.2016, 0),
            ["ice_thardusl"] = new("q", 4, 168, 0.8571, 0),
            ["sand-boss32"] = new("24", 4, 120, 0, 0),
            ["swanp-boss-type1-32"] = new("13", 4, 120, 0, 0),
            ["swanp-boss-type2-32"] = new("24", 4, 149.925, 1.2016, 0),
            ["zonbihei32-2"] = new("b1", 4, 137, 1.5036, 0),
            ["enp-ing-type1-3-32"] = new("q", 3, 76.002, 0, 0),
            ["enp-ing-type2-3-32"] = new("24", 4, 128, 1.75, 0),
            ["ing_large"] = new("24", 4, 133, 1.609, 0),
            ["cliff-bos2-32"] = new("13", 4, 111, 0.1622, 0),
            ["boss_auroraunit"] = new("24", 4, 130, 1.6923, 0),
            ["hi-fight"] = new("13", 4, 152.847, 1.1404, 0),
            ["boss_berzerkerboss"] = new("13", 4, 170, 0.1176, 0),
            ["mp3_darksamus"] = new("e", 4, 125, 1.84, 0),
            ["boss_bryyoseedboss"] = new("24", 4, 149.925, 1.2016, 0),
            ["boss_m_ridley"] = new("13", 4, 100, 0.4, 0),
            ["rundas_fight"] = new("24", 4, 128.064, 1.7481, 0),
            ["space_pirates"] = new("q", 4, 150.075, 1.1984, 0),
            ["gandrayda-fight"] = new("13", 4, 150, 1.2, 30),
            // picked "none": pirates_ato, gen_shortbattle2l, cra_mprime2l, pir_zencyol, pir_battle3l, over-ridleyl,
            // int_escapel, end-escapel, short-bat-32, escape3-1a-32, pirates_kyu-3-32, pirates_zencyo, aurora_fight2,
            // zako, mid-fight_lp_eq, boss_skytownghor, boss_skyseedboss, reptilian2-japan
        };

        public const int TicksPerBeat = 48; // SSEQ: a quarter note
        const float BellGain = 0.3f; // a weight-1 hit's peak, under the music
        const float FadeSeconds = 1f;

        public static Song? ForSeq(int seqId) => Seqs.GetValueOrDefault(seqId);

        // ---- which stream is which song ----

        static readonly ConditionalWeakTable<object, Song> _tags = new();

        // remembers the song a stream belongs to (file: its first file, any folder and extension)
        public static void Tag(object? stream, string file)
        {
            if (stream == null) return;
            string key = Path.GetFileNameWithoutExtension(file.Replace('\\', '/')).ToLowerInvariant();
            if (Streams.TryGetValue(key, out Song? song)) _tags.AddOrUpdate(stream, song);
        }

        public static Song? For(object? stream) => stream != null && _tags.TryGetValue(stream, out Song? song) ? song : null;

        // ---- the beat patterns (the picker's hits(): beat in the bar, 0 = beat 1, and a weight) ----

        static bool Backbeat(int beat, int bpb) => bpb == 3 ? beat > 0 : (beat & 1) == 1;

        public static void Hits(Song song, long bar, List<(double Beat, float Weight)> into)
        {
            into.Clear();
            int bpb = song.Bpb;
            switch (song.Pattern)
            {
                case "24":
                    if (bpb == 3) { into.Add((1, 1)); into.Add((2, 1)); }
                    else { into.Add((1, 1)); into.Add((3, 1)); }
                    break;
                case "13": into.Add((0, 1)); into.Add((2, 1)); break;
                case "q":
                    for (int b = 0; b < bpb; b++) into.Add((b, Backbeat(b, bpb) ? 1f : 0.55f));
                    break;
                case "e":
                    for (int b = 0; b < bpb; b++)
                    {
                        into.Add((b, b == 0 ? 1f : 0.5f));
                        into.Add((b + 0.5, 0.4f));
                    }
                    break;
                case "b1": into.Add((0, 1)); break;
                case "b4": into.Add((bpb - 1, 1)); break;
                case "d2":
                    if ((bar & 1) == 0) into.Add((0, 1));
                    break;
                case "332": into.Add((0, 1)); into.Add((1.5, 0.8f)); into.Add((3, 0.9f)); break;
                case "half": into.Add((bpb == 3 ? 0 : 2, 1)); break;
                case "sec":
                    if (((bar >> 2) & 1) == 1)
                        for (int b = 0; b < bpb; b++)
                            if (Backbeat(b, bpb)) into.Add((b, 1));
                    break;
            }
        }

        // ---- on or off: holiday mode and outdoors (game thread) ----

        static Action<Scene>? _inner;
        static readonly Action<Scene> _chained = Chained;
        static volatile float _target;
        static long _stamp; // Environment.TickCount64 of the last frame that set _target
        static object? _classified; // the roof map the indoor-only test last ran on
        static bool _indoorOnly;

        // after HolidaySnow.Apply (which sets Scene.HostCosmeticFrame to its own frame): runs that first, then ours
        public static void Apply()
        {
            Action<Scene>? current = Scene.HostCosmeticFrame;
            if (ReferenceEquals(current, _chained)) return;
            _inner = current;
            Scene.HostCosmeticFrame = _chained;
        }

        static void Chained(Scene scene)
        {
            _inner?.Invoke(scene);
            Frame(scene);
        }

        static void Frame(Scene scene)
        {
            float target = 0;
            HolidaySnow.RoofMap? map = HolidaySnow.Map;
            if (HolidaySnow.Enabled && map != null && map.RoomId == scene.RoomId)
            {
                if (!ReferenceEquals(_classified, map))
                {
                    _classified = map;
                    _indoorOnly = IndoorOnly(scene, map);
                    Console.WriteLine($"Holiday bells: room {scene.RoomId} {(_indoorOnly ? "indoor only, bells always on" : "has open sky, bells outdoors")}");
                }
                if (_indoorOnly)
                {
                    target = 1;
                }
                else
                {
                    IReadOnlyList<PlayerEntity> players = PlayerEntity.Players;
                    for (int i = 0; i < PlayerEntity.PlayerCount && i < players.Count; i++)
                    {
                        PlayerEntity player = players[i];
                        if (player.IsMainPlayer && player.LoadFlags.TestFlag(LoadFlags.Active))
                        {
                            target = SeesSky(scene, map, player.CameraInfo.Position) ? 1 : 0;
                            break;
                        }
                    }
                }
            }
            _target = target;
            Volatile.Write(ref _stamp, Environment.TickCount64);
        }

        // out in the open, or only under a ledge or lip: open sky within SkyReach along one of 8 level directions, short of
        // any wall (owner 2026-11-21: a ledge above shouldn't stop the bells; a closed hall should). Re-checked 10 times a
        // second (the fade is 1 s)
        const float SkyReach = 5f;
        const int SkyDirections = 8;
        static long _skyAt;
        static bool _sky;

        static bool SeesSky(Scene scene, HolidaySnow.RoofMap map, Vector3 eye)
        {
            if (map.SeesSky(eye)) return true;
            long now = Environment.TickCount64;
            if (now - _skyAt < 100) return _sky;
            _skyAt = now;
            _sky = false;
            for (int d = 0; d < SkyDirections && !_sky; d++)
            {
                float a = d * MathF.Tau / SkyDirections;
                var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                CollisionResult res = default;
                float reach = SkyReach;
                if (CollisionDetection.CheckBetweenPoints(eye, eye + dir * SkyReach, TestFlags.None, scene, ref res))
                {
                    reach = Vector3.Distance(eye, res.Position);
                }
                for (float t = map.Cell; t < reach; t += map.Cell)
                {
                    if (map.SeesSky(eye + dir * t)) { _sky = true; break; }
                }
            }
            return _sky;
        }

        // a room where no player or item spawn is under open sky (or that sees no sky at all, the snow's own rule) has no
        // snow anywhere a player goes
        static bool IndoorOnly(Scene scene, HolidaySnow.RoofMap map)
        {
            if (!map.AnyOpen) return true;
            int spots = 0;
            var up = new Vector3(0, 1.5f, 0);
            foreach (PlayerSpawnEntity spawn in scene.GetPlayerSpawnEntities())
            {
                spots++;
                if (map.SeesSky(spawn.Position + up)) return false;
            }
            foreach (ItemSpawnEntity spawn in scene.GetItemSpawnEntities())
            {
                spots++;
                if (map.SeesSky(spawn.Position + up)) return false;
            }
            return spots > 0;
        }

        // what the bells fade towards now (audio threads): off once the game stops sending frames (menus, loading)
        static float Target()
        {
            long age = Environment.TickCount64 - Volatile.Read(ref _stamp);
            return age > 500 ? 0 : _target;
        }

        // ---- the bell sound ----

        static readonly object _soundGate = new();
        static float[]? _source; // stereo at 44100
        static int _sourceRate;
        static bool _packed;
        static float[]? _cached;
        static int _cachedRate;

        // the bell at this output rate, stereo float, peak 1
        static float[] Sound(int rate)
        {
            float[]? c = Volatile.Read(ref _cached);
            if (c != null && _cachedRate == rate) return c;
            lock (_soundGate)
            {
                if (_cached != null && _cachedRate == rate) return _cached;
                if (_source == null)
                {
                    (float[], int)? packed = LoadPacked();
                    _packed = packed != null;
                    (_source, _sourceRate) = packed ?? (Procedural(44100), 44100);
                }
                float[] made = Resample(_source, _sourceRate, rate);
                Console.WriteLine($"Holiday bells: {(_packed ? "the built-in bell" : "the made bell (no built-in one)")}, {made.Length / 2} frames at {rate} Hz");
                _cachedRate = rate;
                Volatile.Write(ref _cached, made);
                return made;
            }
        }

        // the owner's own bell (private: kept out of git, built in as an obfuscated resource when present)
        static (float[], int)? LoadPacked()
        {
            try
            {
                using Stream? st = typeof(HolidayBells).Assembly.GetManifestResourceStream("holiday/bells.bin");
                if (st == null) return null;
                using var ms = new MemoryStream();
                st.CopyTo(ms);
                byte[] data = ms.ToArray();
                Unpack(data);
                return ReadWav(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Holiday bells: packed bell unreadable, using the made one: {ex.Message}");
                return null;
            }
        }

        // the packing (tools: the scratch packer XORs the WAV the same way)
        static void Unpack(byte[] data)
        {
            uint x = 0x5EC0D4B7;
            for (int i = 0; i < data.Length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] ^= (byte)x;
            }
        }

        // a 16-bit PCM WAV, trimmed to a second with a fade at the end, peak 1
        static (float[], int)? ReadWav(byte[] d)
        {
            if (d.Length < 44 || d[0] != 'R' || d[8] != 'W') return null;
            int channels = 0, rate = 0, bits = 0;
            int p = 12;
            while (p + 8 <= d.Length)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (d[p] == 'f' && d[p + 1] == 'm' && d[p + 2] == 't')
                {
                    channels = BitConverter.ToInt16(d, p + 10);
                    rate = BitConverter.ToInt32(d, p + 12);
                    bits = BitConverter.ToInt16(d, p + 22);
                }
                else if (d[p] == 'd' && d[p + 1] == 'a' && d[p + 2] == 't' && d[p + 3] == 'a')
                {
                    if (bits != 16 || channels < 1 || channels > 2 || rate <= 0) return null;
                    int frames = Math.Min(size, d.Length - p - 8) / (2 * channels);
                    int keep = Math.Min(frames, rate); // a second
                    int fade = rate * 3 / 10;
                    var o = new float[keep * 2];
                    float peak = 0;
                    for (int i = 0; i < keep; i++)
                    {
                        int at = p + 8 + i * 2 * channels;
                        float l = BitConverter.ToInt16(d, at) / 32768f;
                        float r = channels == 2 ? BitConverter.ToInt16(d, at + 2) / 32768f : l;
                        float g = i >= keep - fade ? (keep - i) / (float)fade : 1;
                        o[i * 2] = l * g;
                        o[i * 2 + 1] = r * g;
                        peak = MathF.Max(peak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));
                    }
                    if (peak > 0)
                        for (int i = 0; i < o.Length; i++) o[i] /= peak;
                    return (o, rate);
                }
                p += 8 + size + (size & 1);
            }
            return null;
        }

        // a made sleigh-bell shake for when the owner's bell isn't built in: a few small bells struck a few
        // milliseconds apart, each a cluster of high inharmonic partials with a fast decay, panned a little
        static float[] Procedural(int rate)
        {
            int frames = rate * 6 / 10;
            var o = new float[frames * 2];
            var rng = new Random(1225);
            for (int bell = 0; bell < 7; bell++)
            {
                int start = (int)(rate * (bell * 0.009 + rng.NextDouble() * 0.006));
                double f0 = 2600 + rng.NextDouble() * 2400;
                float pan = (float)(rng.NextDouble() * 0.6 - 0.3);
                double decay = 18 + rng.NextDouble() * 14;
                float amp = bell == 0 ? 1f : 0.55f + (float)rng.NextDouble() * 0.35f;
                double[] ratios = { 1, 1.47, 2.09, 2.56 };
                for (int i = start; i < frames; i++)
                {
                    double t = (i - start) / (double)rate;
                    double env = Math.Exp(-decay * t) * Math.Min(1, t * rate / 24.0);
                    double s = 0;
                    for (int k = 0; k < ratios.Length; k++)
                        s += Math.Sin(Math.Tau * f0 * ratios[k] * t + k) / (k + 1);
                    float v = (float)(s * env) * amp;
                    o[i * 2] += v * (1 - pan);
                    o[i * 2 + 1] += v * (1 + pan);
                }
            }
            float peak = 0;
            foreach (float v in o) peak = MathF.Max(peak, MathF.Abs(v));
            for (int i = 0; i < o.Length; i++) o[i] /= peak;
            return o;
        }

        static float[] Resample(float[] src, int from, int to)
        {
            if (from == to) return src;
            int frames = src.Length / 2;
            int outFrames = (int)((long)frames * to / from);
            var o = new float[outFrames * 2];
            double step = (double)from / to;
            for (int i = 0; i < outFrames; i++)
            {
                double x = i * step;
                int a = (int)x;
                float f = (float)(x - a);
                int b = Math.Min(a + 1, frames - 1);
                o[i * 2] = src[a * 2] + (src[b * 2] - src[a * 2]) * f;
                o[i * 2 + 1] = src[a * 2 + 1] + (src[b * 2 + 1] - src[a * 2 + 1]) * f;
            }
            return o;
        }

        // ---- one song's bells (one audio thread each) ----

        // the bells sounding over one song, on that song's own frame clock (stream frames or generated SSEQ frames)
        public sealed class Voices
        {
            public readonly Song Song;
            readonly List<(long Start, float Weight, bool Started)> _ringing = new();
            readonly List<(double Beat, float Weight)> _hits = new();
            float _gate;
            long _lastTick = -1;

            public Voices(Song song) => Song = song;

            public void Clear()
            {
                _ringing.Clear();
                _lastTick = -1;
            }

            // ---- SSEQ: sequence ticks [last, total) took effect at frame ----

            public void RestartTicks(long ticks)
            {
                _ringing.Clear();
                _lastTick = Math.Max(0, ticks - 1);
            }

            public void Ticks(long total, long frame, int rate)
            {
                if (_lastTick < 0) _lastTick = Math.Max(0, total - 1);
                long first = _lastTick;
                _lastTick = total;
                if (total <= first || Song.Pattern == "none") return;
                long barTicks = (long)Song.Bpb * TicksPerBeat;
                long nudge = (long)(Song.NudgeMs * rate / 1000);
                for (long bar = first / barTicks; bar * barTicks < total; bar++)
                {
                    Hits(Song, bar, _hits);
                    foreach ((double beat, float weight) in _hits)
                    {
                        double h = bar * barTicks + beat * TicksPerBeat;
                        if (h >= first && h < total && h >= Song.StartTicks) Ring(frame + nudge, weight);
                    }
                }
            }

            // ---- streams: stream time [a, b) seconds plays at frames [at, ...) ----

            public void Times(double a, double b, long at, int rate)
            {
                if (Song.Pattern == "none" || b <= a) return;
                double beatSec = 60 / Song.Bpm;
                double anchor = Song.T0 + Song.NudgeMs / 1000;
                double barSec = beatSec * Song.Bpb;
                long bar0 = (long)Math.Floor((a - anchor) / barSec) - 1;
                long bar1 = (long)Math.Floor((b - anchor) / barSec);
                for (long bar = bar0; bar <= bar1; bar++)
                {
                    Hits(Song, bar, _hits);
                    foreach ((double beat, float weight) in _hits)
                    {
                        double t = anchor + bar * barSec + beat * beatSec;
                        if (t >= a && t < b && t >= 0) Ring(at + (long)Math.Round((t - a) * rate), weight);
                    }
                }
            }

            void Ring(long start, float weight)
            {
                if (_ringing.Count < 16) _ringing.Add((start, weight, false));
            }

            // the clock moves on by frames (a stream's bells count from each chunk's first frame)
            public void Shift(long frames)
            {
                for (int i = 0; i < _ringing.Count; i++)
                    _ringing[i] = (_ringing[i].Start - frames, _ringing[i].Weight, _ringing[i].Started);
            }

            public void Rescale(double s)
            {
                for (int i = _ringing.Count - 1; i >= 0; i--)
                {
                    if (_ringing[i].Started) _ringing.RemoveAt(i);
                    else _ringing[i] = ((long)Math.Round(_ringing[i].Start * s), _ringing[i].Weight, false);
                }
            }

            // adds the bells to stereo frames [first, first + frames) of this song's clock; level: the song's own fade.
            // clamp: keep the sum in -1..1 (the SSEQ output is already final)
            public void Mix(Span<float> stereo, long first, int rate, float level, bool clamp)
            {
                int frames = stereo.Length / 2;
                float target = Target();
                if (_ringing.Count == 0 && _gate == 0 && target == 0) return;
                float step = 1f / (FadeSeconds * rate);
                float g0 = _gate;
                // the gate after this chunk; inside it, a straight ramp
                float g1 = target > g0 ? MathF.Min(target, g0 + step * frames) : MathF.Max(target, g0 - step * frames);
                _gate = g1;
                if (_ringing.Count == 0) return;
                float[] sound = Sound(rate);
                int len = sound.Length / 2;
                for (int i = _ringing.Count - 1; i >= 0; i--)
                {
                    (long start, float weight, bool started) = _ringing[i];
                    if (!started)
                    {
                        if (start >= first + frames) continue; // not yet
                        if (start < first - 2048) { _ringing.RemoveAt(i); continue; } // missed
                        if (start < first) start = first;
                    }
                    int from = (int)Math.Max(0, start - first);
                    long offset = first + from - start;
                    float w = weight * BellGain * level;
                    if (g0 > 0 || g1 > 0)
                    {
                        for (int k = from; k < frames; k++)
                        {
                            long s = offset + k - from;
                            if (s >= len) break;
                            float g = w * (g0 + (g1 - g0) * k / frames);
                            stereo[k * 2] += sound[s * 2] * g;
                            stereo[k * 2 + 1] += sound[s * 2 + 1] * g;
                        }
                    }
                    if (first + frames - start >= len) _ringing.RemoveAt(i);
                    else _ringing[i] = (start, weight, true);
                }
                if (clamp)
                    for (int k = 0; k < stereo.Length; k++) stereo[k] = Math.Clamp(stereo[k], -1f, 1f);
            }
        }
    }
}
