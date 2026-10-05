using System.Collections.Generic;
using System.Linq;
using MphRead;

namespace MphRecomp.Multiplayer
{
    // What a mode's entity layer in an arena holds (the ROM's own entity data), which decides whether the mode can
    // be played there: every mode needs spawn points; octolith modes need their flags and bases; nodes/defender
    // need capture nodes.
    public readonly record struct ArenaCensus(int Spawns, int Flags, int Bases, int Nodes)
    {
        public bool Supports(GameMode mode) => Spawns >= 2 && mode switch
        {
            GameMode.Capture => Flags >= 2 && Bases >= 2,
            GameMode.Bounty or GameMode.BountyTeams => Flags >= 1 && Bases >= 1,
            GameMode.Nodes or GameMode.NodesTeams or GameMode.Defender or GameMode.DefenderTeams => Nodes >= 1,
            _ => true
        };
    }

    // The retail multiplayer arenas: MphRead's room table ids 93-118 (Data Shrine .. Oubliette, including both
    // Transfer Lock variants). The later multiplayer entries are unused leftovers (E3 / "early" rooms, First Hunt
    // levels) and aren't offered. Imported arenas (Echoes, Core/Arenas) follow, once registered as host rooms.
    public static class MatchArenas
    {
        public const int FirstId = 93;
        public const int LastId = 118;

        static readonly IReadOnlyList<RoomMetadata> _retail = Metadata.RoomList
            .Where(r => r.Multiplayer && !r.FirstHunt && r.Id >= FirstId && r.Id <= LastId)
            .OrderBy(r => r.Id)
            .ToList();

        public static IReadOnlyList<RoomMetadata> All
        {
            get
            {
                MphRecomp.Arenas.EchoesArena.EnsureRegistered();
                return Metadata.HostRooms.Count == 0 ? _retail : _retail.Concat(Metadata.HostRooms.Where(r => r.Multiplayer)).ToList();
            }
        }

        public static bool IsArena(string name) => All.Any(r => r.Name == name);

        public static RoomMetadata? Find(string nameOrInGameName)
        {
            return All.FirstOrDefault(r => r.Name == nameOrInGameName)
                ?? All.FirstOrDefault(r => string.Equals(r.InGameName, nameOrInGameName, System.StringComparison.OrdinalIgnoreCase))
                ?? All.FirstOrDefault(r => string.Equals(r.Name, nameOrInGameName, System.StringComparison.OrdinalIgnoreCase));
        }

        private static readonly Dictionary<(string, GameMode, int), ArenaCensus> _census = new();

        // Needs the ROM's files (Paths set up): reads the arena's entity file on the layer the mode loads for that
        // many players (Metadata.GetMultiplayerEntityLayer, as SceneSetup does).
        public static ArenaCensus Census(RoomMetadata arena, GameMode mode, int players = 4)
        {
            if (_census.TryGetValue((arena.Name, mode, players), out ArenaCensus cached))
            {
                return cached;
            }
            int layer = Metadata.GetMultiplayerEntityLayer(mode, players);
            int spawns = 0, flags = 0, bases = 0, nodes = 0;
            if (arena.EntityPath != null)
            {
                foreach (Entity entity in Read.GetEntities(arena.EntityPath, layer, firstHunt: false))
                {
                    switch (entity.Type)
                    {
                    case EntityType.PlayerSpawn: spawns++; break;
                    case EntityType.OctolithFlag: flags++; break;
                    case EntityType.FlagBase: bases++; break;
                    case EntityType.NodeDefense: nodes++; break;
                    }
                }
            }
            var census = new ArenaCensus(spawns, flags, bases, nodes);
            _census[(arena.Name, mode, players)] = census;
            return census;
        }

        public static bool Supports(RoomMetadata arena, GameMode mode, int players = 4) => Census(arena, mode, players).Supports(mode);
    }
}
