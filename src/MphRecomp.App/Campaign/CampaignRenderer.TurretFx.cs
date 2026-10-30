using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Shooting Gallery's turret in Echoes' effects (docs/SHOOTING_GALLERY_TURRET.md section 9.1): the PARTs and ELSCs the arena
// import copied from the player's own disc (EchoesTurret.ImportFx, hd/turret_fx/, listed in turret.json's Fx), run from
// what the turret entity does, as the area's connections and the actor's animation events start them. Clients mirror
// the host's turret (NetTurret), so every device runs the same from its own copy.
//   lift: EFCT 'Effect_LiftFX5' from Enter until Leave / Destroyed; lower: 'Effect_LowerTurret' from Leave (its own life)
//   explode: 'Effect_TurretExplode' at each blow-up (MPH's stand-in hidden: EchoesTurretEntity.HostOwnExplode)
//   ballglow: turret_ball_up's / turret_ball_down's event on ball_SDK; muzzle, smoke: turret_shoot's on muzzleend_LCTR
//   held: 'BombSlotElectric' (ELSC) while the slot holds a ball (BALT ENTR / EXIT)
//   bombarc: 'Arc' (ELSC) as a bomb takes the turret (DTRG DEAD; TIMR 0x272 stops it 0.5 s later)
//   grate: the grates' break in Echoes' debris (TickGrateFx), here and in Sidehopper Station (whose turret.json has no
//   turret: its grates' break alone)
// The particle systems go in the drone effects' live list (_fxLive: stepped by TickDroneFx, drawn by DrawDroneFx); the
// arcs' lines are drawn here, as the Hyper Ball's are, with Retro's GPSM / EPSM systems at each bolt's ends.
internal sealed partial class CampaignRenderer
{
    sealed class TurretFxLib
    {
        public RetroParticles Parts = null!;
        public TurretLook Look = null!;
        public string Dir = "";   // the effects' folder (EchoesTurret.FxDir): the seat HUD's textures too
        public readonly Dictionary<string, TurretFx> Roles = new(StringComparer.Ordinal);
        public readonly Dictionary<uint, Elsc> Elscs = new();
    }

    // an arc and the systems at its bolts' ends
    sealed class TurretArc
    {
        public RetroElectric Elec = null!;
        public uint Gen, End;
        public readonly Dictionary<object, (DroneFxLive? Gen, DroneFxLive? End)> Bolts = new();
        public int Left = -1;   // frames until it stops (-1: held by the caller)
    }

    TurretFxLib? _turretFx;
    int _turretFxRoom = -1;
    EchoesTurretEntity? _turretFxOf;
    EchoesTurretEntity.TurretState _turretFxState;
    int _turretFxShots, _turretFxBlowups;
    bool _turretFxHeld;
    DroneFxLive? _turretLift;
    // the ball's green halo (ballglow, a007a9a9): every raised clip's event starts it with duration 0 (on until a clip
    // change stops it), so it glows round the ball the whole time the turret is up; turret_ball_down's start turns its
    // emission off (its particles fade) and plays the down clip's own (ballglowdown: 15 frames, then wiped)
    DroneFxLive? _turretBallGlow, _turretDownGlow;
    TurretArc? _turretHeldArc, _turretBombArc;
    readonly List<(DroneFxLive Fx, TurretPiece Bone)> _turretOnBones = new();
    readonly List<(TurretFx Fx, float At)> _turretDue = new();      // a shot's events still to start (SinceShot)
    readonly List<(object Bolt, Vector3 Start, Vector3 End)> _turretEnds = new();
    readonly HashSet<object> _turretSeen = new();
    readonly List<RetroElectric.Line> _turretLines = new();

