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

// The Pirate Blaster's shots in Prime 1's look (owner 2026-10-10: "let's try red for now": the regular Space Pirate's shot,
// WPSC aba4b71d, not the Power Trooper's yellow one), every seat's, when the Trooper import carries it
// (recipes/bodies/MP1PowerTrooper.json "fx" -> bodies/MP1PowerTrooper/fx, Core Import/Effects/BodyFx.cs "part" lines).
// Each bolt: the WPSC's two systems (APSM 91f9a346 riding the bolt (AP11 on), APS2 d4f701ff left where it was made (AP21
// off), both oriented along the shot (APSO)), run by Core Campaign/RetroParticles.cs and drawn with the drone's effects
// (CampaignRenderer.DroneFx.cs: _fxLive); the three swooshes their systems trail (SSWH), run and drawn here (Core
// Campaign/RetroSwoosh.cs); Prime's shot sound per bolt (548); where it hits, its CRSC's default effect and sound (fx.cfg
// impact / impactsound). MPH's look for those shots is hidden (MphRead PlayerEntity.HostOwnPirateBlasterLook ->
// BeamProjectileEntity.HostOwnLook) and its shot / hit sounds not played (HostOwnPirateBlasterSfx / HitSfx). Drawing and
// sound only: the shots, their speed, damage and hits are the simulation's, the same with or without the import. Without
// it: MphRead's recoloured Power Beam (as before).
internal sealed partial class CampaignRenderer
{
    const string BlasterFxBody = "MP1PowerTrooper";
    // Prime metres -> world units (about a Space Pirate's size to a hunter's; owner to tune by eye)
    const float BlasterFxScale = 0.5f;
    const uint BlasterShotSound = 548; // every Space Pirate's shot (Prime sound id)
    // the hit (release test N7, owner 2026-10-10: "is there an impact sound? ... impact image"): at BlasterFxScale the
    // Prime hit was ~0.15 units across, smaller than the bolt (-pirateblastertest impact); x1.5 with 4 emit frames
    // reads at ~0.6-1 unit like MPH's Power Beam hit. Its sound at vanilla's beam-hit range (BeamProjectileEntity: 2).
    const float BlasterImpactScale = 1.5f;
    const int BlasterImpactEmit = 4;
    const int BlasterHitSfxRange = 2;
    // the hit sound (release 0.2.0 F4, owner: "too quiet" on walls, "inaudible" over the damage sound on players):
    // played wherever MPH plays its beam's hit sound (BeamProjectileEntity.HostHitSfx: walls, players including the main
    // one, doors, ...), not only where a hit effect spawns. Prime's 547 sits at MPH's Power Beam hit level (-19 dBFS RMS
    // after the game's volumes, -blastersfxlevels); raised to the Imperialist hit's (-15, MPH's loudest beam hit): x1.7
    // on the samples (peak -6.2 -> -1.6 dBFS, no clipping; PlayPcm clamps its gain at 1)
    const float BlasterHitGain = 1.7f;

    // Drawn path (release test N6, owner 2026-10-10: "starts really far away from the players arm cannon"): the sim's shot
    // starts at the eye and is already a step (2 units) out when first seen, so each bolt is drawn from the cannon tip the
    // player sees (third person: the body's tip, or MPH's shoot node where its muzzle flash is; first person: the muzzle
    // flash's point), on a straight line to where the real shot lands, starting at the tip and catching up with the
    // real shot over BlasterCatchUp units. The muzzle -> tip line and tip -> landing line are wall-checked: a tip behind a
    // wall, or a wall right beside it, keeps the sim's path. Draw only.
    const float BlasterCatchUp = 6;
    const float BlasterNear = 1.5f;
    // first person (owner 2026-10-10: the flash at the cannon "a bit smaller ... to match the beam"): a bolt starting at
    // the tip is right in front of the camera, where its white core filled a big part of the screen. In the player's own
    // view it starts at BlasterOwnStart of its size and grows to full over its first BlasterOwnGrow units
    const float BlasterOwnStart = 0.2f;
    const float BlasterOwnGrow = 3;
    // every bolt's flash the same (release 0.2.0 F4, owner: "the size is inconsistent ... can it just be red?"): the
    // bolt's systems run from one fixed seed (their random sizes alike every shot), and the first-person growth goes by
    // how far the drawn bolt has come from where it was first drawn (not from the sim's spawn point, which sits a varying
    // distance behind the tip)
    const int BlasterSeed = 0x5EED;

