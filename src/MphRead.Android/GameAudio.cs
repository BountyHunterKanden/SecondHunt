using System.Collections.Generic;
using System.Linq;
using Android.Media;
using Android.Util;
using MphRead;
using MphRead.Formats.Sound;
using MphRead.Sound;
using NCSF123;
using NCSFPlayer;
using OpenTK.Mathematics;
using SoundFlow.Enums;
using AndroidAudioFormat = Android.Media.AudioFormat;

namespace MphRecomp.App;

// Shared Android audio backend for MphRead's host seams (MusicPlayer.Host, Sfx.SetHost): one AndroidMusicPlayer +
// one AndroidSfxPlayer per activity, used by both the campaign (CampaignActivity: positional world SFX, sequenced
// room/event music) and the front end (GameActivity / FrontendRenderer: the menu theme, UI feedback sounds).
// Neither class touches Activity lifecycle beyond what's passed to its constructor -- callers own pausing them
// (Pause/Play or SetPaused) and disposing them.

// Renders one NCSF sequence to an AudioTrack in real time, in the mode RecompSettings.Music picks (MusicMode: the four
// lanes of MusicTestActivity, which explains "fixes" and why; this is that file's single-lane live-playback twin instead
// of its four-lane A/B tester), switching mode live mid-song. Implements MusicPlayer.IHost so MphRead's Music.* game
// logic (room music, tempo ramps, track fades, escape/encounter music) drives it exactly as it drives the desktop
// SoundFlow/MiniAudio path; the front end instead calls PlaySeq directly (it has no Music.* game state to run).
internal sealed class AndroidMusicPlayer : MusicPlayer.IHost
{
    // Music, movie audio and SFX all sit on the DS mixer's own scale, and NcsfPlay's float output already is that
    // scale (SEQ_DRONE, seq vol 127, renders within 0.2 dB of a BizHawk capture of vanilla), so no extra gain. The
    // 0.875 that was here only matched CHUTNEY because NcsfPlay applied the SDAT sequence volume linearly (fixed by
    // SquaredSeqVolume below); it left vol-127 cues ~1 dB quiet and vol-86 cues ~2.4 dB loud (UI session's mix
    // measurements, handoffs/ui-to-music_2026-10-01.md 2b). The limiter catches cues that go over full scale.
    const float HeadroomGain = 1f;
    const float DcBlockHz = 10;
    const int ChunkFrames = 512;

    const int HqRate = 48000, DsRate = 32728;
    // "hq + tone": MusicTestActivity's treble shelf, which puts HQ's band balance back to the original's
    const float ToneShelfHz = 7000, ToneShelfDb = 6;
    // a live mode change fades the old mode out and the new one in over this long each (no click, no restart)
    const int FadeMs = 5;

    // RecompSettings.Music (MusicTestActivity's four lanes): "plain" = the DS's own 32.7 kHz with no interpolation and
    // no fixes (what the recomp first played); "original" (the default) = that with the fixes; "hq" = 48 kHz sinc with
    // the fixes; "hqtone" = hq with the tone shelf. Fixes = the samples' DC offset removed, a look-ahead limiter instead
    // of clipping, volume/pan/vibrato smoothed between sequencer ticks, exact pitch. Equal modes are the same instance.
    internal sealed record MusicMode(string Name, int Rate, Interpolation Interp, bool Fixes, bool Tone)
    {
        static readonly MusicMode Plain = new("Original", DsRate, Interpolation.None, false, false);
        static readonly MusicMode Original = new("Original + fixes", DsRate, Interpolation.None, true, false);
        static readonly MusicMode Hq = new("HQ + fixes", HqRate, Interpolation.Sinc, true, false);
        static readonly MusicMode HqTone = new("HQ + fixes + tone", HqRate, Interpolation.Sinc, true, true);

        public static MusicMode For(string? music) => music switch
        {
            "plain" => Plain,
            "hq" => Hq,
            "hqtone" => HqTone,
            _ => Original,
        };
    }

    // set by SetQuality; a Load reads it once, when its task starts, and the pump follows it from then on (Pump.Switch)
    volatile MusicMode _mode;
    readonly object _gate = new();

    NCSFPlayerStream? _stream;
    AudioTrack? _track;
    System.Threading.Thread? _thread;
    volatile bool _stopped;
    volatile bool _playing;
    // reset while paused: the pump waits on it instead of spinning (see Pump)
    readonly System.Threading.ManualResetEventSlim _resume = new(true);
    float _volume = 1;
    // bumped by every Load()/Stop() call; a Load() task whose generation is stale when it (re)checks bails out
    // instead of clobbering a newer Load's stream/track -- Task.Run gives no ordering guarantee between calls, and
    // the campaign can fire several PlaySeq/Stop in quick succession (room entry, encounter/escape music...)
    int _generation;
    // Prepare(): this seq is loaded, its first quarter second already rendered into a track that hasn't started;
    // PlaySeq of the same seq just starts it (no file load + track set-up in between: ~0.23 s on the Odin)
    SeqId? _prepared;
    ushort _preparedTracks;
    // the host's own pause (Pause() until the next Play()): the campaign's pause menu, the app in the background
    volatile bool _hostPaused;
    // OPTIONS > AUDIO's music volume as a gain on the track (MasterGain), on top of the game's own fades
    volatile float _master = 1;

    // the DS's master volume steps 0..9 (OPTIONS > AUDIO) as an amplitude: the game sets v * 127 / 9 on its sound
    // players, which put a volume through the squared decibel curve (Player.DecibelSquare, tenths of a dB)
    public static float MasterGain(int step) => step >= 9 ? 1f : step <= 0 ? 0f
        : System.MathF.Pow(10f, Player.DecibelSquare(step * 127 / 9) / 200f);

    public float MasterVolume
    {
        get => _master;
        set
        {
            if (value == _master) return;
            lock (_gate)
            {
                _master = value;
                _track?.SetVolume(value);
            }
        }
    }

    // music: RecompSettings.Music (see MusicMode)
    public AndroidMusicPlayer(string? music)
    {
        _mode = MusicMode.For(music);
    }

    // RecompSettings.Music changed (the settings pages; the hosts call this every frame, so the unchanged case is one
    // compare). The playing song carries on: the pump fades the old mode out and the new one in between two chunks of
    // music, at the same point of the song (Pump.Switch). While the host has the music paused (the campaign's pause
    // menu), the change is heard on resume, after the quarter second the track already holds.
    public void SetQuality(string? music)
    {
        var mode = MusicMode.For(music);
        if (ReferenceEquals(mode, _mode)) return;
        _mode = mode;
        Log.Info("MPHAudio", $"music quality -> {mode.Name} (live)");
    }

    // the stream's settings for a mode, at load
    static void Configure(NCSFPlayerStream stream, MusicMode mode)
    {
        stream.OutputGain = HeadroomGain;
        stream.SquaredSeqVolume = true; // the SDAT volume on the NITRO player's curve, as on the DS
        stream.ExactTempo = true; // the DS's 5.2095 ms sequencer tick, not rounded up to whole samples
        // look-ahead limiter, not the tanh soft limit: same on most cues (both ~-50 dB distortion), but the one cue
        // that runs 5.5 dB over at this gain (GOREA_1 M21, Seal Sphere) distorts 9 dB less (PC, scratchpad limtest,
        // 2026-09-30)
        stream.DcBlockHz = mode.Fixes ? DcBlockHz : 0;
        stream.Limiter = mode.Fixes;
        stream.Player.Smoothing = mode.Fixes;
        stream.Player.ExactPitch = mode.Fixes;
        stream.HighShelfHz = mode.Tone ? ToneShelfHz : 0;
        stream.HighShelfDb = mode.Tone ? ToneShelfDb : 0;
    }

    // the same, mid-song (from the pump, between two Reads)
    static void ApplyLive(NCSFPlayerStream stream, MusicMode mode)
    {
        stream.ChangeQuality((uint)mode.Rate, mode.Interp);
        stream.ChangeFixes(mode.Fixes ? DcBlockHz : 0, mode.Fixes, mode.Fixes, mode.Fixes,
            mode.Tone ? ToneShelfHz : 0, mode.Tone ? ToneShelfDb : 0);
    }

