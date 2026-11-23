using System;
using MphRead;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// DEPTH OF FIELD on the menus' arena backdrop (owner 2026-11-21: "a depth of field setting in the menu background shot
// picker ... only if that depth of field will apply when the actual map is displayed live on menu"; then "get this
// instant like the maps"). A picked shot's focus, blur and range (MenuBackdropShots.DofFor; set in the dev MENU SHOT picker,
// CampaignRenderer.MenuShotPick.cs) are drawn as part of the backdrop's own offscreen pass (CampaignRenderScale.cs:
// the menus' screen has no depth buffer, so the backdrop always draws into a target): that target is made of textures,
// and in place of its blit a gather pass draws it onto the screen, sharp within RANGE of the focus distance (owner 11-21
// "yes add range"; 0: only at it) and softer away from that band
// (a golden-angle disc of taps, each on a mip of the picture that fits the tap spacing; nearer blurry surfaces spread
// over sharp ones behind them, not the other way round). It loads as the arena's own look does: the program through
// CampaignGlCache (its saved binary) and the target in BackdropBegin, at app start beside the scene's programs, so
// nothing is made once the menu is on screen. It lands before the backdrop's darkening and the menus' blur, so those
// have it too. Only the backdrop and the picker ever turn it on; gameplay never does, so no setting and no cost there.
// (PANINI view framing draws through its own target instead: no depth of field there.) logcat MPHView logs it on/off.
internal sealed partial class CampaignRenderer
{
    const int GL_DEPTH_STENCIL = 0x84F9, GL_UNSIGNED_INT_24_8 = 0x84FA;
    const int DofTaps = 32; // (48 looked the same at BLUR 5 and cost ~0.06 ms a tap more on PC, 11-21)

    int _dofProgram, _dofVao;
    int _dofPxLoc, _dofLinLoc, _dofBandLoc, _dofScaleLoc, _dofMaxRLoc;
    bool _dofBroken, _dofLogged;
    (float Focus, float Blur, float Range) _backdropDof; // the backdrop's shot's, found once (BackdropFinish)

    // this frame's depth of field: the picker's live values while picking, the backdrop's shot's; Blur 0 = none.
    // Logged when it turns on or off
    bool DofNow(out float focus, out float blur, out float range)
    {
        (focus, blur, range) = _menuShotPick && PhotoOpen ? (_menuShotFocus, _menuShotBlur, _menuShotRange)
            : _menuBackdrop ? _backdropDof
            : (MenuBackdropShots.DefaultFocus, 0, 0);
        bool on = blur > 0 && !_dofBroken;
        if (on != _dofLogged)
        {
            _dofLogged = on;
            Log.Info("MPHView", on ? $"depth of field on: focus {focus:0.0} blur {blur:0.#} range {range:0.#}" : "depth of field off");
        }
        return on;
    }

