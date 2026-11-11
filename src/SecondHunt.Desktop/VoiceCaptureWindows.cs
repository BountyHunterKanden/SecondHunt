using MphRecomp.App.Platform;
using MphRecomp.Online.Voice;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SecondHunt.Desktop;

// Voice chat's microphone on Windows (milestone O8): the default capture device through SoundFlow/miniaudio (the library
// DesktopAudio plays through), converted by miniaudio to 16 kHz mono and cut into 20 ms frames on its capture thread.
// Its own engine, so opening and closing the microphone never touches the game's output device.
// WASAPI first, then DirectSound, each tried on its own: a mic whose WASAPI mix format miniaudio can't read fails with
// FormatNotSupported (a 0.4.1 player's, 10-31), and an engine given both only ever uses the first. (SoundFlow's
// miniaudio has no WinMM: NoBackend.)
internal sealed class VoiceCaptureWindows : IVoiceCapture
{
    static readonly MiniAudioBackend[] Backends = { MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound };

    MiniAudioEngine? _engine;
    AudioCaptureDevice? _device;
    Action<short[]>? _frame;
    readonly short[] _pcm = new short[VoiceFormat.FrameSamples];
    int _at;
    volatile bool _run;

    public bool Running => _run;
    public string? Error { get; private set; }

    public bool Start(Action<short[]> frame)
    {
        var failed = new List<string>();
        foreach (MiniAudioBackend backend in Backends)
        {
            if (Start(frame, backend, out string? error))
            {
                if (failed.Count > 0)
                {
                    Log.Info("MPHVoice", "microphone: " + string.Join("; ", failed));
                }
                Error = null;
                return true;
            }
            failed.Add($"{backend}: {error}");
        }
        Error = "microphone: " + string.Join("; ", failed);
        return false;
    }

    bool Start(Action<short[]> frame, MiniAudioBackend backend, out string? error)
    {
        try
        {
            _engine = new MiniAudioEngine(new[] { backend });
            var format = new AudioFormat { SampleRate = VoiceFormat.Rate, Channels = 1, Format = SampleFormat.F32 };
            var config = new MiniAudioDeviceConfig { PeriodSizeInFrames = (uint)VoiceFormat.FrameSamples };
            _device = _engine.InitializeCaptureDevice(null, format, config);
            _frame = frame;
            _at = 0;
            _device.OnAudioProcessed += OnAudio;
            _run = true;
            _device.Start();
            Log.Info("MPHVoice", $"microphone: {_device.Info?.Name ?? "default"}, {VoiceFormat.Rate} Hz mono ({_engine.ActiveBackend})");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Stop();
            return false;
        }
    }

    void OnAudio(Span<float> samples, Capability capability)
    {
        if (!_run || _frame == null)
        {
            return;
        }
        foreach (float s in samples)
        {
            _pcm[_at++] = (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue);
            if (_at == _pcm.Length)
            {
                _at = 0;
                _frame(_pcm);
            }
        }
    }

    public void Stop()
    {
        _run = false;
        try
        {
            if (_device != null)
            {
                _device.OnAudioProcessed -= OnAudio;
                _device.Stop();
                _device.Dispose();
            }
            _engine?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHVoice", "microphone close: " + ex.Message);
        }
        _device = null;
        _engine = null;
    }

    public void Dispose() => Stop();
}