    sealed class BlasterBolt
    {
        public Vector3 Spawn, Pos, Dir, SimDir;
        public Vector3 Shift;      // tip - spawn, eased to nothing at FadeAt along the path (Zero: no shift)
        public float FadeAt = 1, Lead;
        public bool ArmPath;       // the arm-cannon thread's own path (HostDrawShift/HostDrawHide) is set: use it
        public bool OwnView;       // the main player's own shot seen in first person (BlasterOwnStart)
        public float Age;
        public Vector3 First;      // where it was first drawn (BoltScale)
        public bool HasFirst;
        public bool Live = true;
        public readonly List<DroneFxLive> Fx = new();
        public readonly List<RetroSwoosh> Swooshes = new();
    }

    bool _blasterRead;
    RetroParticles? _blasterLib;
    BodyFx.Config? _blasterCfg;
    readonly Dictionary<uint, (short[] Pcm, int Rate)> _blasterSounds = new();
    readonly List<Part.Swoosh> _blasterSwooshes = new();
    readonly Dictionary<BeamProjectileEntity, BlasterBolt> _blasterBolts = new();
    readonly List<BlasterBolt> _blasterDying = new();
    readonly List<(BeamProjectileEntity Beam, CollisionResult Col, bool Expired)> _blasterHits = new();
    readonly List<BeamProjectileEntity> _blasterGone = new();
    readonly List<RetroSwoosh.Vertex> _blasterVerts = new();
    readonly SoundSource _blasterSound = new();

