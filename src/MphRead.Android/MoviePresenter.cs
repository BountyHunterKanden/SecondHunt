using Android.Media;
using Android.Opengl;
using Android.Util;
using System;
using System.Threading;
using MphRecomp.Media;

namespace MphRecomp.App;

// Shows a MoviePlayer (MphRecomp.Core/Media) with GLES3 and plays its sound on an AudioTrack. Shared by the campaign
// (in-game cutscenes) and the front end (the intro), from any GLSurfaceView renderer, on the GL thread:
//
//   var presenter = new MoviePresenter();                 // after the GL context exists
//   each frame:  movie.Update(dt); presenter.Draw(movie, viewportWidth, viewportHeight);   // also runs the audio
//   done:        presenter.Stop(); movie.Dispose();       // context lost (OnSurfaceCreated): presenter.ResetGl()
//
// Or composite yourself: presenter.Upload(movie) keeps TopTexture / BottomTexture (RGB, 256x192) current, and
// presenter.Audio(movie, paused) keeps the sound going.
// The DS shows two-screen movies on both screens; on one 16:9 screen the default is the two side by side (each 4:3),
// TopOnly fills the height with the top screen, Stacked puts them one above the other.
internal sealed class MoviePresenter : IDisposable
{
    public enum Layout { SideBySide, TopOnly, Stacked }

    public Layout DualLayout { get; set; } = Layout.SideBySide;
    // linear filtering when scaling the 256x192 frames up (false: DS-sharp nearest)
    public bool Smooth { get; set; } = true;
    public int TopTexture => _texTop;
    public int BottomTexture => _texBottom;

    int _program, _vao, _vbo, _texTop, _texBottom;
    int _uploadedVersion = -1;
    MoviePlayer? _uploadedFor;
    readonly float[] _verts = new float[16];
    Java.Nio.FloatBuffer _vertBuffer = null!;
    Java.Nio.ByteBuffer _pixels = null!;
    MovieAudio? _audio;

    const string VertexShader = @"#version 300 es
layout(location = 0) in vec2 pos;
layout(location = 1) in vec2 uv;
out vec2 vUv;
void main() { vUv = uv; gl_Position = vec4(pos, 0.0, 1.0); }";

    const string FragmentShader = @"#version 300 es
precision mediump float;
uniform sampler2D tex;
in vec2 vUv;
out vec4 color;
void main() { color = vec4(texture(tex, vUv).rgb, 1.0); }";

    public MoviePresenter()
    {
        ResetGl();
    }

