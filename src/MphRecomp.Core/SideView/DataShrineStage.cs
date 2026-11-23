using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.SideView
{
    // recomp: side-view mode P0 (owner 2026-11-12, docs/SIDE_VIEW_MODE.md "Data Shrine stage"). Data Shrine ("MP1
    // SANCTORUS") cut on the plane x = 0 down its centre walkway, seen along x: +z is screen right. Two lanes:
    //   - the lower floor at y 0, solid and the lowest point;
    //   - the walkway at 3.53, soft (jump up through it, land on top, Down drops through); its middle over the centre hex
    //     is the hex's solid top (3.74), with short soft ramps either side.
    // The three hexes are solid. The centre room is the whole stage (owner 11-12, "you shouldnt be able to go into the side
    // rooms that should be the kill zone"): the camera never looks past its end walls (CamZ, z +-18.84), and a player who
    // walks on past them on either lane is off the screen (in a bubble) for a stretch before the KO line, KoGapDefault
    // further on (owner 11-12, "some room between off screen and actually hitting the kill zone"; z +-24.84, the host's
    // 9 / 0 keys move it), with the floors carried well past that so nobody falls first. Doors are visual only. KO over y 31.3, or under the
    // safety line at y -11.3; the camera's box stops at the dome (13.6) with a little room over it.
    public static class DataShrineStage
    {
        public const string Room = "MP1 SANCTORUS";
        public const float KoGapDefault = 6; // the side KO lines past the camera's box
        const float FloorEnd = 40; // past the farthest KO line the gap can set (9 / 0: up to 20 past the walls)

        public static SideViewStage Create()
        {
            return new SideViewStage
            {
                Name = "Data Shrine",
                Solids = new[]
                {
                    new SideViewStage.Box(-FloorEnd, FloorEnd, -2, 0), // lower floor
                    new SideViewStage.Box(-2.35f, 2.35f, 0, 3.74f), // centre hex (its top is the walkway's solid middle)
                    new SideViewStage.Box(3.53f, 8.24f, 0, 2.14f), // right hex
                    new SideViewStage.Box(-8.24f, -3.53f, 0, 2.14f), // left hex
                },
                SoftFloors = new[]
                {
                    new[]
                    {
                        new Vector2(-FloorEnd, 3.53f),
                        new Vector2(-16.5f, 3.53f),
                        new Vector2(-11, 3.74f),
                        new Vector2(11, 3.74f),
                        new Vector2(16.5f, 3.53f),
                        new Vector2(FloorEnd, 3.53f),
                    },
                },
                KoLeft = -18.84f - KoGapDefault, // the end walls + the gap
                KoRight = 18.84f + KoGapDefault,
                KoTop = 31.3f,
                KoBottom = -11.3f,
                Spawns = new[]
                {
                    new Vector2(-12, 3.74f),
                    new Vector2(12, 3.74f),
                    new Vector2(-14, 0),
                    new Vector2(14, 0),
                },
                CamZMin = -18.84f, // the end walls
                CamZMax = 18.84f,
                CamYMin = -2.5f,
                CamYMax = 14.5f,
            };
        }
    }
}
