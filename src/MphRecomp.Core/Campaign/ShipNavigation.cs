using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Campaign
{
    public enum Planet
    {
        Alinos,
        CelestialArchives,
        VesperDefenseOutpost,
        Arcterra,
        Oubliette
    }

    // One destination on the gunship's star map. Area ids are MphRead's: 0/1 Alinos, 2/3 Celestial Archives,
    // 4/5 VDO, 6/7 Arcterra, 8 Oubliette. StorySave.Areas holds one unlock bit per area; the game sets both halves
    // of a planet together (EnemySpawnEntity: Cretaphid -> Alinos, Slench -> VDO + Arcterra; eight Octoliths ->
    // Oubliette), and MphRead's save menu reads a planet as unlocked from its first half's bit.
    public sealed class PlanetInfo
    {
        public Planet Id { get; }
        public string Name { get; }
        public string LandingRoom { get; }
        public int LandingRoomId { get; }
        public int FirstArea { get; }
        public int AreaCount { get; }

        public PlanetInfo(Planet id, string name, string landingRoom, int landingRoomId, int firstArea, int areaCount)
        {
            Id = id;
            Name = name;
            LandingRoom = landingRoom;
            LandingRoomId = landingRoomId;
            FirstArea = firstArea;
            AreaCount = areaCount;
        }
    }

    public sealed class PlanetStatus
    {
        public PlanetInfo Planet { get; init; } = null!;
        public bool Unlocked { get; init; }
        public bool Visited { get; init; }
        public int Octoliths { get; init; }
        public int OctolithTotal { get; init; }
        public int Artifacts { get; init; }
        public int ArtifactTotal { get; init; }
        // one per half (Oubliette: one): None, Escape (boss down, escape running) or Clear
        public AreaState[] Areas { get; init; } = Array.Empty<AreaState>();
    }

    // Why a hosted adventure stopped (CampaignHost.Ended). MphRead ends its session through Scene.QuitGame in three
    // ways: the ship hatch's YES (straight away or after the take-off movie), an ending movie (Gorea 1's or
    // Gorea 2's, both AfterMovie.EndGame), and NO at game over. The first two both pass enteringShip: true (health
    // and ammo refilled, Menu.NeededSave = Menu.SaveFromShip); only the ship hatch is in a landing room and only the
    // endings are in Gorea's rooms.
    public enum CampaignEnd
    {
        None,
        EnteredShip,
        Ending,
        Quit
    }

    public static class ShipNavigation
    {
        public const int GoreaFirstRoomId = 91; // Gorea_b1
        public const int GoreaSecondRoomId = 92; // Gorea_b2

        private static readonly PlanetInfo[] _planets =
        {
            new PlanetInfo(Planet.Alinos, "ALINOS", "UNIT1_LAND", 27, firstArea: 0, areaCount: 2),
            new PlanetInfo(Planet.CelestialArchives, "CELESTIAL ARCHIVES", "UNIT2_LAND", 45, firstArea: 2, areaCount: 2),
            new PlanetInfo(Planet.VesperDefenseOutpost, "VESPER DEFENSE OUTPOST", "UNIT3_LAND", 65, firstArea: 4, areaCount: 2),
            new PlanetInfo(Planet.Arcterra, "ARCTERRA", "UNIT4_LAND", 77, firstArea: 6, areaCount: 2),
            new PlanetInfo(Planet.Oubliette, "OUBLIETTE", "Gorea_Land", 89, firstArea: 8, areaCount: 1)
        };

        public static IReadOnlyList<PlanetInfo> Planets => _planets;

        public static PlanetInfo Get(Planet planet) => _planets[(int)planet];

        // the in-ship cockpit view's camseqs (overlay9_0's table, rev 1 0x21456E0): parked on the pad (loop), coming
        // down onto it (a revisit's landing) and rising off it (LAUNCH SHIP)
        public static (int Loop, int Land, int Takeoff) CockpitSeqs(Planet planet) => planet switch
        {
            Planet.Alinos => (102, 114, 115),
            Planet.CelestialArchives => (103, 116, 117),
            Planet.VesperDefenseOutpost => (104, 118, 119),
            Planet.Arcterra => (105, 120, 121),
            _ => (168, 169, 170)
        };

        // the cockpit's music, SEQ_SHIP_LAND1-4 by planet (Oubliette has LAND4)
        public static SeqId CockpitMusic(Planet planet) => planet switch
        {
            Planet.Alinos => SeqId.SHIP_LAND1,
            Planet.CelestialArchives => SeqId.SHIP_LAND2,
            Planet.VesperDefenseOutpost => SeqId.SHIP_LAND3,
            _ => SeqId.SHIP_LAND4
        };

        public static MphRead.Movie LandingMovie(Planet planet) => planet switch
        {
            Planet.Alinos => MphRead.Movie.AlinosLanding,
            Planet.CelestialArchives => MphRead.Movie.CALanding,
            Planet.VesperDefenseOutpost => MphRead.Movie.VDOLanding,
            Planet.Arcterra => MphRead.Movie.ArcterraLanding,
            _ => MphRead.Movie.OublietteLanding
        };

        // Oubliette has none: straight to the planet select
        public static MphRead.Movie TakeoffMovie(Planet planet) => planet switch
        {
            Planet.Alinos => MphRead.Movie.AlinosTakeoff,
            Planet.CelestialArchives => MphRead.Movie.CATakeoff,
            Planet.VesperDefenseOutpost => MphRead.Movie.VDOTakeoff,
            Planet.Arcterra => MphRead.Movie.ArcterraTakeoff,
            _ => MphRead.Movie.None
        };

        public static PlanetInfo? ByLandingRoom(int roomId)
        {
            foreach (PlanetInfo planet in _planets)
            {
                if (planet.LandingRoomId == roomId)
                {
                    return planet;
                }
            }
            return null;
        }

        public static PlanetInfo? ByArea(int areaId)
        {
            foreach (PlanetInfo planet in _planets)
            {
                if (areaId >= planet.FirstArea && areaId < planet.FirstArea + planet.AreaCount)
                {
                    return planet;
                }
            }
            return null;
        }

        public static PlanetInfo? ByRoomName(string roomName)
        {
            foreach (PlanetInfo planet in _planets)
            {
                if (String.Equals(planet.LandingRoom, roomName, StringComparison.OrdinalIgnoreCase))
                {
                    return planet;
                }
            }
            return null;
        }

        public static bool IsUnlocked(StorySave save, Planet planet)
        {
            return (save.Areas & (1 << Get(planet).FirstArea)) != 0;
        }

        public static PlanetStatus Status(StorySave save, Planet planet)
        {
            PlanetInfo info = Get(planet);
            int octoliths = 0;
            int artifacts = 0;
            var areas = new AreaState[info.AreaCount];
            for (int i = 0; i < info.AreaCount; i++)
            {
                int area = info.FirstArea + i;
                areas[i] = GameState.GetAreaState(area, save);
                if (area < 8)
                {
                    if (save.CheckFoundOctolith(area))
                    {
                        octoliths++;
                    }
                    artifacts += save.CountFoundArtifacts(area);
                }
            }
            bool hasItems = info.FirstArea < 8;
            return new PlanetStatus
            {
                Planet = info,
                Unlocked = IsUnlocked(save, planet),
                Visited = save.CheckVisitedRoom(info.LandingRoomId),
                Octoliths = octoliths,
                OctolithTotal = hasItems ? info.AreaCount : 0,
                Artifacts = artifacts,
                ArtifactTotal = hasItems ? 3 * info.AreaCount : 0,
                Areas = areas
            };
        }

        public static IReadOnlyList<PlanetStatus> StarMap(StorySave save)
        {
            var list = new List<PlanetStatus>(_planets.Length);
            foreach (PlanetInfo planet in _planets)
            {
                list.Add(Status(save, planet.Id));
            }
            return list;
        }

        // Call once CampaignHost.Ended is set. Reads and clears Menu.NeededSave (MphRead's desktop menu consumes
        // it the same way before offering its own save prompt).
        public static CampaignEnd Classify(CampaignHost host)
        {
            if (!host.Ended)
            {
                return CampaignEnd.None;
            }
            bool viaShipPath = Menu.NeededSave == Menu.SaveFromShip && Menu.SaveFromShip != Menu.SaveFromExit;
            Menu.NeededSave = SaveWhen.Never;
            if (!viaShipPath)
            {
                return CampaignEnd.Quit;
            }
            int roomId = host.RoomId;
            if (roomId == GoreaFirstRoomId || roomId == GoreaSecondRoomId)
            {
                return CampaignEnd.Ending;
            }
            return ByLandingRoom(roomId) != null ? CampaignEnd.EnteredShip : CampaignEnd.Quit;
        }

        // What MphRead's GameState.CommitSave does to the story save before writing it: the Omega Cannon doesn't
        // survive a save, and Gorea's two arenas always start fresh.
        public static void PrepareForSave(StorySave save)
        {
            save.Weapons &= 0xFF;
            if (save.WeaponSlots[2] == (int)BeamType.OmegaCannon)
            {
                save.WeaponSlots[2] = (int)BeamType.None;
            }
            for (int r = GoreaFirstRoomId; r <= GoreaSecondRoomId; r++)
            {
                Array.Clear(save.RoomState[r - 27]);
            }
        }
    }
}
