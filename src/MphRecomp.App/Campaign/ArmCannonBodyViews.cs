using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dev pictures for the Trooper's third-person arm (owner 2026-10-09: "line up the arm as close as possible to where the
// beam is coming out without having the arm look like it is contorted or clipping"): on one render, the main player's
// whole posed body alone on a plain background from fixed sides, once per arm pose, so poses compare on the very same
// frame. Each picture gets a line in <dir>/points.txt with where the cannon tip, the sim muzzle (where every shot
// starts) and a point 1.5 along the aim land in it, for marking afterwards. Render only.
//   --es armcannonbodyviews <dir> [armcannonbodyframe <render, default 2000>] [armcannonposes "clips|hold|raise:35,10|..."]
//   (pose: clips = the Space Pirate clips alone, hold = the arm held on the aim, raise:<lift>,<flare>[,<twist>] = hold + AimArm)
internal sealed partial class CampaignRenderer
{
    string? _bodyViewsDir;
    int _bodyViewsFrame = 2000;
    string _bodyViewsPoses = "hold|raise:45,0,30";

    void ConfigureBodyViews(LaunchArgs args)
    {
        _bodyViewsDir = args.Get("armcannonbodyviews");
        if (int.TryParse(args.Get("armcannonbodyframe"), out int f)) _bodyViewsFrame = f;
        if (args.Get("armcannonposes") is string poses) _bodyViewsPoses = poses;
    }

    void BodyViewsIfDue()
    {
        if (_bodyViewsDir == null || _host == null || _cannonRenders < _bodyViewsFrame) return;
        PlayerEntity p = _host.Player;
        if (p.IsAltForm || p.Health == 0 || BodyOf(p) is not CampaignRetroBody body || !body.CannonInWorld(p, out _, out _)) return;
        string dir = _bodyViewsDir;
        _bodyViewsDir = null;
        bool hold = CampaignRetroBody.TrooperArmHold, raise = CampaignRetroBody.TrooperArmRaise;
        float lift = CampaignRetroBody.TrooperArmLift, flare = CampaignRetroBody.TrooperArmFlare, twist = CampaignRetroBody.TrooperTwist;
        try { ShootBodyViews(p, body, dir); }
        finally
        {
            CampaignRetroBody.TrooperArmHold = hold;
            CampaignRetroBody.TrooperArmRaise = raise;
            CampaignRetroBody.TrooperArmLift = lift;
            CampaignRetroBody.TrooperArmFlare = flare;
            CampaignRetroBody.TrooperTwist = twist;
            GLES30.GlUseProgram(_program);
            GLES30.GlBindVertexArray(0);
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
            GLES30.GlDisable(CullFaceCap);
            GLES30.GlDepthMask(true);
        }
    }

