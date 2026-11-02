using System;
using System.IO;
using System.Text;

namespace MphRecomp.Replay
{
    // Owner 2026-11-01: "if a replay can't be played I don't want it to be playable at all". A replay that doesn't match
    // this build is refused up front by its header and data fingerprint (ReplayPlayer.CheckBeforeStart, ReplayData).
    // Should one still be seen not to play exactly -- by the viewer reaching a differing step, or by the background
    // prepare (ReplayPrep.Run) -- that is written here, and from then on the REPLAYS list and the open path refuse it with
    // the step named, up front.
    //   <data>/replays/verdicts/<key>.txt   (key = this build + the replay file as it is on disk; another build re-checks)
    public static class ReplayVerdict
    {
        static string? Dir => ReplayData.ReplaysRoot is string root ? Path.Combine(root, "replays", "verdicts") : null;

        static string Key(string path, ReplayFile header)
        {
            var info = new FileInfo(path);
            ulong h = 14695981039346656037UL;
            void Add(ReadOnlySpan<byte> b)
            {
                foreach (byte x in b) h = (h ^ x) * 1099511628211UL;
            }
            Add(Encoding.UTF8.GetBytes(ReplayPrep.BuildId));
            Add(Encoding.UTF8.GetBytes(header.Build));
            Add(BitConverter.GetBytes(info.Length));
            Add(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
            Add(BitConverter.GetBytes(header.Rng1));
            Add(BitConverter.GetBytes(header.Rng2));
            return h.ToString("x16");
        }

        // null = not known to differ on this build; else why it can't be played
        public static string? Refused(string path, ReplayFile header)
        {
            try
            {
                if (Dir is not string dir || !File.Exists(path)) return null;
                string file = Path.Combine(dir, Key(path, header) + ".txt");
                return File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // (a viewer or prepare that saw step `step` differ) marks the replay unplayable on this build
        public static void MarkFailed(string path, ReplayFile header, int step)
        {
            try
            {
                if (Dir is not string dir || !File.Exists(path)) return;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, Key(path, header) + ".txt"),
                    $"it doesn't play exactly as recorded on this build (differs from {Clock(step)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // a step as m:ss at the sim's 60 steps a second
        public static string Clock(int step)
        {
            int s = step / 60;
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
