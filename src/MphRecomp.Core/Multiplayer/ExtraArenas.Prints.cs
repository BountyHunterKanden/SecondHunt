using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MphRead;
using MphRecomp.Arenas;
using MphRecomp.Replay;

namespace MphRecomp.Multiplayer
{
    // Each arena's play data as one number (2026-10-19: crates without pickups, pickups different on different screens
    // online): the baked pickups for it (Arenas/Placements, inside the app: another app version can have others),
    // and for an extra arena its entities file (spawns and item spawners) and what its import's arena.json says about play
    // (bounds, kill height, scale, the crates, their weapons, the breakables and fluids; rounded, so an import made on another
    // device that differs only in the last bits of a float, or in its notes and lighting, counts the same). Two devices with different numbers don't play the same arena (items
    // and crates are matched by their place in the lists), so the host refuses a joiner whose copy differs, and START waits
    // as for an arena it lacks (Lacking). The JOIN carries them after the extra arenas, optional too: an older host ignores
    // them and an older joiner's JOIN has none (not checked, as before).
    public static partial class ExtraArenas
    {
        private static readonly Dictionary<int, (string Key, uint Print)> s_prints = new();
        private static Dictionary<int, uint>? s_placed;

        // the arena's number, or null when this device doesn't have it
        public static uint? Print(string arena)
        {
            RoomMetadata? meta = MatchArenas.All.FirstOrDefault(r => r.Name == arena);
            return meta != null ? Print(meta) : null;
        }

        private static uint Print(RoomMetadata meta)
        {
            bool extra = IsExtra(meta.Name);
            string collision = Paths.Combine(meta.FirstHunt || meta.Hybrid ? Paths.FhFileSystem : Paths.FileSystem, meta.CollisionPath);
            string? json = extra && Path.GetDirectoryName(collision) is string dir ? Path.Combine(dir, "arena.json") : null;
            string? entities = extra && meta.EntityPath != null
                ? Paths.Combine(meta.FirstHunt ? Paths.FhFileSystem : Paths.FileSystem, meta.EntityPath) : null;
            string key = extra ? $"{entities}|{Stamp(entities)}|{json}|{Stamp(json)}" : "";
            lock (s_prints)
            {
                if (s_prints.TryGetValue(meta.Id, out var kept) && kept.Key == key) return kept.Print;
            }
            var text = new StringBuilder();
            text.Append("placed=").Append(Placed().TryGetValue(meta.Id, out uint placed) ? placed.ToString("x8") : "none");
            if (extra)
            {
                text.Append(";entities=").Append(entities != null && File.Exists(entities)
                    ? ReplayFile.Crc32(File.ReadAllBytes(entities)).ToString("x8") : "none");
                text.Append(";arena=").Append(json != null && File.Exists(json) ? ArenaPlay(json) : "none");
            }
            uint print = ReplayFile.Crc32(Encoding.UTF8.GetBytes(text.ToString()));
            lock (s_prints) s_prints[meta.Id] = (key, print);
            return print;
        }

