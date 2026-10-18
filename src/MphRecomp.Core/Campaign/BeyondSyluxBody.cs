using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Metroid Prime 4: Beyond Sylux on HIS OWN skeleton (bodies/MP4Sylux: Import/Bodies/BeyondBody, the gameplay
    // character's joints and the model's own skin). Owner 2026-10-17, after the flourish on the DS bones looked "really
    // bad": "We have Syluxs real rig ... we should only be using vanilla rig for movement like running strafe and
    // backwards. why cant we get the clean results we got for trooper and dark samus". So:
    //  - standing: his own idle_sylux;
    //  - his idle flourish: taunt_flex_sylux, then taunt_armSwipe_sylux, back and forth (BodyFlourish.Beyond, in turn), from
    //    his spine root up, his legs on idle_sylux (planted);
    //  - every move (walks, jumps, landings, hits, morphing, the spawn) and the upper body's shots and charge: vanilla
    //    Sylux's DS clips put on his skeleton at load (DsRetarget, from the player's own MPH ROM), his feet where his idle
    //    stands them; the upper body pitches with the aim from his spine root, as a hunter's Spine_1 does.
    // His legs and his upper body hang separately off the skeleton's root (hip_C0_0_jnt, spine_C0_hip_jnt): both move.
    public static class BeyondSyluxBody
    {
        public const string Idle = "idle_sylux";
        public const string Ds = "ds:";
        const string SpineRoot = "spine_C0_hip_jnt";

        static readonly DsRetarget.Spec Spec = new(Hunter.Sylux,
            new (string, string, string?, string?)[]
            {
                ("hip_C0_0_jnt", "Pelvis", "", ""), (SpineRoot, "Spine_1", "", ""), ("spine_C0_1_jnt", "Spine_2", "", ""),
                ("neck_C0_head_jnt", "Head_1", "", ""),
                ("arm_L0_upper_jnt", "L_shoulder", "arm_L0_forearm_jnt", "L_elbow"), ("arm_L0_forearm_jnt", "L_elbow", "arm_L0_wrist_jnt", "L_wrist"),
                ("arm_L0_wrist_jnt", "L_wrist", null, null),
                // his coil arm: the DS forearm is the cannon (no wrist bone), pointing ahead
                ("arm_R0_upper_jnt", "R_shoulder", "arm_R0_forearm_jnt", "R_elbow"), ("arm_R0_forearm_jnt", "R_elbow", "arm_R0_wrist_jnt", "*fwd"),
                ("leg_and_foot_L0_hip_jnt", "L_hip", "leg_and_foot_L0_knee_jnt", "L_knee"), ("leg_and_foot_L0_knee_jnt", "L_knee", "leg_and_foot_L0_ankle_jnt", "L_ankle"),
                ("leg_and_foot_L0_ankle_jnt", "L_ankle", null, null),
                ("leg_and_foot_R0_hip_jnt", "R_hip", "leg_and_foot_R0_knee_jnt", "R_knee"), ("leg_and_foot_R0_knee_jnt", "R_knee", "leg_and_foot_R0_ankle_jnt", "R_ankle"),
                ("leg_and_foot_R0_ankle_jnt", "R_ankle", null, null),
            },
            new[] { ("hip_C0_0_jnt", "Pelvis"), (SpineRoot, "Spine_1") },
            ("leg_and_foot_L0_hip_jnt", "leg_and_foot_L0_knee_jnt", "leg_and_foot_L0_ankle_jnt"), Ds,
            FloorClip: Idle, FloorBones: new[] { "leg_and_foot_L0_ankle_jnt", "leg_and_foot_R0_ankle_jnt" });

#pragma warning disable CA2255 // registers his map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(For);
#pragma warning restore CA2255

        static readonly PlayerAnimation[] Walks =
        {
            PlayerAnimation.WalkForward, PlayerAnimation.WalkBackward, PlayerAnimation.WalkLeft, PlayerAnimation.WalkRight,
        };
        static readonly PlayerAnimation[] Jumps =
        {
            PlayerAnimation.JumpNeutral, PlayerAnimation.JumpForward, PlayerAnimation.JumpBack, PlayerAnimation.JumpLeft, PlayerAnimation.JumpRight,
        };
        static readonly PlayerAnimation[] Lands = { PlayerAnimation.LandNeutral, PlayerAnimation.LandLeft, PlayerAnimation.LandRight };
        static readonly PlayerAnimation[] Hits =
        {
            PlayerAnimation.DamageFront, PlayerAnimation.DamageBack, PlayerAnimation.DamageLeft, PlayerAnimation.DamageRight,
        };
        static readonly PlayerAnimation[] Torso = { PlayerAnimation.Shoot, PlayerAnimation.Charge, PlayerAnimation.ChargeShoot };

        static bool Loops(PlayerAnimation a) => a is PlayerAnimation.WalkForward or PlayerAnimation.WalkBackward or PlayerAnimation.WalkLeft
            or PlayerAnimation.WalkRight or PlayerAnimation.Charge;

        static BodyClips? For(NativeBody body)
        {
            if (body.Game != "mp4" || body.Clip(Idle) == null || body.Bone(SpineRoot) < 0 || body.Bone("hip_C0_0_jnt") < 0) return null;
            var moves = new List<PlayerAnimation> { PlayerAnimation.Morph, PlayerAnimation.Unmorph, PlayerAnimation.Spawn };
            moves.AddRange(Walks);
            moves.AddRange(Jumps);
            moves.AddRange(Lands);
            moves.AddRange(Hits);
            moves.AddRange(Torso);
            DsRetarget.AddClips(body, Spec, moves, Loops);

            string idle = Planted(body, Idle) ?? Idle;
            var m = new BodyClips { Idle = idle, FlourishSet = BodyFlourish.Prepare(body, BodyFlourish.Beyond, idle, SpineRoot) };
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Flourish, PlayerAnimation.Turn,
                PlayerAnimation.Charge, PlayerAnimation.Shoot, PlayerAnimation.ChargeShoot })
            {
                m.Set(a, idle, true);
            }
            foreach (PlayerAnimation a in moves)
            {
                if (Array.IndexOf(Torso, a) >= 0 || body.Clip(Ds + a) == null) continue;
                m.Set(a, Ds + a, Loops(a));
            }
            if (Array.TrueForAll(Walks, w => body.Clip(Ds + w) != null))
            {
                m.Lean = (Ds + PlayerAnimation.WalkForward, Ds + PlayerAnimation.WalkBackward, Ds + PlayerAnimation.WalkLeft, Ds + PlayerAnimation.WalkRight);
            }
            // the upper body: vanilla Sylux's shots, charge and flinches over the legs, from his spine root
            m.TorsoRoot = SpineRoot;
            m.AimBone = SpineRoot;
            foreach (PlayerAnimation a in Torso)
            {
                if (body.Clip(Ds + a) != null) m.SetTorso(a, Ds + a, Loops(a));
            }
            foreach (PlayerAnimation a in Hits)
            {
                if (body.Clip(Ds + a) != null) m.SetTorso(a, Ds + a, false);
            }
            return m;
        }

        // His idle with his feet planted (owner 10-17 ~21:30: "his idle also does not have feet planted on the ground they kind of
        // glide around a little"): idle_sylux keeps his hips still and moves each ankle up to ~0.05 (Prime 4 plants them in
        // game), so each frame both legs bend (hip, knee: a two-bone reach in the knee's own plane) to hold the ankle where frame
        // 0 has it, turned as frame 0 has it (the toes ride it). Made once at load as "<idle>|planted"; null if he lacks a leg.
        static readonly (string Hip, string Knee, string Ankle)[] Legs =
        {
            ("leg_and_foot_L0_hip_jnt", "leg_and_foot_L0_knee_jnt", "leg_and_foot_L0_ankle_jnt"),
            ("leg_and_foot_R0_hip_jnt", "leg_and_foot_R0_knee_jnt", "leg_and_foot_R0_ankle_jnt"),
        };

        static string? Planted(NativeBody body, string clip)
        {
            HdGun g = body.Data;
            string name = clip + "|planted";
            if (body.Clip(name) != null) return name;
            if (body.Clip(clip) is not HdGun.Anim a || a.Keys < 2) return null;
            var legs = new (int Hip, int Knee, int Ankle)[Legs.Length];
            for (int l = 0; l < Legs.Length; l++)
            {
                legs[l] = (body.Bone(Legs[l].Hip), body.Bone(Legs[l].Knee), body.Bone(Legs[l].Ankle));
                if (legs[l].Hip < 0 || legs[l].Knee < 0 || legs[l].Ankle < 0 || g.Parent[legs[l].Ankle] != legs[l].Knee
                    || g.Parent[legs[l].Knee] != legs[l].Hip) return null;
            }
            int n = g.BoneCount, st = g.KeyStride;
            float[] F = (float[])a.Frames.Clone();
            var W = new Matrix4[n];
            Matrix4 Local(int k, int b)
            {
                int o = (k * n + b) * st;
                return Matrix4.CreateFromQuaternion(new Quaternion(F[o], F[o + 1], F[o + 2], F[o + 3]))
                    * Matrix4.CreateTranslation(F[o + 4], F[o + 5], F[o + 6]);
            }
            void World(int k)
            {
                foreach (int b in g.Order) W[b] = g.Parent[b] >= 0 ? Local(k, b) * W[g.Parent[b]] : Local(k, b);
            }
            // a bone's world rotation set (its translation kept): back into its local key
            void SetRot(int k, int b, Matrix4 world)
            {
                Matrix4 local = world * (g.Parent[b] >= 0 ? W[g.Parent[b]].Inverted() : Matrix4.Identity);
                Quaternion q = local.ExtractRotation().Normalized();
                int o = (k * n + b) * st;
                F[o] = q.X; F[o + 1] = q.Y; F[o + 2] = q.Z; F[o + 3] = q.W;
                W[b] = world;
            }
            // world turned about its own origin by r (model space)
            static Matrix4 TurnAbout(Matrix4 world, Quaternion r)
            {
                Vector3 at = world.ExtractTranslation();
                return world * Matrix4.CreateTranslation(-at) * Matrix4.CreateFromQuaternion(r) * Matrix4.CreateTranslation(at);
            }
            static Quaternion Arc(Vector3 from, Vector3 to)
            {
                from.Normalize();
                to.Normalize();
                Vector3 axis = Vector3.Cross(from, to);
                float d = Math.Clamp(Vector3.Dot(from, to), -1, 1);
                if (axis.LengthSquared < 1e-12f) return Quaternion.Identity;
                return Quaternion.FromAxisAngle(axis.Normalized(), MathF.Acos(d));
            }
            World(0);
            var goal = new Matrix4[legs.Length];
            for (int l = 0; l < legs.Length; l++) goal[l] = W[legs[l].Ankle];
            for (int k = 1; k < a.Keys; k++)
            {
                World(k);
                for (int l = 0; l < legs.Length; l++)
                {
                    (int hi, int ki, int ai) = legs[l];
                    Vector3 h = W[hi].ExtractTranslation(), kn = W[ki].ExtractTranslation(), an = W[ai].ExtractTranslation();
                    Vector3 t = goal[l].ExtractTranslation();
                    float l1 = (kn - h).Length, l2 = (an - kn).Length;
                    Vector3 toT = t - h;
                    float d = Math.Clamp(toT.Length, Math.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
                    Vector3 dir = toT.Normalized();
                    // the knee: in the plane of the reach and where the knee points now
                    Vector3 pole = (kn - h) - Vector3.Dot(kn - h, dir) * dir;
                    if (pole.LengthSquared < 1e-10f) continue;
                    pole.Normalize();
                    float along = (l1 * l1 - l2 * l2 + d * d) / (2 * d);
                    Vector3 knee = h + dir * along + pole * MathF.Sqrt(MathF.Max(l1 * l1 - along * along, 0));
                    SetRot(k, hi, TurnAbout(W[hi], Arc(kn - h, knee - h)));
                    W[ki] = Local(k, ki) * W[hi];
                    W[ai] = Local(k, ai) * W[ki];
                    an = W[ai].ExtractTranslation();
                    SetRot(k, ki, TurnAbout(W[ki], Arc(an - knee, t - knee)));
                    W[ai] = Local(k, ai) * W[ki];
                    // the ankle turned as frame 0 has it (at where the reach put it)
                    Matrix4 want = goal[l].ClearTranslation() * Matrix4.CreateTranslation(W[ai].ExtractTranslation());
                    SetRot(k, ai, want);
                }
            }
            body.Data.Anims[name] = new HdGun.Anim { Name = name, Duration = a.Duration, Tick = a.Tick, Keys = a.Keys, Frames = F };
            return name;
        }
    }
}
