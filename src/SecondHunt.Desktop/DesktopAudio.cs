using MphRecomp.App.Platform;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SecondHunt.Desktop;

// The game's sound on Windows: one output device (miniaudio through SoundFlow, the library MphRead's own desktop music
// already uses) at 48 kHz stereo float, and a small mixer over the app's PCM tracks (IAudioOutput: the music, the SFX
// mixer, movie sound). Each track is a ring buffer its owner fills from its own thread, the way an Android AudioTrack
// is: a blocking write waits for room while the track plays, a paused one takes what fits and returns. Tracks at other
// rates (the DS's 32728 Hz music, movie sound) are resampled with a windowed-sinc filter, as Android's mixer does.
internal sealed class DesktopAudio : IAudioOutput, IDisposable
{
    public const int Rate = 48000;
    const int PeriodFrames = 480; // 10 ms

    readonly MiniAudioEngine _engine;
    readonly AudioPlaybackDevice _device;
    readonly Mixer _mixer;

    public int NativeRate => Rate;
    public int NativeBurstFrames => PeriodFrames;

    DesktopAudio(MiniAudioEngine engine, AudioPlaybackDevice device, Mixer mixer)
    {
        _engine = engine;
        _device = device;
        _mixer = mixer;
    }

    // the default output device, or null (no sound device: the game runs silent)
    public static DesktopAudio? TryOpen()
    {
        try
        {
            // Windows' own audio stack first (shared-mode WASAPI), the older ones only if it won't open
            var engine = new MiniAudioEngine(new[] { MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound, MiniAudioBackend.WinMm });
            var format = new AudioFormat { SampleRate = Rate, Channels = 2, Format = SampleFormat.F32 };
            var config = new MiniAudioDeviceConfig { PeriodSizeInFrames = PeriodFrames };
            AudioPlaybackDevice device = engine.InitializePlaybackDevice(null, format, config);
            var mixer = new Mixer(engine, format);
            device.MasterMixer.AddComponent(mixer);
            device.Start();
            Log.Info("MPHAudio", $"sound device: {device.Info?.Name ?? "default"}, {Rate} Hz, {PeriodFrames}-frame periods ({engine.ActiveBackend})");
            return new DesktopAudio(engine, device, mixer);
        }
        catch (Exception ex)
        {
            Log.Error("MPHAudio", "no sound device, playing silent: " + ex.Message);
            return null;
        }
    }

    public IPcmTrack CreateTrack(int sampleRate, int channels, PcmFormat format, int bufferFrames, PcmUsage usage,
        bool lowLatency = false)
    {
        // at least two device periods' worth at the track's own rate, so a blocking writer always gets ahead of the device
        int minimum = (int)Math.Ceiling(2.0 * PeriodFrames * sampleRate / Rate) + Track.Taps;
        var track = new Track(_mixer, sampleRate, channels, Math.Max(bufferFrames, minimum));
        _mixer.Add(track);
        return track;
    }

    // for the log: what the device has played so far, and the loudest sample since the last call
    public string Stats()
    {
        string s = $"{_mixer.FramesRendered} frames played ({_mixer.FramesRendered / (double)Rate:0.0} s), peak {_mixer.Peak:0.000}";
        _mixer.Peak = 0;
        return s;
    }

