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

// The grapple beam in Echoes' look and sound (docs/GRAPPLE.md G2): the PARTs and WAVs the arena import copied from the
// player's own disc (EchoesGrapple.ImportLook: SamGunFx.pak's grapple effects in hd/grapple_fx/, the sounds beside them,
// listed in hd/grapple.json), run from each player's grapple state (PlayerGrapple.cs). Every device runs it from the
// seat's synced state, from its own copy.
//   muzzle: at the left wrist while the beam is out; claw: on the beam's tip; hit: where the claw catches.
//   segment: Echoes' beam (docs/GRAPPLE.md section 7.6): one system every half Echoes unit from the wrist to the tip
//   (2 x length + 1 of them), each pushed off the line by the arm's X / Z wave (TweakPlayerGun 0.25 / 0.125): sideways
//   by X cos(i + phase), up by Z sin(i), i the segment's number taken as radians and the phase 0.875 on a frame; both
//   amplitudes run down at 4/s once the claw holds, so the caught beam is a straight line.
//   fire: the shot (sfx 3149); loop: the beam's hum from the shot until it lets go (sfx 3148)
// The wrist is the posed body's L_wrist (PlayerEntity.HostGrappleHand: the left arm raised at the tip); the main
// player's own in first person is Echoes' grappling arm spot, low left of the view (TweakPlayerGun's Grappling arm
// position). SWHC (the swoosh's swept trail) has no player in RetroParticles yet: not drawn.
// The systems go in the drone effects' live list (_fxLive: stepped by TickDroneFx, drawn by DrawDroneFx).
internal sealed partial class CampaignRenderer
{
    const int GrappleSegmentsMax = 64;       // 25 units out is 51
    const float GrappleWaveX = 0.25f, GrappleWaveZ = 0.125f, GrappleWavePhase = 0.875f, GrappleWaveFade = 4f / 60;
    const float GrappleSfxVolume = 1f;
    const float GrappleSfxRange = 40f;

    sealed class GrappleFxLib
    {
        public RetroParticles Parts = null!;
        public readonly Dictionary<string, uint> Fx = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (short[] Pcm, int Rate)> Sounds = new(StringComparer.Ordinal);
    }

    sealed class GrappleFxSeat
    {
        public PlayerEntity.GrappleState State;
        public DroneFxLive? Muzzle, Claw;
        public readonly List<DroneFxLive> Segments = new();
        public object? Loop;
        public readonly SoundSource Sound = new();
        public int Age;
        public float Phase, Fade;
    }

    GrappleFxLib? _grappleFx;
    int _grappleFxRoom = -1;
    readonly Dictionary<PlayerEntity, GrappleFxSeat> _grappleSeats = new();
    readonly List<PlayerEntity> _grappleGone = new();