    static AudioTrack BuildTrack(int rate)
    {
        int minBytes = AudioTrack.GetMinBufferSize(rate, ChannelOut.Stereo, Android.Media.Encoding.PcmFloat);
        int bytes = System.Math.Max(minBytes, rate / 4 * 8); // >= 250 ms of stereo float
        return new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)
            .SetAudioFormat(new AndroidAudioFormat.Builder()
                .SetEncoding(Android.Media.Encoding.PcmFloat)!
                .SetSampleRate(rate)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build()!)
            .SetBufferSizeInBytes(bytes)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
    }

    public void Load(SeqId seqId, ushort tracks, float volume, System.Action loaded) => Load(seqId, tracks, volume, loaded, startPaused: false);

    void Load(SeqId seqId, ushort tracks, float volume, System.Action loaded, bool startPaused)
    {
        _volume = volume;
        int myGen = System.Threading.Interlocked.Increment(ref _generation);
        Log.Info("MPHAudio", $"music Load({seqId}, tracks=0x{tracks:X4}, vol={volume:0.00}{(startPaused ? ", prepare" : "")}) gen {myGen}");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                MusicMode mode;
                lock (_gate)
                {
                    if (myGen != _generation)
                    {
                        Log.Info("MPHAudio", $"music Load gen {myGen} superseded before starting (now {_generation}), skipped");
                        return;
                    }
                    StopLocked();
                    mode = _mode; // the build's mode; the pump follows any later SetQuality live
                }
                // The stream and track are built OUTSIDE _gate (~0.1 s on the Odin). Holding it for the build froze the
                // game thread on every music change: Music.PlaySeq sets Tempo right after Load and Music.ProcessTempo
                // reads it every frame, both through _gate (owner's Guardian-fight frame drops, 2026-10-02: 92-108 ms
                // freezes, one per GREY/GUARDIAN switch).
                string path = Paths.Combine(Paths.FileSystem, "_seq", Metadata.SequenceFiles[(int)seqId]);
                var stream = new NCSFPlayerStream(path, (uint)mode.Rate, mode.Interp, skipSilenceOnStartSec: 5,
                    defaultLengthInMS: 115000, defaultFadeInMS: 5000, NCSF123.VolumeType.ReplayGainAlbum, PeakType.ReplayGainTrack,
                    playForever: true, volume, channelMutes: 0, (ushort)(tracks ^ 0xFFFF), ignoreVolume: false);
                Configure(stream, mode);
                if (myGen != System.Threading.Volatile.Read(ref _generation))
                {
                    // a newer Load() arrived while the NCSF stream/file I/O above was in flight -- discard our
                    // work instead of installing a stale sequence over the newer one
                    Log.Info("MPHAudio", $"music Load gen {myGen} superseded mid-build (now {_generation}), discarding");
                    stream.Dispose();
                    return;
                }
                var track = BuildTrack(mode.Rate);
                track.SetVolume(_master);
                lock (_gate)
                {
                    if (myGen != _generation)
                    {
                        Log.Info("MPHAudio", $"music Load gen {myGen} superseded mid-build (now {_generation}), discarding");
                        track.Release();
                        stream.Dispose();
                        return;
                    }
                    // nothing can have been installed since the StopLocked above (a newer Load bumps _generation before
                    // its task runs, so it fails the check); this only keeps the swap safe if that ever changes
                    StopLocked();
                    _stream = stream;
                    _track = track;
                    _stopped = false;
                    if (startPaused)
                    {
                        // the pump fills the track's buffer, then waits (a track that isn't playing takes no more)
                        _resume.Reset();
                        _playing = false;
                        _prepared = seqId;
                        _preparedTracks = tracks;
                    }
                    else
                    {
                        track.Play();
                        _playing = true;
                    }
                    _thread = new System.Threading.Thread(new Pump(this, stream, track, myGen, mode).Run) { IsBackground = true, Name = "mph-music" };
                    _thread.Start();
                    Log.Info("MPHAudio", $"music Load gen {myGen} {(startPaused ? "prepared" : "started")} ({seqId} @ {mode.Rate} Hz, {mode.Name})");
                }
            }
            catch (System.Exception ex)
            {
                Log.Error("MPHAudio", $"music load failed ({seqId}) gen {myGen}: {ex}");
            }
            finally
            {
                loaded();
            }
        });
    }

    // a plain "play this seq and loop forever" entry point for the front end, which has no Music.* game state to
    // drive Load/Play itself
    public void PlaySeq(SeqId seqId, ushort tracks = 0xFFFF)
    {
        lock (_gate)
        {
            if (_prepared == seqId && _preparedTracks == tracks && _track != null && !_playing)
            {
                _prepared = null;
                Log.Info("MPHAudio", $"music PlaySeq({seqId}): prepared, starting at once");
                Play(1);
                return;
            }
        }
        Load(seqId, tracks, 1, () => Play(1));
    }

    // load a seq ahead of its PlaySeq (the front end's boot logos: DRONE must start on the page change, as on the DS)
    public void Prepare(SeqId seqId, ushort tracks = 0xFFFF) => Load(seqId, tracks, 1, () => { }, startPaused: true);

    // The music thread: renders the stream into the track chunk by chunk, and makes live mode changes (Switch).
    sealed class Pump
    {
        readonly AndroidMusicPlayer _owner;
        readonly NCSFPlayerStream _stream;
        readonly int _gen;
        readonly byte[] _bytes = new byte[ChunkFrames * 8];
        readonly float[] _floats = new float[ChunkFrames * 2];
        AudioTrack _track;
        MusicMode _mode;
        // the ship screens' echo runs over the music too (the DS echoes its whole output; no-op elsewhere)
        MphRecomp.Frontend.DsEcho _echo;
        int _count, _offset; // _floats[_offset.._count) still to go into _track
        long _written; // frames given to _track
        int _fadeIn; // frames of fade-in left after a mode change
        AudioTrack? _retired; // the track before a rate change, playing out its fade; released a moment later
        long _retiredAt;

        public Pump(AndroidMusicPlayer owner, NCSFPlayerStream stream, AudioTrack track, int gen, MusicMode mode)
        {
            _owner = owner;
            _stream = stream;
            _track = track;
            _gen = gen;
            _mode = mode;
            _echo = new MphRecomp.Frontend.DsEcho(mode.Rate);
        }

        int FadeFrames => _mode.Rate * FadeMs / 1000;

        public void Run()
        {
            try
            {
                while (!_owner._stopped)
                {
                    if (_offset == _count)
                    {
                        if (!ReferenceEquals(_owner._mode, _mode))
                        {
                            if (!Switch()) return;
                            continue;
                        }
                        if (!Render()) return;
                        if (_retired != null && System.Environment.TickCount64 - _retiredAt > 200)
                        {
                            _retired.Release();
                            _retired = null;
                        }
                    }
                    if (!Write()) return;
                }
            }
            catch (System.Exception ex)
            {
                // torn down mid-render (Stop/Load called from another thread, which disposes stream/track under us) or a
                // genuine playback error -- either way this pump is done; Error (not Debug) so it isn't filtered out
                Log.Error("MPHAudio", $"music gen {_gen} pump stopped: {ex}");
            }
            finally
            {
                _retired?.Release();
            }
        }

        // the next chunk of the song into _floats, with the echo and what's left of a fade-in; false = stop
        bool Render()
        {
            int n = _stream.Read(_bytes, 0, _bytes.Length);
            if (n <= 0)
            {
                Log.Warn("MPHAudio", $"music gen {_gen}: stream.Read returned {n}, stopping pump");
                return false;
            }
            System.Buffer.BlockCopy(_bytes, 0, _floats, 0, n);
            _count = n / 4;
            _offset = 0;
            int frames = _count / 2, fade = FadeFrames;
            _echo.ProcessInterleaved(_floats, frames);
            for (int f = 0; _fadeIn > 0 && f < frames; f++, _fadeIn--)
            {
                float g = 1f - (float)_fadeIn / fade;
                _floats[2 * f] *= g;
                _floats[2 * f + 1] *= g;
            }
            return true;
        }

        // one blocking write of what's left of the chunk; false = stop
        bool Write()
        {
            int w = _track.Write(_floats, _offset, _count - _offset, WriteMode.Blocking);
            if (w < 0)
            {
                Log.Warn("MPHAudio", $"music gen {_gen}: AudioTrack.Write returned {w}, stopping pump");
                return false;
            }
            _offset += w;
            _written += w / 2;
            if (_offset < _count)
            {
                // A paused AudioTrack turns blocking writes non-blocking: Write returns 0 at once. Without this wait
                // the pump spun at 100% CPU for as long as the track stayed paused (the front end's menu theme, paused
                // under the whole campaign), rendering music it then threw away. The timeout is only a safety net;
                // Play/Stop set the event.
                _owner._resume.Wait(50);
            }
            return true;
        }

        // A live mode change (SetQuality), between two chunks: the old mode's next chunk ends in a fade-out, the stream
        // switches (NCSFPlayerStream.ChangeQuality/ChangeFixes: the song carries on from the same point) and fades back
        // in. Same rate: the same track carries on. A new rate needs a new track: it's filled while the old one plays
        // out what it holds (its ~250 ms buffer) and started as the old one's fade-out begins. False = stop.
        bool Switch()
        {
            MusicMode next = _owner._mode, old = _mode;
            if (!Render()) return false;
            int frames = _count / 2, fade = System.Math.Min(frames, FadeFrames);
            for (int i = 0; i < fade; i++)
            {
                float g = (float)(fade - 1 - i) / fade;
                int f = frames - fade + i;
                _floats[2 * f] *= g;
                _floats[2 * f + 1] *= g;
            }
            while (_offset < _count)
            {
                if (_owner._stopped || !Write()) return false;
            }
            long fadeStart = _written - fade;
            ApplyLive(_stream, next);
            _mode = next;
            _fadeIn = FadeFrames;
            Log.Info("MPHAudio", $"music gen {_gen}: {old.Name} -> {next.Name} live");
            if (next.Rate == old.Rate) return true;

            AudioTrack oldTrack = _track, fresh = BuildTrack(next.Rate);
            fresh.SetVolume(_owner._master);
            _echo = new MphRecomp.Frontend.DsEcho(next.Rate);
            // a track that hasn't started takes what fits in its buffer; the chunk that doesn't fit waits in _floats
            long freshWritten = 0;
            while (true)
            {
                if (_owner._stopped || !Render())
                {
                    fresh.Release();
                    return false;
                }
                int w = fresh.Write(_floats, 0, _count, WriteMode.NonBlocking);
                if (w < 0)
                {
                    Log.Warn("MPHAudio", $"music gen {_gen}: new track Write returned {w}, stopping pump");
                    fresh.Release();
                    return false;
                }
                _offset = w;
                freshWritten += w / 2;
                if (_offset < _count) break;
            }
            // the old track only moves while it plays (a paused host, the pause menu, holds it until Play)
            long head;
            while (true)
            {
                if (_owner._stopped)
                {
                    fresh.Release();
                    return false;
                }
                head = oldTrack.PlaybackHeadPosition & 0xFFFFFFFFL;
                long left = fadeStart - head;
                if (left <= 0) break;
                if (oldTrack.PlayState != PlayState.Playing)
                {
                    _owner._resume.Wait(50);
                    System.Threading.Thread.Sleep(5);
                    continue;
                }
                System.Threading.Thread.Sleep((int)System.Math.Clamp(left * 1000 / old.Rate - 1, 1, 20));
            }
            // swap under _gate (Play/Pause act on _owner._track), without blocking on it: StopLocked holds it while it
            // waits for this thread
            while (!System.Threading.Monitor.TryEnter(_owner._gate, 5))
            {
                if (_owner._stopped)
                {
                    fresh.Release();
                    return false;
                }
            }
            bool playing;
            try
            {
                if (_owner._stopped)
                {
                    fresh.Release();
                    return false;
                }
                playing = oldTrack.PlayState == PlayState.Playing;
                fresh.SetVolume(_owner._master); // MasterVolume may have changed meanwhile (it sets _owner._track's)
                if (playing) fresh.Play();
                _owner._track = fresh;
            }
            finally
            {
                System.Threading.Monitor.Exit(_owner._gate);
            }
            Log.Info("MPHAudio", $"music gen {_gen}: now on a {next.Rate} Hz track ({(playing ? "playing" : "paused")}; old track at {head} of {_written} frames)");
            // a streaming track's Stop plays out what it holds (the rest of the fade) before it stops
            oldTrack.Stop();
            _retired?.Release();
            _retired = oldTrack;
            _retiredAt = System.Environment.TickCount64;
            _track = fresh;
            _written = freshWritten;
            return true;
        }
    }

    // must be called with _gate held
    void StopLocked()
    {
        _prepared = null;
        _stopped = true;
        _resume.Set(); // wake a pump waiting out a pause so it sees _stopped
        _playing = false;
        _track?.Pause();
        _track?.Flush();
        _thread?.Join(500);
        _track?.Stop();
        _track?.Release();
        _stream?.Dispose();
        _track = null;
        _stream = null;
        _thread = null;
    }

    public void Play(float volume)
    {
        _hostPaused = false;
        Volume = volume;
        lock (_gate)
        {
            Log.Info("MPHAudio", $"music Play({volume:0.00}), track {(_track == null ? "null" : "present")}");
            _track?.Play();
            _playing = _track != null;
            _resume.Set();
        }
    }

    public void Pause()
    {
        _hostPaused = true;
        lock (_gate)
        {
            Log.Info("MPHAudio", "music Pause()");
            _resume.Reset();
            _track?.Pause();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            System.Threading.Interlocked.Increment(ref _generation); // invalidate any in-flight Load()
            Log.Info("MPHAudio", "music Stop()");
            StopLocked();
        }
    }

    public PlaybackState State
    {
        get { lock (_gate) { return _playing ? PlaybackState.Playing : PlaybackState.Stopped; } }
    }

    public float Volume
    {
        get { lock (_gate) { return _stream?.VolumeModification ?? _volume; } }
        set
        {
            lock (_gate)
            {
                _volume = value;
                if (_stream != null)
                {
                    _stream.VolumeModification = System.Math.Clamp(value, 0, 1);
                }
            }
        }
    }

    public ushort Tracks
    {
        get { lock (_gate) { return _stream != null ? (ushort)(_stream.Player.TrackMutes ^ 0xFFFF) : (ushort)0; } }
        set
        {
            lock (_gate)
            {
                if (_stream != null)
                {
                    _stream.Player.TrackMutes = (ushort)(value ^ 0xFFFF);
                }
            }
        }
    }

    public ushort Tempo
    {
        get { lock (_gate) { return _stream?.Player.TempoRatio ?? 0; } }
        set
        {
            lock (_gate)
            {
                if (_stream != null)
                {
                    _stream.Player.TempoRatio = value;
                }
            }
        }
    }

    public NCSFCommon.Track? GetTrack(int index)
    {
        lock (_gate)
        {
            return _stream?.Player.GetTrack(index);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }
}

