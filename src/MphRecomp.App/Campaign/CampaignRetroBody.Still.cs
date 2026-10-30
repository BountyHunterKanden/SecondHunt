using System;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// A native body drawn still (IMPORT GAMES' tile and card pictures, ImportItemRender: the Scout Drones' bodies/<id>/
// imports, which have no .dae for CampaignSuit.TryLoadStill): the body held in the first frame of its idle clip, in its
// own model space (model = identity), every pass as a seat's body draws them, no seat needed. Kept in its own file
// beside CampaignRetroBody.cs.
internal sealed partial class CampaignRetroBody
{
    NativeBodyAnimator? _still;

    // the pose of a picture: a Scout Drone unfolded in its firing loop (owner 2026-10-26: "in the unfolded firing position"),
    // any other body in its idle clip
    BodyClips.Choice StillChoice()
        => Body.Clip("A_shootloop_drone") != null ? new BodyClips.Choice("still", "A_shootloop_drone", true, Snap: true) : Clips.Standing();

    // a worker thread, after TryLoad: the idle pose's box in model space (its vertices skinned as the GX program skins
    // them), for the picture's framing; null when nothing is posed. Poses this body (a picture's own load, unshared)
    public (Vector3 Min, Vector3 Max)? StillBox()
    {
        _still ??= new NativeBodyAnimator(Body, Clips);
        _still.Step(StillChoice(), 0);
        if (!_still.Pose(null)) return null;
        const int F = HdGun.FloatsPerVertex;
        var min = new Vector3(Single.MaxValue);
        var max = new Vector3(Single.MinValue);
        for (int m = 0; m < Body.Data.Materials.Length; m++)
        {
            HdGun.Material mat = Body.Data.Materials[m];
            int[] pal = mat.Palette;
            var bones = new Matrix4[pal.Length];
            for (int k = 0; k < pal.Length; k++) bones[k] = Body.Data.InvBind[pal[k]] * Body.World[pal[k]];
            float[] v = mat.Vertices;
            for (int i = 0; i + F <= v.Length; i += F)
            {
                var pos = new Vector3(v[i], v[i + 1], v[i + 2]);
                Vector3 at = Vector3.Zero;
                float sum = 0;
                for (int j = 0; j < 4; j++)
                {
                    float w = v[i + 15 + j];
                    int slot = (int)(v[i + 11 + j] + 0.5f);
                    if (w <= 0 || slot < 0 || slot >= pal.Length) continue;
                    at += Vector3.TransformPosition(pos, bones[slot]) * w;
                    sum += w;
                }
                if (sum <= 0) continue;
                min = Vector3.ComponentMin(min, at);
                max = Vector3.ComponentMax(max, at);
            }
        }
        return min.X <= max.X ? (min, max) : null;
    }

    // the body in its idle clip's first frame at model = identity (the GL thread, once Warm says ready): opaque, blended,
    // additive, as DrawBody. The caller binds its framebuffer and enables the depth test; false when it failed
    public bool DrawStill(Matrix4 view, Matrix4 proj)
    {
        if (Failed || !Warm(1000)) return false;
        _still ??= new NativeBodyAnimator(Body, Clips);
        _still.Step(StillChoice(), 0);
        for (int p = 0; p < 3; p++) Draw(_still, null, Matrix4.Identity, view, proj, (SuitPass)p, 1f, false);
        return !Failed;
    }

    // a picture's body done with: free its GL objects (on the GL thread; the programs are CampaignGlCache's, shared)
    public void DeleteGl()
    {
        foreach (int t in _texIds.Values) if (t != 0) GLES30.GlDeleteTextures(1, new[] { t }, 0);
        if (_vao != 0) GLES30.GlDeleteVertexArrays(1, new[] { _vao }, 0);
        if (_vbo != 0) GLES30.GlDeleteBuffers(1, new[] { _vbo }, 0);
        _vao = _vbo = 0;
        _texIds.Clear();
        _glReady = false;
    }
}
