using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Formats;
using MphRead.Formats.Sound;
using MphRead.Sound;
using OpenTK.Audio.OpenAL;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    public enum AfterMovie
    {
        StartGame,
        LoadRoom,
        EndGame
    }

    public partial class Scene
    {
        private const int _frameWidth = 256;
        private const int _frameHeight = 192;
        private int _topMovieBinding = -1;
        private int _botMovieBinding = -1;
        private int _movieFrameCount = 0;
        private int _movieFrameIndex = -1;
        private int _lastRenderedMovieFrameIndex = 0;
        private int _movieFrameTotal = 0;
        public bool MoviePlaying => _movieFrameIndex != -1;
        private bool _skipMovie = false;
        // hack to avoid things (like spawn SFX) happening on frame 0 of processing before we can start playing the landing movie
        private bool _playingLandingMovie = false;

        private bool _dualScreenMovie = true;
        private readonly byte[] _topImageBuffer = new byte[_frameWidth * _frameHeight * 3];
        private readonly byte[] _botImageBuffer = new byte[_frameWidth * _frameHeight * 3];
        private CancellationTokenSource _decoderCts = null!;

        public void StartMovies(Movie movieId, Movie afterMovieId, FadeType fadeToMovieType, float fadeToMovieLength,
            FadeType fadeFromMovieType, float fadeFromMovieLength, AfterMovie afterMovieAction = AfterMovie.LoadRoom)
        {
            StartMovie(movieId, fadeToMovieType, fadeToMovieLength, fadeFromMovieType, fadeFromMovieLength,
                afterMovieId: afterMovieId, afterMovieAction: afterMovieAction);
        }

        public void StartMovie(Movie movieId, FadeType fadeToMovieType, float fadeToMovieLength, FadeType fadeFromMovieType,
            float fadeFromMovieLength, Vector3? afterPosition = null, Vector3? afterFacing = null, Movie? afterMovieId = null,
            AfterMovie afterMovieAction = AfterMovie.LoadRoom)
        {
            // on the frame when the whiteout completes (middle frame if the type is out+in, last frame if out only), the scene needs to pause
            // it can start playing the movie then or delay a couple frames to match the game, but the movie plays during the end of the fade
            // at the end of the movie (or when skipped), the last frame of the movie needs to freeze on-screen while after-fade occurs
            // then at the complete (middle) of the after-fade, the movie should stop being dislayed and the scene should unpause
            // --> what exactly happens depends on the after-movie action
            // - resume as-is
            // - resume with updated position/facing/node ref
            // - do room transition and then resume
            // - end game/credits/menu/etc.
            _movieSettings.MovieId = movieId;
            _movieSettings.AfterMovieId = afterMovieId;
            _movieSettings.AfterFadeType = fadeFromMovieType;
            _movieSettings.AfterFadeLength = fadeFromMovieLength;
            _movieSettings.AfterPosition = afterPosition;
            _movieSettings.AfterFacing = afterFacing;
            _movieSettings.AfterMovieAction = afterMovieAction;
            SetFade(fadeToMovieType, fadeToMovieLength, overwrite: true, AfterFade.PlayMovie);
        }

        private void PlayMovie(Movie movieId)
        {
            // - start decoding and wait for first frame
            // - proceed with scene processing to draw first frame
            // - after enough time has elapsed, request the next frame's color buffer from the decoder
            // - decoder can decode up to 4 frames, then needs to wait until its oldest frame is requested by the renderer,
            //   then it can drop that frame and decode another one
            Music.Stop();
            GameState.PauseDialog();
            Sfx.SfxMute = true;
            Sfx.LongSfxMute++;
            Sfx.TimedSfxMute++;
            Sfx.ForceFieldSfxMute++;
            if (Headless)
            {
                if (HostPlaysMovies)
                {
                    // the host decodes and shows it itself (MphRecomp.Media.MoviePlayer) and ends it with SkipMovie:
                    // the same after-fade and after-movie action as the movie running out
                    HostMovie = movieId;
                    HostMovieSerial++;
                    _movieFrameIndex = 0;
                    _movieFrameCount = 0;
                    _skipMovie = false;
                    return;
                }
                // no decoder/audio: treat the movie as already finished (StopMovie chains or runs the after-action)
                _movieFrameIndex = 0;
                StopMovie();
                return;
            }
            VxDecoder.Instance1.Reset();
            VxDecoder.Instance2.Reset();
            _decoderCts = new CancellationTokenSource();
            if (_audioHandle != -1)
            {
                AL.SourceStop(_audioHandle);
                AL.DeleteSource(_audioHandle);
                AL.DeleteBuffers(_audioBufferIds);
            }
            _audioHandle = AL.GenSource();
            AL.GenBuffers(_audioBufferIds);
            AL.Source(_audioHandle, ALSourcef.Gain, Music.UserVolume * 0.5f);
            Array.Fill(_audioBuffersAvailable, true);
            _audioBufferIndex = 0;
            Metadata.MovieInfo info = Metadata.MovieFiles[(int)movieId];
            _dualScreenMovie = info.BottomScreenPath != null;
            string topPath = Paths.Combine(Paths.FileSystem, info.TopScreenPath);
            Task.Run(async () => await VxDecoder.Instance1.Decode(topPath, token: _decoderCts.Token), _decoderCts.Token);
            if (_dualScreenMovie)
            {
                string botPath = Paths.Combine(Paths.FileSystem, info.BottomScreenPath!);
                Task.Run(async () => await VxDecoder.Instance2.Decode(botPath, token: _decoderCts.Token), _decoderCts.Token);
            }
            else
            {
                Array.Fill<byte>(_botImageBuffer, 0);
            }
            // start with a few frames to make sure audio doesn't run out
            // (note that even if we don't sleep, we'll hit the max of 4 anyway)
            while (VxDecoder.Instance1.FramesQueued < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
            VxDecoder.Instance1.GetImage(frameIndex: 0, _topImageBuffer);
            if (_dualScreenMovie)
            {
                while (VxDecoder.Instance2.FramesQueued < 4)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(10));
                }
                VxDecoder.Instance2.GetImage(frameIndex: 0, _botImageBuffer);
            }
            if (_topMovieBinding == -1)
            {
                _topMovieBinding = ++_textureCount;
                _botMovieBinding = ++_textureCount;
            }
            GL.BindTexture(TextureTarget.Texture2D, _topMovieBinding);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, _frameWidth, _frameHeight, 0,
                PixelFormat.Rgb, PixelType.UnsignedByte, _topImageBuffer);
            GL.BindTexture(TextureTarget.Texture2D, _botMovieBinding);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, _frameWidth, _frameHeight, 0,
                PixelFormat.Rgb, PixelType.UnsignedByte, _botImageBuffer);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            Debug.Assert(!_dualScreenMovie || VxDecoder.Instance1.FrameCount == VxDecoder.Instance2.FrameCount);
            _movieFrameTotal = VxDecoder.Instance1.FrameCount;
            _lastRenderedMovieFrameIndex = 0;
            _movieFrameIndex = 0;
            _movieFrameCount = 0;
            _skipMovie = false;
            Task.Run(async () =>
            {
                // while loop outside the method so we can use stackalloc without limitation (possibly different sizes each iteration)
                while (!_decoderCts.Token.IsCancellationRequested && _movieFrameCount != Int32.MaxValue)
                {
                    await UpdateMovieAudio(_decoderCts.Token);
                }
            }, _decoderCts.Token);
            Task.Run(async () => await UpdateMovieImage(_decoderCts.Token), _decoderCts.Token);
        }

        private int _audioHandle = -1;
        public int MovieAudioHandle => _audioHandle;

        // Headless hosts that present movies themselves (the Android app) set this: PlayMovie then records the movie in
        // HostMovie (HostMovieSerial counts starts, so a chained second movie is noticed) and leaves it playing until
        // the host calls SkipMovie. Off (tools, tests): headless scenes skip movies.
        public static bool HostPlaysMovies { get; set; }
        public Movie HostMovie { get; private set; } = Movie.None;
        public int HostMovieSerial { get; private set; }
        private const int _audioBufferCount = 16;
        private int _audioBufferIndex = 0;
        private readonly int[] _audioBufferIds = new int[_audioBufferCount];
        private readonly bool[] _audioBuffersAvailable = new bool[_audioBufferCount];

        private async Task UpdateMovieAudio(CancellationToken token)
        {
            Span<short> stereoBuffer = stackalloc short[128 * 2];
            bool initial = _audioBufferIndex == 0;
            int framesAvailable = VxDecoder.Instance1.AudioFrameTotal - _audioBufferIndex;
            if (framesAvailable > 0)
            {
                int buffersAvailable = 0;
                if (_audioBufferIndex == 0)
                {
                    buffersAvailable = _audioBufferCount;
                }
                else
                {
                    int buffersProcessed = AL.GetSource(_audioHandle, ALGetSourcei.BuffersProcessed);
                    if (buffersProcessed > 0)
                    {
                        Span<int> processedIds = stackalloc int[Math.Min(framesAvailable, buffersProcessed)];
                        AL.SourceUnqueueBuffers(_audioHandle, processedIds);
                        for (int i = 0; i < _audioBufferCount; i++)
                        {
                            _audioBuffersAvailable[i] = processedIds.Contains(_audioBufferIds[i]);
                        }
                    }
                    for (int i = 0; i < _audioBufferCount; i++)
                    {
                        if (_audioBuffersAvailable[i])
                        {
                            buffersAvailable++;
                        }
                    }
                }
                if (buffersAvailable > 0)
                {
                    Span<int> queueBuffers = stackalloc int[Math.Min(framesAvailable, buffersAvailable)];
                    int bufferIndex = 0;
                    for (int i = 0; i < queueBuffers.Length; i++)
                    {
                        for (; bufferIndex < _audioBufferCount; bufferIndex++)
                        {
                            if (_audioBuffersAvailable[bufferIndex])
                            {
                                queueBuffers[i] = _audioBufferIds[bufferIndex];
                                _audioBuffersAvailable[bufferIndex] = false;
                                break;
                            }
                        }
                        Debug.Assert(bufferIndex < _audioBufferCount);
                        if (_dualScreenMovie)
                        {
                            ReadOnlySpan<short> buffer1 = VxDecoder.Instance1.GetAudioBuffer(_audioBufferIndex);
                            ReadOnlySpan<short> buffer2 = VxDecoder.Instance2.GetAudioBuffer(_audioBufferIndex++);
                            for (int j = 0; j < 128; j++)
                            {
                                stereoBuffer[j * 2] = buffer1[j];
                                stereoBuffer[j * 2 + 1] = buffer2[j];
                            }
                            AL.BufferData<short>(queueBuffers[i], ALFormat.Stereo16, stereoBuffer, VxDecoder.Instance1.AudioSampleRate);
                        }
                        else
                        {
                            ReadOnlySpan<short> buffer = VxDecoder.Instance1.GetAudioBuffer(_audioBufferIndex++);
                            AL.BufferData<short>(queueBuffers[i], ALFormat.Mono16, buffer, VxDecoder.Instance1.AudioSampleRate);
                        }
                    }
                    AL.SourceQueueBuffers(_audioHandle, queueBuffers);
                    var state = (ALSourceState)AL.GetSource(_audioHandle, ALGetSourcei.SourceState);
                    if (state != ALSourceState.Playing)
                    {
                        // this should only occur once unless things are lagging behind
                        AL.SourcePlay(_audioHandle);
                    }
                }
            }
            try
            {
                await Task.Delay(1);
            }
            catch (TaskCanceledException) { }
        }

        private void StopMovie()
        {
            if (_audioHandle != -1)
            {
                AL.SourceStop(_audioHandle);
                AL.DeleteSource(_audioHandle);
                AL.DeleteBuffers(_audioBufferIds);
                _audioHandle = -1;
            }
            if (_movieSettings.AfterMovieId.HasValue)
            {
                Sfx.LongSfxMute--;
                Sfx.TimedSfxMute--;
                Sfx.ForceFieldSfxMute--;
                _decoderCts?.Cancel();
                _movieSettings.MovieId = _movieSettings.AfterMovieId.Value;
                _movieSettings.AfterMovieId = null;
                PlayMovie(_movieSettings.MovieId);
                _fadeType = FadeType.None;
            }
            else if (_movieSettings.AfterMovieAction == AfterMovie.EndGame)
            {
                QuitGame(enteringShip: true);
            }
            else
            {
                if (_movieSettings.AfterMovieAction != AfterMovie.StartGame)
                {
                    Debug.Assert(_room != null);
                    _room.LoadRoom(resume: GameState.TransitionRoomId == -1);
                }
                else
                {
                    // hold on white to give the landing cam seq a chance to fade in, overwriting this, if any.
                    // otherwise this fade will fade out to gameplay (no cam seq for an existing save).
                    SetFade(FadeType.FadeInWhite, 5 / 30f, overwrite: true, delay: 5 / 30f);
                    Music.PlayPausedMusic();
                    _playingLandingMovie = false;
                }
                Sfx.SfxMute = false;
                Sfx.LongSfxMute--;
                Sfx.TimedSfxMute--;
                Sfx.ForceFieldSfxMute--;
                GameState.UnpauseDialog();
                _decoderCts?.Cancel();
                _movieFrameIndex = -1;
            }
        }

        public void SkipMovie()
        {
            if (_movieFrameIndex != -1 && _movieFrameCount != Int32.MaxValue)
            {
                _skipMovie = true;
            }
        }

        private void UpdateMovie()
        {
            if (_skipMovie)
            {
                if (_movieFrameCount != Int32.MaxValue)
                {
                    _movieFrameCount = Int32.MaxValue;
                    SetFade(_movieSettings.AfterFadeType, _movieSettings.AfterFadeLength, overwrite: true, AfterFade.StopMovie);
                }
                return;
            }
        }

        private async Task UpdateMovieImage(CancellationToken token)
        {
            TimeSpan frameTime = TimeSpan.FromSeconds(1 / 15.0);
            TimeSpan nextFrameElapsed = frameTime;
            // tolerance is used mainly so the audio doesn't run out if we end up sleeping a bit past the next 15 fps mark
            // there are either 11 or 12 audio frames per video frame, which balance each other out, but will drift unless the video waits are balanced
            TimeSpan tolerance = TimeSpan.FromMilliseconds(15);
            var sw = new Stopwatch();
            sw.Start();
            while (!token.IsCancellationRequested && _movieFrameCount != Int32.MaxValue)
            {
                TimeSpan elapsed = sw.Elapsed;
                if (elapsed >= nextFrameElapsed && nextFrameElapsed - elapsed < tolerance)
                {
                    _movieFrameCount++;
                    nextFrameElapsed += frameTime;
                }
                int frameIndex = _movieFrameCount;
                if (_movieFrameCount != _movieFrameIndex)
                {
                    if (frameIndex == _movieFrameTotal)
                    {
                        _movieFrameCount = Int32.MaxValue;
                        SetFade(_movieSettings.AfterFadeType, _movieSettings.AfterFadeLength, overwrite: true, AfterFade.StopMovie);
                        return;
                    }
                    if (frameIndex < _movieFrameTotal)
                    {
                        if (VxDecoder.Instance1.GetImage(frameIndex, _topImageBuffer)
                            && !_dualScreenMovie || VxDecoder.Instance2.GetImage(frameIndex, _botImageBuffer))
                        {
                            _movieFrameIndex = frameIndex;
                        }
                    }
                }
                try
                {
                    await Task.Delay(1);
                }
                catch (TaskCanceledException) { }
            }
        }

        private void DrawMovieFrame()
        {
            GL.Uniform1(_shaderLocations.LayerAlpha, 1);
            bool newFrame = false;

            void DrawScreen(int movieBinding, byte[] imageBuffer, float y)
            {
                GL.BindTexture(TextureTarget.Texture2D, movieBinding);
                int minParameter = (int)TextureMinFilter.Nearest;
                int magParameter = (int)TextureMagFilter.Nearest;
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, minParameter);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, magParameter);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                if (_movieFrameIndex != _lastRenderedMovieFrameIndex || newFrame && _dualScreenMovie)
                {
                    newFrame = true;
                    _lastRenderedMovieFrameIndex = _movieFrameIndex;
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, _frameWidth, _frameHeight, PixelFormat.Rgb, PixelType.UnsignedByte, imageBuffer);
                }
                GL.Begin(PrimitiveType.TriangleStrip);
                // top right
                GL.TexCoord3(1f, 0f, 0f);
                GL.Vertex3(0.5f, y, 0f);
                // top left
                GL.TexCoord3(0f, 0f, 0f);
                GL.Vertex3(-0.5f, y, 0f);
                // bottom right
                GL.TexCoord3(1f, 1f, 0f);
                GL.Vertex3(0.5f, y - 1, 0f);
                // bottom left
                GL.TexCoord3(0f, 1f, 0f);
                GL.Vertex3(-0.5f, y - 1, 0f);
                GL.End();
                GL.BindTexture(TextureTarget.Texture2D, 0);
            }

            GL.Uniform4(_shaderLocations.FadeColor, 0, 0, 0, 1);
            GL.Begin(PrimitiveType.TriangleStrip);
            // top right
            GL.TexCoord3(1f, 1f, 0f);
            GL.Vertex3(1f, 1f, 0f);
            // top left
            GL.TexCoord3(0f, 1f, 0f);
            GL.Vertex3(-1f, 1f, 0f);
            // bottom right
            GL.TexCoord3(1f, 0f, 0f);
            GL.Vertex3(1f, -1f, 0f);
            // bottom left
            GL.TexCoord3(0f, 0f, 0f);
            GL.Vertex3(-1f, -1f, 0f);
            GL.End();
            DrawScreen(_topMovieBinding, _topImageBuffer, y: 1);
            DrawScreen(_botMovieBinding, _botImageBuffer, y: 0);
        }
    }
}
