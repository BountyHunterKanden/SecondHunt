using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Import;
using MphRecomp.Import.BrawlFx;
using OpenTK.Mathematics;
using N = System.Numerics;

namespace MphRecomp.App;

// The Paralyzer in Brawl's look (owner 2026-10-12: "whatever path gets us to a convincing 'same look' effect and beams";
// docs/BRAWL_PARALYZER.md B4), every seat's, when the user's own Brawl import has it (recipes/brawl/Paralyzer.json ->
// brawl/Paralyzer: Zero Suit's effect archive + the common textures it names). The effects run in Core
// Import/BrawlFx/BrawlFxPlayer.cs from the archive's data:
//   tap shot      PtcSzerosuitParalyzerBulletS (its trail) + the bullet model (effect list model file 1)
//   charged shot  PtcSzerosuitParalyzerBulletL (+ rings and sparks) + its model (file 2)
//   fired         PtcSzerosuitParalyzerShot (at most every ParalyzerShotEvery frames) + PtcSzerosuitMuzzleFlash at the cannon
//   charging      PtcSzerosuitParalyzerHold at the cannon, again every ParalyzerHoldEvery frames
//   hit           PtcSzerosuitParalyzerHit where a shot lands
// MPH's look for those (the recoloured Power Beam: trail, muzzle flash, charge glow, hit) is hidden (MphRead
// PlayerEntity.HostOwnParalyzerLook -> BeamProjectileEntity.HostParalyzerLook / HostOwnLook); MPH's sounds are kept. Drawing
// only: the shots, their speed, damage, stun and hits are the simulation's, the same with or without the import. Each
// shot is drawn on the Pirate Blaster's path from the cannon tip (CampaignRenderer.BlasterFx.cs BoltPath / BoltDrawn).
internal sealed partial class CampaignRenderer
{
    const string ParalyzerFxItem = "Paralyzer";
    // Brawl units -> world units: Brawl Zero Suit's own body scale (bodies/SSBBZeroSuit: 19.43 tall -> 1.69), so the effects
    // are her size as in Brawl (owner 10-15: "really busy and big"; was 0.1)
    const float ParalyzerFxScale = 0.087f;
    const int ParalyzerShotEvery = 6;
    const int ParalyzerHoldEvery = 6;
    // a hit's effect at this size (owner 10-15: "about half as small"; was 1)
    const float ParalyzerHitScale = 0.5f;
    // the main player's own first-person view: the muzzle effects at this size (they sit right in front of the camera)
    const float ParalyzerOwnMuzzle = 0.5f;
    const int ParalyzerFxMax = 96;
    // the cannon's effects are depth-tested this much nearer the camera (world units, about her forearm), drawn where they
    // are: Brawl's muzzle flash is a 2 x 5 quad centred on the tip, so its back half lies in her hand and arm, and seen
    // from above (the quad flat) her body cut it off in a straight edge (owner 10-15: "part of the muzzle flash kind of get
    // dropped into a geometric shape like it's being blocked from fully extending"). Brawl's side view never shows that
    const float ParalyzerMuzzlePull = 0.4f;

    sealed class ParalyzerShot
    {
        public BlasterBolt Path = null!;   // the drawn path (BlasterFx's)
        public BrawlFxSystem Fx = null!;
        public BrawlFxModel? Model;
        public int Frame;
    }

    sealed class ParalyzerSeat
    {
        public int Frame, LastShot = -1000, LastHold = -1000;
        public readonly List<BrawlFxSystem> Muzzle = new(); // riding the cannon tip
    }

    bool _paraRead;
    BrawlFxLibrary? _paraLib;
    int _paraSeed = 0x2A2A;
    readonly Dictionary<BeamProjectileEntity, ParalyzerShot> _paraShots = new();
    readonly List<BeamProjectileEntity> _paraGone = new();
    readonly List<BrawlFxSystem> _paraFx = new();   // placed once: hits, ended shots' trails
    readonly Dictionary<PlayerEntity, ParalyzerSeat> _paraSeats = new();
    readonly List<(BeamProjectileEntity Beam, CollisionResult Col, bool Expired)> _paraHits = new();
    Action<BeamProjectileEntity, CollisionResult, bool>? _paraImpact, _paraPrevImpact;
    readonly List<BrawlFxDraw> _paraDraws = new();
    readonly Dictionary<BrawlFxDraw, int> _paraMuzzleVerts = new(); // per draw: its first n vertices are the cannon's
    readonly Dictionary<string, int> _paraTex = new();
    float[] _paraVerts = new float[13 * 1024];
    int _paraProg, _paraVao, _paraVbo, _paraWhite;