    // TickSkins (MatchSkins.cs), before TickDroneFx (which steps the systems in _fxLive)
    void TickTurretFx()
    {
        if (_host == null || _host.Ended)
        {
            StopTurretFx();
            EchoesBreakables.HostOwnGrateBreak = false;
            _grateBits.Clear();
            return;
        }
        if (_turretFxRoom != _host.RoomId)
        {
            StopTurretFx();
            _turretFxRoom = _host.RoomId;
            _turretFx = LoadTurretFx();
        }
        EchoesTurretEntity.HostOwnExplode = _turretFx != null && _turretFx.Roles.ContainsKey("explode");
        TickGrateFx();
        TickTurretShots();
        EchoesTurretEntity? t = _turretFx == null ? null : NetWorld.TurretOf(_host.Scene);
        if (t != _turretFxOf)
        {
            StopTurretFx();
            _turretFxOf = t;
            if (t != null)
            {
                _turretFxState = t.State;
                _turretFxShots = t.Shots;
                _turretFxBlowups = t.Destructions;
                _turretFxHeld = t.Held != null;
                if (t.State is EchoesTurretEntity.TurretState.Entering or EchoesTurretEntity.TurretState.Active)
                {
                    _turretLift = TurretPlaced("lift", held: true);
                    _turretBallGlow = TurretOnBone("ballglow", t, held: true);
                }
            }
        }
        if (t == null) return;
        TurretFxLib lib = _turretFx!;

        // the state's effects
        if (t.State != _turretFxState)
        {
            switch (t.State)
            {
                case EchoesTurretEntity.TurretState.Entering:
                    StopFx(ref _turretLift);
                    _turretLift = TurretPlaced("lift", held: true);
                    StopFx(ref _turretBallGlow);
                    _turretBallGlow = TurretOnBone("ballglow", t, held: true);
                    break;
                case EchoesTurretEntity.TurretState.Leaving:
                    StopFx(ref _turretLift);
                    TurretPlaced("lower");
                    StopFx(ref _turretBallGlow); // (the clip's start: emission off, its particles (LTME 36-100) fade)
                    _turretDownGlow = TurretOnBone("ballglowdown", t);
                    break;
                case EchoesTurretEntity.TurretState.Destroyed:
                    StopFx(ref _turretLift); // (no ballglow: Echoes snaps to rest, turret_ball_down and its event don't play)
                    StopFx(ref _turretBallGlow);
                    break;
                case EchoesTurretEntity.TurretState.Free:
                    StopFx(ref _turretBallGlow);
                    break;
            }
            _turretFxState = t.State;
        }
        bool held = t.Held != null;
        if (t.Destructions != _turretFxBlowups)
        {
            if (t.Destructions > _turretFxBlowups)
            {
                TurretPlaced("explode");
                // a blow-up with a ball in the slot is the bomb's (the slot's DTRG DEAD starts its arc)
                if (held || _turretFxHeld)
                {
                    _turretBombArc ??= TurretArcOf("bombarc");
                    if (_turretBombArc != null)
                    {
                        _turretBombArc.Elec.Emitting = true;
                        _turretBombArc.Left = (int)MathF.Round(EchoesTurret.BombArcTime * 60);
                    }
                }
            }
            _turretFxBlowups = t.Destructions;
        }
        _turretFxHeld = held;
        if (t.Shots != _turretFxShots)
        {
            if (t.Shots > _turretFxShots)
            {
                foreach (TurretFx f in lib.Look.Fx!)
                {
                    if (f.Role is "muzzle" or "smoke") _turretDue.Add((f, f.Time));
                }
            }
            _turretFxShots = t.Shots;
        }
        for (int i = _turretDue.Count - 1; i >= 0; i--)
        {
            if (t.SinceShot + 1e-4f < _turretDue[i].At) continue;
            TurretOnBone(_turretDue[i].Fx, t);
            _turretDue.RemoveAt(i);
        }

        // the bones' effects follow their bones (mode 2: the whole system rides it). Seen by the occupant they fade with
        // the turret's body: Echoes draws the actor at alpha 0.25 in its occupant's view (PreRender, CampaignArena
        // SeatAlpha) and its event particles take the actor's draw colour (so the ball's glow, 0.2 alpha, is all but gone)
        float onBoneFade = t.Occupant != null && t.Occupant == PlayerEntity.Main ? 0.25f : 1;
        // an event effect with Wipe: its particles go as its frames end (Retro's flags bit 0: DestroyParticles)
        if (_turretDownGlow is { } down && !down.Sys.Emitting)
        {
            if (lib.Roles.TryGetValue("ballglowdown", out TurretFx? df) && df.Wipe) _fxLive.Remove(down);
            _turretDownGlow = null;
        }
        for (int i = _turretOnBones.Count - 1; i >= 0; i--)
        {
            (DroneFxLive fx, TurretPiece bone) = _turretOnBones[i];
            if (!_fxLive.Contains(fx))
            {
                _turretOnBones.RemoveAt(i);
                continue;
            }
            fx.Sys.Transform = EchoesTurret.PieceNow(lib.Look, bone, t);
            fx.Fade = onBoneFade;
        }

        // the slot's arcs
        if (held) _turretHeldArc ??= TurretArcOf("held");
        if (_turretHeldArc != null) _turretHeldArc.Elec.Emitting = held;
        StepArc(_turretHeldArc);
        if (_turretBombArc != null)
        {
            if (_turretBombArc.Left > 0 && --_turretBombArc.Left == 0) _turretBombArc.Elec.Emitting = false;
            StepArc(_turretBombArc);
        }
    }

