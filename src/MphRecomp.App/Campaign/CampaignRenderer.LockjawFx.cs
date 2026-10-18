using System;
using System.Collections.Generic;
using MphRead;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Beyond Sylux's Lockjaw orb (owner 10-17: "there is a missing glowing orb in the center"): Prime 4 draws it as an effect on
// the core joint between Lockjaw's blades (its model carries no mesh there, and Prime 4's effects aren't imported), so here
// a code-drawn glow sits on it: a pulsing mint glow with a white-hot core, the seat's Lockjaw bomb colour on a hunter copy
// (HostSyluxBombColor). Drawing only, no asset. Places: MatchLockjaw.DrawLockjaw, from the opaque pass's pose.
internal sealed partial class CampaignRenderer
{
    static readonly Vector3 LockjawOrbColour = new(0.35f, 1f, 0.45f);
    readonly List<(Vector3 At, Vector3 Colour)> _lockjawOrbs = new();

    // DrawSkins (MatchSkins.cs), before the seats: the opaque pass starts the frame's list, the additive pass draws it
    void DrawLockjawFx(Scene scene, SuitPass pass)
    {
        if (_host == null || scene != _host.Scene) return;
        if (pass == SuitPass.Opaque) _lockjawOrbs.Clear();
        if (pass != SuitPass.Additive || _lockjawOrbs.Count == 0 || !FxGl() || !FallbackGl()) return;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        float t = scene.FrameCount % 100000;
        // sizes in world units: the orb nearly fills the hoop between Lockjaw's blades (~0.57 across outside, ~0.48 inside,
        // at Sylux's body scale; owner 10-17: "the existing orb just needed to be bigger to almost fill the spherical
        // space", then "about twice as big"); a glow quad shows about half its width
        int n = 0;
        for (int i = 0; i < _lockjawOrbs.Count; i++)
        {
            (Vector3 at, Vector3 c) = _lockjawOrbs[i];
            float ts = t + i * 31;
            float pulse = 0.92f + 0.08f * MathF.Sin(ts * 0.13f) * MathF.Sin(ts * 0.051f + 1);
            Vector3 hot = Vector3.Lerp(c, Vector3.One, 0.7f);
            Glow(ref n, vp, right, up, at, 1.7f * pulse, new Vector4(c, 0.35f));
            Glow(ref n, vp, right, up, at, 1.04f * pulse, new Vector4(Vector3.Lerp(c, Vector3.One, 0.3f), 0.6f));
            Glow(ref n, vp, right, up, at, 0.56f * pulse, new Vector4(hot, 0.95f));
        }
        if (n == 0) return;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        GLES30.GlBindSampler(0, 0);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOne);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, n * 10 * 4, _fxVerts, GLES30.GlDynamicDraw);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _fbGlow);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, n);
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }
}
