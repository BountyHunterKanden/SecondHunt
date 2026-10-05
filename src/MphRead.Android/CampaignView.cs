using System;
using Android.Content;
using Android.Opengl;
using Android.Util;
using MphRead;
using MphRecomp.Framing;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// View framing (the widescreen "fish-eye" A/B): WIDE (MphRead's own, ~110 degrees across on 16:9) is the view -- the
// owner's pick (board #30a, 2026-10-04). The in-play tap that cycled WIDE -> DS FRAMING -> PRIME (Metroid Prime's 55
// degrees) -> PANINI is gone (taps belong to the touch controls, MatchTouchOverlay.cs); the other modes stay reachable
// only through the dev extra below. The math, the numbers and the why live in MphRecomp.Core Framing/ViewFraming.cs (PC checks: Tools -viewtest [sim]).
// DS framing and Prime only change the projection (MphRead Scene.HostFramingAspect). Panini draws the 3D passes into an
// offscreen target (1.38x the screen, so the magnified centre keeps 1:1 pixels; mipmapped, so the squeezed edges don't
// shimmer) and warps it onto the screen before the HUD; Scene.HostScreenWarp moves the reticle, locator icons and scan
// brackets with the picture. The Prime gun keeps its vanilla size in every mode (GunProjection: its own projection, as
// modern games draw viewmodels); in Panini it is drawn after the warp, unwarped, on top (MPH's own gun, the "original"
// suit, is part of the scene and still follows the mode). The in-ship cockpit always uses WIDE. Bot vision is separate
// (Scene.HostAiAspect, fixed 16:9).
// CampaignActivity.cs calls: AttachView (OnCreate), ViewGlReset (OnSurfaceCreated),
// BeginViewWarp / EndViewWarp (Render, around the 3D passes), GunWaitsForWarp / GunProjection (DrawPrimeGun),
// ResetViewHooks (Shutdown).
// Dev: --es view wide|ds|prime|panini picks the mode (default WIDE); logcat MPHView logs it and the target size.
internal sealed partial class CampaignRenderer
{
    // GL enums this binding doesn't surface as un-shadowed constants (as in RenderActivity.cs)
    const int GL_FRAMEBUFFER = 0x8D40, GL_RENDERBUFFER = 0x8D41, GL_COLOR_ATTACHMENT0 = 0x8CE0,
              GL_DEPTH_STENCIL_ATTACHMENT = 0x821A, GL_DEPTH24_STENCIL8 = 0x88F0, GL_FRAMEBUFFER_COMPLETE = 0x8CD5,
              GL_LINEAR_MIPMAP_LINEAR = 0x2703;

    static volatile int s_viewMode; // index into ViewFraming.Modes (0 = WIDE); set by AttachView each launch
    int _warpProgram, _warpVao, _warpFbo, _warpTex, _warpDepth, _warpW, _warpH;
    int _warpExtentsLoc, _warpDLoc, _warpScaleLoc;
    bool _warpBroken;
    bool _gunWaitsForWarp; // Panini: the Prime gun is drawn after the warp (EndViewWarp), not in the 3D passes
    float _warpD, _warpHookD;
    Scene? _warpHookScene;

    static ViewMode CurrentViewMode => ViewFraming.Modes[s_viewMode];

    public static void AttachView(Intent? intent)
    {
        // WIDE unless the dev extra asks for another (a launch without it doesn't inherit an earlier dev launch's mode)
        s_viewMode = ViewFraming.Parse(intent?.GetStringExtra("view")) is ViewMode mode
            ? Array.IndexOf(ViewFraming.Modes, mode) : Array.IndexOf(ViewFraming.Modes, ViewMode.Wide);
        ResetViewHooks();
        Log.Info("MPHView", "view: " + ViewFraming.Label(CurrentViewMode));
    }

    public static void ResetViewHooks()
    {
        Scene.HostFramingAspect = 0;
        Scene.HostScreenWarp = null;
    }

    // UI thread, the touch overlay (MatchTouchOverlay.cs) relabels JUMP / FIRE while a dialog is up:
    // 0 none, 1 an OK-type dialog, 2 YES / NO
    public int TouchDialog
    {
        get
        {
            var host = _host;
            return host == null || !host.DialogPaused ? 0 : host.Dialog == MphRead.Entities.DialogType.YesNo ? 2 : 1;
        }
    }

