using System;
using System.Collections.Generic;
using System.Threading;
using MphRecomp.Online.Voice;

namespace MphRecomp.App;

// Music ducking for voice chat (Thread C, C3, 2026-10-23): while somebody else is heard the music fades down (DuckEnvelope: an
// onset delay so a short burst doesn't pump, a hold so gaps between words don't, slower release), and comes back after.
// A small timer runs only while a music player or the SFX mixer exists; it hands the envelope's gain to every
// GameMusicPlayer (sequenced track + streamed music, a factor beside the OPTIONS > AUDIO volume) and every GameSfxPlayer's
// streams (the title theme). Off with ONLINE SETTINGS' DUCK MUSIC (VoiceSettings.DuckMusic). Any thread.
internal static class MusicDuck
{
    const int PeriodMs = 20;

    static readonly object s_gate = new();
    static readonly List<GameMusicPlayer> s_music = new();
    static readonly List<GameSfxPlayer> s_sfx = new();
    static readonly DuckEnvelope s_envelope = new();
    static Timer? s_timer;

    public static void Register(GameMusicPlayer p)
    {
        lock (s_gate)
        {
            s_music.Add(p);
            Start();
        }
    }

    public static void Register(GameSfxPlayer p)
    {
        lock (s_gate)
        {
            s_sfx.Add(p);
            Start();
        }
    }

    public static void Unregister(GameMusicPlayer p)
    {
        lock (s_gate)
        {
            s_music.Remove(p);
            Stop();
        }
    }

    public static void Unregister(GameSfxPlayer p)
    {
        lock (s_gate)
        {
            s_sfx.Remove(p);
            Stop();
        }
    }

    // (under s_gate)
    static void Start() => s_timer ??= new Timer(_ => Tick(), null, PeriodMs, PeriodMs);

    static void Stop()
    {
        if (s_music.Count + s_sfx.Count > 0) return;
        s_timer?.Dispose();
        s_timer = null;
    }

    static void Tick()
    {
        GameMusicPlayer[] music;
        GameSfxPlayer[] sfx;
        float gain;
        lock (s_gate)
        {
            if (s_timer == null) return;
            bool heard = VoiceChat.Current?.AnyoneHeard == true;
            float before = s_envelope.Gain;
            gain = s_envelope.Step(Environment.TickCount64, heard, VoiceSettings.Current.DuckMusic);
            if (gain == before && gain == 1) return; // (nothing to do: the usual case)
            music = s_music.ToArray();
            sfx = s_sfx.ToArray();
        }
        foreach (GameMusicPlayer p in music) p.SetDuck(gain);
        foreach (GameSfxPlayer p in sfx) p.StreamDuck = gain;
    }
}
