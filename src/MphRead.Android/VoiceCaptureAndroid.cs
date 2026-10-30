using Android.App;
using Android.Content.PM;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using MphRecomp.Online.Voice;
using AndroidLog = Android.Util.Log;

[assembly: UsesPermission(Android.Manifest.Permission.RecordAudio)]

namespace MphRecomp.App;

// Voice chat's microphone on Android (milestone O8): AudioRecord at 16 kHz mono from the VOICE_COMMUNICATION source (the
// device's echo canceller and noise suppressor ride on it where it has them; we switch on the audiofx ones too), read in
// 20 ms frames on its own urgent-audio thread. The first time voice wants the microphone without RECORD_AUDIO, the
// resumed activity asks once; VoiceChat tries again every 2 s, so a yes takes effect without a restart.
internal sealed class VoiceCaptureAndroid : IVoiceCapture
{
    const int PermissionRequest = 0x7108;

    AudioRecord? _record;
    AcousticEchoCanceler? _aec;
    NoiseSuppressor? _ns;
    Thread? _thread;
    volatile bool _run;

    public bool Running => _run;
    public string? Error { get; private set; }

    public bool Start(Action<short[]> frame)
    {
        if (Application.Context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            Error = "no microphone permission";
            VoiceAndroid.AskMicrophone();
            return false;
        }
        int min = AudioRecord.GetMinBufferSize(VoiceFormat.Rate, ChannelIn.Mono, Android.Media.Encoding.Pcm16bit);
        if (min <= 0)
        {
            Error = $"16 kHz mono capture not supported ({min})";
            return false;
        }
        try
        {
            _record = new AudioRecord(AudioSource.VoiceCommunication, VoiceFormat.Rate, ChannelIn.Mono,
                Android.Media.Encoding.Pcm16bit, Math.Max(min, VoiceFormat.FrameSamples * 2 * 8));
            if (_record.State != State.Initialized)
            {
                Error = "the microphone didn't open";
                Release();
                return false;
            }
            int session = _record.AudioSessionId;
            if (AcousticEchoCanceler.IsAvailable)
            {
                _aec = AcousticEchoCanceler.Create(session);
                _aec?.SetEnabled(true);
            }
            if (NoiseSuppressor.IsAvailable)
            {
                _ns = NoiseSuppressor.Create(session);
                _ns?.SetEnabled(true);
            }
            _record.StartRecording();
            if (_record.RecordingState != RecordState.Recording)
            {
                Error = "the microphone is in use";
                Release();
                return false;
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            Error = "microphone: " + ex.Message;
            Release();
            return false;
        }
        AndroidLog.Info("MPHVoice", $"microphone: {VoiceFormat.Rate} Hz, buffer {min} B, echo canceller {(_aec != null ? "on" : "none")}, "
            + $"noise suppressor {(_ns != null ? "on" : "none")}");
        Error = null;
        _run = true;
        AudioRecord record = _record;
        _thread = new Thread(() => ReadLoop(record, frame)) { IsBackground = true, Name = "voice capture" };
        _thread.Start();
        return true;
    }

    void ReadLoop(AudioRecord record, Action<short[]> frame)
    {
        try
        {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        }
        catch (Java.Lang.Exception) { }
        var pcm = new short[VoiceFormat.FrameSamples];
        try
        {
            while (_run)
            {
                int got = 0;
                while (got < pcm.Length && _run)
                {
                    int n = record.Read(pcm, got, pcm.Length - got);
                    if (n < 0)
                    {
                        Error = $"microphone read failed ({n})";
                        _run = false;
                        break;
                    }
                    got += n;
                }
                if (got == pcm.Length)
                {
                    frame(pcm);
                }
            }
        }
        catch (Exception ex)
        {
            Error = "microphone: " + ex.Message;
            _run = false;
        }
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(200);
        _thread = null;
        Release();
    }

    void Release()
    {
        try
        {
            if (_record?.RecordingState == RecordState.Recording) _record.Stop();
        }
        catch (Java.Lang.Exception) { }
        _aec?.Release();
        _ns?.Release();
        _record?.Release();
        _aec = null;
        _ns = null;
        _record = null;
    }

    public void Dispose() => Stop();

    public static int RequestCode => PermissionRequest;
}

// Installs voice chat at app start (MphApp.OnCreate): settings beside recomp_settings.json, this microphone, the shared
// speaker (VoiceAudio); keeps the resumed activity for the one-time microphone question.
internal static class VoiceAndroid
{
    static Activity? _resumed;
    static bool _asked;

    public static void Install(Application app)
    {
        app.RegisterActivityLifecycleCallbacks(new Tracker());
        string dir = app.GetExternalFilesDir(null)?.AbsolutePath ?? app.FilesDir!.AbsolutePath;
        VoiceAudio.Install(dir, () => new VoiceCaptureAndroid());
    }

    // (the capture's thread): ask for RECORD_AUDIO once per run, from whatever screen is up
    public static void AskMicrophone()
    {
        Activity? a = _resumed;
        if (_asked || a == null)
        {
            return;
        }
        _asked = true;
        a.RunOnUiThread(() =>
        {
            try
            {
                a.RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, VoiceCaptureAndroid.RequestCode);
                AndroidLog.Info("MPHVoice", "asked for the microphone");
            }
            catch (Java.Lang.Exception ex)
            {
                AndroidLog.Warn("MPHVoice", "microphone question: " + ex.Message);
            }
        });
    }

    sealed class Tracker : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        public void OnActivityResumed(Activity activity) => _resumed = activity;
        public void OnActivityPaused(Activity activity)
        {
            if (_resumed == activity) _resumed = null;
        }
        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
        public void OnActivityDestroyed(Activity activity) { }
        public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
        public void OnActivityStarted(Activity activity) { }
        public void OnActivityStopped(Activity activity) { }
    }
}
