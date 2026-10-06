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
            switch (level)
            {
            case AppLog.Level.Debug: AndroidLog.Debug(tag, message); break;
            case AppLog.Level.Info: AndroidLog.Info(tag, message); break;
            case AppLog.Level.Warn: AndroidLog.Warn(tag, message); break;
            default: AndroidLog.Error(tag, message); break;
            }
        };
        AppPlatform.Audio = new AndroidAudioOutput(context);
        AppPlatform.DecodeImage = DecodeImage;
        AppPlatform.EncodePng = EncodePng;
        AppPlatform.CacheDir = context.FilesDir!.AbsolutePath;
        AppPlatform.RuntimeStat = name => Int64.TryParse(Android.OS.Debug.GetRuntimeStat(name), out long v) ? v : 0;
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

// The shared mixers' PCM tracks (GameAudio.cs, MoviePresenter.cs) as AudioTracks in streaming mode: the Game usage with
// the content type the track plays, at least the platform's minimum buffer, the low-latency path for the SFX mixer.
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
        AudioTrack.Builder builder = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
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
        return new Track(builder.SetTransferMode(AudioTrackMode.Stream).Build(), bytes / frameBytes);
    }

    public void RaiseThreadPriority() => Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);

    sealed class Track : IPcmTrack
    {
        readonly AudioTrack _track;
        readonly int _bufferFrames;

        public Track(AudioTrack track, int bufferFrames)
        {
            _track = track;
            _bufferFrames = bufferFrames;
        }

        public int Write(float[] data, int offset, int count, bool blocking)
            => _track.Write(data, offset, count, blocking ? WriteMode.Blocking : WriteMode.NonBlocking);

        public int Write(short[] data, int offset, int count, bool blocking)
            => _track.Write(data, offset, count, blocking ? WriteMode.Blocking : WriteMode.NonBlocking);

        public void Play() => _track.Play();
        public void Pause() => _track.Pause();
        public void Stop() => _track.Stop();
        public void Flush() => _track.Flush();
        public void Release() => _track.Release();
        public bool IsPlaying => _track.PlayState == PlayState.Playing;
        public long PlaybackHeadPosition => (uint)_track.PlaybackHeadPosition;
        public void SetVolume(float gain) => _track.SetVolume(gain);

        public string Describe() => $"buffer {_bufferFrames} frames, performanceMode {_track.PerformanceMode} "
            + "(LowLatency=1 means the fast path was granted)";
    }
}
