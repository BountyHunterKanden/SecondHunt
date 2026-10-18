using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The first-person arm cannon (owner 2026-10-08): when the main player's seat wears a native body with a cannon (the
// Troopers, Dark Samus: MatchSkins' bodies), that body's own cannon (Core BodyArmCannon, CampaignRetroBody.DrawCannon)
// replaces MPH's first-person gun -- WeavelGun for the Trooper, SamusGun and the campaign's Prime gun for Dark Samus --
// on the frames MPH draws its gun. MPH places it (the player's GunDrawTransform), its muzzle sits on MPH's muzzle point
// (where the sim spawns the shots and MphRead draws the muzzle flash, charge glow and smoke), it moves as MPH's own gun
// moves (Core MphGunMotion: recoil, charge-shot kick, the raise), at a size per body (BodyArmCannon.Size). Drawing only: the match, the
// aim and every shot are the same with or without it.
// ON by default (owner approved the cannons 2026-10-08 22:47); while paused (the gun-adjust pause, L3 + R3), D-pad
// left/right = MPH's gun / the cannon, up/down = its size.
//   --es armcannon 0   (dev) start with MPH's gun
//   --es armcannonsize <x> / armcannonpitch, armcannonyaw, armcannonroll <deg> / armcannonside, armcannonlift,
//   armcannonpush <world units, left/up/forward>   (dev) its framing, about the muzzle, for pictures
//   --es armcannonviews <dir>   (dev) once it's in place: the cut alone from six sides, <dir>/<body>_<side>.png
//   --es armcannonlight <x>   (dev) the cannon's light (BodyArmCannon.Light: Echoes Dark Samus 1.6, the rest 1)
//   --es armcannonblast <x,y,z>   (dev) where MPH's charge glow / muzzle flash / smoke go, cannon space at its
//   first-person size (BodyArmCannon.Blast: the Trooper's under the spike, by the hand); "off" = on the muzzle
// Third person (part B, owner OK 2026-10-09): MPH's charge glow / muzzle flash on each native body's own cannon tip (every
// seat wearing one), not on the hidden MPH model's shoot node. Draw only; the pause's D-pad left/right switches it with
// the cannon (MPH = both vanilla).
//   --es armcannon3p 0   (dev) the third-person glow / flash where MPH puts them
// The Troopers in third person: owner 2026-10-09 17:45, after the hold, the bent shots, the raise and the new pose ("this
// is bad too ... go back to the way it was before we tried any of these methods"): his arm as his own Space Pirate clips
// have it, the flash on its tip. Everything tried stays dev only:
//   --es armcannonhold 1   (dev) the cannon arm held on the aim (CampaignRetroBody.TrooperArmHold, as Dark Samus's D14)
// Shots drawn bent from the tip (ShiftCannonBeam): turned down ("the bend looks bad").
// On top of the hold (turned down 17:45; the first raise, elbow on the shot line + chest leaned back, clipped the arm
// through the body): his chest turned to bring the right shoulder in, the upper arm forward and up, the elbow bending
// only in its own plane, the forearm on the aim (CampaignRetroBody.TrooperArmRaise, AimArm).
//   --es armcannonhold 1 --es armcannonraise 1   (dev) that pose
//   --es armcannonlift / armcannonflare / armcannontwist <deg>   (dev) the upper arm's lift, its flare out, the chest turn
//   --es armcannonview <deg> [armcannonviewdist <units>]   (dev pictures) the view on the main player from that far
//   round from behind (90 = his right side), render only (Scene.HostView), one frame behind
//   --es armcannonbeam 1|all   (dev, owner turned it down) the Troopers' shots / every native body's drawn from its tip,
//   easing onto their real path within BeamProjectileEntity.HostDrawFade units (HostDrawShift); hits unchanged
//   --es armcannonmeasure 1   (dev, part B) each frame: the body's cannon tip vs the sim muzzle vs the third-person
//   shoot node, in the player's frame (left, up, forward from p.Position), to the log
internal sealed partial class CampaignRenderer
{
    bool _cannonOn = true, _cannonActive;
    float _cannonSize = 1; // times the cannon's own first-person size (BodyArmCannon.Size)
    Matrix4 _cannonFrame = Matrix4.Identity; // dev framing about the muzzle (BodyArmCannon.GunSpace)
    CampaignRetroBody? _cannonBody;
    Matrix4 _cannonModel;
    Vector4 _cannonTint2;
    Vector4 _cannonTint; // the seat's team / duplicate colour on its real body (MatchSkins.SeatTint), on the cannon too
    float _cannonAlpha;
    string _cannonGun = "", _cannonLogged = "";
    readonly Dictionary<Hunter, MphGunMotion?> _cannonMotion = new();
    string? _cannonViewsDir;
    Vector3? _cannonBlast; // dev: armcannonblast
    float _cannonLightDev; // dev: armcannonlight (0 = the cannon's own)
    bool _cannonBlastOff;
    int _cannonViewsWait = 30;
    bool _cannonMeasure, _cannon3p = true, _cannonHold, _cannonPoseNew;
    int _cannonMeasureFrame;
    float? _cannonViewDeg;
    float _cannonViewDist = 2.2f;
    string? _cannonBeam; // dev armcannonbeam: "1" = the Troopers' shots drawn from the tip, "all"
    int _cannonRenders; // dev: renders so far, for armcannonbeam's log

