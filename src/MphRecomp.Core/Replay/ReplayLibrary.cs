using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MphRecomp.Replay
{
    // Where the app keeps replays, beside the player's other files (docs/REPLAY_MODE.md R2):
    //   <data>/replays/          the last KeepRecent matches, saved as they end; the oldest goes when a new one comes
    //   <data>/replays/pinned/   the ones the player chose to keep (never removed by the app)
    //   <data>/replays/shared/   the ones that came from someone else (a friend's shared replay, a file opened with or shared
    //                            to the app: 2026-10-19); kept until deleted, never pushed out by the last ten
    // Names sort by time: "20261007-131502 Battle Combat Hall.mphreplay".
    public static class ReplayLibrary
    {
        public const int KeepRecent = 10;
        public const string Extension = ".mphreplay";

        public static string RecentDir(string dataDir) => Path.Combine(dataDir, "replays");
        public static string PinnedDir(string dataDir) => Path.Combine(dataDir, "replays", "pinned");
        public static string SharedDir(string dataDir) => Path.Combine(dataDir, "replays", "shared");

        // Writes the replay (to a temporary name first, so a crash mid-write never leaves a cut file under the real
        // name), then drops the oldest unpinned ones past KeepRecent. Returns the new file's path.
        public static string Save(string dataDir, ReplayFile file, DateTime when)
        {
            string dir = RecentDir(dataDir);
            Directory.CreateDirectory(dir);
            string arena = MphRecomp.Multiplayer.MatchArenas.Find(file.Settings.Arena)?.InGameName ?? file.Settings.Arena;
            string name = $"{when:yyyyMMdd-HHmmss} {file.Settings.Mode} {Clean(arena)}";
            string path = Path.Combine(dir, name + Extension);
            for (int i = 2; File.Exists(path); i++)
            {
                path = Path.Combine(dir, $"{name} ({i}){Extension}");
            }
            string tmp = path + ".tmp";
            file.Save(tmp);
            File.Move(tmp, path);
            Prune(dataDir);
            return path;
        }

        // keeps the newest `keep` recent replays (pinned ones don't count and are never touched)
        public static void Prune(string dataDir, int keep = KeepRecent)
        {
            foreach (string old in Recent(dataDir).Skip(keep))
            {
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // in use or gone: next time
                }
            }
        }

        // newest first
        public static IReadOnlyList<string> Recent(string dataDir) => Files(RecentDir(dataDir));
        public static IReadOnlyList<string> Pinned(string dataDir) => Files(PinnedDir(dataDir));
        public static IReadOnlyList<string> Shared(string dataDir) => Files(SharedDir(dataDir));

        // a replay from someone else into shared/: throws InvalidDataException for anything that isn't an intact replay this
        // build reads (its message says why); named "<now> <mode> <arena> from <who>"; returns the saved file's path
        public static string SaveShared(string dataDir, byte[] bytes, string from)
        {
            ReplayFile.Read(new MemoryStream(bytes)); // (checked whole; the bytes are kept as they came)
            // (named without the arena table: the app may have been started just to take the file, its game files not read yet)
            ReplayFile header = ReplayFile.ReadHeader(new MemoryStream(bytes), out _);
            string dir = SharedDir(dataDir);
            Directory.CreateDirectory(dir);
            string name = $"{DateTime.Now:yyyyMMdd-HHmmss} " + Clean($"{header.Settings.Mode} {header.Settings.Arena.ToLowerInvariant()} from {from}");
            string path = Path.Combine(dir, name + Extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){Extension}");
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path);
            return path;
        }

        public static bool IsShared(string dataDir, string path) =>
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(SharedDir(dataDir)), StringComparison.Ordinal);

        // the most recently saved replay, pinned or not (null = none yet)
        public static string? Newest(string dataDir)
        {
            return Recent(dataDir).Concat(Pinned(dataDir)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        // keeps a recent replay for good (moves it to pinned/); returns its new path
        public static string Pin(string dataDir, string path)
        {
            string dir = PinnedDir(dataDir);
            Directory.CreateDirectory(dir);
            string to = Path.Combine(dir, Path.GetFileName(path));
            File.Move(path, to);
            return to;
        }

        // back among the recent ones (the oldest past KeepRecent go, as after a save)
        public static string Unpin(string dataDir, string path)
        {
            string dir = RecentDir(dataDir);
            Directory.CreateDirectory(dir);
            string to = Path.Combine(dir, Path.GetFileName(path));
            File.Move(path, to);
            Prune(dataDir);
            return to;
        }

        // ---- the REPLAYS list (docs/REPLAY_MODE.md R3): what the multiplayer menu's REPLAYS page shows and does ----

        // Every replay, newest first, pinned, recent and shared together (a shared one says so: ReplayInfo.Shared), read from the
        // files' headers (cheap: no steps).
        // Refused: why it can't play exactly on this build and device (the same checks playback makes up front:
        // game logic version, bot vision, the arena present, the arena's files the same) -- null = it plays here.
        public static IReadOnlyList<ReplayInfo> List(string dataDir)
        {
            var list = new List<ReplayInfo>();
            foreach ((string path, bool pinned) in Recent(dataDir).Select(p => (p, false)).Concat(Pinned(dataDir).Select(p => (p, true))))
            {
                list.Add(Describe(path, pinned));
            }
            foreach (string path in Shared(dataDir))
            {
                list.Add(Describe(path, pinned: false) with { Shared = true });
            }
            return list.OrderByDescending(i => Path.GetFileName(i.Path), StringComparer.Ordinal).ToList();
        }

        public static ReplayInfo Describe(string path, bool pinned)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            DateTime? when = DateTime.TryParseExact(name.Length >= 15 ? name[..15] : "", "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime t) ? t : null;
            string title = when != null ? name[15..].Trim() : name;
            try
            {
                ReplayFile header = ReplayFile.LoadHeader(path, out int steps);
                string? refused = ReplayPlayer.CheckBeforeStart(header);
                if (refused == null && header.Data.Length > 0)
                {
                    refused = ReplayData.Compare(header.Data, ReplayData.Fingerprint(header.Settings.Arena, header.Settings.Mode));
                }
                return new ReplayInfo(path, pinned, when, title, header, steps, refused);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new ReplayInfo(path, pinned, when, title, null, 0, ex.Message);
            }
        }

        // a new title (the file's name after its time); returns the new path
        public static string Rename(string path, string title)
        {
            string dir = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string time = name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ? name[..15] + " " : "";
            string clean = Clean(title);
            if (clean.Length == 0)
            {
                return path;
            }
            string to = Path.Combine(dir, time + clean + Extension);
            for (int i = 2; File.Exists(to) && !string.Equals(to, path, StringComparison.Ordinal); i++)
            {
                to = Path.Combine(dir, $"{time}{clean} ({i}){Extension}");
            }
            if (!string.Equals(to, path, StringComparison.Ordinal))
            {
                File.Move(path, to);
            }
            return to;
        }

        // the REPLAYS page's RENAME: a named replay is one worth keeping, so it's also pinned (a FAVORITE, past the last
        // ten: owner 2026-10-10, renamed replays were going with the oldest recent ones); returns the new path
        public static string RenameAndKeep(string dataDir, string path, string title)
        {
            string renamed = Rename(path, title);
            return IsPinned(dataDir, renamed) ? renamed : Pin(dataDir, renamed);
        }

        public static void Delete(string path) => File.Delete(path);

        public static bool IsPinned(string dataDir, string path) =>
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(PinnedDir(dataDir)), StringComparison.Ordinal);

        private static IReadOnlyList<string> Files(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }
            // the name starts with the time, so name order is age order (a copied file keeps its name, not its date)
            return Directory.GetFiles(dir, "*" + Extension).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
        }

        private static string Clean(string arena)
        {
            var sb = new StringBuilder(arena.Length);
            foreach (char c in arena)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '_');
            }
            return sb.ToString().Trim();
        }
    }

    // One row of the REPLAYS list. Header: the file's settings and where it was recorded (Steps empty; null = the file
    // couldn't be read, Refused says why). Steps / 60 = the match's length in seconds.
    public sealed record ReplayInfo(string Path, bool Pinned, DateTime? Recorded, string Title, ReplayFile? Header, int Steps,
        string? Refused)
    {
        public bool PlaysHere => Refused == null;
        // from someone else (replays/shared/: a friend's shared replay, or a file opened with or shared to the app)
        public bool Shared { get; init; }
        public bool Complete => Header?.Complete == true;
        public int Seconds => Steps / 60;
        public MphRead.GameMode? Mode => Header?.Settings.Mode;
        // the arena's in-game name ("Combat Hall"), or the room name the file holds when this build doesn't know it
        public string Arena => Header == null ? "" : MphRecomp.Multiplayer.MatchArenas.Find(Header.Settings.Arena)?.InGameName ?? Header.Settings.Arena;
        public IReadOnlyList<MphRecomp.Multiplayer.MatchPlayer> Players =>
            (IReadOnlyList<MphRecomp.Multiplayer.MatchPlayer>?)Header?.Settings.Players ?? Array.Empty<MphRecomp.Multiplayer.MatchPlayer>();
        // this device's player in it (the one who recorded it, or a guest's own seat in the host's file: ReplayFile.ViewerSeat)
        public int LocalSeat => Header?.ViewerSeat ?? 0;
        // "" a local match, "LAN", "online" (format 19)
        public string Net => Header?.Net ?? "";
        // a seat's online identity as the match had it (format 17+; null = none known: a bot, an older file)
        public ReplaySeatIdentity? Identity(int seat) =>
            Header?.Identities.Where(i => i.Seat == seat).Select(i => (ReplaySeatIdentity?)i).FirstOrDefault();
        // how that player placed (0 = won), null = not known (incomplete, or recorded before format 3)
        public int? LocalPlace => Header?.Places.Where(p => p.Seat == LocalSeat).Select(p => (int?)p.Place).FirstOrDefault();
    }
}
