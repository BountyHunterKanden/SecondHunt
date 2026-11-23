using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
// - A bolt (uncharged): the "beam" WPSC (Corruption's Hyper Mode beam shot); a charged release's stream (every shot of
//   it, PlayerHyperBeam.cs): the "burst" WPSC (PhazonBurst; the simulation's shot flies Corruption's curl itself, MphRead
//   BeamHyperWind.cs, and is drawn where it is). Each: its APSM and APS2 systems riding the bolt or leaving their particles along its path (the
//   WPSC's AP11 / AP21: the PhazonBeam's tail glows stay where made), its B1 head (B1TX: the burst's swirl ball,
//   HyperBeamHeadSize), its swooshes (ASW1-3 and those its systems trail, left along the path, each in its SBDM blend
//   blend), run by Core Campaign/RetroParticles.cs / RetroSwoosh.cs and drawn with the
//   drone's effects (CampaignRenderer.DroneFx.cs: _fxLive) and here; the path from the cannon tip as the Pirate
//   Blaster's (CampaignRenderer.BlasterFx.cs BoltPath / BoltDrawn).
// - Where it hits: its CRSC's default effect and its sound (the bolt's samus_phazon_impact, walls and players alike; a
//   stream shot's CRSC has none, so they hit silently as Corruption's).
// - At the cannon, as Corruption's gun (main.dol, 11-13): the "muzzle" flash (PhazonMuzzle) emitting 0.0625 s a bolt,
//   the "burstmuzzle" (PhazonBurstMuzzle) emitting through a charged release's stream, the "charge" glow (PhazonCharge)
//   made when the charge starts and gone when it ends; all three on the gun's frame (HyperBeamGunFxScale), carried
//   with it (DroneFxLive.Carry).
// - Sound (all Corruption's, MphRead's off: PlayerSound HostOwnHyperBeamSfx), each at its CAUD's pitch (a bolt's -300
//   +- 50 cents): "fire" a bolt, "chargefire" a charged release; through a charge "chargeloop" (samus_phazoncharge_lp,
//   a 3 s one-shot started with the charge glow and stopped at the full charge, 0x80185524), then "chargeready" (..._ready_lp) at the full charge
//   until the release, then "streamloop" (..._fire_lp) under the stream; both loop from their CSMP's own loop point
//   (1.61 s and 1.71 s in), not their start: the intro once, then only the held part (owner 11-14 "loops at the wrong point").
// MPH's look for those shots (its recoloured Power Beam) is hidden (PlayerEntity.HostOwnHyperBeamLook). Drawing and
// sound only: the shots, damage and hits are the simulation's (MphRead PlayerHyperBeam.cs). Without the import: the
// recoloured Power Beam and its sounds.
internal sealed partial class CampaignRenderer
{
    const string HyperBeamFxBody = "MP3DarkSamus";
    // Corruption metres -> world units: the Echoes arenas' Retro scale (hunter 1.6 units over Retro Samus's 2.7 m), so the
    // effects sit on Dark Samus at the size they sit on Samus in Corruption (was 0.57 by eye, then x1.5 on the impact)
    const float HyperBeamFxScale = MphRecomp.Arenas.EchoesArena.DefaultScale;
    // a muzzle's EmitLeft (TickDroneFx: N gives N - 1 emitting steps), from Corruption's beam weapon (the gun's +0x244,
    // vtable 0x80594b98): Fire (+0x1c, 0x801d229c) sets its muzzle timer W+0x20c to 0.0625 s a shot (0x801d2978; 100 s
    // only for a charged shot of a WPSC with DP1C, 0x801d23a0, the +0x64 bit 0x02 read at 0x8049ca2c, which neither
    // PhazonBeam's nor PhazonBurst's WPSC sets); the muzzle update (+0x3c, 0x801d1964) emits while W+0x20c > 0; the
    // countdown (+0x28, 0x801d0a04) takes dt off it, floored at 0 (0x801d0c54). The gun's update (0x80186274) runs the
    // countdown (0x80186330 -> 0x80186ac4), then the fire (0x801865d0 -> 0x80188c70), then the muzzle update
    // (0x80186624 -> 0x80185c64), so 0.0625 s at 60 Hz is the shot's frame + 3 more: 4 emitting frames
    const int HyperBeamMuzzleEmit = 5;
    // The burst muzzle has no hold of its own: each stream shot re-arms it with HyperBeamMuzzleEmit (each one's Fire sets
    // the timer again), and it stops when the stream ends: the Hyper fire state's exit (0x8018b024) calls the weapon's
    // Stop (vt+0x38 at 0x8018b040 -> 0x801d1544), which zeroes the timer. NOT YET PORTED: Corruption keeps one muzzle
    // generator a slot (normal / charged) for the gun's life; ours makes a PhazonMuzzle system a press shot (8 a seat).
    // The impact and the decal have no count: their data ends them (PSLT 1 on the CRSC's DEFS, on each of its KSSM
    // children and on every DPSC quad), and HyperSpent turns them off once Done says the PSLT is up.
    // the ChargeLevel (60 fps, full at HyperBeam.FullCharge x2) the charge glow and its build-up sound start past:
    // Corruption's charge state (0x8018aa88) runs the charge C+0x44 from 0 to 1 over the tweak's 2.5 s and calls
    // ChargeFx(gun, 1) (0x8018535c: the PhazonCharge glow and the weapon's +0x2c sound) once it passes C+0x48
    // (0x8018ab80), which the gun's constructor sets to 0.25 / 1.8 (0x8018f3cc: SDA2 0x80680a84 over 0x80192ae0's
    // 0x80680aa8). So it starts at ChargeLevel 21 of 150, and the glow's frame-120 children (KSSM) and its core's SIZE
    // peak (frame 130) land on the full charge, where ChargeReady (0x80185524) swaps the sound for the ready loop
    const float HyperBeamChargeStart = HyperBeam.FullCharge * 2 * (0.25f / 1.8f);
    // the cannon's effects (muzzle flashes, charge glow), on the effect frame's 1 Corruption metre: main.dol's scale 2
    // (0x80680610, the static 0x805c6130 set at 0x8015e12c, given to the effect at 0x801d1bf0), on a gun transform that
    // carries no scale of its own (gun+0x1a0 = the elbow's translation x gun+0x7c, 0x801865a4; +0x7c and the LBEAM
    // locator +0x10c are locator matrices, 0x801863c4 / 0x8018654c)
    const float HyperBeamGunFxScale = 2f;
    // first person, Corruption's charge on screen (HyperGunTip): its emitter's depth (view w 1.584 m in the 11-14 FIFO: the
    // newest core square's, the ORNT sparks' centre 1.585) and its projection's focal length over that depth (M22 1.921 /
    // 1.584: screen half heights a metre)
    const float HyperBeamGunFxDepth = 1.5842f;
    const float HyperBeamGunFxFocus = 1.9209824f / HyperBeamGunFxDepth;
    const int HyperBeamSeed = 0x48594250;
    // a shot's head (B1TX: the stream's swirl ball): main.dol's defaults where the WPSC has no B1SE / B1CL (the head's state
    // made at 0x804985b8, drawn at 0x8049a844, 11-14): a camera-facing square 0.5 m across tinted 0x808080, rolled by B1RT
    // taken afresh each frame, added when RB1A; drawn full size from the cannon (no first-person shrink: Corruption's isn't),
    // not widened as the press shot's trail is (widened, the stream's heads came out as rings filling the view at the cannon,
    // owner 11-14 "far too big")
    const float HyperBeamHeadSize = 0.5f;
    static readonly Vector4 HyperBeamHeadColor = new(128 / 255f, 128 / 255f, 128 / 255f, 1);
    readonly Random _hbHeadRand = new(HyperBeamSeed);

