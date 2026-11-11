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

// Mining Plaza's collapsing pillars in Echoes' look and sound: the PARTs and WAVs the arena import copied from the
// player's own disc (MiningPlazaPillars.ImportLook: Metroid3.pak's pillar effects in hd/pillar_fx/, the sounds beside
// them, listed in hd/pillar.json), run from each pillar's fall time (MiningPlazaPillarEntity.FallTime, synced online).
//   0 s: the blast at the hit (explosion, stopped at ExplosionOff), the hit sound and the slide's loop
//   TopGone: the slide stops, the scrape plays; Landed: the landing sound and the impact effect; DustAt: the dust
// A pillar first seen already down (joining late, a replay's seek) plays nothing: only what its time passes through.
// The Luminoth gate (MiningPlazaGateEntity.OpenTime, synced online): its idle hum until the open, then Echoes' Gate Control
// cues (GateSpot.Cues: squeak, stop, dust on/off, the lower loop on/off, the hum off).
// The systems go in the drone effects' live list (_fxLive: stepped by TickDroneFx, drawn by DrawDroneFx).
internal sealed partial class CampaignRenderer
{
    const float PillarSfxRange = 80f;
    // the gate's lowering grind (owner 2026-10-31: "not full volume everywhere but audible everywhere and volume depends
    // on your proximity"): full at the gate, down linearly to nothing at 200 units (the arena is ~100 across: about half
    // at the far side)
    const float GateGrindRange = 200f;

    sealed class PillarFxLib
    {
        public RetroParticles Parts = null!;
        public readonly Dictionary<string, uint> Fx = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (short[] Pcm, int Rate, float Volume)> Sounds = new(StringComparer.Ordinal);
    }

    sealed class PillarFxState
    {
        public float Last = -1;
        public DroneFxLive? Explosion;
        public object? Slide;
        public readonly SoundSource Sound = new();
    }

    sealed class GateFxState
    {
        public float Last = -1;
        public object? Idle, Lower;
        public DroneFxLive? DustFront, DustBack;
        public readonly SoundSource Sound = new(), Grind = new();
    }

    readonly Dictionary<MiningPlazaGateEntity, GateFxState> _gateStates = new();

    PillarFxLib? _pillarFx;
    int _pillarFxRoom = -1;
    readonly Dictionary<MiningPlazaPillarEntity, PillarFxState> _pillarStates = new();

    // TickSkins (MatchSkins.cs), before TickDroneFx (which steps the systems in _fxLive)
    void TickPillarFx()
    {
        if (_host == null || _host.Ended)
        {
            StopPillarFx();
            return;
        }
        if (_pillarFxRoom != _host.RoomId)
        {
            StopPillarFx();
            _pillarFxRoom = _host.RoomId;
            _pillarFx = LoadPillarFx();
        }
        TickSandBits(_pillarFx);
        if (_pillarFx is not { } lib) return;
        foreach (EntityBase e in _host.Scene.Entities)
        {
            if (e is MiningPlazaCrateEntity sc)
            {
                TickSandCrate(lib, sc);
                continue;
            }
            if (e is MiningPlazaGateEntity gt)
            {
                TickGate(lib, gt);
                continue;
            }
            if (e is not MiningPlazaPillarEntity pl) continue;
            if (!_pillarStates.TryGetValue(pl, out PillarFxState? st))
            {
                // first seen: what has already happened stays unplayed
                _pillarStates[pl] = st = new PillarFxState { Last = pl.FallTime };
                continue;
            }
            TickPillar(lib, pl, st);
        }
    }

