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
            return String.Join(';', parts);
        }

        // null = the same; else why the replay can't play exactly here
        public static string? Compare(string recorded, string here)
        {
            Dictionary<string, string> now = Parse(here);
            foreach ((string what, string value) in Parse(recorded))
            {
                if (!now.TryGetValue(what, out string? current) || current != value)
                {
                    string label = what switch
                    {
                        "entities" => "objects",
                        "nodes" => "bot paths",
                        _ => what
                    };
                    return current == "missing"
                        ? $"this arena's {label} file is missing on this device"
                        : $"this arena's {label} file on this device differs from the recording's (another import or game version)";
                }
            }
            return null;
        }

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

        private static string Part(string what, string path)
        {
            if (!File.Exists(path))
            {
                return what + "=missing";
            }
            byte[] bytes = File.ReadAllBytes(path);
            return $"{what}={ReplayFile.Crc32(bytes):x8}/{bytes.Length}";
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
