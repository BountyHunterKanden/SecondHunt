using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The spider ball's look and sound (docs/SPIDER_BALL.md S2), from each player's attached state (PlayerSpiderBall.cs), the
// same on every hunter (owner 2026-11-06):
//   sound: Echoes' spider loop (sa2_b_spidlach_lp), rendered at import into the arena's hd/spider_loop.wav
//   (EchoesSpider.ImportSound), played while the form holds the track.
//   look: Echoes' own, copied from the player's disc at import (EchoesSpider.ImportFx: TestAnim.pak's PART 22b005a1
//   SpiderBallMagnetEffect and SWHC 4735d6e1 SpiderElectric in hd/spider_fx/), run by Prime's rules (CMorphBall, read
//   from Metaforce; docs/SPIDER_BALL.md S1):
//     beads: each step while attached, one-frame particles every 0.2 Echoes units from the form's centre toward the
//     track point (L / 0.2 + 1 of them, the end itself never reached), the line laid 8 x L / 2.1 + 1 times over
//     (CreateSpiderBallParticles); drawn with the drone effects (_fxLive), the system's own emission off.
//     sparks: 5 new a step from a pool of 32, each living 4..8 steps, a straight 6-point line from 1.19 R to 0.9 R off
//     the form's centre on one side, nudged by a 0.6-unit random direction (AddSpiderBallElectricalEffect,
//     DoSpiderBallWarmup), then frozen and carried with the form, fading over its life; drawn as thin additive strips
//     in the SWHC's colour, radius (LRAD 0.01..0.03) and texture. The SWHC's own per-point elements (its jag) are not
//     evaluated: a small random push per point stands in. The forms here don't roll as Echoes' ball does, so each
//     spark keeps the random frame it was made in rather than spinning with the ball.
//   Without hd/spider_fx (an arena imported before arenas v22): the earlier drawn stand-in, an electric bolt from the
//   track point to the form's centre, brighter as the gap grows.
// The lines are drawn as DrawHyperFx draws its lightning (camera-facing strips, additive).
internal sealed partial class CampaignRenderer
{
    const int SpiderBoltBends = 6;
    const float SpiderSfxVolume = 0.8f, SpiderSfxRange = 40f;
    const float SpiderGlowUp = 0.25f, SpiderGlowDown = 0.15f; // per step toward on / off
    const float SpiderGapFull = 0.6f;                         // the gap (units past the form's radius) at full strength
    const int SpiderSparkPool = 32, SpiderSparksPerStep = 5, SpiderSparkPoints = 6;
    const int SpiderBeadsMax = 200;                           // the PART's MAXP
    static readonly Vector4 SpiderSparkColor = new(0.584f, 0.8f, 1f, 1f); // the SWHC's COLR

    sealed class SpiderLook
    {
        public RetroParticles Parts = null!;
        public uint SparkTex;
    }

    sealed class SpiderSpark
    {
        public readonly Vector3[] Local = new Vector3[SpiderSparkPoints]; // off the form's centre
        public int Age, Life;
        public float Radius;
    }

    sealed class SpiderFxSeat
    {
        public bool On;
        public float Glow;
        public object? Loop;
        public readonly SoundSource Sound = new();
        public readonly Vector3[] Main = new Vector3[SpiderBoltBends + 1], Fork = new Vector3[SpiderBoltBends + 1];
        public Vector3 From, To;
        public float Gap;
        public int Age;
        public DroneFxLive? Beads;
        public readonly List<SpiderSpark> Sparks = new();
    }

    (short[] Pcm, int Rate)? _spiderSound;
    SpiderLook? _spiderLook;
    int _spiderFxRoom = -1;
    readonly Dictionary<PlayerEntity, SpiderFxSeat> _spiderSeats = new();
    readonly List<PlayerEntity> _spiderGone = new();
    readonly List<RetroElectric.Line> _spiderLines = new();
    readonly Random _spiderRng = new(0x2641);

