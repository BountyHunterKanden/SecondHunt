using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Pickups outside the view aren't drawn (Mining Plaza frame dips, 2026-11-01). MPH's own rooms hide a pickup
// through its room node's visibility (EntityBase.IsVisible); an imported Echoes arena has no MPH nodes, so every pickup
// drew every frame: Mining Plaza's 32 placed pickups were ~120 of its ~230 draw items, each drawn in 2-3 passes (opaque
// + depth rebuild, or the translucent layers' marking + back + front), ~1 ms of the render thread's ~6 ms a frame on the
// Odin. Render only: the pickup still exists, steps and scans as before. The sphere is generous (a pickup is ~1 unit,
// bobbing and spinning; smooth frames carry the view a little), so nothing at the screen's edge pops.
internal sealed partial class CampaignRenderer
{
    const float PickupCullRadius = 4f;
    Matrix4 _pickupCullVp;
    readonly Vector4[] _pickupPlanes = new Vector4[6];

    bool PickupOffscreen(RenderItem item, Scene scene)
    {
        if (item.HostOwner is not ItemInstanceEntity pickup) return false;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        if (vp != _pickupCullVp)
        {
            _pickupCullVp = vp;
            // row vectors (OpenTK): clip = p * vp, so the planes are column 3 +/- columns 0..2
            Vector4 c0 = vp.Column0, c1 = vp.Column1, c2 = vp.Column2, c3 = vp.Column3;
            _pickupPlanes[0] = c3 + c0; _pickupPlanes[1] = c3 - c0;
            _pickupPlanes[2] = c3 + c1; _pickupPlanes[3] = c3 - c1;
            _pickupPlanes[4] = c3 + c2; _pickupPlanes[5] = c3 - c2;
            for (int i = 0; i < 6; i++)
            {
                Vector4 p = _pickupPlanes[i];
                float len = p.Xyz.Length;
                _pickupPlanes[i] = len > 0 ? p / len : Vector4.Zero;
            }
        }
        Vector3 at = pickup.Position;
        for (int i = 0; i < 6; i++)
        {
            Vector4 p = _pickupPlanes[i];
            if (p.X * at.X + p.Y * at.Y + p.Z * at.Z + p.W < -PickupCullRadius) return true;
        }
        return false;
    }
}
