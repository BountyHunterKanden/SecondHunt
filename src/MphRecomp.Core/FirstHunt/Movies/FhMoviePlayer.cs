using System;
using System.IO;
using MphRead;
using MphRecomp.Media;

namespace MphRecomp.FirstHunt.Movies
{
    // The cart's movie table (arm9 0x20759c4), in its order
    public enum FhMovie
    {
        Opening = 0,
        Death = 1,
        Teaser = 2,
        SpawnYellow = 3,
        SpawnGreen = 4,
        SpawnWhite = 5,
        SpawnBlue = 6
    }

    // Plays one of First Hunt's movies (docs/FIRST_HUNT_PORT.md P6) for the app's presenter (IMoviePlayback): the pair
    // of files from the cart's movie table (0x20759c4, 12 bytes each: {u16 channel volume, u16, char* up-left,
    // char* down-right}), the up-left file on the top screen and the left speaker, the down-right one on the bottom
    // screen and the right speaker, each at the entry's volume (the cart's player 0x20370f0: two PCM16 channels at
    // 32768 Hz, pan 0 and 127). Decoded on the caller's thread as the clock reaches each frame (about 1 ms a frame).
    //
    //   var movie = FhMoviePlayer.Open(FhMovie.Teaser);   each frame: movie.Update(seconds); presenter.Draw(movie, ...)
    public sealed class FhMoviePlayer : IMoviePlayback
    {
        public const int Width = FvdsVideo.Width, Height = FvdsVideo.Height;
        private const int MovieTable = 0x20759c4;
        private const int EntrySize = 12;

        public FhMovie Movie { get; }
        public string Name => Movie.ToString();
        public bool DualScreen => true;
        public byte[] TopFrame { get; } = new byte[Width * Height * 3];
        public byte[] BottomFrame { get; } = new byte[Width * Height * 3];
        public int FrameVersion { get; private set; }
        public int FrameIndex { get; private set; } = -1;
        public int FrameCount { get; }
        public double FrameRate { get; }
        public double Duration => FrameCount / FrameRate;
        public bool Started => FrameIndex >= 0;
        public bool Finished { get; private set; }
        public bool Skipped { get; private set; }
        public int AudioSampleRate { get; }
        public int AudioChannels => 2;
        public int? DsVolume { get; }

        private readonly FvdsFile _up, _down;
        private readonly FvdsVideo _upVideo = new FvdsVideo(), _downVideo = new FvdsVideo();
        private readonly FvdsAudio _upAudio = new FvdsAudio(), _downAudio = new FvdsAudio();
        private readonly short[] _left = new short[FvdsAudio.SamplesPerRecord], _right = new short[FvdsAudio.SamplesPerRecord];
        private int _audioFrame = 0;
        private double _clock = 0;

        private readonly object _audioLock = new object();
        private short[] _audio = new short[32768 * 2];
        private int _audioRead, _audioCount;

        // the movie's two files, as the cart names them (from the imported arm9), under the First Hunt file system
        public static (string UpLeft, string DownRight, int Volume)? Files(FhMovie movie)
        {
            int at = MovieTable + (int)movie * EntrySize;
            if (FirstHuntRules.ReadArm9Int(at) is not int head || FirstHuntRules.ReadArm9Int(at + 4) is not int up
                || FirstHuntRules.ReadArm9Int(at + 8) is not int down)
            {
                return null;
            }
            string? Path(int address)
            {
                string? name = FhMenuData.Str(address, 0x40);
                // "data/movies/x.fv": the file system is the data folder
                return string.IsNullOrEmpty(name) ? null
                    : Paths.Combine(Paths.FhFileSystem, name.StartsWith("data/") ? name["data/".Length..] : name);
            }
            return Path(up) is string u && Path(down) is string d ? (u, d, Math.Clamp(head & 0xffff, 0, 127)) : null;
        }

        // why the last Open returned null, for the host's log
        public static string? OpenError { get; private set; }
        // frames that didn't decode (bad data: the picture holds)
        public int BadFrames { get; private set; }

        // null (OpenError says why) if the files are missing or unreadable
        public static FhMoviePlayer? Open(FhMovie movie)
        {
            OpenError = null;
            try
            {
                if (Files(movie) is not (string up, string down, int volume))
                {
                    OpenError = $"movie {movie}: no entry in the cart's movie table";
                    return null;
                }
                FvdsFile? a = File.Exists(up) ? FvdsFile.Read(File.ReadAllBytes(up)) : null;
                FvdsFile? b = File.Exists(down) ? FvdsFile.Read(File.ReadAllBytes(down)) : null;
                if (a == null || b == null || a.Frames.Count == 0 || !FvdsAudio.Available)
                {
                    OpenError = $"movie {movie}: {(a == null ? up : b == null ? down : "no frames or no cart code")}";
                    return null;
                }
                return new FhMoviePlayer(movie, a, b, volume);
            }
            catch (Exception ex)
            {
                OpenError = $"movie {movie}: {ex.Message}";
                return null;
            }
        }