    // bodies/MP1PowerTrooper/fx, read once the imports' folder is known; false without the shot in it
    bool BlasterFxReady()
    {
        if (_blasterRead) return _blasterCfg != null;
        if (BodiesRoot is not string root) return false;
        _blasterRead = true;
        if (!CampaignRetroBody.Imported(root, BlasterFxBody)) return false; // ship-safe audit: only the importer's body
        string dir = CampaignRetroBody.DirOf(root, BlasterFxBody);
        try
        {
            BodyFx.Config? cfg = BodyFx.Config.Load(dir);
            if (cfg == null || cfg.Parts.Count == 0) return false;
            string fx = Path.Combine(dir, "fx");
            RetroParticles lib = RetroParticles.Load(fx);
            // the swooshes the shot's systems trail (their SSWH, through their child systems)
            var seen = new HashSet<uint>();
            var todo = new Queue<uint>(cfg.Parts);
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!seen.Add(id) || lib.Get(id) is not Part part) continue;
                foreach ((string type, uint dep) in part.Dependencies())
                {
                    if (type == "PART") todo.Enqueue(dep);
                    else if (type == "SWHC" && File.Exists(Path.Combine(fx, $"{dep:x8}.swhc")))
                    {
                        _blasterSwooshes.Add(Part.Swoosh.Load(File.ReadAllBytes(Path.Combine(fx, $"{dep:x8}.swhc"))));
                    }
                }
            }
            foreach (uint id in cfg.Sfx.Keys)
            {
                string wav = Path.Combine(fx, BodyFx.SfxFile(id));
                if (File.Exists(wav) && MusyX.ReadWav(wav, out int rate) is short[] pcm && pcm.Length > 0)
                {
                    if (id == cfg.ImpactSound)
                    {
                        pcm = (short[])pcm.Clone();
                        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)(pcm[i] * BlasterHitGain), short.MinValue, short.MaxValue);
                    }
                    _blasterSounds[id] = (pcm, rate);
                }
            }
            foreach (string png in Directory.GetFiles(fx, "*.png"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) _fxTexFile.TryAdd(t, png);
            }
            _blasterLib = lib;
            _blasterCfg = cfg;
            Log.Info("MPHAudio", $"Pirate Blaster: Prime shot from {BlasterFxBody}: {lib.Count} effects, {_blasterSwooshes.Count} swooshes, {_blasterSounds.Count} sounds");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHAudio", $"Pirate Blaster: the Prime shot failed to load, MPH's look kept: {ex.Message}");
            _blasterLib = null;
            _blasterCfg = null;
            _blasterSwooshes.Clear();
            _blasterSounds.Clear();
        }
        return _blasterCfg != null;
    }

    // TickSkins (MatchSkins.cs), before TickDroneFx (which steps the systems in _fxLive): the bolts one 60 Hz frame on
    void TickBlasterFx()
    {
        bool on = BlasterFxReady();
        PlayerEntity.HostOwnPirateBlasterLook = on;
        PlayerEntity.HostOwnPirateBlasterSfx = on && _sfx != null && _blasterSounds.ContainsKey(BlasterShotSound);
        PlayerEntity.HostOwnPirateBlasterHitSfx = on && _sfx != null && _blasterSounds.ContainsKey(_blasterCfg!.ImpactSound);
        if (!on) return;
        DevBlasterHands();
        RetroParticles lib = _blasterLib!;
        BodyFx.Config cfg = _blasterCfg!;
        BeamProjectileEntity.HostImpact = OnHostShotHit;
        BeamProjectileEntity.HostHitSfx = OnBlasterHitSfx;
        // where bolts hit this step, the sound (MphRead's hit-sound spots)
        if (_sfx != null)
        {
            foreach (Vector3 at in _blasterHitSounds)
            {
                _blasterSound.Update(at, BlasterHitSfxRange);
                PlayDroneSound(_blasterSounds, cfg.ImpactSound, _blasterSound);
            }
        }
        _blasterHitSounds.Clear();
        // where bolts hit this step: Prime's hit effect facing out of the surface, and its sound
        foreach ((BeamProjectileEntity beam, CollisionResult col, bool expired) in _blasterHits)
        {
            if (_blasterBolts.TryGetValue(beam, out BlasterBolt? hit)) hit.Pos = col.Position;
            if (expired) continue; // ran out of life in the air: nothing hit
            if (cfg.Impact == 0 || lib.Get(cfg.Impact) == null) continue;
            Vector3 n = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -beam.Direction;
            Matrix4 at = Frame(n, col.Position + n * 0.05f, BlasterImpactScale);
            _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib, cfg.Impact, at, follow: false, seed: Environment.TickCount), EmitLeft = BlasterImpactEmit });
        }
        _blasterHits.Clear();
        BlasterShotSounds();
        // every seat's blaster bolts: new ones start, gone ones stop emitting (a pool slot reused: a new bolt)
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p?.EquipInfo.Beams is not BeamProjectileEntity[] beams) continue;
            foreach (BeamProjectileEntity b in beams)
            {
                bool alive = b.HostBlasterLook && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided);
                _blasterBolts.TryGetValue(b, out BlasterBolt? bolt);
                if (bolt != null && (!alive || b.SpawnPosition != bolt.Spawn || b.Age < bolt.Age))
                {
                    EndBolt(bolt);
                    _blasterBolts.Remove(b);
                    bolt = null;
                }
                if (!alive) continue;
                if (bolt == null) _blasterBolts[b] = bolt = StartBolt(b, lib, cfg);
                bolt.Age = b.Age;
                bolt.Pos = BoltDrawn(bolt, b);
                Matrix4 at = BoltFrame(bolt.Dir, bolt.Pos, BoltScale(bolt));
                foreach (DroneFxLive fx in bolt.Fx) fx.Sys.Transform = at;
                foreach (RetroSwoosh w in bolt.Swooshes) w.Step();
            }
        }
        // ended bolts: their trails run out where the bolt stopped
        for (int i = _blasterDying.Count - 1; i >= 0; i--)
        {
            BlasterBolt bolt = _blasterDying[i];
            bool done = true;
            foreach (RetroSwoosh w in bolt.Swooshes)
            {
                w.Step();
                if (!w.Done) done = false;
            }
            if (done) _blasterDying.RemoveAt(i);
        }
    }

    // dev only (PC pictures of the shot: SecondHunt.exe with MPH_DEV_BLASTER=1 in its environment, never set on a
    // device, ignored in public builds): every seat is handed the blaster once, at the start (the bots then fire it)
    readonly HashSet<PlayerEntity> _devBlasterGiven = new();
    int _devBlasterFrame;
    void DevBlasterHands()
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || Environment.GetEnvironmentVariable("MPH_DEV_BLASTER") != "1") return;
        // MPH_DEV_BLASTER_FIRE=1: the pad's B (a fire button) held, so desktop pictures get the main player's bolts
        if (Environment.GetEnvironmentVariable("MPH_DEV_BLASTER_FIRE") == "1") Pad.Set(PadButton.B, _devBlasterFrame++ % 90 < 70);
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || p.Health <= 0 || !_devBlasterGiven.Add(p)) continue;
            p.HostPickUpWeapon(ItemType.PirateBlaster);
            Log.Info("MPHMatch", $"dev: seat {p.SlotIndex} handed the Pirate Blaster");
        }
    }

    BlasterBolt StartBolt(BeamProjectileEntity b, RetroParticles lib, BodyFx.Config cfg)
    {
        Vector3 dir = b.Direction.LengthSquared > 1e-6f ? b.Direction.Normalized() : Vector3.UnitZ;
        var bolt = new BlasterBolt { Spawn = b.SpawnPosition, Age = b.Age, Dir = dir, SimDir = dir };
        bolt.Lead = MathF.Max(0, Vector3.Dot(b.Position - b.SpawnPosition, dir));
        BoltPath(bolt, b);
        bolt.Pos = BoltDrawn(bolt, b);
        bolt.First = bolt.Pos;
        bolt.HasFirst = true;
        if (_devBlasterGiven.Count > 0) Log.Info("MPHMatch", FormattableString.Invariant(
            $"dev: blaster bolt seat {(b.Owner as PlayerEntity)?.SlotIndex} lead {bolt.Lead:0.00} arm {bolt.ArmPath} shift {bolt.Shift.Length:0.00} fade {bolt.FadeAt:0.0} drawn-from-spawn {(bolt.Pos - bolt.Spawn).Length:0.00}"));
        int seed = BlasterSeed;
        // first person: the second part's puff (APS2) would hang right in front of the camera as a big cloud
        bool ownView = b.Owner is PlayerEntity { IsMainPlayer: true } op && !op.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
        bolt.OwnView = ownView;
        Matrix4 at = BoltFrame(bolt.Dir, bolt.Pos, BoltScale(bolt));
        for (int i = 0; i < cfg.Parts.Count; i++)
        {
            if (lib.Get(cfg.Parts[i]) == null || (ownView && i >= 1)) continue;
            // the first (APSM) rides the bolt, the second's particles stay where they were made
            var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib, cfg.Parts[i], at, follow: i == 0, seed: seed + i) };
            _fxLive.Add(fx);
            bolt.Fx.Add(fx);
        }
        for (int i = 0; i < _blasterSwooshes.Count; i++) bolt.Swooshes.Add(new RetroSwoosh(_blasterSwooshes[i], seed ^ (i + 1) * 7919));
        return bolt;
    }

    // Prime's shot sound, once per bolt fired (the seat's HostPirateBlasterBolts count), at the cannon; the main player's
    // own shot as vanilla's (PlayerProcess: his sound source is "self"). Not tied to a drawn bolt (release 0.2.0 F4: "still
    // muffled facing a wall ... you dont even have to be that close"): a bolt fired a few units from a wall hits it in the
    // step it spawns, was never seen alive here, and so never sounded: only its hit was heard.
    readonly Dictionary<PlayerEntity, int> _blasterShotsHeard = new();
    void BlasterShotSounds()
    {
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            int bolts = p.HostPirateBlasterBolts;
            if (!_blasterShotsHeard.TryGetValue(p, out int heard) || bolts < heard) heard = bolts;
            _blasterShotsHeard[p] = bolts;
            if (_sfx == null || bolts == heard) continue;
            bool self = p.IsMainPlayer && !p.IsAltForm && !PlayerEntity.HostMainSoundsPlaced;
            Vector3 at = BlasterTip(p) ?? p.Position;
            for (int i = 0; i < Math.Min(bolts - heard, PirateBlaster.BurstBolts); i++)
            {
                _blasterSound.Update(at, self ? -1 : DroneSfxRange);
                PlayDroneSound(_blasterSounds, BlasterShotSound, _blasterSound);
            }
        }
    }

    // the cannon tip the shooter is seen firing from (header), or null
    static Vector3? BlasterTip(PlayerEntity p)
    {
        if (!p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) return p.IsMainPlayer ? p.HostFxMuzzlePosition : null;
        if (p.HostFxShootOffset is Vector3 off)
        {
            var ahead = new Vector3(p.Field70, 0, p.Field74);
            if (ahead.LengthSquared < 1e-12f) return null;
            ahead.Normalize();
            return p.Position + Vector3.Cross(Vector3.UnitY, ahead) * off.X + Vector3.UnitY * off.Y + ahead * off.Z;
        }
        Vector3 shoot = p.HostShootPoint;
        return shoot != Vector3.Zero && Vector3.DistanceSquared(shoot, p.Position) < 9 ? shoot : null;
    }

    // once per bolt: the drawn line from the tip (header)
    void BoltPath(BlasterBolt bolt, BeamProjectileEntity b)
    {
        if (b.HostDrawShift != null || b.HostDrawHide > 0)
        {
            bolt.ArmPath = true; // ArmCannonBeamPath.cs already drew this shot from the Trooper's arm
            return;
        }
        if (_host == null || b.Owner is not PlayerEntity p || BlasterTip(p) is not Vector3 tip) return;
        Vector3 start = b.SpawnPosition, dir = bolt.SimDir;
        if ((tip - start).LengthSquared < 0.0025f) return; // already at the tip
        Scene scene = _host.Scene;
        float reach = b.MaxDistance > 0 ? MathF.Min(b.MaxDistance, 200) : 200;
        var res = new CollisionResult();
        Vector3 end = start + dir * reach;
        if (CollisionDetection.CheckBetweenPoints(start, end, TestFlags.Beams, scene, ref res)) end = res.Position;
        float range = (end - start).Length;
        res = new CollisionResult();
        if (CollisionDetection.CheckBetweenPoints(start, tip, TestFlags.Beams, scene, ref res)) return; // tip through a wall
        float fade;
        if (Blocked(tip, end, scene, out float at) is not Vector3 block) fade = MathF.Max(range, BlasterNear);
        else
        {
            // a wall on the line from the tip: meet the real path just before it, if that line is clear
            float s = Vector3.Dot(block - start, dir);
            float meet = s - 0.5f;
            if (at < BlasterNear || meet < BlasterNear || Blocked(tip, start + dir * meet, scene, out _) != null) return;
            fade = meet;
        }
        bolt.Shift = tip - start;
        bolt.FadeAt = fade;
        Vector3 line = start + dir * fade - tip;
        if (line.LengthSquared > 1e-6f) bolt.Dir = line.Normalized();
    }

    // where the bolt is drawn this frame: from the tip, catching up with the real shot (header)
    static Vector3 BoltDrawn(BlasterBolt bolt, BeamProjectileEntity b)
    {
        float s = MathF.Max(0, Vector3.Dot(b.Position - bolt.Spawn, bolt.SimDir));
        float lag = bolt.Lead * MathF.Max(0, 1 - (s - bolt.Lead) / BlasterCatchUp);
        float d = MathF.Max(0, s - lag);
        Vector3 point = bolt.Spawn + bolt.SimDir * d;
        if (bolt.ArmPath) return b.HostShifted(point);
        if (bolt.Shift == Vector3.Zero) return point;
        float ease = 1 - d / bolt.FadeAt;
        if (ease <= 0)
        {
            bolt.Dir = bolt.SimDir;
            return point;
        }
        return point + bolt.Shift * ease;
    }

    void EndBolt(BlasterBolt bolt)
    {
        bolt.Live = false;
        foreach (DroneFxLive fx in bolt.Fx) fx.Sys.Emitting = false;
        foreach (RetroSwoosh w in bolt.Swooshes) w.Emitting = false;
        _blasterDying.Add(bolt);
    }

    // DroneFx's HostImpact: a blaster bolt's hit here, the rest the drone's
    void OnHostShotHit(BeamProjectileEntity beam, CollisionResult col, bool expired)
    {
        if (!beam.HostBlasterLook)
        {
            OnDroneShotHit(beam, col, expired);
            return;
        }
        if (_blasterHits.Count < 64) _blasterHits.Add((beam, col, expired));
    }

    // BeamProjectileEntity.HostHitSfx: a blaster bolt's hit sound, played on the next tick
    readonly List<Vector3> _blasterHitSounds = new();
    void OnBlasterHitSfx(BeamProjectileEntity beam)
    {
        if (beam.HostBlasterLook && _blasterHitSounds.Count < 64) _blasterHitSounds.Add(beam.Position);
    }

    // a replay restarting its match (ResetDroneFx)
    void ResetBlasterFx()
    {
        _blasterBolts.Clear();
        _blasterDying.Clear();
        _blasterHits.Clear();
        _blasterHitSounds.Clear();
        _blasterShotsHeard.Clear();
    }

    // effect space (Prime: +Y forward, +Z up) along a shot, scaled, at a point
    // the bolt's size: full, or in the player's own first-person view growing from BlasterOwnStart near the cannon
    static float BoltScale(BlasterBolt bolt)
    {
        if (!bolt.OwnView) return 1;
        float t = Math.Clamp((bolt.Pos - (bolt.HasFirst ? bolt.First : bolt.Spawn)).Length / BlasterOwnGrow, 0, 1);
        return BlasterOwnStart + (1 - BlasterOwnStart) * t;
    }

    static Matrix4 BoltFrame(Vector3 dir, Vector3 at, float scale = 1)
    {
        Vector3 y = dir;
        Vector3 refUp = MathF.Abs(y.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 z = (refUp - y * Vector3.Dot(refUp, y)).Normalized();
        Vector3 x = Vector3.Cross(y, z);
        var m = Matrix4.Identity;
        m.Row0.Xyz = x * (BlasterFxScale * scale);
        m.Row1.Xyz = y * (BlasterFxScale * scale);
        m.Row2.Xyz = z * (BlasterFxScale * scale);
        m.Row3.Xyz = at;
        return m;
    }

    // DrawSkins (MatchSkins.cs), after DrawDroneFx (which draws the bolts' systems): their swooshes
    void DrawBlasterFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _blasterCfg == null) return;
        if (_blasterBolts.Count == 0 && _blasterDying.Count == 0) return;
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
        void Draw(BlasterBolt bolt)
        {
            Matrix4 at = BoltFrame(bolt.Dir, bolt.Pos, BoltScale(bolt));
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
        foreach (BlasterBolt bolt in _blasterBolts.Values) Draw(bolt);
        foreach (BlasterBolt bolt in _blasterDying) Draw(bolt);
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
