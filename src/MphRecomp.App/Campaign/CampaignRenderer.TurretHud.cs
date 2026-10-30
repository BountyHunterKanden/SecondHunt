using System;
using System.Diagnostics;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Shooting Gallery's turret: the seated player's own HUD (docs/SHOOTING_GALLERY_TURRET.md section 9.1), Echoes'
// FRME_TurretHud4Combat (NoARAM.pak) drawn over the normal HUD while the local player mans the turret. The frame's
// widgets as the FRME places them, seen from its camera (fov 65, at y -19 looking +y: x right, z up), every widget
// additive (SRC_ALPHA, ONE):
//   model_frame (embedded model 1): the title frame's two halves at (-0.2142, 9.7258), scale (-1.1161, 0.7092), green
//   model_reticle (embedded model 0): four quarters of side 2.1952 about the centre
//   energybart01_hullenergy (ENRG under basewidget_energystuff): the turret's health, x 8.5 t - 4.25, z 0..0.4 at
//     9.6979; filled (0, 1, 0) at 99 a second, the shadow (0, 0.211, 0) after 0.7 s at 20 a second, empty not drawn
//   textpane_title: 'Turret' in its own font (baked at import: EchoesTurret.HudTitleFile), centred at
//     (-0.069, 10.4665), 7.6504 / 167 units a font pixel times the pane's scale 1.5466, colour (0.376, 0.996, 0.286)
internal sealed partial class CampaignRenderer
{
    const uint TurretTitleTex = 0x7469746c;   // EchoesTurret.HudTitleFile's key in _fxTexFile (no Echoes id)
    const float TurretHudCamY = 19, TurretHudFovY = 65;
    const float TurretBarFillSpeed = 99, TurretBarShadowSpeed = 20, TurretBarShadowDelay = 0.7f;

    float _turretBarFill = -1, _turretBarShadow, _turretBarWait;
    readonly Stopwatch _turretHudClock = new();

