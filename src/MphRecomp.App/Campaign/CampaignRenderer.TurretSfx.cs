using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Campaign;
using MphRecomp.Arenas;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Shooting Gallery's turret in Echoes' sounds (docs/SHOOTING_GALLERY_TURRET.md section 8): the WAVs the arena import
// rendered from the player's own disc (EchoesTurret.ImportSounds, listed in hd/turret.json), played from what the
// turret entity does. Clients mirror the host's turret (NetTurret), so every device hears the same from its own copy.
//   enter: turret_ball_up's event, EnterSoundAt into Entering; exit: turret_ball_down's, as Leaving starts
//   hud: the occupant's own loop (ambient, viewport-dependent SOND) from Entering until Leaving / Destroyed
//   holo: the hologram's loop while it shows (Free)
//   explode: each blow-up; arc: the slot's loop while it holds a ball (BALT ENTR / EXIT)
//   rotate: the PLRT's loop while the barrel turns; fire: each shot; hit: each hit on the turret (all hear it)
//   fly: each shot's loop while it flies; impact: where it hits (MphRead's Power Beam hit sound not played)
// Echoes' SequenceTimer sends only the connections it has times for (Leave / Destroyed have none for the hum's STOP or
// the hologram's PLAY: CScriptSequenceTimer, rs5mp2_p.dol 0x802076f4, 2026-10-16); the hum stops and the hologram's
// sound comes back with the hologram here, as the area's connections mean them.
internal sealed partial class CampaignRenderer
{
    const float TurretSfxVolume = 1f;
    const int TurretRotateHold = 4;     // 60 Hz frames still before the rotation loop stops (a client's aim moves at 30 Hz)

    sealed class TurretSfxLib
    {
        public readonly Dictionary<string, (short[] Pcm, int Rate, TurretSound Def)> Sounds = new(StringComparer.Ordinal);
    }

    TurretSfxLib? _turretSfx;
    int _turretSfxRoom = -1;
    EchoesTurretEntity? _turretSfxOf;
    EchoesTurretEntity.TurretState _turretSfxState;
    int _turretSfxShots, _turretSfxBlowups, _turretSfxStill;
    float _turretSfxYaw, _turretSfxPitch;
    bool _turretSfxEntered;
    int _turretSfxHp = -1; // the turret's health last tick (a drop = a hit)
    object? _turretHum, _turretHolo, _turretArc, _turretRotate;
    readonly SoundSource _turretSound = new(), _turretArcSound = new(), _turretSelf = new();
    readonly Dictionary<BeamProjectileEntity, (object? Voice, SoundSource Source)> _turretFly = new();
    readonly List<Vector3> _turretImpacts = new();
    readonly List<BeamProjectileEntity> _turretGone = new();
    Func<BeamProjectileEntity, bool>? _turretHitHook;

