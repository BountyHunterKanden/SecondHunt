using Android.App;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using MphRead;
using MphRead.Formats.Sound;
using NCSF123;
using NCSFPlayer;
using Button = Android.Widget.Button;
using Orientation = Android.Widget.Orientation;
using Stopwatch = System.Diagnostics.Stopwatch;
using VolumeType = NCSF123.VolumeType;

namespace MphRecomp.App;

// Music A/B test. Plays any song from the sound test's named playlist (the song plus the tracks the game switches on for
// it) rendered once per mode, all in lockstep from the user's own ROM data. Switching modes only changes which render is
// audible, so the song never restarts. Every mode plays at the same loudness: the clean-output modes render with
// headroom, and the plain modes are turned down by the same amount after rendering (so they still clip where they
// would today). Switch with the mode buttons, or L2 / Select / Y (next) and L1 (previous) on the Odin.
[Activity(Name = "com.mphrecomp.app.MusicTestActivity", Label = "mph-recomp music", Exported = BuildFlags.ExportDevActivities,
    ScreenOrientation = ScreenOrientation.Landscape)]
public class MusicTestActivity : Activity
{
    // keep this in sync with GameMusicPlayer.HeadroomGain: unity, the DS scale once the SDAT sequence volume is on
    // the squared curve (SquaredSeqVolume, every lane); the look-ahead limiter catches cues that go over full scale.
    const float HeadroomGain = 1f;
    const float DcBlockHz = 10;
    // Interpolation band-limits the samples, losing the brightness the DS's sample-and-hold playback adds above ~6 kHz;
    // this shelf was fitted to put HQ's band balance back within ~0.65 dB of the original's across several songs.
    const float ToneShelfHz = 7000;
    const float ToneShelfDb = 6;

    // Fixes = the samples' DC offset removed, headroom with a look-ahead limiter instead of clipping, volume/pan/vibrato
    // smoothed between sequencer ticks, and exact pitch. None of them change the tone.
    sealed record Mode(string Name, string Detail, int Rate, Interpolation Interpolation, bool Fixes, bool ToneMatch);

    static readonly Mode[] Modes =
    [
        new("Original", "32.7 kHz, no interpolation -- what the recomp plays today", 32728, Interpolation.None, false, false),
        new("Original + fixes", "the DS's own bright playback, with the DC offset removed, no clipping, smoothed " +
            "volume/pan/vibrato and exact pitch", 32728, Interpolation.None, true, false),
        new("HQ + fixes", "48 kHz sinc interpolation (cleaner but duller up top) with the same fixes",
            48000, Interpolation.Sinc, true, false),
        new("HQ + fixes + tone", "HQ + fixes with a treble shelf that restores the original's brightness",
            48000, Interpolation.Sinc, true, true),
    ];

    readonly MultiPlayer _player = new();
    readonly Button[] _modeButtons = new Button[Modes.Length];
    TextView _status = null!;
    string? _playing;
    string? _message;
    float _l2Axis;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(36, 32, 36, 16);

