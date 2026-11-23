using System.Runtime.InteropServices;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
using MphRecomp.App.Platform;
using AndroidLog = Android.Util.Log;
using AppLog = MphRecomp.App.Platform.Log;
using Gles = MphRecomp.App.Gl.GLES30;

namespace MphRecomp.App;

// The shared app layer's platform seams on Android (MphRecomp.App/Platform): logcat, AudioTrack output, BitmapFactory
// for PNGs, the files dir for caches, ART's GC counters. Installed once, by MphApp.OnCreate (CrashLog.cs), before any
// activity starts.
internal static class AndroidPlatform
{
    public static void Install(Context context)
    {
        AppLog.Sink = (level, tag, message) =>
        {
            AndroidFileLog.Write(level, tag, message); // (first: the file log's logcat copy skips this line's echo)
            (tag, message) = AndroidFileLog.ForLogcat(tag, message); // (a tagged line: area tag, "[a,b] message")
            switch (level)
            {
            case AppLog.Level.Debug: AndroidLog.Debug(tag, message); break;
            case AppLog.Level.Info: AndroidLog.Info(tag, message); break;
            case AppLog.Level.Warn: AndroidLog.Warn(tag, message); break;
            default: AndroidLog.Error(tag, message); break;
            }
        };
        AndroidFileLog.Install(context, ProcessSuffix());
        Console.SetOut(new AndroidFileLog.ConsoleWriter()); // (SetOut wraps it thread-safe)
        AppLog.Info("MPHLog", $"{context.PackageName} on Android {Android.OS.Build.VERSION.Release} ({Android.OS.Build.Model}), "
            + $"process {Android.OS.Process.MyPid()}, log file {AndroidFileLog.Path ?? "(none)"}");
        AppPlatform.Audio = new AndroidAudioOutput(context);
        MphRead.Rumble.Output = new AndroidRumble(context); // OPTIONS > RUMBLE PAK's motor (AndroidRumble.cs)
        AppPlatform.DecodeImage = DecodeImage;
        AppPlatform.EncodePng = EncodePng;
        AppPlatform.SaveScreenshot = (rgba, w, h, name) => AndroidScreenshots.Save(context, rgba, w, h, name); // photo mode
        AppPlatform.CacheDir = context.FilesDir!.AbsolutePath;
        AppPlatform.RuntimeStat = name => Int64.TryParse(Android.OS.Debug.GetRuntimeStat(name), out long v) ? v : 0;
        ReplayPrepService.Install(context); // replay keyframes made in the :replayprep process (fast seeking)
    }

    // "" in the app's main process, "replayprep" in the keyframe service's (the part after the ':')
    static string ProcessSuffix()
    {
        try
        {
            string name = File.ReadAllText("/proc/self/cmdline").Split('\0')[0];
            int colon = name.IndexOf(':');
            return colon < 0 ? "" : name[(colon + 1)..];
        }
        catch { return ""; }
    }

    static IntPtr _gles;

    // the GL binding, on the GL thread with the GLSurfaceView's context current (GlScreenRenderer); libGLESv3.so exports
    // every ES 3.0 entry point
    public static void LoadGl()
    {
        if (Gles.Loaded) return;
        if (_gles == IntPtr.Zero) _gles = NativeLibrary.Load("libGLESv3.so");
        Gles.Load(name => NativeLibrary.TryGetExport(_gles, name, out IntPtr p) ? p : IntPtr.Zero);
    }

    // a PNG -> ABGR ints (GL's RGBA bytes); getPixels gives straight (not premultiplied) ARGB
    static (int[] Pixels, int Width, int Height)? DecodeImage(string path)
    {
        Bitmap? bmp = BitmapFactory.DecodeFile(path);
        if (bmp == null) return null;
        int w = bmp.Width, h = bmp.Height;
        var px = new int[w * h];
        bmp.GetPixels(px, 0, w, 0, 0, w, h);
        bmp.Recycle();
        for (int i = 0; i < px.Length; i++)
        {
            int a = px[i];
            px[i] = (int)((a & 0xFF00FF00u) | ((uint)(a >> 16) & 0xFF) | ((uint)(a & 0xFF) << 16)); // ARGB -> ABGR
        }
        return (px, w, h);
    }