    // after DrawHud (CampaignRenderer.cs)
    void DrawTurretHud(Scene scene)
    {
        if (_host == null || scene != _host.Scene || _turretFx is not { Look.Hud: { } hud } lib) return;
        EchoesTurretEntity? t = NetWorld.TurretOf(_host.Scene);
        PlayerEntity? main = PlayerEntity.Main;
        if (t == null || main == null || t.Occupant != main || t.State != EchoesTurretEntity.TurretState.Active)
        {
            _turretBarFill = -1;
            _turretHudClock.Reset();
            return;
        }
        float dt = _turretHudClock.IsRunning ? Math.Min((float)_turretHudClock.Elapsed.TotalSeconds, 0.1f) : 0;
        _turretHudClock.Restart();
        TurretBar(t.Hp, dt);
        if (!FxGl()) return;
        if (hud.TitleWidth > 0) _fxTexFile.TryAdd(TurretTitleTex, Path.Combine(lib.Dir, EchoesTurret.HudTitleFile));

        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindSampler(0, 0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);

        float k = 1 / (TurretHudCamY * MathF.Tan(MathHelper.DegreesToRadians(TurretHudFovY) / 2));
        float kx = k * _height / Math.Max(1f, _width);
        var green = new Vector4(0, 1, 0, 1);
        var white = Vector4.One;

        // the frame: two halves, each a quad of the model's own vertices
        int n = 0;
        const float fx = -0.2142f, fz = 9.7258f, sx = -1.1161f, sz = 0.7092f, h = 1.9075f, u0 = 0.9891f, u1 = 0.0109f;
        HudQuad(ref n, kx, k, green,
            (fx, fz + sz * h, u0, 0), (fx + sx * 6.1282f, fz + sz * h, u1, 0),
            (fx + sx * 4.2208f, fz - sz * h, u1, 1), (fx, fz - sz * h, u0, 1));
        HudQuad(ref n, kx, k, green,
            (fx, fz + sz * h, u0, 0), (fx - sx * 6.1282f, fz + sz * h, u1, 0),
            (fx - sx * 4.6921f, fz - sz * h, u1, 1), (fx, fz - sz * h, u0, 1));
        DrawRun(hud.Frame, true, n);

        // the reticle: the quarter texture in each quarter (u 1 - |z| / side, v |x| / side)
        n = 0;
        const float r = 2.1952f;
        for (int q = 0; q < 4; q++)
        {
            float qx = (q & 1) == 0 ? r : -r, qz = (q & 2) == 0 ? r : -r;
            HudQuad(ref n, kx, k, white, (0, 0, 1, 0), (qx, 0, 1, 1), (qx, qz, 0, 1), (0, qz, 0, 0));
        }
        DrawRun(hud.Reticle, true, n);

        // the hull energy bar: the shadow behind, then the filled part
        float full = Math.Max(1, t.FullHp);
        float fill = Math.Clamp(_turretBarFill / full, 0, 1), shadow = Math.Clamp(_turretBarShadow / full, 0, 1);
        const float bz = 1.032f + 8.6659f, bh = 0.4f;
        n = 0;
        if (shadow > fill) BarQuad(ref n, kx, k, fill, shadow, bz, bh, new Vector4(0, 0.211f, 0, 1));
        if (fill > 0) BarQuad(ref n, kx, k, 0, fill, bz, bh, green);
        DrawRun(hud.Bar, true, n);

        // the title
        if (hud.TitleWidth > 0)
        {
            float px = 7.6504f / 167 * 1.5466f, hw = hud.TitleWidth * px / 2, hh = hud.TitleHeight * px / 2;
            const float tx = -0.069f, tz = 10.4665f;
            n = 0;
            HudQuad(ref n, kx, k, new Vector4(0.376f, 0.996f, 0.286f, 1),
                (tx - hw, tz + hh, 0, 0), (tx + hw, tz + hh, 1, 0), (tx + hw, tz - hh, 1, 1), (tx - hw, tz - hh, 0, 1));
            DrawRun(TurretTitleTex, true, n);
        }

        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlBindSampler(0, _samplers[0]);
        GLES30.GlBindVertexArray(0);
        GLES30.GlUseProgram(_program);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    // CAuiEnergyBarT01: the filled part follows the energy down at its drain speed (a rise shows at once); the shadow
    // waits its delay after the last drop, then drains to the filled part at its own speed
    void TurretBar(int hp, float dt)
    {
        if (_turretBarFill < 0)
        {
            _turretBarFill = _turretBarShadow = hp;
            _turretBarWait = 0;
        }
        else if (hp > _turretBarFill)
        {
            _turretBarFill = hp;
            _turretBarShadow = Math.Max(_turretBarShadow, hp);
        }
        else if (hp < _turretBarFill)
        {
            _turretBarFill = Math.Max(hp, _turretBarFill - TurretBarFillSpeed * dt);
            _turretBarWait = TurretBarShadowDelay;
        }
        else if (_turretBarWait > 0)
        {
            _turretBarWait -= dt;
        }
        else
        {
            _turretBarShadow = Math.Max(_turretBarFill, _turretBarShadow - TurretBarShadowSpeed * dt);
        }
    }

    void BarQuad(ref int n, float kx, float k, float from, float to, float z, float h, Vector4 col)
    {
        float x0 = 8.5f * from - 4.25f, x1 = 8.5f * to - 4.25f;
        HudQuad(ref n, kx, k, col, (x0, z + h, from, 0), (x1, z + h, to, 0), (x1, z, to, 1), (x0, z, from, 1));
    }

    // a quad of the frame's plane (x, z, u, v) in clip space, as two triangles into _fxVerts
    void HudQuad(ref int n, float kx, float k, Vector4 col, (float X, float Z, float U, float V) a, (float X, float Z, float U, float V) b,
        (float X, float Z, float U, float V) c, (float X, float Z, float U, float V) d)
    {
        HudVert(ref n, kx, k, a, col); HudVert(ref n, kx, k, b, col); HudVert(ref n, kx, k, c, col);
        HudVert(ref n, kx, k, a, col); HudVert(ref n, kx, k, c, col); HudVert(ref n, kx, k, d, col);
    }

    void HudVert(ref int n, float kx, float k, (float X, float Z, float U, float V) p, Vector4 col)
    {
        int o = n * 10;
        _fxVerts[o] = p.X * kx; _fxVerts[o + 1] = p.Z * k; _fxVerts[o + 2] = 0; _fxVerts[o + 3] = 1;
        _fxVerts[o + 4] = p.U; _fxVerts[o + 5] = p.V;
        _fxVerts[o + 6] = col.X; _fxVerts[o + 7] = col.Y; _fxVerts[o + 8] = col.Z; _fxVerts[o + 9] = col.W;
        n++;
    }
}
