using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: the campaign host's third-person view. A world offset from the main player's camera that MphRead draws
    // from -- view matrix, camera position (billboards, sky) and the culling frustum -- while the player's own camera,
    // aim and logic stay first person. Zero (the default) changes nothing.
    public partial class Scene
    {
        public Vector3 HostCameraOffset { get; set; }
    }
}
