using System;
using System.Collections.Generic;
using System.Numerics;
using MphRead;                 // runtime data layer: Read, Metadata, Entity types
using MphRead.Formats.Collision;
using MphRecomp.Game;
using MphRecomp.Sim;

// Adapter that bridges real MPH room data into the mph-recomp sim: reads a room's entity list
// (via MphRead's GL-free parser) and populates a SimWorld with dynamic sim entities the renderer
// draws from the snapshot. This is the dynamic counterpart to the renderer's current static entity
// baking -- the same authored positions, but as live entities that can spin, move, and be collected.
namespace MphRecomp.World
{
    // An item pickup that idles with a slow spin about Y (as in MPH) until collected. Collection
    // logic lands with the gameplay layer; for now it's a spinning, placed, drawable entity.
    public sealed class PickupEntity : SimEntity
    {
        public int ItemType;
        public float SpinSpeed = 2.0f; // rad/sec
        private float _angle;

        public override void Tick(float dt, in InputState input, SimWorld world)
        {
            _angle += SpinSpeed * dt;
            Facing = new Vector3(MathF.Sin(_angle), 0f, -MathF.Cos(_angle));
            Up = Vector3.UnitY;
        }
    }

    public static class RoomLoader
    {
        // Populate `world` with the room's item pickups as spinning sim entities at their authored
        // positions. Returns the count placed. (Props/doors/etc. follow as the sim gains behaviors;
        // the renderer already knows how to draw any of these model names.) Requires the game files
        // to be set up (Directory.SetCurrentDirectory(files) + Paths.UpdatePaths/ChooseMphPath).
        public static int PopulatePickups(SimWorld world, string roomName)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(roomName);
            if (meta?.EntityPath == null) return 0;
            IReadOnlyList<Entity> entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt, allowHook: true);
            int count = 0;
            foreach (Entity e in entities)
            {
                if (e.Type != EntityType.ItemSpawn) continue;
                int it = (int)((Entity<ItemSpawnEntityData>)e).Data.ItemType;
                if (it < 0 || it >= Metadata.Items.Count) continue;
                world.Spawn(new PickupEntity
                {
                    ItemType = it,
                    ModelName = Metadata.Items[it],
                    Position = new Vector3(e.Position.X, e.Position.Y, e.Position.Z)
                });
                count++;
            }
            return count;
        }

        // Load a room's collision mesh as a MeshCollision the sim player can walk on. The collision
        // points are already in the same world units as entity/render geometry (skyboxes have none).
        // Requires the game files set up. Returns null if the room has no collision.
        public static MeshCollision? LoadCollision(string roomName)
        {
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(roomName);
            if (meta == null) return null;
            var ci = Collision.GetCollision(meta, roomLayerMask: 0);
            if (ci.Info is not MphCollisionInfo info) return null;
            var tris = new List<float>(info.Data.Count * 9);
            foreach (var d in info.Data)
            {
                int n = d.PointIndexCount;
                if (n < 3) continue;
                var p0 = info.Points[info.PointIndices[d.PointStartIndex]];
                for (int j = 1; j < n - 1; j++) // fan-triangulate the n-gon
                {
                    var pa = info.Points[info.PointIndices[d.PointStartIndex + j]];
                    var pb = info.Points[info.PointIndices[d.PointStartIndex + j + 1]];
                    tris.Add(p0.X); tris.Add(p0.Y); tris.Add(p0.Z);
                    tris.Add(pa.X); tris.Add(pa.Y); tris.Add(pa.Z);
                    tris.Add(pb.X); tris.Add(pb.Y); tris.Add(pb.Z);
                }
            }
            return new MeshCollision(tris.ToArray());
        }
    }
}