    // (re)creates the GL objects -- call again after the GL context was lost
    public void ResetGl()
    {
        _program = Link(VertexShader, FragmentShader);
        var ids = new int[2];
        GLES30.GlGenVertexArrays(1, ids, 0);
        _vao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0);
        _vbo = ids[0];
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, _verts.Length * 4, null, GLES30.GlDynamicDraw);
        GLES30.GlVertexAttribPointer(0, 2, GLES30.GlFloat, false, 16, 0);
        GLES30.GlVertexAttribPointer(1, 2, GLES30.GlFloat, false, 16, 8);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlEnableVertexAttribArray(1);
        GLES30.GlBindVertexArray(0);
        var vb = Java.Nio.ByteBuffer.AllocateDirect(_verts.Length * 4);
        vb.Order(Java.Nio.ByteOrder.NativeOrder());
        _vertBuffer = vb.AsFloatBuffer()!;
        _pixels = Java.Nio.ByteBuffer.AllocateDirect(MoviePlayer.Width * MoviePlayer.Height * 3);
        GLES30.GlGenTextures(2, ids, 0);
        _texTop = ids[0];
        _texBottom = ids[1];
        foreach (int tex in ids)
        {
            GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgb, MoviePlayer.Width, MoviePlayer.Height, 0,
                GLES30.GlRgb, GLES30.GlUnsignedByte, null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        }
        GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
        _uploadedVersion = -1;
        _uploadedFor = null;
    }

    // keeps TopTexture / BottomTexture on the player's current frames
    public void Upload(MoviePlayer player)
    {
        if (player == _uploadedFor && player.FrameVersion == _uploadedVersion)
        {
            return;
        }
        GLES30.GlPixelStorei(GLES30.GlUnpackAlignment, 1);
        UploadOne(_texTop, player.TopFrame);
        if (player.DualScreen)
        {
            UploadOne(_texBottom, player.BottomFrame);
        }
        _uploadedFor = player;
        _uploadedVersion = player.FrameVersion;
    }

    void UploadOne(int tex, byte[] rgb)
    {
        _pixels.Position(0);
        _pixels.Put(rgb);
        _pixels.Position(0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
        GLES30.GlTexSubImage2D(GLES30.GlTexture2d, 0, 0, 0, MoviePlayer.Width, MoviePlayer.Height,
            GLES30.GlRgb, GLES30.GlUnsignedByte, _pixels);
    }

    // Clears the viewport to black and draws the movie letterboxed into it; also keeps the audio running.
    public void Draw(MoviePlayer player, int width, int height, bool paused = false)
    {
        Audio(player, paused);
        GLES30.GlViewport(0, 0, width, height);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE
        GLES30.GlClearColor(0, 0, 0, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit);
        if (!player.Started)
        {
            return; // still decoding the first frames
        }
        Upload(player);
        GLES30.GlUseProgram(_program);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindSampler(0, 0); // a renderer may leave its own sampler object bound on unit 0
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_program, "tex"), 0);
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
        float aspect = width / (float)Math.Max(1, height);
        if (!player.DualScreen || DualLayout == Layout.TopOnly)
        {
            DrawScreen(_texTop, Fit(4f / 3f, aspect, 0, 0, 1, 1));
        }
        else if (DualLayout == Layout.SideBySide)
        {
            (float x, float y, float w, float h) r = Fit(8f / 3f, aspect, 0, 0, 1, 1);
            DrawScreen(_texTop, (r.x, r.y, r.w / 2, r.h));
            DrawScreen(_texBottom, (r.x + r.w / 2, r.y, r.w / 2, r.h));
        }
        else
        {
            (float x, float y, float w, float h) r = Fit(2f / 3f, aspect, 0, 0, 1, 1);
            DrawScreen(_texTop, (r.x, r.y + r.h / 2, r.w, r.h / 2));
            DrawScreen(_texBottom, (r.x, r.y, r.w, r.h / 2));
        }
        GLES30.GlBindVertexArray(0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
    }

    // the largest rect of the given aspect centred in the (x, y, w, h) area, in 0..1 viewport units (y up)
    static (float x, float y, float w, float h) Fit(float contentAspect, float viewAspect, float x, float y, float w, float h)
    {
        float cw = w, ch = h;
        if (contentAspect > viewAspect * (w / h))
        {
            ch = w * viewAspect / contentAspect;
        }
        else
        {
            cw = h * contentAspect / viewAspect;
        }
        return (x + (w - cw) / 2, y + (h - ch) / 2, cw, ch);
    }

    void DrawScreen(int tex, (float x, float y, float w, float h) r)
    {
        GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
        int filter = Smooth ? GLES30.GlLinear : GLES30.GlNearest;
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, filter);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, filter);
        float x0 = r.x * 2 - 1, y0 = r.y * 2 - 1, x1 = (r.x + r.w) * 2 - 1, y1 = (r.y + r.h) * 2 - 1;
        // strip: bottom-left, bottom-right, top-left, top-right; frame row 0 is the top of the picture
        float[] v = _verts;
        v[0] = x0; v[1] = y0; v[2] = 0; v[3] = 1;
        v[4] = x1; v[5] = y0; v[6] = 1; v[7] = 1;
        v[8] = x0; v[9] = y1; v[10] = 0; v[11] = 0;
        v[12] = x1; v[13] = y1; v[14] = 1; v[15] = 0;
        _vertBuffer.Position(0);
        _vertBuffer.Put(v);
        _vertBuffer.Position(0);
        GLES30.GlBufferSubData(GLES30.GlArrayBuffer, 0, v.Length * 4, _vertBuffer);
        GLES30.GlDrawArrays(GLES30.GlTriangleStrip, 0, 4);
    }

    // starts the sound once the first frame is up, pauses/resumes it, and stops it when the movie ends
    public void Audio(MoviePlayer player, bool paused)
    {
        if (_audio != null && _audio.Player != player)
        {
            Stop();
        }
        if (player.Finished)
        {
            Stop();
            return;
        }
        if (_audio == null && player.Started && player.AudioSampleRate > 0)
        {
            _audio = new MovieAudio(player);
        }
        _audio?.SetPaused(paused);
    }

    public void Stop()
    {
        _audio?.Dispose();
        _audio = null;
    }

    public void Dispose()
    {
        Stop();
    }

    static int Link(string vs, string fs)
    {
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, Compile(GLES30.GlVertexShader, vs));
        GLES30.GlAttachShader(p, Compile(GLES30.GlFragmentShader, fs));
        GLES30.GlLinkProgram(p);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0)
        {
            Log.Error("MPHMovie", "link: " + GLES30.GlGetProgramInfoLog(p));
        }
        return p;
    }

    static int Compile(int type, string src)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0)
        {
            Log.Error("MPHMovie", "compile: " + GLES30.GlGetShaderInfoLog(s));
        }
        return s;
    }
}