// A small software mixer on one shared AudioTrack, standing in for SfxInstance's OpenAL playback: positional
// one-shot/looping sound (distance attenuation + stereo pan relative to the listener -- no full 3D mixer) for direct
// samples (PlaySample), "generated" combo sounds (PlayDgn: up to three layered samples whose volume/pitch come from
// the DGN file's own breakpoint curves) and SFX scripts (PlayScript: a sequence of delayed samples, each at the
// script's own volume/pan/pitch). Used directly (PlayUi) for front-end menu sounds, which have no SoundSource.
//
// The bookkeeping mirrors SfxInstance (MphRead/Sound/Sfx.cs) one to one, because the game's sound calls rely on it:
// - An Instance is one play request (a sample, a DGN or a script) with one voice per layer, like SoundInstance.
// - SetUpInstance: a LOOPING request from a source that is already playing that id reuses the playing instance
//   instead of starting another. The game asks for held sounds every frame (the charge beam's loop is requested on
//   every frame of a charge); without this each request stacked a new voice. `recency` does the same for one-shots
//   that must not overlap themselves.
// - Looping samples loop from their own loop point (SoundSample.LoopStart), not from the start of the sample, so an
//   intro plays once (the Power Beam charge has a 1.9 s intro before its 0.9 s loop).
// - Script entries carry flags in SfxData: 0x8000 = stop that sample, 0x4000 = loop it; the sample id is & 0x3FFF.
// - Playing instances refresh their gain/pan from the source position every Update (unless noUpdate), and a DGN
//   layer whose curve reaches zero volume stops.
//
// This replaced an earlier SoundPool-backed version: SoundPool.Load() never actually decoded any sample (logcat's
// own "play soundID N not READY" kept repeating forever), because MphRead.Formats.Sound.SoundSample.WaveData is
// HEADERLESS raw PCM (16-bit for MPH's ADPCM-sourced samples, see SoundRead.GetWaveData) -- there was never a valid
// RIFF/WAVE container in the file handed to SoundPool, so its decoder had nothing to parse. Decoding is already done
// for us (that's what WaveData is), so this class just reads those samples directly into a small mixer.
//
// Streams (PlayFreeStream/QueueStream: the title music and the voices, SDAT STRMs) play as voices on this same mixer,
// see the streams section. Not implemented yet: environment SFX (PlayEnvironmentSfx, the ambient loops such as
// electricity and bubbles) are still the silent base no-op.
internal sealed class AndroidSfxPlayer : SfxInstanceBase
{
    // Android only grants a FAST/low-latency AudioTrack at the device's own native output rate (PROPERTY_OUTPUT_
    // SAMPLE_RATE, typically 48000): at any other rate AudioFlinger resamples and silently falls back to the normal
    // mixer with large buffers. Mixing already resamples every voice to the output rate (Voice.Step), so there's no
    // cost to targeting whatever rate the device reports; the burst size (PROPERTY_OUTPUT_FRAMES_PER_BUFFER) is the
    // fast mixer's own period, so a chunk of exactly that size and a buffer of 2x it is the smallest that stays
    // glitch-free on it.
    readonly int _mixRate;
    readonly int _mixChunkFrames;

    const int MaxInstances = 128; // SfxInstance's _instances
    const int MaxPerInstance = 12; // SfxInstance's _maxPerInst
    const int MaxVoices = 64;

    readonly object _lock = new();
    AudioTrack? _mixTrack;
    System.Threading.Thread? _mixThread;
    volatile bool _mixStop;
    // OPTIONS > AUDIO's volumes (AndroidMusicPlayer.MasterGain): sound effects, and streams (the title music) with the music
    volatile float _sfxMaster = 1, _streamMaster = 1;
    public float SfxMasterVolume { get => _sfxMaster; set => _sfxMaster = value; }
    public float StreamMasterVolume { get => _streamMaster; set => _streamMaster = value; }
    // reset while paused: the mixer waits on it instead of spinning (see MixerLoop)
    readonly System.Threading.ManualResetEventSlim _mixResume = new(true);

    IReadOnlyList<SoundSample>? _samples;
    IReadOnlyList<DgnFile>? _dgnFiles;
    IReadOnlyList<SfxScriptFile>? _sfxScripts;
    IReadOnlyList<Sound3dEntry> _rangeData = new List<Sound3dEntry>();
    public override IReadOnlyList<Sound3dEntry> RangeData => _rangeData;

    readonly Dictionary<int, short[]> _pcmCache = new(); // sample id -> decoded mono PCM16 (decoded once, kept in RAM)
    sbyte[] _sampleSlots = System.Array.Empty<sbyte>(); // SNDTBLS SlotCount per sample id (see SlotLimit)

    // set by the owning activity once it has a listener (the campaign's player camera); left null for the front end,
    // where every PlayUi call is positionless (source: null) and these are never consulted
    public System.Func<Vector3>? ListenerPosition;
    public System.Func<Vector3>? ListenerUp;
    public System.Func<Vector3>? ListenerFacing;

