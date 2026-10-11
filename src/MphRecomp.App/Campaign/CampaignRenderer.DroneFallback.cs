using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Scout Drone's two attacks on a device WITHOUT the MP1 import (docs/CHARACTERS_AND_SKINS.md D5, look-alikes thread):
// CampaignRenderer.DroneFx.cs draws and plays Prime's own effects and sounds from the import's fx/ files; with none
// (no drone body imported, or one without fx/), these fallbacks keep both attacks visible and audible from MPH's own
// sounds and effects drawn in code, on the sim's own timing and path. Drawing and sound only: nothing here touches the
// sim (no MphRead effect is spawned: those draw on the sim's random numbers).
//   - burst (fire): MphRead's Power Beam bolts and its shot sound per volley stay (PlayerScoutDrone); added: the unfold's
//     charge (Power Beam charge sound from its first frame, a charge glow at the drone growing through the unfold, held
//     while it fires)
//   - cutting laser (L2): Prime's beam is three plain tubes drawn in code (CDroneLaser), so the same tubes draw here in
//     place of MphRead's thin red stand-in line; a code-drawn burn glow where it lands; sounds: a wind-up (Power Beam
//     charge) from the clip's first frame, the Imperialist's shot as the beam comes on (frame 57, MphRead's own stand-in
//     sound), the Shock Coil's beam held while it sweeps, stopped as it goes off (frame 123)
internal sealed partial class CampaignRenderer
{
    sealed class DroneFallbackSeat
    {
        public int State = -1, LaserFrame = -1, Frame;
        public readonly SoundSource Sound = new();
        public int Charge = -1, Loop = -1; // the playing sounds (sfx ids), -1 none
    }

    readonly Dictionary<PlayerEntity, DroneFallbackSeat> _fbSeats = new();
    int _fbGlow; // a radial glow texture made in code (no asset)
    const int DroneLaserOnFrame = 57, DroneLaserOffFrame = 123, DroneUnfoldFrames = 32;
    const float DroneGunAhead = 0.3f, DroneGunSide = 0.22f; // the charge glows' place from the drone's centre (MPH units)

