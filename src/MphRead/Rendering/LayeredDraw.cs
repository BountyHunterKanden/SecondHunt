using System;
using System.Collections.Generic;

namespace MphRead.Rendering;

// The per-frame layered draw: solid surfaces, then decals, then translucent surfaces in two layers so that a
// translucent polygon ID never blends over itself (the DS rule). The pixel-level idea:
// 1. solids (alpha exactly 1) fill colour and depth;
// 2. decals are pulled toward the viewer and blended on top;
// 3. "marking": each translucent fragment that's in front, and whose ID is higher than the pixel's current owner,
//    becomes the owner (stencil holds the owner ID, clamped to 255 by GL); no colour;
// 4. the depth buffer is rebuilt from the solids only;
// 5. back layer: translucent fragments at pixels another ID owns are blended;
// 6. front layer: then those at pixels their own ID owns.
//
// Each phase is split into Begin (sets state), an item step, and End where the phase has one, because the Android
// renderer runs extra draws between them and the state those draws leave behind is part of the expected result. So a
// Begin sets exactly the state listed for it and nothing else, and an item step only sets the per-item stencil test
// (where there is one) before calling the caller's draw.

// Which fragments survive, by their final alpha.
public enum LayerAlphaFilter
{
    // keep every fragment
    Off,
    // keep only fragments whose alpha is exactly 1
    SolidOnly,
    // keep only fragments whose alpha is below 1
    SeeThroughOnly,
}

public enum LayerDepthFunc
{
    Less,
    LessOrEqual,
}

public enum LayerStencilFunc
{
    Always,
    Greater,
    NotEqual,
    Equal,
}

public enum LayerStencilOp
{
    Keep,
    Zero,
    Replace,
}

// The GL state operations the layered draw needs, one implementation per platform. Each call maps to one GL state
// change (or, for the ES alpha filter, one uniform write); none of them touches anything else.
public interface ILayerGl
{
    // True on desktop GL. The draw applies the spec's "desktop also" state only when this is true.
    bool IsDesktop { get; }

    // Desktop: the fixed-function alpha test (Off = disabled, SolidOnly = EQUAL 1.0, SeeThroughOnly = LESS 1.0).
    // ES: writes u_alphaPass (0 / 1 / 2) on the scene program, which must be the current program.
    void SetAlphaFilter(LayerAlphaFilter filter);

    // glColorMask with all four channels set to on
    void SetColorWrites(bool on);

    // glDepthMask
    void SetDepthWrites(bool on);

    // glDepthFunc
    void SetDepthFunc(LayerDepthFunc func);

    // glClear of the depth buffer only (colour and stencil are kept)
    void ClearDepth();

    // enable / disable GL_STENCIL_TEST
    void SetStencilTest(bool on);

    // glStencilMask
    void SetStencilWriteMask(int mask);

    // glStencilOp (stencil fail, depth fail, pass)
    void SetStencilOp(LayerStencilOp fail, LayerStencilOp depthFail, LayerStencilOp pass);

    // glStencilFunc, as part of a phase's Begin
    void SetStencilFunc(LayerStencilFunc func, int reference, int mask);

    // glStencilFunc, once per item inside an item step (the hot path; same GL call as SetStencilFunc)
    void SetItemStencilFunc(LayerStencilFunc func, int reference, int mask);

    // enable / disable GL_POLYGON_OFFSET_FILL
    void SetPolygonOffsetFill(bool on);

    // glPolygonOffset
    void SetPolygonOffset(float factor, float units);

    // enable / disable GL_BLEND
    void SetBlending(bool on);

    // glBlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA)
    void SetBlendFuncSourceAlpha();
}

// Runs the phases over items of type TItem. The caller supplies how to read an item's polygon ID (1 or more; may be
// above 255) and how to draw it (set its uniforms and issue its geometry).
//
// One frame, phase by phase (what DrawFrame does; the Android renderer makes the same calls with its extra draws in
// between):
//   BeginSolid, DrawSolidItems(nonDecal), EndSolid,
//   BeginDecal, DrawDecalItems(decals), EndDecal,
//   BeginMarking, DrawMarkingItems(translucent),
//   BeginDepthRebuild, DrawDepthRebuildItems(nonDecal),
//   BeginBackLayer, DrawBackLayerItems(translucent),
//   BeginFrontLayer, DrawFrontLayerItems(translucent),
//   Finish.
// On entry the caller has cleared colour, depth (to 1.0) and an 8-bit stencil (to 0).
public sealed class LayeredDraw<TItem>
{
    private const int AllBits = 0xFF;

    private readonly ILayerGl _gl;
    private readonly Func<TItem, int> _polygonId;
    private readonly Action<TItem> _draw;

