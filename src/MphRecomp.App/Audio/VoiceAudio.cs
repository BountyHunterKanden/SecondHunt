using MphRecomp.App.Platform;
using MphRecomp.Online.Voice;

namespace MphRecomp.App;

// Voice chat's speaker side on both platforms (milestone O8): its own 16 kHz mono PCM track from AppPlatform.Audio, beside
// the game's music and sound-effect tracks (the game's mixer thread never waits on voice). VoiceChat's playback thread
// writes 20 ms at a time and blocks here, which is its clock.
public sealed class VoiceAudio : IVoicePlayback
{
    // ~100 ms ahead of the speaker: enough for a late thread wake, small enough to keep mouth-to-ear low
    const int BufferFrames = VoiceFormat.Rate / 10;

    readonly IPcmTrack _track;

    VoiceAudio(IPcmTrack track) => _track = track;

    public static VoiceAudio? Open()
    {
        try
        {
            IPcmTrack track = AppPlatform.Audio.CreateTrack(VoiceFormat.Rate, 1, PcmFormat.Int16, BufferFrames, PcmUsage.Effects);
            track.Play();
            Log.Info("MPHVoice", $"voice output: {VoiceFormat.Rate} Hz mono, {BufferFrames}-frame buffer");
            return new VoiceAudio(track);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHVoice", "no voice output: " + ex.Message);
            return null;
        }
    }

    public void Write(short[] samples, int count)
    {
        int at = 0;
        while (at < count)
        {
            int n = _track.Write(samples, at, count - at, blocking: true);
            if (n <= 0)
            {
                Thread.Sleep(VoiceFormat.FrameMs); // paused or failed: keep the playback thread's pace anyway
                return;
            }
            at += n;
        }
    }

    public void Dispose()
    {
        try
        {
            _track.Stop();
            _track.Release();
        }
        catch (Exception ex)
        {
            Log.Warn("MPHVoice", "voice output close: " + ex.Message);
        }
    }

    // once at app start, by each host: settings from its profile folder, its microphone, our speaker, then follow online
    // games (VoiceChat.Start)
    public static void Install(string? profileDir, Func<IVoiceCapture?> capture)
    {
        VoiceSettings.Use(profileDir);
        VoiceDevices.Log = line => Log.Info("MPHVoice", line);
        VoiceDevices.Capture = capture;
        VoiceDevices.Playback = Open;
        VoiceChat.Start();
    }
}
