using MphRead;
using MphRead.Entities;

namespace MphRecomp.App;

// The HUD disruption wobble: a disrupting hit (Affliction.Disrupt) starts PlayerHud's disrupted state, and the picture's
// scan lines slide left and right in a moving wave for a couple of seconds (HudDisruptionFactor ramps up, holds, ramps
// down). MphRead's desktop renderer does it in its shift shader over the world (Renderer.cs, shift_table / shift_idx /
// lerp_fac / shift_fac, the table built at Renderer.cs:887); our renderer never drew it. Here, only while it runs: the
// world as drawn (after the view warp / render scale, before the HUD) is copied off the screen (as LeaveCapture does) and
// drawn back through the same per-line shift, the table's math done in the shader. Off it costs nothing.
internal sealed partial class CampaignRenderer
{
    int _dsProgram, _dsVao, _dsFbo, _dsTex, _dsW, _dsH;
    int _dsHeightLoc, _dsIdxLoc, _dsLerpLoc, _dsFacLoc;
    bool _dsBroken;

    // a new GL context took the old objects with it (ViewGlReset)
    void DisruptionGlReset()
    {
        _dsProgram = _dsVao = _dsFbo = _dsTex = 0;
        _dsW = _dsH = 0;
        _dsBroken = false;
    }

    // Render, after the 3D passes and EndViewWarp, before DrawHud (so the HUD and the white-out stay put, as in MphRead)
    void DrawDisruption(Scene scene)
    {
        if (_dsBroken || PlayerEntity.HostNoViewer) return;
        PlayerEntity owner = PlayerEntity.HudOwner;
        float fac = owner.HudDisruptionFactor;
        if (owner.HudDisruptedState == 0 || fac <= 0) return;
        // where the picture is (as HyperGrade reads it: the screen isn't always framebuffer 0 at 0,0); only while it runs
        var v = new int[4];
        GLES30.GlGetIntegerv(0x8CA6, v, 0); // GL_DRAW_FRAMEBUFFER_BINDING
        int screen = v[0];
        GLES30.GlGetIntegerv(0x0BA2, v, 0); // GL_VIEWPORT
        int vx = v[0], vy = v[1], w = v[2], h = v[3];
        if (w < 16 || h < 16) return;
        if (_dsProgram == 0)
        {
            _dsProgram = Link(WarpVertexShader, DisruptFragmentShader);
            if (_dsProgram == 0)
            {
                _dsBroken = true;
                return;
            }
            _dsHeightLoc = GLES30.GlGetUniformLocation(_dsProgram, "view_y_height");
            _dsIdxLoc = GLES30.GlGetUniformLocation(_dsProgram, "shift_idx");
            _dsLerpLoc = GLES30.GlGetUniformLocation(_dsProgram, "lerp_fac");
            _dsFacLoc = GLES30.GlGetUniformLocation(_dsProgram, "shift_fac");
            GLES30.GlUseProgram(_dsProgram);
            GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_dsProgram, "tex"), 0);
            var vao = new int[1];
            GLES30.GlGenVertexArrays(1, vao, 0); // no attributes: the triangle comes from gl_VertexID
            _dsVao = vao[0];
        }
        if (w != _dsW || h != _dsH)
        {
            var ids = new int[1];
            if (_dsTex != 0) GLES30.GlDeleteTextures(1, new[] { _dsTex }, 0);
            GLES30.GlGenTextures(1, ids, 0);
            _dsTex = ids[0];
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindTexture(GLES30.GlTexture2d, _dsTex);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (byte[]?)null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            if (_dsFbo == 0)
            {
                GLES30.GlGenFramebuffers(1, ids, 0);
                _dsFbo = ids[0];
            }
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _dsFbo);
            GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlTexture2d, _dsTex, 0);
            int status = GLES30.GlCheckFramebufferStatus(GLES30.GlFramebuffer);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, screen);
            if (status != GL_FRAMEBUFFER_COMPLETE)
            {
                Log.Error("MPHView", $"disruption copy {w}x{h} incomplete (0x{status:X}); wobble off");
                _dsBroken = true;
                return;
            }
            (_dsW, _dsH) = (w, h);
        }
        // the world as drawn, off the screen
        GLES30.GlGetIntegerv(GLES30.GlScissorTest, v, 0);
        bool scissor = v[0] != 0;
        if (scissor) GLES30.GlDisable(GLES30.GlScissorTest);
        GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, screen);
        GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _dsFbo);
        GLES30.GlBlitFramebuffer(vx, vy, vx + w, vy + h, 0, 0, w, h, GLES30.GlColorBufferBit, GLES30.GlNearest);
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, screen);
        GLES30.GlViewport(vx, vy, w, h);
        if (scissor) GLES30.GlEnable(GLES30.GlScissorTest);
        // back onto it, line by line shifted (MphRead: the wave steps at 30 Hz from its elapsed time, blended between steps)
        float div = scene.ElapsedTime * 30;
        int index = (int)div;
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDisable(GLES30.GlStencilTest);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlUseProgram(_dsProgram);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _dsTex);
        GLES30.GlBindSampler(0, 0); // the 3D passes leave a nearest-filter sampler object on unit 0
        GLES30.GlUniform2f(_dsHeightLoc, vy, h);
        GLES30.GlUniform1i(_dsIdxLoc, index & 63);
        GLES30.GlUniform1f(_dsLerpLoc, div - index);
        GLES30.GlUniform1f(_dsFacLoc, fac);
        GLES30.GlBindVertexArray(_dsVao);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        GLES30.GlBindVertexArray(0);
        // back to Render's state for the HUD pass
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    // MphRead's ShiftFragmentShader (Shaders.cs), its shift_table worked out per line: entry i is a triangle wave over 64,
    // -(val - 16) / 256 of the width, val = i & 31 rising then falling. One band per DS scan line (192), odd lines half a
    // wave ahead; off the picture's edge is black.
    const string DisruptFragmentShader = @"#version 300 es
precision highp float;
uniform sampler2D tex;
uniform vec2 view_y_height; // the viewport's bottom and height, in pixels
uniform int shift_idx;
uniform float lerp_fac;
uniform float shift_fac;
in vec2 v_uv;
out vec4 color;
float shift_at(int i)
{
    int val = (i & 32) != 0 ? 31 - (i & 31) : (i & 31);
    return -float(val - 16) / 256.0;
}
void main()
{
    float y = (gl_FragCoord.y - view_y_height.x) / view_y_height.y;
    int band = int((1.0 - y) * 192.0);
    int index = (band + shift_idx + (band & 1) * 32) & 63;
    float value = mix(shift_at(index), shift_at((index + 1) & 63), lerp_fac) * shift_fac;
    float x = v_uv.x + value;
    if (x < 0.0 || x > 1.0) {
        color = vec4(0.0, 0.0, 0.0, 1.0);
    }
    else {
        color = vec4(texture(tex, vec2(x, v_uv.y)).rgb, 1.0);
    }
}
";
}