    // the sim loop, after each step (CampaignRenderer.cs; every sound stops once the match has ended)
    void TickTurretSfx()
    {
        if (_host == null || _sfx == null || _host.Ended)
        {
            StopTurretSfx();
            return;
        }
        if (_turretSfxRoom != _host.RoomId)
        {
            StopTurretSfx();
            _turretSfxRoom = _host.RoomId;
            _turretSfx = LoadTurretSfx();
        }
        EchoesTurretEntity? t = _turretSfx == null ? null : NetWorld.TurretOf(_host.Scene);
        if (t != _turretSfxOf)
        {
            StopTurretSfx();
            _turretSfxOf = t;
            if (t != null)
            {
                _turretSfxState = t.State;
                _turretSfxShots = t.Shots;
                _turretSfxBlowups = t.Destructions;
                _turretSfxYaw = t.Yaw;
                _turretSfxPitch = t.Pitch;
                _turretSfxEntered = t.State != EchoesTurretEntity.TurretState.Entering;
            }
        }
        _turretHitHook ??= OnTurretShotHit;
        bool ownImpact = t != null && _turretSfx!.Sounds.ContainsKey("impact");
        if (ownImpact) BeamProjectileEntity.HostOwnHitSound = _turretHitHook;
        else if (BeamProjectileEntity.HostOwnHitSound == _turretHitHook) BeamProjectileEntity.HostOwnHitSound = null;
        TickGrateSfx();
        if (t == null) return;

        PlayerEntity? main = PlayerEntity.Main;
        bool mine = t.Occupant != null && t.Occupant == main;
        Place(_turretSound, t.PivotNow);
        _turretSelf.Update(t.PivotNow, -1);

        // the state's sounds
        if (t.State != _turretSfxState)
        {
            if (t.State == EchoesTurretEntity.TurretState.Entering) _turretSfxEntered = false;
            if (t.State == EchoesTurretEntity.TurretState.Leaving) PlayTurret("exit", _turretSound);
            _turretSfxState = t.State;
        }
        if (!_turretSfxEntered && t.State == EchoesTurretEntity.TurretState.Entering && t.StateTime >= EchoesTurret.EnterSoundAt)
        {
            _turretSfxEntered = true;
            PlayTurret("enter", _turretSound);
        }
        if (t.Destructions != _turretSfxBlowups)
        {
            if (t.Destructions > _turretSfxBlowups) PlayTurret("explode", _turretSound);
            _turretSfxBlowups = t.Destructions;
        }
        if (t.Shots != _turretSfxShots)
        {
            // the occupant hears its own shot unplaced (Echoes' multiplayer: volume 127, not at the turret)
            if (t.Shots > _turretSfxShots) PlayTurret("fire", mine ? _turretSelf : _turretSound);
            _turretSfxShots = t.Shots;
        }
        // a hit on the turret (its XDMG, rso 0x19c8-0x1a30): multiplayer Echoes plays it unplaced on the one TV, so the
        // shooter hears it too, on every damage message: on each drop in its health (a client's from the host's
        // snapshots). Was the hit flash's start, so a burst inside its 10 frames played once (owner 10-17: 'not sure
        // there are cannon-hit noises')
        int hp = t.Hp;
        if (_turretSfxHp >= 0 && hp < _turretSfxHp && t.State is EchoesTurretEntity.TurretState.Entering or EchoesTurretEntity.TurretState.Active)
            PlayTurret("hit", mine ? _turretSelf : _turretSound);
        _turretSfxHp = hp;

        // the loops
        bool up = t.State is EchoesTurretEntity.TurretState.Entering or EchoesTurretEntity.TurretState.Active;
        TurretLoop(ref _turretHum, "hud", up && mine, _turretSelf);
        TurretLoop(ref _turretHolo, "holo", t.HologramShown, _turretSound);
        if (t.Held != null) Place(_turretArcSound, t.Held.Volume.SpherePosition);
        TurretLoop(ref _turretArc, "arc", t.Held != null, _turretArcSound);
        bool turning = t.State == EchoesTurretEntity.TurretState.Active
            && (MathF.Abs(t.Yaw - _turretSfxYaw) > 0.01f || MathF.Abs(t.Pitch - _turretSfxPitch) > 0.01f);
        _turretSfxStill = turning ? 0 : _turretSfxStill + 1;
        _turretSfxYaw = t.Yaw;
        _turretSfxPitch = t.Pitch;
        // the rotation loop: the occupant's own (unplaced, as Echoes starts it with the player's pan), bent by the barrel's
        // turn: PitchBend 8192 + 4000 x yaw / (pi / 2) (rso 0x12a0-0x12d4), on MusyX's default +-2 semitone bend range (inferred)
        TurretLoop(ref _turretRotate, "rotate", t.State == EchoesTurretEntity.TurretState.Active && _turretSfxStill < TurretRotateHold, mine ? _turretSelf : _turretSound);
        if (_turretRotate != null && _turretSfx.Sounds.TryGetValue("rotate", out var rot))
        {
            float semis = 4000f / 8192f * 2 * Math.Clamp(t.Yaw / 90f, -1, 1);
            _sfx.PitchPcm(_turretRotate, rot.Rate, MathF.Pow(2, semis / 12));
        }

        // the shots: each one's flight loop, then its hit
        if (_turretSfx!.Sounds.TryGetValue("fly", out var fly))
        {
            foreach (EntityBase e in _host.Scene.Entities)
            {
                if (e is not BeamProjectileEntity b || b.Lifespan <= 0 || b.Flags.TestFlag(BeamFlags.Collided) || !t.IsOwnShot(b)) continue;
                if (!_turretFly.TryGetValue(b, out var f))
                {
                    var src = new SoundSource();
                    Place(src, b.Position);
                    src.MaxDistance = fly.Def.Range;
                    f = (_sfx.PlayPcmLoop(fly.Pcm, fly.Rate, TurretSfxVolume * fly.Def.Volume, src), src);
                    _turretFly[b] = f;
                }
                Place(f.Source, b.Position);
                _sfx.MovePcm(f.Voice, TurretSfxVolume * fly.Def.Volume, f.Source);
            }
            foreach ((BeamProjectileEntity b, var f) in _turretFly)
            {
                if (b.Lifespan <= 0 || b.Flags.TestFlag(BeamFlags.Collided) || !t.IsOwnShot(b)) _turretGone.Add(b);
            }
            foreach (BeamProjectileEntity b in _turretGone)
            {
                _sfx.StopPcm(_turretFly[b].Voice);
                _turretFly.Remove(b);
            }
            _turretGone.Clear();
        }
        foreach (Vector3 at in _turretImpacts)
        {
            var src = new SoundSource();
            Place(src, at);
            PlayTurret("impact", src);
        }
        _turretImpacts.Clear();
    }

