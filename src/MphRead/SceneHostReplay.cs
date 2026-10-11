using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: replays (docs/REPLAY_MODE.md, R1). Both default off, which keeps MphRead's own behaviour.
    public partial class Scene
    {
        // HostDrawFreeSim: what's drawn never changes the match. MphRead does two things in its draw pass that play can
        // feel: Spire's alt-attack rocks are placed in PlayerEntity.Draw (and hit players, PlayerCollision.cs), and
        // effects only age in GetDrawItems. On: the rocks are placed every frame where Draw would place them, drawn or
        // not, and effects also age on frames without a draw list. A frame that builds a draw list plays exactly as
        // before; one without (headless, replay verification, fast-forward) now plays the same as a drawn one.
        public bool HostDrawFreeSim { get; set; }

        // HostView: a render-only camera (replay spectator views). MphRead draws from it -- view matrix, camera position
        // (billboards, sky, portals), field of view and the culling frustum -- while the camera mode stays Player, so the
        // main player's own camera, IsMainPlayer logic and the match are untouched. (SetHostCamera's Roam mode turns
        // main-player logic off; this doesn't.) Null = the main player's camera (plus HostCameraOffset).
        public HostView? HostView { get; set; }
    }

    // fovY: the full vertical angle in degrees, like CameraInfo.Fov (0 = the main player's own)
    public readonly record struct HostView(Vector3 Position, Vector3 Target, Vector3 Up, float FovY);
}
