using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: side-view mode (owner 2026-11-12, docs/SIDE_VIEW_MODE.md; P0 = Data Shrine on the PC). A stage on the plane
    // x = 0, in (z, y): solid boxes, soft floors (jump up through, land on top, Down drops through), the KO lines, the
    // spawn points and the box the camera keeps to. The game's own room collision isn't used: the side view walks this
    // stage only (PlayerSideView.cs). The numbers for each room live with the host (MphRecomp.Core SideView/*Stage.cs).
    public sealed class SideViewStage
    {
        public readonly struct Box
        {
            public readonly float ZMin;
            public readonly float ZMax;
            public readonly float YMin;
            public readonly float YMax;

            public Box(float zMin, float zMax, float yMin, float yMax)
            {
                ZMin = zMin;
                ZMax = zMax;
                YMin = yMin;
                YMax = yMax;
            }
        }

        public string Name { get; init; } = "";
        public Box[] Solids { get; init; } = Array.Empty<Box>();
        // each a polyline of (z, y) points, z rising; the floor's height between two points is a straight line
        public Vector2[][] SoftFloors { get; init; } = Array.Empty<Vector2[]>();
        // (the side lines can move: the host's KO gap past the camera's box, tuned live)
        public float KoLeft { get; set; } = -30;
        public float KoRight { get; set; } = 30;
        public float KoTop { get; init; } = 30;
        public float KoBottom { get; init; } = -10;
        // floor points (z, y): a player stands there, feet on y
        public Vector2[] Spawns { get; init; } = Array.Empty<Vector2>();
        public float CamZMin { get; init; } = -30;
        public float CamZMax { get; init; } = 30;
        public float CamYMin { get; init; } = -4;
        public float CamYMax { get; init; } = 30;

        // the soft floor's height under z on one polyline, or NaN past its ends
        public static float FloorY(Vector2[] line, float z)
        {
            for (int i = 1; i < line.Length; i++)
            {
                Vector2 a = line[i - 1];
                Vector2 b = line[i];
                if (z >= a.X && z <= b.X)
                {
                    float span = b.X - a.X;
                    return span <= 0 ? MathF.Max(a.Y, b.Y) : a.Y + (b.Y - a.Y) * (z - a.X) / span;
                }
            }
            return Single.NaN;
        }

        public bool PastKo(Vector3 position)
        {
            return position.Z < KoLeft || position.Z > KoRight || position.Y > KoTop || position.Y < KoBottom;
        }

        public struct Contact
        {
            public bool Standing;
            public bool Soft; // standing on a soft floor (Down drops through it)
            public bool Bonk; // head hit a solid's underside
            public bool Lateral; // pushed out of a solid's side
        }

        // moves a body (feet at y + minY, head at y + maxY, half width halfZ along z) from prev to pos against the stage.
        // Solids resolve by where the body was: above it lands on top, below it bonks, beside it is pushed out sideways.
        // A soft floor only catches a body falling onto it from above (and not while dropping through).
        public Contact Resolve(Vector3 prev, ref Vector3 pos, ref Vector3 speed, float minY, float maxY, float halfZ, bool dropping)
        {
            var contact = new Contact();
            const float eps = 0.001f;
            for (int i = 0; i < Solids.Length; i++)
            {
                Box box = Solids[i];
                float feet = pos.Y + minY;
                float head = pos.Y + maxY;
                if (pos.Z + halfZ <= box.ZMin || pos.Z - halfZ >= box.ZMax)
                {
                    continue;
                }
                float prevFeet = prev.Y + minY;
                float prevHead = prev.Y + maxY;
                bool support = speed.Y <= 0 && prevFeet >= box.YMax - eps && feet <= box.YMax + 0.1f;
                if (!support && (head <= box.YMin || feet >= box.YMax))
                {
                    continue;
                }
                if (support || prevFeet >= box.YMax - eps)
                {
                    pos.Y = box.YMax - minY;
                    if (speed.Y < 0)
                    {
                        speed.Y = 0;
                    }
                    contact.Standing = true;
                    contact.Soft = false;
                }
                else if (prevHead <= box.YMin + eps)
                {
                    pos.Y = box.YMin - maxY;
                    if (speed.Y > 0)
                    {
                        speed.Y = 0;
                    }
                    contact.Bonk = true;
                }
                else
                {
                    float center = (box.ZMin + box.ZMax) / 2;
                    pos.Z = prev.Z < center || prev.Z == center && pos.Z < center ? box.ZMin - halfZ : box.ZMax + halfZ;
                    speed.Z = 0;
                    contact.Lateral = true;
                }
            }
            if (!dropping && speed.Y <= 0 && !contact.Standing)
            {
                for (int i = 0; i < SoftFloors.Length; i++)
                {
                    float floorNow = FloorY(SoftFloors[i], pos.Z);
                    float floorPrev = FloorY(SoftFloors[i], prev.Z);
                    if (Single.IsNaN(floorNow))
                    {
                        continue;
                    }
                    if (Single.IsNaN(floorPrev))
                    {
                        floorPrev = floorNow;
                    }
                    float feet = pos.Y + minY;
                    float prevFeet = prev.Y + minY;
                    // (a little slack above the floor so walking down its slope stays on it)
                    if (prevFeet >= floorPrev - eps && feet <= floorNow + 0.1f)
                    {
                        pos.Y = floorNow - minY;
                        speed.Y = 0;
                        contact.Standing = true;
                        contact.Soft = true;
                        break;
                    }
                }
            }
            pos.X = 0;
            speed.X = 0;
            return contact;
        }
    }
}
