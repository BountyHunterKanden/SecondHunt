using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The first-person arm cannon cut from a native body (Core BodyArmCannon; owner 2026-10-08): the cut's vertices ride
// in the body's own vertex buffer after the body's, one batch per material the cut keeps, drawn by the body's own
// material code (Material / Emit: the same TEV programs, textures, wraps, scrolling texture matrices and light), so
// every effect the body shows in third person shows on its cannon. The body is held in the cannon's one pose (its
// palette), and the model matrix puts the cannon where MPH draws its first-person gun (MatchArmCannon.cs).
internal sealed partial class CampaignRetroBody
{
    public BodyArmCannon? Cannon { get; private set; }
    readonly List<Batch> _cannon = new();
    float _cannonLight = 1;
    public static float CannonAlphaDev; // dev: armcannonalpha, for a cannon with an alpha of its own (0 = its own)

    // after the body's vertices are read (and V-flipped): the cut's copied after them (a worker thread at load; the cut
    // is worked out once -- it poses the shared body, before any seat animates it)
    void AppendCannon()
    {
        if (_verts == null) return;
        try
        {
            Cannon ??= BodyArmCannon.For(Body) ?? HeldGunCannon(); // (+ Brawl Zero Suit's gun arm: ZeroSuitGunTip.cs)
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"body {Id}: no arm cannon cut: {ex.Message}");
        }
        _cannon.Clear();
        if (Cannon == null) return;
        HoldTrooperArm();
        const int F = HdGun.FloatsPerVertex;
        int add = Cannon.Vertices;
        var all = new float[_verts.Length + add * F];
        Array.Copy(_verts, all, _verts.Length);
        int at = _verts.Length / F;
        foreach (Batch b in _batches)
        {
            int[] keep = Cannon.Cut[b.Material];
            if (keep.Length == 0) continue;
            _cannon.Add(new Batch { Material = b.Material, Start = at, Count = keep.Length, Pass = b.Pass, Spec = b.Spec, Gx = b.Gx, P = b.P,
                Cull = b.Cull, BlendDepth = b.BlendDepth }); // culled as the body's parts (owner 10-18: the final form's cannon too bright)
            foreach (int v in keep)
            {
                Array.Copy(_verts, (b.Start + v) * F, all, at * F, F);
                at++;
            }
        }
        _verts = all;
    }

    // the cannon at `model` (body model space in the cannon's pose -> world: BodyArmCannon.GunSpace * the gun matrix):
    // passes as DrawBody (opaque 0 / blended 1 / additive 2, the depth rebuild), faded by alpha (MPH's gun alpha: the
    // cloak); no damage flash (MPH's first-person gun has none). tint: the seat's team / duplicate colour (MatchSkins'
    // SeatTint, as its body wears it; zero = as imported)
    public void DrawCannon(Matrix4 model, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, float light = 0, Vector4 tint = default)
    {
        if (Cannon == null || _cannon.Count == 0 || alpha <= 0f || Failed || !Warm(1000)) return;
        _cannonLight = light > 0 ? light : Cannon.Light;
        try
        {
            _interpOnly = tint.W > 0 || DdTex != 0; // (DdTex: the seat's double damage, as its body)
            _tint = tint;
            foreach (Batch c in _cannon)
            {
                // the body's batch of the same material made the program and textures (again after a GL reset)
                Batch b = _batches[c.Material];
                c.Prog = b.Prog;
                Array.Copy(b.Tex, c.Tex, c.Tex.Length);
            }
            bool rebuild = pass == SuitPass.DepthRebuild;
            int p = rebuild ? 0 : (int)pass;
            bool fade = alpha < 0.999f && _fadeOk;
            bool own = !(fade && p == 0);
            bool moved = fade && p == 1;
            if (!(own && CannonHas(p)) && !(moved && CannonHas(0))) return;
            double s = Environment.TickCount64 / 1000.0;
            Cannon.Pose(s);
            Begin(view, proj, model);
            if (moved) CannonFaded(alpha, s);
            if (own && p == 0 && _depthProg != 0)
            {
                if (!rebuild) GLES30.GlColorMask(false, false, false, false);
                CannonDepth();
                if (!rebuild) GLES30.GlColorMask(true, true, true, true);
                if (!rebuild)
                {
                    foreach (Batch c in _cannon) if (c.Pass == 0) EmitCannon(c, 1f, s, c.DepthFirst);
                }
                else
                {
                    foreach (Batch c in _cannon) if (c.Pass == 0 && !c.DepthFirst) EmitCannon(c, 1f, s);
                }
            }
            else if (own)
            {
                // the blended parts at the cannon's alpha (the final form's shell)
                float own1 = CannonAlphaDev > 0 && Cannon.Alpha < 1 ? CannonAlphaDev : Cannon.Alpha;
                float a = (fade ? alpha : 1f) * (p == 1 ? own1 : 1f);
                foreach (Batch c in _cannon) if (c.Pass == p) EmitCannon(c, a, s);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"body {Id}: arm cannon draw failed -- the body and MPH's gun from now on: {ex}");
            Failed = true;
        }
        finally
        {
            _interpOnly = false;
            _tint = Vector4.Zero;
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindVertexArray(0);
        }
    }

    // Emit at the cannon's own light (BodyArmCannon.Light, or DrawCannon's dev override): the program's exposure, set
    // after Use loads the body's (1) -- Material's Use then keeps it (same program); the body's next Begin loads 1 again.
    // A tinted seat draws through the interpreter (Material: _interpOnly), so that's the program lit
    void EmitCannon(Batch c, float fade, double seconds, bool lequal = false)
    {
        if (_cannonLight != 1f && c.Prog != 0)
        {
            int prog = _interpOnly ? _prog : c.Prog;
            Use(prog);
            GLES30.GlUniform1f(U(prog, "uExposure"), _cannonLight);
        }
        Emit(c, fade, seconds, lequal);
    }

    bool CannonHas(int pass)
    {
        foreach (Batch c in _cannon) if (c.Pass == pass) return true;
        return false;
    }

    void CannonDepth()
    {
        foreach (Batch c in _cannon)
        {
            if (!c.DepthFirst) continue;
            Use(_depthProg);
            Palette(_depthProg, c);
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
            GLES30.GlDepthFunc(DepthFunc(c.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, c.Start, c.Count);
        }
    }

    // the opaque parts at alpha, as DrawFaded does the body's
    void CannonFaded(float alpha, double seconds)
    {
        if (_depthProg != 0)
        {
            GLES30.GlColorMask(false, false, false, false);
            CannonDepth();
            GLES30.GlColorMask(true, true, true, true);
        }
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlZero, GLES30.GlOne);
        GLES30.GlDepthMask(true);
        foreach (Batch c in _cannon)
        {
            if (c.Pass != 0 || _depthProg != 0 && c.DepthFirst) continue;
            Material(c, 1f, seconds);
            GLES30.GlDepthFunc(DepthFunc(c.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, c.Start, c.Count);
        }
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        foreach (Batch c in _cannon)
        {
            if (c.Pass != 0) continue;
            Material(c, alpha, seconds);
            if (_cannonLight != 1f) GLES30.GlUniform1f(U(_cur, "uExposure"), _cannonLight);
            GLES30.GlDrawArrays(GLES30.GlTriangles, c.Start, c.Count);
        }
    }

    // third person (part B; owner 2026-10-09 "the troopers are most off"): the Trooper's cannon arm held on the aim, as
    // Dark Samus's is (D14: DarkSamusBody.Hold, from his cannon's own cut frame A_gunloop_pirate t0), not where his
    // Space Pirate clips swing it (8-17 deg off the aim). Not while he clings to a wall (the wall clips have their own
    // frame). TrooperArmHold switches it live (MatchArmCannon: the pause toggle, dev armcannonhold). Once, at load.
    public static bool TrooperArmHold;
    public string? ArmHoldInfo { get; private set; }
    // owner 2026-10-09 ("he is shooting so far right of where the beam actually happens"): on top of the hold, his cannon
    // brought toward the shot's line (eye height, just right of centre) without contorting him. The first try (elbow put
    // on the line + chest leaned back) clipped the arm through the body, turned down. Now: the upper arm swung forward and
    // up by TrooperArmLift (out by TrooperArmFlare) instead of flared out sideways, its roll turned with it so the elbow
    // still bends in its own plane, the forearm level on the aim. Owner 2026-10-09 17:30 (on the Odin, live switch): his
    // upper body also turns his right shoulder in toward the shot by TrooperTwist about the chest (Spine_2), the neck
    // turned back by as much so his head still faces ahead -- a shooter bringing the gun to the eye. Draw only (the pose).
    // 17:45 owner: "this is bad too" -> hold and pose dev only again (MatchArmCannon: armcannonhold 1, armcannonraise 1).
    public static bool TrooperArmRaise;
    public static float TrooperArmLift = 45, TrooperArmFlare = 0, TrooperArmMaxUp = 70, TrooperTwist = 30;

    void HoldTrooperArm()
    {
        if (Clips.AfterPose != null || Body.Bone("R_gun_LCTR") < 0) return;
        if (DarkSamusBody.Hold.For(Body, "A_gunloop_pirate", 0, "R_gun_LCTR") is not DarkSamusBody.Hold hold) return;
        int shoulder = Body.Bone("R_shoulder"), elbow = Body.Bone("R_elbow"), tip = Body.Bone("R_gun_LCTR");
        int chest = Body.Bone("Spine_2"), neck = Body.Bone("Neck_1");
        float lift = Clips.Lift;
        Clips.AfterPose = (body, who) =>
        {
            if (!TrooperArmHold || who?.WallClinging == true) return;
            hold.Apply(body, who);
            if (TrooperArmRaise && who != null && shoulder >= 0 && elbow >= 0) AimArm(body, who, lift, chest, neck, shoulder, elbow, tip);
        };
        ArmHoldInfo = FormattableString.Invariant($"arm held on the aim (A_gunloop_pirate t0, {hold.OffDegrees:0} deg off before truing)");
    }

    // TrooperArmRaise, after the hold (model space). The shoulder turns as one frame (upper arm + elbow hinge axis) onto
    // the new upper arm, so the elbow then only opens about its own hinge to put the barrel on the aim: no sideways bend.
    static void AimArm(NativeBody body, PlayerEntity who, float lift, int chest, int neck, int shoulder, int elbow, int tip)
    {
        if (chest >= 0 && TrooperTwist != 0)
        {
            // model space: +X his left, +Z ahead; a positive turn about Y brings his right shoulder forward and in
            float twist = MathHelper.DegreesToRadians(TrooperTwist);
            body.Turn(chest, Matrix4.CreateRotationY(twist));
            if (neck >= 0) body.Turn(neck, Matrix4.CreateRotationY(-twist));
        }
        var ahead = new Vector3(who.Field70, 0, who.Field74);
        if (ahead.LengthSquared < 1e-12f) return;
        Matrix4 inv = body.ModelMatrix(who.Hunter, who.Position, ahead, lift).Inverted();
        Vector3 f = Vector3.TransformVector(who.GunDrawTransform.Row2.Xyz, inv);
        if (f.LengthSquared < 1e-12f) return;
        f.Normalize();
        Vector3 s = body.World[shoulder].Row3.Xyz, e = body.World[elbow].Row3.Xyz, t = body.World[tip].Row3.Xyz;
        if (!Frame(e - s, t - e, out Matrix4 from)) return;
        Vector3 left = Vector3.Cross(Vector3.UnitY, f);
        if (left.LengthSquared < 1e-8f) return;
        left.Normalize();
        Vector3 up = Vector3.Cross(f, left);
        float pitch = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(f.Y, -1f, 1f)));
        float elev = MathHelper.DegreesToRadians(MathF.Max(0, MathF.Min(TrooperArmLift, TrooperArmMaxUp - pitch)));
        Vector3 u = f * MathF.Cos(elev) + up * MathF.Sin(elev) - left * MathF.Tan(MathHelper.DegreesToRadians(TrooperArmFlare));
        if (!Frame(u, f, out Matrix4 to)) return;
        // rows (arm, hinge, third) -> the same rows of the new frame: from^T * to, for row vectors
        body.Turn(shoulder, Matrix4.Transpose(from) * to);
        Vector3 barrel = body.World[tip].Row3.Xyz - body.World[elbow].Row3.Xyz;
        if (barrel.LengthSquared < 1e-12f) return;
        body.Turn(elbow, DarkSamusBody.Hold.Turn(barrel.Normalized(), f));
    }

    // an arm's frame as rows: upper arm, elbow hinge axis (upper arm x forearm), their cross; false when straight
    static bool Frame(Vector3 arm, Vector3 forearm, out Matrix4 m)
    {
        m = Matrix4.Identity;
        if (arm.LengthSquared < 1e-12f || forearm.LengthSquared < 1e-12f) return false;
        arm.Normalize();
        Vector3 hinge = Vector3.Cross(arm, forearm.Normalized());
        if (hinge.LengthSquared < 1e-6f) return false;
        hinge.Normalize();
        m.Row0 = new Vector4(arm, 0);
        m.Row1 = new Vector4(hinge, 0);
        m.Row2 = new Vector4(Vector3.Cross(arm, hinge), 0);
        return true;
    }

    // dev pictures: `p`'s body posed as DrawBody poses it (Body.World) and its model matrix
    public bool BodyModel(PlayerEntity p, out Matrix4 model)
    {
        model = Matrix4.Identity;
        if (!_anims.TryGetValue(p, out NativeBodyAnimator? a) || !a.Pose(p)) return false;
        model = Body.ModelMatrix(p.Hunter, p.Position, a.WallFacing ?? new Vector3(p.Field70, 0, p.Field74), Clips.Lift);
        return true;
    }

    // third person (part B): the body's cannon where `p` stands now, as DrawBody poses and places it -- its muzzle
    // locator in the world, and the barrel's way (elbow -> locator). False before the seat's first Tick.
    public bool CannonInWorld(PlayerEntity p, out Vector3 tip, out Vector3 barrel)
    {
        tip = barrel = default;
        if (Cannon == null || !_anims.TryGetValue(p, out NativeBodyAnimator? a) || !a.Pose(p)) return false;
        Vector3 facing = a.WallFacing ?? new Vector3(p.Field70, 0, p.Field74);
        Matrix4 model = Body.ModelMatrix(p.Hunter, p.Position, facing, Clips.Lift);
        tip = (Body.World[Cannon.Tip] * model).Row3.Xyz;
        Vector3 elbow = (Body.World[Cannon.Elbow] * model).Row3.Xyz;
        barrel = (tip - elbow).LengthSquared > 1e-12f ? (tip - elbow).Normalized() : Vector3.Zero;
        return true;
    }
}
