using System;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Dark Samus on her own game's skeleton with her own clips (docs/CHARACTERS_AND_SKINS.md D2/D8/D14/D18; clip picks:
    // handoffs/dark-samus-clipmap_2026-10-08.md): the clip map for her Echoes body (bodies/MP2DarkSamus, CINF 4abc8a50)
    // and her Corruption body (bodies/MP3DarkSamus, CINF adba16a78d7d49f7), registered with BodyClips.
    // D8: Samus's gameplay; her body is drawn with its toe tips just into the floor (BodyClips.Lift; owner 2026-10-08 20:30
    // after the 0.25 hover: "way too high ... the tips of her big toe a little bit in the ground").
    // D14: her arm cannon always points along the aim, like a vanilla hunter's: after every pose R_shoulder is turned to
    // a held model-space rotation (from one of her own aiming frames, trued so the cannon points straight ahead) and
    // pitched by MPH's aim (PlayerDraw's Spine_1 bend: the facing's elevation, clamped to ~45 deg); R_elbow (+ Corruption's
    // R_wrist) take that frame's local pose. The shoulder joint itself rides her torso, so breathing and hits still show.
    public static class DarkSamusBody
    {
        // her ready clip's toe tips this far into the floor (MPH units, owner to tune by eye); per game, as Echoes' ready clip
        // has them on her bind feet and Corruption's 0.04 of her own units (0.023 MPH) below them
        public const float Sink = 0.03f;
        const float EchoesLift = -Sink, CorruptionLift = 0.023f - Sink;

#pragma warning disable CA2255 // registers her map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(For);
#pragma warning restore CA2255

        static BodyClips? For(NativeBody body)
        {
            if (body.Clip("B_ready_dark_samus") != null) return Echoes(body);
            if (body.Clip("b_ready_dark_samus") != null) return Corruption(body);
            return null;
        }

        // her morph ball trail: her balls' own core colour (recipes/hd/MP2|MP3DarkSamusBall "core" 90 200 255), not MPH's
        // yellow-orange
        public static readonly Vector3 Trail = new Vector3(90, 200, 255) / 255f;

        // owner 2026-10-08: curling into the ball, out of it and jumps play MPH's own Samus clips on MPH's rig (her HD suit,
        // hd/MP2|MP3DarkSamus, drawn in place of this body for those states: MatchSkins.SuitLift); out of the ball on her
        // own body where she has the clip (owner 20:55: the vanilla unmorph's end snapped to her ready pose)
        // Jumps stay MPH's for both games (owner 21:20, after trying Corruption's energy-wave leap: "the jump is bad ... can
        // we do the same thing echoes does"); landings are her ready loop, as Echoes' (the leap's landing clip also slid
        // her along the floor after a spawn)
        static void Vanilla(BodyClips m, bool ownUnmorph)
        {
            m.SuitLegs.Add(PlayerAnimation.Morph);
            if (!ownUnmorph) m.SuitLegs.Add(PlayerAnimation.Unmorph);
            m.SuitLegs.UnionWith(new[] { PlayerAnimation.JumpNeutral, PlayerAnimation.JumpForward, PlayerAnimation.JumpBack,
                PlayerAnimation.JumpLeft, PlayerAnimation.JumpRight });
            m.Trail = Trail;
        }

        // Echoes' own way out of her boost ball (Metroid5.pak): uncurls from the ball into a crouched aim, stretched to MPH's
        // unmorph; standing up from the crouch into ready (or a lean) cross-fades over UncurlFade
        const string Uncurl = "A_outofBoostBall_toChargeReady_dark_samus";
        const float UncurlFade = 0.35f;

        // Echoes: she floats, leans into 2-key "glide" poses and dashes (no walk, jump or land clips of her own)
        static BodyClips Echoes(NativeBody body)
        {
            const string ready = "B_ready_dark_samus";
            var m = new BodyClips { Idle = ready, Lift = EchoesLift, FlourishSet = BodyFlourish.Prepare(body, BodyFlourish.Echoes, ready) };
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Spawn, PlayerAnimation.Flourish, PlayerAnimation.Turn,
                PlayerAnimation.Morph, PlayerAnimation.Unmorph, PlayerAnimation.JumpNeutral, PlayerAnimation.LandNeutral,
                PlayerAnimation.LandLeft, PlayerAnimation.LandRight })
            {
                m.Set(a, ready, true);
            }
            bool uncurl = body.Clip(Uncurl) != null;
            if (uncurl)
            {
                m.Set(PlayerAnimation.Unmorph, Uncurl, false);
                m.Fit.Add(PlayerAnimation.Unmorph);
                m.FadeOut[Uncurl] = UncurlFade;
            }
            m.Set(PlayerAnimation.WalkForward, "B_glideForward_dark_samus", true);
            m.Set(PlayerAnimation.WalkBackward, "B_glideBackwards_dark_samus", true);
            m.Set(PlayerAnimation.WalkLeft, "B_glideLeft_dark_samus", true);
            m.Set(PlayerAnimation.WalkRight, "B_glideRight_dark_samus", true);
            m.Lean = ("B_glideForward_dark_samus", "B_glideBackwards_dark_samus", "B_glideLeft_dark_samus", "B_glideRight_dark_samus");
            m.Set(PlayerAnimation.JumpForward, "B_glideForward_dark_samus", true);
            m.Set(PlayerAnimation.JumpBack, "B_glideBackwards_dark_samus", true);
            m.Set(PlayerAnimation.JumpLeft, "B_glideLeft_dark_samus", true);
            m.Set(PlayerAnimation.JumpRight, "B_glideRight_dark_samus", true);
            // the torso layer (MphRead plays these on biped2 only): a hit by where it came from (Retro's L/R name the side
            // she's knocked to), her missile barrage as the shot's recoil, her boost-ball charge (Metroid5.pak)
            m.TorsoRoot = "Spine_1";
            m.SetTorso(PlayerAnimation.Shoot, "A_MissileBarrage_dark_samus", false);
            m.SetTorso(PlayerAnimation.Charge, "A_BoostBall_ChargeReady_dark_samus", true);
            m.SetTorso(PlayerAnimation.ChargeShoot, "A_BoostBall_ChargeShot_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageFront, "R_heavyHitF_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageBack, "R_heavyHitB_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageLeft, "R_heavytHitR_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageRight, "R_heavyHitL_dark_samus", false);
            if (HoldOf(body) is Hold h) m.AfterPose = (b, who) => h.Apply(b, who, 1 - m.FlourishWeight);
            Vanilla(m, ownUnmorph: uncurl);
            return m;
        }

        // Corruption: 4-way float leans with their own into / out-of clips; her idle floats (DarkSamusFloat: our own float
        // made from her b_ready + her side leans' hanging legs; owner 2026-10-11), else her planted b_ready
        static BodyClips Corruption(NativeBody body)
        {
            string ready = DarkSamusFloat.Make(body, "b_ready_dark_samus", "b_float_right_c_dark_samus", "b_float_right_c_dark_samus",
                "b_float_forward_c_dark_samus", "b_float_forward_c_dark_samus") ?? "b_ready_dark_samus";
            var m = new BodyClips { Idle = ready, Lift = CorruptionLift, FlourishSet = BodyFlourish.Prepare(body, BodyFlourish.Corruption, ready) };
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Spawn, PlayerAnimation.Flourish, PlayerAnimation.Turn,
                PlayerAnimation.Morph, PlayerAnimation.Unmorph, PlayerAnimation.LandNeutral, PlayerAnimation.LandLeft, PlayerAnimation.LandRight })
            {
                m.Set(a, ready, true);
            }
            m.Set(PlayerAnimation.WalkForward, "b_float_forward_c_dark_samus", true);
            m.Set(PlayerAnimation.WalkBackward, "b_float_back_c_dark_samus", true);
            m.Set(PlayerAnimation.WalkLeft, "b_float_left_c_dark_samus", true);
            m.Set(PlayerAnimation.WalkRight, "b_float_right_c_dark_samus", true);
            m.Lean = ("b_float_forward_c_dark_samus", "b_float_back_c_dark_samus", "b_float_left_c_dark_samus", "b_float_right_c_dark_samus");
            // the torso layer: her one blaster stance for every shot (no kick in her set), its steady loop while charging;
            // no hit-from-behind clip, so the front one serves both
            m.TorsoRoot = "Spine_1";
            m.SetTorso(PlayerAnimation.Shoot, "a_mega_blaster_c_dark_samus", false);
            m.SetTorso(PlayerAnimation.Charge, "a_mega_blaster_c_steady_dark_samus", true);
            m.SetTorso(PlayerAnimation.ChargeShoot, "a_mega_blaster_c_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageFront, "r_heavy_hit_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageBack, "r_heavy_hit_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageLeft, "r_hit_right_dark_samus", false);
            m.SetTorso(PlayerAnimation.DamageRight, "r_hit_left_dark_samus", false);
            if (HoldOf(body) is Hold h) m.AfterPose = (b, who) => h.Apply(b, who, 1 - m.FlourishWeight);
            Vanilla(m, ownUnmorph: false); // her own ball clips (b_morphball_2_hover) are multi-part: not imported yet
            return m;
        }

        // her cannon tip (a locator on the forearm): Echoes GUN_Particle_LCTR, Corruption Gun_LCTR
        public static string TipOf(NativeBody body) => body.Bone("GUN_Particle_LCTR") >= 0 ? "GUN_Particle_LCTR" : "Gun_LCTR";

        // the D14 hold from her own aiming frame: Echoes A_MissileBarrage t=0 (arm straight out at shoulder height, 12 deg
        // off ahead before truing), Corruption a_mega_blaster_c_steady t=0 (7 deg off); null for another body
        public static Hold? HoldOf(NativeBody body) =>
            body.Clip("A_MissileBarrage_dark_samus") != null ? Hold.For(body, "A_MissileBarrage_dark_samus", 0, TipOf(body))
            : body.Clip("a_mega_blaster_c_steady_dark_samus") != null ? Hold.For(body, "a_mega_blaster_c_steady_dark_samus", 0, TipOf(body))
            : null;

        // the cannon arm held along the aim (D14)
        public sealed class Hold
        {
            readonly int _shoulder, _elbow, _wrist;
            readonly Matrix4 _shoulderRot;            // R_shoulder's held model-space rotation, trued to point the cannon ahead
            readonly Matrix4 _elbowLocal, _wristLocal;
            public readonly float OffDegrees;         // how far off ahead the held frame's cannon pointed before truing

            Hold(int shoulder, int elbow, int wrist, Matrix4 shoulderRot, Matrix4 elbowLocal, Matrix4 wristLocal, float off)
            {
                _shoulder = shoulder;
                _elbow = elbow;
                _wrist = wrist;
                _shoulderRot = shoulderRot;
                _elbowLocal = elbowLocal;
                _wristLocal = wristLocal;
                OffDegrees = off;
            }

            // from the body's own clip at time t (poses the shared body: call before any seat animates it); null when a
            // bone or the clip is missing (she's then drawn as the clips have her)
            public static Hold? For(NativeBody body, string clip, float t, string tip)
            {
                int s = body.Bone("R_shoulder"), e = body.Bone("R_elbow"), w = body.Bone("R_wrist"), k = body.Bone(tip);
                if (s < 0 || e < 0 || k < 0 || body.Clip(clip) is not HdGun.Anim a) return null;
                body.Pose(a, t);
                Matrix4 rot = body.World[s].ClearTranslation();
                Matrix4 elbow = body.World[e] * body.World[s].Inverted();
                Matrix4 wrist = w >= 0 ? body.World[w] * body.World[e].Inverted() : Matrix4.Identity;
                Vector3 dir = body.World[k].Row3.Xyz - body.World[e].Row3.Xyz;
                if (dir.LengthSquared < 1e-10f) return null;
                dir.Normalize();
                float off = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(dir.Z, -1f, 1f)));
                return new Hold(s, e, w, rot * Turn(dir, Vector3.UnitZ), elbow, wrist, off);
            }

            // after each pose: the arm on the aim (who null: a standing body, aimed level); while she uncurls from the ball
            // (her own unmorph) the hold eases in over the clip's second half, so the cannon isn't stuck out of the ball
            public void Apply(NativeBody body, PlayerEntity? who) => Apply(body, who, 1);

            // ... by how much (1 = held, 0 = her own arm: let go while her flourish plays, BodyClips.FlourishWeight)
            public void Apply(NativeBody body, PlayerEntity? who, float release)
            {
                float weight = release;
                if (who != null && (PlayerAnimation)who.HostBipedModel1.AnimInfo.Index[0] == PlayerAnimation.Unmorph)
                {
                    AnimationInfo a = who.HostBipedModel1.AnimInfo;
                    float t = a.FrameCount[0] > 0 ? (float)a.Frame[0] / a.FrameCount[0] : 1;
                    weight *= Math.Clamp(2 * t - 1, 0, 1);
                }
                Apply(body, who?.FacingVector.Y ?? 0, weight);
            }

            // ... aimed at elevation aimY (the facing vector's y), mixed over the clip's own arm by weight
            public void Apply(NativeBody body, float aimY, float weight = 1)
            {
                if (weight <= 0) return;
                Matrix4 world = _shoulderRot * Pitch(aimY);
                Matrix4 elbow = _elbowLocal, wrist = _wristLocal;
                if (weight < 1)
                {
                    world = Mix(body.World[_shoulder].ClearTranslation(), world, weight);
                    elbow = Mix(body.World[_elbow] * body.World[_shoulder].Inverted(), elbow, weight);
                    if (_wrist >= 0) wrist = Mix(body.World[_wrist] * body.World[_elbow].Inverted(), wrist, weight);
                }
                world.Row3 = body.World[_shoulder].Row3;
                body.SetWorld(_shoulder, world);
                body.SetWorld(_elbow, elbow * body.World[_shoulder]);
                if (_wrist >= 0) body.SetWorld(_wrist, wrist * body.World[_elbow]);
            }

            // rigid transforms a -> b by w: rotation slerped, offset lerped
            static Matrix4 Mix(Matrix4 a, Matrix4 b, float w)
            {
                Matrix4 m = Matrix4.CreateFromQuaternion(Quaternion.Slerp(a.ExtractRotation(), b.ExtractRotation(), w));
                m.Row3 = new Vector4(Vector3.Lerp(a.Row3.Xyz, b.Row3.Xyz, w), 1);
                return m;
            }

            // the cannon arm's direction now (elbow to tip, model space): for tests
            public static Vector3 Aim(NativeBody body, string tip)
            {
                Vector3 d = body.World[body.Bone(tip)].Row3.Xyz - body.World[body.Bone("R_elbow")].Row3.Xyz;
                return d.LengthSquared > 0 ? d.Normalized() : d;
            }

            // MPH's aim bend (PlayerDraw: Spine_1 turned by the facing's elevation, |y| clamped to Fixed 2896), about the
            // body's own left axis (+X): forward (+Z) goes up for a positive elevation. Row vectors, as OpenTK.
            public static Matrix4 Pitch(float y)
            {
                const float limit = 2896 / 4096f;
                float sin = y, cos = MathF.Sqrt(MathF.Max(0, 1 - y * y));
                if (MathF.Abs(y) > limit)
                {
                    cos = limit;
                    sin = y <= 0 ? -limit : limit;
                }
                var m = Matrix4.Identity;
                m.Row1 = new Vector4(0, cos, -sin, 0);
                m.Row2 = new Vector4(0, sin, cos, 0);
                return m;
            }

            // the shortest turn taking direction a onto b (unit vectors), for row vectors (v * M)
            public static Matrix4 Turn(Vector3 a, Vector3 b)
            {
                Vector3 axis = Vector3.Cross(a, b);
                float s = axis.Length, c = Vector3.Dot(a, b);
                if (s < 1e-6f) return Matrix4.Identity; // already along it (her held frames are never backwards)
                axis /= s;
                float t = 1 - c, x = axis.X, y = axis.Y, z = axis.Z;
                return new Matrix4(
                    c + t * x * x, t * x * y + s * z, t * x * z - s * y, 0,
                    t * x * y - s * z, c + t * y * y, t * y * z + s * x, 0,
                    t * x * z + s * y, t * y * z - s * x, c + t * z * z, 0,
                    0, 0, 0, 1);
            }
        }
    }
}