        private FhMoviePlayer(FhMovie movie, FvdsFile up, FvdsFile down, int volume)
        {
            Movie = movie;
            _up = up;
            _down = down;
            DsVolume = volume;
            FrameCount = Math.Max(up.Frames.Count, down.Frames.Count);
            FrameRate = up.Fps16 > 0 ? up.FramesPerSecond : 15;
            AudioSampleRate = up.AudioRate > 0 ? up.AudioRate : 32768;
            ShowFrame(0);
            QueueAudio(1);
        }

        // advances the clock (0 while paused); decodes the frames it reaches, the sound a frame ahead of the picture
        public void Update(double seconds)
        {
            if (Finished) return;
            _clock += seconds;
            int target = (int)(_clock * FrameRate);
            if (target >= FrameCount)
            {
                Finished = true;
                return;
            }
            // a long stall (app paused) doesn't fast-forward through every frame: skip the clock ahead instead
            while (FrameIndex < target && ShowFrame(FrameIndex + 1))
            {
            }
            QueueAudio(Math.Min(FrameCount - 1, target + 1));
        }

        public void Skip()
        {
            Skipped = true;
            Finished = true;
        }

        private bool ShowFrame(int index)
        {
            if (index >= FrameCount) return false;
            bool ok = Decode(_up, _upVideo, index, TopFrame);
            ok = Decode(_down, _downVideo, index, BottomFrame) && ok;
            FrameIndex = index;
            FrameVersion++;
            if (!ok) BadFrames++;
            return true;
        }

        private static bool Decode(FvdsFile file, FvdsVideo video, int index, byte[] rgb)
        {
            if (index >= file.Frames.Count) return true; // the shorter file holds its last frame
            FvdsFile.Frame f = file.Frames[index];
            bool ok = video.Decode(file.Data, f.VideoOffset, f.VideoLength);
            ReadOnlySpan<ushort> px = video.Frame;
            for (int i = 0, o = 0; i < px.Length; i++, o += 3)
            {
                int c = px[i];
                int r = c & 31, g = c >> 5 & 31, b = c >> 10 & 31;
                rgb[o] = (byte)(r << 3 | r >> 2);
                rgb[o + 1] = (byte)(g << 3 | g >> 2);
                rgb[o + 2] = (byte)(b << 3 | b >> 2);
            }
            return ok;
        }

        // decodes both files' sound through frame 'last' into the interleaved queue (left = up-left)
        private void QueueAudio(int last)
        {
            for (; _audioFrame <= last && _audioFrame < FrameCount; _audioFrame++)
            {
                int records = Math.Max(Records(_up, _audioFrame), Records(_down, _audioFrame));
                for (int r = 0; r < records; r++)
                {
                    Record(_up, _upAudio, _audioFrame, r, _left);
                    Record(_down, _downAudio, _audioFrame, r, _right);
                    lock (_audioLock)
                    {
                        Ensure(FvdsAudio.SamplesPerRecord * 2);
                        int write = (_audioRead + _audioCount) % _audio.Length;
                        for (int i = 0; i < FvdsAudio.SamplesPerRecord; i++)
                        {
                            _audio[write] = _left[i];
                            _audio[(write + 1) % _audio.Length] = _right[i];
                            write = (write + 2) % _audio.Length;
                        }
                        _audioCount += FvdsAudio.SamplesPerRecord * 2;
                    }
                }
            }
        }

        private static int Records(FvdsFile file, int frame) =>
            frame < file.Frames.Count ? file.Frames[frame].AudioLength / FvdsAudio.RecordSize : 0;

        private static void Record(FvdsFile file, FvdsAudio audio, int frame, int record, short[] output)
        {
            if (record >= Records(file, frame))
            {
                Array.Clear(output);
                return;
            }
            FvdsFile.Frame f = file.Frames[frame];
            audio.Decode(file.Data.AsSpan(f.AudioOffset + record * FvdsAudio.RecordSize, FvdsAudio.RecordSize), output);
        }

        private void Ensure(int samples)
        {
            if (_audioCount + samples <= _audio.Length) return;
            var bigger = new short[Math.Max(_audio.Length * 2, _audioCount + samples)];
            for (int i = 0; i < _audioCount; i++)
            {
                bigger[i] = _audio[(_audioRead + i) % _audio.Length];
            }
            _audio = bigger;
            _audioRead = 0;
        }

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
    }
}