    public override Vector3 GetListenerPosition() => ListenerPosition?.Invoke() ?? base.GetListenerPosition();
    public override Vector3 GetListenerUp() => ListenerUp?.Invoke() ?? base.GetListenerUp();
    public override Vector3 GetListenerFacing() => ListenerFacing?.Invoke() ?? base.GetListenerFacing();

    // one mixer voice: a position + step (resampling ratio) into a cached mono PCM buffer, plus its stereo gains.
    // The mixer thread owns Pos and removes a voice once it's Stopped or (non-looping) finished.
    sealed class Voice
    {
        public short[] Pcm = null!;
        public double Pos;
        public double Step;
        public float VolL, VolR;
        public bool Loop;
        public int LoopStart; // where a loop restarts: the sample's own loop point (0 = the whole sample)
        public volatile bool Stopped;
        public volatile bool Finished; // set by the mixer when a one-shot reaches its end
        // a stopped sound on the DS isn't cut: StopSfxHandle (ARM9 rev 0 0x2071d68) force-stops its sequence and the ARM7
        // releases the channel, about -18 dB a frame, silent ~55 ms later (BizHawk SOUNDxCNT, USA rev 1, 2026-10-01:
        // v80 -> 40/s2 -> 20/s3 -> 1/s3 -> off; UI session's sound_census/thrust/report.md). The mixer fades Env out.
        public volatile bool Releasing;
        public float Env = 1;
        // a stream's StopStream(fade): the DS stream fader runs the volume down to 0 over the fade frames, through the
        // SDK's linear decibel table (see StreamGain), so the amplitude falls linearly: Fade drops by FadeStep a sample
        public float Fade = 1, FadeStep;
        public bool Stream; // a STRM channel (see the streams section): never dropped by the voice cap
        public bool Alive => !Stopped && !Finished && !Releasing;
    }

    // one layer of an instance (SoundInstance's per-channel Samples/Volume/Pitch/Loop arrays)
    sealed class Layer
    {
        public int SampleId;
        public Voice Voice = null!;
        public float RelVolume = 1; // DGN curve volume or script entry volume; 1 for a plain sample
        // only a plain sample plays at its SoundTable volume: the game's sample play (ARM9 rev 0 0x2071d78) reads the
        // table volume only when passed 0xFF; scripts pass header volume x entry volume and DGNs 0x7F x the curve
        // (BizHawk hook on rev 1 0x020725CC, 2026-10-01: ALINOS_INTRO_SCR -> (0x7F, 0x60), DGN 1 -> (0x7F, 0x20))
        public bool TableVolume;
        public float PitchMul = 1;
        public float? Pan; // a script entry's fixed pan (no positional audio); null = positional
    }

    // one play request (SoundInstance)
    sealed class Instance
    {
        public int Handle;
        public int SfxId; // the id exactly as the game passed it (flags included), like SoundInstance.SfxId
        public SoundSource? Source;
        public float PlayTime;
        public bool NoUpdate;
        public bool Cancellable;
        public bool Paused;
        public bool Removed;
        public Vector3? FrozenPosition; // noUpdate: the source position when the sound started
        public DgnFile? Dgn;
        public SfxScriptFile? Script;
        public int ScriptIndex = -1;
        // the DS script clock: one script tick per game frame -- 60 Hz in the title menus and the in-ship menu, 30 Hz in
        // game, the briefing and the planet select, half that for an id the game plays with bit 31 (the briefing's
        // TELEPATHIC_MESSAGE, ARM9 0x206eda8). SfxScriptEntry.Delay is raw / 30 s, so script time runs at ScriptRate / 30.
        public float ScriptRate = 30;
        public readonly List<Layer> Layers = new();

        public bool AnyVoicePlaying => Layers.Exists(l => l.Voice.Alive);
        public bool IsLooping => Layers.Exists(l => l.Voice.Loop);
        public bool IsPlaying => !Removed && (AnyVoicePlaying || (Script != null && ScriptIndex < Script.Entries.Count - 1));
    }

    readonly List<Instance> _instances = new();
    readonly List<Voice> _voices = new(); // what the mixer thread actually reads each chunk
    int _nextHandle = 1;
    readonly HashSet<int> _logged = new(); // one MPHAudio line per distinct sample id, first time it plays

    void LogOnce(int sfxId, string message)
    {
        lock (_lock)
        {
            if (!_logged.Add(sfxId))
            {
                return;
            }
        }
        Log.Info("MPHAudio", message);
    }

