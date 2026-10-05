using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Media
{
    // Plays one of the ROM's VX movies with MphRead's own decoder -- no GL, no audio device: the playback clock, both
    // screens' RGB24 frames and the PCM audio. How it is shown and heard (GL textures, AudioTrack, 16:9 layout) is up
    // to the caller (MphRead.Android/MoviePresenter.cs does both for GLES).
    //
    //   var movie = MoviePlayer.Open("01");        // or MoviePlayer.Open(Movie.CALanding)
    //   each frame: movie.Update(seconds); if (movie.FrameVersion != seen) upload movie.TopFrame / BottomFrame
    //   audio thread: movie.ReadAudio(buffer) -> AudioSampleRate, AudioChannels; movie.Finished / Skip(); Dispose()
    //
    // MphRead's decoder decodes on a background task up to 4 frames ahead of the frame last shown (GetImage releases
    // one), with a 60-block audio ring; Update copies every new audio block out of that ring into this player's own
    // queue, so a slow audio consumer can never read samples the decoder has since overwritten. MphRead has two decoder
    // instances (top / bottom screen), so one movie plays at a time: opening another disposes the current one.
    public sealed class MoviePlayer : IDisposable
    {
        public const int Width = 256;
        public const int Height = 192;
        private const int AudioBlock = 128; // samples per VX audio frame
        private static readonly short[] _silence = new short[AudioBlock];

        public string Name { get; }
        public bool DualScreen { get; }
        // RGB24, 256 x 192, first row = top of the screen; the bottom screen stays black for one-screen movies
        public byte[] TopFrame { get; } = new byte[Width * Height * 3];
        public byte[] BottomFrame { get; } = new byte[Width * Height * 3];
        // bumps whenever TopFrame/BottomFrame change
        public int FrameVersion { get; private set; }
        // frame on screen (-1 until the first one is decoded)
        public int FrameIndex { get; private set; } = -1;
        public int FrameCount { get; private set; }
        public double FrameRate { get; private set; } = 15;
        public int AudioSampleRate { get; private set; }
        // two-screen movies carry one mono track per screen; MphRead plays them as stereo (top left, bottom right)
        public int AudioChannels => DualScreen ? 2 : 1;
        // playback has started (the first frames are decoded)
        public bool Started { get; private set; }
        public bool Finished { get; private set; }
        public bool Skipped { get; private set; }
        public string? Error { get; private set; }
        public double Position => _clock;
        public double Duration => FrameRate > 0 ? FrameCount / FrameRate : 0;

        private static MoviePlayer? _current;

        private readonly VxDecoder _top = VxDecoder.Instance1;
        private readonly VxDecoder _bottom = VxDecoder.Instance2;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _topTask;
        private readonly Task? _bottomTask;
        private double _clock;
        private int _audioIndex;
        private readonly object _audioLock = new object();
        private short[] _audio = new short[AudioBlock * 2 * 256];
        private int _audioRead;
        private int _audioCount;
        private bool _disposed;
        // the two screens' files can differ by a frame (22_top.vx has 140, 22_bot.vx 139): the shorter one holds its
        // last frame, and its audio channel is padded with silence
        private int _topFrames;
        private int _bottomFrames;
        // wall-clock watchdog: no new frame for this long while playback wants one -> give up rather than hang
        private readonly System.Diagnostics.Stopwatch _sinceProgress = System.Diagnostics.Stopwatch.StartNew();
        private const double StallSeconds = 3;

        // "01" -> movies/01_top.vx + movies/01_bot.vx; "04" -> movies/04.vx
        public static MoviePlayer Open(string name)
        {
            string top = Paths.Combine(Paths.FileSystem, "movies", $"{name}_top.vx");
            if (File.Exists(top))
            {
                return new MoviePlayer(name, top, Paths.Combine(Paths.FileSystem, "movies", $"{name}_bot.vx"));
            }
            return new MoviePlayer(name, Paths.Combine(Paths.FileSystem, "movies", $"{name}.vx"), null);
        }

        // MphRead's movie table (Metadata.MovieFiles), as the game's scripts name them
        public static MoviePlayer Open(Movie movie)
        {
            Metadata.MovieInfo info = Metadata.MovieFiles[(int)movie];
            if (info == null)
            {
                throw new ArgumentException($"no movie file for {movie}");
            }
            string top = Paths.Combine(Paths.FileSystem, info.TopScreenPath);
            string? bottom = info.BottomScreenPath == null ? null : Paths.Combine(Paths.FileSystem, info.BottomScreenPath);
            return new MoviePlayer(movie.ToString(), top, bottom);
        }

        private MoviePlayer(string name, string topPath, string? bottomPath)
        {
            if (!File.Exists(topPath))
            {
                throw new FileNotFoundException("movie not found", topPath);
            }
            if (bottomPath != null && !File.Exists(bottomPath))
            {
                bottomPath = null;
            }
            _current?.Dispose();
            _current = this;
            Name = name;
            DualScreen = bottomPath != null;
            _top.Reset();
            _bottom.Reset();
            CancellationToken token = _cts.Token;
            _topTask = Task.Run(() => Decode(_top, topPath, token), token);
            if (bottomPath != null)
            {
                _bottomTask = Task.Run(() => Decode(_bottom, bottomPath, token), token);
            }
        }

        private async Task Decode(VxDecoder decoder, string path, CancellationToken token)
        {
            try
            {
                await decoder.Decode(path, token: token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = $"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}";
            }
        }

        // Advances the playback clock; call once per rendered frame (0 while paused).
        public void Update(double seconds)
        {
            if (Finished || _disposed)
            {
                return;
            }
            if (Error != null)
            {
                Finished = true;
                return;
            }
            PumpAudio();
            if (_sinceProgress.Elapsed.TotalSeconds > StallSeconds)
            {
                Error = $"decoder stalled at frame {FrameIndex + 1} of {FrameCount}";
                Finished = true;
                return;
            }
            if (!Started)
            {
                // like MphRead: start once a few frames are decoded, so the audio has a lead
                if (!Prebuffered(_top, _topTask) || DualScreen && !Prebuffered(_bottom, _bottomTask))
                {
                    return;
                }
                _topFrames = _top.FrameCount;
                _bottomFrames = DualScreen ? _bottom.FrameCount : 0;
                FrameCount = Math.Max(_topFrames, _bottomFrames);
                FrameRate = _top.FrameRate > 0 ? (double)_top.FrameRate : 15;
                AudioSampleRate = _top.AudioSampleRate;
                if (FrameCount == 0 || !ShowFrame(0))
                {
                    Finished = true;
                    return;
                }
                Started = true;
                return;
            }
            _clock += seconds;
            int target = (int)(_clock * FrameRate);
            // every frame goes through GetImage in order: each call frees a decoder slot
            while (FrameIndex < Math.Min(target, FrameCount - 1) && ShowFrame(FrameIndex + 1))
            {
            }
            if (target >= FrameCount && FrameIndex == FrameCount - 1)
            {
                Finished = true; // the last frame has had its time on screen
                return;
            }
            if (FrameIndex >= target)
            {
                _sinceProgress.Restart(); // on time: nothing is owed
            }
            if (FrameIndex < target)
            {
                // the decoder is behind (slow device): hold the clock on the next frame instead of running ahead to the
                // end -- the audio is produced with the frames, so this keeps picture and sound together
                _clock = (FrameIndex + 1) / FrameRate;
            }
        }

        private static bool Prebuffered(VxDecoder decoder, Task? task)
        {
            // FramesQueued is 0 until the header is read and a frame decoded; FrameCount is only valid after that
            int queued = decoder.FramesQueued;
            return queued > 0 && (queued >= Math.Min(4, decoder.FrameCount) || task?.IsCompleted == true);
        }

        private bool ShowFrame(int index)
        {
            // frames are taken strictly in order, so "a frame is queued" means frame 'index' is decoded; both screens
            // must have it (each GetImage frees its decoder's slot -- skipping one would stall that decoder)
            bool needTop = index < _topFrames;
            bool needBottom = DualScreen && index < _bottomFrames;
            if (needTop && _top.FramesQueued <= 0 || needBottom && _bottom.FramesQueued <= 0)
            {
                return false;
            }
            if (needTop && !_top.GetImage(index, TopFrame))
            {
                return false;
            }
            if (needBottom)
            {
                _bottom.GetImage(index, BottomFrame);
            }
            FrameIndex = index;
            FrameVersion++;
            _sinceProgress.Restart();
            return true;
        }

        // Copies newly decoded audio blocks out of the decoders' rings into the queue ReadAudio drains.
        private void PumpAudio()
        {
            // VxDecoder.Reset() leaves the previous movie's audio block total in place until Decode reads this movie's
            // header; once a frame of THIS movie is decoded (Reset zeroed FramesQueued) the total is this movie's
            if (!Started && (_top.FramesQueued <= 0 || DualScreen && _bottom.FramesQueued <= 0))
            {
                return;
            }
            int topTotal = _top.AudioFrameTotal;
            int bottomTotal = DualScreen ? _bottom.AudioFrameTotal : 0;
            int available = topTotal;
            if (DualScreen)
            {
                // a finished file stops limiting the other (its channel is padded with silence past its end)
                int topLimit = _topTask.IsCompleted ? Int32.MaxValue : topTotal;
                int bottomLimit = _bottomTask!.IsCompleted ? Int32.MaxValue : bottomTotal;
                available = Math.Min(Math.Min(topLimit, bottomLimit), Math.Max(topTotal, bottomTotal));
            }
            if (available - _audioIndex > VxDecoder.SampleBufferCount)
            {
                // fell a whole ring behind (shouldn't happen with Update running every frame): drop what was overwritten
                _audioIndex = available - VxDecoder.SampleBufferCount;
            }
            if (available <= _audioIndex)
            {
                return;
            }
            lock (_audioLock)
            {
                int channels = AudioChannels;
                for (; _audioIndex < available; _audioIndex++)
                {
                    EnsureAudioSpace(AudioBlock * channels);
                    ReadOnlySpan<short> left = _audioIndex < topTotal ? _top.GetAudioBuffer(_audioIndex) : _silence;
                    int write = (_audioRead + _audioCount) % _audio.Length;
                    if (channels == 1)
                    {
                        for (int i = 0; i < AudioBlock; i++)
                        {
                            _audio[write] = left[i];
                            write = (write + 1) % _audio.Length;
                        }
                    }
                    else
                    {
                        ReadOnlySpan<short> right = _audioIndex < bottomTotal ? _bottom.GetAudioBuffer(_audioIndex) : _silence;
                        for (int i = 0; i < AudioBlock; i++)
                        {
                            _audio[write] = left[i];
                            _audio[(write + 1) % _audio.Length] = right[i];
                            write = (write + 2) % _audio.Length;
                        }
                    }
                    _audioCount += AudioBlock * channels;
                }
            }
        }

        private void EnsureAudioSpace(int samples)
        {
            if (_audioCount + samples <= _audio.Length)
            {
                return;
            }
            var bigger = new short[Math.Max(_audio.Length * 2, _audioCount + samples)];
            for (int i = 0; i < _audioCount; i++)
            {
                bigger[i] = _audio[(_audioRead + i) % _audio.Length];
            }
            _audio = bigger;
            _audioRead = 0;
        }

        // PCM16, interleaved when AudioChannels is 2. Safe to call from an audio thread. Returns samples written.
        public int ReadAudio(Span<short> destination)
        {
            lock (_audioLock)
            {
                int count = Math.Min(destination.Length, _audioCount);
                for (int i = 0; i < count; i++)
                {
                    destination[i] = _audio[(_audioRead + i) % _audio.Length];
                }
                _audioRead = (_audioRead + count) % _audio.Length;
                _audioCount -= count;
                return count;
            }
        }

        public int QueuedAudioSamples
        {
            get
            {
                lock (_audioLock)
                {
                    return _audioCount;
                }
            }
        }

        public void Skip()
        {
            Skipped = true;
            Finished = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Finished = true;
            _cts.Cancel();
            try
            {
                // the decoders are shared singletons: let this movie's tasks stop before another one resets them
                Task.WaitAll(_bottomTask == null ? new[] { _topTask } : new[] { _topTask, _bottomTask }, TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            if (_current == this)
            {
                _current = null;
            }
            _cts.Dispose();
        }
    }
}