    // TickSkins (MatchSkins.cs), before TickDroneFx (which steps the systems in _fxLive)
    void TickGrappleFx()
    {
        if (_host == null || _host.Ended || PlayerEntity.HostGrapplePoints == null)
        {
            StopGrappleFx();
            return;
        }
        if (_grappleFxRoom != _host.RoomId)
        {
            StopGrappleFx();
            _grappleFxRoom = _host.RoomId;
            _grappleFx = LoadGrappleFx();
        }
        if (_grappleFx is not { } lib) return;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            bool on = p.Grapple != PlayerEntity.GrappleState.None;
            if (!_grappleSeats.TryGetValue(p, out GrappleFxSeat? seat))
            {
                if (!on) continue;
                _grappleSeats[p] = seat = new GrappleFxSeat();
            }
            TickGrappleSeat(lib, p, seat);
        }
        foreach ((PlayerEntity p, GrappleFxSeat seat) in _grappleSeats)
        {
            if (seat.State == PlayerEntity.GrappleState.None) _grappleGone.Add(p);
        }
        foreach (PlayerEntity p in _grappleGone) _grappleSeats.Remove(p);
        _grappleGone.Clear();
    }

    void TickGrappleSeat(GrappleFxLib lib, PlayerEntity p, GrappleFxSeat seat)
    {
        PlayerEntity.GrappleState now = p.Grapple;
        PlayerEntity.GrappleState was = seat.State;
        seat.State = now;
        if (now == PlayerEntity.GrappleState.None)
        {
            StopGrappleSeat(seat);
            return;
        }
        Vector3 hand = GrappleHandOf(p);
        Vector3 tip = p.GrappleTip;
        Vector3 along = tip - hand;
        float len = along.Length;
        Vector3 y = len > 1e-4f ? along / len : p.CameraInfo.Facing.LengthSquared > 1e-6f ? p.CameraInfo.Facing.Normalized() : Vector3.UnitZ;
        Vector3 x = Vector3.Cross(y, MathF.Abs(y.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
        Vector3 z = Vector3.Cross(x, y);

        // the shot
        if (was == PlayerEntity.GrappleState.None)
        {
            seat.Age = 0;
            seat.Phase = 0;
            seat.Fade = 0;
            PlaceGrappleSound(seat.Sound, p, hand);
            if (lib.Sounds.TryGetValue("fire", out var fire)) _sfx?.PlayPcm(fire.Pcm, fire.Rate, GrappleSfxVolume, seat.Sound);
        }
        seat.Age++;
        // the claw catching
        if (was == PlayerEntity.GrappleState.Firing && now != PlayerEntity.GrappleState.Firing) GrappleOnce(lib, "hit", tip, -y, x);

        seat.Muzzle = GrappleHeld(lib, seat.Muzzle, "muzzle", hand, y, x, z);
        seat.Claw = GrappleHeld(lib, seat.Claw, "claw", tip, y, x, z);

        // the beam: Echoes' segment wave, straightening once the claw holds
        float sc = EchoesArena.DefaultScale;
        int count = lib.Fx.ContainsKey("segment") ? Math.Clamp((int)(2 * len / sc + 1), 1, GrappleSegmentsMax) : 0;
        while (seat.Segments.Count > count)
        {
            seat.Segments[^1].Sys.Emitting = false;
            seat.Segments.RemoveAt(seat.Segments.Count - 1);
        }
        seat.Phase += GrappleWavePhase;
        if (seat.Phase > MathF.Tau) seat.Phase -= MathF.Tau;
        if (now != PlayerEntity.GrappleState.Firing) seat.Fade += GrappleWaveFade;
        float ax = MathF.Max(0, GrappleWaveX - seat.Fade) * sc;
        float az = MathF.Max(0, GrappleWaveZ - seat.Fade) * sc;
        for (int i = 0; i < count; i++)
        {
            Vector3 at = hand + along * ((float)i / count);
            if (i > 0) at += x * (ax * MathF.Cos(i + seat.Phase)) + z * (az * MathF.Sin(i));
            DroneFxLive? seg = i < seat.Segments.Count ? seat.Segments[i] : null;
            seg = GrappleHeld(lib, seg, "segment", at, y, x, z);
            if (seg == null) break;
            if (i < seat.Segments.Count) seat.Segments[i] = seg;
            else seat.Segments.Add(seg);
        }

        // the hum while the beam is out
        if (_sfx != null && lib.Sounds.TryGetValue("loop", out var loop))
        {
            PlaceGrappleSound(seat.Sound, p, hand);
            if (seat.Loop == null) seat.Loop = _sfx.PlayPcmLoop(loop.Pcm, loop.Rate, GrappleSfxVolume, seat.Sound);
            else _sfx.MovePcm(seat.Loop, GrappleSfxVolume, seat.Sound);
        }
    }

    // the left wrist: the posed body's (third person, other players), else Echoes' grappling arm spot under the view
    Vector3 GrappleHandOf(PlayerEntity p)
    {
        bool firstPerson = p == PlayerEntity.Main && !_thirdPerson;
        if (!firstPerson && p.HostGrappleHand is Vector3 wrist) return wrist;
        CameraInfo cam = p.CameraInfo;
        if (firstPerson && cam.Facing.LengthSquared > 1e-6f)
        {
            Vector3 f = cam.Facing.Normalized();
            Vector3 up = cam.UpVector.LengthSquared > 1e-6f ? cam.UpVector.Normalized() : Vector3.UnitY;
            Vector3 right = Vector3.Cross(f, up);
            right = right.LengthSquared > 1e-6f ? right.Normalized() : Vector3.UnitX;
            float s = EchoesArena.DefaultScale;
            // TweakPlayerGun's Grappling arm (-0.45, -0.1, -0.3): left and down; brought ahead of the near plane to be seen
            return cam.Position + right * (-0.45f * s) + f * (0.35f * s) + up * (-0.3f * s);
        }
        return p.Position + new Vector3(0, 0.9f, 0);
    }

    void PlaceGrappleSound(SoundSource src, PlayerEntity p, Vector3 at)
    {
        if (p == PlayerEntity.Main && !PlayerEntity.HostNoViewer) // a replay's free cam: placed, like anyone's
        {
            src.Update(at, -1);
            src.Self = true;
            return;
        }
        src.Position = at;
        src.Self = false;
        src.ReferenceDistance = 0;
        src.MaxDistance = GrappleSfxRange;
    }

    // a held system placed at `at` (y along the beam), made again if the live list was emptied (a replay's restart)
    DroneFxLive? GrappleHeld(GrappleFxLib lib, DroneFxLive? fx, string role, Vector3 at, Vector3 y, Vector3 x, Vector3 z)
    {
        Matrix4 m = GrappleMatrix(at, y, x, z);
        if (fx != null && _fxLive.Contains(fx))
        {
            fx.Sys.Transform = m;
            return fx;
        }
        if (!lib.Fx.TryGetValue(role, out uint id) || lib.Parts.Get(id) == null) return null;
        fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, m, follow: true, seed: Environment.TickCount ^ (int)id) };
        _fxLive.Add(fx);
        return fx;
    }

    void GrappleOnce(GrappleFxLib lib, string role, Vector3 at, Vector3 y, Vector3 x)
    {
        if (!lib.Fx.TryGetValue(role, out uint id) || lib.Parts.Get(id) == null) return;
        _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, GrappleMatrix(at, y, x, Vector3.Cross(x, y)), follow: true, seed: Environment.TickCount ^ (int)id) });
    }

    static Matrix4 GrappleMatrix(Vector3 at, Vector3 y, Vector3 x, Vector3 z)
    {
        float s = EchoesArena.DefaultScale;
        return new Matrix4(new Vector4(x * s, 0), new Vector4(y * s, 0), new Vector4(z * s, 0), new Vector4(at, 1));
    }

    void StopGrappleSeat(GrappleFxSeat seat)
    {
        StopFx(ref seat.Muzzle);
        StopFx(ref seat.Claw);
        foreach (DroneFxLive f in seat.Segments) f.Sys.Emitting = false;
        seat.Segments.Clear();
        if (seat.Loop != null) _sfx?.StopPcm(seat.Loop);
        seat.Loop = null;
        seat.State = PlayerEntity.GrappleState.None;
    }

    void StopGrappleFx()
    {
        foreach (GrappleFxSeat seat in _grappleSeats.Values) StopGrappleSeat(seat);
        _grappleSeats.Clear();
        if (_host == null || _host.Ended) _grappleFxRoom = -1;
    }

    // the arena's grapple look (hd/grapple.json, hd/grapple_fx/), null without it
    GrappleFxLib? LoadGrappleFx()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        if (EchoesGrapple.LoadLook(dir) is not { } look) return null;
        string fxDir = Path.Combine(dir, EchoesGrapple.FxDir);
        var lib = new GrappleFxLib();
        try
        {
            lib.Parts = Directory.Exists(fxDir) ? RetroParticles.Load(fxDir, echoes: true) : RetroParticles.Load(dir, echoes: true);
        }
        catch (Exception x)
        {
            Log.Warn("MPHArena", $"grapple beam: effects not read ({x.Message})");
            return null;
        }
        if (Directory.Exists(fxDir))
        {
            foreach (string png in Directory.GetFiles(fxDir, "*.png"))
            {
                if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) continue;
                _fxTexFile.TryAdd(t, png);
                _fxTexRepeat.Add(t);
            }
        }
        foreach ((string role, GrappleFx f) in look.Fx)
        {
            if (f.Kind == "part" && lib.Parts.Get(f.Id) != null) lib.Fx[role] = f.Id;
        }
        foreach (GrappleSound snd in look.Sounds)
        {
            string wav = Path.Combine(fxDir, EchoesGrapple.SoundFile(snd.Role));
            if (!File.Exists(wav)) wav = Path.Combine(dir, EchoesGrapple.SoundFile(snd.Role));
            if (File.Exists(wav) && MusyX.ReadWav(wav, out int rate) is short[] pcm && pcm.Length > 0) lib.Sounds[snd.Role] = (pcm, rate);
        }
        Log.Info("MPHArena", $"grapple beam: Echoes effects ({string.Join(", ", lib.Fx.Keys)}), sounds ({string.Join(", ", lib.Sounds.Keys)})");
        return lib;
    }
}
