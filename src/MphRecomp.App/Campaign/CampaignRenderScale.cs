using System;
using MphRead;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// RENDER SCALE (LOOK > DISPLAY; RecompSettings.RenderScale, 50-200%; GitHub issue #14): the 3D passes draw into an
// offscreen target at that share of the screen's size, which is stretched (bilinear) onto the screen before the HUD, so
// the HUD, the menus, the map and the touch controls stay at the screen's own resolution. Below 100% it trades
// sharpness for GPU time (a weak GPU on a high-res screen); above it, it supersamples. At exactly 100% none of this
// runs: no target, no copy, the frame is drawn as before.
// The menus' arena backdrop always draws here (its screen has no depth buffer); with a depth of field (CampaignRenderer.
// Dof.cs) the target is made of textures and the gather pass draws it onto the screen in place of the stretch.
// The hooks: CampaignView.cs BeginViewWarp (Panini off: BeginRenderScale; Panini on: its own target takes the scale,
// RenderScaleOver), EndViewWarp (EndRenderScale) and ViewGlReset (RenderScaleGlReset); ReloadAimFromSettings
// (LoadRenderScale: live after the pause menu); the mesh lines' width (SceneLineScale). logcat MPHView logs the scale
// and the target size.
internal sealed partial class CampaignRenderer
{
    const int GL_MAX_RENDERBUFFER_SIZE = 0x84E8, GL_MAX_TEXTURE_SIZE = 0x0D33, GL_MAX_VIEWPORT_DIMS = 0x0D3A,
              GL_SAMPLE_BUFFERS = 0x80A8;

    volatile int _renderScalePct = 100; // the settings' value, set wherever they load; read on the GL thread each frame
    int _rsLoggedPct = -1;
    int _rsFbo, _rsColor, _rsDepth, _rsW, _rsH, _rsMax;
    int _rsColorTex, _rsDepthTex; // the target as textures, in place of _rsColor / _rsDepth (depth of field)
    bool _rsTex, _rsMs; // the target is textures; the screen is multisampled (found when the target is made)
    bool _rsDofOn;
    float _rsDofFocus, _rsDofBlur, _rsDofRange;
    Scene? _rsScene;
    int _rsBrokenPct; // the scale whose target failed (0 = none): it draws at 100% until another is picked
    bool _rsOn;
    float _rsLine = 1; // the 3D target's height over the screen's this frame, so mesh lines keep their look
    readonly int[] _rsScissor = new int[1];
    static int s_renderScaleArg; // dev launch arg renderscale <pct> (measurements): this run's scale, the file untouched
    // (read at draw time: ConfigureView parses the args after the renderer has loaded its settings)

    // ConfigureView: each launch
    static void ConfigureRenderScale(LaunchArgs args)
    {
        s_renderScaleArg = int.TryParse(args.Get("renderscale"), out int pct)
            && Array.IndexOf(MphRecomp.Config.RecompSettings.RenderScales, pct) >= 0 ? pct : 0;
    }

    // ReloadAimFromSettings: at launch and after the pause menu
    void LoadRenderScale(MphRecomp.Config.RecompSettings s)
    {
        _renderScalePct = Array.IndexOf(MphRecomp.Config.RecompSettings.RenderScales, s.RenderScale) >= 0 ? s.RenderScale : 100;
    }

    // this frame's scale: the dev arg over the settings, 100 in a build without the row (no stray saved value); logged
    // when it changes
    int RenderScalePct()
    {
        int pct = !MphRecomp.Frontend.RecompMenus.RenderScaleOffered ? 100
            : s_renderScaleArg > 0 ? s_renderScaleArg : _renderScalePct;
        if (pct != _rsLoggedPct)
        {
            _rsLoggedPct = pct;
            Log.Info("MPHView", $"render scale {pct}%");
        }
        return pct;
    }

    // the mesh lines' width (DrawLines): 1 at 100%
    float SceneLineScale => _rsLine;

