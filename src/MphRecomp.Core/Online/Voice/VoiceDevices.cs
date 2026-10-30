using System;

namespace MphRecomp.Online.Voice
{
    // Voice chat's one audio format (docs/ONLINE_PLAY.md section 8, milestone O8): 16 kHz mono 16-bit, 20 ms Opus frames,
    // two frames to a packet (25 packets a second while talking, ~2 KB/s), about 16 kbps of speech.
    public static class VoiceFormat
    {
        public const int Rate = 16000;
        public const int FrameMs = 20;
        public const int FrameSamples = Rate * FrameMs / 1000; // 320
        public const int FramesPerPacket = 2;
        public const int Bitrate = 16000;
        // the biggest Opus frame we accept (16 kbps VBR frames are ~40 bytes; FEC and onsets run higher)
        public const int MaxFrameBytes = 200;
    }

    // The microphone: 16 kHz mono 16-bit, FrameSamples at a time, on the capture's own thread (never the game's). Android:
    // AudioRecord (VoiceCaptureAndroid, asks for RECORD_AUDIO the first time); Windows: SoundFlow's capture device.
    public interface IVoiceCapture : IDisposable
    {
        // false (and Error) when it can't start yet: no permission (asked once), no microphone; VoiceChat tries again later
        bool Start(Action<short[]> frame);
        void Stop();
        bool Running { get; }
        string? Error { get; }
    }

    // The speaker side: 16 kHz mono 16-bit into its own output track (beside the game's mixer, never inside it). Write
    // blocks until the samples fit, which paces the voice playback thread.
    public interface IVoicePlayback : IDisposable
    {
        void Write(short[] samples, int count);
    }

    // what the platform gives voice chat, set once at start (Android: VoiceCaptureAndroid.Install; Windows: the host)
    public static class VoiceDevices
    {
        public static Func<IVoiceCapture?>? Capture { get; set; }
        public static Func<IVoicePlayback?>? Playback { get; set; }
        // one line of the voice log (Android logcat MPHVoice); null = quiet
        public static Action<string>? Log { get; set; }

        internal static void Write(string line) => Log?.Invoke(line);
    }
}
