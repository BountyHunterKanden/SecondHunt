using System;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Prime 1 Main Quarry's crane and wall (MainQuarryCrane.cs), from the room's Prime1Fx look (_p1Fx): crane_turn loops while
// the crane turns, crane_clank 6 s into each swing (the clips' sound event), crane_stop as it stops; the wall's break puts
// wall_dust at its two Dust Effects (Prime's scales), plays wall_break (0.5 s on, Destroy Wall's delay, for a crane drawn
// still; at the touch for the swinging one) and flies its five pieces as the crates' (_sandBits, CampaignRenderer.SandCrateFx.cs).
// It follows the crane entity (the host's sim, a guest's mirror); a wall already broken when first seen (a late join)
// stays quiet. The shake is the sim's (MainQuarryCraneEntity).
internal sealed partial class CampaignRenderer
{
    const float QuarrySfxRange = 120f;

    int _quarryRoom = -1;
    bool _quarrySeen, _quarryMoving, _quarryWallUp;
    object? _quarryTurn;
    float _quarryWallAt = -1, _quarrySwingAt = -1;
    readonly SoundSource _quarryTurnSound = new() { ReferenceDistance = 0, MaxDistance = QuarrySfxRange };
    readonly SoundSource _quarryStopSound = new() { ReferenceDistance = 0, MaxDistance = QuarrySfxRange };
    readonly SoundSource _quarryWallSound = new() { ReferenceDistance = 0, MaxDistance = QuarrySfxRange };
    readonly SoundSource _quarryClankSound = new() { ReferenceDistance = 0, MaxDistance = QuarrySfxRange };

    // TickSkins (MatchSkins.cs), after TickPrime1Fx
    void TickQuarryFx()
    {
        int room = _host == null || _host.Ended ? -1 : _host.RoomId;
        if (_quarryRoom != room || room < 0)
        {
            StopQuarryFx();
            _quarryRoom = room;
        }
        if (_host == null || _host.Ended || _p1Fx is not { } lib) return;
        MainQuarryCraneEntity? crane = null;
        foreach (EntityBase e in _host.Scene.Entities)
        {
            if (e is MainQuarryCraneEntity c) { crane = c; break; }
        }
        if (crane == null) return;
        MainQuarryCrane.CraneSpot s = crane.Spot;
        if (!_quarrySeen)
        {
            _quarrySeen = true;
            _quarryMoving = crane.Moving;
            _quarryWallUp = crane.WallUp;
        }
        if (crane.Moving != _quarryMoving)
        {
            _quarryMoving = crane.Moving;
            if (!_quarryMoving)
            {
                _sfx?.StopPcm(_quarryTurn);
                _quarryTurn = null;
                if (Place(_quarryStopSound, s.StopSound)) Prime1Sound(lib, "crane_stop", _quarryStopSound);
            }
        }
        if (_quarryMoving && _quarryTurn == null && _sfx != null && lib.Sounds.TryGetValue("crane_turn", out var turn)
            && Place(_quarryTurnSound, s.TurnSound))
        {
            _quarryTurn = _sfx.PlayPcmLoop(turn.Pcm, turn.Rate, 1, _quarryTurnSound);
        }
        float swing = crane.SwingTime;
        if (swing >= MainQuarryCrane.ClankAt && _quarrySwingAt >= 0 && _quarrySwingAt < MainQuarryCrane.ClankAt
            && Place(_quarryClankSound, s.TurnSound))
        {
            Prime1Sound(lib, "crane_clank", _quarryClankSound);
        }
        _quarrySwingAt = swing;
        if (_quarryWallUp && !crane.WallUp) BreakQuarryWall(lib, crane);
        _quarryWallUp = crane.WallUp;
        if (_quarryWallAt >= 0 && _p1Time >= _quarryWallAt)
        {
            _quarryWallAt = -1;
            if (Place(_quarryWallSound, s.WallSound)) Prime1Sound(lib, "wall_break", _quarryWallSound);
        }
    }