    // BeginViewWarp, Panini off: the 3D passes go to the scaled target. False at 100% (or if the target can't be made):
    // the frame draws straight to the screen, as before
    bool BeginRenderScale(Scene scene)
    {
        _rsOn = false;
        _rsLine = 1;
        _rsDofOn = false;
        int pct = RenderScalePct();
        bool dof = DofNow(out float focus, out float blur, out float range);
        // the menus' arena backdrop draws here at 100% too: the menus' screen has no depth buffer (GameActivity's EGL
        // config), so straight onto it the room drew in submission order (CampaignRenderer.MenuBackdrop.cs); so does a
        // depth of field (CampaignRenderer.Dof.cs: the gather pass reads the target's depth)
        if ((pct == 100 && !_menuBackdrop && !dof) || pct == _rsBrokenPct || _width <= 0 || _height <= 0)
        {
            return false;
        }
        dof = dof && EnsureDof();
        (int w, int h) = RenderScaleSize(pct);
        // (a texture target blits as well, unless onto a multisampled screen)
        if (w != _rsW || h != _rsH || (dof && !_rsTex) || (!dof && _rsTex && _rsMs))
        {
            if (dof && !AllocRenderScaleTarget(w, h, pct, textures: true))
            {
                _dofBroken = true; // the plain picture rather than a black one
                dof = false;
            }
            if (!dof && !AllocRenderScaleTarget(w, h, pct, textures: false))
            {
                _rsBrokenPct = pct;
                return false;
            }
        }
        (_rsDofOn, _rsDofFocus, _rsDofBlur, _rsDofRange, _rsScene) = (dof, focus, blur, range, scene);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _rsFbo);
        GLES30.GlViewport(0, 0, w, h);
        Color4 clear = scene.ClearColor;
        GLES30.GlClearColor(clear.R, clear.G, clear.B, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit | GLES30.GlStencilBufferBit);
        _rsOn = true;
        _rsLine = h / (float)_height;
        return true;
    }

    // BeginViewWarp, Panini on: its target (already larger than the screen) takes the scale too. Unchanged at 100%
    float RenderScaleOver(float scale)
    {
        int pct = RenderScalePct();
        if (pct == 100)
        {
            _rsLine = 1;
            return scale;
        }
        float k = pct / 100f;
        k = Math.Min(k, RenderScaleMax() / (scale * Math.Max(_width, _height))); // as large as the GPU allows
        _rsLine = k;
        return scale * k;
    }

    // EndViewWarp, Panini off: the scaled picture stretched onto the screen (or through the depth of field's gather
    // pass), then the screen's own size for the HUD
    void EndRenderScale()
    {
        _rsOn = false;
        _rsLine = 1;
        GLES30.GlGetIntegerv(GLES30.GlScissorTest, _rsScissor, 0);
        if (_rsScissor[0] != 0) GLES30.GlDisable(GLES30.GlScissorTest);
        if (_rsDofOn)
        {
            DrawDof();
        }
        else
        {
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _rsFbo);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, 0);
            GLES30.GlBlitFramebuffer(0, 0, _rsW, _rsH, 0, 0, _width, _height, GLES30.GlColorBufferBit, GLES30.GlLinear);
        }
        _rsDofOn = false;
        _rsScene = null;
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        if (_rsScissor[0] != 0) GLES30.GlEnable(GLES30.GlScissorTest);
    }

    bool RenderScaleActive => _rsOn;

    (int, int) RenderScaleSize(int pct)
    {
        float k = pct / 100f;
        int max = RenderScaleMax();
        k = Math.Min(k, max / (float)Math.Max(_width, _height)); // 200% on a big screen: as large as the GPU allows
        return (Math.Clamp((int)MathF.Round(_width * k), 1, max), Math.Clamp((int)MathF.Round(_height * k), 1, max));
    }

    // the largest target side this GPU takes (renderbuffer, texture for Panini's, viewport)
    int RenderScaleMax()
    {
        if (_rsMax == 0)
        {
            var v = new int[2];
            GLES30.GlGetIntegerv(GL_MAX_RENDERBUFFER_SIZE, v, 0);
            int max = v[0] > 0 ? v[0] : 4096;
            GLES30.GlGetIntegerv(GL_MAX_TEXTURE_SIZE, v, 0);
            if (v[0] > 0) max = Math.Min(max, v[0]);
            GLES30.GlGetIntegerv(GL_MAX_VIEWPORT_DIMS, v, 0);
            if (v[0] > 0 && v[1] > 0) max = Math.Min(max, Math.Min(v[0], v[1]));
            _rsMax = Math.Max(1, max);
        }
        return _rsMax;
    }

    // renderbuffers, or textures for the depth of field (its gather pass reads them; a mip chain on the colour for the
    // wide taps). Also made ahead by the backdrop's BackdropBegin, so its first frame makes nothing
    bool AllocRenderScaleTarget(int w, int h, int pct, bool textures)
    {
        var ids = new int[1];
        GLES30.GlGetIntegerv(GL_SAMPLE_BUFFERS, ids, 0); // the stretch is a blit, which a multisampled screen refuses
        _rsMs = ids[0] > 0;
        if (_rsMs && !textures) // (the depth of field's gather is a draw: fine)
        {
            Log.Error("MPHView", $"render scale {pct}%: the screen is multisampled; drawing at 100%");
            return false;
        }
        if (_rsFbo == 0)
        {
            GLES30.GlGenFramebuffers(1, ids, 0); _rsFbo = ids[0];
        }
        DeleteRenderScaleAttachments(); // fresh ones: a texture's old mips have the old size
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _rsFbo);
        if (textures)
        {
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlGenTextures(1, ids, 0); _rsColorTex = ids[0];
            GLES30.GlBindTexture(GLES30.GlTexture2d, _rsColorTex);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (byte[]?)null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GL_LINEAR_MIPMAP_LINEAR);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlGenerateMipmap(GLES30.GlTexture2d); // allocates the chain once; refilled every frame (DrawDof)
            // depth + stencil, as the screen's: MphRead's translucency passes sort overlapping surfaces with the stencil
            GLES30.GlGenTextures(1, ids, 0); _rsDepthTex = ids[0];
            GLES30.GlBindTexture(GLES30.GlTexture2d, _rsDepthTex);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GL_DEPTH24_STENCIL8, w, h, 0, GL_DEPTH_STENCIL, GL_UNSIGNED_INT_24_8, (byte[]?)null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
            GLES30.GlFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GLES30.GlTexture2d, _rsColorTex, 0);
            GLES30.GlFramebufferTexture2D(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GLES30.GlTexture2d, _rsDepthTex, 0);
        }
        else
        {
            GLES30.GlGenRenderbuffers(1, ids, 0); _rsColor = ids[0];
            GLES30.GlGenRenderbuffers(1, ids, 0); _rsDepth = ids[0];
            GLES30.GlBindRenderbuffer(GL_RENDERBUFFER, _rsColor);
            GLES30.GlRenderbufferStorage(GL_RENDERBUFFER, GLES30.GlRgba8, w, h);
            GLES30.GlBindRenderbuffer(GL_RENDERBUFFER, _rsDepth);
            // depth + stencil, as the screen's: MphRead's translucency passes sort overlapping surfaces with the stencil
            GLES30.GlRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, w, h);
            GLES30.GlBindRenderbuffer(GL_RENDERBUFFER, 0);
            GLES30.GlFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, _rsColor);
            GLES30.GlFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, _rsDepth);
        }
        int status = GLES30.GlCheckFramebufferStatus(GL_FRAMEBUFFER);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        _rsTex = textures;
        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            Log.Error("MPHView", $"render scale {pct}%: {(textures ? "depth of field " : "")}target {w}x{h} incomplete (0x{status:X}); "
                + (textures ? "no depth of field" : "drawing at 100%"));
            _rsW = _rsH = 0;
            return false;
        }
        _rsW = w;
        _rsH = h;
        Log.Info("MPHView", $"render scale {pct}%: 3D at {w}x{h} for the {_width}x{_height} screen{(textures ? " (depth of field textures)" : "")}");
        return true;
    }

    // the target's colour + depth, whichever kind (the framebuffer stays)
    void DeleteRenderScaleAttachments()
    {
        if (_rsColor != 0 || _rsDepth != 0) GLES30.GlDeleteRenderbuffers(2, new[] { _rsColor, _rsDepth }, 0);
        if (_rsColorTex != 0 || _rsDepthTex != 0) GLES30.GlDeleteTextures(2, new[] { _rsColorTex, _rsDepthTex }, 0);
        _rsColor = _rsDepth = _rsColorTex = _rsDepthTex = 0;
    }

    // ViewGlReset: a new GL context took the old objects with it
    void RenderScaleGlReset()
    {
        _rsFbo = _rsColor = _rsDepth = _rsColorTex = _rsDepthTex = 0;
        _rsW = _rsH = _rsMax = 0;
        _rsBrokenPct = 0;
        _rsOn = _rsTex = _rsMs = _rsDofOn = false;
        _rsScene = null;
        _dofProgram = _dofVao = 0; // (the program itself is CampaignGlCache's)
        _dofBroken = false;
        _rsLine = 1;
    }
}