        var modeRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Modes.Length; i++)
        {
            int mode = i;
            var b = new Button(this) { TextSize = 16f };
            b.Click += (_, _) => SetMode(mode);
            modeRow.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _modeButtons[i] = b;
        }
        root.AddView(modeRow);

        _status = new TextView(this) { TextSize = 14f };
        _status.SetPadding(0, 12, 0, 12);
        root.AddView(_status);

        var stop = new Button(this) { Text = "Stop" };
        stop.Click += (_, _) =>
        {
            _player.Stop();
            _playing = null;
            UpdateUi();
        };
        root.AddView(stop);

        var scroll = new ScrollView(this);
        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(list);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);

        try
        {
            Directory.SetCurrentDirectory(FilesDir!.AbsolutePath);
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            string seqDir = Path.GetFullPath(Paths.Combine(Paths.FileSystem, "_seq"));
            IReadOnlyList<MusicTrack> cues = SoundRead.ReadInterMusicInfo();
            foreach ((MphRead.Menu.MusicType type, int id, string name) in MphRead.Menu.MusicList)
            {
                SeqId seq;
                ushort tracks;
                if (type == MphRead.Menu.MusicType.Music)
                {
                    seq = cues[id].SeqId;
                    tracks = cues[id].Tracks;
                }
                else if (type == MphRead.Menu.MusicType.Seq)
                {
                    seq = (SeqId)id;
                    tracks = UInt16.MaxValue;
                }
                else
                {
                    continue; // the title theme is a pre-rendered stream, not a sequence
                }
                string path = Path.Combine(seqDir, Metadata.SequenceFiles[(int)seq]);
                var b = new Button(this) { Text = name };
                b.Click += (_, _) => Play(name, path, tracks);
                list.AddView(b);
            }
            if (!File.Exists(Path.Combine(seqDir, "mph.ncsflib")))
            {
                _message = $"Music files not found in {seqDir}. Re-extract the ROM from the main screen to create them.";
            }
        }
        catch (Exception ex)
        {
            _message = "Couldn't read the game's music table: " + ex.Message;
            Log.Error("MPHMusic", ex.ToString());
        }
        UpdateUi();
    }

    void Play(string name, string path, ushort tracks)
    {
        _playing = null;
        _message = $"Loading {name}...";
        UpdateUi();
        // loading renders every mode's stream, which takes a moment -- keep it off the UI thread
        Task.Run(() =>
        {
            try
            {
                _player.Play(path, tracks);
                _playing = name;
                _message = null;
            }
            catch (Exception ex)
            {
                _message = $"Couldn't play {name}: {ex.Message}";
                Log.Error("MPHMusic", ex.ToString());
            }
            RunOnUiThread(UpdateUi);
        });
    }

    void SetMode(int mode)
    {
        _player.SetAudible((mode + Modes.Length) % Modes.Length);
        UpdateUi();
    }

    void UpdateUi()
    {
        int audible = _player.Audible;
        for (int i = 0; i < Modes.Length; i++)
        {
            _modeButtons[i].Text = (i == audible ? "▶ " : "") + Modes[i].Name;
        }
        string now = $"Hearing: {Modes[audible].Name} -- {Modes[audible].Detail}.";
        _status.Text = _message != null ? $"{_message}\n{now}"
            : _playing != null ? $"Playing {_playing}. All modes run in sync at the same loudness; switching only changes which you hear.\n{now}"
            : $"Pick a song below, then switch modes with the buttons, or L2 / Select / Y (next) and L1 (previous).\n{now}";
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        int step = keyCode switch
        {
            Keycode.ButtonL2 or Keycode.ButtonSelect or Keycode.ButtonY => 1,
            Keycode.ButtonL1 => -1,
            _ => 0
        };
        if (step != 0)
        {
            if (e == null || e.RepeatCount == 0)
            {
                SetMode(_player.Audible + step);
            }
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        // the Odin reports L2 as an analog trigger axis rather than a key (see RenderActivity)
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            float l2 = e.GetAxisValue(Axis.Ltrigger);
            if (l2 >= 0.5f && _l2Axis < 0.5f)
            {
                SetMode(_player.Audible + 1);
            }
            _l2Axis = l2;
        }
        return base.OnGenericMotionEvent(e);
    }

    protected override void OnPause()
    {
        base.OnPause();
        _player.Stop();
        _playing = null;
        UpdateUi();
    }

    protected override void OnDestroy()
    {
        _player.Stop();
        base.OnDestroy();
    }

    // One NCSF render per mode, each feeding its own AudioTrack at its own rate. All start from the same prefilled point
    // and are consumed in real time, so they stay aligned; switching only changes the track volumes.
    sealed class MultiPlayer
    {
        const int ChunkFrames = 512;

        sealed class Lane
        {
            public readonly Mode Mode;
            public readonly NCSFPlayerStream Stream;
            public readonly AudioTrack Track;
            public Thread? Thread;
            public volatile bool Stopped;

            public Lane(Mode mode, string path, ushort tracks)
            {
                Mode = mode;
                // same settings as MusicPlayer.Load, except no silence skip so every lane starts on the same sample
                Stream = new NCSFPlayerStream(path, (uint)mode.Rate, mode.Interpolation, skipSilenceOnStartSec: 0,
                    defaultLengthInMS: 115000, defaultFadeInMS: 5000, VolumeType.ReplayGainAlbum, PeakType.ReplayGainTrack,
                    playForever: true, volumeMultiplier: 1, channelMutes: 0, (ushort)(tracks ^ UInt16.MaxValue), ignoreVolume: false);
                Stream.SquaredSeqVolume = true; // hardware behaviour, so every lane gets it
                Stream.ExactTempo = true; // likewise: the DS sequencer tick rate
                if (mode.Fixes)
                {
                    Stream.DcBlockHz = DcBlockHz;
                    Stream.OutputGain = HeadroomGain;
                    Stream.Limiter = true; // as GameMusicPlayer
                    Stream.Player.Smoothing = true;
                    Stream.Player.ExactPitch = true;
                }
                if (mode.ToneMatch)
                {
                    Stream.HighShelfHz = ToneShelfHz;
                    Stream.HighShelfDb = ToneShelfDb;
                }
                int minBytes = AudioTrack.GetMinBufferSize(mode.Rate, ChannelOut.Stereo, Android.Media.Encoding.PcmFloat);
                int bytes = Math.Max(minBytes, mode.Rate / 4 * 8); // at least 250 ms of stereo float
                Track = new AudioTrack.Builder()
                    .SetAudioAttributes(new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.Game)!
                        .SetContentType(AudioContentType.Music)!
                        .Build()!)
                    .SetAudioFormat(new AudioFormat.Builder()
                        .SetEncoding(Android.Media.Encoding.PcmFloat)!
                        .SetSampleRate(mode.Rate)!
                        .SetChannelMask(ChannelOut.Stereo)!
                        .Build()!)
                    .SetBufferSizeInBytes(bytes)
                    .SetTransferMode(AudioTrackMode.Stream)
                    .Build();
            }
        }

        readonly object _gate = new();
        Lane[]? _lanes;
        volatile int _audible;

        public int Audible => _audible;

        public void Play(string path, ushort tracks)
        {
            lock (_gate)
            {
                StopLocked();
                Lane[] lanes = [.. Modes.Select(mode => new Lane(mode, path, tracks))];
                _lanes = lanes;
                ApplyVolumes();
                // 100 ms of each up front so every track begins on the same musical sample
                foreach (Lane lane in lanes)
                {
                    Prefill(lane);
                }
                foreach (Lane lane in lanes)
                {
                    lane.Track.Play();
                }
                foreach (Lane lane in lanes)
                {
                    lane.Thread = new Thread(() => Pump(lane)) { IsBackground = true, Name = "mph-music-" + lane.Mode.Name };
                    lane.Thread.Start();
                }
                ApplyVolumes(); // a mode picked while this was loading
            }
        }

        public void SetAudible(int index)
        {
            _audible = index;
            // don't block the UI behind a song that's still loading; Play applies the latest choice when it finishes
            if (Monitor.TryEnter(_gate))
            {
                try
                {
                    ApplyVolumes();
                }
                finally
                {
                    Monitor.Exit(_gate);
                }
            }
        }

        void ApplyVolumes()
        {
            if (_lanes == null)
            {
                return;
            }
            for (int i = 0; i < _lanes.Length; i++)
            {
                // plain modes render at full level and clip as today; turn them down to the fixed modes' loudness
                _lanes[i].Track.SetVolume(i != _audible ? 0f : _lanes[i].Mode.Fixes ? 1f : HeadroomGain);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                StopLocked();
            }
        }

        void StopLocked()
        {
            if (_lanes == null)
            {
                return;
            }
            foreach (Lane lane in _lanes)
            {
                lane.Stopped = true;
                // pause + flush frees buffer space, which releases a pump blocked in Write
                lane.Track.Pause();
                lane.Track.Flush();
            }
            foreach (Lane lane in _lanes)
            {
                lane.Thread?.Join(1000);
                lane.Track.Stop();
                lane.Track.Release();
                lane.Stream.Dispose();
            }
            _lanes = null;
        }

        static void Prefill(Lane lane)
        {
            var bytes = new byte[ChunkFrames * 8];
            var floats = new float[ChunkFrames * 2];
            for (int frames = 0; frames < lane.Mode.Rate / 10; frames += ChunkFrames)
            {
                int n = lane.Stream.Read(bytes, 0, bytes.Length);
                Buffer.BlockCopy(bytes, 0, floats, 0, n);
                lane.Track.Write(floats, 0, n / 4, WriteMode.NonBlocking);
            }
        }

        static void Pump(Lane lane)
        {
            var bytes = new byte[ChunkFrames * 8];
            var floats = new float[ChunkFrames * 2];
            var render = new Stopwatch();
            long frames = 0;
            bool reported = false;
            try
            {
                while (!lane.Stopped)
                {
                    render.Start();
                    int n = lane.Stream.Read(bytes, 0, bytes.Length);
                    render.Stop();
                    if (n <= 0)
                    {
                        break;
                    }
                    Buffer.BlockCopy(bytes, 0, floats, 0, n);
                    if (lane.Track.Write(floats, 0, n / 4, WriteMode.Blocking) < 0)
                    {
                        break;
                    }
                    frames += n / 8;
                    if (!reported && frames >= lane.Mode.Rate * 5L)
                    {
                        // rendering must stay well under real time or the lanes drift apart
                        double audioSec = frames / (double)lane.Mode.Rate;
                        Log.Info("MPHMusic", $"{lane.Mode.Name}: rendered {audioSec:0.0}s of audio in {render.Elapsed.TotalSeconds:0.00}s " +
                            $"({render.Elapsed.TotalSeconds / audioSec:0.00}x real time)");
                        reported = true;
                    }
                }
            }
            catch (Exception ex) when (lane.Stopped)
            {
                // torn down while mid-render; nothing to report
                Log.Debug("MPHMusic", $"{lane.Mode.Name} stopped: {ex.GetType().Name}");
            }
        }
    }
}
