using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRecomp.Campaign;
using MphRecomp.Import.Effects;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The Hyper Beam's shots in Corruption's look and sound (owner 2026-10-24: "a, H2 and H4 together, dev only";
// docs/DARK_SAMUS_HYPER.md H4), every seat's, when Dark Samus's import carries her hyper/ folder (Core
// Import/Effects/HyperFx.cs, recipes/bodies/MP3DarkSamus.json "hyperfx"): whoever fires it, it's drawn from her import.
// - A bolt (uncharged, and Dark Samus's rapid fire): the "beam" WPSC (Corruption's Hyper Mode beam shot); a charged
//   shot: the "burst" WPSC (PhazonBurst, flying straight: option a). Each: its APSM system riding the bolt, its APS2
//   left where it was made, its swooshes (ASW1-3 and those its systems trail), run by Core Campaign/RetroParticles.cs /
//   RetroSwoosh.cs and drawn with the drone's effects (CampaignRenderer.DroneFx.cs: _fxLive) and here; the path from the
//   cannon tip as the Pirate Blaster's (CampaignRenderer.BlasterFx.cs BoltPath / BoltDrawn).
// - Where it hits: its CRSC's default effect. MPH's hit sound stays (Corruption's hit sound isn't in the import).
// - At the cannon: the "muzzle" flash (a bolt) or "burstmuzzle" (a charged shot), the "charge" glow while charging.
// - Sound: Corruption's "fire" (a bolt) / "chargefire" (a charged shot), and MPH's own charge hum (Corruption's charge
//   sound isn't in the import), looped here because MphRead's is off with the shot sound (PlayerSound HostOwnHyperBeamSfx).
// MPH's look for those shots (its recoloured Power Beam) is hidden (PlayerEntity.HostOwnHyperBeamLook). Drawing and
// sound only: the shots, damage and hits are the simulation's (MphRead PlayerHyperBeam.cs). Without the import: the
// recoloured Power Beam and its sounds.
internal sealed partial class CampaignRenderer
{
    const string HyperBeamFxBody = "MP3DarkSamus";
    // Corruption metres -> world units: Dark Samus 3.0 units tall in MPH, ~1.7 m in Corruption (owner to tune by eye)
    const float HyperBeamFxScale = 0.57f;
    const float HyperBeamImpactScale = 1.5f;   // as the blaster's hit (BlasterImpactScale)
    const int HyperBeamImpactEmit = 4;
    const int HyperBeamMuzzleEmit = 6;         // frames a muzzle flash emits (it rides the tip till it's done)
    const float HyperBeamOwnMuzzle = 0.5f;     // the cannon's effects in the player's own first-person view
    const int HyperBeamSeed = 0x48594250;

    sealed class HyperShotLook
    {
        public uint Main, Second;              // APSM (rides the bolt), APS2 (stays)
        public uint Impact;                    // the CRSC's default effect
        public readonly List<Part.Swoosh> Swooshes = new();
    }

    sealed class HyperBeamSeat
    {
        public readonly SoundSource Sound = new();
        public int Hum = -1;                   // the MPH charge sound looping (its id), or -1
        public DroneFxLive? Charge;            // the charge glow
        public readonly List<DroneFxLive> Muzzle = new();
        public int ShotsHeard = -1;
    }

    bool _hbRead;
    RetroParticles? _hbLib;
    HyperShotLook? _hbBeam, _hbBurst;
    uint _hbMuzzle, _hbBurstMuzzle, _hbChargeGlow;
    readonly Dictionary<string, List<(short[] Pcm, int Rate, float Volume)>> _hbSounds = new(StringComparer.Ordinal);
    readonly Dictionary<BeamProjectileEntity, BlasterBolt> _hbBolts = new();
    readonly List<BlasterBolt> _hbDying = new();
    readonly List<(BeamProjectileEntity Beam, CollisionResult Col, bool Expired)> _hbHits = new();
    readonly List<BeamProjectileEntity> _hbGone = new();
    readonly Dictionary<PlayerEntity, HyperBeamSeat> _hbSeats = new();
    readonly SoundSource _hbShotSound = new();
    Action<BeamProjectileEntity, CollisionResult, bool>? _hbImpact, _hbPrevImpact;
    int _hbSeed = HyperBeamSeed;

    // bodies/MP3DarkSamus/hyper, read once the imports' folder is known; false without the beam shot in it
    bool HyperBeamFxReady()
    {
        if (_hbRead) return _hbBeam != null;
        if (BodiesRoot is not string root) return false;
        _hbRead = true;
        if (!CampaignRetroBody.Imported(root, HyperBeamFxBody))
        {
            Log.Info("MPHAudio", $"Hyper Beam: no {HyperBeamFxBody} import, MPH's look kept");
            return false; // ship-safe audit: only the importer's body
        }
        string dir = CampaignRetroBody.DirOf(root, HyperBeamFxBody);
        try
        {
            if (HyperFx.Config.Load(dir) is not HyperFx.Config cfg)
            {
                Log.Info("MPHAudio", $"Hyper Beam: no Corruption effects in {HyperBeamFxBody} (re-import Corruption), MPH's look kept");
                return false;
            }
            string fx = Path.Combine(dir, HyperFx.Folder);
            RetroParticles lib = RetroParticles.Load(fx, mp3: true);
            int glows = OpaqueGlowsAdded(lib, fx);
            HyperShotLook? Shot(string role)
            {
                uint id = cfg.Id(role);
                string path = Path.Combine(fx, $"{id:x8}.wpsc");
                if (id == 0 || !File.Exists(path)) return null;
                Wpsc w = Wpsc.Load(File.ReadAllBytes(path), mp3: true);
                var look = new HyperShotLook { Main = w.AssetId("APSM"), Second = w.AssetId("APS2") };
                // its swooshes: its own and those its systems trail (through their child systems), each once
                var swooshes = new HashSet<uint>();
                foreach (string tag in new[] { "ASW1", "ASW2", "ASW3" })
                {
                    if (w.AssetId(tag) is uint s and not 0) swooshes.Add(s);
                }
                var seen = new HashSet<uint>();
                var todo = new Queue<uint>();
                if (look.Main != 0) todo.Enqueue(look.Main);
                if (look.Second != 0) todo.Enqueue(look.Second);
                while (todo.Count > 0)
                {
                    uint p = todo.Dequeue();
                    if (!seen.Add(p) || lib.Get(p) is not Part part) continue;
                    foreach ((string type, uint dep) in part.Dependencies())
                    {
                        if (type == "PART") todo.Enqueue(dep);
                        else if (type == "SWHC") swooshes.Add(dep);
                    }
                }
                foreach (uint s in swooshes)
                {
                    string sp = Path.Combine(fx, $"{s:x8}.swhc");
                    if (!File.Exists(sp)) continue;
                    Part.Swoosh sw = Part.Swoosh.Load(File.ReadAllBytes(sp), mp3: true);
                    // SBDM past 1 (the beam's 601fef31: 5, no texture) is a screen-warp ribbon we can't draw: it came out
                    // a solid white sheet
                    if ((sw["SBDM"]?.I ?? 0) > 1) continue;
                    look.Swooshes.Add(sw);
                }
                uint colr = w.AssetId("COLR");
                string cp = Path.Combine(fx, $"{colr:x8}.crsc");
                if (colr != 0 && File.Exists(cp) && Crsc.Load(File.ReadAllBytes(cp), mp3: true).DefaultEffect is uint hit and not 0 and not UInt32.MaxValue)
                {
                    look.Impact = hit;
                }
                // the charged burst (679e9cab) has no system, only its swoosh
                if (look.Main != 0 && lib.Get(look.Main) == null) look.Main = 0;
                return look.Main != 0 || look.Swooshes.Count > 0 ? look : null;
            }
            HyperShotLook? beam = Shot("beam");
            if (beam == null)
            {
                Log.Info("MPHAudio", $"Hyper Beam: no beam shot in {HyperBeamFxBody}'s Corruption effects (role {cfg.Id("beam"):x8}), MPH's look kept");
                return false;
            }
            HyperShotLook? burst = Shot("burst");
            uint Particle(string role) => cfg.Id(role) is uint id and not 0 && lib.Get(id) != null ? id : 0;
            _hbMuzzle = Particle("muzzle");
            _hbBurstMuzzle = Particle("burstmuzzle");
            _hbChargeGlow = Particle("charge");
            foreach (string role in new[] { "fire", "chargefire" })
            {
                if (!cfg.Sounds.TryGetValue(role, out var list)) continue;
                var l = new List<(short[], int, float)>();
                foreach ((int n, float vol, bool _) in list)
                {
                    if (MusyX.ReadWav(Path.Combine(fx, HyperFx.SoundFile(role, n)), out int rate) is short[] pcm && pcm.Length > 0) l.Add((pcm, rate, vol));
                }
                if (l.Count > 0) _hbSounds[role] = l;
            }
            foreach (string png in Directory.GetFiles(fx, "*.png"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) _fxTexFile.TryAdd(t, png);
            }
            _hbLib = lib;
            _hbBeam = beam;
            _hbBurst = burst;
            Log.Info("MPHAudio", $"Hyper Beam: Corruption shot from {HyperBeamFxBody}: beam {beam.Swooshes.Count} swooshes hit {(beam.Impact != 0 ? "yes" : "no")}, burst {(burst != null ? $"{burst.Swooshes.Count} swooshes" : "none")}, muzzle {(_hbMuzzle != 0 ? "yes" : "no")}/{(_hbBurstMuzzle != 0 ? "yes" : "no")}, charge {(_hbChargeGlow != 0 ? "yes" : "no")}, {_hbSounds.Count} sounds, {glows} glows added");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHAudio", $"Hyper Beam: the Corruption shot failed to load, MPH's look kept: {ex.Message}");
            _hbLib = null;
            _hbBeam = _hbBurst = null;
            _hbSounds.Clear();
        }
        return _hbBeam != null;
    }

    // TickSkins (MatchSkins.cs), right after TickBlasterFx and before TickDroneFx (which steps the systems in _fxLive)
    void TickHyperBeamFx()
    {
        DevHyperBeamHands(); // before the look's check: a dev run gets the beam without the import too
        bool on = HyperBeamFxReady();
        PlayerEntity.HostOwnHyperBeamLook = on;
        PlayerEntity.HostOwnHyperBeamSfx = on && _sfx != null && _hbSounds.ContainsKey("fire");
        PlayerEntity.HostOwnHyperBeamHitSfx = false; // MPH's hit sound (none in the import)
        if (!on)
        {
            StopHyperBeamHums();
            return;
        }
        RetroParticles lib = _hbLib!;
        // where shots hit last step: the CRSC's effect facing out of the surface
        foreach ((BeamProjectileEntity beam, CollisionResult col, bool expired) in _hbHits)
        {
            if (_hbBolts.TryGetValue(beam, out BlasterBolt? hit)) hit.Pos = col.Position;
            if (expired) continue;
            HyperShotLook look = beam.Flags.TestFlag(BeamFlags.Charged) && _hbBurst != null ? _hbBurst : _hbBeam!;
            if (look.Impact == 0 || lib.Get(look.Impact) == null) continue;
            Vector3 n = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -beam.Direction;
            Matrix4 at = Frame(n, col.Position + n * 0.05f, HyperBeamImpactScale * HyperBeamFxScale);
            _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Impact, at, follow: false, seed: _hbSeed++), EmitLeft = HyperBeamImpactEmit });
        }
        _hbHits.Clear();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            if (!_hbSeats.TryGetValue(p, out HyperBeamSeat? seat)) _hbSeats[p] = seat = new HyperBeamSeat();
            Vector3? tip = BlasterTip(p);
            float own = p.IsMainPlayer && !p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson) ? HyperBeamOwnMuzzle : 1;
            Vector3 aim = p.NetAimVector.LengthSquared > 1e-6f ? p.NetAimVector.Normalized() : Vector3.UnitZ;
            if (FirstPersonGunTip(p, out Vector3 fpTip, out Vector3 barrel))
            {
                tip = fpTip;
                aim = barrel;
            }
            HyperBeamSounds(p, seat, tip ?? p.Position);
            // charging: the glow at the cannon, held while the charge is up (and through Dark Samus's rapid fire)
            bool charging = tip != null && p.Health > 0 && !p.IsAltForm && p.CurrentWeapon == BeamType.HyperBeam
                && p.EquipInfo.ChargeLevel >= p.EquipInfo.Weapon.MinCharge * 2;
            if (charging && seat.Charge == null && _hbChargeGlow != 0)
            {
                seat.Charge = new DroneFxLive { Sys = new RetroParticleSystem(lib, _hbChargeGlow, BoltFrame(aim, tip!.Value, own), follow: true, seed: _hbSeed++) };
                _fxLive.Add(seat.Charge);
            }
            else if (!charging && seat.Charge != null)
            {
                seat.Charge.Sys.Emitting = false;
                seat.Charge = null;
            }
            // the shots: new ones start (with the cannon's flash), gone ones leave their trails to run out
            if (p.EquipInfo.Beams is BeamProjectileEntity[] beams)
            {
                foreach (BeamProjectileEntity b in beams)
                {
                    bool alive = b.HostHyperBeamLook && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided);
                    _hbBolts.TryGetValue(b, out BlasterBolt? bolt);
                    if (bolt != null && (!alive || b.SpawnPosition != bolt.Spawn || b.Age < bolt.Age))
                    {
                        EndHyperBolt(bolt);
                        _hbBolts.Remove(b);
                        bolt = null;
                    }
                    if (!alive) continue;
                    if (bolt == null)
                    {
                        bool charged = b.Flags.TestFlag(BeamFlags.Charged);
                        _hbBolts[b] = bolt = StartHyperBolt(b, lib, charged && _hbBurst != null ? _hbBurst : _hbBeam!);
                        uint flash = charged && _hbBurstMuzzle != 0 ? _hbBurstMuzzle : _hbMuzzle;
                        if (tip is Vector3 at && flash != 0 && seat.Muzzle.Count < 8)
                        {
                            var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, flash, BoltFrame(aim, at, own), follow: true, seed: _hbSeed++), EmitLeft = HyperBeamMuzzleEmit };
                            _fxLive.Add(fx);
                            seat.Muzzle.Add(fx);
                        }
                    }
                    bolt.Age = b.Age;
                    bolt.Pos = BoltDrawn(bolt, b);
                    Matrix4 frame = HyperBoltFrame(bolt);
                    foreach (DroneFxLive fx in bolt.Fx) fx.Sys.Transform = frame;
                    foreach (RetroSwoosh w in bolt.Swooshes) w.Step();
                }
            }
            // the cannon's effects ride the tip, turning with the aim
            if (tip is Vector3 hold)
            {
                Matrix4 at = BoltFrame(aim, hold, own);
                if (seat.Charge != null) seat.Charge.Sys.Transform = Rescale(at);
                foreach (DroneFxLive fx in seat.Muzzle) fx.Sys.Transform = Rescale(at);
            }
            seat.Muzzle.RemoveAll(fx => !fx.Sys.Emitting && fx.Sys.Done);
        }
        // shots whose pool slot went quiet without a tick seeing them end (their seat gone)
        _hbGone.Clear();
        foreach ((BeamProjectileEntity b, BlasterBolt bolt) in _hbBolts)
        {
            if (!b.HostHyperBeamLook || b.Lifespan <= 0) _hbGone.Add(b);
        }
        foreach (BeamProjectileEntity b in _hbGone)
        {
            EndHyperBolt(_hbBolts[b]);
            _hbBolts.Remove(b);
        }
        // ended bolts: their trails run out where the bolt stopped
        for (int i = _hbDying.Count - 1; i >= 0; i--)
        {
            BlasterBolt bolt = _hbDying[i];
            bool done = true;
            foreach (RetroSwoosh w in bolt.Swooshes)
            {
                w.Step();
                if (!w.Done) done = false;
            }
            if (done) _hbDying.RemoveAt(i);
        }
    }

    // TickSkins, after TickDroneFx (the drone and the blaster set HostImpact plainly each tick) and before the Paralyzer
    // (which chains in after this): Hyper Beam hits here, the rest to whoever set it
    void ChainHyperBeamImpact()
    {
        if (_hbBeam == null) return;
        _hbImpact ??= OnHyperBeamHit;
        if (BeamProjectileEntity.HostImpact != _hbImpact)
        {
            _hbPrevImpact = BeamProjectileEntity.HostImpact;
            BeamProjectileEntity.HostImpact = _hbImpact;
        }
    }

    void OnHyperBeamHit(BeamProjectileEntity beam, CollisionResult col, bool expired)
    {
        if (!beam.HostHyperBeamLook)
        {
            _hbPrevImpact?.Invoke(beam, col, expired);
            return;
        }
        if (_hbHits.Count < 64) _hbHits.Add((beam, col, expired));
    }

    // Corruption's shot sound, once per shot fired (the seat's HostHyperBeamShots count), at the cannon; the main player's
    // own as vanilla's ("self"). MPH's charge hum, looped while the charge builds (not through Dark Samus's rapid fire,
    // as MphRead's PlayerInput: no hum at her full charge)
    void HyperBeamSounds(PlayerEntity p, HyperBeamSeat seat, Vector3 at)
    {
        bool self = p.IsMainPlayer && !p.IsAltForm && !PlayerEntity.HostMainSoundsPlaced;
        int shots = p.HostHyperBeamShots;
        int heard = seat.ShotsHeard < 0 || shots < seat.ShotsHeard ? shots : seat.ShotsHeard;
        seat.ShotsHeard = shots;
        if (_sfx != null && PlayerEntity.HostOwnHyperBeamSfx)
        {
            string role = p.HostHyperBeamLastCharged && _hbSounds.ContainsKey("chargefire") ? "chargefire" : "fire";
            for (int i = 0; i < Math.Min(shots - heard, 2); i++)
            {
                if (!_hbSounds.TryGetValue(role, out var list)) break;
                (short[] pcm, int rate, float vol) = list[_hyperRand.Next(list.Count)];
                _hbShotSound.Update(at, self ? -1 : DroneSfxRange);
                _sfx.PlayPcm(pcm, rate, DroneSfxVolume * vol, _hbShotSound);
            }
        }
        WeaponInfo weapon = p.EquipInfo.Weapon;
        bool hum = PlayerEntity.HostOwnHyperBeamSfx && p.Health > 0 && !p.IsAltForm && p.CurrentWeapon == BeamType.HyperBeam
            && p.EquipInfo.ChargeLevel > 0 && !p.HyperBeamRapid
            && !(weapon == HyperBeam.WeaponMPAffinity && p.EquipInfo.ChargeLevel >= weapon.FullCharge * 2);
        seat.Sound.Update(at, self ? -1 : DroneSfxRange);
        if (hum && seat.Hum < 0)
        {
            seat.Hum = Metadata.BeamSfx[PirateBlaster.BaseIndex(BeamType.HyperBeam), (int)BeamSfx.Charge];
            if (seat.Hum >= 0) seat.Sound.PlaySfx(seat.Hum, loop: true);
        }
        else if (!hum && seat.Hum >= 0)
        {
            seat.Sound.StopSfx(seat.Hum);
            seat.Hum = -1;
        }
    }

    void StopHyperBeamHums()
    {
        foreach (HyperBeamSeat seat in _hbSeats.Values)
        {
            if (seat.Hum >= 0) seat.Sound.StopSfx(seat.Hum);
            seat.Hum = -1;
        }
    }

    // dev only (SecondHunt.exe with MPH_DEV_HYPERBEAM=1, or a solo run's --es hyperbeam 1: ZeroSuitGunTip.cs; ignored in
    // public builds): every seat handed the Hyper Beam once. MPH_DEV_HYPERBEAM_FIRE=1 / --es hyperfire 1: B held 2.5 s
    // (a charged shot, or Dark Samus's rapid fire), then tapped
    readonly HashSet<PlayerEntity> _devHyperBeamGiven = new();
    int _devHyperBeamFrame;
    void DevHyperBeamHands()
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM") != "1" && !_devHyperBeamArg) return;
        if (Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_FIRE") == "1" || _devHyperBeamFire)
        {
            int f = _devHyperBeamFrame++ % 300;
            Pad.Set(PadButton.B, f < 150 || f >= 180 && f % 16 < 4);
        }
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || p.Health <= 0 || !_devHyperBeamGiven.Add(p)) continue;
            p.HostPickUpWeapon(ItemType.HyperBeam);
            Log.Info("MPHMatch", $"dev: seat {p.SlotIndex} handed the Hyper Beam");
        }
    }

    // Corruption's glows on an opaque black square (the charge's lightning sheet 005afad3, the burst hit's c8458a50) say
    // PBDM 0, which RetroParticles draws alpha-blended: a black square. Drawn added they're what the game shows, so those
    // systems get PBDM 1 in our own copy of the library. The count (log)
    static int OpaqueGlowsAdded(RetroParticles lib, string fx)
    {
        var opaque = new Dictionary<uint, bool>();
        bool Opaque(uint t)
        {
            if (opaque.TryGetValue(t, out bool o)) return o;
            o = false;
            string png = Path.Combine(fx, $"{t:x8}.png");
            if (File.Exists(png) && AppPlatform.DecodeImage(png) is (int[] px, int, int) && px.Length > 0)
            {
                o = true;
                foreach (int c in px)
                {
                    if ((uint)c >> 24 != 0xff) { o = false; break; }
                }
            }
            return opaque[t] = o;
        }
        int n = 0;
        foreach (string f in Directory.GetFiles(fx, "*.part"))
        {
            if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(f), System.Globalization.NumberStyles.HexNumber, null, out uint id)
                || lib.Get(id) is not Part part || part.Flag("AAPH")) continue;
            if (part["PBDM"] is Part.Elem bm && (bm.Kind != "CNST" || bm.I != 0)) continue;
            if (part["TEXR"] is not { Id: not 0 } tex || !Opaque(tex.Id)) continue;
            part.Props["PBDM"] = new Part.Elem { Cat = Part.Cat.Int, Kind = "CNST", I = 1 };
            n++;
        }
        return n;
    }

    BlasterBolt StartHyperBolt(BeamProjectileEntity b, RetroParticles lib, HyperShotLook look)
    {
        Vector3 dir = b.Direction.LengthSquared > 1e-6f ? b.Direction.Normalized() : Vector3.UnitZ;
        var bolt = new BlasterBolt { Spawn = b.SpawnPosition, Age = b.Age, Dir = dir, SimDir = dir };
        bolt.Lead = MathF.Max(0, Vector3.Dot(b.Position - b.SpawnPosition, dir));
        BoltPath(bolt, b);
        bolt.Pos = BoltDrawn(bolt, b);
        bolt.First = bolt.Pos;
        bolt.HasFirst = true;
        // first person: the second system's puff would hang in front of the camera (as the blaster's)
        bolt.OwnView = b.Owner is PlayerEntity { IsMainPlayer: true } op && !op.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
        Matrix4 at = HyperBoltFrame(bolt);
        int seed = HyperBeamSeed; // every bolt alike (as the blaster's: BlasterSeed)
        DroneFxLive fx;
        if (look.Main != 0)
        {
            fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Main, at, follow: true, seed: seed) };
            _fxLive.Add(fx);
            bolt.Fx.Add(fx);
        }
        if (look.Second != 0 && !bolt.OwnView && lib.Get(look.Second) != null)
        {
            fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Second, at, follow: false, seed: seed + 1) };
            _fxLive.Add(fx);
            bolt.Fx.Add(fx);
        }
        for (int i = 0; i < look.Swooshes.Count; i++) bolt.Swooshes.Add(new RetroSwoosh(look.Swooshes[i], seed ^ (i + 1) * 7919));
        return bolt;
    }

    void EndHyperBolt(BlasterBolt bolt)
    {
        bolt.Live = false;
        foreach (DroneFxLive fx in bolt.Fx) fx.Sys.Emitting = false;
        foreach (RetroSwoosh w in bolt.Swooshes) w.Emitting = false;
        _hbDying.Add(bolt);
    }

    // effect space (Corruption: +Y forward, +Z up) along a bolt at HyperBeamFxScale (BoltFrame is at the blaster's)
    static Matrix4 HyperBoltFrame(BlasterBolt bolt) => HyperBoltFrame(bolt.Dir, bolt.Pos, BoltScale(bolt));
    static Matrix4 HyperBoltFrame(Vector3 dir, Vector3 at, float scale) => Rescale(BoltFrame(dir, at, scale));
    static Matrix4 Rescale(Matrix4 blasterFrame)
    {
        float k = HyperBeamFxScale / BlasterFxScale;
        blasterFrame.Row0.Xyz *= k;
        blasterFrame.Row1.Xyz *= k;
        blasterFrame.Row2.Xyz *= k;
        return blasterFrame;
    }

    // a replay restarting its match (ResetDroneFx; _fxLive itself is cleared there)
    void ResetHyperBeamFx()
    {
        StopHyperBeamHums();
        _hbBolts.Clear();
        _hbDying.Clear();
        _hbHits.Clear();
        _hbSeats.Clear();
    }

    // DrawSkins (MatchSkins.cs), after DrawDroneFx (which draws the bolts' systems): their swooshes
    void DrawHyperBeamFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _hbBeam == null) return;
        if (_hbBolts.Count == 0 && _hbDying.Count == 0) return;
        if (!FxGl()) return;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        void Draw(BlasterBolt bolt, BeamProjectileEntity? beam)
        {
            Vector3 pos = beam != null ? Vector3.TransformPosition(bolt.Pos, SmoothCarry(beam, scene)) : bolt.Pos;
            Matrix4 at = HyperBoltFrame(bolt.Dir, pos, BoltScale(bolt));
            foreach (RetroSwoosh w in bolt.Swooshes)
            {
                _blasterVerts.Clear();
                w.Triangles(at, _blasterVerts);
                if (_blasterVerts.Count == 0) continue;
                if (_blasterVerts.Count * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, Math.Max(_fxVerts.Length * 2, _blasterVerts.Count * 10));
                int n = 0;
                foreach (RetroSwoosh.Vertex v in _blasterVerts) Vert(ref n, vp, v.Pos, (v.Uv.X, v.Uv.Y), v.Color);
                DrawRun(w.Texture, w.Additive, n, white: w.Texture == 0);
            }
        }
        foreach (KeyValuePair<BeamProjectileEntity, BlasterBolt> live in _hbBolts) Draw(live.Value, live.Key);
        foreach (BlasterBolt bolt in _hbDying) Draw(bolt, null);
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