    void ConfigureArmCannon(LaunchArgs args)
    {
        BeamProjectileEntity.HostSpawned = ShiftCannonBeam;
        if (MphRecomp.Config.RecompSettings.PublicBuild) return;
        ConfigureBodyViews(args);
        ConfigureBeamPath(args);
        if (args.Get("armcannon") == "0") _cannonOn = false;
        _cannonViewsDir = args.Get("armcannonviews");
        _cannonMeasure = args.Get("armcannonmeasure") == "1";
        if (args.Get("armcannon3p") == "0") _cannon3p = false;
        if (F("armcannonviewdist") > 0) _cannonViewDist = F("armcannonviewdist");
        if (args.Get("armcannonhold") == "1") _cannonHold = true;
        if (args.Get("armcannonraise") == "1") _cannonPoseNew = true;
        if (args.Get("armcannontwist") != null) CampaignRetroBody.TrooperTwist = F("armcannontwist");
        if (args.Get("armcannonlift") != null) CampaignRetroBody.TrooperArmLift = F("armcannonlift");
        if (args.Get("armcannonflare") != null) CampaignRetroBody.TrooperArmFlare = F("armcannonflare");
        if (args.Get("armcannonbeam") is "1" or "all") _cannonBeam = args.Get("armcannonbeam");
        if (float.TryParse(args.Get("armcannonview"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float viewDeg)) _cannonViewDeg = viewDeg;
        string? blast = args.Get("armcannonblast");
        _cannonBlastOff = blast == "off";
        string[] xyz = blast?.Split(',') ?? Array.Empty<string>();
        if (xyz.Length == 3 && float.TryParse(xyz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bx)
            && float.TryParse(xyz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float by)
            && float.TryParse(xyz[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bz))
        {
            _cannonBlast = new Vector3(bx, by, bz);
        }
        float F(string key) => float.TryParse(args.Get(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0;
        if (F("armcannonsize") > 0) _cannonSize = F("armcannonsize");
        _cannonLightDev = F("armcannonlight");
        _cannonFrame = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(F("armcannonroll")))
            * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(F("armcannonpitch")))
            * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(F("armcannonyaw")))
            * Matrix4.CreateTranslation(F("armcannonside"), F("armcannonlift"), F("armcannonpush"));
    }

    // Render, once MphRead has built the frame's draw list: whether the cannon draws this frame (the Prime gun then
    // doesn't), and where
    bool UpdateArmCannon(Scene scene)
    {
        _cannonActive = false;
        _cannonRenders++;
        foreach (PlayerEntity q in PlayerEntity.Players) if (q != null) q.HostFxMuzzle = null;
        if (_cannonMeasure && _host != null) MeasureCannon(_host.Player);
        CannonFx3P();
        if (_cannonViewDeg is float deg && _host != null) CannonView(scene, _host.Player, deg);
        BodyViewsIfDue();
        // a replay watching another seat from its eyes (MatchReplay.cs, T9): that seat's cannon; and never the main
        // player's Prime gun there (true without a cannon = no Prime gun, MPH's own gun for that seat)
        PlayerEntity? seat = ReplayFirstPersonSeat;
        if (!_cannonOn || _host == null || ShipOpen) return seat != null;
        PlayerEntity p = seat ?? _host.Player;
        if (BodyOf(p) is not CampaignRetroBody body || body.Cannon is not BodyArmCannon c) return seat != null;
        string gun = Metadata.HunterModels[p.Hunter][3];
        if (!MphGunDrawn(scene, gun, out float alpha)) return seat != null;
        Matrix4 motion = Motion(p.Hunter)?.Delta(p) ?? Matrix4.Identity;
        float muzzle = Fixed.ToFloat(p.Values.MuzzleOffset);
        _cannonModel = c.GunSpace(c.Body.Scale * c.Size * _cannonSize, muzzle, motion, _cannonFrame) * p.GunDrawTransform;
        // MPH's charge glow, muzzle flash and smoke by the cannon's own barrel (from the next frame's process on)
        if (!_cannonBlastOff && (_cannonBlast ?? c.Blast) is Vector3 blast)
        {
            p.HostFxMuzzle = Vector3.TransformPosition(blast * _cannonSize, _cannonFrame) + new Vector3(0, 0, muzzle);
        }
        _cannonBody = body;
        _cannonTint = SeatTint(p);
        _cannonTint2 = SeatTint2(p); // (a two-tone copy's lights)
        _cannonAlpha = alpha;
        _cannonGun = gun;
        _cannonActive = true;
        if (_cannonLogged != body.Id)
        {
            _cannonLogged = body.Id;
            Log.Info("MPHCampaign", $"arm cannon: {body.Id} in place of {gun} ({c.Describe()}; motion {Motion(p.Hunter)?.NodeName ?? "none"})");
        }
        return true;
    }

    // third person: each seat's charge glow / muzzle flash on its body's cannon tip, in the seat's own frame (left, up,
    // forward of its position by its facing: PlayerDraw's HostFxShootOffset), so it rides the next frame's position
    void CannonFx3P()
    {
        CampaignRetroBody.TrooperArmHold = _cannonHold && _cannonOn;
        CampaignRetroBody.TrooperArmRaise = _cannonPoseNew;
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null) continue;
            p.HostFxShootOffset = null;
            if (HeldGunFx3P(p)) continue; // ZeroSuitGunTip.cs: Brawl Zero Suit flash on her held gun's muzzle
            if (!_cannon3p || !_cannonOn || BodyOf(p) is not CampaignRetroBody body || !body.CannonInWorld(p, out Vector3 tip, out _)) continue;
            var ahead = new Vector3(p.Field70, 0, p.Field74);
            if (ahead.LengthSquared < 1e-12f) continue;
            ahead.Normalize();
            Vector3 d = tip - p.Position;
            p.HostFxShootOffset = new Vector3(Vector3.Dot(d, Vector3.Cross(Vector3.UnitY, ahead)), d.Y, Vector3.Dot(d, ahead));
        }
    }

    // as each shot spawns (BeamProjectileEntity.HostSpawned): drawn from the seat's cannon tip (CannonFx3P's offset, the frame before),
    // not from the sim's muzzle; not the main player in first person (his cannon's tip is on the muzzle)
    void ShiftCannonBeam(BeamProjectileEntity beam)
    {
        WallCheckShot(beam); // dev (ArmCannonBeamPath.cs)
        if (_cannonBeam == null)
        {
            DrawShotFromArm(beam); // ArmCannonBeamPath.cs
            return;
        }
        if (!_cannonOn || !_cannon3p || beam.Owner is not PlayerEntity p || p.HostFxShootOffset is not Vector3 fx) return;
        if (!p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson)) return;
        if (_cannonBeam != "all" && BodyOf(p)?.Id.Contains("Trooper") != true) return;
        var ahead = new Vector3(p.Field70, 0, p.Field74);
        if (ahead.LengthSquared < 1e-12f) return;
        ahead.Normalize();
        Vector3 tip = p.Position + Vector3.Cross(Vector3.UnitY, ahead) * fx.X + Vector3.UnitY * fx.Y + ahead * fx.Z;
        beam.HostDrawShift = tip - beam.SpawnPosition;
        if (_cannonMeasure) Log.Info("MPHCampaign", FormattableString.Invariant(
            $"cannon beam: render {_cannonRenders} seat {p.SlotIndex} {BodyOf(p)?.Id} shift {beam.HostDrawShift.Value.Length:0.000}"));
    }

    // dev (armcannonview): a fixed view on the main player for pictures of his cannon from the side
    void CannonView(Scene scene, PlayerEntity p, float deg)
    {
        var back = new Vector3(-p.Field70, 0, -p.Field74);
        if (back.LengthSquared < 1e-12f || p.IsAltForm) return;
        back.Normalize();
        float yaw = MathF.Atan2(back.X, back.Z) + MathHelper.DegreesToRadians(deg);
        var dir = new Vector3(MathF.Sin(yaw), 0.15f, MathF.Cos(yaw)).Normalized();
        Vector3 center = p.Position + new Vector3(0, 0.6f, 0);
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(center + dir * _cannonViewDist, center, Vector3.UnitY, 0);
    }

    // dev (armcannonmeasure): where the body's cannon is against where MPH shoots from and draws the flash
    void MeasureCannon(PlayerEntity p)
    {
        _cannonMeasureFrame++;
        if (BodyOf(p) is not CampaignRetroBody body || !body.CannonInWorld(p, out Vector3 tip, out Vector3 barrel)) return;
        var fwd = new Vector3(p.Field70, 0, p.Field74);
        if (fwd.LengthSquared < 1e-12f) return;
        fwd.Normalize();
        Vector3 left = Vector3.Cross(Vector3.UnitY, fwd);
        string At(Vector3 w)
        {
            Vector3 d = w - p.Position;
            return FormattableString.Invariant($"{Vector3.Dot(d, left):0.000} {d.Y:0.000} {Vector3.Dot(d, fwd):0.000}");
        }
        Vector3 aim = p.GunDrawTransform.Row2.Xyz.Normalized();
        float angle = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(barrel, aim), -1f, 1f)));
        Log.Info("MPHCampaign", $"cannon3p f{_cannonMeasureFrame}: tip {At(tip)} | sim {At(p.GunMuzzlePosition)} | node {At(p.HostShootPoint)}"
            + $" | barrel {At(p.Position + barrel)} vs aim {At(p.Position + aim)} = "
            + FormattableString.Invariant($"{angle:0} deg | tip-sim {(tip - p.GunMuzzlePosition).Length:0.000}"));
    }

    MphGunMotion? Motion(Hunter hunter)
    {
        if (_cannonMotion.TryGetValue(hunter, out MphGunMotion? m)) return m;
        try { m = MphGunMotion.For(hunter); }
        catch (Exception ex) { Log.Warn("MPHCampaign", $"arm cannon: {hunter}'s gun motion: {ex.Message} -- held still"); }
        return _cannonMotion[hunter] = m;
    }

    // MPH drew the hunter's first-person gun this frame (its draw items), and at what alpha (the cloak)
    static bool MphGunDrawn(Scene scene, string gun, out float alpha)
    {
        alpha = 0;
        bool drawn = false;
        foreach (IReadOnlyList<RenderItem> list in new[] { scene.OpaqueItems, scene.DecalItems, scene.TranslucentItems })
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsGunItem(scene, list[i], gun)) continue;
                drawn = true;
                alpha = MathF.Max(alpha, list[i].Alpha);
            }
        }
        return drawn;
    }

    static bool IsGunItem(Scene scene, RenderItem item, string gun) =>
        item.Type == RenderItemType.Mesh && item.ListId >= 1 && item.ListId <= scene.HostMeshes.Count
        && scene.HostMeshes[item.ListId - 1].Model.Name == gun;

    // DrawItem: MPH's gun is left to the cannon
    bool CannonHidesItem(Scene scene, RenderItem item) => _cannonActive && IsGunItem(scene, item, _cannonGun);

    // DrawSkins' passes
    void DrawArmCannon(Scene scene, SuitPass pass)
    {
        if (!_cannonActive || _cannonBody == null || _host == null || scene != _host.Scene) return;
        _cannonBody.SeatTint2 = _cannonTint2;
        _cannonBody.DrawCannon(_cannonModel, scene.ViewMatrix, GunProjection(scene), pass, _cannonAlpha, _cannonLightDev, _cannonTint);
        _cannonBody.SeatTint2 = Vector4.Zero;
        if (pass == SuitPass.Additive && _cannonViewsDir != null && --_cannonViewsWait <= 0)
        {
            ShootCannonViews(_cannonBody, _cannonViewsDir);
            _cannonViewsDir = null;
        }
        // MphRead's own drawing state back, as DrawSkins leaves it
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    // the cut alone (dev pictures for the owner): six sides on a plain background, each into a 4x multisampled
    // framebuffer and read back, through the cannon's own draw (DrawCannon: the body's materials and light). Cannon
    // space: +X the player's left, +Y up, +Z the barrel; the muzzle points right in the side and top views
    static void ShootCannonViews(CampaignRetroBody body, string dir)
    {
        const int W = 800, H = 600;
        BodyArmCannon c = body.Cannon!;
        // muzzle at z = 0 and the cut reaching Length behind it: centred, one unit long
        Matrix4 model = c.ToCannon * Matrix4.CreateTranslation(0, 0, c.Length / 2) * Matrix4.CreateScale(1 / c.Length);
        var sides = new (string Name, Vector3 Eye, Vector3 Up)[]
        {
            ("outside", new Vector3(-2.2f, 0.25f, 0), Vector3.UnitY), ("inside", new Vector3(2.2f, 0.25f, 0), Vector3.UnitY),
            ("top", new Vector3(0, 2.2f, 0), Vector3.UnitX), ("muzzle", new Vector3(0.3f, 0.3f, 2.2f), Vector3.UnitY),
            ("behind", new Vector3(-0.5f, 0.6f, -2.1f), Vector3.UnitY), ("three-quarter", new Vector3(-1.5f, 0.9f, 1.4f), Vector3.UnitY),
        };
        var saved = new int[4];
        GLES30.GlGetIntegerv(0x8CA6, saved, 0); // GL_FRAMEBUFFER_BINDING
        int oldFbo = saved[0];
        GLES30.GlGetIntegerv(0x0BA2, saved, 0); // GL_VIEWPORT
        var ids = new int[2];
        GLES30.GlGenFramebuffers(2, ids, 0);
        var rbs = new int[3];
        GLES30.GlGenRenderbuffers(3, rbs, 0);
        try
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[0]);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[0]);
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, 4, GLES30.GlRgba8, W, H);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[0]);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[1]);
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, 4, GLES30.GlDepthComponent24, W, H);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlDepthAttachment, GLES30.GlRenderbuffer, rbs[1]);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[1]);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[2]);
            GLES30.GlRenderbufferStorage(GLES30.GlRenderbuffer, GLES30.GlRgba8, W, H);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[2]);
            Directory.CreateDirectory(dir);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(30 * MathF.PI / 180f, W / (float)H, 0.05f, 20f);
            foreach ((string name, Vector3 eye, Vector3 up) in sides)
            {
                GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[0]);
                GLES30.GlViewport(0, 0, W, H);
                GLES30.GlClearColor(0.24f, 0.25f, 0.28f, 1);
                GLES30.GlDepthMask(true);
                GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
                GLES30.GlEnable(GLES30.GlDepthTest);
                GLES30.GlDepthFunc(GLES30.GlLess);
                Matrix4 view = Matrix4.LookAt(eye, Vector3.Zero, up);
                body.DrawCannon(model, view, proj, SuitPass.Opaque, 1);
                GLES30.GlDepthFunc(GLES30.GlLequal);
                body.DrawCannon(model, view, proj, SuitPass.Translucent, 1);
                body.DrawCannon(model, view, proj, SuitPass.Additive, 1);
                GLES30.GlDepthMask(true);
                GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, ids[0]);
                GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, ids[1]);
                GLES30.GlBlitFramebuffer(0, 0, W, H, 0, 0, W, H, GLES30.GlColorBufferBit, GLES30.GlNearest);
                GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[1]);
                byte[] raw = new byte[W * H * 4], px = new byte[W * H * 4];
                GLES30.GlReadPixels(0, 0, W, H, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
                for (int y = 0; y < H; y++) Buffer.BlockCopy(raw, (H - 1 - y) * W * 4, px, y * W * 4, W * 4);
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                string path = Path.Combine(dir, $"{body.Id}_{name}.png");
                Log.Info("MPHCampaign", $"arm cannon views: {path} {(AppPlatform.EncodePng(path, px, W, H) ? "written" : "NOT written")}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "arm cannon views failed: " + ex.Message);
        }
        finally
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, oldFbo);
            GLES30.GlDeleteRenderbuffers(3, rbs, 0);
            GLES30.GlDeleteFramebuffers(2, ids, 0);
            GLES30.GlViewport(saved[0], saved[1], saved[2], saved[3]);
            GLES30.GlEnable(GLES30.GlDepthTest);
        }
    }

    // PausedInput (pressed this frame): true when the cannon took the D-pad (the main player wears one)
    bool ArmCannonInput(bool up, bool down, bool side)
    {
        if (_host == null || BodyOf(_host.Player)?.Cannon == null) return false;
        if (side)
        {
            // cannon + shots from the arm -> cannon + vanilla shots -> MPH -> ...
            if (_cannonOn && _armShots) _armShots = false;
            else if (_cannonOn) _cannonOn = false;
            else (_cannonOn, _armShots) = (true, true);
        }
        if (up) _cannonSize *= 1.05f;
        if (down) _cannonSize /= 1.05f;
        if (side || up || down) _statusCountdown = 0;
        return true;
    }

    string ArmCannonPauseText() => _host != null && BodyOf(_host.Player)?.Cannon != null
        ? $"\nFirst-person gun: {(_cannonOn ? $"the body's own cannon, size {_cannonSize:0.00}; third person: flash on its tip, {(_armShots ? "shots drawn from the arm" : "vanilla shots")}" : "MPH (flash and shots too)")}  -- D-pad left: arm shots / vanilla shots / MPH, up/down: size"
        : "";
}
