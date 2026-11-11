using System.Collections.Generic;
using MphRead;
using MphRead.Rendering;
using MphRecomp.App.Gl;

namespace MphRecomp.App;

// The fading first-person gun (Trace's cloak; RenderItem.HostOneLayer, PlayerDraw): left out of the layers and drawn
// after them as the opaque gun would be, then faded as one piece -- at each pixel only the gun's nearest surface, the
// lights and strips included, blended once at the gun's alpha. Owner 11-06: "the whole arm cannon should fade together
// as one unified piece"; in the layers the first-drawn fragment kept each pixel, so inner faces showed through and the
// lights (on their own ID, over the casing) stayed vivid while the body thinned, and the gun came apart as it faded.
internal sealed partial class CampaignRenderer
{
    const int GlIncr = 0x1E02;
    readonly List<RenderItem> _fadeGun = new(), _fadeGunDecal = new(), _fadeGunTrans = new();
    bool _fadeGunPass; // DrawItem: the fading gun's items draw only here
    float _fadeGunAlpha = -1; // DrawItem: u_matAlpha for every item while >= 0

    // DrawItem: an item of the fading gun outside its own pass
    bool FadingGunSkips(RenderItem item) => item.HostOneLayer && !_fadeGunPass;

    // after the layers (Finish), with their program, blending and the solids' depth
    void DrawFadingGun(Scene scene, IReadOnlyList<RenderItem> nonDecal, IReadOnlyList<RenderItem> decal)
    {
        _fadeGun.Clear();
        _fadeGunDecal.Clear();
        _fadeGunTrans.Clear();
        for (int i = 0; i < nonDecal.Count; i++)
        {
            RenderItem item = nonDecal[i];
            if (item.HostOneLayer) (item.RenderMode == RenderMode.Translucent ? _fadeGunTrans : _fadeGun).Add(item);
        }
        for (int i = 0; i < decal.Count; i++)
        {
            if (decal[i].HostOneLayer) _fadeGunDecal.Add(decal[i]);
        }
        if (_fadeGun.Count + _fadeGunDecal.Count + _fadeGunTrans.Count == 0) return;
        _fadeGunPass = true;
        ILayerGl gl = _layerGl;
        // 1. its depth where the opaque gun's solid pass leaves it (alpha 1, the solids' filter), its pixels marked 1
        GLES30.GlStencilMask(0xFF);
        GLES30.GlClearStencil(0);
        GLES30.GlClear(GLES30.GlStencilBufferBit);
        gl.SetStencilTest(true);
        GLES30.GlStencilFunc(GLES30.GlAlways, 1, 0xFF);
        GLES30.GlStencilOp(GLES30.GlKeep, GLES30.GlKeep, GLES30.GlReplace);
        gl.SetColorWrites(false);
        gl.SetDepthWrites(true);
        GLES30.GlDepthFunc(GLES30.GlLess);
        gl.SetAlphaFilter(LayerAlphaFilter.SolidOnly);
        _fadeGunAlpha = 1;
        for (int i = 0; i < _fadeGun.Count; i++) DrawItem(_fadeGun[i], scene);
        _fadeGunAlpha = -1;
        // 2. its colour at that nearest surface only, once a pixel (1 -> 2)
        gl.SetAlphaFilter(LayerAlphaFilter.Off);
        gl.SetColorWrites(true);
        gl.SetDepthWrites(false);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthFunc(GLES30.GlEqual);
        GLES30.GlStencilFunc(GLES30.GlEqual, 1, 0xFF);
        GLES30.GlStencilOp(GLES30.GlKeep, GLES30.GlKeep, GlIncr);
        for (int i = 0; i < _fadeGun.Count; i++) DrawItem(_fadeGun[i], scene);
        // 3. its decals on that, pulled forward as in the decal pass, once a pixel (2 -> 3)
        if (_fadeGunDecal.Count > 0)
        {
            GLES30.GlDepthFunc(GLES30.GlLequal);
            GLES30.GlStencilFunc(GLES30.GlEqual, 2, 0xFF);
            gl.SetPolygonOffsetFill(true);
            gl.SetPolygonOffset(-1f, -1f);
            for (int i = 0; i < _fadeGunDecal.Count; i++) DrawItem(_fadeGunDecal[i], scene);
            gl.SetPolygonOffset(0f, 0f);
            gl.SetPolygonOffsetFill(false);
        }
        // 4. its see-through materials (the charge glows) over the rest, behind it hidden
        gl.SetStencilTest(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        for (int i = 0; i < _fadeGunTrans.Count; i++) DrawItem(_fadeGunTrans[i], scene);
        gl.SetDepthWrites(true);
        GLES30.GlStencilOp(GLES30.GlKeep, GLES30.GlKeep, GLES30.GlKeep);
        _fadeGunPass = false;
    }
}