    // the grates' break: their generator's PLAY (role grate) where one breaks, once a step however many break together
    // (Echoes restarts the one SOND); MPH's break sound isn't played (EchoesBreakables.HostOwnGrateBreak)
    readonly Dictionary<EchoesBreakableEntity, int> _grateSfxBreaks = new();
    readonly SoundSource _grateSound = new();

    void TickGrateSfx()
    {
        if (_host == null || _turretSfx == null || !_turretSfx.Sounds.ContainsKey("grate"))
        {
            _grateSfxBreaks.Clear();
            return;
        }
        bool played = false;
        foreach (EntityBase e in _host.Scene.Entities)
        {
            if (e is not EchoesBreakableEntity { Grate: true } g) continue;
            int seen = _grateSfxBreaks.TryGetValue(g, out int n) ? n : g.Breaks;
            if (g.Breaks > seen && !played)
            {
                Place(_grateSound, g.Position);
                PlayTurret("grate", _grateSound);
                played = true;
            }
            _grateSfxBreaks[g] = g.Breaks;
        }
    }

    // BeamProjectileEntity.HostOwnHitSound: a turret shot's hit, its sound on the next tick (MphRead's not played)
    bool OnTurretShotHit(BeamProjectileEntity beam)
    {
        if (_turretSfxOf == null || !_turretSfxOf.IsOwnShot(beam)) return false;
        if (_turretImpacts.Count < 16) _turretImpacts.Add(beam.Position);
        return true;
    }

    // placed at a point, heard to the sound's range (Echoes' max audible distance, fading linearly)
    void Place(SoundSource s, Vector3 at)
    {
        s.Position = at;
        s.Self = false;
        s.ReferenceDistance = 0;
    }

    void PlayTurret(string role, SoundSource at)
    {
        if (_sfx == null || _turretSfx == null || !_turretSfx.Sounds.TryGetValue(role, out var s)) return;
        if (!at.Self) at.MaxDistance = s.Def.Range;
        _sfx.PlayPcm(s.Pcm, s.Rate, TurretSfxVolume * s.Def.Volume, s.Def.Ambient ? _turretSelf : at);
    }

    void TurretLoop(ref object? voice, string role, bool on, SoundSource at)
    {
        if (_sfx == null || _turretSfx == null || !_turretSfx.Sounds.TryGetValue(role, out var s))
        {
            return;
        }
        SoundSource src = s.Def.Ambient ? _turretSelf : at;
        if (!src.Self) src.MaxDistance = s.Def.Range;
        if (on && voice == null) voice = _sfx.PlayPcmLoop(s.Pcm, s.Rate, TurretSfxVolume * s.Def.Volume, src);
        else if (on) _sfx.MovePcm(voice, TurretSfxVolume * s.Def.Volume, src);
        else if (voice != null)
        {
            _sfx.StopPcm(voice);
            voice = null;
        }
    }

    void StopTurretSfx()
    {
        foreach (object? v in new[] { _turretHum, _turretHolo, _turretArc, _turretRotate }) _sfx?.StopPcm(v);
        _turretHum = _turretHolo = _turretArc = _turretRotate = null;
        foreach (var f in _turretFly.Values) _sfx?.StopPcm(f.Voice);
        _turretFly.Clear();
        _turretImpacts.Clear();
        _grateSfxBreaks.Clear();
        _turretSfxOf = null;
        if (_turretHitHook != null && BeamProjectileEntity.HostOwnHitSound == _turretHitHook) BeamProjectileEntity.HostOwnHitSound = null;
    }

    // the arena's turret sounds (hd/turret.json's Sounds), null without them
    TurretSfxLib? LoadTurretSfx()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        if (EchoesTurret.LoadLook(dir) is not { Sounds: { Count: > 0 } list }) return null;
        var lib = new TurretSfxLib();
        foreach (TurretSound d in list)
        {
            if (MusyX.ReadWav(Path.Combine(dir, EchoesTurret.SoundFile(d.Role)), out int rate) is short[] pcm && pcm.Length > 0) lib.Sounds[d.Role] = (pcm, rate, d);
        }
        Log.Info("MPHAudio", $"Shooting Gallery turret: {lib.Sounds.Count} Echoes sounds ({string.Join(", ", lib.Sounds.Keys)})");
        return lib.Sounds.Count > 0 ? lib : null;
    }
}