    // a placed effect (EFCT): held = emitting until stopped, else for its own life
    DroneFxLive? TurretPlaced(string role, bool held = false)
    {
        if (_turretFx is not { } lib || !lib.Roles.TryGetValue(role, out TurretFx? f) || f.At is not { Length: 12 } a || lib.Parts.Get(f.Id) == null) return null;
        var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, f.Id, M(a), follow: true, seed: Environment.TickCount) };
        _fxLive.Add(fx);
        return held ? fx : null;
    }

    DroneFxLive? TurretOnBone(string role, EchoesTurretEntity t, bool held = false) =>
        _turretFx?.Roles.TryGetValue(role, out TurretFx? f) == true ? TurretOnBone(f, t, held) : null;

    // an animation event's effect on its bone, for its frames
    // (held: emitting until stopped, not for the event's frames)
    DroneFxLive? TurretOnBone(TurretFx f, EchoesTurretEntity t, bool held = false)
    {
        if (_turretFx is not { } lib || lib.Parts.Get(f.Id) == null || lib.Look.Locators?.FirstOrDefault(l => l.Bone == f.Bone) is not TurretPiece bone) return null;
        var fx = new DroneFxLive
        {
            Sys = new RetroParticleSystem(lib.Parts, f.Id, EchoesTurret.PieceNow(lib.Look, bone, t), follow: true, seed: Environment.TickCount ^ (int)f.Id),
            EmitLeft = held ? 0 : Math.Max(1, f.Frames),
        };
        _fxLive.Add(fx);
        _turretOnBones.Add((fx, bone));
        return fx;
    }

    TurretArc? TurretArcOf(string role)
    {
        if (_turretFx is not { } lib || !lib.Roles.TryGetValue(role, out TurretFx? f) || f.At is not { Length: 12 } a || !lib.Elscs.TryGetValue(f.Id, out Elsc? e)) return null;
        var arc = new TurretArc
        {
            Elec = new RetroElectric(e, Environment.TickCount ^ (int)f.Id) { Transform = M(a), Emitting = false },
            Gen = e["GPSM"]?.Id ?? 0, End = e["EPSM"]?.Id ?? 0,
        };
        return arc;
    }

    // an arc one 60 Hz frame on, and a GPSM / EPSM system at each live bolt's start / end, emitting while the bolt lives
    void StepArc(TurretArc? arc)
    {
        if (arc == null || _turretFx is not { } lib) return;
        arc.Elec.Step();
        _turretEnds.Clear();
        arc.Elec.Ends(_turretEnds);
        _turretSeen.Clear();
        foreach ((object bolt, Vector3 start, Vector3 end) in _turretEnds)
        {
            _turretSeen.Add(bolt);
            if (arc.Bolts.ContainsKey(bolt)) continue;
            DroneFxLive? At(uint id, Vector3 p)
            {
                if (id is 0 or UInt32.MaxValue || lib.Parts.Get(id) == null) return null;
                var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, Matrix4.CreateTranslation(p) * arc.Elec.Transform, follow: false, seed: bolt.GetHashCode()) };
                _fxLive.Add(fx);
                return fx;
            }
            arc.Bolts[bolt] = (At(arc.Gen, start), At(arc.End, end));
        }
        foreach (object bolt in arc.Bolts.Keys.Where(b => !_turretSeen.Contains(b)).ToList())
        {
            (DroneFxLive? g, DroneFxLive? e) = arc.Bolts[bolt];
            if (g != null) g.Sys.Emitting = false;
            if (e != null) e.Sys.Emitting = false;
            arc.Bolts.Remove(bolt);
        }
    }

    static void StopFx(ref DroneFxLive? fx)
    {
        if (fx != null) fx.Sys.Emitting = false;
        fx = null;
    }

    static Matrix4 M(float[] a) => new(a[0], a[1], a[2], 0, a[3], a[4], a[5], 0, a[6], a[7], a[8], 0, a[9], a[10], a[11], 1);

    void StopTurretFx()
    {
        StopFx(ref _turretLift);
        StopFx(ref _turretBallGlow);
        StopFx(ref _turretDownGlow);
        foreach (TurretArc? arc in new[] { _turretHeldArc, _turretBombArc })
        {
            if (arc == null) continue;
            foreach ((DroneFxLive? g, DroneFxLive? e) in arc.Bolts.Values)
            {
                if (g != null) g.Sys.Emitting = false;
                if (e != null) e.Sys.Emitting = false;
            }
        }
        _turretHeldArc = _turretBombArc = null;
        _turretOnBones.Clear();
        _turretDue.Clear();
        _turretFxOf = null;
        EchoesTurretEntity.HostOwnExplode = false;
        if (BeamProjectileEntity.HostOwnLookWeapon == _turretShotWeapon) BeamProjectileEntity.HostOwnLookWeapon = null;
        _turretShots.Clear();
    }

    // the turret's shots in their own look (WPSC eea4cf05's billboards, turret.json Fx roles shot1 / shot2): MphRead's
    // look hidden (BeamProjectileEntity.HostOwnLookWeapon), each live shot drawn by DrawTurretFx; each hit (counted on the
    // shot: HostHits) starts the hit table's default effect (role impact) at the hit, facing out of the surface
    sealed class TurretShot
    {
        public Vector3 Spawn;
        public int Age;      // 60 Hz frames (the B1 tiles' clock)
    }

    readonly Dictionary<BeamProjectileEntity, TurretShot> _turretShots = new();
    readonly Dictionary<BeamProjectileEntity, int> _turretShotHits = new();
    readonly Random _turretShotRandom = new();
    Func<WeaponInfo, bool>? _turretShotWeapon;

    void TickTurretShots()
    {
        _turretShotWeapon ??= EchoesTurret.IsShotWeapon;
        bool own = _turretFx != null && (_turretFx.Roles.ContainsKey("shot1") || _turretFx.Roles.ContainsKey("shot2"));
        if (own) BeamProjectileEntity.HostOwnLookWeapon = _turretShotWeapon;
        else if (BeamProjectileEntity.HostOwnLookWeapon == _turretShotWeapon) BeamProjectileEntity.HostOwnLookWeapon = null;
        if (!own || EchoesTurretEntity.ShotPool is not { } pool)
        {
            _turretShots.Clear();
            return;
        }
        foreach (BeamProjectileEntity b in pool)
        {
            if (b == null) continue;
            int seen = _turretShotHits.TryGetValue(b, out int h) ? h : b.HostHits;
            if (b.HostHits != seen && b.HostWeaponLook && !b.HostHitExpired) TurretImpact(b.HostHitAt, b.HostHitNormal, b.Direction);
            _turretShotHits[b] = b.HostHits;
            bool alive = b.HostWeaponLook && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided);
            _turretShots.TryGetValue(b, out TurretShot? shot);
            if (!alive || shot != null && shot.Spawn != b.SpawnPosition)
            {
                _turretShots.Remove(b);
                shot = null;
            }
            if (!alive) continue;
            if (shot == null) _turretShots[b] = shot = new TurretShot { Spawn = b.SpawnPosition };
            else shot.Age++;
        }
    }

    // the hit table's default effect where a shot hit: its Y out of the surface (back along the shot without one), at
    // the import's scale (Echoes metres to MPH units)
    void TurretImpact(Vector3 at, Vector3 normal, Vector3 dir)
    {
        if (_turretFx is not { } lib || !lib.Roles.TryGetValue("impact", out TurretFx? f) || lib.Parts.Get(f.Id) == null) return;
        Vector3 y = normal.LengthSquared > 1e-6f ? normal.Normalized() : dir.LengthSquared > 1e-6f ? -dir.Normalized() : Vector3.UnitY;
        Vector3 x = Vector3.Cross(y, MathF.Abs(y.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
        Vector3 z = Vector3.Cross(x, y);
        float s = EchoesArena.DefaultScale;
        var m = new Matrix4(new Vector4(x * s, 0), new Vector4(y * s, 0), new Vector4(z * s, 0), new Vector4(at, 1));
        _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, f.Id, m, follow: true, seed: Environment.TickCount ^ (int)f.Id) });
    }

    // DrawTurretFx: each live shot's billboards (B1: its tile now; B2: a random size a frame), facing the camera
    void DrawTurretShots(Matrix4 vp, Vector3 right, Vector3 up)
    {
        if (_turretFx is not { } lib || _turretShots.Count == 0) return;
        float scale = EchoesArena.DefaultScale;
        foreach (string role in new[] { "shot2", "shot1" })
        {
            if (!lib.Roles.TryGetValue(role, out TurretFx? f)) continue;
            var col = f.Color is { Length: 4 } c ? new Vector4(c[0], c[1], c[2], c[3]) : Vector4.One;
            int n = 0;
            foreach ((BeamProjectileEntity b, TurretShot shot) in _turretShots)
            {
                float size = f.Size is { Length: 2 } sz ? sz[0] + (float)_turretShotRandom.NextDouble() * (sz[1] - sz[0]) : 1;
                float half = size * 0.5f * scale;
                Vector4 uv = TurretTile(lib, f, shot.Age);
                Vector3 at = b.Position, r = right * half, u = up * half;
                Quad(ref n, vp, at - r + u, at + r + u, at - r - u, at + r - u, col, uv: uv);
            }
            DrawRun(f.Id, f.Additive, n);
        }
    }

    // ATEX (CUVEAnimTexture): the tile a shot shows at its age (RetroParticles' Flipbook: from the bottom-left tile, along
    // the row and up), else the whole texture
    static Vector4 TurretTile(TurretFxLib lib, TurretFx f, int age)
    {
        if (f.Tiles is not { Length: 6 } t || lib.Parts.TextureSize(f.Id) is not (int w, int h) || w <= 0 || h <= 0) return default;
        int sw = Math.Max(1, t[2]), sh = Math.Max(1, t[3]), cycle = Math.Max(1, t[4]);
        int tilesX = Math.Max(1, w / sw), tilesY = Math.Max(1, h / sh), tiles = tilesX * tilesY;
        int fr = t[5] != 0 ? age % cycle : Math.Min(age, cycle - 1);
        int tile = Math.Clamp(fr * tiles / cycle, 0, tiles - 1);
        int x = tile % tilesX, y = tilesY - 1 - tile / tilesX;
        float u0 = x * sw / (float)w, v0 = y * sh / (float)h;
        return new Vector4(u0, v0, u0 + t[0] / (float)w, v0 + t[1] / (float)h);
    }

    // a replay restarting its match (ResetDroneFx, which empties _fxLive)
    void ResetTurretFx()
    {
        StopTurretFx();
        _grateBits.Clear();
        _grateBreaks.Clear();
        _turretFxRoom = -1;
    }

    // the arena's turret effects (hd/turret.json's Fx, hd/turret_fx/), null without them
    TurretFxLib? LoadTurretFx()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        if (EchoesTurret.LoadLook(dir) is not { Fx: { Count: > 0 } list } look) return null;
        string fxDir = Path.Combine(dir, EchoesTurret.FxDir);
        if (!Directory.Exists(fxDir)) return null;
        var lib = new TurretFxLib { Look = look, Dir = fxDir };
        try
        {
            lib.Parts = RetroParticles.Load(fxDir, echoes: true);
            foreach (string f in Directory.GetFiles(fxDir, "*.elsc"))
            {
                if (UInt32.TryParse(Path.GetFileNameWithoutExtension(f), System.Globalization.NumberStyles.HexNumber, null, out uint id)) lib.Elscs[id] = Elsc.Load(File.ReadAllBytes(f));
            }
        }
        catch (Exception x)
        {
            Log.Warn("MPHArena", $"Shooting Gallery turret: effects not read ({x.Message})");
            return null;
        }
        foreach (string png in Directory.GetFiles(fxDir, "*.png"))
        {
            if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) continue;
            _fxTexFile.TryAdd(t, png);
            _fxTexRepeat.Add(t); // (repeating, as Retro samples them)
        }
        foreach (TurretFx f in list) lib.Roles.TryAdd(f.Role, f);
        Log.Info("MPHArena", $"Shooting Gallery turret: {list.Count} Echoes effects ({string.Join(", ", lib.Roles.Keys)}), {lib.Parts.Count} systems, {lib.Elscs.Count} arcs");
        return lib;
    }

    // the turret effects' model particles (PMDL: the lift/lower rings, the smoke's corner pieces): their models are placed
    // in the arena's HD look at their own passes (TurretLook.ModelPasses), drawn with the arena's props (DrawCrates). The
    // placed models are in our axes ((x, z, -y) x the arena scale, the importer's Map); a particle's transform works in
    // Echoes' own, so the vertices go back first. All of Shooting Gallery's are additive (PMAB): coloured by PMCL (the
    // brightness recolour), faded by its alpha.
    static readonly Matrix4 MphToEchoes = new Matrix4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1)
        * Matrix4.CreateScale(1 / EchoesArena.DefaultScale);
    readonly List<RetroModelParticle> _turretModels = new();

    // (the grates' bits with the crates; the particles into their own list, drawn after the blended pass)
    void AddTurretModels(List<(Matrix4 Model, bool Flash, CrateLight? Light, int Pass, float Fade, Vector3? Tint)> crates, List<(Matrix4 Model, bool Flash, CrateLight? Light, int Pass, float Fade, Vector3? Tint)> props)
    {
        if (_turretFx is not { } lib || lib.Look.ModelPasses.Count == 0) return;
        AddGrateBits(lib, crates);
        _turretModels.Clear();
        foreach (DroneFxLive f in _fxLive) f.Sys.CollectModels(_turretModels);
        foreach (RetroModelParticle m in _turretModels)
        {
            if (!lib.Look.ModelPasses.TryGetValue(m.Model, out int pass) || !m.Additive || m.Color.W <= 0.002f) continue;
            props.Add((MphToEchoes * m.Transform, false, null, pass, Math.Clamp(m.Color.W, 0, 1), m.Color.Xyz));
        }
    }

    // the effects' PMUS squares (RetroModelParticle with no model: the impact's big flash, 08dd96ac's 8d483388): each a
    // textured square in its particle's XZ plane, as Metaforce's RenderModels draws them (corner (0.5, 0, 0.5) at the
    // tile's (u1, v1)); additive ones add (src alpha, one), the rest blend; no depth writes, both faces
    readonly List<RetroModelParticle> _turretSquares = new();

    // (pulled toward the camera a little: the column's rings are born 0.01 Echoes units over the floor, 0e1ef708's POFS
    // with the lift effect on the floor, and fought it, flickering round the base where Echoes' glow holds steady; owner 10-16)
    void DrawTurretSquares(Matrix4 vp)
    {
        if (_turretSquares.Count == 0) return;
        GLES30.GlEnable(GLES30.GlPolygonOffsetFill);
        GLES30.GlPolygonOffset(-1, -4);
        foreach (RetroModelParticle m in _turretSquares)
        {
            int n = 0;
            Vector3 C(float x, float z) => Vector3.TransformPosition(new Vector3(x, 0, z), m.Transform);
            Quad(ref n, vp, C(-0.5f, -0.5f), C(0.5f, -0.5f), C(-0.5f, 0.5f), C(0.5f, 0.5f), m.Color, uv: m.Uv);
            DrawRun(m.Texture, m.Additive, n);
        }
        GLES30.GlPolygonOffset(0, 0);
        GLES30.GlDisable(GLES30.GlPolygonOffsetFill);
    }

    // DrawSkins (MatchSkins.cs), additive pass, after DrawDroneFx: the slot's arcs' lines (as DrawHyperFx draws them), the shots
    void DrawTurretFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene) return;
        _turretLines.Clear();
        _turretHeldArc?.Elec.Collect(_turretLines);
        _turretBombArc?.Elec.Collect(_turretLines);
        _turretSquares.Clear();
        if (_turretFx != null) foreach (DroneFxLive f in _fxLive) f.Sys.CollectModels(_turretSquares);
        _turretSquares.RemoveAll(m => m.Model != 0 || m.Color.W <= 0.002f);
        if (_turretLines.Count == 0 && _turretShots.Count == 0 && _turretSquares.Count == 0 || !FxGl()) return;
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
        _turretLines.Sort((a, b) => a.Texture.CompareTo(b.Texture));
        for (int li = 0; li < _turretLines.Count;)
        {
            uint tex = _turretLines[li].Texture;
            int n = 0;
            for (; li < _turretLines.Count && _turretLines[li].Texture == tex; li++)
            {
                RetroElectric.Line line = _turretLines[li];
                // Echoes weights a line by its LCLn alpha (CParticleElectric::RenderLines: KONST colour and alpha, SrcAlpha /
                // One; the slot's alphas run 0.01-0.4, looping), unlike Corruption's lines (alpha only a > 0 test, HyperFx)
                var col = new Vector4(line.Color.Xyz, Math.Clamp(line.Color.W, 0, 1));
                Vector3[] pts = line.Points;
                float du = 1f / Math.Max(1, pts.Length - 1);
                Vector3 Side(int k)
                {
                    Vector3 at = pts[k], dir = pts[Math.Min(k + 1, pts.Length - 1)] - pts[Math.Max(k - 1, 0)];
                    Vector3 sd = Vector3.Cross(dir, cam - at);
                    float half = line.WidthPx * 0.5f * pxToWorld * (cam - at).Length;
                    return (sd.LengthSquared > 1e-10f ? sd.Normalized() : right) * half;
                }
                Vector3 sa = Side(0);
                for (int k = 0; k + 1 < pts.Length; k++)
                {
                    Vector3 a = pts[k], b = pts[k + 1], sb = Side(k + 1);
                    float u0 = k * du, u1 = (k + 1) * du;
                    if ((n + 6) * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, _fxVerts.Length * 2);
                    Vert(ref n, vp, a + sa, (u0, u0), col); Vert(ref n, vp, b + sb, (u1, u1), col); Vert(ref n, vp, a - sa, (u0, u0), col);
                    Vert(ref n, vp, a - sa, (u0, u0), col); Vert(ref n, vp, b + sb, (u1, u1), col); Vert(ref n, vp, b - sb, (u1, u1), col);
                    sa = sb;
                }
            }
            if (n > 0) DrawRun(tex, additive: true, n, white: tex == 0 || FxTexture(tex) is not int id || id == 0);
        }
        DrawTurretShots(vp, right, new Vector3(view.M12, view.M22, view.M32));
        DrawTurretSquares(vp);
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    // ---- the grates' break (turret.json Fx role grate, kind debris: EchoesTurret.GrateDebris) ----
    // Echoes' grating dies into its generator (GENR 'Generator 003'), which makes one of each DebrisExtended at the
    // grating (CScriptGenerator: no FOLW, so the sender; its own turn kept) and sends each Activate. CScriptDebris
    // (extended; PrimeDecomp CScriptDebris.cpp) then: a speed in a cone about its direction (debris_cone, object-relative)
    // and a spin about a random axis (a 360 cone; mass 1, so rad/s), a life between its two, a constant pull down (its
    // gravity), no collision (IsCollider off), its colour faded in over the first share and out from FadeOutStart
    // (PreRender: alpha-blended, depth written only while whole), its model drawn while alive, and its death particle
    // started where it is as its life ends. Shooting Gallery's 8: 7 grate chunks for 2-3 s and one 0.1 s piece whose
    // death particle (741b9b1d) is the big white-pink puff. MPH's own break isn't drawn or heard here
    // (EchoesBreakables.HostOwnGrateBreak); its sound is the generator's PLAY (TurretSfx.cs, role grate).
    sealed class GrateBit
    {
        public TurretFx F = null!;
        public Matrix4 Turn;            // its own turn into our axes (At)
        public Vector3 Pos, Vel, Axis;  // MPH units, units/s; the spin's axis
        public float Spin, Angle, Life, Age;
        public CrateLight? Light;
    }

    readonly List<GrateBit> _grateBits = new();
    readonly Dictionary<EchoesBreakableEntity, int> _grateBreaks = new();
    readonly Random _grateRandom = new();

    void TickGrateFx()
    {
        TurretFxLib? lib = _turretFx;
        bool own = lib?.Look.Fx?.Any(f => f.Kind == "debris" && f.Debris is { Length: >= EchoesTurret.DebrisLength }) == true;
        EchoesBreakables.HostOwnGrateBreak = own;
        const float dt = 1 / 60f;
        float scale = EchoesArena.DefaultScale;
        for (int i = _grateBits.Count - 1; i >= 0; i--)
        {
            GrateBit b = _grateBits[i];
            // (CPhysicsActor moves it, then Think counts its time: done at the end of its life)
            b.Vel.Y -= b.F.Debris![EchoesTurret.DebrisGravity] * scale * dt;
            b.Pos += b.Vel * dt;
            b.Angle += b.Spin * dt;
            b.Age += dt;
            if (b.Age < b.Life) continue;
            _grateBits.RemoveAt(i);
            if (lib != null && b.F.Death != 0 && lib.Parts.Get(b.F.Death) != null)
            {
                // not oriented: Echoes' own axes at its place, at the import's scale
                var m = new Matrix4(scale, 0, 0, 0, 0, 0, -scale, 0, 0, scale, 0, 0, b.Pos.X, b.Pos.Y, b.Pos.Z, 1);
                _fxLive.Add(new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, b.F.Death, m, follow: true, seed: _grateRandom.Next(1, Int32.MaxValue)) });
            }
        }
        if (!own || _host == null)
        {
            _grateBreaks.Clear();
            return;
        }
        foreach (EntityBase e in _host.Scene.Entities)
        {
            if (e is not EchoesBreakableEntity { Grate: true } g) continue;
            int seen = _grateBreaks.TryGetValue(g, out int n) ? n : g.Breaks;
            if (g.Breaks > seen) BreakGrate(lib!, g);
            _grateBreaks[g] = g.Breaks;
        }
    }

    void BreakGrate(TurretFxLib lib, EchoesBreakableEntity g)
    {
        float scale = EchoesArena.DefaultScale;
        float R(float lo, float hi) => lo + (float)_grateRandom.NextDouble() * (hi - lo);
        // debris_cone: a length between the two, within half the cone's angle of +Z (the side's cosine uniform)
        Vector3 Cone(float cone, float lo, float hi)
        {
            float mag = R(lo, hi);
            float side = 1 - (1 - MathF.Cos(MathF.PI / 360 * cone)) * (float)_grateRandom.NextDouble();
            float hyp = mag * MathF.Sqrt(MathF.Max(0, 1 - side * side));
            float a = MathF.Tau * (float)_grateRandom.NextDouble();
            return new Vector3(hyp * MathF.Cos(a), hyp * MathF.Sin(a), mag * side);
        }
        // the generator's picks (TurretLook.GrateCount of GrateSlots followers, repeats allowed), else one of each piece
        List<TurretFx> pieces = lib.Look.Fx!.Where(f => f.Kind == "debris").ToList();
        IEnumerable<TurretFx> made = pieces;
        if (lib.Look.GrateCount > 0)
        {
            int slots = Math.Max(lib.Look.GrateSlots, pieces.Count);
            made = Enumerable.Range(0, lib.Look.GrateCount).Select(_ => _grateRandom.Next(slots)).Where(i => i < pieces.Count).Select(i => pieces[i]).ToList();
        }
        foreach (TurretFx f in made)
        {
            if (f.Debris is not { Length: >= EchoesTurret.DebrisLength } d || f.At is not { Length: 12 } at) continue;
            Matrix4 turn = M(at);
            // the cone about its own direction (debris_cone's +Z turned onto it), in its own axes, then ours
            var dir = new Vector3(d[EchoesTurret.DebrisDir], d[EchoesTurret.DebrisDir + 1], d[EchoesTurret.DebrisDir + 2]);
            dir = dir.LengthSquared > 1e-8f ? dir.Normalized() : Vector3.UnitZ;
            Vector3 c = Cone(d[EchoesTurret.DebrisCone], d[EchoesTurret.DebrisSpeed], d[EchoesTurret.DebrisSpeed + 1]);
            Vector3 u = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX).Normalized(), w = Vector3.Cross(dir, u);
            Vector3 local = u * c.X + w * c.Y + dir * c.Z;
            // (ApplyImpulseOR: its turn only, not its scale; the puff's carrier is drawn at 0.1)
            Matrix4 rot = turn;
            rot.Row0 = new Vector4(rot.Row0.Xyz.Normalized(), 0);
            rot.Row1 = new Vector4(rot.Row1.Xyz.Normalized(), 0);
            rot.Row2 = new Vector4(rot.Row2.Xyz.Normalized(), 0);
            Vector3 vel = Vector3.TransformVector(local, rot) * scale;
            Vector3 spin = Cone(360, d[EchoesTurret.DebrisSpin], d[EchoesTurret.DebrisSpin + 1]);
            _grateBits.Add(new GrateBit
            {
                F = f, Turn = turn, Pos = g.Position, Vel = vel,
                Axis = spin.LengthSquared > 1e-8f ? spin.Normalized() : Vector3.UnitY, Spin = spin.Length,
                Life = Math.Max(1 / 60f, R(d[EchoesTurret.DebrisLife], d[EchoesTurret.DebrisLife + 1])), Light = g.Light,
            });
        }
    }

    // AddTurretModels: each live piece's model, turned, spun, scaled out and faded as PreRender colours it
    void AddGrateBits(TurretFxLib lib, List<(Matrix4 Model, bool Flash, CrateLight? Light, int Pass, float Fade, Vector3? Tint)> props)
    {
        foreach (GrateBit b in _grateBits)
        {
            if (!lib.Look.ModelPasses.TryGetValue(b.F.Id, out int pass)) continue;
            float[] d = b.F.Debris!;
            float t = b.Age / b.Life, fin = d[EchoesTurret.DebrisFadeIn], fout = d[EchoesTurret.DebrisFadeOut];
            float k = t < fin ? (fin > 0 ? 1 - b.Age / (b.Life * fin) : 0) : t > fout ? (b.Age - b.Life * fout) / (b.Life * (1 - fout)) : 0;
            float alpha = 1 + (d[EchoesTurret.DebrisEndColor + 3] - 1) * Math.Clamp(k, 0, 1);
            if (alpha <= 0.002f) continue;
            float so = d[EchoesTurret.DebrisScaleOut];
            float st = t > so && so < 1 ? (b.Age - b.Life * so) / (b.Life * (1 - so)) : 0;
            var end = new Vector3(d[EchoesTurret.DebrisEndScale], d[EchoesTurret.DebrisEndScale + 1], d[EchoesTurret.DebrisEndScale + 2]);
            Vector3 sc = Vector3.Lerp(Vector3.One, end, Math.Clamp(st, 0, 1));
            Matrix4 m = Matrix4.CreateScale(sc) * b.Turn * Matrix4.CreateFromAxisAngle(b.Axis, b.Angle) * Matrix4.CreateTranslation(b.Pos);
            // (whole: drawn as placed; fading: blended at its alpha, depth not written)
            props.Add((m, false, b.Light, pass, alpha >= 0.998f ? 1 : -alpha, null));
        }
    }
}
