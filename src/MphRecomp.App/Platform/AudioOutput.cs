namespace MphRecomp.App.Platform;

// The platform's sound output, as the mixers in this layer (GameAudio.cs, MoviePresenter.cs) use it: streaming PCM
// tracks the caller keeps filled from its own thread, each at its own sample rate, mono or interleaved stereo (a mono
// track plays at full level on both sides, as Android plays one).
public interface IAudioOutput
{
    // the device's own rate and period: the SFX mixer renders at this rate in chunks of this many frames (on Android
    // the low-latency path is only granted at the native rate)
    int NativeRate { get; }
    int NativeBurstFrames { get; }

    // bufferFrames: how much the track holds ahead of the speaker (the platform may round it up to its minimum)
    IPcmTrack CreateTrack(int sampleRate, int channels, PcmFormat format, int bufferFrames, PcmUsage usage, bool lowLatency = false);

    // the calling thread renders audio in real time (the SFX mixer): raise it to the platform's audio priority
    void RaiseThreadPriority();
}

public enum PcmFormat { Float32, Int16 }

// what a track plays, for the platform's routing and policies (Android's AudioAttributes)
public enum PcmUsage { Music, Effects, Movie }

// One streaming track, the shape of Android's AudioTrack in streaming mode. Counts are in samples (channels per frame).
public interface IPcmTrack
{
    // blocking: wait until it all fits (a paused or stopped track returns what fit at once instead, like AudioTrack);
    // returns the samples taken, or a negative error
    int Write(float[] data, int offset, int count, bool blocking);
    int Write(short[] data, int offset, int count, bool blocking);

    void Play();
    void Pause();
    // stop after what's queued has played
    void Stop();
    // drop what's queued (paused or stopped tracks)
    void Flush();
    void Release();

    bool IsPlaying { get; }
    // frames played since the track started (wraps at 2^32, as AudioTrack's does)
    long PlaybackHeadPosition { get; }
    // 0..1 gain on the whole track
    void SetVolume(float gain);
    // for the log: what the platform actually gave (Android: the performance mode)
    string Describe();
}

// no sound at all (a host that hasn't set Audio, or a test): writes are taken and dropped
public sealed class NullAudioOutput : IAudioOutput
{
    public static readonly NullAudioOutput Instance = new();

    public int NativeRate => 48000;
    public int NativeBurstFrames => 480;

    public IPcmTrack CreateTrack(int sampleRate, int channels, PcmFormat format, int bufferFrames, PcmUsage usage,
        bool lowLatency = false) => new Track();

    public void RaiseThreadPriority() { }

    sealed class Track : IPcmTrack
    {
        bool _playing;

        public int Write(float[] data, int offset, int count, bool blocking)
        {
            if (blocking && _playing) Thread.Sleep(5);
            return count;
        }

        public int Write(short[] data, int offset, int count, bool blocking)
        {
            if (blocking && _playing) Thread.Sleep(5);
            return count;
        }

        public void Play() => _playing = true;
        public void Pause() => _playing = false;
        public void Stop() => _playing = false;
        public void Flush() { }
        public void Release() => _playing = false;
        public bool IsPlaying => _playing;
        public long PlaybackHeadPosition => 0;
        public void SetVolume(float gain) { }
        public string Describe() => "no audio output";
    }
}