    // the gather pass's program (CampaignGlCache: the saved binary after the first launch ever) and its empty vertex
    // array; false if it didn't link (then never again: the plain picture)
    bool EnsureDof()
    {
        if (_dofProgram != 0) return true;
        if (_dofBroken) return false;
        _dofProgram = CampaignGlCache.Program(WarpVertexShader, DofFragmentShader, "depth of field");
        if (_dofProgram == 0)
        {
            Log.Error("MPHView", "depth of field program failed; depth of field off");
            _dofBroken = true;
            return false;
        }
        _dofPxLoc = GLES30.GlGetUniformLocation(_dofProgram, "px");
        _dofLinLoc = GLES30.GlGetUniformLocation(_dofProgram, "lin");
        _dofBandLoc = GLES30.GlGetUniformLocation(_dofProgram, "band");
        _dofScaleLoc = GLES30.GlGetUniformLocation(_dofProgram, "cocScale");
        _dofMaxRLoc = GLES30.GlGetUniformLocation(_dofProgram, "maxR");
        GLES30.GlUseProgram(_dofProgram);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_dofProgram, "tex"), 0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_dofProgram, "depthTex"), 1);
        GLES30.GlUseProgram(_program);
        if (_dofVao == 0)
        {
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); // no attributes: the vertex shader makes its triangle from gl_VertexID
            _dofVao = ids[0];
        }
        return true;
    }

    // EndRenderScale, depth of field on: the target's picture through the gather pass onto the screen (bound by the
    // caller), then Render's state for the HUD
    void DrawDof()
    {
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlUseProgram(_dofProgram);
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _rsDepthTex);
        GLES30.GlBindSampler(1, 0); // the 3D passes leave nearest-filter sampler objects on units 0 and 1
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _rsColorTex);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlBindSampler(0, 0);
        // the blur's size goes with the picture's height, so a shot looks the same on every screen: BLUR 10 = the far
        // background ~2.5% of the height across; nearer than half the focus it grows to twice that (at most 4%)
        float cocScale = _rsDofBlur * 0.0025f * _rsH;
        Matrix4 proj = _rsScene!.PerspectiveMatrix;
        GLES30.GlUniform2f(_dofPxLoc, 1f / _rsW, 1f / _rsH);
        GLES30.GlUniform2f(_dofLinLoc, proj.M33, proj.M43);
        GLES30.GlUniform2f(_dofBandLoc, Math.Max(0, _rsDofFocus - _rsDofRange), _rsDofFocus + _rsDofRange);
        GLES30.GlUniform1f(_dofScaleLoc, cocScale);
        GLES30.GlUniform1f(_dofMaxRLoc, Math.Max(1, Math.Min(cocScale * 2, 0.04f * _rsH)));
        GLES30.GlBindVertexArray(_dofVao);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    // the view distance from the depth buffer: OpenTK's projection (row vectors) gives ndc z = -M33 + M43 / distance.
    // A tap's blur radius in pixels (coc) is cocScale * |1 - focus / distance|, at most maxR, measured from the sharp band's
    // edge (band: focus - range, focus + range): 0 inside it, cocScale * (1 - far / distance) beyond it and
    // cocScale * (near / distance - 1) before it (range 0: the first, as before range). Each tap is weighed by
    // whether its blur reaches this pixel; one behind this pixel is held to twice this pixel's blur, so a sharp
    // subject's edge doesn't smear over the blurry background behind it, while a blurry one in front still spreads.
    static readonly string DofFragmentShader = @"#version 300 es
precision highp float;
uniform sampler2D tex;
uniform highp sampler2D depthTex;
uniform vec2 px;        // one pixel in uv
uniform vec2 lin;       // projection M33, M43
uniform vec2 band;      // the view distances that stay sharp: nearest, farthest
uniform float cocScale; // blur radius in pixels at an infinite distance
uniform float maxR;     // the largest blur radius in pixels
in vec2 v_uv;
out vec4 color;
const int N = " + DofTaps + @";
float dist(vec2 uv)
{
    return lin.y / (texture(depthTex, uv).r * 2.0 - 1.0 + lin.x);
}
float coc(float d)
{
    d = max(d, 0.001);
    return min(maxR, cocScale * max(0.0, max(1.0 - band.y / d, band.x / d - 1.0)));
}
void main()
{
    float cd = dist(v_uv);
    float cr = coc(cd);
    float spacing = maxR * sqrt(3.14159265 / float(N));
    vec3 sum = textureLod(tex, v_uv, log2(max(1.0, cr * 0.7))).rgb;
    float tot = 1.0;
    for (int i = 0; i < N; i++)
    {
        float r = maxR * sqrt((float(i) + 0.5) / float(N));
        float a = float(i) * 2.39996323;
        vec2 uv = v_uv + vec2(cos(a), sin(a)) * r * px;
        float sd = dist(uv);
        float sr = coc(sd);
        if (sd > cd) sr = min(sr, cr * 2.0);
        float m = smoothstep(r - min(r, spacing * 0.5), r + spacing * 0.5, sr);
        vec3 c = textureLod(tex, uv, log2(max(1.0, min(spacing, sr) * 0.6))).rgb;
        sum += mix(sum / tot, c, m);
        tot += 1.0;
    }
    color = vec4(sum / tot, 1.0);
}
";
}
