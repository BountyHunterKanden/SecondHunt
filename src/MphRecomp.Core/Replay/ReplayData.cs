using System;
using System.Collections.Generic;
using System.IO;
using MphRead;

namespace MphRecomp.Replay
{
    // What a match was played on (docs/REPLAY_MODE.md, "Exact or refused"): the arena's files that shape play -- its
    // entities, its collision and the bots' node data, picked the way SceneSetup, Collision and NodeData read them -- as
    // "entities=<crc32>/<length>;collision=...;nodes=..." (or "=missing"). A device whose files differ (an Echoes arena
    // imported differently, another game extraction) can't play the replay exactly, so playback refuses it before the
    // first step, naming the file, instead of failing part-way. Models, textures and sounds don't count: they only change
    // what's drawn or heard. (MPH's arenas: the same 55 files in rev 0 and rev 1, checked 2026-10-07.)
    public static class ReplayData
    {
        public static string Fingerprint(string arena, GameMode mode)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(arena);
            if (meta == null)
            {
                return "";
            }
            var parts = new List<string>();
            if (meta.EntityPath != null)
            {
                parts.Add(Part("entities", Paths.Combine(meta.FirstHunt ? Paths.FhFileSystem : Paths.FileSystem, meta.EntityPath)));
            }
            parts.Add(Part("collision", Paths.Combine(meta.FirstHunt || meta.Hybrid ? Paths.FhFileSystem : Paths.FileSystem,
                meta.CollisionPath)));
            if (NodePath(meta, mode) is string nodePath)
            {
                parts.Add(Part("nodes", Paths.Combine(Paths.FileSystem, nodePath)));
            }
            // the owner's placed spawns, pads and pickups (Arenas/Placements, shipped in the library) change the match
            // as the room loads: another file is another match (Mining Plaza 2026-11-01: a replay stopped at 0:00)
            if (MphRecomp.Arenas.PlacedPickups.FileFingerprint(meta.Id) is string placed)
            {
                parts.Add("placed=" + placed);
            }
            return String.Join(';', parts);
        }

        // null = the same; else why the replay can't play exactly here
        public static string? Compare(string recorded, string here)
        {
            Dictionary<string, string> now = Parse(here);
            foreach ((string what, string value) in Parse(recorded))
            {
                if ((!now.TryGetValue(what, out string? current) || current != value) && KeptFile(what, value) == null)
                {
                    string label = what switch
                    {
                        "entities" => "objects",
                        "nodes" => "bot paths",
                        "placed" => "placed spawns and pickups",
                        _ => what
                    };
                    return current == "missing"
                        ? $"this arena's {label} file is missing on this device"
                        : $"this arena's {label} file on this device differs from the recording's (another import or game version)";
                }
            }
            return null;
        }

        // Kept copies (owner 2026-11-01: two pinned replays stopped playing after Mining Plaza was re-imported): each match
        // recorded keeps the collision and node files it ran on under <data>/replaydata as <what>_<crc>_<length>.bin
        // (once per distinct file, ~1 MB an arena), and a replay whose arena files changed since plays on those copies
        // (Use, around its scene's setup). Objects (entities) aren't kept: their reads go through the repack hook.
        // next to the replays (<data>/replays/data, set by ReplayLibrary's List and Save: on Android the external data
        // folder, reachable over adb), else beside the game files; copies are looked up in both
        public static string? ReplaysRoot;

        public static string KeepDir => ReplaysRoot != null ? Path.Combine(ReplaysRoot, "replays", "data") : InternalKeepDir;

        private static string InternalKeepDir => Path.Combine(Path.GetDirectoryName(Paths.FileSystem.TrimEnd('/', '\\'))
            ?? Paths.FileSystem, "replaydata");

        private static readonly string[] s_kept = { "collision", "nodes" };