// A MoviePlayer's PCM on an AudioTrack, fed from its own thread (blocking writes pace it in real time).
internal sealed class MovieAudio : IDisposable
{
    // The DS plays a movie's sound at the movie's own volume from data/sound/MOVIEVOLINFO.DAT (01 = 96, 02 = 76, the
    // one-screen 04-11/13 = 127, 15 = 116, 17/19/21/23 = 106, the rest 96), and its mixer scales a channel by
    // vol/128 and pans linearly: a two-screen movie's two channels play hard left/right (BizHawk SOUNDxCNT, movie
    // 01: vol 96, pan 127/0, matched the decoded PCM x 96/128 within 0.2 dB), a one-screen movie's single channel
    // plays centred, half per side (AlinosLanding = 04.vx: one PCM16 channel, vol 127, pan 64, div 0, 2026-10-01).
    float VolumeGain => DsVolume(Player.Name) / 128f * (Player.AudioChannels == 2 ? 1f : 0.5f);

    // MOVIEVOLINFO.DAT: u32 count, then count x {u32 top name offset, u32 bottom name offset, u32 rsr name offset,
    // u32 volume}, names being "movies/NN_top.vx" / "movies/NN.vx" strings later in the file. Player.Name is either
    // the front end's "01" or a MphRead Movie name (MoviePlayer.Open).
    static int DsVolume(string name)
    {
        try
        {
            string top = Enum.TryParse(name, out MphRead.Movie movie) && !char.IsDigit(name[0])
                && MphRead.Metadata.MovieFiles[(int)movie] is MphRead.Metadata.MovieInfo info
                ? System.IO.Path.GetFileName(info.TopScreenPath) : name;
            byte[] d = System.IO.File.ReadAllBytes(MphRead.Paths.Combine(MphRead.Paths.FileSystem, "data", "sound", "MOVIEVOLINFO.DAT"));
            int count = BitConverter.ToInt32(d, 0);
            for (int i = 0; i < count; i++)
            {
                int nameOffset = BitConverter.ToInt32(d, 4 + 16 * i);
                int end = Array.IndexOf(d, (byte)0, nameOffset);
                string file = System.IO.Path.GetFileName(System.Text.Encoding.ASCII.GetString(d, nameOffset, end - nameOffset));
                if (string.Equals(file, top, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file, top + "_top.vx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file, top + ".vx", StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Clamp(BitConverter.ToInt32(d, 4 + 16 * i + 12), 0, 127);
                }
            }
            Log.Info("MPHAudio", $"movie {name}: not in MOVIEVOLINFO, volume 96");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHAudio", $"movie {name}: MOVIEVOLINFO read failed ({ex.Message}), volume 96");
        }
        return 96;
    }

    public MoviePlayer Player { get; }
    readonly AudioTrack _track;
    readonly Thread _thread;
    volatile bool _stop;
    volatile bool _paused;

    public MovieAudio(MoviePlayer player)
    {
        Player = player;
        ChannelOut mask = player.AudioChannels == 2 ? ChannelOut.Stereo : ChannelOut.Mono;
        int minBytes = AudioTrack.GetMinBufferSize(player.AudioSampleRate, mask, Encoding.Pcm16bit);
        // small buffer: the sound should follow the picture, not run far ahead of it
        int bytes = Math.Max(minBytes, player.AudioSampleRate / 20 * 2 * player.AudioChannels); // >= 50 ms
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
                .SetContentType(AudioContentType.Movie)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(player.AudioSampleRate)!
                .SetChannelMask(mask)!
                .Build()!)
            .SetBufferSizeInBytes(bytes)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        float gain = VolumeGain;
        _track.SetVolume(gain);
        Log.Info("MPHAudio", $"movie {player.Name}: {player.AudioChannels} ch, gain {gain:0.000}");
        _track.Play();
        _thread = new Thread(Pump) { IsBackground = true, Name = "MovieAudio" };
        _thread.Start();
    }

    void Pump()
    {
        var buffer = new short[1024 * Player.AudioChannels];
        while (!_stop)
        {
            if (_paused)
            {
                Thread.Sleep(10);
                continue;
            }
            int got = Player.ReadAudio(buffer);
            if (got == 0)
            {
                Thread.Sleep(2);
                continue;
            }
            try
            {
                _track.Write(buffer, 0, got); // blocks while the track's buffer is full
            }
            catch (Exception ex)
            {
                Log.Warn("MPHMovie", "audio write: " + ex.Message);
                return;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        if (paused == _paused)
        {
            return;
        }
        _paused = paused;
        try
        {
            if (paused)
            {
                _track.Pause();
            }
            else
            {
                _track.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMovie", "audio pause: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            _track.Pause();
            _track.Flush();
            _thread.Join(500);
            _track.Stop();
            _track.Release();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMovie", "audio stop: " + ex.Message);
        }
    }
}
