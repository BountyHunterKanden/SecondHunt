using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// the Echoes grapple point icon (docs/GRAPPLE.md G3): a ring with four ticks on every grapple point in sight, drawn in
// our own style (no Echoes art). Its look follows Echoes' tweak (mp2tweak ntwk_dump.txt, Targeting): the point a press
// would grab now is drawn at scale 1 in GrappleIconColor (0.2941, 0.4941, 0.6392, 1), every other point in sight at
// scale 0.5 in GrappleIconColorInactive (1, 0.6196, 0.1569, 0.251); a point eases between the two over a tenth of a
// second. While she hangs on a point only that point shows, in the active look.
internal sealed partial class CampaignRenderer
{
    const float GrappleIconRadius = 0.055f;   // the active ring's outer radius, a fraction of the view's half height
    const float GrappleIconEase = 10;        // per second: 0.1 s from one look to the other
    const int GrappleIconSegments = 20;

    readonly Dictionary<GrapplePointEntity, float> _grappleIconActive = new();
    readonly Stopwatch _grappleHudClock = new();
    static readonly Vector4 GrappleIconOn = new(0.2941f, 0.4941f, 0.6392f, 1);
    static readonly Vector4 GrappleIconOff = new(1, 0.6196f, 0.1569f, 0.251f);

    // after DrawTurretHud (CampaignRenderer.cs)
    void DrawGrappleHud(Scene scene)
    {
        List<GrapplePointEntity>? points = PlayerEntity.HostGrapplePoints;
        PlayerEntity? main = PlayerEntity.Main;
        if (_host == null || scene != _host.Scene || points == null || main == null || main.Health <= 0)
        {
            _grappleIconActive.Clear();
            _grappleHudClock.Reset();
            return;
        }
        float dt = _grappleHudClock.IsRunning ? Math.Min((float)_grappleHudClock.Elapsed.TotalSeconds, 0.1f) : 0;
        _grappleHudClock.Restart();

        GrapplePointEntity? held = main.Grapple != PlayerEntity.GrappleState.None ? main.GrapplePoint : null;
        GrapplePointEntity? pick = held ?? main.HostGrapplePick();
        if (!FxGl()) return;

        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        float aspect = _height / Math.Max(1f, _width);
        bool begun = false;
        foreach (GrapplePointEntity p in points)
        {
            bool shown = held != null ? p == held : p.Usable && main.HostGrappleSees(p);
            if (!shown)
            {
                _grappleIconActive.Remove(p);
                continue;
            }
            Vector4 c = new Vector4(p.Position, 1) * vp;
            if (c.W <= 0.01f) continue;
            float x = c.X / c.W, y = c.Y / c.W;
            if (x < -1.1f || x > 1.1f || y < -1.1f || y > 1.1f) continue;

            float goal = p == pick ? 1 : 0;
            float t = _grappleIconActive.TryGetValue(p, out float was) ? was : goal;
            t = goal > t ? Math.Min(goal, t + GrappleIconEase * dt) : Math.Max(goal, t - GrappleIconEase * dt);
            _grappleIconActive[p] = t;

            if (!begun)
            {
                begun = true;
                GLES30.GlDisable(GLES30.GlDepthTest);
                GLES30.GlUseProgram(_fxProg);
                GLES30.GlBindVertexArray(_fxVao);
                GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
                GLES30.GlActiveTexture(GLES30.GlTexture0);
                GLES30.GlBindSampler(0, 0);
                GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
                GLES30.GlEnable(GLES30.GlBlend);
            }
            Vector4 col = Vector4.Lerp(GrappleIconOff, GrappleIconOn, t);
            float r = GrappleIconRadius * (0.5f + 0.5f * t);
            int n = 0;
            GrappleIcon(ref n, x, y, r, aspect, col);
            DrawRun(0, true, n, white: true);
        }
        if (!begun) return;
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlBindSampler(0, _samplers[0]);
        GLES30.GlBindVertexArray(0);
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    // the ring (inner edge 0.72 of the outer) and four ticks pointing in from outside it, in clip space about (x, y)
    void GrappleIcon(ref int n, float x, float y, float r, float aspect, Vector4 col)
    {
        float ri = r * 0.72f;
        for (int i = 0; i < GrappleIconSegments; i++)
        {
            float a0 = MathF.Tau * i / GrappleIconSegments, a1 = MathF.Tau * (i + 1) / GrappleIconSegments;
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0), c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
            IconQuad(ref n, col, (x + c0 * r * aspect, y + s0 * r), (x + c1 * r * aspect, y + s1 * r),
                (x + c1 * ri * aspect, y + s1 * ri), (x + c0 * ri * aspect, y + s0 * ri));
        }
        float w = r * 0.12f, t0 = r * 1.15f, t1 = r * 1.6f;
        for (int q = 0; q < 4; q++)
        {
            float dx = q switch { 0 => 1, 2 => -1, _ => 0 }, dy = q switch { 1 => 1, 3 => -1, _ => 0 };
            float px = -dy, py = dx;
            IconQuad(ref n, col,
                (x + (dx * t0 + px * w) * aspect, y + dy * t0 + py * w), (x + (dx * t1 + px * w) * aspect, y + dy * t1 + py * w),
                (x + (dx * t1 - px * w) * aspect, y + dy * t1 - py * w), (x + (dx * t0 - px * w) * aspect, y + dy * t0 - py * w));
        }
    }

    void IconQuad(ref int n, Vector4 col, (float X, float Y) a, (float X, float Y) b, (float X, float Y) c, (float X, float Y) d)
    {
        IconVert(ref n, a, col); IconVert(ref n, b, col); IconVert(ref n, c, col);
        IconVert(ref n, a, col); IconVert(ref n, c, col); IconVert(ref n, d, col);
    }

    void IconVert(ref int n, (float X, float Y) p, Vector4 col)
    {
        int o = n * 10;
        _fxVerts[o] = p.X; _fxVerts[o + 1] = p.Y; _fxVerts[o + 2] = 0; _fxVerts[o + 3] = 1;
        _fxVerts[o + 4] = 0.5f; _fxVerts[o + 5] = 0.5f;
        _fxVerts[o + 6] = col.X; _fxVerts[o + 7] = col.Y; _fxVerts[o + 8] = col.Z; _fxVerts[o + 9] = col.W;
        n++;
    }
}
