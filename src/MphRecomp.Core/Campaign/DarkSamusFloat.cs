using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Dark Samus's Corruption body has no floating idle (her b_ready is a planted wide stance; owner 2026-10-11: float like
    // her Echoes idle, ship-safe). This builds one at load, from her own Corruption clips only, plus our own motion:
    //   - upper body: her b_ready loop (breathing, arms)
    //   - legs: hanging legs from her float leans (DarkSamusBody picks: left from b_float_right_c, right from
    //     b_float_forward_c; her left/right mix tucked one leg up like a knee kick)
    //   - our motion on top (sine curves, numbers chosen by eye): a slow bob, a forward lean that deepens as she rises, the
    //     head kept level, the toes flexing a beat behind the bob, a slow side drift
    //   - her feet stay where they hang while the pelvis bobs (the knees take it: two-bone IK), and the pelvis sits Drop
    //     lower than the leans hold it, so the knees bend deeper
    // The Echoes float was only looked at for broad numbers (a ~1 s cycle, a few cm of bob with the feet still, the pelvis
    // ~1.03 above the floor with the toe tips ~0.03 into it, knees bent with toes pointed, a forward lean); no Echoes data
    // is read or needed here. Her deepest toe sits as deep as her ready clip's deepest (toe tips just into the floor, owner
    // 2026-10-08 and 2026-10-11: "the same position echoes does"), so BodyClips.Lift still holds.
    // On = the float; off = the clip carries her b_ready frames (the gun-adjust pause's Y switches it live, for judging).
    public static class DarkSamusFloat
    {
        public const string Clip = "float_idle_secondhunt";

        // the motion (MPH units, degrees, seconds)
        const float Cycle = 1.0f;           // one bob; her b_ready loop is 1.0 s too, so the breathing stays in step
        const float Length = 2 * Cycle;     // the clip: two bobs and one slow drift
        const float Bob = 0.03f;            // up and down from the middle
        // "lags" below are in bob cycles: how long after the bob's top a swing peaks (< 0 = ahead of it)
        const float Lean = 7f, LeanBob = 4f, LeanLag = -0.12f; // extra forward lean of the spine, and its swing (peaks just before the top)
        const float NeckBob = 2f, NeckLag = -0.1f; // the neck pushing the head forward and back with it
        const float ToesL = 5f, ToesLLag = -0.31f, ToesR = 0f, ToesRLag = 0f; // each foot flexing (the front one; the back one stays)
        const float FootRollL = 4f, FootRollLLag = 0.53f, FootRollR = 4f, FootRollRLag = -0.38f; // each foot rocking side to side
        const float Drift = 0.006f, Roll = 1.2f; // slow side drift of the pelvis and roll of the spine (over Length)
        // the legs out of step (owner 2026-10-11: in step she looks like she bobs on her tiptoes): the front (left) foot
        // rises and falls FrontLift (full range) a beat ahead of the bob, the back (right) foot barely lifts, on the
        // opposite beat; both feet swing a little fore and aft, the back one later
        const float FrontLift = 0.085f, FrontLead = 0.21f, FrontBase = 0.03f, BackLift = 0.012f;
        const float FootSwing = 0.013f, FrontSwingLag = 0.04f, BackSwingLag = 0.15f;
        const float Drop = -0.05f;             // the pelvis lowered (raised when < 0) over her pinned feet

        // the pose, in rounded degrees chosen to look like her Echoes float (owner 2026-10-11: "even closer"; broad angles
        // only, no curves): the body turned, the head pushed forward over the chest with the visor tipped down, and each
        // limb segment's direction -- pitch forward of straight down, side toward her left (+X). Her right arm is the
        // cannon arm (DarkSamusBody's D14 hold sets it); her feet: Corruption's ankle-to-toe joint sits ~12 deg further
        // forward on the foot than Echoes', so its foot angles read 12 more
        const float PelvisYaw = 21f, PelvisRoll = -4f, PelvisPitch = 4f, SpineRoll = 12f;
        const float NeckJut = 58f, HeadPitch = 24f;
        // her collarbones, out to the shoulders: pitch down from level, forward of straight out to that side (the right
        // one carries the cannon arm; DarkSamusBody's hold aims it from wherever the shoulder ends up)
        static readonly (string From, string To, float Pitch, float Side, float Out)[] Clavicles =
            { ("L_collar", "L_shoulder", -9, 7, 1), ("R_collar", "R_shoulder", 24, 28, -1) };
        // ... and how each swings with the bob: pitch swing + lag, side swing + lag (the free left arm swings loosely,
        // each segment on its own beat; the legs' swing comes from their feet moving, below)
        static readonly (string From, string To, float Pitch, float Side, float PitchSwing, float PitchLag, float SideSwing, float SideLag)[] Limbs =
        {
            ("L_hip", "L_knee", 62, 9, 0, 0, 0, 0), ("L_knee", "L_ankle", -24, -9, 0, 0, 0, 0), ("L_ankle", "L_ball", 0, -2, 0, 0, 0, 0),
            ("R_hip", "R_knee", 48, -13, 0, 0, 0, 0), ("R_knee", "R_ankle", -43, -1, 0, 0, 0, 0), ("R_ankle", "R_ball", 4, 1, 0, 0, 0, 0),
            ("L_shoulder", "L_elbow", -15, 46, 3.5f, 0.27f, 3.5f, -0.2f), ("L_elbow", "L_wrist", 61, 0, 5, 0.48f, 4, 0.16f),
            ("L_wrist", "L_middle_1", 41, 1, 4.5f, 0.53f, 6, 0.24f),
        };

        static bool _on = true;
        static readonly List<(HdGun.Anim Clip, Frames Float, Frames Ready)> _made = new();

        readonly record struct Frames(float[] Data, int Keys, float Tick, float Duration);

        public static bool On => _on;
        public static bool Made { get { lock (_made) return _made.Count > 0; } }

        // live switch: every body that has the clip swaps its frames (the animators hold the same Anim object)
        public static void Set(bool on)
        {
            lock (_made)
            {
                _on = on;
                foreach ((HdGun.Anim a, Frames f, Frames r) in _made) Use(a, on ? f : r);
            }
        }

        static void Use(HdGun.Anim a, Frames f)
        {
            a.Frames = f.Data;
            a.Keys = f.Keys;
            a.Tick = f.Tick;
            a.Duration = f.Duration;
        }

        // the float clip's name, added to the body's clips; null when one of her clips or bones is missing (she then
        // idles in `ready` as before)
        public static string? Make(NativeBody body, string ready, string legsA, string legsB) => Make(body, ready, legsA, legsB, legsA, legsB);

        // ... each leg its own pair of clips, mixed half and half
        public static string? Make(NativeBody body, string ready, string leftA, string leftB, string rightA, string rightB)
        {
            HdGun g = body.Data;
            if (body.Clip(Clip) != null) return Clip; // made already (the map is asked again for the same body)
            if (g.KeyStride != 7 || body.Clip(ready) is not HdGun.Anim r || body.Clip(leftA) is not HdGun.Anim la
                || body.Clip(leftB) is not HdGun.Anim lb || body.Clip(rightA) is not HdGun.Anim ra || body.Clip(rightB) is not HdGun.Anim rb
                || r.Duration <= 0) return null;
            int pelvis = body.Bone("Skeleton_Root"), spine = body.Bone("Spine_1"), neck = body.Bone("Neck_1");
            int lHip = body.Bone("L_hip"), rHip = body.Bone("R_hip"), lKnee = body.Bone("L_knee"), rKnee = body.Bone("R_knee");
            int lAnkle = body.Bone("L_ankle"), rAnkle = body.Bone("R_ankle");
            if (Math.Min(Math.Min(Math.Min(pelvis, spine), Math.Min(neck, lHip)), Math.Min(Math.Min(rHip, lKnee), Math.Min(rKnee, Math.Min(lAnkle, rAnkle)))) < 0)
            {
                return null;
            }
            int head = body.Bone("Head_1");

            var limbs = new List<(int From, int To, int Row)>();
            for (int li = 0; li < Limbs.Length; li++)
            {
                if (body.Bone(Limbs[li].From) is int fi && fi >= 0 && body.Bone(Limbs[li].To) is int ti && ti >= 0) limbs.Add((fi, ti, li));
            }
            int n = g.BoneCount;
            var legs = new int[n]; // 1 = left leg, 2 = right leg
            legs[lHip] = 1;
            legs[rHip] = 2;
            foreach (int i in g.Order) if (g.Parent[i] >= 0 && legs[g.Parent[i]] > 0) legs[i] = legs[g.Parent[i]];

            const float tick = 1 / 30f;
            int keys = (int)MathF.Round(Length / tick) + 1;
            var data = new float[keys * n * 7];
            var rot = new Quaternion[n];
            var trans = new Vector3[n];
            var world = new Matrix4[n];
            float unit = 1 / body.Scale; // MPH units -> hers
            // where her feet hang (her ready pose's first frame with the legs): they stay there while she bobs
            Matrix4? lFoot = null, rFoot = null;
            for (int k = 0; k < keys; k++)
            {
                float t = k * tick;
                float bob = MathF.Sin(MathF.Tau * t / Cycle);
                float Behind(float lag) => MathF.Sin(MathF.Tau * (t / Cycle - lag));
                float slow = MathF.Sin(MathF.Tau * t / Length);
                for (int i = 0; i < n; i++)
                {
                    if (legs[i] > 0)
                    {
                        g.Sample(legs[i] == 1 ? la : ra, 0, i, out Quaternion qa, out Vector3 pa);
                        g.Sample(legs[i] == 1 ? lb : rb, 0, i, out Quaternion qb, out Vector3 pb);
                        rot[i] = Quaternion.Slerp(qa, qb, 0.5f);
                        trans[i] = Vector3.Lerp(pa, pb, 0.5f);
                    }
                    else g.Sample(r, t % r.Duration, i, out rot[i], out trans[i]);
                }
                Build(g, rot, trans, world);
                // +Y turns face her toward her left, +Z ones roll her top toward her right; +X turns put a bone's top
                // forward (+Z) and its bottom back: the spine leans in, the neck pushes the head forward, the toes point down
                Turn(g, pelvis, Matrix4.CreateRotationX(Rad(PelvisPitch)) * Matrix4.CreateRotationZ(Rad(PelvisRoll)) * Matrix4.CreateRotationY(Rad(PelvisYaw)), rot, trans, world);
                float lean = Lean + LeanBob * Behind(LeanLag);
                Turn(g, spine, Matrix4.CreateRotationX(Rad(lean)) * Matrix4.CreateRotationZ(Rad(SpineRoll + Roll * slow)), rot, trans, world);
                Turn(g, neck, Matrix4.CreateRotationX(Rad(NeckJut + NeckBob * Behind(NeckLag))), rot, trans, world);
                if (head >= 0)
                {
                    Vector3 hf = Vector3.TransformVector(Vector3.UnitZ, world[head].ClearTranslation().ClearScale());
                    Turn(g, head, Matrix4.CreateRotationX(Rad(HeadPitch) - MathF.Atan2(-hf.Y, hf.Z)), rot, trans, world);
                }
                foreach ((string from, string to, float pitch, float side, float outX) in Clavicles)
                {
                    int c = body.Bone(from), sh = body.Bone(to);
                    if (c < 0 || sh < 0) continue;
                    TurnOnto(g, c, world[sh].ExtractTranslation() - world[c].ExtractTranslation(), Across(pitch, side, outX), rot, trans, world);
                }
                foreach ((int from, int to, int row) in limbs)
                {
                    var l = Limbs[row];
                    float p = l.Pitch + l.PitchSwing * Behind(l.PitchLag), sd = l.Side + l.SideSwing * Behind(l.SideLag);
                    TurnOnto(g, from, world[to].ExtractTranslation() - world[from].ExtractTranslation(), Dir(p, sd), rot, trans, world);
                }
                lFoot ??= world[lAnkle];
                rFoot ??= world[rAnkle];
                // the pelvis lowered, bobbing and drifting in model space, the feet kept where they hang, the toes flexing
                Move(g, pelvis, new Vector3(Drift * slow, Bob * bob - Drop, 0) * unit, rot, trans, world);
                // each foot's hang spot moved on its own beat (lowest at the spot, so the toe tips stay where they were)
                float front = 0.5f * (1 + Behind(-FrontLead));
                Vector3 lMove = new Vector3(0, FrontBase + FrontLift * front, FootSwing * Behind(FrontSwingLag)) * unit;
                Vector3 rMove = new Vector3(0, BackLift * (1 - front), FootSwing * Behind(BackSwingLag)) * unit;
                Pin(g, lHip, lKnee, lAnkle, lFoot.Value * Matrix4.CreateTranslation(lMove), rot, trans, world);
                Pin(g, rHip, rKnee, rAnkle, rFoot.Value * Matrix4.CreateTranslation(rMove), rot, trans, world);
                Turn(g, lAnkle, Matrix4.CreateRotationX(Rad(ToesL * Behind(ToesLLag))) * Matrix4.CreateRotationZ(Rad(FootRollL * Behind(FootRollLLag))), rot, trans, world);
                Turn(g, rAnkle, Matrix4.CreateRotationX(Rad(ToesR * Behind(ToesRLag))) * Matrix4.CreateRotationZ(Rad(FootRollR * Behind(FootRollRLag))), rot, trans, world);
                for (int i = 0; i < n; i++)
                {
                    int o = (k * n + i) * 7;
                    data[o] = rot[i].X;
                    data[o + 1] = rot[i].Y;
                    data[o + 2] = rot[i].Z;
                    data[o + 3] = rot[i].W;
                    data[o + 4] = trans[i].X;
                    data[o + 5] = trans[i].Y;
                    data[o + 6] = trans[i].Z;
                }
            }
            var a = new HdGun.Anim { Name = Clip, Keys = keys, Tick = tick, Duration = (keys - 1) * tick, Frames = data };

            // her deepest point onto her ready clip's deepest (the toes the map's Lift was tuned on)
            float want = Single.MaxValue, low = Single.MaxValue;
            for (int k = 0; k < r.Keys; k += 2) want = Math.Min(want, Lowest(body, r, k * r.Tick));
            for (int k = 0; k < keys; k += 3) low = Math.Min(low, Lowest(body, a, k * tick));
            if (low < Single.MaxValue && want < Single.MaxValue)
            {
                Vector3 up = Vector3.TransformVector(Vector3.UnitY * (want - low), ParentRotation(g, a, pelvis).Inverted());
                for (int k = 0; k < keys; k++)
                {
                    int o = (k * n + pelvis) * 7;
                    data[o + 4] += up.X;
                    data[o + 5] += up.Y;
                    data[o + 6] += up.Z;
                }
            }
            g.Anims[Clip] = a;
            var f = new Frames(data, keys, tick, a.Duration);
            var rf = new Frames(r.Frames, r.Keys, r.Tick, r.Duration);
            lock (_made)
            {
                _made.Add((a, f, rf));
                if (!_on) Use(a, rf);
            }
            return Clip;
        }

        static float Rad(float deg) => MathHelper.DegreesToRadians(deg);

        // a hanging segment's direction (model space) from its pitch forward of straight down and its side toward +X
        static Vector3 Dir(float pitch, float side)
        {
            float p = Rad(pitch), s = Rad(side);
            return new Vector3(MathF.Sin(s), -MathF.Cos(s) * MathF.Cos(p), MathF.Cos(s) * MathF.Sin(p));
        }

        // a segment reaching out to one side (outX +1 = her left, -1 = her right): its pitch down from level and its side
        // forward (+Z) of straight out
        static Vector3 Across(float pitch, float side, float outX) => new Vector3(outX, -MathF.Tan(Rad(pitch)), MathF.Tan(Rad(side))).Normalized();

        // world matrices from local poses (NativeBody.BuildWorld's)
        static void Build(HdGun g, Quaternion[] rot, Vector3[] trans, Matrix4[] world)
        {
            foreach (int i in g.Order)
            {
                Matrix4 local = Matrix4.CreateFromQuaternion(rot[i]) * Matrix4.CreateTranslation(trans[i]);
                world[i] = g.Parent[i] >= 0 ? local * world[g.Parent[i]] : local;
            }
        }

        static Matrix4 ParentWorldRotation(HdGun g, int bone, Matrix4[] world) =>
            g.Parent[bone] >= 0 ? world[g.Parent[bone]].ClearTranslation().ClearScale() : Matrix4.Identity;

        // a bone turned by r in model space about its own position (row vectors: world' = world * r), kept as a local pose
        static void Turn(HdGun g, int bone, Matrix4 r, Quaternion[] rot, Vector3[] trans, Matrix4[] world)
        {
            Matrix4 p = ParentWorldRotation(g, bone, world);
            Matrix4 local = Matrix4.CreateFromQuaternion(rot[bone]) * p * r * p.Inverted();
            rot[bone] = local.ExtractRotation().Normalized();
            Build(g, rot, trans, world);
        }

        // turn a bone about its own position so direction `from` (model space) points along `to`
        static void TurnOnto(HdGun g, int bone, Vector3 from, Vector3 to, Quaternion[] rot, Vector3[] trans, Matrix4[] world)
        {
            if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f) return;
            from.Normalize();
            to.Normalize();
            Vector3 axis = Vector3.Cross(from, to);
            if (axis.LengthSquared < 1e-12f) return;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1, 1));
            Turn(g, bone, Matrix4.CreateFromAxisAngle(axis.Normalized(), angle), rot, trans, world);
        }

        // a leg's end put back at target (model space, its turn too), two-bone IK bending the knee the way it bends now
        // (NativeBody.PinLimb's)
        static void Pin(HdGun g, int hip, int knee, int end, Matrix4 target, Quaternion[] rot, Vector3[] trans, Matrix4[] world)
        {
            Vector3 s = world[hip].ExtractTranslation(), e = world[knee].ExtractTranslation();
            Vector3 w = world[end].ExtractTranslation(), t = target.ExtractTranslation();
            float a = (e - s).Length, b = (w - e).Length;
            Vector3 st = t - s;
            float d = st.Length;
            if (d < 1e-5f || a < 1e-5f || b < 1e-5f) return;
            Vector3 dir = st / d;
            float dc = Math.Clamp(d, MathF.Abs(a - b) + 1e-4f, a + b - 1e-4f);
            float along = (a * a - b * b + dc * dc) / (2 * dc);
            float h = MathF.Sqrt(MathF.Max(0, a * a - along * along));
            Vector3 pole = e - s - Vector3.Dot(e - s, dir) * dir;
            if (pole.LengthSquared < 1e-10f) pole = Vector3.Cross(dir, Vector3.UnitY);
            if (pole.LengthSquared < 1e-10f) pole = Vector3.UnitX;
            pole.Normalize();
            Vector3 e2 = s + dir * along + pole * h;
            TurnOnto(g, hip, e - s, e2 - s, rot, trans, world);
            w = world[end].ExtractTranslation();
            TurnOnto(g, knee, w - e2, s + dir * dc - e2, rot, trans, world);
            Matrix4 p = ParentWorldRotation(g, end, world);
            rot[end] = (target.ClearTranslation().ClearScale() * p.Inverted()).ExtractRotation().Normalized();
            Build(g, rot, trans, world);
        }

        // a bone moved by d in model space
        static void Move(HdGun g, int bone, Vector3 d, Quaternion[] rot, Vector3[] trans, Matrix4[] world)
        {
            trans[bone] += Vector3.TransformVector(d, ParentWorldRotation(g, bone, world).Inverted());
            Build(g, rot, trans, world);
        }

        // the bone's parent's model-space turn at the clip's first key
        static Matrix4 ParentRotation(HdGun g, HdGun.Anim a, int bone)
        {
            Matrix4 m = Matrix4.Identity;
            for (int i = g.Parent[bone]; i >= 0; i = g.Parent[i])
            {
                g.Sample(a, 0, i, out Quaternion q, out _);
                m *= Matrix4.CreateFromQuaternion(q);
            }
            return m;
        }

        // the posed, skinned mesh's lowest y (model space) in clip a at t
        static float Lowest(NativeBody body, HdGun.Anim a, float t)
        {
            body.Pose(a, t);
            float lo = Single.MaxValue;
            float[] pal = Array.Empty<float>();
            for (int m = 0; m < body.Data.Materials.Length; m++)
            {
                HdGun.Material mat = body.Data.Materials[m];
                if (pal.Length < mat.Palette.Length * 16) pal = new float[mat.Palette.Length * 16];
                body.FillPalette(m, pal);
                for (int v = 0; v < mat.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    float x = mat.Vertices[o], y = mat.Vertices[o + 1], z = mat.Vertices[o + 2], py = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = mat.Vertices[o + 15 + k];
                        if (w <= 0) continue;
                        int b = (int)mat.Vertices[o + 11 + k] * 16;
                        py += w * (x * pal[b + 1] + y * pal[b + 5] + z * pal[b + 9] + pal[b + 13]);
                    }
                    lo = Math.Min(lo, py);
                }
            }
            return lo;
        }
    }
}
