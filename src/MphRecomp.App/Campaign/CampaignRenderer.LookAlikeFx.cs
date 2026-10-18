using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Dark Samus look-alike's phazon glow (docs/CHARACTERS_AND_SKINS.md D5, look-alikes thread; owner 10-09: a lighter
// blue glow and shimmer, strongest on the shoulders, so she doesn't read as a blue Samus recolour). Her armour's shimmer
// is in her palette sets (LookAlikes.ShimmerSets, stepped by MatchSkins.SetSkinHooks); here, code-drawn glows ride her
// shoulder pads (MphRead's L/R_varias2_SDK nodes via PlayerEntity.HostTrackNodes): a pulsing light-blue glow with a white
// core and flickering motes circling it. Drawing only, no asset.
internal sealed partial class CampaignRenderer
{
    static readonly string[] PhazonNodes = { "L_varias2_SDK", "R_varias2_SDK" };
    const int PhazonMotes = 4;
    const float PhazonAhead = 0.32f; // the glows' centre out in front of the pad (about its size: it doesn't cut through them)
    const int PhazonHoldFrames = 4; // drawn while the body was posed this recently (not every draw frame poses it)
    readonly System.Collections.Generic.Dictionary<PlayerEntity, (int Poses, long Frame)> _phazonSeen = new();

    // the body was posed (drawn) in the last few frames: its tracked points are current
    bool PhazonShown(PlayerEntity p)
    {
        if (p.HostTrackNodes != PhazonNodes) return false;
        if (!_phazonSeen.TryGetValue(p, out var seen) || seen.Poses != p.HostTrackedPoses)
        {
            _phazonSeen[p] = seen = (p.HostTrackedPoses, p.HostTrackedPoses == 0 ? long.MinValue / 2 : _frame); // (never posed: not shown)
        }
        return _frame - seen.Frame <= PhazonHoldFrames;
    }

    // a phazon glow's colour on this seat: its blue, or on a team seat the team's colour (its white core kept)
    Vector4 PhazonColor(PlayerEntity p, float r, float g, float b, float a)
    {
        int i = p.SlotIndex;
        LookAlikes.Variant v = i >= 0 && i < _seatVariant.Length ? _seatVariant[i] : LookAlikes.Variant.First;
        if (v is not (LookAlikes.Variant.OrangeTeam or LookAlikes.Variant.GreenTeam)) return new Vector4(r, g, b, a);
        var c = LookAlikes.TeamColor(v, darkSamus: true);
        float w = MathF.Min(r, MathF.Min(g, b));
        return new Vector4(c.R + (1 - c.R) * w, c.G + (1 - c.G) * w, c.B + (1 - c.B) * w, a);
    }

    // DrawSkins (MatchSkins.cs), after DrawDroneFallback
    void DrawLookAlikeFx(Scene scene, SuitPass pass)
    {
        if (!_looksOn || pass != SuitPass.Additive || _host == null || scene != _host.Scene) return;
        bool any = false;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p != null && PhazonShown(p)) any = true;
        }
        if (!any || !FxGl() || !FallbackGl()) return;
        Vector3 cam = scene.CameraPosition;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        int n = 0;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || !PhazonShown(p)) continue;
            float a = p.HostBipedAlpha;
            if (p.Health <= 0 || p.IsAltForm || a <= 0.01f) continue;
            float t = (float)(_host.Scene.FrameCount % 100000) + p.SlotIndex * 37;
            for (int side = 0; side < PhazonNodes.Length; side++)
            {
                Vector3 pad = p.HostTrackedPoints[side];
                Vector3 toCam = cam - pad;
                // out in front of the pad, toward the camera (owner 10-09: the pad mustn't poke through the glow)
                Vector3 at = pad + (toCam.LengthSquared > 1e-6f ? toCam.Normalized() * PhazonAhead : Vector3.Zero);
                float ts = t + side * 23;
                float pulse = 0.85f + 0.15f * MathF.Sin(ts * 0.11f) * MathF.Sin(ts * 0.047f + 1);
                // (owner 10-09: dimmer than first built: between the v2 and v3 pictures)
                Glow(ref n, vp, right, up, at, 1.25f * pulse, PhazonColor(p, 0.30f, 0.62f, 1f, 0.31f * a));
                Glow(ref n, vp, right, up, at, 0.70f * pulse, PhazonColor(p, 0.45f, 0.82f, 1f, 0.41f * a));
                Glow(ref n, vp, right, up, at, 0.30f * pulse, PhazonColor(p, 0.80f, 0.95f, 1f, 0.38f * a));
                // motes: a few close around the pad in the camera's plane, each seen only as it flares (owner 10-09: kept on
                // the shoulders, not floating off her)
                for (int i = 0; i < PhazonMotes; i++)
                {
                    float ang = ts * 0.05f * (i % 2 == 0 ? 1 : -1.3f) + i * MathF.Tau / PhazonMotes;
                    float r = 0.18f + 0.045f * MathF.Sin(ts * 0.09f + i * 2.1f);
                    float f = MathF.Sin(ts * 0.21f + i * 1.7f);
                    float flare = f > 0 ? f * f : 0;
                    Vector3 mote = at + (right * MathF.Cos(ang) + up * MathF.Sin(ang)) * r;
                    Glow(ref n, vp, right, up, mote, 0.075f + 0.045f * flare, PhazonColor(p, 0.60f, 0.92f, 1f, (0.12f + 0.73f * flare) * a));
                }
            }
        }
        if (n == 0) return;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _fbGlow);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOne);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, n * 10 * 4, _fxVerts, GLES30.GlDynamicDraw);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, n);
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }
}
