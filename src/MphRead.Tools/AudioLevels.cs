using System;
using System.IO;
using NCSF123;
using NCSFPlayer;

namespace MphRead
{
    // -audiolevels: measures peak/RMS (dBFS) of our own output pipeline for music (with/without the "fixes"
    // headroom+limiter) and movie audio, so a relative loudness complaint (movie way louder than menu music, one
    // SFX way louder than others) can be checked against real numbers instead of by ear. PC-only, no device needed.
    internal static class AudioLevels
    {
        const float HeadroomGain = 1f; // keep this in sync with GameMusicPlayer.HeadroomGain
        const float DcBlockHz = 10;

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            SeqId seq = args.Length >= 2 && Enum.TryParse(args[1], true, out SeqId s) ? s : SeqId.CHUTNEY;
            Console.WriteLine($"== music: {seq} ==");
            MeasureSeq(seq, fixes: false, "Original (no fixes)");
            MeasureSeq(seq, fixes: true, "Original + fixes (current default)");

            string movie = args.Length >= 3 ? args[2] : "01";
            Console.WriteLine($"== movie: {movie} ==");
            MeasureMovie(movie);
        }

        static void MeasureSeq(SeqId seq, bool fixes, string label)
        {
            const int rate = 32728;
            string path = Paths.Combine(Paths.FileSystem, "_seq", Metadata.SequenceFiles[(int)seq]);
            using var stream = new NCSFPlayerStream(path, (uint)rate, Interpolation.None, skipSilenceOnStartSec: 5,
                defaultLengthInMS: 115000, defaultFadeInMS: 5000, NCSF123.VolumeType.ReplayGainAlbum, PeakType.ReplayGainTrack,
                playForever: true, 1, channelMutes: 0, 0, ignoreVolume: false);
            stream.SquaredSeqVolume = true;
            stream.ExactTempo = true;
            if (fixes)
            {
                stream.DcBlockHz = DcBlockHz;
                stream.OutputGain = HeadroomGain;
                stream.SoftLimit = true;
                stream.Player.Smoothing = true;
                stream.Player.ExactPitch = true;
            }
            var floats = new float[rate * 2]; // 1 second, stereo interleaved
            var bytes = new byte[floats.Length * 4];
            double sumSq = 0;
            float peak = 0;
            long n = 0;
            for (int sec = 0; sec < 10; sec++)
            {
                int got = stream.Read(bytes, 0, bytes.Length);
                if (got <= 0)
                {
                    break;
                }
                Buffer.BlockCopy(bytes, 0, floats, 0, got);
                int count = got / 4;
                for (int i = 0; i < count; i++)
                {
                    float v = Math.Abs(floats[i]);
                    if (v > peak)
                    {
                        peak = v;
                    }
                    sumSq += (double)floats[i] * floats[i];
                    n++;
                }
            }
            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
            Console.WriteLine($"  {label}: peak {ToDb(peak):0.0} dBFS, RMS {ToDb((float)rms):0.0} dBFS ({n / (double)rate / 2:0.0} s measured)");
        }

        // Reuses -movietest's own "sheet" WAV export (System.IO.File path below) instead of re-driving MoviePlayer
        // here: the decode pump has its own pacing quirks (Started/AudioSampleRate only become valid once enough
        // frames are queued) that -movietest already handles correctly, so this measures its proven-good output.
        static void MeasureMovie(string name)
        {
            string outDir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "extract_out", "movies"));
            string wavPath = Path.Combine(outDir, $"{name}.wav");
            if (!File.Exists(wavPath))
            {
                Console.WriteLine($"  run `-movietest {name} sheet` first to produce {wavPath}");
                return;
            }
            byte[] wav = File.ReadAllBytes(wavPath);
            // canonical 44-byte header (see MovieTest.WriteWav): PCM16, data starts right after "data"+size
            int rate = BitConverter.ToInt32(wav, 24);
            short channels = BitConverter.ToInt16(wav, 22);
            var pcm = new short[(wav.Length - 44) / 2];
            Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length * 2);
            double sumSq = 0;
            short peak = 0;
            long n = pcm.Length;
            {
                for (int i = 0; i < pcm.Length; i++)
                {
                    short v = pcm[i];
                    short a = v < 0 ? (short)-v : v;
                    if (a > peak)
                    {
                        peak = a;
                    }
                    sumSq += (double)v * v;
                }
            }
            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
            float peakF = peak / 32768f;
            float rmsF = (float)(rms / 32768f);
            Console.WriteLine($"  {name}: peak {ToDb(peakF):0.0} dBFS, RMS {ToDb(rmsF):0.0} dBFS " +
                $"({n / (double)Math.Max(1, (int)channels) / rate:0.0} s measured, {rate} Hz x{channels})");
        }

        static double ToDb(float linear) => linear <= 0 ? -96 : 20 * Math.Log10(linear);
    }
}