    // brawl/Paralyzer, read once the imports' folder is known; false without it (or in a public build without its import)
    bool ParalyzerFxReady()
    {
        if (_paraRead) return _paraLib != null;
        if (BodiesRoot is not string bodies) return false;
        _paraRead = true;
        string root = Path.GetDirectoryName(bodies) ?? "";
        if (!ImportedContent.Usable(root, BrawlFxImportKind.ImportKind, ParalyzerFxItem))
        {
            // (10-15: why the look is off, e.g. on a device)
            bool dir = Directory.Exists(Path.Combine(root, BrawlFxImportKind.ImportKind, ParalyzerFxItem));
            Log.Info("MPHMatch", $"Paralyzer: no Brawl look, MPH's kept ({(dir ? "import record stale or missing" : "no " + BrawlFxImportKind.ImportKind + "/" + ParalyzerFxItem)} under {root})");
            return false;
        }
        try
        {
            _paraLib = BrawlFxLibrary.Load(Path.Combine(root, BrawlFxImportKind.ImportKind, ParalyzerFxItem));
            Log.Info("MPHMatch", $"Paralyzer: Brawl look: {_paraLib.Effects.Effects.Count} effects, {_paraLib.Models.Count} models, {_paraLib.Textures.Count} textures");
            LoadParalyzerSounds(root); // (her Brawl sounds: CampaignRenderer.ParalyzerSfx.cs)
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMatch", $"Paralyzer: the Brawl look failed to load, MPH's kept: {ex.Message}");
            _paraLib = null;
        }
        return _paraLib != null;
    }

