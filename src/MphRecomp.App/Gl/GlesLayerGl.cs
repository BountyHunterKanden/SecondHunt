using MphRead.Rendering;

namespace MphRecomp.App.Gl;

// GLES 3 state for the layered draw (MphRead.Rendering.LayeredDraw). Phase state goes through GLES30; the per-item
// stencil test, called once per translucent item in three phases, goes through GlNative (no JNI, no garbage).
//
// GLES has no alpha test, so the alpha filter is the scene program's u_alphaPass uniform: 0 keep all, 1 keep only
// alpha exactly 1, 2 keep only alpha below 1. It's written with glUniform1i, which targets the CURRENT program: the
// scene program has to be bound whenever a phase step that changes the filter runs.
public sealed class GlesLayerGl : ILayerGl
{
    public GlesLayerGl(int alphaPassLocation)
    {
        AlphaPassLocation = alphaPassLocation;
    }

    // u_alphaPass's location in the scene program (set it again if the program is rebuilt)
    public int AlphaPassLocation { get; set; }

    public bool IsDesktop => false;

    public void SetAlphaFilter(LayerAlphaFilter filter)
    {
        int pass = filter switch
        {
            LayerAlphaFilter.Off => 0,
            LayerAlphaFilter.SolidOnly => 1,
            LayerAlphaFilter.SeeThroughOnly => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
        GLES30.GlUniform1i(AlphaPassLocation, pass);
    }

    public void SetColorWrites(bool on)
    {
        GLES30.GlColorMask(on, on, on, on);
    }

    public void SetDepthWrites(bool on)
    {
        GLES30.GlDepthMask(on);
    }

    public void SetDepthFunc(LayerDepthFunc func)
    {
        GLES30.GlDepthFunc(func switch
        {
            LayerDepthFunc.Less => GLES30.GlLess,
            LayerDepthFunc.LessOrEqual => GLES30.GlLequal,
            _ => throw new ArgumentOutOfRangeException(nameof(func)),
        });
    }

    public void ClearDepth()
    {
        GLES30.GlClear(GLES30.GlDepthBufferBit);
    }

    public void SetStencilTest(bool on)
    {
        if (on)
        {
            GLES30.GlEnable(GLES30.GlStencilTest);
        }
        else
        {
            GLES30.GlDisable(GLES30.GlStencilTest);
        }
    }

    public void SetStencilWriteMask(int mask)
    {
        GLES30.GlStencilMask(mask);
    }

    public void SetStencilOp(LayerStencilOp fail, LayerStencilOp depthFail, LayerStencilOp pass)
    {
        GLES30.GlStencilOp(ToGl(fail), ToGl(depthFail), ToGl(pass));
    }

    public void SetStencilFunc(LayerStencilFunc func, int reference, int mask)
    {
        GLES30.GlStencilFunc(ToGl(func), reference, mask);
    }

    // per item: straight to the driver
    public void SetItemStencilFunc(LayerStencilFunc func, int reference, int mask)
    {
        GlNative.StencilFunc(ToGl(func), reference, mask);
    }

    public void SetPolygonOffsetFill(bool on)
    {
        if (on)
        {
            GLES30.GlEnable(GLES30.GlPolygonOffsetFill);
        }
        else
        {
            GLES30.GlDisable(GLES30.GlPolygonOffsetFill);
        }
    }

    public void SetPolygonOffset(float factor, float units)
    {
        GLES30.GlPolygonOffset(factor, units);
    }

    public void SetBlending(bool on)
    {
        if (on)
        {
            GLES30.GlEnable(GLES30.GlBlend);
        }
        else
        {
            GLES30.GlDisable(GLES30.GlBlend);
        }
    }

    public void SetBlendFuncSourceAlpha()
    {
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
    }

    private static int ToGl(LayerStencilOp op)
    {
        return op switch
        {
            LayerStencilOp.Keep => GLES30.GlKeep,
            LayerStencilOp.Zero => GLES30.GlZero,
            LayerStencilOp.Replace => GLES30.GlReplace,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    private static int ToGl(LayerStencilFunc func)
    {
        return func switch
        {
            LayerStencilFunc.Always => GLES30.GlAlways,
            LayerStencilFunc.Greater => GLES30.GlGreater,
            LayerStencilFunc.NotEqual => GLES30.GlNotequal,
            LayerStencilFunc.Equal => GLES30.GlEqual,
            _ => throw new ArgumentOutOfRangeException(nameof(func)),
        };
    }
}
