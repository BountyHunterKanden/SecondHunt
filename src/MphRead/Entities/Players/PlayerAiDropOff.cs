using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: bots stuck over a drop-down node (2026-10-20, seen in replays). Some ROM graph
    // links drop from a ledge to a node whose spot lies just under the ledge's footprint: MP7 PROCESSOR CORE 14 -> 36
    // (catwalk y 13.3, node 36 at y 2.2 ~0.35 inside the catwalk's edge), MP4 HIGHGROUND 55/56 -> 22 (y 4.5 floor, node 22
    // at y 0.1 ~0.3 inside the pit's edge). Steering aims at the node's XZ, which the bot already stands on, so it circles /
    // shakes in place; the node is never reached (IsNodeInRange is 3D), Field118 times out after ~5 s, the AI re-picks the
    // ledge node and routes back down the same link, looping until the bot dies (Weavel held node 36 for 17.8 s). The link's
    // own direction doesn't find the hole (High Ground's runs along the pit's side), so when the bot is grounded well above
    // its next node and near it, look around the node once for open floor down at the node's height and steer past it.
    // Host-guarded (replays before format 57 play without it); Echoes arenas use PlayerAiUnstick.cs instead.
    public partial class PlayerEntity
    {
        public static bool HostBotDropOff { get; set; }
        public static bool HostBotSwitchFirst { get; set; } // recomp: PlayerAiData.Func214380C switches before its charge check

        public partial class PlayerAiData
        {
            private const float DropOffHeight = 2;
            private const float DropOffRadius = 2.5f;
            private const float DropOffStep = 1; // past the open spot, so the body clears the edge

            // 8 directions, exact constants (same on every device)
            private static readonly Vector2[] _dropDirs =
            {
                new Vector2(1, 0), new Vector2(0.70710677f, 0.70710677f), new Vector2(0, 1), new Vector2(-0.70710677f, 0.70710677f),
                new Vector2(-1, 0), new Vector2(-0.70710677f, -0.70710677f), new Vector2(0, -1), new Vector2(0.70710677f, -0.70710677f)
            };
            private static readonly float[] _dropRadii = { 0.8f, 1.4f };

            private NodeData3? _dropFor = null;
            private Vector3? _dropTarget = null;

            private Vector3 DropOffTarget(Vector3 position)
            {
                if (!HostBotDropOff || HostBotUnstick || _node40 == null || !_player.Flags1.TestFlag(PlayerFlags1.Grounded)
                    || _player.Position.Y - position.Y < DropOffHeight
                    || (position - _player.Position).Xz.LengthSquared > DropOffRadius * DropOffRadius)
                {
                    return position;
                }
                if (_dropFor != _node40)
                {
                    _dropFor = _node40;
                    _dropTarget = FindDropSpot(position);
                }
                return _dropTarget ?? position;
            }

            // the open spot nearest the bot, around the node, whose floor is down at the node's height
            private Vector3? FindDropSpot(Vector3 node)
            {
                Vector3? best = null;
                float bestDist = System.Single.MaxValue;
                var result = new CollisionResult();
                foreach (float radius in _dropRadii)
                {
                    foreach (Vector2 dir in _dropDirs)
                    {
                        var spot = new Vector3(node.X + dir.X * radius, 0, node.Z + dir.Y * radius);
                        Vector3 from = spot.WithY(_player.Position.Y + 0.5f);
                        Vector3 to = spot.WithY(node.Y - 1);
                        if (!CollisionDetection.CheckBetweenPoints(from, to, TestFlags.Players, _scene, ref result)
                            || result.Position.Y > node.Y + 1)
                        {
                            continue;
                        }
                        Vector2 away = spot.Xz - _player.Position.Xz;
                        float dist = away.LengthSquared;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            // on past it, away from the bot (stepping off the node's side can lead back onto the ledge)
                            away = dist > 0.0001f ? away / System.MathF.Sqrt(dist) : dir;
                            best = new Vector3(spot.X + away.X * DropOffStep, node.Y, spot.Z + away.Y * DropOffStep);
                        }
                    }
                    if (best != null)
                    {
                        break; // (the near ring first: the far one may be past a wall)
                    }
                }
                return best;
            }
        }
    }
}