    void TickPillar(PillarFxLib lib, MiningPlazaPillarEntity pl, PillarFxState st)
    {
        float now = pl.FallTime, was = st.Last;
        st.Last = now;
        if (now < was)
        {
            // stood up again (a replay's rewind): quiet, and the time runs from here
            StopPillar(st);
            return;
        }
        if (now < 0 || now == was) return;
        MiningPlazaPillars.PillarSpot s = pl.Spot;
        bool Passed(float t) => was < t && now >= t;
        st.Sound.Position = V(s.Explosion);
        st.Sound.Self = false;
        st.Sound.ReferenceDistance = 0;
        st.Sound.MaxDistance = PillarSfxRange;
        if (was < 0)
        {
            st.Explosion = PillarOnce(lib, "explosion", V(s.Explosion), null);
            PillarSound(lib, "hit", st.Sound);
            if (_sfx != null && lib.Sounds.TryGetValue("slide", out var slide)) st.Slide = _sfx.PlayPcmLoop(slide.Pcm, slide.Rate, slide.Volume, st.Sound);
        }
        if (Passed(MiningPlazaPillars.ExplosionOff)) StopFx(ref st.Explosion);
        if (Passed(MiningPlazaPillars.TopGone))
        {
            if (st.Slide != null) _sfx?.StopPcm(st.Slide);
            st.Slide = null;
            PillarSound(lib, "scrape", st.Sound);
        }
        if (Passed(MiningPlazaPillars.Landed))
        {
            st.Sound.Position = V(s.Impact);
            PillarSound(lib, "impact", st.Sound);
            PillarOnce(lib, "impact", V(s.Impact), s.ImpactAxes);
        }
        if (Passed(MiningPlazaPillars.DustAt))
        {
            PillarOnce(lib, "dust", V(s.Dust1), null);
            PillarOnce(lib, "dust", V(s.Dust2), null);
        }
    }

    void TickGate(PillarFxLib lib, MiningPlazaGateEntity gt)
    {
        float now = gt.OpenTime;
        if (!_gateStates.TryGetValue(gt, out GateFxState? st))
        {
            _gateStates[gt] = st = new GateFxState();
            st.Sound.Position = V(gt.Spot.Center);
            st.Sound.Self = false;
            st.Sound.ReferenceDistance = 0;
            st.Sound.MaxDistance = PillarSfxRange;
            st.Grind.Position = st.Sound.Position;
            st.Grind.ReferenceDistance = 0;
            st.Grind.MaxDistance = GateGrindRange;
            // first seen already open: what has already happened stays unplayed
            st.Last = now;
            if (now < 0) st.Idle = GateLoop(lib, "gate_idle", st.Sound);
            return;
        }
        float was = st.Last;
        st.Last = now;
        if (now < was)
        {
            // shut again (a replay's rewind): quiet, the hum back on if it's shut
            StopGate(st);
            if (now < 0) st.Idle = GateLoop(lib, "gate_idle", st.Sound);
            if (now <= 0) return;
            was = 0;
        }
        if (now < 0 || now == was) return;
        MiningPlazaGate.GateSpot s = gt.Spot;
        // the jingle, at the open, the same for everyone (not positional; owner 2026-10-31 "standard volume for everyone")
        if (was < 0) PillarSound(lib, "gate_jingle", null, everyone: true); // the whole match hears it, free cam too
        foreach (MiningPlazaGate.GateCue c in s.Cues)
        {
            // the cues at 0 play on the open's first step
            if (!(was < c.At && now >= c.At) && !(c.At == 0 && was < 0)) continue;
            switch (c.Role)
            {
            case "squeak": PillarSound(lib, "gate_squeak", st.Sound); break;
            case "stop": PillarSound(lib, "gate_stop", st.Sound); break;
            case "dust":
                StopFx(ref st.DustFront);
                StopFx(ref st.DustBack);
                st.DustFront = PillarOnce(lib, "gatedust", V(s.DustFront), null);
                st.DustBack = PillarOnce(lib, "gatedust", V(s.DustBack), null);
                break;
            case "dustoff": StopFx(ref st.DustFront); StopFx(ref st.DustBack); break;
            case "lower": st.Lower ??= GateLoop(lib, "gate_lower", st.Grind); break;
            case "loweroff": if (st.Lower != null) _sfx?.StopPcm(st.Lower); st.Lower = null; break;
            case "idleoff": if (st.Idle != null) _sfx?.StopPcm(st.Idle); st.Idle = null; break;
            }
        }
        // at its end place the grind and hum stop, whatever cues the import has (an older import had no loweroff)
        if (s.MoveEnd > 0 && now >= s.MoveEnd)
        {
            if (st.Lower != null) _sfx?.StopPcm(st.Lower);
            if (st.Idle != null) _sfx?.StopPcm(st.Idle);
            st.Lower = st.Idle = null;
        }
    }

