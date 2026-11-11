using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Mining Plaza's sand crates breaking in Echoes' look and sound (thread 3d373d stage 3). A crate's DEAD fires its GENR
// 'BLOW CRATE': RandomCount (12) picks of its DebrisExtended followers at the crate, each flown by CScriptDebris as the
// Shooting Gallery grates' are (CampaignRenderer.TurretFx.cs: cone speed, random spin, life, gravity, fade, scale-out,
// its death PART where its life ends), trailing its PART (982c7701); with the box's flash (418d14d4) and dust (a982d571)
// at the crate and the explode sound (SOND 0x535). All from the pillar look's library (hd/pillar.json: crate_*).
// Runs off each crate's Breaks count (synced online), so a crate first seen already broken plays nothing.
internal sealed partial class CampaignRenderer
{
    sealed class SandCrateBit
    {
        public TurretFx F = null!;
        public int Pass;
        public Matrix4 Turn;
        public Vector3 Pos, Vel, Axis;
        public float Spin, Angle, Life, Age, Size = 1;
        public CrateLight? Light;
        public DroneFxLive? Trail;
    }

    readonly List<SandCrateBit> _sandBits = new();
    readonly Dictionary<MiningPlazaCrateEntity, (int Breaks, SoundSource Sound)> _sandCrates = new();
    readonly Random _sandRandom = new();

    // TickPillarFx's room loop hands each crate here (the library is the pillar look's)
    void TickSandCrate(PillarFxLib lib, MiningPlazaCrateEntity c)
    {
        if (!_sandCrates.TryGetValue(c, out var st))
        {
            _sandCrates[c] = (c.Breaks, new SoundSource());
            return;
        }
        if (c.Breaks > st.Breaks) BreakSandCrate(lib, c, st.Sound);
        else if (c.Hit && c.Up)
        {
            st.Sound.Position = c.Spot.Center.Length == 3 ? MiningPlazaCrates.V(c.Spot.Center) : c.Position;
            st.Sound.Self = false;
            st.Sound.ReferenceDistance = 0;
            st.Sound.MaxDistance = PillarSfxRange;
            PillarSound(lib, "crate_bounce", st.Sound);
        }
        _sandCrates[c] = (c.Breaks, st.Sound);
    }

