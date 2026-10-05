using System;
using MphRead.Formats.Sound;
using SoundFlow.Enums;

namespace MphRead
{
    // A desktop stand-in for MphRead.Android's AndroidMusicPlayer (MusicPlayer.IHost), for comparing what MphRead's
    // own game logic (Music.PlayRoomMusic/PlayMusic/Stop/track fades...) actually calls during a headless campaign
    // sim against what the device's MPHAudio log shows, without needing a device. Enable with MPHAUDIOLOG=1
    // (see CampaignSim.Run); every call just logs to the console and fakes minimal state (no real audio).
    internal sealed class LoggingMusicHost : MusicPlayer.IHost
    {
        float _volume = 1;
        ushort _tracks;
        ushort _tempo;
        bool _playing;

        public void Load(SeqId seqId, ushort tracks, float volume, Action loaded)
        {
            Console.WriteLine($"[music] Load({seqId}, tracks=0x{tracks:X4}, vol={volume:0.00})");
            _tracks = tracks;
            _volume = volume;
            loaded();
        }

        public void Play(float volume)
        {
            Console.WriteLine($"[music] Play({volume:0.00})");
            _volume = volume;
            _playing = true;
        }

        public void Pause()
        {
            Console.WriteLine("[music] Pause()");
        }

        public void Stop()
        {
            Console.WriteLine("[music] Stop()");
            if (Environment.GetEnvironmentVariable("MPHAUDIOLOG") == "2")
            {
                var st = new System.Diagnostics.StackTrace(1, true);
                for (int i = 0; i < Math.Min(8, st.FrameCount); i++)
                {
                    var f = st.GetFrame(i);
                    Console.WriteLine($"    at {f?.GetMethod()} ({System.IO.Path.GetFileName(f?.GetFileName())}:{f?.GetFileLineNumber()})");
                }
            }
            _playing = false;
        }

        public PlaybackState State => _playing ? PlaybackState.Playing : PlaybackState.Stopped;

        public float Volume
        {
            get => _volume;
            set { Console.WriteLine($"[music] Volume = {value:0.00}"); _volume = value; }
        }

        public ushort Tracks
        {
            get => _tracks;
            set { Console.WriteLine($"[music] Tracks = 0x{value:X4}"); _tracks = value; }
        }

        public ushort Tempo
        {
            get => _tempo;
            set { Console.WriteLine($"[music] Tempo = {value}"); _tempo = value; }
        }

        public NCSFCommon.Track? GetTrack(int index) => null;
    }
}
