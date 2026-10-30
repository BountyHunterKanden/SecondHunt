using System;
using System.IO;
using System.Linq;

namespace MphRecomp.Replay
{
    // Sharing replays (2026-10-19, R3's last leftover; docs/REPLAY_MODE.md): REPLAYS' SHARE hands a copy of the file to another
    // app (Android: the share sheet through the app's own read-only provider; Windows: the file shown in Explorer), named by
    // its title so the other side sees what it is; a replay file opened with or shared to the app comes in as a shared one
    // (shared/: never pushed out by the last ten), after the same checks as any file (ReplayFile.Read: whole, intact, a
    // format this build reads). Whether it plays here is REPLAYS' usual check (CAN'T + the reason).
    public static class ReplayShare
    {
        public const string MimeType = "application/octet-stream";
        private const long MaxBytes = 64L << 20;

        // a copy of the replay at `path` in `shareDir` (emptied first: one shared at a time), named by its title
        public static string ShareCopy(string path, string shareDir)
        {
            Directory.CreateDirectory(shareDir);
            foreach (string old in Directory.GetFiles(shareDir))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            // (its file's name: the date, mode and arena, or the title it was renamed to)
            string name = Clean(Path.GetFileNameWithoutExtension(path));
            string copy = Path.Combine(shareDir, name + ReplayLibrary.Extension);
            File.Copy(path, copy, overwrite: true);
            return copy;
        }

        // a received replay (a file opened with or shared to the app) into the shared ones (ReplayLibrary.SaveShared; favorites
        // before 2026-10-19's shared section): throws InvalidDataException for anything that isn't an intact replay this build
        // reads (its message says why), IOException when it can't be saved; returns the saved file's path
        public static string Import(string dataDir, Stream stream)
        {
            var bytes = new MemoryStream();
            byte[] buffer = new byte[1 << 16];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                bytes.Write(buffer, 0, n);
                if (bytes.Length > MaxBytes) throw new InvalidDataException("too big to be a replay");
            }
            return ReplayLibrary.SaveShared(dataDir, bytes.ToArray(), "a file");
        }

        private static string Clean(string name)
        {
            string clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' ? c : '_').ToArray()).Trim();
            return clean.Length == 0 ? "replay" : clean;
        }
    }
}