    // TickSkins (MatchSkins.cs), after TickDroneFx: every drone seat without Prime's fx
    void TickDroneFallback()
    {
        if (!_looksOn) return; // with the look-alikes (MatchSkins.ConfigureSkins; dev --es lookalikes 0 = off)
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            bool on = p != null && p.HostScoutDrone && p.IsAltForm && p.Health > 0 && DroneOf(p)?.Fx == null;
            if (!on)
            {
                if (p != null && _fbSeats.Remove(p, out DroneFallbackSeat? gone)) StopFallbackSounds(gone);
                continue;
            }
            // ours: the beam (MphRead's red line off) and the laser's sounds (MphRead's one shot sound off)
            p!.HostOwnDroneLaser = true;
            p.HostOwnDroneLaserSfx = true;
            if (!_fbSeats.TryGetValue(p, out DroneFallbackSeat? seat)) _fbSeats[p] = seat = new DroneFallbackSeat();
            seat.Sound.Update(p.DroneDrawCenter, DroneSfxRange);
            // the burst's unfold: the charge sound from its first frame, stopped once it's out of the unfold
            int state = p.DroneFireState;
            if (state != seat.State)
            {
                if (state == 1) Play(seat, ref seat.Charge, Metadata.BeamSfx[(int)BeamType.PowerBeam, (int)BeamSfx.Charge]);
                else if (seat.LaserFrame < 0) Stop(seat, ref seat.Charge);
                seat.State = state;
                seat.Frame = 0;
            }
            else seat.Frame++;
            // the laser on its clip frames
            int lf = p.DroneLaserFrame;
            if (lf != seat.LaserFrame)
            {
                if (lf >= 0 && (seat.LaserFrame < 0 || lf < seat.LaserFrame))
                {
                    Play(seat, ref seat.Charge, Metadata.BeamSfx[(int)BeamType.PowerBeam, (int)BeamSfx.Charge]); // wind-up
                }
                if (lf >= DroneLaserOnFrame && seat.LaserFrame < DroneLaserOnFrame)
                {
                    Stop(seat, ref seat.Charge);
                    seat.Sound.PlaySfx(Metadata.BeamSfx[(int)BeamType.Imperialist, (int)BeamSfx.Shot]);
                    Play(seat, ref seat.Loop, Metadata.BeamSfx[(int)BeamType.ShockCoil, (int)BeamSfx.Shot], loop: true);
                }
                if (lf < 0 || lf >= DroneLaserOffFrame)
                {
                    Stop(seat, ref seat.Loop);
                    if (lf < 0) Stop(seat, ref seat.Charge);
                }
                seat.LaserFrame = lf;
            }
        }
    }

    static void Play(DroneFallbackSeat seat, ref int playing, int id, bool loop = false)
    {
        if (playing >= 0) seat.Sound.StopSfx(playing);
        seat.Sound.PlaySfx(id, loop);
        playing = id;
    }

    static void Stop(DroneFallbackSeat seat, ref int playing)
    {
        if (playing >= 0) seat.Sound.StopSfx(playing);
        playing = -1;
    }

    static void StopFallbackSounds(DroneFallbackSeat seat)
    {
        Stop(seat, ref seat.Charge);
        Stop(seat, ref seat.Loop);
    }

    // a replay restarting its match (MatchReplay.RestartReplay), and the match's end
    void ResetDroneFallback()
    {
        foreach (DroneFallbackSeat s in _fbSeats.Values) StopFallbackSounds(s);
        _fbSeats.Clear();
    }

    // DrawSkins (MatchSkins.cs), after DrawDroneFx: the laser's tubes + burn and the burst's charge glow
    void DrawDroneFallback(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _fbSeats.Count == 0) return;
        bool any = false;
        foreach (PlayerEntity p in _fbSeats.Keys)
        {
            if (p.DroneLaserOn || p.DroneFireState is 1 or 2) any = true;
        }
        if (!any || !FxGl() || !FallbackGl()) return;
        Vector3 cam = scene.CameraPosition;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        // the laser: Prime's three tubes (as DrawDroneFx draws them for an import), outermost first
        foreach ((float radius, Vector4 col) in DroneLaserTubes)
        {
            int n = 0;
            foreach (PlayerEntity p in _fbSeats.Keys)
            {
                if (!p.DroneLaserOn) continue;
                Vector3 from = p.DroneLaserOrigin, to = p.DroneLaserEnd;
                Vector3 dir = to - from;
                if (dir.LengthSquared < 1e-6f) continue;
                dir.Normalize();
                Vector3 SideAt(Vector3 at)
                {
                    Vector3 sd = Vector3.Cross(dir, cam - at);
                    float w = MathF.Max(radius * DroneBody.Scale, (cam - at).Length * DroneLaserMinHalf);
                    return (sd.LengthSquared > 1e-8f ? sd.Normalized() : right) * w;
                }
                Vector3 sf = SideAt(from), st = SideAt(to);
                Quad(ref n, vp, from + sf, to + st, from - sf, to - st, col);
            }
            if (n > 0) DrawRun(0, additive: true, n, white: true);
        }
        // glows: the burn where the beam lands (flickering, red-orange around a white core) and the burst's charge at the
        // drone (growing through the unfold, held while it fires), the local player's own drone fading with its body
        {
            int n = 0;
            foreach ((PlayerEntity p, DroneFallbackSeat seat) in _fbSeats)
            {
                float t = (_host.Scene.FrameCount % 100000) * 0.9f + p.SlotIndex * 1.7f;
                if (p.DroneLaserOn)
                {
                    Vector3 at = p.DroneLaserEnd + p.DroneLaserNormal * 0.03f;
                    float flick = 0.85f + 0.15f * MathF.Sin(t) * MathF.Sin(t * 2.3f);
                    Glow(ref n, vp, right, up, at, 0.55f * flick, new Vector4(1, 0.35f, 0.12f, 0.75f));
                    Glow(ref n, vp, right, up, at, 0.22f * flick, new Vector4(1, 0.95f, 0.85f, 1));
                    Glow(ref n, vp, right, up, p.DroneLaserOrigin, 0.18f * flick, new Vector4(1, 0.6f, 0.5f, 0.8f));
                }
                int state = p.DroneFireState;
                if (state is 1 or 2 && p.DroneLaserFrame < 0)
                {
                    float grow = state == 1 ? Math.Clamp((seat.Frame + 1) / (float)DroneUnfoldFrames, 0, 1) : 1;
                    float pulse = 0.9f + 0.1f * MathF.Sin(t * 1.3f);
                    float a = DroneAlpha(scene, p);
                    // at its two guns, out in front of the stand-in's body (inside it they'd be hidden)
                    Vector3 aim = p.DroneDrawAim.LengthSquared > 1e-6f ? p.DroneDrawAim.Normalized() : Vector3.UnitZ;
                    Vector3 side = Vector3.Cross(aim, Vector3.UnitY);
                    side = side.LengthSquared > 1e-6f ? side.Normalized() : right;
                    foreach (float s in new[] { -1f, 1f })
                    {
                        Vector3 gun = p.DroneDrawCenter + aim * DroneGunAhead + side * (s * DroneGunSide);
                        Glow(ref n, vp, right, up, gun, 0.34f * grow * pulse, new Vector4(1, 0.85f, 0.45f, 0.6f * a));
                        Glow(ref n, vp, right, up, gun, 0.12f * grow * pulse, new Vector4(1, 1, 0.9f, 0.95f * a));
                    }
                }
            }
            if (n > 0)
            {
                GLES30.GlBindTexture(GLES30.GlTexture2d, _fbGlow);
                GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOne);
                GLES30.GlBufferData(GLES30.GlArrayBuffer, n * 10 * 4, _fxVerts, GLES30.GlDynamicDraw);
                GLES30.GlDrawArrays(GLES30.GlTriangles, 0, n);
            }
        }
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    void Glow(ref int n, Matrix4 vp, Vector3 right, Vector3 up, Vector3 at, float size, Vector4 col)
    {
        if (size <= 0 || col.W <= 0.002f) return;
        Vector3 ax = right * size / 2, ay = up * size / 2;
        Quad(ref n, vp, at - ax + ay, at + ax + ay, at - ax - ay, at + ax - ay, col);
    }

    // the glow texture: a soft round spot, made again after a new GL context (FxGl empties its own, keyed the same way)
    bool FallbackGl()
    {
        if (!_u.ContainsKey("dronefb"))
        {
            _fbGlow = 0;
            _u["dronefb"] = 1;
        }
        if (_fbGlow != 0) return true;
        const int size = 64;
        var px = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp(1 - d, 0, 1);
                a = a * a * (3 - 2 * a);
                int ab = (int)MathF.Round(a * 255);
                px[y * size + x] = ab << 24 | 0xFFFFFF; // white, alpha = the falloff (ABGR in memory as RGBA bytes)
            }
        }
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        _fbGlow = ids[0];
        GLES30.GlBindTexture(GLES30.GlTexture2d, _fbGlow);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, size, size, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        return true;
    }
}
