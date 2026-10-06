using System.Collections.Generic;
using System.Threading;
using MphRecomp.App.Platform;
using MphRecomp.Arenas;
using MphRecomp.Import.Retro;

namespace MphRecomp.App;

// Streamed music on its own PCM track, next to the sequenced music: Echoes' RS03 streams in the imported arenas
// (ArenaMusic drives it through MphRead's Music hooks). GameMusicPlayer owns one (its Stream) and passes on its pause,
// resume, OPTIONS > AUDIO volume and disposal, so whatever pauses the music (the pause menu, the app going to the
// background) pauses this too. A replaced stream fades out under the new one, so up to two voices mix at once.
internal sealed class StreamMusicPlayer : ArenaMusic.IOutput, System.IDisposable
{
    const int ChunkFrames = 512;

    sealed class Voice
    {
        public required RetroStreamReader Reader;
        public required int Channels;
        public required float Gain;
        public float Level; // fade level, 0..1
        public float Step; // per frame: up while fading in, down while fading out
        public long Delay; // frames of silence before it starts
        public bool Leaving; // fading out: dropped at level 0
    }

    readonly object _gate = new();
    readonly List<Voice> _voices = new(); // under _gate
    readonly short[] _pcm = new short[ChunkFrames * 2];
    readonly float[] _mix = new float[ChunkFrames * 2];
    // set when there's something to play; the pump waits on it while idle instead of spinning
    readonly ManualResetEventSlim _wake = new(false);
    // reset while the host has the music paused (a paused track takes writes at once, so the pump would spin)
    readonly ManualResetEventSlim _resume = new(true);
    IPcmTrack? _track;
    int _rate;
    // bumped by Cut: a chunk rendered before it is dropped, not written after the cut
    int _cuts;
    Thread? _thread;
    volatile bool _hostPaused;
    volatile bool _disposed;
    volatile float _master = 1;

    public float MasterVolume
    {
        get => _master;
        set
        {
            lock (_gate)
            {
                _master = value;
                _track?.SetVolume(value);
            }
        }
    }

    public void Start(RetroStream stream, float gain, float fadeIn, float delay, float oldFadeOut)
    {
        lock (_gate)
        {
            if (_disposed) return;
            int rate = stream.SampleRate;
            if (oldFadeOut <= 0 || rate != _rate)
            {
                Cut();
            }
            else
            {
                foreach (Voice v in _voices) FadeOut(v, oldFadeOut);
            }
            EnsureTrack(rate);
            _voices.Add(new Voice
            {
                Reader = stream.OpenReader(),
                Channels = stream.Channels,
                Gain = gain,
                Level = fadeIn > 0 ? 0 : 1,
                Step = fadeIn > 0 ? 1f / (fadeIn * rate) : 0,
                Delay = (long)(delay * rate),
            });
            if (!_hostPaused) _track?.Play();
            _wake.Set();
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "StreamMusic" };
                _thread.Start();
            }
        }
    }

    public void Stop(float fadeOut)
    {
        lock (_gate)
        {
            if (fadeOut <= 0)
            {
                Cut();
            }
            else
            {
                foreach (Voice v in _voices) FadeOut(v, fadeOut);
            }
        }
    }

    // GameMusicPlayer.Pause / Play
    public void Pause()
    {
        lock (_gate)
        {
            _hostPaused = true;
            _resume.Reset();
            _track?.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _hostPaused = false;
            if (_voices.Count > 0) _track?.Play();
            _resume.Set();
        }
    }

    void FadeOut(Voice v, float seconds)
    {
        v.Leaving = true;
        v.Step = -1f / (seconds * _rate);
    }

    // under _gate: drop every voice and what the track already holds
    void Cut()
    {
        _cuts++;
        _voices.Clear();
        if (_track != null)
        {
            _track.Pause();
            _track.Flush();
        }
    }

    // under _gate
    void EnsureTrack(int rate)
    {
        if (_track != null && rate == _rate) return;
        _track?.Release();
        _rate = rate;
        // a quarter second of stereo float, as GameMusicPlayer's
        _track = AppPlatform.Audio.CreateTrack(rate, 2, PcmFormat.Float32, rate / 4, PcmUsage.Music);
        _track.SetVolume(_master);
        Log.Info("MPHAudio", $"stream music track: {rate} Hz, {_track.Describe()}");
    }

    void Run()
    {
        try
        {
            while (!_disposed)
            {
                IPcmTrack? track;
                int frames, cuts;
                lock (_gate)
                {
                    if (_voices.Count == 0)
                    {
                        _wake.Reset();
                        track = null;
                        frames = 0;
                    }
                    else
                    {
                        track = _track;
                        frames = Render();
                    }
                    cuts = _cuts;
                }
                if (track == null)
                {
                    _wake.Wait(1000);
                    continue;
                }
                Write(track, frames * 2, cuts);
            }
        }
        catch (System.Exception ex)
        {
            Log.Error("MPHAudio", $"stream music pump stopped: {ex}");
        }
    }

    // under _gate: the next chunk of every voice, mixed into _mix; returns the frames
    int Render()
    {
        System.Array.Clear(_mix);
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            Voice v = _voices[i];
            int f = 0;
            if (v.Delay > 0)
            {
                f = (int)System.Math.Min(v.Delay, ChunkFrames);
                v.Delay -= f;
            }
            if (f < ChunkFrames)
            {
                int n = v.Reader.Read(_pcm, ChunkFrames - f);
                int ch = v.Channels;
                const float Scale = 1f / 32768;
                for (int k = 0; k < n; k++)
                {
                    float g = v.Gain * v.Level * Scale;
                    float l = _pcm[k * ch];
                    float r = ch == 2 ? _pcm[k * 2 + 1] : l;
                    _mix[(f + k) * 2] += l * g;
                    _mix[(f + k) * 2 + 1] += r * g;
                    if (v.Step != 0)
                    {
                        v.Level = System.Math.Clamp(v.Level + v.Step, 0, 1);
                        if (v.Level == 1 && v.Step > 0) v.Step = 0;
                    }
                }
            }
            if (v.Reader.Finished || (v.Leaving && v.Level <= 0))
            {
                _voices.RemoveAt(i);
            }
        }
        return ChunkFrames;
    }

    void Write(IPcmTrack track, int count, int cuts)
    {
        int offset = 0;
        while (offset < count && !_disposed)
        {
            if (cuts != Volatile.Read(ref _cuts)) return;
            if (_hostPaused)
            {
                _resume.Wait(100);
                continue;
            }
            int w = track.Write(_mix, offset, count - offset, blocking: true);
            if (w < 0)
            {
                Log.Warn("MPHAudio", $"stream music: Write returned {w}");
                Thread.Sleep(20);
                return;
            }
            if (w == 0)
            {
                // cut or paused between the check and the write: let the gate settle
                Thread.Sleep(5);
                lock (_gate)
                {
                    if (!ReferenceEquals(track, _track) || _voices.Count == 0 || cuts != _cuts) return;
                }
            }
            offset += w;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _voices.Clear();
            _wake.Set();
            _resume.Set();
            _track?.Pause();
            _track?.Flush();
        }
        _thread?.Join(500);
        lock (_gate)
        {
            _track?.Release();
            _track = null;
        }
    }
}