    static bool Place(SoundSource src, float[]? at)
    {
        if (at is not { Length: 3 }) return false;
        src.Position = new Vector3(at[0], at[1], at[2]);
        src.Self = false;
        return true;
    }

    void BreakQuarryWall(Prime1FxLib lib, MainQuarryCraneEntity crane)
    {
        MainQuarryCrane.CraneSpot s = crane.Spot;
        _quarryWallAt = _p1Time + (s.Parts is { Count: > 0 } ? 0 : MainQuarryCrane.WallSoundDelay);
        // the dust: Prime's axes as the room maps them (X, Y = -Z, Z = up), each by its own scale
        if (s.Dust != null && lib.Fx.TryGetValue("wall_dust", out uint dust))
        {
            foreach (float[] d in s.Dust)
            {
                if (d.Length < 6) continue;
                var at = new Vector3(d[0], d[1], d[2]);
                var fx = new DroneFxLive
                {
                    Sys = new RetroParticleSystem(lib.Parts, dust, GrappleMatrix(at, -Vector3.UnitZ * d[4], Vector3.UnitX * d[3], Vector3.UnitY * d[5]),
                        follow: true, seed: Environment.TickCount ^ (int)dust),
                };
                _fxLive.Add(fx);
                _p1Live.Add((fx, _p1Time + 3));
            }
        }
        // the pieces, each from its own place (as BreakSandCrate: a cone about its up axis, Prime's speeds in MPH units)
        if (s.WallDebris is not { Count: > 0 } pieces || s.WallDebrisAt == null || s.WallDebrisPasses == null) return;
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
        for (int i = 0; i < pieces.Count && i < s.WallDebrisAt.Count; i++)
        {
            TurretFx f = pieces[i];
            float[] p = s.WallDebrisAt[i];
            if (f.Debris is not { Length: >= EchoesTurret.DebrisLength } d || p.Length < 3 || !s.WallDebrisPasses.TryGetValue(f.Id, out int pass)) continue;
            Matrix4 turn = f.At is { Length: 12 } a ? M(a) : Matrix4.Identity;
            turn.Row3 = new Vector4(0, 0, 0, 1);
            var dir = new Vector3(d[EchoesTurret.DebrisDir], d[EchoesTurret.DebrisDir + 1], d[EchoesTurret.DebrisDir + 2]);
            dir = dir.LengthSquared > 1e-8f ? dir.Normalized() : Vector3.UnitZ;
            Vector3 cv = Cone(d[EchoesTurret.DebrisCone], d[EchoesTurret.DebrisSpeed], d[EchoesTurret.DebrisSpeed + 1]);
            Vector3 u = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX).Normalized(), w = Vector3.Cross(dir, u);
            Vector3 local = u * cv.X + w * cv.Y + dir * cv.Z;
            Matrix4 rot = turn;
            rot.Row0 = new Vector4(rot.Row0.Xyz.Normalized(), 0);
            rot.Row1 = new Vector4(rot.Row1.Xyz.Normalized(), 0);
            rot.Row2 = new Vector4(rot.Row2.Xyz.Normalized(), 0);
            Vector3 spin = Cone(360, d[EchoesTurret.DebrisSpin], d[EchoesTurret.DebrisSpin + 1]);
            _sandBits.Add(new SandCrateBit
            {
                F = f, Pass = pass, Turn = turn, Pos = new Vector3(p[0], p[1], p[2]), Vel = Vector3.TransformVector(local, rot) * scale,
                Axis = spin.LengthSquared > 1e-8f ? spin.Normalized() : Vector3.UnitY, Spin = spin.Length,
                Life = Math.Max(1 / 60f, R(d[EchoesTurret.DebrisLife], d[EchoesTurret.DebrisLife + 1])), Light = crane.WallLight,
            });
        }
    }

    void StopQuarryFx()
    {
        _sfx?.StopPcm(_quarryTurn);
        _quarryTurn = null;
        _quarrySeen = false;
        _quarryWallAt = -1;
        _quarrySwingAt = -1;
    }
}