        // arena.json's play part, as text (floats to 1/100 of a unit; an unreadable file reads as itself)
        private static string ArenaPlay(string json)
        {
            byte[] bytes = File.ReadAllBytes(json);
            EchoesArena.Info? i;
            try
            {
                i = JsonSerializer.Deserialize<EchoesArena.Info>(bytes);
            }
            catch (JsonException)
            {
                i = null;
            }
            if (i == null) return ReplayFile.Crc32(bytes).ToString("x8");
            static string F(float v) => MathF.Round(v * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            static string V(float[]? v) => v == null ? "-" : string.Join(",", v.Select(F));
            var t = new StringBuilder();
            t.Append(F(i.Scale)).Append('|').Append(F(i.KillHeight)).Append('|').Append(V(i.Min)).Append('|').Append(V(i.Max));
            t.Append("|spawns ").Append(i.Spawns).Append(" items ").Append(i.Items);
            foreach (EchoesArena.CrateSpot c in i.Crates) t.Append("|crate ").Append(V(c.Pos)).Append('/').Append(V(c.Floor));
            t.Append("|weapons ").Append(string.Join(",", i.CrateWeapons));
            foreach (EchoesArena.BreakSpot b in i.Breakables ?? new List<EchoesArena.BreakSpot>())
            {
                t.Append("|break ").Append(b.Kind).Append(' ').Append(b.Name).Append(' ').Append(V(b.Min)).Append('/').Append(V(b.Max))
                    .Append(' ').Append(V(b.Size)).Append(' ').Append(b.Yaw is float y ? F(y) : "-").Append(' ').Append(b.Health)
                    .Append(' ').Append(b.RespawnFrames);
            }
            foreach (EchoesArena.FluidSpot f in i.Fluids ?? new List<EchoesArena.FluidSpot>())
            {
                t.Append("|fluid ").Append(f.Kind).Append(' ').Append(V(f.Min)).Append('/').Append(V(f.Max)).Append(' ').Append(F(f.Surface));
            }
            t.Append("|turret ").Append(i.Turret != null);
            return ReplayFile.Crc32(Encoding.UTF8.GetBytes(t.ToString())).ToString("x8");
        }

        private static long Stamp(string? path) => path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;

        // the shipped placement files' bytes by room (PlacedPickups.Files reads the same resources)
        private static Dictionary<int, uint> Placed()
        {
            if (s_placed != null) return s_placed;
            var placed = new Dictionary<int, uint>();
            Assembly asm = typeof(PlacedPickups).Assembly;
            foreach (string res in asm.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                string n = res.Replace('\\', '/');
                if (!n.StartsWith("placements/", StringComparison.Ordinal) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                using Stream st = asm.GetManifestResourceStream(res)!;
                var bytes = new MemoryStream();
                st.CopyTo(bytes);
                try
                {
                    PickupPlacement.FileData? data = JsonSerializer.Deserialize<PickupPlacement.FileData>(bytes.ToArray());
                    if (data != null) placed[data.RoomId] = ReplayFile.Crc32(bytes.ToArray());
                }
                catch (JsonException)
                {
                }
            }
            return s_placed = placed;
        }

        // the JOIN's field after the extra arenas: every arena this device has, by room id, with its number
        public static void WritePrints(BinaryWriter w)
        {
            List<RoomMetadata> rooms;
            try
            {
                rooms = MatchArenas.All.Where(r => !FirstHuntArena.IsDevRoom(r.Name)).Take(255).ToList();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                rooms = new List<RoomMetadata>();
            }
            var prints = new List<(int Id, uint Print)>();
            foreach (RoomMetadata r in rooms)
            {
                try
                {
                    prints.Add((r.Id, Print(r)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            w.Write((byte)prints.Count);
            foreach ((int id, uint print) in prints)
            {
                w.Write((short)id);
                w.Write(print);
            }
        }

        // null = a JOIN without them (an older device)
        public static Dictionary<int, uint>? ReadPrints(BinaryReader r)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return null;
            int n = r.ReadByte();
            var prints = new Dictionary<int, uint>();
            for (int i = 0; i < n; i++) prints[r.ReadInt16()] = r.ReadUInt32();
            return prints;
        }

        // a joiner with these numbers has another copy of this arena than this device (null: not known, not checked)
        public static bool PrintDiffers(IReadOnlyDictionary<int, uint>? prints, string arena)
        {
            if (prints == null) return false;
            RoomMetadata? meta = MatchArenas.All.FirstOrDefault(r => r.Name == arena);
            return meta != null && prints.TryGetValue(meta.Id, out uint theirs) && theirs != Print(meta);
        }

        // for the log: both numbers
        public static string PrintNote(IReadOnlyDictionary<int, uint>? prints, string arena)
        {
            RoomMetadata? meta = MatchArenas.All.FirstOrDefault(r => r.Name == arena);
            uint theirs = 0;
            bool known = meta != null && prints != null && prints.TryGetValue(meta.Id, out theirs);
            return $"{arena}: this host's copy {(meta != null ? Print(meta).ToString("x8") : "none")}, the joiner's {(known ? theirs.ToString("x8") : "none")}";
        }

        public static string? PrintRefusal(IReadOnlyDictionary<int, uint>? prints, string arena)
            => PrintDiffers(prints, arena)
            ? $"this game's {Label(arena)} is another copy than yours (another app version or import): update the game, or import it again on import games"
            : null;
    }
}
