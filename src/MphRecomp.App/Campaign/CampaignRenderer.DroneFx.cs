using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRecomp.Campaign;
using MphRecomp.Import.Effects;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Scout Drone's shots in Prime 1's look (owner 2026-10-08: "make the beam real from mp1"; picked "Flying, Prime
// look" + firing glow + unfold charge glow): the effects the drone's own firing clips play in Prime 1 (bodies/<drone>/fx,
// Core Import/Effects/BodyFx.cs, run by Core Campaign/RetroParticles.cs) on its bones: the cyan back-panel puffs and the
// beacon sparkle while it fires, the blue-to-orange glow on its hands as it unfolds, Prime's gun flash at each gun as it
// shoots, Prime's hit effect (its CRSC's default) where each shot lands, and in flight a short white-to-yellow streak in
// Prime's glow (our addition: Prime's drone fires instant rays). MPH's Power Beam look for those shots is hidden
// (BeamProjectileEntity.HostOwnLook, Scene.HostSpawnHidden), its effects still running. Drawing only: the shots, their
// speed, damage and hits are the simulation's (PlayerScoutDrone), the same with or without the import.
// Prime's sounds too (owner: "Prime's sounds"; fx/sfx_<id>.wav, Core Import/Retro/MusyX.cs): the unfold charge as it
// unfolds, the blast once per volley, the cutting laser as it starts, the impact at each hit, from where they happen
// (GameSfxPlayer.PlayPcm); MphRead's Power Beam shot / hit and its stand-in laser sound are then not played
// (HostOwnDroneShotSfx, HostOwnDroneLaserSfx, BeamProjectileEntity.HostOwnSfx).
// Kept on longer than Prime (owner 2026-10-09: "present the whole time firing" / "present at all times"): the hands' charge
// glow, Prime's only as it unfolds, from its unfold start through the whole burst; the beacon sparkle on the eye, Prime's
// only while it fires, whenever the drone is out.
internal sealed partial class CampaignRenderer
{
    const uint DroneGunFlash = 0x06296852;
    const uint DroneHandGlow = 0xa3b5243a, DroneBeacon = 0xd3792285;
    // false: Prime's timing for the two above (the clip events only)
    const bool DroneGlowsHeld = true;
    const uint DroneGlowTex = 0x29782ca6, DroneFlashTex = 0x94d4001c;
    // the streak in flight: its most length behind the shot, widths of its yellow glow and white core, head flash size
    const float StreakLength = 1.0f, StreakGlow = 0.24f, StreakCore = 0.09f, StreakHead = 0.3f;
    // a billboard's distance from the camera over its width: gone at FxNearGone, whole from FxNearWhole
    const float FxNearGone = 0.5f, FxNearWhole = 1.5f;
    static readonly string[] DroneFxClips = { "", "A_intoshootloop_drone", "A_shootloop_drone", "A_outofshootloop_drone" };
    const string DroneLaserClip = "A_cuttinglaser_drone"; // as long as the sim's laser (134 frames): its events 1:1
    // Prime's cutting laser (CDroneLaser::Render, Metaforce): three plain tubes from the drone to where it burns, radius in
    // Prime metres and colour, the first added (One, One), the others (SrcAlpha, One); drawn as camera-facing strips at
    // the drone's scale, never thinner than about a pixel and a half
    static readonly (float Radius, Vector4 Color)[] DroneLaserTubes =
    {
        (0.08f, new Vector4(0.4f, 0.2f, 0.2f, 0.1f)), (0.06f, new Vector4(0.4f, 0, 0, 0.5f)), (0.01f, new Vector4(1, 0.9f, 0.9f, 1)),
    };
    const float DroneLaserMinHalf = 0.0008f; // half width per unit of distance from the camera
    // the sim's unfold / fold lengths (PlayerScoutDrone): the clips (and their events) stretch to them
    static readonly int[] DroneFxStateFrames = { 0, 32, 0, 24 };
    const uint DroneSpawnSound = 632; // Prime's drone alert: macro 255 loops its beep four times
    const float DroneSfxVolume = 1f;  // Prime's macro volumes are in the sounds already
    const int DroneSfxRange = 1;      // MPH's sound range for another player (PlayerEntity: rangeIndex 1)
    static readonly Dictionary<uint, (short[] Pcm, int Rate)> NoSounds = new();
    readonly SoundSource _fxHitSound = new();
    // Prime effect space (Z up) -> the body's model space (Import/Bodies: (x, y, z) -> (-x, z, y))
    static readonly Matrix4 PrimeToModel = new(-1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);

    sealed class DroneFxSeat
    {
        public int State = -1, Frame, LaserFrame = -1;
        public readonly int[] Shots = new int[2];
        public bool Seen;
        public DroneFxLive? Burn;        // the laser's burn effect while it lands on something
        public DroneFxLive? Beacon;      // the eye's sparkle while the drone is out
        public readonly DroneFxLive?[] Hands = new DroneFxLive?[2]; // the hands' glow through a burst
        public readonly SoundSource Sound = new(); // where its drone is, for Prime's sounds
    }

    sealed class DroneFxLive
    {
        public RetroParticleSystem Sys = null!;
        public PlayerEntity? P;          // riding this seat's drone bone (null: placed once, a hit)
        public CampaignRetroBody? Body;
        public int Bone = -1, Mode, EmitLeft;
        public float Scale = 1;
        public float Fade = 1;           // times its alpha (the Echoes turret's own effects seen by its occupant)
    }

    readonly Dictionary<PlayerEntity, DroneFxSeat> _fxSeats = new();
    readonly List<DroneFxLive> _fxLive = new();
    readonly List<(BeamProjectileEntity Beam, CollisionResult Col)> _fxHits = new();
    readonly Dictionary<uint, string> _fxTexFile = new();
    // textures sampled repeating, as Retro loads every particle texture (CElementGen: CTexture::kCM_Repeat): Echoes'
    // turret effects (an ATEX tile can run past the texture: the column bolts' 256-wide tiles on a 128 texture)
    readonly HashSet<uint> _fxTexRepeat = new();
    readonly Dictionary<uint, int> _fxTex = new();
    readonly HashSet<CampaignRetroBody> _fxTexListed = new();
    readonly List<RetroBillboard> _fxBoards = new();
    readonly Dictionary<PlayerEntity, Matrix4?> _fxPose = new();
    float[] _fxVerts = new float[60 * 64];
    int _fxProg, _fxVao, _fxVbo, _fxWhite;

    // TickSkins (MatchSkins.cs), after each sim step: the drone seats' effects one 60 Hz frame on
    void TickDroneFx()
    {
        _fxPose.Clear();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || !p.HostScoutDrone) continue;
            TickDroneSpawnSound(p);
            CampaignRetroBody? d = p.IsAltForm ? DroneOf(p) : null;
            if (d?.Fx is not (RetroParticles lib, BodyFx.Config cfg))
            {
                if (_fxSeats.Remove(p, out DroneFxSeat? gone))
                {
                    if (gone.Burn != null) gone.Burn.Sys.Emitting = false;
                    if (gone.Beacon != null) gone.Beacon.Sys.Emitting = false;
                    foreach (DroneFxLive? h in gone.Hands)
                    {
                        if (h != null) h.Sys.Emitting = false;
                    }
                }
                p.HostOwnDroneLaser = false;
                p.HostOwnDroneShotSfx = false;
                p.HostOwnDroneLaserSfx = false;
                continue;
            }
            // Prime's beam is ours to draw when the import has its burn effect (else MphRead's stand-in line)
            p.HostOwnDroneLaser = cfg.Laser != 0 && lib.Get(cfg.Laser) != null;
            BeamProjectileEntity.HostImpact = OnHostShotHit; // the drone's hits, and the Pirate Blaster's (CampaignRenderer.BlasterFx.cs)
            // Prime's sounds are ours to play when the import has them (else MphRead's)
            IReadOnlyDictionary<uint, (short[] Pcm, int Rate)> snd = _sfx != null ? d.FxSounds : NoSounds;
            uint blast = ClipSound(cfg, DroneFxClips[2]);
            p.HostOwnDroneShotSfx = snd.ContainsKey(blast);
            p.HostOwnDroneLaserSfx = snd.ContainsKey(ClipSound(cfg, DroneLaserClip));
            bool ownHit = snd.ContainsKey(cfg.ImpactSound);
            if (p.DroneBeams is BeamProjectileEntity[] beams)
            {
                foreach (BeamProjectileEntity b in beams)
                {
                    b.HostOwnLook = true;
                    b.HostOwnSfx = ownHit;
                }
            }
            if (_fxTexListed.Add(d))
            {
                foreach (string png in Directory.GetFiles(d.FxDir, "*.png"))
                {
                    if (UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) _fxTexFile.TryAdd(t, png);
                }
            }
            if (!_fxSeats.TryGetValue(p, out DroneFxSeat? seat)) _fxSeats[p] = seat = new DroneFxSeat();
            seat.Sound.Update(p.DroneDrawCenter, DroneSfxRange);
            // the clip's own effects, timed by the sim's burst state (as DroneBody.Animator plays the clips)
            int state = p.DroneFireState;
            if (state != seat.State)
            {
                seat.State = state;
                seat.Frame = 0;
            }
            if (state is >= 1 and <= 3)
            {
                string clip = DroneFxClips[state];
                float dur = d.ClipSeconds(clip);
                int len = state == 2 ? Math.Max(1, (int)MathF.Round(dur * 60)) : DroneFxStateFrames[state];
                int f = state == 2 ? seat.Frame % len : seat.Frame;
                foreach (BodyFx.Event e in cfg.Events)
                {
                    if (e.Clip != clip || e.Part == DroneGunFlash) continue;
                    if (DroneGlowsHeld && e.Part == DroneBeacon) continue; // always on, below
                    int at = state == 2 ? (int)MathF.Round(e.Time * 60) : dur > 0 ? (int)MathF.Round(e.Time / dur * len) : 0;
                    if (DroneGlowsHeld && e.Part == DroneHandGlow)
                    {
                        // held from its unfold start through the firing loop
                        if (state == 1 && f >= at) HoldDroneFx(ref seat.Hands[e.Bone.StartsWith("L_") ? 0 : 1], lib, e, p, d);
                        continue;
                    }
                    if (at == f) SpawnDroneFx(lib, e, p, d);
                }
                if (DroneGlowsHeld && state == 2)
                {
                    foreach (BodyFx.Event e in cfg.Events)
                    {
                        if (e.Part == DroneHandGlow) HoldDroneFx(ref seat.Hands[e.Bone.StartsWith("L_") ? 0 : 1], lib, e, p, d);
                    }
                }
                // the unfold's charge (the firing loop's blast plays per volley, below)
                if (state != 2)
                {
                    foreach (BodyFx.Sound sd in cfg.Sounds)
                    {
                        int at = dur > 0 ? (int)MathF.Round(sd.Time / dur * len) : 0;
                        if (sd.Clip == clip && at == f) PlayDroneSound(snd, sd.Id, seat.Sound);
                    }
                }
                seat.Frame++;
            }
            if (state is not (1 or 2))
            {
                for (int h = 0; h < 2; h++)
                {
                    if (seat.Hands[h] != null) seat.Hands[h]!.Sys.Emitting = false;
                    seat.Hands[h] = null;
                }
            }
            if (DroneGlowsHeld)
            {
                foreach (BodyFx.Event e in cfg.Events)
                {
                    if (e.Part == DroneBeacon)
                    {
                        HoldDroneFx(ref seat.Beacon, lib, e, p, d);
                        break;
                    }
                }
            }
            // Prime's gun flash at each gun as it shoots (its event in the firing loop, on the sim's own shots), and its blast
            // once a volley (both guns fire together)
            bool volley = false;
            for (int i = 0; i < 2; i++)
            {
                int n = p.DroneShotsBySide[i];
                if (seat.Seen && n > seat.Shots[i])
                {
                    volley = true;
                    string bone = i == 0 ? "L_GUN_BOTTOM_LCTR" : "R_GUN_BOTTOM_LCTR";
                    foreach (BodyFx.Event e in cfg.Events)
                    {
                        if (e.Part == DroneGunFlash && e.Bone == bone)
                        {
                            SpawnDroneFx(lib, e, p, d);
                            break;
                        }
                    }
                }
                seat.Shots[i] = n;
            }
            if (volley) PlayDroneSound(snd, blast, seat.Sound);
            seat.Seen = true;
            // the cutting laser: its clip's effects (beacon sparkle, panel puffs, the flare as the beam comes on) on the sim's
            // laser frames, and where the beam lands Prime's burn
            int lf = p.DroneLaserFrame;
            if (lf >= 0 && lf != seat.LaserFrame)
            {
                foreach (BodyFx.Event e in cfg.Events)
                {
                    if (DroneGlowsHeld && e.Part == DroneBeacon) continue;
                    if (e.Clip == DroneLaserClip && (int)MathF.Round(e.Time * 60) == lf) SpawnDroneFx(lib, e, p, d);
                }
                foreach (BodyFx.Sound sd in cfg.Sounds)
                {
                    if (sd.Clip == DroneLaserClip && (int)MathF.Round(sd.Time * 60) == lf) PlayDroneSound(snd, sd.Id, seat.Sound);
                }
            }
            seat.LaserFrame = lf;
            Vector3 normal = p.DroneLaserNormal;
            if (p.HostOwnDroneLaser && p.DroneLaserOn && normal.LengthSquared > 1e-6f)
            {
                if (seat.Burn == null || !_fxLive.Contains(seat.Burn))
                {
                    seat.Burn = new DroneFxLive { Sys = new RetroParticleSystem(lib, cfg.Laser, Matrix4.Identity, follow: false, seed: Environment.TickCount) };
                    _fxLive.Add(seat.Burn);
                }
                seat.Burn.Sys.Transform = BurnFrame(normal, p.DroneLaserEnd, p.DroneDrawAim);
                seat.Burn.Sys.Emitting = true;
            }
            else if (seat.Burn != null)
            {
                seat.Burn.Sys.Emitting = false;
                seat.Burn = null;
            }
        }
        // the hits the simulation reported this step: Prime's hit effect, facing out of the surface
        foreach ((BeamProjectileEntity beam, CollisionResult col) in _fxHits)
        {
            if (beam.Owner is not PlayerEntity p || DroneOf(p) is not CampaignRetroBody d || d.Fx is not (RetroParticles lib, BodyFx.Config cfg))
            {
                continue;
            }
            if (_sfx != null)
            {
                _fxHitSound.Update(col.Position, DroneSfxRange);
                PlayDroneSound(d.FxSounds, cfg.ImpactSound, _fxHitSound);
            }
            if (cfg.Impact == 0 || lib.Get(cfg.Impact) == null) continue;
            Vector3 n = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -beam.Direction;
            Matrix4 at = Frame(n, col.Position + n * 0.05f, DroneBody.Scale);
            _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib, cfg.Impact, at, follow: false, seed: Environment.TickCount), EmitLeft = 2 });
        }
        _fxHits.Clear();
        // every live effect one frame on, those on a bone moved with it
        for (int i = _fxLive.Count - 1; i >= 0; i--)
        {
            DroneFxLive fx = _fxLive[i];
            if (fx.P != null && fx.Body != null && fx.Bone >= 0)
            {
                if (fx.P.IsAltForm && DroneOf(fx.P) == fx.Body && PoseOf(fx.P, fx.Body) is Matrix4 model)
                {
                    if (fx.Mode != 0) fx.Sys.Transform = BoneFrame(fx.Body, fx.Bone, model, fx.Scale);
                }
                else if (fx.Mode == 2)
                {
                    // riding a drone that's gone (recalled, killed, not posed): it goes with it, else what's left of it
                    // (the eye's sparkle) stays floating where the drone last was
                    _fxLive.RemoveAt(i);
                    continue;
                }
                else fx.Sys.Emitting = false;
            }
            if (fx.EmitLeft > 0 && --fx.EmitLeft == 0) fx.Sys.Emitting = false;
            fx.Sys.Step();
            if (!fx.Sys.Emitting && fx.Sys.Done) _fxLive.RemoveAt(i);
        }
    }

    // a replay restarting its match (MatchReplay.RestartReplay): every drone effect off
    void ResetDroneFx()
    {
        _fxLive.Clear();
        _fxSeats.Clear();
        _fxHits.Clear();
        _fxPose.Clear();
        ResetBlasterFx(); // the Pirate Blaster's bolts (CampaignRenderer.BlasterFx.cs)
        ResetParalyzerFx(); // the Paralyzer's Brawl look (CampaignRenderer.ParalyzerFx.cs)
        ResetHyperBeamFx(); // the Hyper Beam's Corruption look (CampaignRenderer.HyperBeamFx.cs)
        ResetHyperFx(); // Dark Samus's Hyper Ball look and hum (CampaignRenderer.HyperFx.cs)
        ResetTurretFx(); // Shooting Gallery turret's Echoes effects (CampaignRenderer.TurretFx.cs)
    }

    // the sound a clip's EVNT plays (the first; none: UInt32.MaxValue)
    static uint ClipSound(BodyFx.Config cfg, string clip)
    {
        foreach (BodyFx.Sound sd in cfg.Sounds)
        {
            if (sd.Clip == clip) return sd.Id;
        }
        return UInt32.MaxValue;
    }

    readonly HashSet<uint> _droneSfxLogged = new();

    readonly Dictionary<PlayerEntity, (int Splits, SoundSource At)> _droneSplit = new();

    // Prime's drone alert (four beeps, SFXdrn_b_alert_00) where the drone deploys, in place of the hunter's spawn sound.
    // Runs for the seat in or out of its alt form: the split happens on the step it enters it, before any seat exists
    void TickDroneSpawnSound(PlayerEntity p)
    {
        CampaignRetroBody? d = DroneOf(p);
        IReadOnlyDictionary<uint, (short[] Pcm, int Rate)> snd = _sfx != null && d?.Fx != null ? d.FxSounds : NoSounds;
        p.HostOwnDroneSpawnSfx = snd.ContainsKey(DroneSpawnSound);
        if (!_droneSplit.TryGetValue(p, out var st)) st = (p.DroneSplits, new SoundSource());
        st.At.Update(p.DroneDrawCenter, DroneSfxRange);
        if (p.DroneSplits != st.Splits) PlayDroneSound(snd, DroneSpawnSound, st.At);
        _droneSplit[p] = (p.DroneSplits, st.At);
    }

    void PlayDroneSound(IReadOnlyDictionary<uint, (short[] Pcm, int Rate)> snd, uint id, SoundSource at)
    {
        if (_sfx == null || !snd.TryGetValue(id, out (short[] Pcm, int Rate) s)) return;
        _sfx.PlayPcm(s.Pcm, s.Rate, DroneSfxVolume, at);
        if (_droneSfxLogged.Add(id)) Log.Info("MPHAudio", $"drone: Prime sound {id} ({s.Pcm.Length} samples @ {s.Rate} Hz)");
    }

    void OnDroneShotHit(BeamProjectileEntity beam, CollisionResult col, bool expired)
    {
        // a shot that runs out of life in the air hits nothing (Prime's ray would fly on); a weapon the app draws itself
        // (the Shooting Gallery turret's shots, fired by a seat that may have a drone) has its own hit effect
        if (!expired && !beam.HostWeaponLook && _fxHits.Count < 64) _fxHits.Add((beam, col));
    }

    Matrix4? PoseOf(PlayerEntity p, CampaignRetroBody d)
    {
        if (!_fxPose.TryGetValue(p, out Matrix4? m)) _fxPose[p] = m = d.PoseDrone(p);
        else if (m != null) d.PoseDrone(p); // the body is shared by the seats wearing it: pose this one again
        return m;
    }

    static Matrix4 BoneFrame(CampaignRetroBody d, int bone, Matrix4 model, float scale)
        => Matrix4.CreateScale(scale) * PrimeToModel * d.Body.World[bone] * model;

    // effect space with +Z along n (Prime's hit faces out of the surface), scaled, at a point
    static Matrix4 Frame(Vector3 n, Vector3 at, float scale)
    {
        Vector3 x = MathF.Abs(n.Y) < 0.9f ? Vector3.Cross(Vector3.UnitY, n).Normalized() : Vector3.Cross(Vector3.UnitX, n).Normalized();
        Vector3 y = Vector3.Cross(n, x);
        var m = Matrix4.Identity;
        m.Row0.Xyz = x * scale;
        m.Row1.Xyz = y * scale;
        m.Row2.Xyz = n * scale;
        m.Row3.Xyz = at;
        return m;
    }

    // the burn's effect space as Prime's CDroneLaser sets it (LookAt the surface normal): +Y out of the surface, +Z as near
    // the world's up as it can be (for a floor or ceiling, as near the aim), at the drone's scale
    static Matrix4 BurnFrame(Vector3 normal, Vector3 at, Vector3 aim)
    {
        Vector3 y = normal.Normalized();
        Vector3 refUp = MathF.Abs(y.Y) < 0.95f ? Vector3.UnitY : (aim.LengthSquared > 1e-6f ? aim.Normalized() : Vector3.UnitZ);
        Vector3 z = refUp - y * Vector3.Dot(refUp, y);
        z = z.LengthSquared > 1e-6f ? z.Normalized() : Vector3.Cross(y, Vector3.UnitX).Normalized();
        Vector3 x = Vector3.Cross(y, z);
        var m = Matrix4.Identity;
        m.Row0.Xyz = x * DroneBody.Scale;
        m.Row1.Xyz = y * DroneBody.Scale;
        m.Row2.Xyz = z * DroneBody.Scale;
        m.Row3.Xyz = at;
        return m;
    }

    // an event's effect on its bone for its frames (held: emitting until stopped)
    DroneFxLive? SpawnDroneFx(RetroParticles lib, BodyFx.Event e, PlayerEntity p, CampaignRetroBody d, bool held = false)
    {
        if (lib.Get(e.Part) == null) return null;
        int bone = d.Body.Bone(e.Bone);
        if (bone < 0 || PoseOf(p, d) is not Matrix4 model) return null;
        float scale = e.Scale > 0 ? e.Scale : 1;
        Matrix4 at = BoneFrame(d, bone, model, scale);
        var fx = new DroneFxLive
        {
            Sys = new RetroParticleSystem(lib, e.Part, at, follow: e.Mode == 2, seed: Environment.TickCount ^ bone),
            P = p, Body = d, Bone = bone, Mode = e.Mode, EmitLeft = held ? 0 : Math.Max(1, e.Frames), Scale = scale,
        };
        _fxLive.Add(fx);
        return fx;
    }

    // keeps a held effect emitting (a new one once the last has been stopped)
    void HoldDroneFx(ref DroneFxLive? live, RetroParticles lib, BodyFx.Event e, PlayerEntity p, CampaignRetroBody d)
    {
        if (live == null || !live.Sys.Emitting || !_fxLive.Contains(live)) live = SpawnDroneFx(lib, e, p, d, held: true);
    }

    // DrawSkins (MatchSkins.cs), additive pass: every live effect's particles and the drone shots' streaks
    void DrawDroneFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene) return;
        _fxBoards.Clear();
        Vector3 cam = scene.CameraPosition;
        foreach (DroneFxLive fx in _fxLive)
        {
            int from = _fxBoards.Count;
            fx.Sys.Collect(_fxBoards);
            // the local player's own drone fades its effects with its body (MatchDrones.DroneAlpha), and any effect
            // fades as the camera gets inside it (Prime's glows are metres wide: in the alt form's close camera one
            // would fill the screen)
            float alpha = (fx.P != null ? DroneAlpha(scene, fx.P) : 1) * fx.Fade;
            // smooth frames: carried with the seat, as its drone and body are (SmoothView)
            Matrix4 carry = SmoothCarry(fx.P, scene);
            bool carried = carry != Matrix4.Identity;
            for (int k = from; k < _fxBoards.Count; k++)
            {
                RetroBillboard bb = _fxBoards[k];
                if (carried)
                {
                    bb.Position = Vector3.TransformPosition(bb.Position, carry);
                    bb.Fore = Vector3.TransformVector(bb.Fore, carry);
                }
                float near = bb.Size > 1e-4f ? ((bb.Position - cam).Length / bb.Size - FxNearGone) / (FxNearWhole - FxNearGone) : 1;
                bb.Color.W *= alpha * Math.Clamp(near, 0, 1);
                _fxBoards[k] = bb;
            }
        }
        _fxBoards.RemoveAll(bb => bb.Color.W <= 0.002f);
        int boards = _fxBoards.Count;
        int streaks = 0;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p?.DroneBeams is not BeamProjectileEntity[] beams || !_fxSeats.ContainsKey(p)) continue;
            foreach (BeamProjectileEntity b in beams)
            {
                if (!b.HostOwnLook || b.Lifespan <= 0 || b.Flags.TestFlag(BeamFlags.Collided)) continue;
                streaks++;
            }
        }
        int lasers = 0;
        foreach (PlayerEntity p in _fxSeats.Keys)
        {
            if (p.HostOwnDroneLaser && p.DroneLaserOn) lasers++;
        }
        if (boards == 0 && streaks == 0 && lasers == 0) return;
        if (!FxGl()) return;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var fwd = -new Vector3(view.M13, view.M23, view.M33);
        // alpha-blended ones first, then the additive ones, grouped by texture
        _fxBoards.Sort((a, b) => a.Additive != b.Additive ? (a.Additive ? 1 : -1) : a.Texture.CompareTo(b.Texture));
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        int i = 0;
        while (i < _fxBoards.Count)
        {
            RetroBillboard first = _fxBoards[i];
            int n = 0;
            int j = i;
            for (; j < _fxBoards.Count && _fxBoards[j].Additive == first.Additive && _fxBoards[j].Texture == first.Texture; j++)
            {
                RetroBillboard bb = _fxBoards[j];
                if (bb.Fore != Vector3.Zero)
                {
                    // ORNT: u along Fore, v across (Fore x the camera's forward, times Width), as Retro's corners
                    Vector3 f = bb.Fore / 2, r = Vector3.Cross(bb.Fore, fwd) * bb.Width / 2;
                    Quad(ref n, vp, bb.Position - f + r, bb.Position + f + r, bb.Position - f - r, bb.Position + f - r, bb.Color, uv: bb.Uv);
                    continue;
                }
                float rad = MathHelper.DegreesToRadians(bb.Rotation);
                float c = MathF.Cos(rad) * bb.Size / 2, s = MathF.Sin(rad) * bb.Size / 2;
                Vector3 ax = right * c + up * s, ay = -right * s + up * c;
                Quad(ref n, vp, bb.Position - ax + ay, bb.Position + ax + ay, bb.Position - ax - ay, bb.Position + ax - ay, bb.Color, uv: bb.Uv);
            }
            DrawRun(first.Texture, first.Additive, n);
            i = j;
        }
        // the streaks: a yellow glow and a white core from a little behind each shot to it, and a flash at its head
        if (streaks > 0)
        {
            foreach (int layer in new[] { 0, 1, 2 })
            {
                int n = 0;
                foreach (PlayerEntity p in PlayerEntity.Players)
                {
                    if (p?.DroneBeams is not BeamProjectileEntity[] beams || !_fxSeats.ContainsKey(p)) continue;
                    foreach (BeamProjectileEntity b in beams)
                    {
                        if (!b.HostOwnLook || b.Lifespan <= 0 || b.Flags.TestFlag(BeamFlags.Collided)) continue;
                        Vector3 head = Vector3.TransformPosition(b.Position, SmoothCarry(b, scene));
                        Vector3 dir = b.Direction.LengthSquared > 1e-6f ? b.Direction.Normalized() : Vector3.UnitZ;
                        float len = MathF.Min(StreakLength, (head - b.SpawnPosition).Length);
                        if (layer == 2)
                        {
                            float h = StreakHead / 2;
                            float rad = b.Age * 20;
                            Vector3 ax = (right * MathF.Cos(rad) + up * MathF.Sin(rad)) * h, ay = (-right * MathF.Sin(rad) + up * MathF.Cos(rad)) * h;
                            Quad(ref n, vp, head - ax + ay, head + ax + ay, head - ax - ay, head + ax - ay, new Vector4(1, 1, 0.85f, 0.9f));
                            continue;
                        }
                        if (len < 0.02f) continue;
                        Vector3 tail = head - dir * len;
                        Vector3 side = Vector3.Cross(dir, cam - head);
                        side = side.LengthSquared > 1e-8f ? side.Normalized() : right;
                        float w = (layer == 0 ? StreakGlow : StreakCore) / 2;
                        Vector4 col = layer == 0 ? new Vector4(1, 0.8f, 0.3f, 0.85f) : new Vector4(1, 1, 0.92f, 1);
                        // the glow texture's middle row along the streak, its full width across it
                        Quad(ref n, vp, tail + side * w, head + side * w, tail - side * w, head - side * w, col, streak: true);
                    }
                }
                if (n > 0) DrawRun(layer == 2 ? DroneFlashTex : DroneGlowTex, additive: true, n);
            }
        }
        // Prime's cutting laser: its three tubes, outermost first
        if (lasers > 0)
        {
            foreach ((float radius, Vector4 col) in DroneLaserTubes)
            {
                int n = 0;
                foreach (PlayerEntity p in _fxSeats.Keys)
                {
                    if (!p.HostOwnDroneLaser || !p.DroneLaserOn) continue;
                    // the laser's start carried with the seat (smooth frames), its end stays on what it hits
                    Vector3 from = Vector3.TransformPosition(p.DroneLaserOrigin, SmoothCarry(p, scene)), to = p.DroneLaserEnd;
                    Vector3 dir = to - from;
                    if (dir.LengthSquared < 1e-6f) continue;
                    dir.Normalize();
                    Vector3 SideAt(Vector3 at)
                    {
                        Vector3 sd = Vector3.Cross(dir, cam - at);
                        float dist = (cam - at).Length;
                        float w = MathF.Max(radius * DroneBody.Scale, dist * DroneLaserMinHalf);
                        return (sd.LengthSquared > 1e-8f ? sd.Normalized() : right) * w;
                    }
                    Vector3 sf = SideAt(from), st = SideAt(to);
                    Quad(ref n, vp, from + sf, to + st, from - sf, to - st, col);
                }
                if (n > 0) DrawRun(0, additive: true, n, white: true);
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

    // two triangles: corners top-left, top-right, bottom-left, bottom-right (texture row 0 = the top); a streak samples
    // the texture's middle row lengthwise (u 0.5 -> along, v across); uv: a flipbook tile (u0, v0, u1, v1)
    void Quad(ref int n, Matrix4 vp, Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, Vector4 col, bool streak = false, Vector4 uv = default)
    {
        if ((n + 6) * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, _fxVerts.Length * 2);
        if (uv == default) uv = new Vector4(0, 0, 1, 1); // a flipbook's tile, else the whole texture
        (float, float) uTl = streak ? (0.15f, 0) : (uv.X, uv.Y), uTr = streak ? (0.5f, 0) : (uv.Z, uv.Y);
        (float, float) uBl = streak ? (0.15f, 1) : (uv.X, uv.W), uBr = streak ? (0.5f, 1) : (uv.Z, uv.W);
        Vert(ref n, vp, tl, uTl, col); Vert(ref n, vp, tr, uTr, col); Vert(ref n, vp, bl, uBl, col);
        Vert(ref n, vp, bl, uBl, col); Vert(ref n, vp, tr, uTr, col); Vert(ref n, vp, br, uBr, col);
    }

    void Vert(ref int n, Matrix4 vp, Vector3 at, (float U, float V) uv, Vector4 col)
    {
        Vector4 c = new Vector4(at, 1) * vp;
        int o = n * 10;
        _fxVerts[o] = c.X; _fxVerts[o + 1] = c.Y; _fxVerts[o + 2] = c.Z; _fxVerts[o + 3] = c.W;
        _fxVerts[o + 4] = uv.U; _fxVerts[o + 5] = uv.V;
        _fxVerts[o + 6] = col.X; _fxVerts[o + 7] = col.Y; _fxVerts[o + 8] = col.Z; _fxVerts[o + 9] = col.W;
        n++;
    }

    void DrawRun(uint tex, bool additive, int verts, bool white = false)
    {
        if (verts == 0) return;
        int id;
        if (white) id = _fxWhite;
        else if (FxTexture(tex) is int t) id = t;
        else return;
        // the MPH passes leave a nearest-filter sampler object on unit 0, and a sampler object overrides the texture's own
        // filter: the effects drew blocky (owner 10-16, the turret's explosions), not smooth as Retro's bilinear ones
        GLES30.GlBindSampler(0, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, id);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, additive ? GLES30.GlOne : GLES30.GlOneMinusSrcAlpha);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, verts * 10 * 4, _fxVerts, GLES30.GlDynamicDraw);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, verts);
    }

    int? FxTexture(uint t)
    {
        if (_fxTex.TryGetValue(t, out int id)) return id;
        if (!_fxTexFile.TryGetValue(t, out string? file) || AppPlatform.DecodeImage(file) is not (int[] px, int w, int h))
        {
            _fxTex[t] = 0;
            return null;
        }
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinearMipmapLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        int wrap = _fxTexRepeat.Contains(t) ? GLES30.GlRepeat : GLES30.GlClampToEdge;
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, wrap);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, wrap);
        return _fxTex[t] = ids[0];
    }

    const string FxVert = "#version 300 es\nlayout(location = 0) in vec4 aPos;\nlayout(location = 1) in vec2 aUv;\n"
        + "layout(location = 2) in vec4 aCol;\nout vec2 vUv;\nout vec4 vCol;\n"
        + "void main() { gl_Position = aPos; vUv = aUv; vCol = aCol; }\n";
    const string FxFrag = "#version 300 es\nprecision mediump float;\nuniform sampler2D uTex;\nin vec2 vUv;\nin vec4 vCol;\n"
        + "out vec4 fragColor;\nvoid main() { fragColor = texture(uTex, vUv) * vCol; }\n";

    // the program, buffer and textures, made again after a new GL context (it empties _u: a key of ours marks ours alive)
    bool FxGl()
    {
        if (!_u.ContainsKey("dronefx"))
        {
            _fxProg = _fxVao = _fxVbo = _fxWhite = 0;
            _fxTex.Clear();
            _u["dronefx"] = 1;
        }
        if (_fxProg == 0)
        {
            _fxProg = CampaignGlCache.Program(FxVert, FxFrag, "Prime effects (drone)");
            if (_fxProg == 0) return false;
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _fxVao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _fxVbo = ids[0];
            GLES30.GlBindVertexArray(_fxVao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
            int[] size = { 4, 2, 4 }, off = { 0, 4, 6 };
            for (int a = 0; a < 3; a++)
            {
                GLES30.GlEnableVertexAttribArray(a);
                GLES30.GlVertexAttribPointer(a, size[a], GLES30.GlFloat, false, 40, off[a] * 4);
            }
            GLES30.GlBindVertexArray(0);
            // a white texel for the laser's plain tubes
            GLES30.GlGenTextures(1, ids, 0); _fxWhite = ids[0];
            GLES30.GlBindTexture(GLES30.GlTexture2d, _fxWhite);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, 1, 1, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, new[] { -1 });
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        }
        return true;
    }
}
