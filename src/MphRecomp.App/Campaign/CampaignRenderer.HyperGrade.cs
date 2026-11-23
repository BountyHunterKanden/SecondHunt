using System;
using MphRead;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Corruption's Hyper Mode screen grade, on the Hyper Beam's effects (owner 2026-11-14: "whatever path gets us the identical
// orb"). In Hyper Mode Corruption draws the frame (the world, the orb, the gun) and then grades the whole EFB (Dolphin FIFO
// logs of the charged orb and of a frame without it, RM3E01, 11-14: hyper_orb.dff draws 7683-7688, no_orb.dff 9312-9317):
// - an EFB copy to an I8 (luma) texture: Y = (66R + 129G + 25B + 128) / 256 + 16, the copy's vertical filter 21/22/21 of
//   64 over the lines above and below (BP 0x53/0x54);
// - that texture read back as C8 through a TLUT (IA8 at tmem 0x40000, 256 entries, the same in both logs):
//   T = clamp(floor((Y - 16) * 510 / 219), 0, 255), 0 to Y 16 and 255 from Y 126;
// - a full-screen quad, TEV clamp(T + K0) with K0 = 124/255 (BP 0xe0/0xe1, KCSEL 12), blend src * DSTCLR + dst * SRCCLR
//   (BP 0x41 0x24d): the frame times 2 * clamp(T + 124/255), so dark pixels x0.97 and anything with Y over ~126 doubled.
// (Before it, a vignette ring of the luma copy to black at the screen's edges: Corruption's visor look, not drawn here.)
// Our world isn't graded (it's MPH's), so the grade is applied to what the Hyper Beam's effects add: the frame under them is
// copied before they draw (S) and after (F), and the area they cover becomes S + gain(F) * F - gain(S) * S, clamped: the
// effects' share of the frame with Corruption's gain, the world's pixels as they were. Off: -hyperbeamhide grade.
internal sealed partial class CampaignRenderer
{
    const float HyperGradeLines = 448; // Corruption's EFB height: the copy filter's line spacing
    readonly int[] _hgFbo = new int[2], _hgTex = new int[2];
    int _hgProg, _hgW, _hgH, _hgOld, _hgVx, _hgVy;
    bool _hgOn, _hgTrack, _hgAll;
    Vector2 _hgMin, _hgMax;

    const string HyperGradeFrag = "#version 300 es\nprecision highp float;\nuniform sampler2D uS;\nuniform sampler2D uF;\n"
        + "uniform float uDy;\nin vec2 vUv;\nout vec4 fragColor;\n"
        + "float gain(sampler2D t) {\n"
        + "  vec3 c = (21.0 * texture(t, vUv - vec2(0.0, uDy)).rgb + 22.0 * texture(t, vUv).rgb + 21.0 * texture(t, vUv + vec2(0.0, uDy)).rgb) * (255.0 / 64.0);\n"
        + "  float y = floor((66.0 * c.r + 129.0 * c.g + 25.0 * c.b + 128.0) / 256.0) + 16.0;\n"
        + "  float l = clamp(floor((y - 16.0) * 510.0 / 219.0), 0.0, 255.0);\n"
        + "  return 2.0 * min(l + 124.0, 255.0) / 255.0;\n}\n"
        + "void main() {\n  vec4 s = texture(uS, vUv), f = texture(uF, vUv);\n"
        + "  fragColor = vec4(clamp(s.rgb + gain(uF) * f.rgb - gain(uS) * s.rgb, 0.0, 1.0), f.a);\n}\n";

    // DrawSkins (MatchSkins.cs), before the Hyper Beam's effects draw: the frame under them (S), and their verts' extent
    // tracked from here (Vert); false: nothing to grade (or it failed), drawn plain
    bool HyperGradeBegin(Scene scene, SuitPass pass)
    {
        _hgOn = _hgTrack = false;
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || HyperHidden("grade")) return false;
        bool any = _hbBolts.Count > 0 || _hbDying.Count > 0;
        foreach (DroneFxLive fx in _fxLive)
        {
            if (any) break;
            any = fx.Graded;
        }
        if (!any || !FxGl()) return false;
        if (!_u.ContainsKey("hypergrade"))
        {
            _hgProg = _hgW = _hgH = 0;
            _hgFbo[0] = _hgFbo[1] = _hgTex[0] = _hgTex[1] = 0;
            _u["hypergrade"] = 1;
        }
        if (_hgProg == 0)
        {
            if ((_hgProg = CampaignGlCache.Program(FxVert, HyperGradeFrag, "Hyper Beam grade")) == 0) return false;
            // fixed uniforms, set once: a per-frame lookup makes the CPU wait for the GPU
            GLES30.GlUseProgram(_hgProg);
            GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_hgProg, "uS"), 0);
            GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_hgProg, "uF"), 1);
            GLES30.GlUniform1f(GLES30.GlGetUniformLocation(_hgProg, "uDy"), 1 / HyperGradeLines);
            GLES30.GlUseProgram(_program);
        }
        var v = new int[4];
        GLES30.GlGetIntegerv(0x8CA6, v, 0); // GL_DRAW_FRAMEBUFFER_BINDING
        _hgOld = v[0];
        GLES30.GlGetIntegerv(0x0BA2, v, 0); // GL_VIEWPORT
        (_hgVx, _hgVy) = (v[0], v[1]);
        if (v[2] < 16 || v[3] < 16) return false;
        if (v[2] != _hgW || v[3] != _hgH) HyperGradeAllocate(v[2], v[3]);
        if (_hgFbo[1] == 0) return false;
        HyperGradeCopy(0, 0, 0, _hgW, _hgH);
        _hgMin = new Vector2(float.MaxValue);
        _hgMax = new Vector2(float.MinValue);
        _hgAll = false;
        return _hgOn = _hgTrack = true;
    }

    // Vert, while tracking: a vert's place on the screen (one behind the camera: the whole screen)
    void HyperGradeTrack(Vector4 c)
    {
        if (c.W <= 1e-5f)
        {
            _hgAll = true;
            return;
        }
        var at = new Vector2(c.X / c.W, c.Y / c.W);
        _hgMin = Vector2.ComponentMin(_hgMin, at);
        _hgMax = Vector2.ComponentMax(_hgMax, at);
    }

    // after the Hyper Beam's effects: the frame with them (F) and the graded area drawn over them
    void HyperGradeEnd()
    {
        _hgTrack = false;
        if (!_hgOn) return;
        _hgOn = false;
        int x0 = 0, y0 = 0, x1 = _hgW, y1 = _hgH;
        if (!_hgAll)
        {
            if (_hgMin.X > _hgMax.X) return; // nothing drawn
            x0 = Math.Clamp((int)MathF.Floor((_hgMin.X * 0.5f + 0.5f) * _hgW) - 1, 0, _hgW);
            x1 = Math.Clamp((int)MathF.Ceiling((_hgMax.X * 0.5f + 0.5f) * _hgW) + 1, 0, _hgW);
            y0 = Math.Clamp((int)MathF.Floor((_hgMin.Y * 0.5f + 0.5f) * _hgH) - 1, 0, _hgH);
            y1 = Math.Clamp((int)MathF.Ceiling((_hgMax.Y * 0.5f + 0.5f) * _hgH) + 1, 0, _hgH);
            if (x0 >= x1 || y0 >= y1) return;
        }
        // F a little past the area: the copy filter reads a Corruption line above and below
        int m = (int)MathF.Ceiling(_hgH / HyperGradeLines) + 1;
        HyperGradeCopy(1, x0, Math.Max(0, y0 - m), x1, Math.Min(_hgH, y1 + m));
        // the area: S + gain(F) F - gain(S) S, written over it
        float u0 = (float)x0 / _hgW, u1 = (float)x1 / _hgW, v0 = (float)y0 / _hgH, v1 = (float)y1 / _hgH;
        int n = 0;
        void Corner(ref int n, float u, float v)
        {
            int o = n++ * 10;
            _fxVerts[o] = u * 2 - 1; _fxVerts[o + 1] = v * 2 - 1; _fxVerts[o + 2] = 0; _fxVerts[o + 3] = 1;
            _fxVerts[o + 4] = u; _fxVerts[o + 5] = v;
            _fxVerts[o + 6] = _fxVerts[o + 7] = _fxVerts[o + 8] = _fxVerts[o + 9] = 1;
        }
        Corner(ref n, u0, v0); Corner(ref n, u1, v0); Corner(ref n, u0, v1);
        Corner(ref n, u0, v1); Corner(ref n, u1, v0); Corner(ref n, u1, v1);
        var t = new int[1];
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlGetIntegerv(0x8069, t, 0); // GL_TEXTURE_BINDING_2D: unit 1's, given back
        GLES30.GlBindSampler(1, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _hgTex[1]);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindSampler(0, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _hgTex[0]);
        GLES30.GlUseProgram(_hgProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, n * 10 * 4, _fxVerts, GLES30.GlDynamicDraw);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, n);
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, t[0]);
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
    }

    // the frame's x0..x1, y0..y1 (from the viewport's corner, rows bottom up) -> copy k at the same place
    void HyperGradeCopy(int k, int x0, int y0, int x1, int y1)
    {
        var s = new int[1];
        GLES30.GlGetIntegerv(GLES30.GlScissorTest, s, 0);
        if (s[0] != 0) GLES30.GlDisable(GLES30.GlScissorTest);
        GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _hgOld);
        GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _hgFbo[k]);
        GLES30.GlBlitFramebuffer(_hgVx + x0, _hgVy + y0, _hgVx + x1, _hgVy + y1, x0, y0, x1, y1, GLES30.GlColorBufferBit, GLES30.GlNearest);
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _hgOld);
        if (s[0] != 0) GLES30.GlEnable(GLES30.GlScissorTest);
    }

    void HyperGradeAllocate(int w, int h)
    {
        if (_hgFbo[0] != 0)
        {
            GLES30.GlDeleteFramebuffers(2, _hgFbo, 0);
            GLES30.GlDeleteTextures(2, _hgTex, 0);
        }
        GLES30.GlGenFramebuffers(2, _hgFbo, 0);
        GLES30.GlGenTextures(2, _hgTex, 0);
        for (int i = 0; i < 2; i++)
        {
            GLES30.GlBindTexture(GLES30.GlTexture2d, _hgTex[i]);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (byte[]?)null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _hgFbo[i]);
            GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlTexture2d, _hgTex[i], 0);
            if (GLES30.GlCheckFramebufferStatus(GLES30.GlFramebuffer) != GLES30.GlFramebufferComplete) _hgFbo[1] = 0;
        }
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _hgOld);
        GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
        (_hgW, _hgH) = (w, h);
    }
}