        private static string? KeptFile(string what, string value)
        {
            if (Array.IndexOf(s_kept, what) < 0 || value == "missing")
            {
                return null;
            }
            string name = $"{what}_{value.Replace('/', '_')}.bin";
            foreach (string dir in new[] { KeepDir, InternalKeepDir })
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file))
                {
                    return file;
                }
            }
            return null;
        }

        // (ReplayRecorder.Started) copy this match's collision and node files in, if not kept already
        public static void Keep(string arena, GameMode mode, string print)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(arena);
            if (meta == null)
            {
                return;
            }
            Dictionary<string, string> parts = Parse(print);
            try
            {
                foreach ((string what, string? rel) in RelPaths(meta, mode))
                {
                    if (rel == null || !parts.TryGetValue(what, out string? value) || value == "missing" || KeptFile(what, value) != null)
                    {
                        continue;
                    }
                    Directory.CreateDirectory(KeepDir);
                    string to = Path.Combine(KeepDir, $"{what}_{value.Replace('/', '_')}.bin");
                    File.Copy(Source(meta, what, rel), to + ".tmp", overwrite: true);
                    File.Move(to + ".tmp", to, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // ReplayPlayer.Start, around the scene's setup: the files that changed since the recording read from the kept
        // copies (Paths.Redirects); disposing clears them
        public static IDisposable Use(string recorded, string arena, GameMode mode)
        {
            Paths.Redirects.Clear();
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(arena);
            if (meta != null && recorded.Length > 0)
            {
                Dictionary<string, string> was = Parse(recorded), now = Parse(Fingerprint(arena, mode));
                foreach ((string what, string? rel) in RelPaths(meta, mode))
                {
                    if (rel != null && was.TryGetValue(what, out string? value) && (!now.TryGetValue(what, out string? cur) || cur != value)
                        && KeptFile(what, value) is string kept)
                    {
                        Paths.Redirects[Paths.RedirectKey(rel)] = kept;
                    }
                }
            }
            return new Clear();
        }

        private sealed class Clear : IDisposable
        {
            public void Dispose() => Paths.Redirects.Clear();
        }

        private static IEnumerable<(string What, string? Rel)> RelPaths(RoomMetadata meta, GameMode mode)
        {
            yield return ("collision", meta.CollisionPath);
            yield return ("nodes", NodePath(meta, mode));
        }

        private static string Source(RoomMetadata meta, string what, string rel) => what == "collision"
            ? Paths.Combine(meta.FirstHunt || meta.Hybrid ? Paths.FhFileSystem : Paths.FileSystem, rel)
            : Paths.Combine(Paths.FileSystem, rel);

        // the node file a match in this mode loads (SceneSetup.LoadNodeData's overrides for multiplayer modes)
        private static string? NodePath(RoomMetadata meta, GameMode mode)
        {
            if (mode == GameMode.Capture && Metadata.CtfNodeDataOverrides.TryGetValue(meta.Id, out string? ctf))
            {
                return ctf;
            }
            if ((mode == GameMode.Nodes || mode == GameMode.NodesTeams || mode == GameMode.Defender
                || mode == GameMode.DefenderTeams) && meta.Id == 107)
            {
                return @"levels\nodeData\mp14_KOTH_node.bin";
            }
            return meta.NodePath;
        }

        // each replay in the list was fingerprinted by reading and CRC-ing its arena's files on every page entry (~0.1 s on
        // the Odin, a hitch under the button's sound: garbled replay-menu sounds, 2026-10-26). A file's result is kept
        // while its write time and length stay, so an import that changes a file is still seen.
        private static readonly Dictionary<string, (long Ticks, long Length, string Print)> s_parts = new(StringComparer.Ordinal);

        private static string Part(string what, string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return what + "=missing";
            }
            long ticks = info.LastWriteTimeUtc.Ticks, length = info.Length;
            lock (s_parts)
            {
                if (s_parts.TryGetValue(path, out var known) && known.Ticks == ticks && known.Length == length)
                {
                    return what + known.Print;
                }
            }
            byte[] bytes = File.ReadAllBytes(path);
            string print = $"={ReplayFile.Crc32(bytes):x8}/{bytes.Length}";
            lock (s_parts) s_parts[path] = (ticks, length, print);
            return what + print;
        }

        private static Dictionary<string, string> Parse(string print)
        {
            var parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in print.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    parts[part[..eq]] = part[(eq + 1)..];
                }
            }
            return parts;
        }
    }
}