    object? GateLoop(PillarFxLib lib, string role, SoundSource src)
        => _sfx != null && lib.Sounds.TryGetValue(role, out var snd) ? _sfx.PlayPcmLoop(snd.Pcm, snd.Rate, snd.Volume, src) : null;

    void StopGate(GateFxState st)
    {
        StopFx(ref st.DustFront);
        StopFx(ref st.DustBack);
        if (st.Idle != null) _sfx?.StopPcm(st.Idle);
        if (st.Lower != null) _sfx?.StopPcm(st.Lower);
        st.Idle = st.Lower = null;
    }

    static Vector3 V(float[] a) => a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;

    void PillarSound(PillarFxLib lib, string role, SoundSource? src, bool everyone = false)
    {
        if (_sfx != null && lib.Sounds.TryGetValue(role, out var snd)) _sfx.PlayPcm(snd.Pcm, snd.Rate, snd.Volume, src, everyone);
    }

    // a system at `at`: Echoes' axes as the room maps them (X, Y = -Z, Z = up), or the import's own (the impact's turn)
    DroneFxLive? PillarOnce(PillarFxLib lib, string role, Vector3 at, float[]? axes)
    {
        if (!lib.Fx.TryGetValue(role, out uint id) || lib.Parts.Get(id) == null) return null;
        Vector3 x = Vector3.UnitX, y = -Vector3.UnitZ, z = Vector3.UnitY;
        if (axes is { Length: 9 })
        {
            x = new Vector3(axes[0], axes[1], axes[2]);
            y = new Vector3(axes[3], axes[4], axes[5]);
            z = new Vector3(axes[6], axes[7], axes[8]);
        }
        var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, GrappleMatrix(at, y, x, z), follow: true, seed: Environment.TickCount ^ (int)id) };
        _fxLive.Add(fx);
        return fx;
    }

    void StopPillar(PillarFxState st)
    {
        StopFx(ref st.Explosion);
        if (st.Slide != null) _sfx?.StopPcm(st.Slide);
        st.Slide = null;
    }

    void StopPillarFx()
    {
        foreach (PillarFxState st in _pillarStates.Values) StopPillar(st);
        _pillarStates.Clear();
        foreach (GateFxState st in _gateStates.Values) StopGate(st);
        _gateStates.Clear();
        StopSandCrates();
        if (_host == null || _host.Ended) _pillarFxRoom = -1;
    }

    // the arena's pillar look (hd/pillar.json, hd/pillar_fx/), null without it (every room but Mining Plaza)
    PillarFxLib? LoadPillarFx()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        if (MiningPlazaPillars.LoadLook(dir) is not { } look) return null;
        string fxDir = Path.Combine(dir, MiningPlazaPillars.FxDir);
        if (!Directory.Exists(fxDir)) return null;
        var lib = new PillarFxLib();
        try
        {
            lib.Parts = RetroParticles.Load(fxDir, echoes: true);
        }
        catch (Exception x)
        {
            Log.Warn("MPHArena", $"pillars: effects not read ({x.Message})");
            return null;
        }
        foreach (string png in Directory.GetFiles(fxDir, "*.png"))
        {
            if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) continue;
            _fxTexFile.TryAdd(t, png);
            _fxTexRepeat.Add(t);
        }
        foreach ((string role, uint id) in look.Fx)
        {
            if (lib.Parts.Get(id) != null) lib.Fx[role] = id;
        }
        foreach (MiningPlazaPillars.PillarSound snd in look.Sounds)
        {
            string wav = Path.Combine(fxDir, MiningPlazaPillars.SoundFile(snd.Role));
            if (File.Exists(wav) && MusyX.ReadWav(wav, out int rate) is short[] pcm && pcm.Length > 0) lib.Sounds[snd.Role] = (pcm, rate, snd.Volume);
        }
        Log.Info("MPHArena", $"pillars: Echoes effects ({string.Join(", ", lib.Fx.Keys)}), sounds ({string.Join(", ", lib.Sounds.Keys)})");
        return lib;
    }
}