    public void RaiseThreadPriority()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
    }

    public void Dispose()
    {
        try
        {
            _device.Stop();
            _device.Dispose();
            _engine.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHAudio", "sound device close: " + ex.Message);
        }
    }

    // the device's one input: every playing track, mixed
    sealed class Mixer : SoundComponent
    {
        readonly object _gate = new();
        Track[] _tracks = Array.Empty<Track>();

        public Mixer(AudioEngine engine, AudioFormat format) : base(engine, format) { }

        public override string Name { get; set; } = "Second Hunt";

        public void Add(Track t)
        {
            lock (_gate) _tracks = _tracks.Append(t).ToArray();
        }

        public void Remove(Track t)
        {
            lock (_gate) _tracks = _tracks.Where(x => x != t).ToArray();
        }

        public long FramesRendered;
        public float Peak;

        protected override void GenerateAudio(Span<float> buffer, int channels)
        {
            FramesRendered += buffer.Length / Math.Max(1, channels);
            buffer.Clear();
            Track[] tracks = _tracks;
            foreach (Track t in tracks)
            {
                t.MixInto(buffer, channels);
            }
            foreach (float v in buffer) Peak = Math.Max(Peak, Math.Abs(v));
        }
    }

    sealed class Track : IPcmTrack
    {
        // windowed sinc, 16 taps, 256 phases (Kaiser beta 8): flat to ~20 kHz from the DS's 32.7 kHz, images below -80 dB
        public const int Taps = 16;
        const int Half = Taps / 2, Phases = 256;
        static readonly float[] _upKernel = Kernel(1.0);

        readonly Mixer _mixer;
        readonly int _rate, _channels;
        readonly double _step; // source frames per output frame
        readonly float[] _kernel;
        readonly object _gate = new();
        readonly float[] _left, _right; // the ring, already stereo
        readonly int _capacity;
        long _readFrame, _writeFrame; // absolute frame numbers; the ring keeps Half frames behind _readFrame for the filter
        double _frac;
        long _played;
        bool _playing, _stopping, _released;
        float _gain = 1, _lastGain = 1;

        public Track(Mixer mixer, int rate, int channels, int bufferFrames)
        {
            _mixer = mixer;
            _rate = rate;
            _channels = channels;
            _step = rate / (double)Rate;
            _kernel = rate <= Rate ? _upKernel : Kernel(Rate / (double)rate);
            _capacity = bufferFrames + Taps;
            _left = new float[_capacity];
            _right = new float[_capacity];
            _readFrame = _writeFrame = Half; // Half frames of silence before the first sample, for the filter's history
        }

        static float[] Kernel(double cutoff)
        {
            var k = new float[(Phases + 1) * Taps];
            double i0Beta = BesselI0(8);
            for (int p = 0; p <= Phases; p++)
            {
                double frac = p / (double)Phases;
                double sum = 0;
                for (int t = 0; t < Taps; t++)
                {
                    double x = t - (Half - 1) - frac; // tap t sits at source frame (index - Half + 1 + t)
                    double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x * cutoff) / (Math.PI * x * cutoff);
                    double r = x / Half;
                    double w = Math.Abs(r) >= 1 ? 0 : BesselI0(8 * Math.Sqrt(1 - r * r)) / i0Beta;
                    k[p * Taps + t] = (float)(sinc * w);
                    sum += sinc * w;
                }
                for (int t = 0; t < Taps; t++) k[p * Taps + t] = (float)(k[p * Taps + t] / sum); // unity gain at DC
            }
            return k;
        }

        static double BesselI0(double x)
        {
            double sum = 1, term = 1;
            for (int k = 1; k < 30; k++)
            {
                term *= (x / (2 * k)) * (x / (2 * k));
                sum += term;
            }
            return sum;
        }

        int Free => _capacity - Taps - (int)(_writeFrame - _readFrame);

        public int Write(float[] data, int offset, int count, bool blocking) => Write(data.AsSpan(offset, count), blocking);

        [ThreadStatic] static float[]? _scratch;

        public int Write(short[] data, int offset, int count, bool blocking)
        {
            float[] f = _scratch is { } s && s.Length >= count ? s : _scratch = new float[Math.Max(count, 4096)];
            for (int i = 0; i < count; i++) f[i] = data[offset + i] / 32768f;
            return Write(f.AsSpan(0, count), blocking);
        }

        int Write(ReadOnlySpan<float> data, bool blocking)
        {
            int frames = data.Length / _channels, done = 0;
            lock (_gate)
            {
                while (done < frames)
                {
                    if (_released) return -1;
                    int n = Math.Min(frames - done, Free);
                    for (int i = 0; i < n; i++)
                    {
                        int at = (int)(_writeFrame % _capacity);
                        int src = (done + i) * _channels;
                        _left[at] = data[src];
                        _right[at] = _channels == 2 ? data[src + 1] : data[src]; // mono: full level on both sides
                        _writeFrame++;
                    }
                    done += n;
                    if (done == frames || !blocking || !_playing) break;
                    Monitor.Wait(_gate, 50); // the device reads, then pulses
                }
            }
            return done * _channels;
        }

        // the device thread: this track's next output frames, resampled, added to the mix
        public void MixInto(Span<float> mix, int channels)
        {
            lock (_gate)
            {
                if (!_playing || _released) return;
                int frames = mix.Length / channels;
                float g0 = _lastGain, g1 = _gain;
                for (int f = 0; f < frames; f++)
                {
                    // the filter needs Half frames past the read position
                    if (_writeFrame - _readFrame < Half + 1)
                    {
                        if (_stopping)
                        {
                            _playing = _stopping = false; // played out
                        }
                        break; // an underrun: silence until the writer catches up
                    }
                    float l = 0, r = 0;
                    if (_step == 1)
                    {
                        int at = (int)(_readFrame % _capacity);
                        l = _left[at];
                        r = _right[at];
                    }
                    else
                    {
                        int phase = (int)(_frac * Phases);
                        int k = phase * Taps;
                        long first = _readFrame - Half + 1;
                        for (int t = 0; t < Taps; t++)
                        {
                            int at = (int)((first + t) % _capacity);
                            float c = _kernel[k + t];
                            l += _left[at] * c;
                            r += _right[at] * c;
                        }
                    }
                    float g = g0 + (g1 - g0) * f / frames;
                    mix[f * channels] += l * g;
                    if (channels > 1) mix[f * channels + 1] += r * g;
                    _frac += _step;
                    int whole = (int)_frac;
                    _frac -= whole;
                    _readFrame += whole;
                    _played += whole;
                }
                _lastGain = g1;
                Monitor.PulseAll(_gate);
            }
        }

        public void Play()
        {
            lock (_gate)
            {
                _playing = true;
                _stopping = false;
                Monitor.PulseAll(_gate);
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                _playing = false;
                Monitor.PulseAll(_gate);
            }
        }

        // as a streaming AudioTrack's stop: what is queued plays out, then it stops
        public void Stop()
        {
            lock (_gate)
            {
                if (_playing) _stopping = true;
                Monitor.PulseAll(_gate);
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (_playing && !_stopping) return; // AudioTrack ignores a flush while playing
                _writeFrame = _readFrame;
                Monitor.PulseAll(_gate);
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                _playing = false;
                Monitor.PulseAll(_gate);
            }
            _mixer.Remove(this);
        }

        public bool IsPlaying
        {
            get { lock (_gate) return _playing; }
        }

        public long PlaybackHeadPosition
        {
            get { lock (_gate) return _played & 0xFFFFFFFFL; }
        }

        public void SetVolume(float gain)
        {
            lock (_gate) _gain = Math.Clamp(gain, 0, 1);
        }

        public string Describe() => $"{_rate} Hz, {_channels} ch, buffer {_capacity - Taps} frames";
    }
}