    static void ApplyPose(string pose)
    {
        CampaignRetroBody.TrooperArmHold = pose != "clips";
        CampaignRetroBody.TrooperArmRaise = pose.StartsWith("raise", StringComparison.Ordinal);
        string[] v = pose.Contains(':') ? pose[(pose.IndexOf(':') + 1)..].Split(',') : Array.Empty<string>();
        if (v.Length > 0 && float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float l)) CampaignRetroBody.TrooperArmLift = l;
        if (v.Length > 1 && float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) CampaignRetroBody.TrooperArmFlare = f;
        if (v.Length > 2 && float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float t)) CampaignRetroBody.TrooperTwist = t;
    }

    void ShootBodyViews(PlayerEntity p, CampaignRetroBody body, string dir)
    {
        const int W = 900, H = 900;
        var ahead = new Vector3(p.Field70, 0, p.Field74).Normalized();
        Vector3 left = Vector3.Cross(Vector3.UnitY, ahead);
        Vector3 center = p.Position + new Vector3(0, 0.45f, 0) + ahead * 0.25f;
        Vector3 At(float l, float u, float f) => center + left * l + Vector3.UnitY * u + ahead * f;
        // sides as the owner would stand: his right, the front, behind (the game's own view side), above, a front-right
        // three-quarter, a rear-right three-quarter
        var sides = new (string Name, Vector3 Eye)[]
        {
            ("right", At(-4.4f, 0.25f, 0)), ("front", At(0, 0.4f, 4.4f)), ("behind", At(0.6f, 0.8f, -4.4f)),
            ("above", At(-0.5f, 4.4f, 0.3f)), ("front-right", At(-3.1f, 0.7f, 3.1f)), ("rear-right", At(-3.1f, 1.0f, -3.1f)),
        };
        Vector3 muzzle = p.GunMuzzlePosition, aim = p.GunDrawTransform.Row2.Xyz.Normalized();
        var saved = new int[4];
        GLES30.GlGetIntegerv(0x8CA6, saved, 0); // GL_FRAMEBUFFER_BINDING
        int oldFbo = saved[0];
        GLES30.GlGetIntegerv(0x0BA2, saved, 0); // GL_VIEWPORT
        var ids = new int[2];
        GLES30.GlGenFramebuffers(2, ids, 0);
        var rbs = new int[3];
        GLES30.GlGenRenderbuffers(3, rbs, 0);
        var points = new StringBuilder();
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
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(30 * MathF.PI / 180f, W / (float)H, 0.05f, 30f);
            string[] poses = _bodyViewsPoses.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < poses.Length; i++)
            {
                ApplyPose(poses[i]);
                body.CannonInWorld(p, out Vector3 tip, out _);
                foreach ((string name, Vector3 eye) in sides)
                {
                    Matrix4 view = Matrix4.LookAt(eye, center, name == "above" ? ahead : Vector3.UnitY);
                    GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[0]);
                    GLES30.GlViewport(0, 0, W, H);
                    GLES30.GlClearColor(0.30f, 0.31f, 0.34f, 1);
                    GLES30.GlDepthMask(true);
                    GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
                    GLES30.GlEnable(GLES30.GlDepthTest);
                    GLES30.GlDepthFunc(GLES30.GlLess);
                    body.DrawBody(p, view, proj, SuitPass.Opaque, 1, false);
                    GLES30.GlDepthFunc(GLES30.GlLequal);
                    body.DrawBody(p, view, proj, SuitPass.Translucent, 1, false);
                    body.DrawBody(p, view, proj, SuitPass.Additive, 1, false);
                    GLES30.GlDepthMask(true);
                    GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, ids[0]);
                    GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, ids[1]);
                    GLES30.GlBlitFramebuffer(0, 0, W, H, 0, 0, W, H, GLES30.GlColorBufferBit, GLES30.GlNearest);
                    GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, ids[1]);
                    byte[] raw = new byte[W * H * 4], px = new byte[W * H * 4];
                    GLES30.GlReadPixels(0, 0, W, H, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
                    for (int y = 0; y < H; y++) Buffer.BlockCopy(raw, (H - 1 - y) * W * 4, px, y * W * 4, W * 4);
                    for (int k = 3; k < px.Length; k += 4) px[k] = 255;
                    string file = $"{i}_{name}.png";
                    AppPlatform.EncodePng(Path.Combine(dir, file), px, W, H);
                    string Px(Vector3 w)
                    {
                        Vector4 c = new Vector4(w, 1) * view * proj;
                        if (c.W <= 1e-6f) return "nan nan";
                        return FormattableString.Invariant($"{(c.X / c.W * 0.5f + 0.5f) * W:0.0} {(0.5f - c.Y / c.W * 0.5f) * H:0.0}");
                    }
                    points.AppendLine($"{file} {poses[i]} | tip {Px(tip)} | sim {Px(muzzle)} | aim {Px(muzzle + aim * 1.5f)}");
                }
                LogBones(p, body, left, ahead, poses[i]);
                Vector3 d = tip - p.Position;
                Log.Info("MPHCampaign", "arm cannon body views: pose " + poses[i] + FormattableString.Invariant(
                    $": tip {Vector3.Dot(d, left):0.000} {d.Y:0.000} {Vector3.Dot(d, ahead):0.000} | off the shot line {OffLine(tip, muzzle, aim):0.000}"));
            }
            File.WriteAllText(Path.Combine(dir, "points.txt"), points.ToString());
            Log.Info("MPHCampaign", $"arm cannon body views: {poses.Length} poses x {sides.Length} sides in {dir}");
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", "arm cannon body views failed: " + ex.Message);
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

    // the arm's chain and the head in the player's frame (left, up, forward of p.Position) for the pose just drawn
    static void LogBones(PlayerEntity p, CampaignRetroBody body, Vector3 left, Vector3 ahead, string pose)
    {
        if (!body.BodyModel(p, out Matrix4 model)) return;
        var sb = new StringBuilder("arm cannon body views: bones " + pose + ":");
        foreach (string name in new[] { "Spine_1", "Spine_2", "Neck_1", "Head_1", "R_collar", "R_shoulder", "R_elbow", "R_wrist", "R_gun_LCTR", "L_shoulder" })
        {
            int b = body.Body.Bone(name);
            if (b < 0) continue;
            Vector3 d = (body.Body.World[b] * model).Row3.Xyz - p.Position;
            sb.Append(FormattableString.Invariant($" {name} ({Vector3.Dot(d, left):0.00} {d.Y:0.00} {Vector3.Dot(d, ahead):0.00})"));
        }
        Log.Info("MPHCampaign", sb.ToString());
    }

    // how far a point sits from the shot's line (from the muzzle along the aim)
    static float OffLine(Vector3 x, Vector3 muzzle, Vector3 aim)
    {
        Vector3 v = x - muzzle;
        return (v - aim * Vector3.Dot(v, aim)).Length;
    }
}