    public LayeredDraw(ILayerGl gl, Func<TItem, int> polygonId, Action<TItem> draw)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _polygonId = polygonId ?? throw new ArgumentNullException(nameof(polygonId));
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    }

    public ILayerGl Gl => _gl;

    // The whole frame in order, with nothing drawn between the phases (the desktop renderer's path).
    // nonDecal = N (includes items with translucent materials), decals = D, translucent = T (may share items with
    // N or D).
    public void DrawFrame(IReadOnlyList<TItem> nonDecal, IReadOnlyList<TItem> decals, IReadOnlyList<TItem> translucent)
    {
        BeginSolid();
        DrawSolidItems(nonDecal);
        EndSolid();

        BeginDecal();
        DrawDecalItems(decals);
        EndDecal();

        BeginMarking();
        DrawMarkingItems(translucent);

        BeginDepthRebuild();
        DrawDepthRebuildItems(nonDecal);

        BeginBackLayer();
        DrawBackLayerItems(translucent);

        BeginFrontLayer();
        DrawFrontLayerItems(translucent);

        Finish();
    }

    // ---- 1. solid surfaces: alpha exactly 1, nearest wins ----

    public void BeginSolid()
    {
        _gl.SetAlphaFilter(LayerAlphaFilter.SolidOnly);
        _gl.SetDepthFunc(LayerDepthFunc.Less);
        _gl.SetStencilTest(true);
        _gl.SetStencilWriteMask(AllBits);
        _gl.SetStencilOp(LayerStencilOp.Zero, LayerStencilOp.Zero, LayerStencilOp.Zero);
        _gl.SetStencilFunc(LayerStencilFunc.Always, 0, AllBits);
        if (_gl.IsDesktop)
        {
            _gl.SetColorWrites(true);
            _gl.SetDepthWrites(true);
        }
    }

    // the non-decal list; no per-item state
    public void DrawSolidItems(IReadOnlyList<TItem> nonDecal)
    {
        DrawEach(nonDecal);
    }

    public void EndSolid()
    {
        _gl.SetAlphaFilter(LayerAlphaFilter.Off);
    }

    // ---- 2. decals: every fragment, pulled forward, depth "nearer or equal", blended ----

    public void BeginDecal()
    {
        _gl.SetPolygonOffsetFill(true);
        _gl.SetPolygonOffset(-1f, -1f);
        _gl.SetDepthFunc(LayerDepthFunc.LessOrEqual);
        if (_gl.IsDesktop)
        {
            _gl.SetBlending(true);
            _gl.SetBlendFuncSourceAlpha();
        }
    }

    // the decal list; no per-item state
    public void DrawDecalItems(IReadOnlyList<TItem> decals)
    {
        DrawEach(decals);
    }

    public void EndDecal()
    {
        _gl.SetPolygonOffset(0f, 0f);
        _gl.SetPolygonOffsetFill(false);
    }

    // ---- 3. marking: translucent fragments claim pixel ownership in the stencil (no colour) ----

    public void BeginMarking()
    {
        _gl.SetAlphaFilter(LayerAlphaFilter.SeeThroughOnly);
        _gl.SetColorWrites(false);
        _gl.SetStencilOp(LayerStencilOp.Keep, LayerStencilOp.Keep, LayerStencilOp.Replace);
    }

    // the translucent list; each item passes the stencil only where its ID is greater than the current owner
    public void DrawMarkingItems(IReadOnlyList<TItem> translucent)
    {
        DrawEachWithStencil(translucent, LayerStencilFunc.Greater);
    }

    // (marking has no End step)

    // ---- 4. depth rebuild: depth from the solids only ----

    public void BeginDepthRebuild()
    {
        _gl.ClearDepth();
        _gl.SetStencilOp(LayerStencilOp.Keep, LayerStencilOp.Keep, LayerStencilOp.Keep);
        _gl.SetStencilFunc(LayerStencilFunc.Always, 0, AllBits);
        _gl.SetAlphaFilter(LayerAlphaFilter.SolidOnly);
    }

    // the non-decal list again; no per-item state
    public void DrawDepthRebuildItems(IReadOnlyList<TItem> nonDecal)
    {
        DrawEach(nonDecal);
    }

    // ---- 5. back layer: translucent fragments at pixels another ID owns ----

    public void BeginBackLayer()
    {
        _gl.SetAlphaFilter(LayerAlphaFilter.SeeThroughOnly);
        _gl.SetColorWrites(true);
        _gl.SetDepthWrites(false);
        _gl.SetDepthFunc(LayerDepthFunc.LessOrEqual);
        if (_gl.IsDesktop)
        {
            _gl.SetStencilOp(LayerStencilOp.Keep, LayerStencilOp.Keep, LayerStencilOp.Keep);
        }
    }

    public void DrawBackLayerItems(IReadOnlyList<TItem> translucent)
    {
        DrawEachWithStencil(translucent, LayerStencilFunc.NotEqual);
    }

    // ---- 6. front layer: translucent fragments at pixels their own ID owns ----

    // Nothing on ES; desktop sets the stencil op again.
    public void BeginFrontLayer()
    {
        if (_gl.IsDesktop)
        {
            _gl.SetStencilOp(LayerStencilOp.Keep, LayerStencilOp.Keep, LayerStencilOp.Keep);
        }
    }

    public void DrawFrontLayerItems(IReadOnlyList<TItem> translucent)
    {
        DrawEachWithStencil(translucent, LayerStencilFunc.Equal);
    }

    // ---- after the last layer ----

    public void Finish()
    {
        _gl.SetDepthWrites(true);
        _gl.SetAlphaFilter(LayerAlphaFilter.Off);
        _gl.SetStencilTest(false);
    }

    // Indexed loops (not foreach) so a frame allocates nothing.
    private void DrawEach(IReadOnlyList<TItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            _draw(items[i]);
        }
    }

    // The raw polygon ID goes in as the stencil reference; GL clamps it to the stencil's 255 itself.
    private void DrawEachWithStencil(IReadOnlyList<TItem> items, LayerStencilFunc func)
    {
        for (int i = 0; i < items.Count; i++)
        {
            TItem item = items[i];
            _gl.SetItemStencilFunc(func, _polygonId(item), AllBits);
            _draw(item);
        }
    }
}