    // UI thread, the touch overlay: MISSILE reads POWER BEAM while missiles are selected (R1 toggles the two;
    // BuildInput). A read of the game thread's weapon: a frame stale at worst.
    public bool TouchMissileSelected => _host?.Player.CurrentWeapon == MphRead.BeamType.Missile;

    // a new GL context took the old objects with it
    public void ViewGlReset()
    {
        _warpProgram = _warpVao = _warpFbo = _warpTex = _warpDepth = 0;
        _warpW = _warpH = 0;
        _warpBroken = false;
    }

    // Render, after the frame's clear, before the 3D passes. Applies the mode (the projection and HUD hooks take effect in
    // the next sim step) and, for Panini, sends the 3D passes to the offscreen target. True = call EndViewWarp after them.
    bool BeginViewWarp(Scene scene)
    {
        ViewMode mode = ShipOpen ? ViewMode.Wide : CurrentViewMode;
        Scene.HostFramingAspect = ViewFraming.FramingAspect(mode);
        float d = _warpBroken ? 0 : ViewFraming.PaniniD(mode);
        if (d != _warpHookD || scene != _warpHookScene) // a new host (ship trip, new planet) brings a new scene
        {
            // the HUD's 3D-placed markers follow the picture; the warp is rebuilt from the projection each call, so the
            // Imperialist's zoom (a narrower view, a weaker warp) stays matched
            Scene.HostScreenWarp = d > 0 ? ndc => PaniniWarp.FromProjection(scene.PerspectiveMatrix, d).RenderToOutput(ndc) : null;
            _warpHookD = d;
            _warpHookScene = scene;
        }
        if (d <= 0 || _width <= 0 || _height <= 0)
        {
            return false;
        }
        // the warp magnifies the centre by 1 / CropScale at MPH's normal 78-degree view (every hunter's NormalFov is 39);
        // render at that scale so the centre keeps 1:1 pixels (zooming in magnifies less)
        float aspect = _width / (float)_height;
        float scale = 1 / ViewFraming.CropScale(MathF.Tan(39 * MathF.PI / 180) * aspect, d);
        int w = (int)MathF.Round(_width * scale), h = (int)MathF.Round(_height * scale);
        if (_warpProgram == 0)
        {
            _warpProgram = Link(WarpVertexShader, WarpFragmentShader);
            _warpExtentsLoc = GLES30.GlGetUniformLocation(_warpProgram, "extents");
            _warpDLoc = GLES30.GlGetUniformLocation(_warpProgram, "d");
            _warpScaleLoc = GLES30.GlGetUniformLocation(_warpProgram, "scale");
            GLES30.GlUseProgram(_warpProgram);
            GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_warpProgram, "tex"), 0);
            GLES30.GlUseProgram(_program);
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); // no attributes: the vertex shader makes its triangle from gl_VertexID
            _warpVao = ids[0];
        }
        if (w != _warpW || h != _warpH)
        {
            if (!AllocWarpTarget(w, h))
            {
                _warpBroken = true; // stay on the plain picture rather than a black one
                return false;
            }
        }
        _warpD = d;
        _gunWaitsForWarp = true;
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _warpFbo);
        GLES30.GlViewport(0, 0, w, h);
        Color4 clear = scene.ClearColor;
        GLES30.GlClearColor(clear.R, clear.G, clear.B, 1);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit | GLES30.GlStencilBufferBit);
        return true;
    }

    bool AllocWarpTarget(int w, int h)
    {
        var ids = new int[1];
        if (_warpFbo == 0)
        {
            GLES30.GlGenFramebuffers(1, ids, 0); _warpFbo = ids[0];
            GLES30.GlGenRenderbuffers(1, ids, 0); _warpDepth = ids[0];
        }
        if (_warpTex != 0)
        {
            GLES30.GlDeleteTextures(1, new[] { _warpTex }, 0); // a fresh texture: its old mip chain has the old size
        }
        GLES30.GlGenTextures(1, ids, 0); _warpTex = ids[0];
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _warpTex);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (Java.Nio.Buffer?)null);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GL_LINEAR_MIPMAP_LINEAR);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d); // allocates the chain once; refilled every frame in EndViewWarp
        GLES30.GlBindRenderbuffer(GL_RENDERBUFFER, _warpDepth);
        // depth + stencil: MphRead's translucency passes sort overlapping surfaces with the stencil (as the screen's does)
        GLES30.GlRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH24_STENCIL8, w, h);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _warpFbo);
        GLES30.GlFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GLES30.GlTexture2d, _warpTex, 0);
        GLES30.GlFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, _warpDepth);
        int status = GLES30.GlCheckFramebufferStatus(GL_FRAMEBUFFER);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            Log.Error("MPHView", $"warp target {w}x{h} incomplete (0x{status:X}); Panini off");
            return false;
        }
        _warpW = w;
        _warpH = h;
        Log.Info("MPHView", $"warp target {w}x{h} for the {_width}x{_height} screen");
        return true;
    }

    // Render, after the 3D passes and before the HUD: the offscreen picture through the Panini warp onto the screen
    void EndViewWarp(Scene scene)
    {
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        var warp = PaniniWarp.FromProjection(scene.PerspectiveMatrix, _warpD);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlUseProgram(_warpProgram);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _warpTex);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlBindSampler(0, 0); // the 3D passes leave a nearest-filter sampler object on unit 0
        GLES30.GlUniform2f(_warpExtentsLoc, warp.TanX, warp.TanY);
        GLES30.GlUniform1f(_warpDLoc, warp.D);
        GLES30.GlUniform1f(_warpScaleLoc, warp.Scale);
        GLES30.GlBindVertexArray(_warpVao);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        GLES30.GlBindVertexArray(0);
        // back to Render's state for the HUD pass
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlEnable(GLES30.GlDepthTest);
        _gunWaitsForWarp = false;
        if (_gunActive)
        {
            // the gun unwarped at its vanilla size, over the picture (the screen's depth is still clear: the 3D went to
            // the offscreen target), as modern games draw viewmodels
            GLES30.GlDepthMask(true);
            GLES30.GlDepthFunc(GLES30.GlLess);
            DrawPrimeGun(scene, GLES30.GlLess);
        }
    }

    // DrawPrimeGun: in Panini the 3D passes skip the gun; EndViewWarp draws it
    bool GunWaitsForWarp => _gunWaitsForWarp;

    // DrawPrimeGun: the gun's own projection, the one Wide uses, so no mode resizes it (zoom still applies, as in vanilla)
    Matrix4 GunProjection(Scene scene)
    {
        if (Scene.HostFramingAspect <= 0)
        {
            return scene.PerspectiveMatrix; // Wide and Panini render with MphRead's own projection
        }
        float fov = _host!.Player.CameraInfo.Fov > 0 ? _host.Player.CameraInfo.Fov : ViewFraming.MphFovY;
        return ViewFraming.VanillaProjection(scene.PerspectiveMatrix, fov, _width / (float)_height);
    }

    const string WarpVertexShader = @"#version 300 es
out vec2 v_uv;
void main()
{
    // one triangle covering the screen
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    v_uv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
";

    // output NDC -> Panini plane -> rectilinear tangent coords -> render NDC: ViewFraming.PaniniToRect, line for line
    const string WarpFragmentShader = @"#version 300 es
precision highp float;
uniform sampler2D tex;
uniform vec2 extents; // tangents of the render's half-angles (x, y)
uniform float d;      // Panini strength
uniform float scale;  // crop to fit (ViewFraming.CropScale)
in vec2 v_uv;
out vec4 color;
void main()
{
    vec2 p = (v_uv * 2.0 - 1.0) * extents * scale;
    float viewDist = 1.0 + d;
    float hypSq = p.x * p.x + viewDist * viewDist;
    float isectD = p.x * d;
    float qz = (-isectD * p.x + viewDist * sqrt(hypSq - isectD * isectD)) / hypSq;
    float t = (qz + d) / viewDist;
    vec2 ndc = p * (t / qz) / extents;
    color = vec4(texture(tex, ndc * 0.5 + 0.5).rgb, 1.0);
}
";
}