    // TickSkins (MatchSkins.cs)
    void TickSpiderFx()
    {
        if (_host == null || _host.Ended || !PlayerEntity.HostSpiderBall)
        {
            StopSpiderFx();
            return;
        }
        if (_spiderFxRoom != _host.RoomId)
        {
            StopSpiderFx();
            _spiderFxRoom = _host.RoomId;
            _spiderSound = LoadSpiderSound();
            _spiderLook = LoadSpiderLook();
        }
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            bool on = p.SpiderAttached && p.IsAltForm && p.Health > 0;
            if (!_spiderSeats.TryGetValue(p, out SpiderFxSeat? seat))
            {
                if (!on) continue;
                _spiderSeats[p] = seat = new SpiderFxSeat();
            }
            TickSpiderSeat(p, seat, on);
        }
        foreach ((PlayerEntity p, SpiderFxSeat seat) in _spiderSeats)
        {
            if (!seat.On && seat.Glow <= 0 && seat.Sparks.Count == 0) _spiderGone.Add(p);
        }
        foreach (PlayerEntity p in _spiderGone) _spiderSeats.Remove(p);
        _spiderGone.Clear();
    }

    void TickSpiderSeat(PlayerEntity p, SpiderFxSeat seat, bool on)
    {
        seat.On = on;
        seat.Glow = on ? MathF.Min(1, seat.Glow + SpiderGlowUp) : MathF.Max(0, seat.Glow - SpiderGlowDown);
        if (on)
        {
            seat.From = p.SpiderPoint;
            seat.To = p.Volume.SpherePosition;
            seat.Gap = MathF.Max(0, p.SpiderDistance - p.Volume.SphereRadius);
            if (_spiderLook != null) SpiderEcho(_spiderLook, p, seat);
            else if (seat.Age % 2 == 0)
            {
                SpiderBend(seat.Main, seat.From, seat.To, 0.18f);
                SpiderBend(seat.Fork, seat.From, seat.To, 0.3f);
            }
            seat.Age++;
        }
        else if (seat.Beads != null)
        {
            seat.Beads.Sys.Emitting = false;
            seat.Beads = null;
        }
        for (int i = seat.Sparks.Count - 1; i >= 0; i--)
        {
            if (++seat.Sparks[i].Age >= seat.Sparks[i].Life) seat.Sparks.RemoveAt(i);
        }
        // the loop while the form holds the track
        if (_sfx == null) return;
        if (on && _spiderSound is { } snd)
        {
            PlaceGrappleSound(seat.Sound, p, seat.To);
            if (seat.Loop == null) seat.Loop = _sfx.PlayPcmLoop(snd.Pcm, snd.Rate, SpiderSfxVolume, seat.Sound);
            else _sfx.MovePcm(seat.Loop, SpiderSfxVolume, seat.Sound);
        }
        else if (seat.Loop != null)
        {
            _sfx.StopPcm(seat.Loop);
            seat.Loop = null;
        }
    }

    // one step of Echoes' look for an attached form: the beads into its system, the new sparks
    void SpiderEcho(SpiderLook look, PlayerEntity p, SpiderFxSeat seat)
    {
        float sc = EchoesArena.DefaultScale;
        // the beads (made again when the live list dropped the system: no particles left after a step, a replay's restart)
        if (seat.Beads == null || !_fxLive.Contains(seat.Beads))
        {
            seat.Beads = null;
            if (look.Parts.Get(EchoesSpider.BeadsId) != null)
            {
                seat.Beads = new DroneFxLive { P = p, Sys = new RetroParticleSystem(look.Parts, EchoesSpider.BeadsId, Matrix4.CreateScale(sc), follow: false, seed: Environment.TickCount) };
                seat.Beads.Sys.Emitting = false;
                _fxLive.Add(seat.Beads);
            }
        }
        if (seat.Beads != null)
        {
            Vector3 d = seat.From - seat.To;
            float len = d.Length / sc;
            int sub = (int)(len / 0.2f + 1), laps = (int)(8 * (len / 2.1f));
            Vector3 step = d / sub;
            for (int lap = 0; lap <= laps; lap++)
            {
                Vector3 t = seat.To;
                for (int j = 0; j < sub && seat.Beads.Sys.LiveParticles < SpiderBeadsMax; j++, t += step) seat.Beads.Sys.EmitAt(t, 1);
            }
        }
        // the sparks
        if (look.SparkTex == 0) return;
        float r = p.Volume.SphereRadius;
        float Rnd() => (float)_spiderRng.NextDouble();
        for (int k = 0; k < SpiderSparksPerStep && seat.Sparks.Count < SpiderSparkPool; k++)
        {
            var x = new Vector3(Rnd() * 2 - 1, Rnd() * 2 - 1, Rnd() * 2 - 1);
            x = x.LengthSquared > 1e-4f ? x.Normalized() : Vector3.UnitX;
            Vector3 y = Vector3.Cross(x, MathF.Abs(x.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
            Vector3 z = Vector3.Cross(x, y);
            float side = 0.9f * r;
            float a0 = MathHelper.DegreesToRadians(-(80 * Rnd() - 40));
            float a1 = MathHelper.DegreesToRadians(90 - (80 * Rnd() - 40));
            float nudge = 0.6f * sc;
            Vector3 start = x * (1.32f * side)
                + (x * -(MathF.Sin(a1) * MathF.Cos(a0)) + y * MathF.Sin(a0) + z * (MathF.Cos(a0) * MathF.Cos(a1))) * nudge;
            Vector3 inc = (x * side - start) / SpiderSparkPoints;
            var spark = new SpiderSpark { Life = 4 + _spiderRng.Next(5), Radius = (0.01f + 0.02f * Rnd()) * sc };
            float jag = 0.05f * sc;
            for (int i = 0; i < SpiderSparkPoints; i++)
            {
                spark.Local[i] = start + inc * i;
                if (i > 0) spark.Local[i] += new Vector3(Rnd() * 2 - 1, Rnd() * 2 - 1, Rnd() * 2 - 1) * jag;
            }
            seat.Sparks.Add(spark);
        }
    }

    // the arena's hd/spider_fx (EchoesSpider.ImportFx), null without it (the stand-in bolt)
    SpiderLook? LoadSpiderLook()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        string fxDir = Path.Combine(dir, EchoesSpider.FxDir);
        if (!Directory.Exists(fxDir))
        {
            Log.Info("MPHArena", "spider ball: no Echoes effects (re-import the arena), the stand-in bolt");
            return null;
        }
        var look = new SpiderLook();
        try
        {
            look.Parts = RetroParticles.Load(fxDir, echoes: true);
            string sw = Path.Combine(fxDir, $"{EchoesSpider.ArcsId:x8}.swhc");
            if (File.Exists(sw)) look.SparkTex = Part.Swoosh.Load(File.ReadAllBytes(sw)).Texture;
        }
        catch (Exception x)
        {
            Log.Warn("MPHArena", $"spider ball: effects not read ({x.Message}), the stand-in bolt");
            return null;
        }
        bool beads = look.Parts.Get(EchoesSpider.BeadsId) != null;
        if (!beads && look.SparkTex == 0) return null;
        foreach (string png in Directory.GetFiles(fxDir, "*.png"))
        {
            if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) continue;
            _fxTexFile.TryAdd(t, png);
            _fxTexRepeat.Add(t);
        }
        Log.Info("MPHArena", $"spider ball: Echoes effects (beads {(beads ? "yes" : "no")}, sparks texture {look.SparkTex:x8})");
        return look;
    }

    // a jagged line from `a` to `b`: the ends fixed, each bend pushed off the line by up to `wander` of its length (more
    // in the middle)
    void SpiderBend(Vector3[] pts, Vector3 a, Vector3 b, float wander)
    {
        Vector3 d = b - a;
        float len = d.Length;
        Vector3 y = len > 1e-4f ? d / len : Vector3.UnitY;
        Vector3 x = Vector3.Cross(y, MathF.Abs(y.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
        Vector3 z = Vector3.Cross(x, y);
        float reach = MathF.Max(len, 0.25f) * wander;
        for (int i = 0; i <= SpiderBoltBends; i++)
        {
            float t = (float)i / SpiderBoltBends;
            float push = i == 0 || i == SpiderBoltBends ? 0 : reach * MathF.Sin(t * MathF.PI);
            pts[i] = a + d * t + x * (push * ((float)_spiderRng.NextDouble() * 2 - 1)) + z * (push * ((float)_spiderRng.NextDouble() * 2 - 1));
        }
    }

    void StopSpiderFx()
    {
        foreach (SpiderFxSeat seat in _spiderSeats.Values)
        {
            if (seat.Loop != null) _sfx?.StopPcm(seat.Loop);
            seat.Loop = null;
            if (seat.Beads != null) seat.Beads.Sys.Emitting = false;
            seat.Beads = null;
        }
        _spiderSeats.Clear();
        if (_host == null || _host.Ended) _spiderFxRoom = -1;
    }

    // the arena's hd/spider_loop.wav (EchoesSpider.ImportSound), null without it
    (short[] Pcm, int Rate)? LoadSpiderSound()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        string wav = Path.Combine(dir, EchoesSpider.LoopFile);
        if (!File.Exists(wav) || MusyX.ReadWav(wav, out int rate) is not short[] pcm || pcm.Length == 0)
        {
            Log.Info("MPHArena", "spider ball: no loop sound (re-import the arena)");
            return null;
        }
        Log.Info("MPHArena", $"spider ball: loop sound ({pcm.Length / (float)rate:0.00} s)");
        return (pcm, rate);
    }

    // DrawSkins (MatchSkins.cs), additive pass: each attached form's bolt
    void DrawSpiderFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _spiderSeats.Count == 0) return;
        _spiderLines.Clear();
        bool echo = _spiderLook != null;
        foreach ((PlayerEntity p, SpiderFxSeat seat) in _spiderSeats)
        {
            if (echo)
            {
                Matrix4 c = SmoothCarry(p, scene);
                foreach (SpiderSpark sp in seat.Sparks)
                {
                    var pts = new Vector3[SpiderSparkPoints];
                    for (int i = 0; i < pts.Length; i++) pts[i] = seat.To + sp.Local[i];
                    float a = 1 - (float)sp.Age / sp.Life;
                    _spiderLines.Add(new RetroElectric.Line(SpiderCarried(pts, c), sp.Radius * 2, SpiderSparkColor * a, 0, 0));
                }
                continue;
            }
            if (seat.Glow <= 0 || seat.Age == 0) continue;
            float gap = Math.Clamp(seat.Gap / SpiderGapFull, 0, 1);
            // faint on the track, brighter as it drifts off
            float k = seat.Glow * (0.3f + 0.7f * gap);
            Matrix4 carry = SmoothCarry(p, scene);
            Vector3[] main = SpiderCarried(seat.Main, carry);
            _spiderLines.Add(new RetroElectric.Line(main, 2 + 3 * gap, new Vector4(1f * k, 0.85f * k, 0.35f * k, 1), 0, 0));
            _spiderLines.Add(new RetroElectric.Line(main, 1, new Vector4(k, k, k * 0.9f, 1), 0, 0));
            if (gap > 0.25f) _spiderLines.Add(new RetroElectric.Line(SpiderCarried(seat.Fork, carry), 1.5f, new Vector4(0.8f * k * gap, 0.6f * k * gap, 0.2f * k * gap, 1), 0, 0));
        }
        if (_spiderLines.Count == 0 || !FxGl()) return;
        Vector3 cam = scene.CameraPosition;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        float pxToWorld = 2f / 528f / MathF.Max(1e-3f, scene.PerspectiveMatrix.M22);
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        int n = 0;
        foreach (RetroElectric.Line line in _spiderLines)
        {
            Vector3[] pts = line.Points;
            Vector3 Side(int j)
            {
                Vector3 at = pts[j], dir = pts[Math.Min(j + 1, pts.Length - 1)] - pts[Math.Max(j - 1, 0)];
                Vector3 sd = Vector3.Cross(dir, cam - at);
                // the sparks' widths are world sizes (the SWHC's radius), kept to at least a pixel
                float px = 0.5f * pxToWorld * (cam - at).Length;
                float half = echo ? MathF.Max(line.WidthPx * 0.5f, px) : line.WidthPx * px;
                return (sd.LengthSquared > 1e-10f ? sd.Normalized() : right) * half;
            }
            Vector3 sa = Side(0);
            for (int j = 0; j + 1 < pts.Length; j++)
            {
                Vector3 a = pts[j], b = pts[j + 1], sb = Side(j + 1);
                if ((n + 6) * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, _fxVerts.Length * 2);
                Vert(ref n, vp, a + sa, (0, 0), line.Color); Vert(ref n, vp, b + sb, (1, 1), line.Color); Vert(ref n, vp, a - sa, (0, 0), line.Color);
                Vert(ref n, vp, a - sa, (0, 0), line.Color); Vert(ref n, vp, b + sb, (1, 1), line.Color); Vert(ref n, vp, b - sb, (1, 1), line.Color);
                sa = sb;
            }
        }
        uint tex = echo ? _spiderLook!.SparkTex : 0;
        if (tex != 0 && FxTexture(tex) != null) DrawRun(tex, additive: true, n);
        else DrawRun(0, additive: true, n, white: true);
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    static Vector3[] SpiderCarried(Vector3[] pts, Matrix4 carry)
    {
        var o = new Vector3[pts.Length];
        for (int i = 0; i < pts.Length; i++) o[i] = carry == Matrix4.Identity ? pts[i] : Vector3.TransformPosition(pts[i], carry);
        return o;
    }
}