    void BreakSandCrate(PillarFxLib lib, MiningPlazaCrateEntity c, SoundSource sound)
    {
        MiningPlazaCrates.CrateSpot s = c.Spot;
        Vector3 at = s.Center.Length == 3 ? MiningPlazaCrates.V(s.Center) : c.Position;
        sound.Position = at;
        sound.Self = false;
        sound.ReferenceDistance = 0;
        sound.MaxDistance = PillarSfxRange;
        PillarSound(lib, "crate_break", sound);
        PillarOnce(lib, "crate_flash", at, null);
        PillarOnce(lib, "crate_dust", at, null);
        if (s.Debris.Count == 0) return;
        float scale = EchoesArena.DefaultScale;
        float R(float lo, float hi) => lo + (float)_sandRandom.NextDouble() * (hi - lo);
        Vector3 Cone(float cone, float lo, float hi)
        {
            float mag = R(lo, hi);
            float side = 1 - (1 - MathF.Cos(MathF.PI / 360 * cone)) * (float)_sandRandom.NextDouble();
            float hyp = mag * MathF.Sqrt(MathF.Max(0, 1 - side * side));
            float a = MathF.Tau * (float)_sandRandom.NextDouble();
            return new Vector3(hyp * MathF.Cos(a), hyp * MathF.Sin(a), mag * side);
        }
        int slots = Math.Max(s.DebrisSlots, s.Debris.Count);
        IEnumerable<TurretFx> made = s.DebrisCount > 0
            ? Enumerable.Range(0, s.DebrisCount).Select(_ => _sandRandom.Next(slots)).Where(i => i < s.Debris.Count).Select(i => s.Debris[i]).ToList()
            : s.Debris;
        // the crate's own turn (its placed delta): the pieces' axes go with it, as the generator keeps the sender's turn
        Matrix4 crateTurn = MiningPlazaCrates.M(s.Delta);
        crateTurn.Row3 = new Vector4(0, 0, 0, 1);
        foreach (TurretFx f in made)
        {
            if (f.Debris is not { Length: >= EchoesTurret.DebrisLength } d || !s.DebrisPasses.TryGetValue(f.Id, out int pass)) continue;
            Matrix4 turn = f.At is { Length: 12 } a ? M(a) : Matrix4.Identity;
            turn.Row3 = new Vector4(0, 0, 0, 1);
            var dir = new Vector3(d[EchoesTurret.DebrisDir], d[EchoesTurret.DebrisDir + 1], d[EchoesTurret.DebrisDir + 2]);
            dir = dir.LengthSquared > 1e-8f ? dir.Normalized() : Vector3.UnitZ;
            Vector3 cv = Cone(d[EchoesTurret.DebrisCone], d[EchoesTurret.DebrisSpeed], d[EchoesTurret.DebrisSpeed + 1]);
            Vector3 u = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX).Normalized(), w = Vector3.Cross(dir, u);
            Vector3 local = u * cv.X + w * cv.Y + dir * cv.Z;
            Matrix4 rot = turn * crateTurn;
            rot.Row0 = new Vector4(rot.Row0.Xyz.Normalized(), 0);
            rot.Row1 = new Vector4(rot.Row1.Xyz.Normalized(), 0);
            rot.Row2 = new Vector4(rot.Row2.Xyz.Normalized(), 0);
            Vector3 vel = Vector3.TransformVector(local, rot) * scale;
            Vector3 spin = Cone(360, d[EchoesTurret.DebrisSpin], d[EchoesTurret.DebrisSpin + 1]);
            var bit = new SandCrateBit
            {
                F = f, Pass = pass, Turn = turn * crateTurn, Pos = at, Vel = vel,
                Axis = spin.LengthSquared > 1e-8f ? spin.Normalized() : Vector3.UnitY, Spin = spin.Length,
                Life = Math.Max(1 / 60f, R(d[EchoesTurret.DebrisLife], d[EchoesTurret.DebrisLife + 1])), Light = c.Light,
                // BLOW CRATE's random scale (0.75 - 1.25)
                Size = R(0.75f, 1.25f),
            };
            if (lib.Fx.TryGetValue("crate_trail", out uint trail) && lib.Parts.Get(trail) != null)
            {
                var m = new Matrix4(scale, 0, 0, 0, 0, 0, -scale, 0, 0, scale, 0, 0, at.X, at.Y, at.Z, 1);
                bit.Trail = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, trail, m, follow: false, seed: _sandRandom.Next(1, Int32.MaxValue)) };
                _fxLive.Add(bit.Trail);
            }
            _sandBits.Add(bit);
        }
    }

    // TickPillarFx (before TickDroneFx steps the systems): fly the pieces, their trails with them
    void TickSandBits(PillarFxLib? lib)
    {
        const float dt = 1 / 60f;
        float scale = EchoesArena.DefaultScale;
        for (int i = _sandBits.Count - 1; i >= 0; i--)
        {
            SandCrateBit b = _sandBits[i];
            b.Vel.Y -= b.F.Debris![EchoesTurret.DebrisGravity] * scale * dt;
            b.Pos += b.Vel * dt;
            b.Angle += b.Spin * dt;
            b.Age += dt;
            if (b.Trail != null) b.Trail.Sys.Transform.Row3 = new Vector4(b.Pos, 1);
            if (b.Age < b.Life) continue;
            _sandBits.RemoveAt(i);
            if (b.Trail != null) b.Trail.Sys.Emitting = false;
            if (lib != null && b.F.Death != 0 && lib.Parts.Get(b.F.Death) != null)
            {
                var m = new Matrix4(scale, 0, 0, 0, 0, 0, -scale, 0, 0, scale, 0, 0, b.Pos.X, b.Pos.Y, b.Pos.Z, 1);
                _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, b.F.Death, m, follow: true, seed: _sandRandom.Next(1, Int32.MaxValue)) });
            }
        }
    }

    void StopSandCrates()
    {
        foreach (SandCrateBit b in _sandBits)
        {
            if (b.Trail != null) b.Trail.Sys.Emitting = false;
        }
        _sandBits.Clear();
        _sandCrates.Clear();
    }

    // DrawCrates: the live pieces with the props (whole: as placed; fading: blended)
    void AddSandBits(List<(Matrix4 Model, bool Flash, CrateLight? Light, int Pass, float Fade, Vector3? Tint)> props)
    {
        foreach (SandCrateBit b in _sandBits)
        {
            float[] d = b.F.Debris!;
            float t = b.Age / b.Life, fin = d[EchoesTurret.DebrisFadeIn], fout = d[EchoesTurret.DebrisFadeOut];
            float k = t < fin ? (fin > 0 ? 1 - b.Age / (b.Life * fin) : 0) : t > fout ? (b.Age - b.Life * fout) / (b.Life * (1 - fout)) : 0;
            float alpha = 1 + (d[EchoesTurret.DebrisEndColor + 3] - 1) * Math.Clamp(k, 0, 1);
            if (alpha <= 0.002f) continue;
            float so = d[EchoesTurret.DebrisScaleOut];
            float st = t > so && so < 1 ? (b.Age - b.Life * so) / (b.Life * (1 - so)) : 0;
            var end = new Vector3(d[EchoesTurret.DebrisEndScale], d[EchoesTurret.DebrisEndScale + 1], d[EchoesTurret.DebrisEndScale + 2]);
            Vector3 sc = Vector3.Lerp(Vector3.One, end, Math.Clamp(st, 0, 1)) * b.Size;
            Matrix4 m = Matrix4.CreateScale(sc) * b.Turn * Matrix4.CreateFromAxisAngle(b.Axis, b.Angle) * Matrix4.CreateTranslation(b.Pos);
            props.Add((m, false, b.Light, b.Pass, alpha >= 0.998f ? 1 : -alpha, null));
        }
    }
}