    sealed class HyperShotLook
    {
        public uint Head;                      // B1TX's texture, or 0
        public bool HeadAdditive;              // RB1A
        public Part.Elem? HeadRoll;            // B1RT (degrees)
        public uint Main, Second;              // APSM, APS2
        // AP11 / AP21: the system rides the shot (CProjectileWeapon's SetGlobalTranslation), else only its new particles
        // start where the shot is and the rest stay where they were made (SetTranslation: the PhazonBeam's APSM, AP11 off,
        // leaves its glows along the path, Corruption's long tail)
        public bool MainRides, SecondRides;
        public uint Impact;                    // the CRSC's default effect
        public readonly List<uint> Decals = new(); // its default decal's quads (Part.Dpsc.Quad, ids in the library)
        public readonly List<Part.Swoosh> Swooshes = new();
    }

    sealed class HyperBeamSeat
    {
        public readonly SoundSource Sound = new();
        public object? Build, Ready, Stream;   // the charge's sounds playing (GameAudio voices), or null
        public int BuildLeft;                  // ticks left of the build-up (a one-shot: stopped as it ends)
        public bool Built;                     // this charge's build-up has started (it doesn't start over)
        public DroneFxLive? Charge;            // the charge glow (PhazonCharge), made at charge-on, gone at charge-off
        public DroneFxLive? Burst;             // the burst muzzle (PhazonBurstMuzzle), emitting through a stream
        public readonly List<DroneFxLive> Muzzle = new(); // bolts' muzzle flashes and ended bursts, running out on the tip
        public int ShotsHeard = -1;
    }