    public AndroidSfxPlayer()
    {
        var audioManager = (AudioManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.AudioService);
        _mixRate = ParseIntProperty(audioManager, AudioManager.PropertyOutputSampleRate, 48000);
        int burst = ParseIntProperty(audioManager, AudioManager.PropertyOutputFramesPerBuffer, 192);
        _mixChunkFrames = burst;
        int minBytes = AudioTrack.GetMinBufferSize(_mixRate, ChannelOut.Stereo, Android.Media.Encoding.Pcm16bit);
        int bytes = System.Math.Max(minBytes, burst * 4 * 2); // 2x the fast mixer's own period, not a fixed 250 ms
        _mixTrack = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
                .SetContentType(AudioContentType.Sonification)!
                .Build()!)
            .SetAudioFormat(new AndroidAudioFormat.Builder()
                .SetEncoding(Android.Media.Encoding.Pcm16bit)!
                .SetSampleRate(_mixRate)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build()!)
            .SetBufferSizeInBytes(bytes)
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        _mixTrack.Play();
        Log.Info("MPHAudio", $"sfx mixer: rate {_mixRate} Hz, burst {burst} frames, buffer {bytes / 4} frames, " +
            $"performanceMode {_mixTrack.PerformanceMode} (LowLatency=1 means the fast path was granted)");
        _mixThread = new System.Threading.Thread(MixerLoop) { IsBackground = true, Name = "mph-sfx-mixer" };
        _mixThread.Start();
    }

    static int ParseIntProperty(AudioManager? mgr, string property, int fallback)
    {
        string? s = mgr?.GetProperty(property);
        return System.Int32.TryParse(s, out int v) && v > 0 ? v : fallback;
    }

    void MixerLoop()
    {
        // this thread only ever touches the small critical section below and writes to the AudioTrack -- worth the
        // real-time scheduling class so the OS doesn't preempt it behind normal-priority work
        Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var mixL = new float[_mixChunkFrames];
        var mixR = new float[_mixChunkFrames];
        var outBuf = new short[_mixChunkFrames * 2];
        // the release's fall per output sample: 1.08 dB/ms
        float releaseMul = System.MathF.Pow(10, -54f / _mixRate);
        var echo = new MphRecomp.Frontend.DsEcho(_mixRate);
        try
        {
            while (!_mixStop)
            {
                System.Array.Clear(mixL);
                System.Array.Clear(mixR);
                lock (_lock)
                {
                    for (int vi = _voices.Count - 1; vi >= 0; vi--)
                    {
                        Voice v = _voices[vi];
                        if (v.Stopped || v.Pcm.Length == 0)
                        {
                            _voices.RemoveAt(vi);
                            continue;
                        }
                        int end = v.Pcm.Length;
                        int loopLength = end - v.LoopStart;
                        bool done = false;
                        float master = v.Stream ? _streamMaster : _sfxMaster;
                        float volL = v.VolL * master, volR = v.VolR * master;
                        for (int i = 0; i < _mixChunkFrames; i++)
                        {
                            if (v.Pos >= end)
                            {
                                if (v.Loop)
                                {
                                    v.Pos = v.LoopStart + (v.Pos - end) % loopLength;
                                }
                                else
                                {
                                    done = true;
                                    break;
                                }
                            }
                            int idx = (int)v.Pos;
                            int idx2 = idx + 1 < end ? idx + 1 : (v.Loop ? v.LoopStart : idx);
                            float frac = (float)(v.Pos - idx);
                            float s = v.Pcm[idx] + (v.Pcm[idx2] - v.Pcm[idx]) * frac;
                            if (v.Releasing)
                            {
                                s *= v.Env;
                                v.Env *= releaseMul;
                                if (v.Env < 2.5e-4f) // -72 dB: gone
                                {
                                    done = true;
                                    break;
                                }
                            }
                            if (v.FadeStep != 0)
                            {
                                s *= v.Fade;
                                v.Fade -= v.FadeStep;
                                if (v.Fade <= 0)
                                {
                                    done = true;
                                    break;
                                }
                            }
                            mixL[i] += s * volL;
                            mixR[i] += s * volR;
                            v.Pos += v.Step;
                        }
                        if (done)
                        {
                            v.Finished = true;
                            _voices.RemoveAt(vi);
                        }
                    }
                }
                // the ship screens' post-mix echo (no-op elsewhere)
                echo.Process(mixL, mixR, _mixChunkFrames);
                for (int i = 0; i < _mixChunkFrames; i++)
                {
                    outBuf[i * 2] = (short)System.Math.Clamp(mixL[i], short.MinValue, short.MaxValue);
                    outBuf[i * 2 + 1] = (short)System.Math.Clamp(mixR[i], short.MinValue, short.MaxValue);
                }
                int offset = 0;
                while (offset < outBuf.Length && !_mixStop)
                {
                    int written = _mixTrack!.Write(outBuf, offset, outBuf.Length - offset, WriteMode.Blocking);
                    if (written < 0)
                    {
                        // a dead/invalid track: don't spin on it
                        LogOnce(-1, $"sfx mixer: AudioTrack.Write returned {written}");
                        _mixResume.Wait(100);
                        break;
                    }
                    offset += written;
                    if (offset < outBuf.Length)
                    {
                        // A paused AudioTrack turns blocking writes non-blocking: Write returns 0 at once. Without
                        // this wait the mixer spun at 100% CPU, at UrgentAudio priority, for as long as the track
                        // stayed paused (the front end's mixer, paused under the whole campaign), and played every
                        // voice out at full speed. Waiting keeps the voices where the pause found them.
                        _mixResume.Wait(50);
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Log.Error("MPHAudio", "sfx mixer stopped: " + ex);
        }
    }

    // reads the sample/DGN/script tables from the current ROM's file system (Paths must already point at it) --
    // safe to call with no Scene at all (the front end has none); the campaign calls it too, instead of relying on
    // the virtual Load(Scene) below, which the game engine invokes on the plain no-op base *before* the campaign
    // activity gets a chance to install this instance as Sfx.Instance (see Sfx.SetHost's doc comment)
    public void LoadTables()
    {
        _samples = SoundRead.ReadSoundSamples();
        _dgnFiles = SoundRead.ReadDgnFiles();
        _sfxScripts = SoundRead.ReadSfxScriptFiles();
        _rangeData = SoundRead.ReadSound3dList();
        SoundTable table = SoundRead.ReadSoundTables();
        _sampleSlots = table.Entries.Select(e => (sbyte)e.SlotCount).ToArray();
        for (int i = 0; i < _samples.Count && i < table.Entries.Count; i++)
        {
            _samples[i].Volume = table.Entries[i].InitialVolume / 127f;
        }
        Log.Info("MPHAudio", $"tables loaded: {_samples.Count} samples, {_dgnFiles.Count} dgn, {_sfxScripts.Count} scripts");
        StartStreamLoad();
    }

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _mixResume.Reset();
            _mixTrack?.Pause();
        }
        else
        {
            _mixTrack?.Play();
            _mixResume.Set();
        }
    }

    public void Dispose()
    {
        _mixStop = true;
        _mixResume.Set();
        _mixThread?.Join(500);
        _mixTrack?.Stop();
        _mixTrack?.Release();
        _mixTrack = null;
    }

    // the front end's entry point: no SoundSource, no DGN/script curve inputs -- just "play this ROM sound", exactly
    // like MphRead.Sound.Sfx's own id-based dispatch (PlaySfx/PlayFreeSfx) picks sample vs. script by the id's flags
    // scriptRate: the screen's DS script clock (60 = title menus and the in-ship menu, 30 = in game, the briefing and the
    // planet select; half for an id the game plays with bit 31). Returns the sample's handle (-1 for a script or
    // nothing), for a loop the caller stops. (UI session, 2026-10-01: script clock + handle, see Instance.ScriptRate.)
    public int PlayUi(int sfxId, float scriptRate = 30)
    {
        if (sfxId < 0)
        {
            return -1;
        }
        if ((sfxId & 0x4000) != 0)
        {
            PlayScript(sfxId, source: null, noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
            lock (_lock)
            {
                Instance? inst = _instances.FindLast(i => i.SfxId == sfxId && i.Script != null && i.Source == null && !i.Removed);
                if (inst != null)
                {
                    inst.ScriptRate = scriptRate;
                }
            }
            return -1;
        }
        if ((sfxId & 0x8000) == 0)
        {
            return PlaySample(sfxId, source: null, loop: null, noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
        }
        return -1;
    }

    // the game's StopAllSfx: every sound stops (the pause map's open, overlay 8 -- UI session 2026-10-03)
    public void StopAll()
    {
        lock (_lock)
        {
            _instances.FindAll(i => !i.Removed).ForEach(ReleaseInstance);
        }
    }

    // the DS's one free-script slot holds UI / dialog scripts: StopFreeScripts(1) (ARM9 rev 0 0x206ec74) before each menu
    // script or popup sample stops those -- not an entity's (the cockpit view's room keeps running under the ship menu)
    public void StopUiScripts()
    {
        lock (_lock)
        {
            _instances.FindAll(i => i.Script != null && i.Source == null).ForEach(ReleaseInstance);
        }
    }

    // UpdateSfxHandle (ARM9 rev 0 0x2071bb4): a playing UI sample's volume (0..127 for PlaySfx's 0x7F, 0xFF keeps it),
    // pan (0..127, 0x40 centre, 0xFF keeps it) and pitch (0x2000 = x1, +-12 semitones at 0x4000 / 0x1000, 0xFFFF keeps
    // it) -- the options' sensitivity loop follows its slider with it every frame
    public void UpdateUiHandle(int handle, int volume = 0xFF, int pan = 0xFF, int pitch = 0xFFFF)
    {
        lock (_lock)
        {
            Instance? inst = _instances.Find(i => i.Handle == handle && !i.Removed);
            if (inst == null)
            {
                return;
            }
            foreach (Layer layer in inst.Layers)
            {
                if (volume != 0xFF)
                {
                    layer.RelVolume = System.Math.Clamp(volume, 0, 127) / 127f;
                }
                if (pan != 0xFF)
                {
                    layer.Pan = System.Math.Clamp((pan - 64) / 64f, -1f, 1f);
                }
                if (pitch != 0xFFFF)
                {
                    layer.PitchMul = Sfx.CalculatePitchDiv(pitch);
                }
            }
            RefreshGains(inst);
        }
    }

    // SfxInstance.SetUpInstance: false (with the existing instance) when the request should reuse a playing one
    bool SetUpInstance(int id, SoundSource? source, bool loop, float recency, bool sourceOnly, bool cancellable,
        bool noUpdate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Instance? inst)
    {
        if (loop)
        {
            recency = float.MaxValue;
            sourceOnly = true;
        }
        if (recency >= 0)
        {
            Instance? recent = _instances.Find(i => i.SfxId == id && i.PlayTime <= recency && i.IsPlaying
                && (!sourceOnly || ReferenceEquals(i.Source, source)));
            if (recent != null)
            {
                inst = recent;
                return false;
            }
        }
        if (source != null && !TakeSlot(id, source, noUpdate))
        {
            inst = null;
            return false; // every slot of this id holds a louder sound: the game drops the request
        }
        if (_instances.Count(i => !i.Removed) >= MaxInstances)
        {
            // SfxInstance.FindInstance: take over the longest-playing instance, preferring one from the same source
            Instance oldest = _instances.Where(i => !i.Removed)
                .OrderByDescending(i => source != null && ReferenceEquals(i.Source, source))
                .ThenByDescending(i => i.PlayTime).First();
            StopInstance(oldest);
        }
        inst = new Instance
        {
            Handle = _nextHandle++,
            SfxId = id,
            Source = source,
            Cancellable = cancellable,
            NoUpdate = noUpdate,
            FrozenPosition = noUpdate && source != null && !source.Self ? source.Position : null,
        };
        _instances.Add(inst);
        return true;
    }

    public override int PlaySample(int id, SoundSource? source, bool? loop, bool noUpdate,
        float recency, bool sourceOnly, bool cancellable)
    {
        if (_samples == null || id < 0 || id >= _samples.Count)
        {
            return -1;
        }
        short[] pcm = GetPcm(id); // decode outside the lock so the mixer never waits on it
        lock (_lock)
        {
            if (!SetUpInstance(id, source, loop.GetValueOrDefault(), recency, sourceOnly, cancellable, noUpdate, out Instance? inst))
            {
                return -1; // already playing (a held loop, or a recent one-shot): SfxInstance returns no handle either
            }
            if (!AddLayer(inst, id, pcm, relVolume: 1, pitchMul: 1, pan: null, loop ?? _samples[id].Loop, tableVolume: true))
            {
                StopInstance(inst);
                return -1;
            }
            return inst.Handle;
        }
    }

    public override void PlayDgn(int id, SoundSource? source, bool loop, bool noUpdate,
        float recency, bool cancellable, float amountA, float amountB)
    {
        if (_dgnFiles == null)
        {
            return;
        }
        int dgnId = id & 0x3FFF;
        if (dgnId < 0 || dgnId >= _dgnFiles.Count)
        {
            return;
        }
        DgnFile file = _dgnFiles[dgnId];
        var pcms = new short[file.Entries.Count][];
        for (int i = 0; i < file.Entries.Count; i++)
        {
            pcms[i] = GetPcm((int)file.Entries[i].SfxId);
        }
        lock (_lock)
        {
            if (!SetUpInstance(id, source, loop, recency, sourceOnly: true, cancellable, noUpdate, out Instance? inst))
            {
                // the same held sound from the same source: only its curve inputs change (the charge sounds, lava);
                // null = no free slot (TakeSlot)
                if (inst != null)
                {
                    UpdateDgn(inst, amountA, amountB);
                }
                return;
            }
            inst.Dgn = file;
            bool anyVolume = false;
            for (int i = 0; i < file.Entries.Count; i++)
            {
                (float volume, float pitch) = DgnCurves(file, file.Entries[i], amountA, amountB);
                anyVolume |= volume > 0;
                if (!AddLayer(inst, (int)file.Entries[i].SfxId, pcms[i], volume, pitch, pan: null, loop))
                {
                    StopInstance(inst);
                    return;
                }
            }
            if (!anyVolume)
            {
                StopInstance(inst); // SfxInstance.PlayDgn: "no volume was above 0"
            }
        }
    }

    public override void PlayScript(int id, SoundSource? source, bool noUpdate,
        float recency, bool sourceOnly, bool cancellable)
    {
        if (_sfxScripts == null)
        {
            return;
        }
        int scriptId = id & 0x3FFF;
        if (scriptId < 0 || scriptId >= _sfxScripts.Count)
        {
            return;
        }
        SfxScriptFile file = _sfxScripts[scriptId];
        if (file.Entries.Count == 0) // e.g. TELEPORT_ACTIVATE_SCR
        {
            return;
        }
        foreach (SfxScriptEntry e in file.Entries)
        {
            if ((e.SfxData & 0x8000) == 0)
            {
                GetPcm(e.SfxData & 0x3FFF); // decode now, outside the lock, rather than mid-Update
            }
        }
        lock (_lock)
        {
            if (!SetUpInstance(id, source, loop: false, recency, sourceOnly, cancellable, noUpdate, out Instance? inst))
            {
                return;
            }
            inst.Script = file;
        }
    }

    // driven by the game loop once per sim frame (Sfx.Update): SfxInstance.Update/UpdateScript
    public override void Update(float time)
    {
        lock (_lock)
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                Instance inst = _instances[i];
                if (inst.Removed)
                {
                    continue;
                }
                if (inst.Script != null)
                {
                    if (inst.Source == null || !Sfx.SfxMute)
                    {
                        UpdateScript(inst, time);
                    }
                    continue;
                }
                foreach (Layer layer in inst.Layers)
                {
                    if (layer.RelVolume == 0 && layer.Voice.Alive)
                    {
                        layer.Voice.Stopped = true; // a DGN layer whose curve reached zero volume
                    }
                }
                if (inst.AnyVoicePlaying)
                {
                    inst.PlayTime += time;
                    RefreshGains(inst);
                }
                else
                {
                    StopInstance(inst);
                }
            }
            _instances.RemoveAll(i => i.Removed);
            // every 2 s: flag an unusually busy mixer (a pile-up of one id means a request that should be reusing a
            // playing sound isn't)
            if (++_updates % 120 == 0)
            {
                int voices = _voices.Count;
                if (voices >= 24 && voices != _lastBusyVoices)
                {
                    string most = string.Join(", ", _instances.GroupBy(i => i.SfxId).OrderByDescending(g => g.Count())
                        .Take(3).Select(g => $"id {g.Key} x{g.Count()}"));
                    Log.Info("MPHAudio", $"sfx busy: {_instances.Count} instances, {voices} voices; most: {most}");
                }
                _lastBusyVoices = voices;
            }
        }
        UpdateStreams(time);
    }

    int _updates;
    int _lastBusyVoices;

    void UpdateScript(Instance inst, float time)
    {
        if (inst.Paused)
        {
            return;
        }
        inst.PlayTime += time * inst.ScriptRate / 30f;
        inst.Layers.RemoveAll(l => !l.Voice.Alive);
        SfxScriptFile script = inst.Script!;
        if (inst.ScriptIndex >= script.Entries.Count - 1 && inst.Layers.Count == 0)
        {
            StopInstance(inst);
            return;
        }
        for (int e = inst.ScriptIndex + 1; e < script.Entries.Count; e++)
        {
            SfxScriptEntry entry = script.Entries[e];
            if (entry.Delay > inst.PlayTime)
            {
                break;
            }
            inst.ScriptIndex = e;
            int sampleId = entry.SfxData & 0x3FFF;
            if ((entry.SfxData & 0x8000) != 0)
            {
                // a "stop" entry, not a sound
                if (inst.Source == null)
                {
                    StopSoundById(sampleId);
                }
                else
                {
                    StopSoundFromSource(inst.Source, sampleId);
                }
                continue;
            }
            if (inst.Layers.Count >= MaxPerInstance)
            {
                StopInstance(inst);
                return;
            }
            // -1 = no pan (positional); anything else is a fixed pan that overrides positional audio
            // SfxScriptEntry.Pan is (raw - 64) / 64 / 2 (MphRead's OpenAL scale); the DS pan register spans the full
            // -1..1 (raw 0 = hard left, 127 = hard right): the landing's zoom pair is panned hard L/R
            float? pan = entry.Pan > -1 ? System.Math.Clamp(entry.Pan * 2, -1f, 1f) : null;
            bool loop = (entry.SfxData & 0x4000) != 0;
            if (!AddLayer(inst, sampleId, GetPcm(sampleId), entry.Volume, entry.Pitch, pan, loop))
            {
                StopInstance(inst);
                return;
            }
        }
    }

    void UpdateDgn(Instance inst, float amountA, float amountB)
    {
        if (inst.Dgn == null)
        {
            return;
        }
        for (int i = 0; i < inst.Layers.Count && i < inst.Dgn.Entries.Count; i++)
        {
            (inst.Layers[i].RelVolume, inst.Layers[i].PitchMul) = DgnCurves(inst.Dgn, inst.Dgn.Entries[i], amountA, amountB);
        }
        RefreshGains(inst);
    }

    // SfxInstance.UpdateDgn: the entry's volume (0..1, including the file's own volume) and pitch multiplier
    static (float Volume, float PitchMul) DgnCurves(DgnFile file, DgnFileEntry entry, float amountA, float amountB)
    {
        float volumeA = GetDgnValue(entry.Data1, amountA);
        float volumeB = GetDgnValue(entry.Data2, amountB);
        float pitchA = GetDgnValue(entry.Data3, amountA);
        float pitchB = GetDgnValue(entry.Data4, amountB);
        float volumeFac = volumeA / 127f * volumeB;
        volumeFac = volumeFac / 127f * file.Header.InitialVolume / 127f;
        if (volumeFac < 1 / 130f)
        {
            volumeFac = 0;
        }
        // the game: pitchA * pitchB >> 13 (ARM9 0x2070b4c / 0x2070cb8). Not "0x2000f": C# reads that as the hex int
        // 0x2000F (131087), which put every DGN sound on the 0.25x rate floor (two octaves down, four times as long)
        float pitchFac = pitchA / 0x2000 * pitchB;
        if (pitchFac >= 0x4000)
        {
            pitchFac = 0x3FFF;
        }
        return (volumeFac, Sfx.CalculatePitchDiv(pitchFac));
    }

    bool AddLayer(Instance inst, int sampleId, short[] pcm, float relVolume, float pitchMul, float? pan, bool loop,
        bool tableVolume = false)
    {
        if (_samples == null || sampleId < 0 || sampleId >= _samples.Count)
        {
            LogOnce(sampleId, $"sample {sampleId} out of range (table has {_samples?.Count.ToString() ?? "no"} entries)");
            return false;
        }
        if (pcm.Length == 0)
        {
            LogOnce(sampleId, $"sample {sampleId} decoded to 0 samples");
            return false;
        }
        SoundSample sample = _samples[sampleId];
        int loopStart = sample.Loop && sample.LoopStart > 0 && sample.LoopStart < pcm.Length ? sample.LoopStart : 0;
        var layer = new Layer
        {
            SampleId = sampleId,
            RelVolume = relVolume,
            TableVolume = tableVolume,
            PitchMul = pitchMul,
            Pan = pan,
            Voice = new Voice { Pcm = pcm, Loop = loop, LoopStart = loopStart },
        };
        inst.Layers.Add(layer);
        ApplyGain(inst, layer);
        _voices.Add(layer.Voice);
        if (_voices.Count > MaxVoices)
        {
            // hard cap on simultaneous voices: drop the oldest (never a stream's channel)
            int oldest = _voices.FindIndex(x => !x.Stream);
            _voices[oldest].Stopped = true;
            _voices.RemoveAt(oldest);
        }
        LogOnce(sampleId, $"sample {sampleId}: {pcm.Length} samples @ {sample.SampleRate} Hz, loop {loop}" +
            (loopStart > 0 ? $" from {loopStart}" : "") + $", vol L{layer.Voice.VolL:0.00}/R{layer.Voice.VolR:0.00}, " +
            $"pitch x{pitchMul:0.00}, from {(inst.Dgn != null ? "DGN" : inst.Script != null ? "script" : "sample")} id {inst.SfxId}");
        return true;
    }

    void RefreshGains(Instance inst)
    {
        foreach (Layer layer in inst.Layers)
        {
            if (layer.Voice.Alive)
            {
                ApplyGain(inst, layer);
            }
        }
    }

    // The DS plays a sound effect on a hardware channel on the same scale as the music channels (BizHawk SOUNDxCNT:
    // BEAM, table vol 110 -> channel vol 111; BEAM_CHARGE2, 96 -> 97), so no extra SFX scale. MphRead desktop's
    // Sfx.Volume default (0.35) with a full-volume-per-side pan put every effect 3.1 dB under vanilla at centre and
    // 9.1 dB under at a hard pan (UI session's mix measurements, handoffs/ui-to-music_2026-10-01.md 2b).
    const float SfxDsGain = 1f;

    // SfxDsGain * the layer's own scale (1 for a plain sample, the DGN curve's volume, or a script entry's volume)
    // * the SoundTable volume (plain samples only, see Layer.TableVolume) * mute * the source's volume, then distance
    // attenuation + pan for a positional, non-self source (SoundInstance.UpdateParameters/UpdatePosition). A script
    // entry's fixed pan is relative and unattenuated, like the OpenAL path's SourceRelative + RolloffFactor 0.
    void ApplyGain(Instance inst, Layer layer)
    {
        SoundSample sample = _samples![layer.SampleId];
        SoundSource? source = inst.Source;
        float mute = Sfx.SfxMute && source != null ? 0 : 1;
        float vol = SfxDsGain * layer.RelVolume * (layer.TableVolume ? sample.Volume : 1) * mute * (source?.Volume ?? 1);
        float pan = layer.Pan ?? 0;
        if (source != null && !source.Self && layer.Pan == null)
        {
            Vector3 listenerPos = GetListenerPosition();
            Vector3 toSource = (inst.FrozenPosition ?? source.Position) - listenerPos;
            float dist = toSource.Length;
            float refDist = source.ReferenceDistance, maxDist = source.MaxDistance;
            vol *= Falloff(dist, refDist, maxDist);
            if (dist > 0.001f)
            {
                Vector3 facing = GetListenerFacing();
                Vector3 up = GetListenerUp();
                Vector3 right3 = Vector3.Cross(facing, up).Normalized();
                pan = System.Math.Clamp(Vector3.Dot(right3, toSource.Normalized()), -1f, 1f);
            }
        }
        vol = System.Math.Clamp(vol, 0f, 1f);
        // the DS mixer's pan law (pan register 0..127: left (128 - pan) / 128, right pan / 128): linear, half per side
        // at centre, all of it on one side at a hard pan
        layer.Voice.VolL = vol * (1 - pan) * 0.5f;
        layer.Voice.VolR = vol * (1 + pan) * 0.5f;
        float rate = System.Math.Clamp(layer.PitchMul, 0.25f, 4f);
        layer.Voice.Step = sample.SampleRate * (double)rate / _mixRate;
    }

    static float Falloff(float dist, float refDist, float maxDist)
    {
        return dist <= refDist ? 1f
            : maxDist <= refDist ? 0f
            : System.Math.Clamp(1f - (dist - refDist) / (maxDist - refDist), 0f, 1f);
    }

    // How many instances of one id may play at once from sourced (3D) requests: the ROM's per-id slot counts
    // (SNDTBLS / DGNFILES / SFXSCRIPTFILES SlotCount; the game preallocates that many 0x28-byte instances per id when
    // the room's sounds load, ARM9 0x2073c84, a negative count meaning 3). A count of 0 means the game's 3D path can't
    // play the id at all; left unlimited here so nothing that plays today goes silent.
    int SlotLimit(int id)
    {
        int index = id & 0x3FFF;
        int count = (id & 0x8000) != 0 ? (_dgnFiles != null && index < _dgnFiles.Count ? (sbyte)_dgnFiles[index].Header.SlotCount : -1)
            : (id & 0x4000) != 0 ? (_sfxScripts != null && index < _sfxScripts.Count ? (sbyte)_sfxScripts[index].Header.SlotCount : -1)
            : id < _sampleSlots.Length ? _sampleSlots[id] : -1;
        return count < 0 ? 3 : count == 0 ? int.MaxValue : count;
    }

    // what the game compares when it has to reuse a slot: the source's current 3D volume (the start-time copy for a
    // noUpdate sound) -- here the source volume times distance falloff; 1 for a positionless/self source
    float Audibility(SoundSource source, Vector3? frozen)
    {
        if (source.Self)
        {
            return source.Volume;
        }
        float dist = ((frozen ?? source.Position) - GetListenerPosition()).Length;
        return source.Volume * Falloff(dist, source.ReferenceDistance, source.MaxDistance);
    }

    // ARM9 0x2073964 (from the 3D play, 0x2073674): a free slot of this id if there is one; else take over the quietest
    // playing instance of the id that is no louder than the new sound; else this source's own oldest instance of it;
    // else the request is dropped. Without this every repeat of a one-shot (a Magmaul shot's 5-8 bounces, a hit sound
    // requested every frame) stacked a new voice on the old ones.
    bool TakeSlot(int id, SoundSource source, bool noUpdate)
    {
        List<Instance> same = _instances.FindAll(i => i.SfxId == id && i.Source != null && i.IsPlaying);
        if (same.Count < SlotLimit(id))
        {
            return true;
        }
        Instance? victim = null;
        float quietest = Audibility(source, noUpdate && !source.Self ? source.Position : null);
        foreach (Instance i in same)
        {
            float a = Audibility(i.Source!, i.FrozenPosition);
            if (a <= quietest)
            {
                quietest = a;
                victim = i;
            }
        }
        if (victim == null)
        {
            float oldest = -1;
            foreach (Instance i in same)
            {
                if (ReferenceEquals(i.Source, source) && i.PlayTime >= oldest)
                {
                    oldest = i.PlayTime;
                    victim = i;
                }
            }
        }
        if (victim == null)
        {
            return false;
        }
        StopInstance(victim);
        return true;
    }

    void StopInstance(Instance inst)
    {
        foreach (Layer layer in inst.Layers)
        {
            layer.Voice.Stopped = true;
        }
        inst.Removed = true;
    }

    // the game's own stop (StopSfxHandle and the stops built on it): the channel's release, not a cut (Voice.Releasing)
    void ReleaseInstance(Instance inst)
    {
        foreach (Layer layer in inst.Layers)
        {
            layer.Voice.Releasing = true;
        }
        inst.Removed = true;
    }

    // WaveData is headerless raw PCM: 16-bit little-endian for MPH's ADPCM-sourced samples (SoundRead.GetWaveData's
    // ADPCM branch writes 16-bit values), 8-bit unsigned for the rarer PCM8 case (its bytes are the signed source
    // XORed with 0x80 -- the standard signed<->unsigned PCM8 swap) -- expanded to 16-bit here either way, since the
    // mixer only deals in one format. Decoded once per id and cached in RAM (no disk I/O at all).
    short[] GetPcm(int sampleId)
    {
        if (_samples == null || sampleId < 0 || sampleId >= _samples.Count)
        {
            return System.Array.Empty<short>();
        }
        lock (_lock)
        {
            if (_pcmCache.TryGetValue(sampleId, out short[]? cached))
            {
                return cached;
            }
        }
        SoundSample sample = _samples[sampleId];
        byte[] raw;
        try
        {
            raw = sample.WaveData.Value;
        }
        catch (System.Exception ex)
        {
            Log.Error("MPHAudio", $"sample {sampleId} WaveData decode failed: {ex}");
            raw = System.Array.Empty<byte>();
        }
        short[] pcm;
        if (sample.Format == WaveFormat.PCM8)
        {
            pcm = new short[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                pcm[i] = (short)(((int)raw[i] - 128) * 256);
            }
        }
        else
        {
            pcm = new short[raw.Length / 2];
            System.Buffer.BlockCopy(raw, 0, pcm, 0, pcm.Length * 2);
        }
        lock (_lock)
        {
            _pcmCache[sampleId] = pcm;
        }
        return pcm;
    }

    // ported from SfxInstance.GetDgnValue -- pure breakpoint-curve math: amount (0..0xFFFF, the game's own units) ->
    // a volume-% or pitch-% value
    static float GetDgnValue(IReadOnlyList<DgnData> data, float amount)
    {
        DgnData first = data[0];
        if (amount <= first.Amount)
        {
            return first.Value & 0x3FFF;
        }
        DgnData last = data[^1];
        if (amount >= last.Amount)
        {
            return last.Value & 0x3FFF;
        }
        if (data.Count == 1)
        {
            return 0;
        }
        DgnData d1, d2;
        int i = 0;
        while (true)
        {
            d1 = data[i];
            d2 = data[i + 1];
            if (amount < d2.Amount)
            {
                break;
            }
            if (++i >= data.Count - 1)
            {
                return 0;
            }
        }
        float diff = amount - d1.Amount;
        float ratio = diff / (d2.Amount - d1.Amount);
        float v1 = d1.Value & 0x3FFF;
        float v2 = d2.Value & 0x3FFF;
        int flags2 = d2.Value & 0xC000;
        if (flags2 == 0x4000)
        {
            return v1 + (v2 - v1) * System.MathF.Sin(System.MathF.PI / 2 * ratio);
        }
        return v1 + (v2 - v1) * ratio; // linear (the OpenAL path asserts false on the one unused curve-shape flag)
    }

    // ---------------------------------------------------------------- streams (SDAT STRMs)

    // The ROM's 12 STRMs (data/sound/sound_data.sdat, IMA-ADPCM at 22767 Hz): the title screen's BGM_TITLE_MUSIC
    // (stereo, 21 s, no loop) and the voices (mono, ~2 s: VOICE_EVACUATE three times when a campaign escape starts, the
    // multiplayer announcer). MphRead's SoundRead.ReadSdat decodes them all to PCM16 (~3 MB); LoadTables starts it on a
    // worker. Each channel is a voice on this mixer: hard left/right for stereo (the NNS stream pans 0 and 127), centred
    // for mono. The game plays one stream at a time; MphRead's queue (SfxInstance.QueueStream/UpdateStreams) is
    // mirrored below.
    System.Threading.Tasks.Task<IReadOnlyList<SoundStream>>? _streamLoad;
    readonly Dictionary<int, (short[][] Channels, int Rate, float Gain)> _streamPcm = new();
    readonly List<Voice> _streamVoices = new(); // the playing stream's channels, under _lock
    readonly List<QueuedStream> _streamQueue = new(); // under _lock; [0] is the one playing once Playing is set
    const int MaxQueuedStreams = 16; // SfxInstance's _inactiveQueue

    sealed class QueuedStream
    {
        public int Id;
        public float Delay, Expiration;
        public bool Playing;
    }

    void StartStreamLoad()
    {
        _streamLoad ??= System.Threading.Tasks.Task.Run(() => SoundRead.ReadSdat().Streams);
    }

    // The DS's stream volume (MPH's stream wrapper, rev 1 arm9 0x2074938, over the NNS stream update 0x207F4D8): the
    // STRM's own SDAT volume stays on the stream and the wrapper fades it to info * master >> 7 (master 127 = the music
    // volume option at its default); each part goes through the SDK's decibel table (0x20B3A44, exactly
    // round(200 log10(v / 127)) in 0.1 dB), so dB = T[info] + T[info * 127 >> 7]. Title (info 70): -10.5 dB; BizHawk
    // title.State shows its channels (SOUND6/7) at vol 76 shift 1 = 0.297, pans 0/127. A voice (127): -0.1 dB.
    static float StreamGain(int info)
    {
        static int T(int v) => (int)System.Math.Round(200 * System.Math.Log10(v / 127.0));
        int fader = info * 127 >> 7;
        return info <= 0 || fader <= 0 ? 0 : (float)System.Math.Pow(10, (T(info) + T(fader)) / 200.0);
    }

    // a stream's PCM per channel; waits for the worker's decode only if a stream is asked for that early. Outside _lock
    // (the mixer takes it every chunk).
    bool TryStreamPcm(int id, out (short[][] Channels, int Rate, float Gain) pcm)
    {
        if (_streamPcm.TryGetValue(id, out pcm))
        {
            return true;
        }
        IReadOnlyList<SoundStream> streams;
        try
        {
            StartStreamLoad();
            if (!_streamLoad!.IsCompleted)
            {
                Log.Info("MPHAudio", $"stream {id}: waiting for the stream decode");
            }
            streams = _streamLoad.Result;
        }
        catch (System.Exception ex)
        {
            LogOnce(-2, "streams unavailable: " + ex.Message);
            return false;
        }
        if (id < 0 || id >= streams.Count)
        {
            return false;
        }
        SoundStream stream = streams[id];
        var channels = new short[stream.Channels.Count][];
        for (int c = 0; c < channels.Length; c++)
        {
            byte[] data = stream.Channels[c];
            // ADPCM (all of MPH's) is decoded to PCM16 LE; PCM8 stays signed bytes
            channels[c] = stream.Format == WaveFormat.PCM8
                ? data.Select(b => (short)((sbyte)b << 8)).ToArray()
                : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data).ToArray();
        }
        pcm = (channels, stream.SampleRate, StreamGain((int)System.Math.Round(stream.Volume * 127)));
        _streamPcm[id] = pcm;
        return true;
    }

    bool StreamAliveLocked() => _streamVoices.Exists(v => v.Alive);

    void CutStreamLocked()
    {
        foreach (Voice v in _streamVoices)
        {
            v.Stopped = true;
        }
        _streamVoices.Clear();
    }

    // the game's PlayStream (rev 1 0x20720B0): StopStream(0), then the stream from its start
    void StartStream(int id)
    {
        if (!TryStreamPcm(id, out var pcm))
        {
            lock (_lock)
            {
                CutStreamLocked();
            }
            return;
        }
        int n = pcm.Channels.Length;
        lock (_lock)
        {
            CutStreamLocked();
            for (int c = 0; c < n; c++)
            {
                int pan = n == 1 ? 64 : c == 0 ? 0 : 127;
                var v = new Voice
                {
                    Pcm = pcm.Channels[c],
                    Stream = true,
                    Step = pcm.Rate / (double)_mixRate,
                    VolL = pcm.Gain * (128 - pan) / 128f, // the DS mixer's pan law, as ApplyGain
                    VolR = pcm.Gain * pan / 128f
                };
                _streamVoices.Add(v);
                _voices.Add(v); // added together: the channels start in the same mixer chunk
            }
        }
        int length = n > 0 ? pcm.Channels[0].Length : 0;
        Log.Info("MPHAudio", $"stream {id}: {n} ch @ {pcm.Rate} Hz, {length / (float)pcm.Rate:0.0} s, " +
            $"gain {pcm.Gain:0.000} ({20 * System.Math.Log10(pcm.Gain):0.0} dB)");
    }

    // MphRead's PlayFreeStream: the queue emptied, this stream now (the front end's page music uses it as the game's
    // PlayStream)
    public override void PlayFreeStream(int id)
    {
        lock (_lock)
        {
            _streamQueue.Clear();
            _streamQueue.Add(new QueuedStream { Id = id });
        }
        UpdateStreams(0);
    }

    public override void QueueStream(int id, float delay, float expiration)
    {
        lock (_lock)
        {
            if (_streamQueue.Count < MaxQueuedStreams)
            {
                _streamQueue.Add(new QueuedStream { Id = id, Delay = delay, Expiration = expiration });
            }
        }
    }

    // the game's StopStream(fade): the queue emptied and the playing stream faded out over `fadeFrames` sound frames
    // (60 Hz in the front end, 30 in game) -- cut at once with 0
    public void StopStream(int fadeFrames = 0, float frameRate = 60)
    {
        lock (_lock)
        {
            _streamQueue.Clear();
            if (fadeFrames <= 0)
            {
                CutStreamLocked();
                return;
            }
            float step = frameRate / (fadeFrames * (float)_mixRate);
            foreach (Voice v in _streamVoices)
            {
                v.FadeStep = step;
            }
        }
    }

    // the game's !IsStreamStopped: a fade-out still counts as playing
    public bool StreamPlaying
    {
        get
        {
            lock (_lock)
            {
                return StreamAliveLocked();
            }
        }
    }

    // SfxInstance.UpdateStreams, once per Update: the head of the queue plays once its delay is over and leaves the
    // queue when it ends; the others count their delays down, and expire (when given an expiration) while waiting
    void UpdateStreams(float time)
    {
        int start = -1;
        lock (_lock)
        {
            int index = 0;
            for (int i = 0; i < _streamQueue.Count; index++)
            {
                QueuedStream item = _streamQueue[i];
                if (index == 0 && item.Playing)
                {
                    if (!StreamAliveLocked())
                    {
                        _streamQueue.RemoveAt(i);
                        continue; // the next one counts as index 1 this frame, as in SfxInstance
                    }
                }
                else if (item.Delay > 0)
                {
                    item.Delay = System.Math.Max(0, item.Delay - time);
                }
                if (item.Delay == 0 && !item.Playing && index == 0)
                {
                    item.Playing = true;
                    start = item.Id;
                    i++;
                    continue;
                }
                if (index > 0 && item.Expiration > 0)
                {
                    item.Expiration -= time;
                    if (item.Expiration <= 0)
                    {
                        _streamQueue.RemoveAt(i);
                        continue;
                    }
                }
                i++;
            }
        }
        if (start >= 0)
        {
            StartStream(start);
        }
    }

    public override void StopSoundByHandle(int handle)
    {
        if (handle < 0)
        {
            return;
        }
        lock (_lock)
        {
            _instances.FindAll(i => i.Handle == handle).ForEach(ReleaseInstance);
        }
    }

    // SfxInstance.StopSoundFromSource: only looping, cancellable or forced sounds stop; a forced stop spares noUpdate ones
    public override void StopSoundFromSource(SoundSource source, bool force)
    {
        lock (_lock)
        {
            _instances.FindAll(i => ReferenceEquals(i.Source, source)
                && (force || i.IsLooping || i.Cancellable) && (!force || !i.NoUpdate)).ForEach(StopInstance);
        }
    }

    public override void StopSoundFromSource(SoundSource source, int id)
    {
        lock (_lock)
        {
            _instances.FindAll(i => ReferenceEquals(i.Source, source) && i.SfxId == id).ForEach(StopInstance);
        }
    }

    // SfxInstance.StopAllSound: sourceless one-shots survive an unforced stop
    public override void StopAllSound(bool force = false)
    {
        lock (_lock)
        {
            _instances.FindAll(i => force || i.Source != null || i.IsLooping).ForEach(StopInstance);
        }
    }

    public override void StopSoundById(int id)
    {
        lock (_lock)
        {
            _instances.FindAll(i => i.SfxId == id).ForEach(ReleaseInstance);
        }
    }

    public override void StopFreeSfxScripts()
    {
        lock (_lock)
        {
            _instances.FindAll(i => i.Script != null).ForEach(ReleaseInstance);
        }
    }

    public override void SetPausedFreeSfxScripts(bool paused)
    {
        lock (_lock)
        {
            foreach (Instance inst in _instances)
            {
                if (inst.Script != null)
                {
                    inst.Paused = paused;
                }
            }
        }
    }

    public override bool IsHandlePlaying(int handle)
    {
        lock (_lock)
        {
            return handle >= 0 && _instances.Exists(i => i.Handle == handle && i.IsPlaying);
        }
    }

    public override int CountPlayingSfx(int id)
    {
        lock (_lock)
        {
            return _instances.Count(i => i.SfxId == id && i.IsPlaying);
        }
    }

    public override int CountSourcePlayingSfx(int id, SoundSource source)
    {
        lock (_lock)
        {
            return _instances.Count(i => i.SfxId == id && ReferenceEquals(i.Source, source) && i.IsPlaying);
        }
    }
}
