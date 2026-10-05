using System;
using OpenTK.Graphics.OpenGL;

namespace MphRead.Rendering;

// Desktop GL (OpenTK, compatibility profile) state for the layered draw. The alpha filter is the fixed-function
// alpha test, so the scene shader needs no alpha-pass uniform here.
public sealed class DesktopLayerGl : ILayerGl
{
    public bool IsDesktop => true;

    public void SetAlphaFilter(LayerAlphaFilter filter)
    {
        switch (filter)
        {
        case LayerAlphaFilter.Off:
            GL.Disable(EnableCap.AlphaTest);
            break;
        case LayerAlphaFilter.SolidOnly:
            GL.Enable(EnableCap.AlphaTest);
            GL.AlphaFunc(AlphaFunction.Equal, 1.0f);
            break;
        case LayerAlphaFilter.SeeThroughOnly:
            GL.Enable(EnableCap.AlphaTest);
            GL.AlphaFunc(AlphaFunction.Less, 1.0f);
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    public void SetColorWrites(bool on)
    {
        GL.ColorMask(on, on, on, on);
    }

    public void SetDepthWrites(bool on)
    {
        GL.DepthMask(on);
    }

    public void SetDepthFunc(LayerDepthFunc func)
    {
        GL.DepthFunc(func switch
        {
            LayerDepthFunc.Less => DepthFunction.Less,
            LayerDepthFunc.LessOrEqual => DepthFunction.Lequal,
            _ => throw new ArgumentOutOfRangeException(nameof(func)),
        });
    }

    public void ClearDepth()
    {
        GL.Clear(ClearBufferMask.DepthBufferBit);
    }

    public void SetStencilTest(bool on)
    {
        if (on)
        {
            GL.Enable(EnableCap.StencilTest);
        }
        else
        {
            GL.Disable(EnableCap.StencilTest);
        }
    }

    public void SetStencilWriteMask(int mask)
    {
        GL.StencilMask(mask);
    }

    public void SetStencilOp(LayerStencilOp fail, LayerStencilOp depthFail, LayerStencilOp pass)
    {
        GL.StencilOp(ToGl(fail), ToGl(depthFail), ToGl(pass));
    }

    public void SetStencilFunc(LayerStencilFunc func, int reference, int mask)
    {
        GL.StencilFunc(ToGl(func), reference, mask);
    }

    public void SetItemStencilFunc(LayerStencilFunc func, int reference, int mask)
    {
        GL.StencilFunc(ToGl(func), reference, mask);
    }

    public void SetPolygonOffsetFill(bool on)
    {
        if (on)
        {
            GL.Enable(EnableCap.PolygonOffsetFill);
        }
        else
        {
            GL.Disable(EnableCap.PolygonOffsetFill);
        }
    }

    public void SetPolygonOffset(float factor, float units)
    {
        GL.PolygonOffset(factor, units);
    }

    public void SetBlending(bool on)
    {
        if (on)
        {
            GL.Enable(EnableCap.Blend);
        }
        else
        {
            GL.Disable(EnableCap.Blend);
        }
    }

    public void SetBlendFuncSourceAlpha()
    {
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    private static StencilOp ToGl(LayerStencilOp op)
    {
        return op switch
        {
            LayerStencilOp.Keep => StencilOp.Keep,
            LayerStencilOp.Zero => StencilOp.Zero,
            LayerStencilOp.Replace => StencilOp.Replace,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    private static StencilFunction ToGl(LayerStencilFunc func)
    {
        return func switch
        {
            LayerStencilFunc.Always => StencilFunction.Always,
            LayerStencilFunc.Greater => StencilFunction.Greater,
            LayerStencilFunc.NotEqual => StencilFunction.Notequal,
            LayerStencilFunc.Equal => StencilFunction.Equal,
            _ => throw new ArgumentOutOfRangeException(nameof(func)),
        };
    }
}
