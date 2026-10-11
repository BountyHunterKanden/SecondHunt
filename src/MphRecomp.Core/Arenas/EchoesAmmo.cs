using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // UA pickups in Echoes arenas (owner 2026-10-10, release test note N5: "make ammo pickups for echoes maps"). Echoes'
    // own multiplayer items are health and Missile x5 only (plus Spider Complex's Quad Damage); its beams' ammo came
    // from the crates and kills, so none of the arenas' pickups were ammo (-echoesarena scripts). Here the weapons from
    // the crates and the Pirate Blaster use MPH's UA, so each arena gets UA big pickups at about vanilla's density
    // (docs/PICKUPS.md census: 4-8 UA big per vanilla arena, 10 s respawn): half its Missile x5 spots, 4 to 6, each
    // 1.6 units beside one (spread out, on the same floor, clear of walls and the other pickups; Pipeline's missiles
    // sit on tight ledges, so two go beside its health). Spots: -echoesarena ammospots, MPH units at the default scale.
    // They join the room's entities as it loads (SceneSetup.HostExtraEntities), first after the room's own, so their
    // ids are the same on every device whatever crates or breakables follow; LAN/online clients mirror the host's items
    // by those ids (Net/ItemSync). MphRead's own spawners: bots seek them like any vanilla UA.
    // Off unless a match turns them on: old replays (format < 24: Settings.EchoesAmmo false) play without them.
    public static class EchoesAmmo
    {
        public const ItemType Item = ItemType.UABig;
        public const int RespawnSeconds = 10;

        static readonly Dictionary<int, Vector3[]> Spots = new()
        {
            [1001] = new[]   // Sidehopper Station
            {
                new Vector3(-50.46f, 0.12f, 53.50f),
                new Vector3(50.07f, 0.12f, 53.41f),
                new Vector3(-0.09f, 0.09f, -23.38f),
                new Vector3(11.06f, 12.92f, -19.35f),
                new Vector3(-11.06f, 13.04f, -19.35f),
            },
            [1002] = new[]   // Spires
            {
                new Vector3(-7.76f, 2.10f, -104.34f),
                new Vector3(8.44f, 4.44f, 72.15f),
                new Vector3(12.43f, 2.08f, -86.27f),
                new Vector3(-5.89f, 4.44f, 50.68f),
            },
            [1003] = new[]   // Crossfire Chaos
            {
                new Vector3(-4.41f, 0.21f, 35.24f),
                new Vector3(-4.01f, 0.22f, -59.08f),
                new Vector3(6.74f, -1.29f, -6.65f),
                new Vector3(-5.77f, 0.32f, 16.20f),
            },
            [1004] = new[]   // Pipeline
            {
                new Vector3(33.59f, 2.18f, -28.10f),
                new Vector3(33.59f, 2.18f, 28.11f),
                new Vector3(-7.84f, -0.59f, -31.45f),
                new Vector3(-7.84f, -0.28f, 31.03f),
            },
            [1005] = new[]   // Spider Complex
            {
                new Vector3(29.29f, 5.10f, 13.81f),
                new Vector3(-20.57f, 5.09f, -6.23f),
                new Vector3(-22.55f, 6.53f, 21.02f),
                new Vector3(8.76f, 8.07f, 28.61f),
            },
            [1006] = new[]   // Shooting Gallery
            {
                new Vector3(19.40f, 8.30f, -11.01f),
                new Vector3(-19.30f, 8.30f, -11.00f),
                new Vector3(13.84f, 2.71f, -46.12f),
                new Vector3(0.05f, 8.30f, -25.01f),
            },
        };

        // this match has them (set by MatchStart before the arena loads)
        public static bool Enabled { get; set; }

        // on unless the settings turn them off
        public static bool For(MatchSettings settings) => settings.EchoesAmmo ?? true;

        public static IReadOnlyList<Vector3> SpotsFor(int roomId) => Spots.TryGetValue(roomId, out Vector3[]? s) ? s : System.Array.Empty<Vector3>();

        // SceneSetup.HostExtraEntities: an Echoes arena's UA spawners after its own entities, ids past theirs
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            if (!Enabled || !GameState.Multiplayer || !Spots.TryGetValue(roomId, out Vector3[]? spots)) return entities;
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            foreach (Vector3 p in spots)
            {
                ItemSpawnEntityData data = PickupPlacement.SpawnerData(id++, Item, p, hasBase: false, (ushort)(RespawnSeconds * 30));
                list.Add(new ItemSpawnEntity(data, ArenaModelWriter.RoomNode, scene));
            }
            scene.LoadModel(Metadata.Items[(int)Item]);
            return list;
        }
    }
}