    // TickSkins (MatchSkins.cs), after the drone's and the blaster's (they set HostImpact each tick): one 60 Hz frame on
    void TickParalyzerFx()
    {
        bool on = ParalyzerFxReady();
        PlayerEntity.HostOwnParalyzerLook = on;
        PlayerEntity.HostOwnParalyzerSfx = PlayerEntity.HostOwnParalyzerHitSfx = on && ParalyzerSfxOn;
        DevParalyzerHands(); // (10-15: before the look's check, so a dev run gets the beam without the import too)
        if (!on) return;
        BrawlFxLibrary lib = _paraLib!;
        // our hits first, the rest to whoever set HostImpact (the drone / the blaster)
        _paraImpact ??= OnParalyzerHit;
        if (BeamProjectileEntity.HostImpact != _paraImpact)
        {
            _paraPrevImpact = BeamProjectileEntity.HostImpact;
            BeamProjectileEntity.HostImpact = _paraImpact;
        }
        foreach ((BeamProjectileEntity beam, CollisionResult col, bool expired) in _paraHits)
        {
            if (_paraShots.TryGetValue(beam, out ParalyzerShot? hit)) hit.Path.Pos = col.Position;
            if (expired || _paraFx.Count >= ParalyzerFxMax) continue;
            Vector3 n = col.Plane.Xyz.LengthSquared > 1e-6f ? col.Plane.Xyz.Normalized() : -beam.Direction;
            _paraFx.Add(new BrawlFxSystem(lib, "PtcSzerosuitParalyzerHit", ParalyzerFrame(n, col.Position + n * 0.05f, ParalyzerHitScale), _paraSeed++));
        }
        _paraHits.Clear();
        TickParalyzerSfx();
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            if (!_paraSeats.TryGetValue(p, out ParalyzerSeat? seat)) _paraSeats[p] = seat = new ParalyzerSeat();
            seat.Frame++;
            Vector3? tip = BlasterTip(p);
            float own = p.IsMainPlayer && !p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson) ? ParalyzerOwnMuzzle : 1;
            Vector3 aim = p.NetAimVector.LengthSquared > 1e-6f ? p.NetAimVector.Normalized() : Vector3.UnitZ;
            // first person: on her gun's muzzle as drawn (ZeroSuitGunTip.cs)
            if (FirstPersonGunTip(p, out Vector3 fpTip, out Vector3 barrel))
            {
                tip = fpTip;
                aim = barrel;
            }
            // charging: the hold glow at the cannon, again every few frames
            if (tip is Vector3 hold && p.Health > 0 && p.CurrentWeapon == BeamType.Paralyzer
                && p.EquipInfo.ChargeLevel >= p.EquipInfo.Weapon.MinCharge * 2 && seat.Frame - seat.LastHold >= ParalyzerHoldEvery)
            {
                seat.LastHold = seat.Frame;
                seat.Muzzle.Add(new BrawlFxSystem(lib, "PtcSzerosuitParalyzerHold", ParalyzerFrame(aim, hold, own), _paraSeed++) { FollowAll = true });
            }
            // the shots: new ones start (with the cannon's flashes), gone ones leave their trail to run out
            if (p.EquipInfo.Beams is BeamProjectileEntity[] beams)
            {
                foreach (BeamProjectileEntity b in beams)
                {
                    bool alive = b.HostParalyzerLook && b.Lifespan > 0 && !b.Flags.TestFlag(BeamFlags.Collided);
                    _paraShots.TryGetValue(b, out ParalyzerShot? shot);
                    if (shot != null && (!alive || b.SpawnPosition != shot.Path.Spawn || b.Age < shot.Path.Age))
                    {
                        EndParalyzerShot(b, shot);
                        shot = null;
                    }
                    if (!alive) continue;
                    if (shot == null)
                    {
                        _paraShots[b] = shot = StartParalyzerShot(b, lib);
                        if (tip is Vector3 at && seat.Muzzle.Count < 16)
                        {
                            bool charged = b.Flags.TestFlag(BeamFlags.Charged);
                            if (charged || seat.Frame - seat.LastShot >= ParalyzerShotEvery)
                            {
                                seat.LastShot = seat.Frame;
                                seat.Muzzle.Add(new BrawlFxSystem(lib, "PtcSzerosuitParalyzerShot", ParalyzerFrame(shot.Path.Dir, at, own), _paraSeed++) { FollowAll = true });
                            }
                            seat.Muzzle.Add(new BrawlFxSystem(lib, "PtcSzerosuitMuzzleFlash", ParalyzerFrame(shot.Path.Dir, at, own), _paraSeed++) { FollowAll = true });
                        }
                    }
                    shot.Path.Age = b.Age;
                    shot.Path.Pos = BoltDrawn(shot.Path, b);
                    shot.Fx.Transform = ParalyzerFrame(shot.Path.Dir, shot.Path.Pos, BoltScale(shot.Path));
                    shot.Fx.Step();
                    shot.Frame++;
                }
            }
            // the cannon's effects ride the tip, turning with the aim, their particles too (FollowAll: owner 10-15 "can the
            // muzzle flash follow the gun when moving"; before, only new particles started at the tip)
            for (int i = seat.Muzzle.Count - 1; i >= 0; i--)
            {
                BrawlFxSystem fx = seat.Muzzle[i];
                if (tip is Vector3 at) fx.Transform = ParalyzerFrame(aim, at, own);
                fx.Step();
                if (fx.Done || fx.Age > 600) seat.Muzzle.RemoveAt(i);
            }
        }
        // shots whose pool slot went quiet without a tick seeing them end (their seat gone)
        _paraGone.Clear();
        foreach ((BeamProjectileEntity b, ParalyzerShot shot) in _paraShots)
        {
            if (!b.HostParalyzerLook || b.Lifespan <= 0) _paraGone.Add(b);
        }
        foreach (BeamProjectileEntity b in _paraGone) EndParalyzerShot(b, _paraShots[b]);
        for (int i = _paraFx.Count - 1; i >= 0; i--)
        {
            BrawlFxSystem fx = _paraFx[i];
            fx.Step();
            if (fx.Done || fx.Age > 600) _paraFx.RemoveAt(i);
        }
    }

    // dev only (PC pictures: SecondHunt.exe with MPH_DEV_PARALYZER=1, never set on a device, ignored in public builds):
    // every seat is handed the Paralyzer once
    readonly HashSet<PlayerEntity> _devParalyzerGiven = new();
    int _devParalyzerFrame;
    void DevParalyzerHands()
    {
        if (MphRecomp.Config.RecompSettings.PublicBuild || Environment.GetEnvironmentVariable("MPH_DEV_PARALYZER") != "1" && !_devParalyzerArg) return; // (+ a solo run's --es paralyzer 1: ZeroSuitGunTip.cs)
        // MPH_DEV_PARALYZER_FIRE=1: the pad's B held 1.5 s (a charged shot on release), then tapped (rapid shots)
        if (Environment.GetEnvironmentVariable("MPH_DEV_PARALYZER_FIRE") == "1" || _devParalyzerFire) // (+ --es parafire 1)
        {
            int f = _devParalyzerFrame++ % 240;
            Pad.Set(PadButton.B, f < 90 || f >= 120 && f % 6 < 3);
        }
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || p.Health <= 0 || !_devParalyzerGiven.Add(p)) continue;
            p.HostPickUpWeapon(ItemType.Paralyzer);
            Log.Info("MPHMatch", $"dev: seat {p.SlotIndex} handed the Paralyzer");
        }
    }

    ParalyzerShot StartParalyzerShot(BeamProjectileEntity b, BrawlFxLibrary lib)
    {
        Vector3 dir = b.Direction.LengthSquared > 1e-6f ? b.Direction.Normalized() : Vector3.UnitZ;
        var path = new BlasterBolt { Spawn = b.SpawnPosition, Age = b.Age, Dir = dir, SimDir = dir };
        path.Lead = MathF.Max(0, Vector3.Dot(b.Position - b.SpawnPosition, dir));
        BoltPath(path, b);
        path.Pos = BoltDrawn(path, b);
        path.First = path.Pos;
        path.HasFirst = true;
        path.OwnView = b.Owner is PlayerEntity { IsMainPlayer: true } op && !op.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
        string name = b.Flags.TestFlag(BeamFlags.Charged) ? "PtcSzerosuitParalyzerBulletL" : "PtcSzerosuitParalyzerBulletS";
        var fx = new BrawlFxSystem(lib, name, ParalyzerFrame(path.Dir, path.Pos, BoltScale(path)), _paraSeed++) { KeepChildren = true };
        return new ParalyzerShot { Path = path, Fx = fx, Model = lib.ModelOf(name) };
    }

    void EndParalyzerShot(BeamProjectileEntity b, ParalyzerShot shot)
    {
        _paraShots.Remove(b);
        shot.Fx.Stop();
        if (_paraFx.Count < ParalyzerFxMax) _paraFx.Add(shot.Fx);
    }

    // HostImpact: a Paralyzer shot's hit here, the rest to the previous handler
    void OnParalyzerHit(BeamProjectileEntity beam, CollisionResult col, bool expired)
    {
        if (!beam.HostParalyzerLook)
        {
            _paraPrevImpact?.Invoke(beam, col, expired);
            return;
        }
        if (_paraHits.Count < 64) _paraHits.Add((beam, col, expired));
    }

    // a replay restarting its match (ResetDroneFx)
    void ResetParalyzerFx()
    {
        _paraShots.Clear();
        _paraFx.Clear();
        _paraSeats.Clear();
        _paraHits.Clear();
        ResetParalyzerSfx();
    }

    // Brawl effect space (+Z along the shot, +Y up) at a point, in world units
    static N.Matrix4x4 ParalyzerFrame(Vector3 dir, Vector3 at, float scale)
    {
        Vector3 z = dir.LengthSquared > 1e-8f ? dir.Normalized() : Vector3.UnitZ;
        Vector3 refUp = MathF.Abs(z.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 y = (refUp - z * Vector3.Dot(refUp, z)).Normalized();
        Vector3 x = Vector3.Cross(y, z);
        float s = ParalyzerFxScale * scale;
        return new N.Matrix4x4(x.X * s, x.Y * s, x.Z * s, 0, y.X * s, y.Y * s, y.Z * s, 0, z.X * s, z.Y * s, z.Z * s, 0, at.X, at.Y, at.Z, 1);
    }

    static N.Vector3 Nv(Vector3 v) => new(v.X, v.Y, v.Z);

    // DrawSkins (MatchSkins.cs), additive pass: every Paralyzer effect and bullet
    void DrawParalyzerFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _paraLib == null) return;
        foreach (BrawlFxDraw d in _paraDraws) d.Verts.Clear();
        Matrix4 view = scene.ViewMatrix;
        N.Vector3 cam = Nv(scene.CameraPosition);
        N.Vector3 right = Nv(new Vector3(view.M11, view.M21, view.M31)), up = Nv(new Vector3(view.M12, view.M22, view.M32));
        // the cannon's effects first, so each draw's first vertices are theirs
        foreach (ParalyzerSeat seat in _paraSeats.Values)
        {
            foreach (BrawlFxSystem fx in seat.Muzzle) fx.Collect(_paraDraws, cam, right, up);
        }
        _paraMuzzleVerts.Clear();
        foreach (BrawlFxDraw d in _paraDraws) _paraMuzzleVerts[d] = d.Verts.Count;
        foreach (ParalyzerShot shot in _paraShots.Values)
        {
            shot.Fx.Collect(_paraDraws, cam, right, up);
            if (shot.Model != null) BrawlFxMeshes.Collect(shot.Model, shot.Frame, shot.Fx.Transform, cam, _paraDraws);
        }
        foreach (BrawlFxSystem fx in _paraFx) fx.Collect(_paraDraws, cam, right, up);
        bool any = false;
        foreach (BrawlFxDraw d in _paraDraws) any |= d.Verts.Count > 0;
        if (!any || !ParalyzerGl()) return;
        Matrix4 vp = view * scene.PerspectiveMatrix;
        GLES30.GlUseProgram(_paraProg);
        GLES30.GlBindVertexArray(_paraVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _paraVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_paraProg, "uTex"), 0);
        int ia = GLES30.GlGetUniformLocation(_paraProg, "uIa");
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        // alpha-blended runs first, then the additive ones
        for (int pass2 = 0; pass2 < 2; pass2++)
        {
            foreach (BrawlFxDraw d in _paraDraws)
            {
                if (d.Verts.Count == 0 || d.Additive != (pass2 == 1) || ParalyzerTexture(d.Texture) is not (int tex, bool intensity)) continue;
                if (d.Verts.Count * 13 > _paraVerts.Length) Array.Resize(ref _paraVerts, Math.Max(_paraVerts.Length * 2, d.Verts.Count * 13));
                int o = 0, muzzle = _paraMuzzleVerts.GetValueOrDefault(d);
                foreach (BrawlFxVertex v in d.Verts)
                {
                    Vector4 c = new Vector4(v.Pos.X, v.Pos.Y, v.Pos.Z, 1) * vp;
                    if (o < muzzle * 13 && c.W > 1e-4f)
                    {
                        // the depth of the point pulled toward the camera (at most half way), x / y / w as they were
                        var at = new Vector3(v.Pos.X, v.Pos.Y, v.Pos.Z);
                        Vector3 toCam = scene.CameraPosition - at;
                        float dist = toCam.Length;
                        Vector4 n = new Vector4(at + toCam * (MathF.Min(ParalyzerMuzzlePull, dist * 0.5f) / MathF.Max(dist, 1e-4f)), 1) * vp;
                        if (n.W > 1e-4f) c.Z = n.Z / n.W * c.W;
                    }
                    _paraVerts[o] = c.X; _paraVerts[o + 1] = c.Y; _paraVerts[o + 2] = c.Z; _paraVerts[o + 3] = c.W;
                    _paraVerts[o + 4] = v.Uv.X; _paraVerts[o + 5] = v.Uv.Y;
                    _paraVerts[o + 6] = v.Primary.X; _paraVerts[o + 7] = v.Primary.Y; _paraVerts[o + 8] = v.Primary.Z; _paraVerts[o + 9] = v.Primary.W;
                    _paraVerts[o + 10] = v.Secondary.X; _paraVerts[o + 11] = v.Secondary.Y; _paraVerts[o + 12] = v.Secondary.Z;
                    o += 13;
                }
                GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
                GLES30.GlUniform1f(ia, intensity ? 1 : 0);
                GLES30.GlBlendFunc(GLES30.GlSrcAlpha, d.Additive ? GLES30.GlOne : GLES30.GlOneMinusSrcAlpha);
                GLES30.GlBufferData(GLES30.GlArrayBuffer, d.Verts.Count * 13 * 4, _paraVerts, GLES30.GlDynamicDraw);
                GLES30.GlDrawArrays(GLES30.GlTriangles, 0, d.Verts.Count);
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

    // a texture by name (null: plain white), and whether its alpha is its intensity (I4 / I8)
    (int Id, bool Intensity)? ParalyzerTexture(string? name)
    {
        if (name == null) return (_paraWhite, false);
        if (_paraLib == null || !_paraLib.Textures.TryGetValue(name, out BrawlFxTexture? t)) return null;
        if (!_paraTex.TryGetValue(name, out int id))
        {
            var ids = new int[1];
            if (t.Rgba != null)
            {
                GLES30.GlGenTextures(1, ids, 0);
                GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
                GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, t.Width, t.Height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, t.Rgba);
            }
            else if (t.PngPath != null && AppPlatform.DecodeImage(t.PngPath) is (int[] px, int w, int h))
            {
                GLES30.GlGenTextures(1, ids, 0);
                GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
                GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
            }
            if (ids[0] != 0)
            {
                GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinearMipmapLinear);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            }
            _paraTex[name] = id = ids[0];
        }
        return id == 0 ? null : (id, t.IntensityAlpha);
    }

    // Brawl's particle colour (every effect here): rgb = lerp(secondary, primary, texture), alpha = primary alpha x texture
    // alpha (an intensity texture's alpha is its intensity)
    const string ParaVert = "#version 300 es\nlayout(location = 0) in vec4 aPos;\nlayout(location = 1) in vec2 aUv;\n"
        + "layout(location = 2) in vec4 aPri;\nlayout(location = 3) in vec3 aSec;\nout vec2 vUv;\nout vec4 vPri;\nout vec3 vSec;\n"
        + "void main() { gl_Position = aPos; vUv = aUv; vPri = aPri; vSec = aSec; }\n";
    const string ParaFrag = "#version 300 es\nprecision mediump float;\nuniform sampler2D uTex;\nuniform float uIa;\nin vec2 vUv;\n"
        + "in vec4 vPri;\nin vec3 vSec;\nout vec4 fragColor;\n"
        + "void main() { vec4 t = texture(uTex, vUv); if (uIa > 0.5) t.a = t.r;\n"
        + "  fragColor = vec4(mix(vSec, vPri.rgb, t.rgb), vPri.a * t.a); }\n";

    // the program, buffer and white texel, made again after a new GL context (it empties _u: a key of ours marks ours alive)
    bool ParalyzerGl()
    {
        if (!_u.ContainsKey("paralyzerfx"))
        {
            _paraProg = _paraVao = _paraVbo = _paraWhite = 0;
            _paraTex.Clear();
            _u["paralyzerfx"] = 1;
        }
        if (_paraProg == 0)
        {
            _paraProg = CampaignGlCache.Program(ParaVert, ParaFrag, "Brawl effects (Paralyzer)");
            if (_paraProg == 0) return false;
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _paraVao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _paraVbo = ids[0];
            GLES30.GlBindVertexArray(_paraVao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _paraVbo);
            int[] size = { 4, 2, 4, 3 }, off = { 0, 4, 6, 10 };
            for (int a = 0; a < 4; a++)
            {
                GLES30.GlEnableVertexAttribArray(a);
                GLES30.GlVertexAttribPointer(a, size[a], GLES30.GlFloat, false, 52, off[a] * 4);
            }
            GLES30.GlBindVertexArray(0);
            GLES30.GlGenTextures(1, ids, 0); _paraWhite = ids[0];
            GLES30.GlBindTexture(GLES30.GlTexture2d, _paraWhite);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, 1, 1, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, new[] { -1 });
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        }
        return true;
    }
}