    static bool EncodePng(string path, byte[] rgba, int w, int h)
    {
        using Bitmap bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        bmp.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(rgba));
        using var fs = File.Create(path);
        return bmp.Compress(Bitmap.CompressFormat.Png!, 100, fs);
    }
}

// GLSurfaceView's renderer callbacks onto a shared-layer screen (FrontendRenderer, CampaignRenderer), with the GL binding
// loaded on the GL thread first
internal sealed class GlScreenRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    readonly IGlScreen _screen;

    public GlScreenRenderer(IGlScreen screen)
    {
        _screen = screen;
    }

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        AndroidPlatform.LoadGl();
        _screen.OnSurfaceCreated();
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height) => _screen.OnSurfaceChanged(width, height);

    public void OnDrawFrame(IGL10? gl) => _screen.OnDrawFrame();
}

// The shared mixers' PCM tracks (GameAudio.cs, MoviePresenter.cs) as AudioTracks in streaming mode: the Media usage with
// the content type the track plays, at least the platform's minimum buffer, the low-latency path for the SFX mixer
// (filled only a few device bursts ahead, more if it underruns).
internal sealed class AndroidAudioOutput : IAudioOutput
{
    public int NativeRate { get; }
    public int NativeBurstFrames { get; }

    public AndroidAudioOutput(Context context)
    {
        var audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);
        NativeRate = ParseIntProperty(audioManager, AudioManager.PropertyOutputSampleRate, 48000);
        NativeBurstFrames = ParseIntProperty(audioManager, AudioManager.PropertyOutputFramesPerBuffer, 192);
    }

    static int ParseIntProperty(AudioManager? mgr, string property, int fallback)
    {
        string? s = mgr?.GetProperty(property);
        return Int32.TryParse(s, out int v) && v > 0 ? v : fallback;
    }

    public IPcmTrack CreateTrack(int sampleRate, int channels, PcmFormat format, int bufferFrames, PcmUsage usage,
        bool lowLatency = false)
    {
        ChannelOut mask = channels == 2 ? ChannelOut.Stereo : ChannelOut.Mono;
        Encoding encoding = format == PcmFormat.Float32 ? Encoding.PcmFloat : Encoding.Pcm16bit;
        int frameBytes = channels * (format == PcmFormat.Float32 ? 4 : 2);
        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, mask, encoding);
        int bytes = Math.Max(minBytes, bufferFrames * frameBytes);
        AudioContentType content = usage switch
        {
            PcmUsage.Music => AudioContentType.Music,
            PcmUsage.Movie => AudioContentType.Movie,
            _ => AudioContentType.Sonification,
        };
        // usage media, not game: with LE Audio earbuds (the Odin + Pixel Buds Pro 2, owner 2026-11-20) our game-usage
        // tracks ran ~1.5 s behind on the primary output while Fruity Prime's single media-usage track sat at ~0.12 s;
        // the volume slider is the same media stream either way
        AudioTrack.Builder builder = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(content)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(encoding)!
                .SetSampleRate(sampleRate)!
                .SetChannelMask(mask)!
                .Build()!)
            .SetBufferSizeInBytes(bytes);
        if (lowLatency)
        {
            builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);
        }
        AudioTrack track = builder.SetTransferMode(AudioTrackMode.Stream).Build();
        int frames = bytes / frameBytes;
        // The low-latency (sfx) track is filled only as far ahead as the mixer asks, two device bursts, whatever the
        // platform's minimum is. That minimum covers the output's whole latency, and on Bluetooth it is huge: 21142
        // frames (440 ms) on the owner's Odin, 23064 (480 ms) on finRatman's realme GT 8 Pro (2026-11-21 log, fast
        // path granted, 921 ms total behind the game). The capacity stays at that minimum so the fill level can grow:
        // the Track adds a burst on every underrun, so a device whose output pulls in bigger blocks settles where it
        // stops crackling instead of at one size picked for every phone. The mixer thread blocks on a full buffer,
        // so the fill level is how far our sound runs behind the game
        int growFrames = 0;
        if (lowLatency)
        {
            growFrames = Math.Max(1, bufferFrames / 2);
            if (bufferFrames < frames)
            {
                int set = track.SetBufferSizeInFrames(bufferFrames);
                if (set > 0)
                {
                    frames = set;
                }
            }
        }
        return new Track(track, frames, channels, growFrames);
    }

    public void RaiseThreadPriority() => Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);

    // SetVolume is a gain on the samples as they're written, not AudioTrack.setVolume: Android's playback capture (the
    // screen recorder) taps a track's audio before the mixer applies the track's volume, so music muted in the game was
    // silent on the device but recorded at full volume (owner 2026-11-14). A gain change is heard once the audio
    // already queued has played (the track's buffer: 0.25 s in a game, 1 s in the menus)
    sealed class Track : IPcmTrack
    {
        readonly AudioTrack _track;
        readonly int _channels;
        readonly int _growFrames; // 0: a fixed-size track
        int _underruns;
        int _settle; // writes left before underruns count again: a track (re)started from empty underruns once
        volatile float _gain = 1;
        float _written = 1; // the gain the last write ended on: a change ramps from it across the next write (no click)
        bool _wrote; // a new track starts at its gain (no ramp down from full on a muted track's first write)
        float[]? _floats;
        short[]? _shorts;

        public Track(AudioTrack track, int bufferFrames, int channels, int growFrames)
        {
            _track = track;
            _channels = channels;
            _growFrames = growFrames;
            _settle = 8;
        }

        // after each write on the mixer thread: grow the fill level by a burst when the output ran dry since the last
        // check (one JNI read of a counter in shared memory)
        int Grow(int written)
        {
            if (_growFrames == 0)
            {
                return written;
            }
            int underruns = _track.UnderrunCount;
            if (_settle > 0)
            {
                _settle--;
                _underruns = underruns;
                return written;
            }
            if (underruns > _underruns)
            {
                _underruns = underruns;
                int size = _track.BufferSizeInFrames;
                if (size < _track.BufferCapacityInFrames)
                {
                    int set = _track.SetBufferSizeInFrames(size + _growFrames);
                    AppLog.Info("MPHAudio", $"sfx track underran ({underruns} so far): buffer {size} -> {set} frames");
                }
            }
            return written;
        }

        public int Write(float[] data, int offset, int count, bool blocking)
        {
            WriteMode mode = blocking ? WriteMode.Blocking : WriteMode.NonBlocking;
            float gain = _gain;
            if (gain == 1 && _written == 1)
            {
                _wrote = true;
                return Grow(_track.Write(data, offset, count, mode));
            }
            if (_floats == null || _floats.Length < count) _floats = new float[count];
            float from = _wrote ? _written : gain;
            _wrote = true;
            int frames = Math.Max(1, count / _channels);
            for (int i = 0; i < count; i++)
            {
                _floats[i] = data[offset + i] * (from + (gain - from) * Math.Min(1f, (i / _channels + 1) / (float)frames));
            }
            _written = gain;
            return Grow(_track.Write(_floats, 0, count, mode));
        }

        public int Write(short[] data, int offset, int count, bool blocking)
        {
            WriteMode mode = blocking ? WriteMode.Blocking : WriteMode.NonBlocking;
            float gain = _gain;
            if (gain == 1 && _written == 1)
            {
                _wrote = true;
                return Grow(_track.Write(data, offset, count, mode));
            }
            if (_shorts == null || _shorts.Length < count) _shorts = new short[count];
            float from = _wrote ? _written : gain;
            _wrote = true;
            int frames = Math.Max(1, count / _channels);
            for (int i = 0; i < count; i++)
            {
                _shorts[i] = (short)(data[offset + i] * (from + (gain - from) * Math.Min(1f, (i / _channels + 1) / (float)frames)));
            }
            _written = gain;
            return Grow(_track.Write(_shorts, 0, count, mode));
        }

        public void Play()
        {
            _settle = 8;
            _track.Play();
        }

        public void Pause() => _track.Pause();
        public void Stop() => _track.Stop();
        public void Flush() => _track.Flush();
        public void Release() => _track.Release();
        public bool IsPlaying => _track.PlayState == PlayState.Playing;
        public long PlaybackHeadPosition => (uint)_track.PlaybackHeadPosition;
        public void SetVolume(float gain) => _gain = Math.Clamp(gain, 0, 1);

        public string Describe() => $"buffer {_track.BufferSizeInFrames} of {_track.BufferCapacityInFrames} frames, performanceMode {_track.PerformanceMode} "
            + "(LowLatency=1 means the fast path was granted)";
    }
}
