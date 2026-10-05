using System;
using System.Collections.Generic;
using MphRead;

namespace MphRecomp.World
{
    // A room's collision mesh as flat world-space triangles (9 floats per tri), shared by the Android
    // renderer (walk/floor raycasts) and the headless tools (MeshCollision soak tests) so both run on
    // exactly the same data. Moved out of RenderActivity unchanged.
    public static class RoomCollision
    {
        // The game's node/collision layer mask for a room: for multiplayer arenas the standard
        // battle layout (2 players); for single-player rooms the room's own authored layer. This
        // is what selects the open MP arena over the campaign walls (see Model.FilterNodes).
        public static int RoomLayerMask(RoomMetadata meta) => meta.Multiplayer
            ? SceneSetup.GetNodeLayer(GameMode.Battle, roomLayer: 0, playerCount: 2)
            : SceneSetup.GetNodeLayer(GameMode.SinglePlayer, meta.NodeLayer, playerCount: 1);

        // The game's ENTITY layer for the same configuration as RoomLayerMask (SceneSetup.SetUpRoom):
        // multiplayer = the Battle mode's 2-player layer; single-player = the boss-flags layer, which is
        // 0 (first visit) with no save. The game loads exactly one entity layer -- loading all of them
        // (layerId -1) stacks mode-specific duplicates, e.g. Sanctorus's Defender-only PlayerSpawns sit
        // 0.03-0.13 units from the Battle ones (measured with -animprobe).
        public static int EntityLayerId(RoomMetadata meta) => meta.Multiplayer
            ? Metadata.GetMultiplayerEntityLayer(GameMode.Battle, playerCount: 2)
            : 0;

        // Fan-triangulate the room's collision polygons into world-space triangles and report the
        // playable centre + radius. The collision points are already in the same world units as the
        // baked render geometry. Skyboxes have no collision, so this is exactly the reachable space.
        public static float[] LoadTriangles(string room, float[] centerOut, out float radius)
        {
            radius = 10f;
            RoomMetadata meta = Metadata.RoomMetadata[room];
            // match the rendered layer: skyboxes have no collision, but the campaign _s0x walls DO,
            // so an unfiltered (mask 0 -> SP layer) load would keep phantom walls the MP arena drops.
            var ci = MphRead.Formats.Collision.Collision.GetCollision(meta, roomLayerMask: RoomLayerMask(meta));
            var info = (MphRead.Formats.Collision.MphCollisionInfo)ci.Info;
            var tris = new List<float>(info.Data.Count * 9);
            float minX = 1e9f, minY = 1e9f, minZ = 1e9f, maxX = -1e9f, maxY = -1e9f, maxZ = -1e9f;
            void Acc(OpenTK.Mathematics.Vector3 p)
            {
                tris.Add(p.X); tris.Add(p.Y); tris.Add(p.Z);
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y; if (p.Z > maxZ) maxZ = p.Z;
            }
            foreach (var d in info.Data)
            {
                int n = d.PointIndexCount;
                if (n < 3) continue;
                var p0 = info.Points[info.PointIndices[d.PointStartIndex]];
                for (int j = 1; j < n - 1; j++)
                {
                    var pa = info.Points[info.PointIndices[d.PointStartIndex + j]];
                    var pb = info.Points[info.PointIndices[d.PointStartIndex + j + 1]];
                    Acc(p0); Acc(pa); Acc(pb);
                }
            }
            if (tris.Count == 0) { centerOut[0] = centerOut[1] = centerOut[2] = 0; return Array.Empty<float>(); }
            centerOut[0] = (minX + maxX) / 2f; centerOut[1] = (minY + maxY) / 2f; centerOut[2] = (minZ + maxZ) / 2f;
            float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            radius = 0.5f * (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); if (radius < 1f) radius = 1f;
            return tris.ToArray();
        }
    }
}