    bool _hbRead;
    RetroParticles? _hbLib;
    HyperShotLook? _hbBeam, _hbBurst;
    uint _hbMuzzle, _hbBurstMuzzle, _hbChargeGlow;
    readonly Dictionary<string, List<(short[] Pcm, int Rate, float Volume, int Pitch, int Spread, int LoopStart)>> _hbSounds = new(StringComparer.Ordinal);
    readonly Dictionary<BeamProjectileEntity, BlasterBolt> _hbBolts = new();
    readonly List<BlasterBolt> _hbDying = new();
    // first person: the shots GunTipPath started on the gun, drawn through Corruption's lens (HyperLens) from their drawn
    // start (From) along the drawn line (Along) to where it meets the sim's (Len)
    readonly Dictionary<BlasterBolt, (Vector3 From, Vector3 Along, float Len)> _hbLens = new();
    readonly List<(BeamProjectileEntity Beam, CollisionResult Col, bool Expired)> _hbHits = new();
    readonly List<BeamProjectileEntity> _hbGone = new();
    readonly List<DroneFxLive> _hbSpent = new();
    readonly Dictionary<PlayerEntity, HyperBeamSeat> _hbSeats = new();
    readonly SoundSource _hbShotSound = new();
    // the main player's gun placement (GunDrawTransform) the cannon was last drawn at (_cannonModel, UpdateArmCannon):
    // a tick after the steps since moves the cannon on with the gun (HyperGunTip)
    Matrix4? _hbCannonGun;
    Action<BeamProjectileEntity, CollisionResult, bool>? _hbImpact, _hbPrevImpact;
    Action<BeamProjectileEntity>? _hbHitSfx, _hbPrevHitSfx;
    readonly List<Vector3> _hbHitSounds = new();
    readonly SoundSource _hbHitSound = new();
    readonly Dictionary<short[], List<object>> _hbVoices = new(); // each sound's playing one-shots, oldest first (HyperSfxCap)
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
            lib.RetroRules = true; // Corruption's own rules (LINE, MBDM, KEYE, ORNT...), the Echoes import's untouched
            // MPH_DEV_HYPERBEAM_HIDE=<folded part id> (dev): that part makes no particles
            foreach (string h in HyperBeamHide)
                if (h.Length == 8 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint hid) && lib.Get(hid) is Part hp)
                    hp.Props["MAXP"] = new Part.Elem { Cat = Part.Cat.Int, Kind = "CNST", I = 0 };
            HyperShotLook? Shot(string role)
            {
                uint id = cfg.Id(role);
                string path = Path.Combine(fx, $"{id:x8}.wpsc");
                if (id == 0 || !File.Exists(path)) return null;
                Wpsc w = Wpsc.Load(File.ReadAllBytes(path), mp3: true);
                var look = new HyperShotLook
                {
                    Main = w.AssetId("APSM"), Second = w.AssetId("APS2"), MainRides = w.Flag("AP11"), SecondRides = w.Flag("AP21"),
                    Head = w.AssetId("B1TX"), HeadAdditive = w.Flag("RB1A"), HeadRoll = w["B1RT"],
                };
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
                    // SBDM is the blend (RetroBlends.Mp3): the beam's 601fef31 (5, no texture or COLR: white) is its added
                    // white core, a spindle (LRAD 0.3 from 30% to 70% of its 5 points) that glows
                    if (HyperBeamHide.Length > 0) Log.Info("MPHAudio", $"Hyper Beam: {role} trail{look.Swooshes.Count} = {s:x8}");
                    if (sw.Flag("TEXW") && sw.Texture != 0) _fxTexRepeat.Add(sw.Texture); // TEXW: its texture repeats
                    look.Swooshes.Add(sw);
                }
                uint colr = w.AssetId("COLR");
                string cp = Path.Combine(fx, $"{colr:x8}.crsc");
                if (colr != 0 && File.Exists(cp))
                {
                    Crsc c = Crsc.Load(File.ReadAllBytes(cp), mp3: true);
                    if (c.DefaultEffect is uint hit and not 0 and not UInt32.MaxValue) look.Impact = hit;
                    // the wall mark (the beam's ba67a110: an added glow and a black scorch, CDecal's two quads)
                    string dp = Path.Combine(fx, $"{c.DefaultDecal:x8}.dpsc");
                    if (c.DefaultDecal is not (0 or UInt32.MaxValue) && File.Exists(dp))
                    {
                        Part.Dpsc d = Part.Dpsc.Load(File.ReadAllBytes(dp), mp3: true);
                        for (int q = 1; q <= 2; q++)
                        {
                            if (d.Quad(q) is not Part quad) continue;
                            uint qid = c.DefaultDecal ^ (uint)q;
                            lib.Add(qid, quad);
                            look.Decals.Add(qid);
                        }
                    }
                }
                // the charged burst (679e9cab) has no system, only its head and swoosh
                if (look.Main != 0 && lib.Get(look.Main) == null) look.Main = 0;
                if (look.Head != 0 && !File.Exists(Path.Combine(fx, $"{look.Head:x8}.png"))) look.Head = 0;
                return look.Main != 0 || look.Head != 0 || look.Swooshes.Count > 0 ? look : null;
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
            foreach (string role in new[] { "fire", "chargefire", "chargeloop", "chargeready", "streamloop", "hit" })
            {
                if (!cfg.Sounds.TryGetValue(role, out var list)) continue;
                var l = new List<(short[], int, float, int, int, int)>();
                foreach ((int n, float vol, bool _, int pitch, int spread, int loopStart) in list)
                {
                    if (MusyX.ReadWav(Path.Combine(fx, HyperFx.SoundFile(role, n)), out int rate) is short[] pcm && pcm.Length > 0) l.Add((pcm, rate, vol, pitch, spread, loopStart));
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
            Log.Info("MPHAudio", $"Hyper Beam: Corruption shot from {HyperBeamFxBody}: beam {beam.Swooshes.Count} swooshes hit {(beam.Impact != 0 ? "yes" : "no")} decal {beam.Decals.Count}, burst {(burst != null ? $"{burst.Swooshes.Count} swooshes head {(burst.Head != 0 ? "yes" : "no")}" : "none")}, muzzle {(_hbMuzzle != 0 ? "yes" : "no")}/{(_hbBurstMuzzle != 0 ? "yes" : "no")}, charge {(_hbChargeGlow != 0 ? "yes" : "no")}, {_hbSounds.Count} sounds");
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
        // the hits whose PSLT is up and that have run out (HyperSpent): off, so TickDroneFx lets them go
        for (int i = _hbSpent.Count - 1; i >= 0; i--)
        {
            if (!_hbSpent[i].Sys.Done) continue;
            _hbSpent[i].Sys.Emitting = false;
            _hbSpent.RemoveAt(i);
        }
        bool on = HyperBeamFxReady();
        PlayerEntity.HostOwnHyperBeamLook = on;
        PlayerEntity.HostOwnHyperBeamSfx = on && _sfx != null && _hbSounds.ContainsKey("fire");
        PlayerEntity.HostOwnHyperBeamHitSfx = on && _sfx != null && _hbSounds.ContainsKey("hit");
        if (!on)
        {
            StopHyperBeamHums();
            _hbHitSounds.Clear();
            return;
        }
        RetroParticles lib = _hbLib!;
        // where bolts hit last step (OnHyperBeamHitSfx, MphRead's hit-sound spots): Corruption's impact sound
        if (_sfx != null && _hbSounds.TryGetValue("hit", out var hitSounds))
        {
            foreach (Vector3 at in _hbHitSounds)
            {
                var hit = hitSounds[_hyperRand.Next(hitSounds.Count)];
                HyperSfxPlace(_hbHitSound, at, false);
                HyperSfxOneShot(hit.Pcm, HyperBeamRate(hit), DroneSfxVolume * hit.Volume, _hbHitSound, HyperSfxHitCap);
            }
        }
        _hbHitSounds.Clear();
        int dyingBefore = _hbDying.Count; // bolts ended this tick had their step (a hit's, on the wall) or skip one
        // where shots hit last step: the CRSC's effect and decal in Retro's hit frame (+Y out of the surface), at the
        // effects' own scale (they're sized for the wall; the x1.5 the blaster's hit takes made them far too big, owner 11-13)
        foreach ((BeamProjectileEntity beam, CollisionResult col, bool expired) in _hbHits)
        {
            HyperShotLook look = beam.Flags.TestFlag(BeamFlags.Charged) && _hbBurst != null ? _hbBurst : _hbBeam!;
            Vector3 n = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -beam.Direction;
            _hbBolts.TryGetValue(beam, out BlasterBolt? hit);
            if (hit != null && hit.Live)
            {
                // its trails' last point on the wall (this tick's step: the bolt ends below)
                hit.Pos = col.Position;
                foreach (RetroSwoosh w in hit.Swooshes)
                {
                    w.Place(HyperBoltFrame(hit.Dir, col.Position, 1));
                    w.Step();
                }
            }
            if (expired) continue;
            HyperImpact(lib, look, col.Position, n);
        }
        _hbHits.Clear();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            if (!_hbSeats.TryGetValue(p, out HyperBeamSeat? seat)) _hbSeats[p] = seat = new HyperBeamSeat();
            Vector3? tip = BlasterTip(p);
            Vector3 aim = p.NetAimVector.LengthSquared > 1e-6f ? p.NetAimVector.Normalized() : Vector3.UnitZ;
            // the cannon's effects at HyperBeamGunFxScale (main.dol's); first
            // person, that scale on the drawn cannon (HyperGunTip: as big against it as Corruption's are against its gun),
            // or on MPH's own gun by the same rule (HyperMphGunTip)
            float gunFx = HyperBeamGunFxScale;
            if (HyperGunTip(p, out Vector3 fpTip, out Vector3 barrel, out float fpFx))
            {
                tip = fpTip;
                aim = barrel;
                gunFx = fpFx;
            }
            else if (HyperMphGunTip(p, out Vector3 mphTip, out float mphFx))
            {
                tip = mphTip;
                gunFx = mphFx;
            }
            HyperBeamSounds(p, seat, tip ?? p.Position);
            bool armed = tip != null && p.Health > 0 && !p.IsAltForm && p.CurrentWeapon == BeamType.HyperBeam;
            // charging: the glow at the cannon while fire is held; Corruption deletes it when the charge ends (released,
            // or the stream it fired takes over), so it goes at once
            bool charging = armed && !p.HyperBeamStreaming && p.EquipInfo.ChargeLevel > HyperBeamChargeStart;
            if (charging && seat.Charge == null && _hbChargeGlow != 0 && !HyperHidden("charge"))
            {
                seat.Charge = new DroneFxLive { Sys = new RetroParticleSystem(lib, _hbChargeGlow, HyperBoltFrame(aim, tip!.Value, gunFx), follow: true, seed: _hbSeed++), Carry = p, Whole = true, Graded = true };
                _fxLive.Add(seat.Charge);
            }
            else if (!charging && seat.Charge != null)
            {
                _fxLive.Remove(seat.Charge);
                seat.Charge = null;
            }
            // the burst muzzle, kept on by the stream's shots (below); when the stream ends (or the cannon goes), it stops
            // emitting and what it made runs out on the tip
            if (seat.Burst != null && (!armed || !p.HyperBeamStreaming || !seat.Burst.Sys.Emitting))
            {
                seat.Burst.Sys.Emitting = false;
                seat.Burst.EmitLeft = 0;
                seat.Muzzle.Add(seat.Burst);
                seat.Burst = null;
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
                        // the cannon's flash: a bolt's for HyperBeamMuzzleEmit frames; a stream's burst muzzle made once
                        // and kept emitting by each of its shots, so it's on through the stream as Corruption's
                        if (tip is Vector3 at && charged && _hbBurstMuzzle != 0 && !HyperHidden("burstmuzzle"))
                        {
                            if (seat.Burst == null)
                            {
                                seat.Burst = new DroneFxLive { Sys = new RetroParticleSystem(lib, _hbBurstMuzzle, HyperBoltFrame(aim, at, gunFx), follow: true, seed: _hbSeed++), Carry = p, Whole = true, Graded = true };
                                _fxLive.Add(seat.Burst);
                            }
                            seat.Burst.EmitLeft = HyperBeamMuzzleEmit;
                        }
                        else if (tip is Vector3 at1 && _hbMuzzle != 0 && seat.Muzzle.Count < 8 && !(charged && HyperHidden("burstmuzzle")) && !HyperHidden("muzzle"))
                        {
                            var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, _hbMuzzle, HyperBoltFrame(aim, at1, gunFx), follow: true, seed: _hbSeed++), EmitLeft = HyperBeamMuzzleEmit, Carry = p, Whole = true, Graded = true };
                            _fxLive.Add(fx);
                            seat.Muzzle.Add(fx);
                        }
                    }
                    bolt.Age = b.Age;
                    bolt.Pos = HyperDrawn(bolt, b);
                    Vector3 drawn = bolt.Pos;
                    // first person, its own systems through Corruption's lens as its trails are (HyperLens), at the
                    // tick's view (they're not redrawn each frame: turning between ticks keeps the tick's, a gap)
                    float lensScale = 1;
                    Vector3 seen = _hbLens.ContainsKey(bolt) ? HyperLens(bolt, drawn, Matrix4.Identity, GunView(_host!.Scene), HyperLensFocus(_host.Scene), out lensScale) : drawn;
                    Matrix4 frame = HyperBoltFrame(bolt.Dir, seen, BoltScale(bolt) * lensScale);
                    foreach (DroneFxLive fx in bolt.Fx) fx.Sys.Transform = frame;
                    // the trails are made where the shot is, full size (Corruption's are, own view too), as wide on screen
                    Matrix4 place = HyperBoltFrame(bolt.Dir, drawn, 1);
                    foreach (RetroSwoosh w in bolt.Swooshes)
                    {
                        w.Place(place);
                        w.Step();
                    }
                }
            }
            // the cannon's effects ride the tip, turning with the aim (and, smooth frames, are carried with the seat as
            // its gun is: DroneFxLive.Carry; without it they hung back at the step's tip while the gun rode the camera)
            if (tip is Vector3 hold)
            {
                Matrix4 at = HyperBoltFrame(aim, hold, gunFx);
                if (seat.Charge != null) seat.Charge.Sys.Transform = at;
                if (seat.Burst != null) seat.Burst.Sys.Transform = at;
                foreach (DroneFxLive fx in seat.Muzzle) fx.Sys.Transform = at;
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
        for (int i = dyingBefore - 1; i >= 0; i--)
        {
            BlasterBolt bolt = _hbDying[i];
            bool done = true;
            foreach (RetroSwoosh w in bolt.Swooshes)
            {
                w.Step();
                if (!w.Done) done = false;
            }
            if (!done) continue;
            _hbDying.RemoveAt(i);
            _hbLens.Remove(bolt);
        }
    }

    // TickSkins, after TickDroneFx (the drone and the blaster set HostImpact / HostHitSfx plainly each tick) and before
    // the Paralyzer (which chains in after this): Hyper Beam hits and hit sounds here, the rest to whoever set them. The
    // Paralyzer's (left from last tick) forwards to ours, so it's never the one ours forwards to: with neither the drone
    // nor the blaster resetting them, the two forwarded to each other and another beam's hit went round for ever
    void ChainHyperBeamImpact()
    {
        if (_hbBeam == null) return;
        _hbImpact ??= OnHyperBeamHit;
        if (BeamProjectileEntity.HostImpact != _hbImpact)
        {
            if (BeamProjectileEntity.HostImpact != _paraImpact) _hbPrevImpact = BeamProjectileEntity.HostImpact;
            BeamProjectileEntity.HostImpact = _hbImpact;
        }
        _hbHitSfx ??= OnHyperBeamHitSfx;
        if (BeamProjectileEntity.HostHitSfx != _hbHitSfx)
        {
            if (BeamProjectileEntity.HostHitSfx != _paraHitSfx) _hbPrevHitSfx = BeamProjectileEntity.HostHitSfx;
            BeamProjectileEntity.HostHitSfx = _hbHitSfx;
        }
    }

    // BeamProjectileEntity.HostHitSfx: a bolt's impact sound, played next tick; a stream shot's none (its CRSC has none)
    void OnHyperBeamHitSfx(BeamProjectileEntity beam)
    {
        if (!beam.HostHyperBeamLook)
        {
            _hbPrevHitSfx?.Invoke(beam);
            return;
        }
        if (beam.Flags.TestFlag(BeamFlags.Charged) && _hbBurst != null) return;
        if (_hbHitSounds.Count < 64) _hbHitSounds.Add(beam.Position);
    }

    // a sound's rate at its CAUD's pitch: a random whole number of cents in pitch +- spread (main.dol 0x803849f4)
    int HyperBeamRate((short[] Pcm, int Rate, float Volume, int Pitch, int Spread, int LoopStart) s)
    {
        int cents = s.Pitch + (s.Spread > 0 ? _hyperRand.Next(-s.Spread, s.Spread + 1) : 0);
        return cents == 0 ? s.Rate : (int)MathF.Round(s.Rate * MathF.Pow(2, cents / 1200f));
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
    // own as vanilla's ("self"). Its charge's: the build-up from the charge's start (the glow's, HyperBeamChargeStart) to
    // the full charge or the release (ChargeReady 0x80185524 stops it as it starts the ready loop; ChargeFx off
    // 0x801854c0 on a release), the ready loop from the full charge to the release, the stream's loop through it
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
                var s = list[_hyperRand.Next(list.Count)];
                HyperSfxPlace(_hbShotSound, at, self);
                HyperSfxOneShot(s.Pcm, HyperBeamRate(s), DroneSfxVolume * s.Volume, _hbShotSound, HyperSfxGunCap);
            }
        }
        bool armed = _sfx != null && PlayerEntity.HostOwnHyperBeamSfx && p.Health > 0 && !p.IsAltForm && p.CurrentWeapon == BeamType.HyperBeam;
        bool streaming = armed && p.HyperBeamStreaming;
        bool charging = armed && !streaming && p.EquipInfo.ChargeLevel > HyperBeamChargeStart;
        bool full = charging && p.EquipInfo.ChargeLevel >= HyperBeam.FullCharge * 2;
        HyperSfxPlace(seat.Sound, at, self);
        if (!charging) seat.Built = false;
        else if (!seat.Built && _hbSounds.TryGetValue("chargeloop", out var build))
        {
            // a one-shot (3.00 s, longer than the 2.15 s it's held for) the full charge or the release cuts short: played
            // as a loop (a voice to stop) and stopped just before it'd go round
            seat.Built = true;
            seat.Build = HyperBeamLoop(seat.Build, false, "chargeloop", seat);
            seat.Build = HyperBeamLoop(null, true, "chargeloop", seat, out int rate);
            seat.BuildLeft = (int)(build[0].Pcm.Length * 60L / Math.Max(1, rate)) - 2;
        }
        seat.Build = HyperBeamLoop(seat.Build, charging && !full && seat.BuildLeft-- > 0, "chargeloop", seat);
        seat.Ready = HyperBeamLoop(seat.Ready, full, "chargeready", seat);
        seat.Stream = HyperBeamLoop(seat.Stream, streaming, "streamloop", seat);
    }

    // Corruption's distance falloff for every Hyper Beam sound, in place of MPH's range table (DroneSfxRange / BlasterHitSfxRange,
    // MPH's linear rows). The five gun CAUDs (fire, chargefire, the three charge/stream loops) and the hit's
    // (samus_phazon_impact) all carry the same: entry +0x08 shape 0.3, +0x14 min 2 m, +0x18 max 100 m. main.dol's curve
    // builder 0x803341ac makes the gain curve from the shape: shape >= 0 lerps preset B to preset C (bss 0x805cc338, set up
    // by 0x80334480) by it, over x = (d - min) / (max - min); the points go to the mixer (0x80335708 -> 0x80413b2c), which
    // reads them linearly (0x804114c4). Full gain to 2 m (1.19 units), half at 25.3 m (15.0), 0.22 at 53 m (31.4), silent at
    // 100 m (59.3): metres x the Retro scale (HyperBeamFxScale). The pan is still the mixer's (left/right off the facing).
    // OPEN: the main player's own gun stays unplaced (MPH's "self", as before), not checked against Corruption, whose
    // listener-relative flag (mask 0x40000, 0x80411408) wasn't followed further (0x80336014 / 0x80336140 next); and
    // whether a CAUD can skip the curve (flag mask 0x04000000, 0x804114bc; its linear 0x804115d4 / inverse 0x80411614
    // fallbacks) wasn't settled: these CAUDs are taken to use it.
    const float HyperSfxShape = 0.3f, HyperSfxMin = 2.0f * HyperBeamFxScale, HyperSfxMax = 100.0f * HyperBeamFxScale;
    static readonly (float X, float Gain)[] HyperSfxPresetB = { (0, 1), (0.3f, 0.7f), (0.5f, 0.5f), (0.7f, 0.3f), (1, 0) };
    static readonly (float X, float Gain)[] HyperSfxPresetC = { (0, 1), (0.03f, 0.1f), (0.05f, 0.05f), (0.1f, 0.03f), (1, 0) };
    static readonly (float X, float Gain)[] HyperSfxCurve = HyperSfxPresetB.Select((b, i) =>
        (b.X + (HyperSfxPresetC[i].X - b.X) * HyperSfxShape, b.Gain + (HyperSfxPresetC[i].Gain - b.Gain) * HyperSfxShape)).ToArray();
    // instances of one sound at once (the CAUD's entry +0x0c: 4 for each gun sound, 2 for the hit), the oldest cut for a new
    // one (entry +0x8a set on all six), as main.dol 0x80333e20 does: count +0x44 against the cap +0x10, steal 0x80330fac.
    // Corruption's cap is per sound across every player, so it's per sound here. No other ducking: nothing in that path
    // lowers other sounds. OPEN: the entry's priority byte (165 gun, 204 hit) isn't used (our mixer has no priorities)
    const int HyperSfxGunCap = 4, HyperSfxHitCap = 2;

    static float HyperSfxGain(float d)
    {
        float x = (d - HyperSfxMin) / (HyperSfxMax - HyperSfxMin);
        if (x <= 0) return 1;
        for (int i = 1; i < HyperSfxCurve.Length; i++)
        {
            var (x0, g0) = HyperSfxCurve[i - 1];
            var (x1, g1) = HyperSfxCurve[i];
            if (x <= x1) return g0 + (g1 - g0) * (x - x0) / Math.Max(1e-6f, x1 - x0);
        }
        return 0;
    }

    // a sound's source at a spot with Corruption's gain (Volume; the mixer's own linear falloff turned off), or the main
    // player's own, unplaced as MPH's own gun sounds are (PlayerProcess.cs: rangeIndex -1 when "self"; owner 11-15 "treat
    // the sound like mph treats gun sounds regardless", whatever Corruption does with its own gun's)
    void HyperSfxPlace(SoundSource source, Vector3 at, bool self)
    {
        source.Update(at, self ? -1 : DroneSfxRange);
        if (self || _sfx == null)
        {
            source.Volume = 1;
            return;
        }
        source.ReferenceDistance = float.MaxValue;
        source.MaxDistance = float.MaxValue;
        source.Volume = HyperSfxGain((at - _sfx.GetListenerPosition()).Length);
    }

    // a one-shot under its sound's instance cap: the oldest still playing cut if the cap is full
    void HyperSfxOneShot(short[] pcm, int rate, float volume, SoundSource source, int cap)
    {
        if (_sfx == null) return;
        if (!_hbVoices.TryGetValue(pcm, out List<object>? playing)) _hbVoices[pcm] = playing = new List<object>();
        playing.RemoveAll(v => !_sfx.PcmAlive(v));
        while (playing.Count >= cap)
        {
            _sfx.StopPcm(playing[0]);
            playing.RemoveAt(0);
        }
        if (_sfx.PlayPcm(pcm, rate, volume, source) is object voice) playing.Add(voice);
    }

    // a charge sound's voice on (started at its pitch, or carried with the cannon) or off (stopped: the mixer fades it)
    object? HyperBeamLoop(object? voice, bool on, string role, HyperBeamSeat seat) => HyperBeamLoop(voice, on, role, seat, out _);

    object? HyperBeamLoop(object? voice, bool on, string role, HyperBeamSeat seat, out int rate)
    {
        rate = 0;
        if (!on || _sfx == null || !_hbSounds.TryGetValue(role, out var list))
        {
            if (voice != null) _sfx?.StopPcm(voice);
            return null;
        }
        var s = list[0];
        if (voice != null)
        {
            _sfx.MovePcm(voice, DroneSfxVolume * s.Volume, seat.Sound);
            return voice;
        }
        rate = HyperBeamRate(s);
        return _sfx.PlayPcmLoop(s.Pcm, rate, DroneSfxVolume * s.Volume, seat.Sound, s.LoopStart);
    }

    void StopHyperBeamHums()
    {
        foreach (HyperBeamSeat seat in _hbSeats.Values)
        {
            seat.Build = HyperBeamLoop(seat.Build, false, "chargeloop", seat);
            seat.Ready = HyperBeamLoop(seat.Ready, false, "chargeready", seat);
            seat.Stream = HyperBeamLoop(seat.Stream, false, "streamloop", seat);
            seat.Built = false;
        }
    }

    // dev only (SecondHunt.exe with MPH_DEV_HYPERBEAM=1, or a solo run's --es hyperbeam 1: ZeroSuitGunTip.cs; ignored in
    // public builds): every seat handed the Hyper Beam once (SOLO: the main player's alone). MPH_DEV_HYPERBEAM_FIRE=1 / --es hyperfire 1: B held 2.8 s
    // (a full charge, 2.5 s, released into its stream), then tapped
    readonly HashSet<PlayerEntity> _devHyperBeamGiven = new();
    // dev only: MPH_DEV_HYPERBEAM_HIDE=trail,head,muzzle,burstmuzzle,charge,systems leaves those parts undrawn (a capture
    // shows which part draws what)
    static readonly string[] HyperBeamHide = MphRecomp.Config.RecompSettings.PublicBuild ? Array.Empty<string>()
        : (Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_HIDE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static bool HyperHidden(string part) => Array.IndexOf(HyperBeamHide, part) >= 0;
    int _devHyperBeamFrame;
    void DevHyperBeamHands()
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM") != "1" && !_devHyperBeamArg) return;
        if (Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_FIRE") == "1" || _devHyperBeamFire)
        {
            int f = _devHyperBeamFrame++ % 420;
            Pad.Set(PadButton.B, f < 170 || f >= 340 && f % 16 < 4);
        }
        else if (Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_FIRE") == "tap")
        {
            // press shots only, one every 30 steps (a capture of the standard shot without the charge cycle)
            Pad.Set(PadButton.B, _devHyperBeamFrame++ % 30 < 3);
        }
        // MPH_DEV_HYPERBEAM_TURN=n: the right stick held right the first n steps (a capture facing a far wall)
        if (int.TryParse(Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_TURN"), out int turn)) Pad.Rx = _devHyperBeamFrame < turn ? 1 : 0;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || p.Health <= 0 || !_devHyperBeamGiven.Add(p)) continue;
            // MPH_DEV_HYPERBEAM_SOLO=1: the main player's only (the bots keep their own beams, a capture shows ours alone)
            if (!p.IsMainPlayer && Environment.GetEnvironmentVariable("MPH_DEV_HYPERBEAM_SOLO") == "1") continue;
            p.HostPickUpWeapon(ItemType.HyperBeam);
            Log.Info("MPHMatch", $"dev: seat {p.SlotIndex} handed the Hyper Beam");
        }
    }

    BlasterBolt StartHyperBolt(BeamProjectileEntity b, RetroParticles lib, HyperShotLook look)
    {
        Vector3 dir = b.Direction.LengthSquared > 1e-6f ? b.Direction.Normalized() : Vector3.UnitZ;
        var bolt = new BlasterBolt { Spawn = b.SpawnPosition, Age = b.Age, Dir = dir, SimDir = dir };
        bolt.Lead = MathF.Max(0, Vector3.Dot(b.Position - b.SpawnPosition, dir));
        BoltPath(bolt, b);
        GunTipPath(bolt, b);
        bolt.Pos = HyperDrawn(bolt, b);
        bolt.First = bolt.Pos;
        bolt.HasFirst = true;
        // first person: the second system's puff would hang in front of the camera (as the blaster's)
        bolt.OwnView = b.Owner is PlayerEntity { IsMainPlayer: true } op && !op.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
        Matrix4 at = HyperBoltFrame(bolt);
        int seed = HyperBeamSeed; // every bolt alike (as the blaster's: BlasterSeed)
        DroneFxLive fx;
        if (look.Main != 0 && !HyperHidden("systems"))
        {
            fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Main, at, follow: look.MainRides, seed: seed), Graded = true };
            _fxLive.Add(fx);
            bolt.Fx.Add(fx);
        }
        if (look.Second != 0 && !bolt.OwnView && lib.Get(look.Second) != null)
        {
            fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Second, at, follow: look.SecondRides, seed: seed + 1), Graded = true };
            _fxLive.Add(fx);
            bolt.Fx.Add(fx);
        }
        // the trails at the world's size (the effects' own frame), as Corruption's: on screen as thick as its at its view
        // (55 degrees high, FIELD OF VIEW about 85 here); at a wider view, thinner, as any world object is
        Matrix4 place = HyperBoltFrame(bolt.Dir, bolt.Pos, 1);
        for (int i = 0; i < look.Swooshes.Count; i++)
        {
            // Corruption's trails stay along the path the shot flew (RetroSwoosh path mode), not riding it. The press shot
            // flies Corruption's speed in the sim (Metadata/HyperBeam.cs: IVEC 2.5 m a frame), so its points are as far apart
            var w = new RetroSwoosh(look.Swooshes[i], seed ^ (i + 1) * 7919) { Path = true };
            w.Place(place);
            bolt.Swooshes.Add(w);
        }
        return bolt;
    }

    // first person on a body's cannon: the drawn cannon's muzzle (its muzzle locator, cannon space's origin: Corruption's
    // effects sit on its gun's LBEAM locator itself, 0x80185c28, no push) as the scene's projection shows it at its depth -- FirstPersonGunTip's
    // mapping (the cannon is drawn with GunProjection), from the muzzle: _cannonModel's own origin is the body model's
    // (Dark Samus's feet's, a metre below and behind the eye), so FirstPersonGunTip's point there was behind the eye and
    // the effects fell back to MPH's muzzle point, inside the drawn cannon (capture 11-14 HBLOG)
    // fxScale: the cannon's effects' HyperBoltFrame scale there, so they're as big on screen as Corruption's: its gun and its
    // effects share one projection (Dolphin FIFO 11-14: M22 1.921, 55 degrees high, gun draws and the charge's alike) and
    // its charge sits HyperBeamGunFxDepth metres out, so a Corruption metre there is HyperBeamGunFxFocus of the screen's
    // half height; ours, at the tip's depth in the scene's projection, is scaled to the same (the core's squares then come
    // out the FIFO's on screen, within 0.5%). Sized on our cannon instead (its world per cannon unit) it was 3% bigger, and
    // vc 672's flat 2 bigger still (owner 11-14 "too big")
    bool HyperGunTip(PlayerEntity p, out Vector3 tip, out Vector3 barrel) => HyperGunTip(p, out tip, out barrel, out _);
    bool HyperGunTip(PlayerEntity p, out Vector3 tip, out Vector3 barrel, out float fxScale)
    {
        tip = barrel = default;
        fxScale = HyperBeamGunFxScale;
        if (_host == null || p != _host.Player || p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson) || !_cannonActive
            || _cannonBody?.Cannon is not BodyArmCannon cannon || BodyOf(p) != _cannonBody)
        {
            return false;
        }
        Matrix4 gun = Matrix4.Invert(cannon.ToCannon) * _cannonModel; // cannon space -> world
        // the cannon's placed at a render, the tick runs after the steps since: moved on with the gun to this step, the
        // effects sit on the muzzle as it's drawn (owner 11-14 "still lots trailing behind the muzzle": a step behind
        // turning, more at higher frame rates, the effects carried with the camera from there, SmoothCarry)
        if (_hbCannonGun is Matrix4 drawn) gun = gun * Matrix4.Invert(drawn) * p.GunDrawTransform;
        barrel = gun.Row2.Xyz;
        if (barrel.LengthSquared < 1e-12f) return false;
        barrel.Normalize();
        return HyperGunSeen(gun.Row3.Xyz, out tip, out fxScale);
    }

    // first person with MPH's own gun (any hunter holding the Hyper Beam): the charge and muzzle flashes sized by the same
    // rule as on Dark Samus's cannon (HyperGunTip), as big on screen as Corruption's charge at its gun, here at MPH's
    // muzzle point (HostFxMuzzlePosition, where MPH's own charge and muzzle effects sit) as it's drawn. Corruption has no
    // MPH gun: this takes MPH's muzzle for Corruption's LBEAM locator, the one assumption. With the flat third-person
    // scale 2 at a muzzle half a unit from the eye the charge filled the screen (owner 11-15 screenshots: "way bigger")
    bool HyperMphGunTip(PlayerEntity p, out Vector3 tip, out float fxScale)
    {
        tip = default;
        fxScale = HyperBeamGunFxScale;
        if (_host == null || p != _host.Player || p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) return false;
        return HyperGunSeen(p.HostFxMuzzlePosition, out tip, out fxScale);
    }

    // a first-person gun point drawn with GunProjection: the world point the scene's projection shows there at its depth,
    // and the effect scale there that makes Corruption's charge its on-screen size (HyperGunTip's header)
    bool HyperGunSeen(Vector3 world, out Vector3 tip, out float fxScale)
    {
        tip = default;
        fxScale = HyperBeamGunFxScale;
        Scene scene = _host!.Scene;
        Matrix4 view = GunView(scene), proj = scene.PerspectiveMatrix; // the gun is placed at the step (smooth frames)
        Vector4 v = new Vector4(world, 1) * view;
        Vector4 g = v * GunProjection(scene), c = v * proj;
        if (g.W <= 1e-5f || c.W <= 1e-5f) return false;
        c.X = g.X / g.W * c.W;
        c.Y = g.Y / g.W * c.W;
        Vector4 seen = c * Matrix4.Invert(proj);
        if (MathF.Abs(seen.W) <= 1e-6f) return false;
        tip = (new Vector4(seen.Xyz / seen.W, 1) * Matrix4.Invert(view)).Xyz;
        float focus = MathF.Abs(proj.M22);
        if (focus > 1e-6f) fxScale = HyperBeamGunFxScale * HyperBeamGunFxFocus * c.W / focus / HyperBeamFxScale;
        return true;
    }

    // first person: the bolt drawn from the gun's LBEAM locator as it's seen (Dark Samus's cannon: HyperGunTip, main.dol
    // 0x80185c28, no push out; MPH's own gun: HyperMphGunTip, its muzzle point as the drawn gun shows it, where the muzzle
    // flash sits), easing onto the simulation's line by the wall, as BoltPath does from a third-person tip. Draw only: the
    // shot is the sim's. MPH's gun's was drawn unshifted at the sim's muzzle point, which the gun's own projection shows
    // elsewhere, so its stream started away from its flash (owner 11-15: "they start visibily out in front of the gun")
    void GunTipPath(BlasterBolt bolt, BeamProjectileEntity b)
    {
        if (_host == null || bolt.ArmPath || bolt.Shift != Vector3.Zero || b.Owner is not PlayerEntity p) return;
        Vector3 start = b.SpawnPosition, dir = bolt.SimDir;
        Scene scene = _host.Scene;
        if (!HyperGunTip(p, out Vector3 tip, out _, out _) && !HyperMphGunTip(p, out tip, out _)) return;
        // the shot leaves the tip as Corruption's leaves its LBEAM locator: HyperBeamGunFxDepth metres from the eye at true
        // scale, seen through Corruption's lens (HyperLens), on the screen point of the tip (where the muzzle flash sits):
        // so it's as wide there as Corruption's and each move takes it as far toward the aim. At the drawn tip's own depth
        // it came out wider than the cannon (owner 11-15 screenshots: "poking off the sides of the cannon"); vc 689 took it
        // only as far out as made that width through our wider lens (about 0.8 m), so its first moves shrank it toward the
        // aim twice as fast as Corruption's (owner 11-15: "cramped ... I'm seeing it move away in a frame")
        Matrix4 view = GunView(scene);
        Vector3 v = Vector3.TransformPosition(tip, view);
        float lens = HyperLensFocus(scene);
        if (v.Z > -1e-4f || lens <= 1e-6f) return;
        v *= HyperBeamGunFxDepth * HyperBeamFxScale / -v.Z;
        v.X /= lens;
        v.Y /= lens;
        tip = Vector3.TransformPosition(v, Matrix4.Invert(view));
        if ((tip - start).LengthSquared < 0.0025f) return;
        var res = new CollisionResult();
        if (CollisionDetection.CheckBetweenPoints(start, tip, TestFlags.Beams, scene, ref res)) return; // tip through a wall
        float reach = b.MaxDistance > 0 ? MathF.Min(b.MaxDistance, 200) : 200;
        res = new CollisionResult();
        Vector3 end = start + dir * reach;
        if (CollisionDetection.CheckBetweenPoints(start, end, TestFlags.Beams, scene, ref res)) end = res.Position;
        float range = (end - start).Length;
        if (PlayerOnPath(b, start, dir, range) is float body) range = body;
        // meet the real path by the landing (or a little short of a wall on the line from the tip)
        float fade = MathF.Max(range, 0.5f);
        if (Blocked(tip, start + dir * range, scene, out _) is Vector3 block)
        {
            float meet = Vector3.Dot(block - start, dir) - 0.25f;
            if (meet < 0.5f || Blocked(tip, start + dir * meet, scene, out _) != null) return;
            fade = meet;
        }
        bolt.Shift = tip - start;
        bolt.FadeAt = fade;
        Vector3 line = start + dir * fade - tip;
        if (line.LengthSquared > 1e-6f)
        {
            bolt.Dir = line.Normalized();
            _hbLens[bolt] = (tip, bolt.Dir, line.Length);
        }
    }

    // Corruption's lens over ours: its gun and its effects share one projection, 55 degrees high (Dolphin FIFO 11-14: M22
    // 1.9209824), against the scene's (FIELD OF VIEW, wider)
    static float HyperLensFocus(Scene scene)
    {
        float ours = MathF.Abs(scene.PerspectiveMatrix.M22);
        return ours > 1e-6f ? HyperBeamGunFxFocus * HyperBeamGunFxDepth / ours : 1;
    }

    // first person: a point of a shot GunTipPath started on the gun as Corruption's lens shows it: its x, y in the view
    // times the lens (HyperLensFocus), eased back to ours along the drawn line by where it meets the sim's, so the hit
    // lands where the sim's is (the ease is ours: Corruption draws all of it so, a gap). Draw only. carry: the shot's
    // SmoothCarry; s: the scale the point takes (a camera-facing size's)
    Vector3 HyperLens(BlasterBolt bolt, Vector3 world, Matrix4 carry, Matrix4 view, float lens, out float s)
    {
        s = 1;
        if (!_hbLens.TryGetValue(bolt, out var l) || MathF.Abs(lens - 1) < 1e-4f) return world;
        float t = Vector3.Dot(world - Vector3.TransformPosition(l.From, carry), Vector3.TransformVector(l.Along, carry)) / l.Len;
        s = 1 + (lens - 1) * (1 - Math.Clamp(t, 0, 1));
        Vector3 v = Vector3.TransformPosition(world, view);
        v.X *= s;
        v.Y *= s;
        return Vector3.TransformPosition(v, Matrix4.Invert(view));
    }

    // where a shot is drawn this tick: a bolt as the blaster's (BoltDrawn: from the tip onto the simulation's line); a
    // charged one (MphRead BeamHyperWind.cs: it curls as Corruption's) where the simulation has it, eased from the drawn
    // cannon's tip as it leaves, along its own flight. Corruption's gun fires in the player's update, after the frame's
    // PreThink and Think passes (main.dol 0x8027351c: PreThink 0x802738d0, Think 0x8027396c, then the player 0x80273f40
    // -> 0x80156224 -> ... -> the gun's state machine at gun +0x510, 0x80189d58 -> the stream's fire 0x8018b0c4), and makes
    // the shot at the LBEAM locator (0x80071268): it's drawn there that frame and moved from the next. The sim does the
    // same since sim 21 (BeamProjectileEntity.Process: no move or hit its first frame), so the stream starts in the muzzle
    // flash and every frame, the hit's included, is Corruption's (vc 687 drew a move behind a sim that moved at once)
    static Vector3 HyperDrawn(BlasterBolt bolt, BeamProjectileEntity b)
    {
        if (!b.HyperWind) return BoltDrawn(bolt, b);
        if (b.Velocity.LengthSquared > 1e-8f) bolt.Dir = b.Velocity.Normalized();
        Vector3 pos = b.Position;
        if (bolt.ArmPath) return b.HostShifted(pos);
        if (bolt.Shift == Vector3.Zero) return pos;
        float ease = 1 - (pos - bolt.Spawn).Length / bolt.FadeAt;
        return ease <= 0 ? pos : pos + bolt.Shift * ease;
    }

    // a shot's hit: the CRSC's effect and decal in Retro's hit frame (+Y out of the surface), at the effects' own scale
    void HyperImpact(RetroParticles lib, HyperShotLook look, Vector3 pos, Vector3 n)
    {
        Matrix4 at = HyperBoltFrame(n, pos + n * 0.01f, 1); // Corruption's hit + normal x 0.01 (main.dol)
        if (look.Impact != 0 && lib.Get(look.Impact) != null)
            HyperSpent(new DroneFxLive { Sys = new RetroParticleSystem(lib, look.Impact, at, follow: false, seed: _hbSeed++), Graded = true });
        foreach (uint q in look.Decals)
            HyperSpent(new DroneFxLive { Sys = new RetroParticleSystem(lib, q, at, follow: false, seed: _hbSeed++), Graded = true });
    }

    // an effect its own data ends (PSLT): no EmitLeft, so TickDroneFx leaves it emitting, and TickHyperBeamFx turns it off
    // once Done (its PSLT is up and what it made has run out), so TickDroneFx lets it go
    void HyperSpent(DroneFxLive fx)
    {
        _fxLive.Add(fx);
        _hbSpent.Add(fx);
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
        _hbHitSounds.Clear();
        _hbVoices.Clear();
        _hbSeats.Clear();
        _hbSpent.Clear();
        _hbLens.Clear();
    }

    // a head's B1RT: a CNST, or a RAND between two CNSTs (the burst's 0..360), as Corruption's each frame
    float HeadRoll(Part.Elem? e)
    {
        static float K(Part.Elem? k) => k?.Kind == "CNST" ? k.F : 0;
        return e?.Kind switch
        {
            "CNST" => e.F,
            "RAND" => K(e.Kid(0)) + (float)_hbHeadRand.NextDouble() * (K(e.Kid(1)) - K(e.Kid(0))),
            _ => 0,
        };
    }

    // DrawSkins (MatchSkins.cs), after DrawDroneFx (which draws the bolts' systems): their swooshes
    void DrawHyperBeamFx(Scene scene, SuitPass pass)
    {
        _hbCannonGun = _cannonActive && _host?.Player is PlayerEntity me ? me.GunDrawTransform : null; // (UpdateArmCannon's)
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _hbBeam == null) return;
        if (_hbBolts.Count == 0 && _hbDying.Count == 0) return;
        if (!FxGl()) return;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        // the trails' points are in the world (path mode): a live shot's carried as it is (smooth frames), an ended one's
        // where they are; the camera for the ribbons facing it
        Vector3 camera = scene.ViewMatrix.Inverted().Row3.Xyz;
        float lens = HyperLensFocus(scene);
        void Draw(BlasterBolt bolt, BeamProjectileEntity? beam)
        {
            if (HyperHidden("trail")) return;
            Matrix4 carry = beam != null ? SmoothCarry(beam, scene) : Matrix4.Identity;
            bool lensed = _hbLens.ContainsKey(bolt);
            int wi = 0;
            foreach (RetroSwoosh w in bolt.Swooshes)
            {
                if (HyperHidden($"trail{wi++}")) continue;
                _blasterVerts.Clear();
                w.Triangles(carry, _blasterVerts, camera);
                if (_blasterVerts.Count == 0) continue;
                if (_blasterVerts.Count * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, Math.Max(_fxVerts.Length * 2, _blasterVerts.Count * 10));
                int n = 0;
                foreach (RetroSwoosh.Vertex v in _blasterVerts)
                {
                    Vector3 pos = lensed ? HyperLens(bolt, v.Pos, carry, scene.ViewMatrix, lens, out _) : v.Pos;
                    Vert(ref n, vp, pos, (v.Uv.X, v.Uv.Y), v.Color);
                }
                (RetroBlend blend, _) = w.Mode;
                DrawRun(w.Texture, blend, n, white: w.Texture == 0);
            }
        }
        foreach (KeyValuePair<BeamProjectileEntity, BlasterBolt> live in _hbBolts) Draw(live.Value, live.Key);
        foreach (BlasterBolt bolt in _hbDying) Draw(bolt, null);
        // the live shots' heads (gone with the shot), facing the camera, each rolled afresh
        Matrix4 view = scene.ViewMatrix;
        Vector3 right = new(view.M11, view.M21, view.M31), up = new(view.M12, view.M22, view.M32);
        HyperShotLook? headLook = null;
        void Head(ref int n, Vector3 pos, float scale)
        {
            float roll = MathHelper.DegreesToRadians(HeadRoll(headLook!.HeadRoll)), half = HyperBeamHeadSize * 0.5f * HyperBeamFxScale * scale;
            float c = MathF.Cos(roll) * half, sn = MathF.Sin(roll) * half;
            Vector3 r = right * c + up * sn, u = up * c - right * sn;
            Quad(ref n, vp, pos - r + u, pos + r + u, pos - r - u, pos + r - u, HyperBeamHeadColor);
        }
        foreach (HyperShotLook? look in new[] { _hbBeam, _hbBurst })
        {
            if (look == null || look.Head == 0 || HyperHidden("head")) continue;
            bool burst = look == _hbBurst;
            headLook = look;
            int n = 0;
            foreach (KeyValuePair<BeamProjectileEntity, BlasterBolt> live in _hbBolts)
            {
                if ((live.Key.Flags.TestFlag(BeamFlags.Charged) && _hbBurst != null) != burst) continue;
                Matrix4 carry = SmoothCarry(live.Key, scene);
                Vector3 at = Vector3.TransformPosition(live.Value.Pos, carry);
                float scale = 1;
                if (_hbLens.ContainsKey(live.Value)) at = HyperLens(live.Value, at, carry, scene.ViewMatrix, lens, out scale);
                Head(ref n, at, scale);
            }
            DrawRun(look.Head, look.HeadAdditive, n);
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
}
