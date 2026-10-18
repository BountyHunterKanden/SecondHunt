using System;
using MphRecomp.Import.Retro;

namespace MphRecomp.PrimeMusic
{
    // One streamed music track as the Prime games store it, whatever its format (docs/PRIME_MUSIC.md): Prime 1's DSP
    // pairs and RSF files, Echoes' RS03 streams, Corruption's FSB3 files. The app's stream player pulls interleaved PCM16
    // from a reader a block at a time; a looping track never finishes.
    public interface IMusicStream
    {
        int Channels { get; }
        int SampleRate { get; }
        // samples per channel
        int SampleCount { get; }
        bool Loops { get; }
        // first looped sample and the exclusive end of the looped range (0 and SampleCount when not looping)
        int LoopStart { get; }
        int LoopEnd { get; }
        IMusicReader OpenReader();
    }

    public interface IMusicReader
    {
        // the next sample to be played
        long Position { get; }
        bool Finished { get; }
        void Restart();
        // writes up to frames interleaved sample frames into destination; returns how many
        int Read(Span<short> destination, int frames);
    }

    // the app's stream player (App Audio/StreamMusicPlayer.cs)
    public interface IMusicOutput
    {
        // plays stream (what plays now fades out over oldFadeOut) at gain, after delay seconds, fading in over fadeIn;
        // repeat: a song that doesn't loop starts over at its end
        void Start(IMusicStream stream, float gain, float fadeIn, float delay, float oldFadeOut, bool repeat);
        void Stop(float fadeOut);
    }

    public static class MusicFormats
    {
        // a recipe's "format": dsp (Prime 1: one mono file or the left and right files), rsf (Prime 1's G.721 files),
        // rs03 (Echoes), fsb3 (Corruption)
        public static readonly string[] All = { "dsp", "rsf", "rs03", "fsb3" };

        public static IMusicStream Open(string format, byte[][] files, bool rsfLoops = true) => format switch
        {
            "dsp" => DspMusic.FromDsp(files),
            "rsf" => new RsfMusic(One(files), rsfLoops),
            "rs03" => new Rs03Music(new RetroStream(One(files))),
            "fsb3" => DspMusic.FromFsb3(One(files)),
            _ => throw new NotSupportedException($"music format '{format}'"),
        };

        // whether a file read from the disc is what the format expects (an import checks before it copies)
        public static bool Looks(string format, byte[] file) => format switch
        {
            "dsp" => DspMusic.IsDsp(file),
            "rsf" => file.Length > 1024,
            "rs03" => RetroStream.IsRetroStream(file),
            "fsb3" => DspMusic.IsFsb3(file),
            _ => false,
        };

        static byte[] One(byte[][] files) =>
            files.Length == 1 ? files[0] : throw new ArgumentException("this format is one file a track", nameof(files));
    }

    // Echoes' RS03 streams (Import/Retro/RetroStream.cs, clean room D) as a music stream
    public sealed class Rs03Music : IMusicStream
    {
        public readonly RetroStream Stream;

        public Rs03Music(RetroStream stream) { Stream = stream; }

        public int Channels => Stream.Channels;
        public int SampleRate => Stream.SampleRate;
        public int SampleCount => Stream.SampleCount;
        public bool Loops => Stream.Loops;
        public int LoopStart => Stream.LoopStart;
        public int LoopEnd => Stream.LoopEnd;
        public IMusicReader OpenReader() => new Reader(Stream.OpenReader());

        sealed class Reader : IMusicReader
        {
            readonly RetroStreamReader _r;
            public Reader(RetroStreamReader r) { _r = r; }
            public long Position => _r.Position;
            public bool Finished => _r.Finished;
            public void Restart() => _r.Restart();
            public int Read(Span<short> destination, int frames) => _r.Read(destination, frames);
        }
    }
}
