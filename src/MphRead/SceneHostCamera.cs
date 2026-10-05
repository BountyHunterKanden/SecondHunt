using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: a camera set by the host each frame, for a headless scene with no room or player (the gunship's planet
    // select draws the deep-space model this way). Roam mode: TransformCamera builds a LookAt from these.
    public partial class Scene
    {
        // fovY: the full vertical angle in degrees (the DS keeps half-angles)
        public void SetHostCamera(Vector3 position, Vector3 target, Vector3 up, float fovY)
        {
            _cameraMode = CameraMode.Roam;
            _cameraPosition = position;
            _cameraFacing = (target - position).Normalized();
            _cameraUp = up.Normalized();
            _cameraRight = Vector3.Cross(_cameraFacing, _cameraUp).Normalized();
            _cameraFov = MathHelper.DegreesToRadians(fovY);
        }

        // replaces this frame's projection (OnUpdateFrame rebuilds it as a perspective); e.g. an orthographic view
        public void SetHostProjection(Matrix4 projection) => _perspectiveMatrix = projection;
    }
}
