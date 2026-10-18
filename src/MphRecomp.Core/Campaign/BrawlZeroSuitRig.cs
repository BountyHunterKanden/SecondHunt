using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead;
using MphRead.Entities;
using MphRecomp.Anim;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // What Brawl Zero Suit (BrawlZeroSuitBody) does to every pose after its clips (BodyClips.AfterPose), owner 2026-10-15:
    // "we need to get her gun facing where she is aiming like samus always is" and her hair (and chest) moving as Brawl
    // moves them.
    //  - Aim: her gun arm held on the aim in every state -- the clip's arm (Samus's arm cannon hold) turned at the shoulder
    //    so the forearm lies along the aim, then the hand turns so the Paralyzer's barrel lies along the aim, its top up; a
    //    shot kicks it up for a moment (MphRead's biped2 Shoot / ChargeShoot frame). The rest of her body is the clip's.
    //  - Springs: her ponytail, side locks and chest follow the pose through damped springs driven by how the body moves
    //    in the WORLD (the seat's position and facing): turns swing the hair, running streams it back, jumps and landings
    //    bounce it. Brawl's own ponytail bones hold one pose in every clip (it simulates them live), so this is ours. Her
    //    fighter has no chest bones: the body build adds LBreastN / RBreastN (Tools ZeroSuitBrawl.AddChest); they move
    //    (shift), they don't turn, so the bounce shows.
    //  - Narrow (on Samus's clips, BrawlZeroSuitBody.Narrowed), owner 2026-10-15: "use samuss vanilla rig, but adjust it so
    //    that the body parts sit relative where they should be ... the armor takes up more space than regular limbs ... so
    //    the legs and such were further apart". Her thighs, off upper arm and forearm turn in toward her middle by how much
    //    wider Samus's clips hold them than a human does in the same moves (measured on her skeleton against the CC0 human
    //    set, -zsbrawl pose: thighs 15-17 deg out in Samus's idle vs 8-11, 2-7 vs ~0 running; off upper arm ~33 vs ~20;
    //    forearm ~17 vs ~0), in her pelvis's / chest's own frame (so the aim twist doesn't skew it); then the feet are put
    //    back down on the floor. The motion stays Samus's.
    //  - Straighten (Samus's walks: run forward / back and strafes), owner 2026-10-15 16:5x: "for the running animation there
    //    is still awkward twisting/side to side movement. it needs to look just like legs straight forward, knees bending,
    //    feet landing on steps like the vanilla rigs do, no side to side movement, no twisting". Samus's run swings her hips
    //    -12..+19 degrees (her walk back 29..44) and her ankles wander ~1.2 across; the CC0 human jog on her skeleton keeps
    //    the hips square and each ankle on one line (her middle +-0.95). So her hips are squared to her facing (the upper
    //    body kept as the clip turns it) and each leg is laid by two-bone IK with no roll from her bind: the knee bends
    //    straight ahead, the foot points straight ahead at the clip's pitch, each ankle where the clip steps it -- on its
    //    line running forward and back.
    // Narrow and Straighten are baked into her clips at load (Bake: BrawlZeroSuitBody's "dsn:" / "dss:" clips), so her
    // own Brawl idle and every blend between clips are left alone; AfterPose only does Aim and Springs.
    public sealed class BrawlZeroSuitRig
    {
        // dev, live (the gun-adjust pause: CampaignRenderer ZeroSuitToggle)
        public static bool Aim = true, Springs = true;
        // dev, live (the gun-adjust pause): her ponytail kept off her back, neck and head (Clear)
        public static bool Hair = true;
        // dev, live (the gun-adjust pause, A): her walks straightened (Straighten, default) or Samus's taken in
        public static bool Straight = true;
        // dev, live (the gun-adjust pause): her torso upright in the idle and the walks (Upright)
        public static bool Upright = true;
        // Straighten: each ankle's line running forward and back, across from her middle (the CC0 jog: 0.90..1.01)
        const float Track = 0.95f;
        // Straighten: the hips' tilt allowed (degrees)
        const float HipTilt = 5;
        const float VelSmooth = 0.045f;   // seconds: how the measured speed is smoothed before it drives the springs

        // Narrow: degrees taken off each limb's outward angle (less where it is only a little out: it never crosses in)
        public const float ThighIn = 7, UpperArmIn = 12, ForearmIn = 9;
        readonly int _hip, _bust, _top, _spine, _neck, _head;
        readonly (int Joint, int Child)[] _thighs, _arm;
        readonly (int Thigh, int Knee, int Foot)[] _legs;
        readonly int[] _feet;

        readonly int _shoulder, _elbow, _hand, _have;
        readonly Vector3 _barrel, _gunTop;   // the Paralyzer's barrel and its top, in RHaveN's own frame (from its vertices)
        readonly Vector3? _muzzle;   // the middle of the barrel's front end, in RHaveN's own frame

        sealed class Chain
        {
            public int[] Bones = Array.Empty<int>();
            public float Omega, Damping, Gain, Drag, MaxFrac;
            public bool Shift;   // moves the bone by its tip's offset instead of turning it (a chest bone: no length to swing)
            public float[]? Clear;   // kept off her body (the ponytail): each bone's hair thickness round its line
        }

        readonly List<Chain> _chains = new();

        sealed class Sim
        {
            public long Stamp;
            public bool Ready;
            public Vector3[] X = Array.Empty<Vector3>(), V = Array.Empty<Vector3>();   // tip offsets from the pose (world) and their speed
            public Vector3[] TipPrev = Array.Empty<Vector3>(), TipVel = Array.Empty<Vector3>();
        }

        readonly Dictionary<int, Sim> _sims = new();
        readonly BrawlZeroSuitFaces? _faces;   // her face sets (null: an older body with one face)
        readonly int[] _gunArm;   // RShoulderJ and every bone under it (the aim's, eased off for the flourish)
        Matrix4[] _armSave = Array.Empty<Matrix4>();
        readonly Dictionary<int, Vector3> _tipLocal = new();   // a one-bone chain's tip, in the bone's frame
        int _points;

        public BrawlZeroSuitRig(NativeBody body)
        {
            _shoulder = body.Bone("RShoulderJ");
            _elbow = body.Bone("RArmJ");
            _hand = body.Bone("RHandN");
            _have = body.Bone("RHaveN");
            (_barrel, _gunTop) = GunAxes(body, _have);
            _muzzle = GunFront(body, _have, _barrel);
            _hip = body.Bone("HipN");
            _bust = body.Bone("BustN");
            _top = body.Bone("TopN");
            _spine = body.Bone("BodyN");
            _neck = body.Bone("NeckN");
            _head = body.Bone("HeadN");
            _thighs = new[] { (body.Bone("LLegJ"), body.Bone("LKneeJ")), (body.Bone("RLegJ"), body.Bone("RKneeJ")) };
            _arm = new[] { (body.Bone("LShoulderJ"), body.Bone("LArmJ")), (body.Bone("LArmJ"), body.Bone("LHandN")) };
            _feet = new[] { body.Bone("LFootJ"), body.Bone("RFootJ"), body.Bone("LToeN"), body.Bone("RToeN") };
            _legs = new[] { (body.Bone("LLegJ"), body.Bone("LKneeJ"), body.Bone("LFootJ")), (body.Bone("RLegJ"), body.Bone("RKneeJ"), body.Bone("RFootJ")) };
            _faces = BrawlZeroSuitFaces.Load(body);
            var under = new bool[body.Data.BoneCount];
            var arm = new List<int>();
            foreach (int i in body.Data.Order)
            {
                under[i] = i == _shoulder || body.Data.Parent[i] >= 0 && under[body.Data.Parent[i]];
                if (under[i] && _shoulder >= 0) arm.Add(i);
            }
            _gunArm = arm.ToArray();
            // (frequency rad/s, damping ratio, how hard the body's motion throws it, streaming back per unit of speed, its
            // reach as a share of its length)
            Add(body, new[] { "Ponytail01N", "Ponytail02N", "Ponytail03N" }, 7.5f, 0.25f, 0.55f, 0.12f, 0.9f);
            if (_chains.Count > 0 && BuildBack(body)) _chains[^1].Clear = HairThickness(body, _chains[^1]);
            Add(body, new[] { "LMomi01N", "LMomi02N" }, 11f, 0.3f, 0.35f, 0.06f, 0.6f);
            Add(body, new[] { "RMomi01N", "RMomi02N" }, 11f, 0.3f, 0.35f, 0.06f, 0.6f);
            // the chest: shifted (side to side, up and down, in and out), ~2 bounces a second, lightly damped, a share of the
            // body's change in speed (jump, landing, a stop, a turn, each step), at most its own depth (owner 10-15 on the
            // first try: "just a bit more dramatic"; 18:0x: "bring her chest physics back down a little? not as low as it was
            // before but an in between": halfway from 15 / 0.12 / 0.16 / 0.75)
            Add(body, new[] { "LBreastN" }, 14f, 0.11f, 0.22f, 0, 0.875f, shift: true);
            Add(body, new[] { "RBreastN" }, 14f, 0.11f, 0.22f, 0, 0.875f, shift: true);
        }

        public string Describe() => $"aim {(_hand >= 0 && _have >= 0 ? "on" : "off")} (barrel {_barrel}, top {_gunTop}), springs: {_chains.Count} chains, {_points} points";

        void Add(NativeBody body, string[] names, float omega, float zeta, float gain, float drag, float maxFrac, bool shift = false)
        {
            var bones = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                bones[i] = body.Bone(names[i]);
                if (bones[i] < 0) return;
            }
            if (bones.Length == 1 && SkinTip(body, bones[0]) is Vector3 tip) _tipLocal[bones[0]] = tip;
            else if (bones.Length == 1) return;
            _chains.Add(new Chain { Bones = bones, Omega = omega, Damping = 2 * zeta * omega, Gain = gain, Drag = drag, MaxFrac = maxFrac, Shift = shift });
            _points += bones.Length;
        }

        public void AfterPose(NativeBody body, PlayerEntity? p)
        {
            if (p != null && p.IsAltForm) return;
            if (Aim) AimArmEased(body, p);
            if (Springs && p != null) Spring(body, p);
            else if (Hair) Collide(body);
            _faces?.Apply(body, p);
        }

        // AimArm, let go for her idle flourish (Brawl's Wait2 plays its own gun arm, owner 10-15 "there is a 'flourish'
        // animation that she does if she idles for a while ... can we give her this"): off over its first 15%, back on over
        // its last 15% (each arm bone's world turn and place eased between the clip's and the aim's)
        void AimArmEased(NativeBody body, PlayerEntity? p)
        {
            float w = 1;
            if (p != null && p.HostBipedModel1.AnimInfo is AnimationInfo legs && (PlayerAnimation)legs.Index[0] == PlayerAnimation.Flourish
                && legs.FrameCount[0] > 0 && body.Clip(BrawlZeroSuitFaces.FlourishClip) != null)
            {
                float f = Math.Clamp(legs.Frame[0] / (float)legs.FrameCount[0], 0, 1);
                float x = Math.Clamp(Math.Min(f, 1 - f) / 0.15f, 0, 1);
                w = 1 - x * x * (3 - 2 * x);
            }
            if (w <= 1e-3f) return;
            if (w >= 0.999f || _gunArm.Length == 0)
            {
                AimArm(body, p);
                return;
            }
            if (_armSave.Length < _gunArm.Length) _armSave = new Matrix4[_gunArm.Length];
            for (int k = 0; k < _gunArm.Length; k++) _armSave[k] = body.World[_gunArm[k]];
            AimArm(body, p);
            for (int k = 0; k < _gunArm.Length; k++)
            {
                Matrix4 a = _armSave[k], b = body.World[_gunArm[k]];
                Quaternion q = Quaternion.Slerp(a.ExtractRotation(), b.ExtractRotation(), w);
                Vector3 at = Vector3.Lerp(a.ExtractTranslation(), b.ExtractTranslation(), w);
                body.World[_gunArm[k]] = Matrix4.CreateFromQuaternion(q) * Matrix4.CreateTranslation(at);
            }
        }

        // ---- Samus's armoured spacing taken in ----

        void Narrow(NativeBody body)
        {
            if (_hip < 0 || _bust < 0 || _top < 0) return;
            float floor = Lowest(body);
            Vector3 hipSide = Side(body, _hip);
            foreach ((int j, int c) in _thighs) TurnIn(body, j, c, hipSide, ThighIn);
            NarrowArm(body);
            float sank = floor - Lowest(body);
            if (sank <= 0) return;
            // (the posed bones themselves: SetWorld would rebuild them from the clip and lose the turns)
            Matrix4 up = Matrix4.CreateTranslation(0, sank, 0);
            for (int i = 0; i < body.World.Length; i++) body.World[i] *= up;
        }

        void NarrowArm(NativeBody body)
        {
            Vector3 bustSide = Side(body, _bust);
            TurnIn(body, _arm[0].Joint, _arm[0].Child, bustSide, UpperArmIn);
            TurnIn(body, _arm[1].Joint, _arm[1].Child, bustSide, ForearmIn);
        }

        // ---- her clips baked at load ----

        // clip src (one of Samus's, DsRetarget's) with every key taken in (Narrow) and, for a walk, straightened
        // (Straighten; track: each ankle on its line, running forward and back), as a new clip called name
        // (upright: the walk's torso held upright too, Straighten)
        public HdGun.Anim Bake(NativeBody body, HdGun.Anim src, string name, bool walk, bool track, bool upright = false)
        {
            HdGun.Anim a = Empty(body, src, name);
            // (upright: each spine bone and the head held at the clip's average lean forward, no nod)
            float[]? lean = null;
            if (walk && upright)
            {
                int[] sp = SpineBones;
                lean = new float[sp.Length + 1];
                for (int key = 0; key < src.Keys; key++)
                {
                    body.Pose(src, Math.Min(src.Duration, key * src.Tick));
                    for (int i = 0; i <= sp.Length; i++)
                    {
                        int b = i < sp.Length ? sp[i] : _head;
                        if (b >= 0) lean[i] += Pitch(Delta(body, b)) / src.Keys;
                    }
                }
            }
            for (int key = 0; key < src.Keys; key++)
            {
                body.Pose(src, Math.Min(src.Duration, key * src.Tick));
                if (walk) Straighten(body, track, lean);
                else Narrow(body);
                Store(body, a, key);
            }
            return a;
        }

        static HdGun.Anim Empty(NativeBody body, HdGun.Anim src, string name) => new HdGun.Anim
        {
            Name = name, Keys = src.Keys, Tick = src.Tick, Duration = src.Duration, Frames = new float[src.Keys * body.Data.BoneCount * body.Data.KeyStride],
        };

        // the posed bones' locals as key `key` of a
        static void Store(NativeBody body, HdGun.Anim a, int key)
        {
            HdGun g = body.Data;
            int n = g.BoneCount, stride = g.KeyStride;
            foreach (int i in g.Order)
            {
                Matrix4 local = g.Parent[i] >= 0 ? body.World[i] * body.World[g.Parent[i]].Inverted() : body.World[i];
                Quaternion q = local.ExtractRotation();
                q.Normalize();
                Vector3 at = local.ExtractTranslation();
                int o = (key * n + i) * stride;
                a.Frames[o] = q.X; a.Frames[o + 1] = q.Y; a.Frames[o + 2] = q.Z; a.Frames[o + 3] = q.W;
                a.Frames[o + 4] = at.X; a.Frames[o + 5] = at.Y; a.Frames[o + 6] = at.Z;
                if (stride >= 10) { a.Frames[o + 7] = 1; a.Frames[o + 8] = 1; a.Frames[o + 9] = 1; }
            }
        }

        // ---- her torso upright ----
        // owner 10-15 17:3x: "straighten her torso in idle pose, but keep everything else and its animation" and "she also
        // does this sort of back and forth wobble on her torso while running ... straighten her torso for her whole running
        // animation". Her spine (BodyN, BustN, NeckN) is turned in model space; her head keeps the way it faces (so she
        // still looks where the clip looks), everything under the chest follows it.

        // a bone's turn from its bind, in model space (row vectors: bind, then this)
        Matrix4 Delta(NativeBody body, int bone) => Rot(body.Data.InvBind[bone] * body.World[bone]);

        int[] SpineBones => new[] { _spine, _bust, _neck };

        // each spine bone to `turn(i, its turn now)`; the head to turn(3, ...) too (head) or back to how it faced
        void SetSpine(NativeBody body, Func<int, Matrix4, Matrix4> turn, bool head = false)
        {
            int[] sp = SpineBones;
            var was = new Matrix4[sp.Length + 1];
            for (int i = 0; i <= sp.Length; i++)
            {
                int b = i < sp.Length ? sp[i] : _head;
                was[i] = b >= 0 ? Delta(body, b) : Matrix4.Identity;
            }
            Matrix4 faced = _head >= 0 ? Rot(body.World[_head]) : Matrix4.Identity;
            for (int i = 0; i < sp.Length; i++)
            {
                if (sp[i] < 0) continue;
                body.SetWorld(sp[i], Rot(body.Data.InvBind[sp[i]].Inverted()) * turn(i, was[i]) * Matrix4.CreateTranslation(At(body, sp[i])));
            }
            if (_head < 0) return;
            Matrix4 r = head ? Rot(body.Data.InvBind[_head].Inverted()) * turn(sp.Length, was[sp.Length]) : faced;
            body.SetWorld(_head, r * Matrix4.CreateTranslation(At(body, _head)));
        }

        // a turn's lean forward (+) / back, radians
        static float Pitch(Matrix4 d)
        {
            Vector3 f = Vector3.TransformVector(Vector3.UnitZ, d);
            return MathF.Atan2(-f.Y, new Vector2(f.X, f.Z).Length);
        }

        // a held clip (her idle) with the spine's average turn taken out: upright, its breathing and sway kept
        public HdGun.Anim BakeUpright(NativeBody body, HdGun.Anim src, string name)
        {
            int[] sp = SpineBones;
            var sum = new Quaternion[sp.Length];
            for (int key = 0; key < src.Keys; key++)
            {
                body.Pose(src, Math.Min(src.Duration, key * src.Tick));
                for (int i = 0; i < sp.Length; i++)
                {
                    if (sp[i] < 0) continue;
                    Quaternion q = Delta(body, sp[i]).ExtractRotation();
                    if (key > 0 && sum[i].X * q.X + sum[i].Y * q.Y + sum[i].Z * q.Z + sum[i].W * q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                    sum[i] = key == 0 ? q : sum[i] + q;
                }
            }
            var undo = new Matrix4[sp.Length];
            for (int i = 0; i < sp.Length; i++)
            {
                Quaternion q = sum[i];
                undo[i] = q.LengthSquared > 1e-12f ? Matrix4.CreateFromQuaternion(q.Normalized()).Inverted() : Matrix4.Identity;
            }
            HdGun.Anim a = Empty(body, src, name);
            for (int key = 0; key < src.Keys; key++)
            {
                body.Pose(src, Math.Min(src.Duration, key * src.Tick));
                SetSpine(body, (i, d) => d * undo[i]);
                Store(body, a, key);
            }
            return a;
        }

        // ---- her walks straightened ----

        // after Pose: taken in (Narrow), then the hips squared to her facing and each leg laid straight (Leg), the feet back
        // on the floor where the clip has them
        void Straighten(NativeBody body, bool track, float[]? lean)
        {
            Narrow(body);
            if (_hip < 0 || _spine < 0) return;
            float floor = Lowest(body);
            var ankle = new Vector3[2];
            var bend = new float[2];
            for (int s = 0; s < 2; s++)
            {
                ankle[s] = _legs[s].Foot >= 0 ? At(body, _legs[s].Foot) : Vector3.Zero;
                bend[s] = Ankle(body, _legs[s].Knee, _legs[s].Foot, _feet[2 + s]);
            }
            // the hips squared (SetWorld rebuilds what's under them from the clip: the spine put back, the arm taken in again)
            Matrix4 spine = Rot(body.World[_spine]);
            Vector3 f = Vector3.TransformVector(Vector3.UnitZ, body.Data.InvBind[_hip] * body.World[_hip]);
            f.Y = 0;
            if (f.LengthSquared > 1e-6f) body.Turn(_hip, DsRetarget.Onto(f, Vector3.UnitZ));
            // and their side-to-side tilt kept within a human's (the CC0 jog: 5 degrees; Samus's walk back: 14)
            Vector3 side = Side(body, _hip);
            float tilt = MathF.Asin(Math.Clamp(side.Y, -1, 1)), keep = MathHelper.DegreesToRadians(HipTilt);
            if (MathF.Abs(tilt) > keep)
            {
                Vector3 level = side - Vector3.UnitY * side.Y;
                Vector3 to = level.Normalized() * MathF.Cos(keep) + Vector3.UnitY * MathF.Sin(keep) * MathF.Sign(tilt);
                body.Turn(_hip, DsRetarget.Onto(side, to));
            }
            body.SetWorld(_spine, spine * Matrix4.CreateTranslation(At(body, _spine)));
            if (lean != null) SetSpine(body, (i, _) => Matrix4.CreateRotationX(lean[i]), head: true);
            NarrowArm(body);
            float mid = At(body, _hip).X;
            for (int s = 0; s < 2; s++)
            {
                (int thigh, int knee, int foot) = _legs[s];
                if (thigh < 0 || knee < 0 || foot < 0) continue;
                Vector3 a = ankle[s];
                if (track) a.X = mid + (s == 0 ? Track : -Track);   // (_legs[0]: her left, +X)
                Leg(body, thigh, knee, foot, _feet[2 + s], a, bend[s]);
            }
            float sank = floor - Lowest(body);
            Matrix4 up = Matrix4.CreateTranslation(0, sank, 0);
            for (int i = 0; i < body.World.Length; i++) body.World[i] *= up;
        }

        // the clip's ankle: the foot's turn off the shin about the knee's hinge (radians; 0 = foot along the shin, + = toes
        // ahead of it). Kept relative to the shin, not to the floor: the foot's pitch in the world, flipped to point ahead,
        // folded her swinging foot back up against her shin (owner 10-15: "maybe theres a frame where it looks like her leg
        // is broken")
        float Ankle(NativeBody body, int knee, int foot, int toeBone)
        {
            if (knee < 0 || foot < 0 || toeBone < 0) return MathF.PI / 2;
            Vector3 k = At(body, knee), f = At(body, foot), shin = (f - k).Normalized(), toe = At(body, toeBone) - f;
            // the knee's hinge at bind: square to the bind leg and straight ahead (-X, as Leg's), turned with the knee
            Vector3 hinge = Vector3.TransformVector(-Vector3.UnitX, Rot(body.Data.InvBind[knee] * body.World[knee])).Normalized();
            Vector3 ahead = Square(Vector3.Cross(hinge, shin), shin);
            if (ahead.LengthSquared < 0.5f) return MathF.PI / 2;
            return MathF.Atan2(Vector3.Dot(toe, ahead), Vector3.Dot(toe, shin));
        }

        // one leg laid from its hip joint to ankle `a` with no roll from her bind (each bone's bind turned the shortest way
        // onto its new line): the knee bends straight ahead (+Z), the foot points straight ahead, turned `bend` off the shin
        static void Leg(NativeBody body, int thigh, int knee, int foot, int toeBone, Vector3 a, float bend)
        {
            HdGun g = body.Data;
            Vector3 bt = BindAt(g, thigh), bk = BindAt(g, knee), bf = BindAt(g, foot);
            float l1 = (bk - bt).Length, l2 = (bf - bk).Length;
            Vector3 h = At(body, thigh), d = a - h;
            float dist = Math.Clamp(d.Length, Math.Abs(l1 - l2) + 1e-3f, (l1 + l2) * 0.9995f);
            Vector3 dn = d.LengthSquared > 1e-10f ? d.Normalized() : -Vector3.UnitY;
            float along = (l1 * l1 - l2 * l2 + dist * dist) / (2 * dist);
            float out_ = MathF.Sqrt(Math.Max(0, l1 * l1 - along * along));
            Vector3 pole = Square(Vector3.UnitZ, dn);
            if (pole.LengthSquared < 0.5f) pole = Vector3.UnitZ;
            Vector3 k = h + dn * along + pole * out_, end = h + dn * dist;
            // the knee's hinge: square to the leg's plane (its bind one: square to the bind bone and straight ahead)
            Vector3 hinge = Vector3.Cross(dn, pole).Normalized();
            body.SetWorld(thigh, Rot(g.InvBind[thigh].Inverted()) * Frame(bk - bt, Vector3.UnitZ, k - h, hinge) * Matrix4.CreateTranslation(h));
            body.SetWorld(knee, Rot(g.InvBind[knee].Inverted()) * Frame(bf - bk, Vector3.UnitZ, end - k, hinge) * Matrix4.CreateTranslation(k));
            if (toeBone < 0) return;
            Vector3 shin = (end - k).Normalized(), want = shin * MathF.Cos(bend) + Vector3.Cross(hinge, shin).Normalized() * MathF.Sin(bend);
            Vector3 bToe = BindAt(g, toeBone) - bf;
            body.SetWorld(foot, Rot(g.InvBind[foot].Inverted()) * Frame(bToe, Vector3.UnitY, want, hinge) * Matrix4.CreateTranslation(end));
        }

        // the turn taking bind direction `from` onto `to` and, about it, the bind side (from x `ahead`, square to `from`)
        // onto `side` (square to `to`): a bone laid with no roll of its own (row vectors)
        static Matrix4 Frame(Vector3 from, Vector3 ahead, Vector3 to, Vector3 side)
        {
            Vector3 b1 = from.Normalized(), b2 = Square(Vector3.Cross(from, ahead), b1);
            Vector3 n1 = to.Normalized(), n2 = Square(side, n1);
            if (b2.LengthSquared < 0.5f || n2.LengthSquared < 0.5f) return DsRetarget.Onto(from, to);
            var b = new Matrix4(new Vector4(b1, 0), new Vector4(b2, 0), new Vector4(Vector3.Cross(b1, b2), 0), Vector4.UnitW);
            var n = new Matrix4(new Vector4(n1, 0), new Vector4(n2, 0), new Vector4(Vector3.Cross(n1, n2), 0), Vector4.UnitW);
            return Matrix4.Transpose(b) * n;
        }

        static Vector3 BindAt(HdGun g, int bone) => g.InvBind[bone].Inverted().ExtractTranslation();


        // v's part square to the unit axis, unit length (zero if v lies along it)
        static Vector3 Square(Vector3 v, Vector3 axis)
        {
            Vector3 s = v - axis * Vector3.Dot(v, axis);
            return s.LengthSquared > 1e-10f ? s.Normalized() : Vector3.Zero;
        }

        // a bone's "her left" now: model +X carried by how the bone has turned from its bind
        static Vector3 Side(NativeBody body, int bone)
        {
            Vector3 s = Vector3.TransformVector(Vector3.UnitX, body.Data.InvBind[bone] * body.World[bone]);
            return s.LengthSquared > 1e-10f ? s.Normalized() : Vector3.UnitX;
        }

        // the limb joint -> child turned toward her middle by `deg` of its outward angle (side: her left; which side the
        // limb is on from where its joint sits)
        static void TurnIn(NativeBody body, int joint, int child, Vector3 side, float deg)
        {
            if (joint < 0 || child < 0) return;
            Vector3 a = At(body, joint), u = At(body, child) - a;
            float len = u.Length;
            if (len < 1e-6f) return;
            float s = Vector3.Dot(a - At(body, 0), side) >= 0 ? 1 : -1;
            float outward = Vector3.Dot(u, side) * s / len;
            if (outward <= 0) return;
            float ang = MathF.Asin(Math.Min(1, outward)), d = MathHelper.DegreesToRadians(deg);
            float now = ang - d * Math.Min(1, ang / (1.5f * d));
            Vector3 flat = u - side * Vector3.Dot(u, side);
            if (flat.LengthSquared < 1e-10f) return;
            Vector3 v = flat.Normalized() * (len * MathF.Cos(now)) + side * (s * len * MathF.Sin(now));
            TurnOnto(body, joint, u, v);
        }

        float Lowest(NativeBody body)
        {
            float y = float.MaxValue;
            foreach (int f in _feet) if (f >= 0) y = Math.Min(y, At(body, f).Y);
            return y;
        }

        // ---- the gun arm on the aim ----

        void AimArm(NativeBody body, PlayerEntity? p)
        {
            if (_shoulder < 0 || _elbow < 0 || _hand < 0 || _have < 0) return;
            Vector3 d = Vector3.UnitZ;
            if (p != null)
            {
                Vector3 aim = p.FacingVector;
                var lateral = new Vector3(p.Field70, 0, p.Field74);
                if (aim.LengthSquared > 1e-6f && lateral.LengthSquared > 1e-6f)
                {
                    aim.Normalize();
                    lateral.Normalize();
                    Vector3 side = Vector3.Cross(Vector3.UnitY, lateral);
                    d = new Vector3(Vector3.Dot(aim, side), aim.Y, Vector3.Dot(aim, lateral)).Normalized();
                }
            }
            Vector3 up = Vector3.UnitY - d * Vector3.Dot(Vector3.UnitY, d);
            up = up.LengthSquared > 1e-6f ? up.Normalized() : Vector3.UnitZ;
            // the clip's own gun arm (Samus's: her arm cannon's hold) turned at the shoulder so the forearm lies along the aim,
            // its bend kept (owner 10-15 17:1x: "can we get her gun arm like vanilla samus while idling", and the fixed hold
            // before -- hand ahead of the shoulder, in toward her middle -- "clips into her breast")
            Vector3 e = At(body, _elbow), w = At(body, _hand);
            if ((w - e).LengthSquared < 1e-10f) return;
            body.Turn(_shoulder, DsRetarget.Onto(w - e, d));
            // a shot's kick: the forearm and the barrel up and back down within a few frames (MphRead's biped2 Shoot /
            // ChargeShoot frame). Before the hand is set: turning the elbow rebuilds the hand from the clip, which rolled
            // the gun sideways for a moment after every shot (owner 10-15: "when she shoots her gun goes from straight up
            // and down to a sort of sideways position")
            float kick = 0;
            if (p != null)
            {
                AnimationInfo info = p.BipedModel2.AnimInfo;
                var state = (PlayerAnimation)info.Index[0];
                if (state is PlayerAnimation.Shoot or PlayerAnimation.ChargeShoot)
                {
                    float t = info.Frame[0] / 30f;
                    kick = MathHelper.DegreesToRadians(state == PlayerAnimation.ChargeShoot ? 22 : 12) * MathF.Exp(-t / 0.07f);
                }
            }
            Vector3 aimD = d, aimUp = up;
            if (kick > 1e-3f)
            {
                Matrix4 lift = Matrix4.CreateFromAxisAngle(Vector3.Cross(d, up).Normalized(), kick);
                body.Turn(_elbow, Matrix4.CreateFromAxisAngle(Vector3.Cross(d, up).Normalized(), kick * 0.5f));
                aimD = Vector3.TransformNormal(d, lift).Normalized();
                aimUp = Vector3.TransformNormal(up, lift).Normalized();
            }
            // the barrel along the (kicked) aim, its top up -- last, so nothing rebuilds the hand after it
            Matrix4 have = Rot(body.World[_have]);
            Vector3 barrel = Vector3.TransformNormal(_barrel, have).Normalized(), top = Vector3.TransformNormal(_gunTop, have);
            top = (top - barrel * Vector3.Dot(top, barrel)).Normalized();
            body.Turn(_hand, Frame(barrel, top).Inverted() * Frame(aimD, aimUp));
        }

        // the Paralyzer's muzzle as posed (model space): where her shots' flash and charge glow go in third person (owner
        // 10-15 18:0x, screenshot: the flash hung a gun's length past the barrel -- MPH's muzzle offset on Samus's shoot node)
        public bool GunTip(NativeBody body, out Vector3 tip)
        {
            tip = default;
            if (_muzzle is not Vector3 m || _have < 0) return false;
            tip = Vector3.TransformPosition(m, body.World[_have]);
            return true;
        }

        // her first-person gun arm (owner 10-15: "the first person 'arm cannon' dissections we did for trooper and dark samus"):
        // forearm, hand and Paralyzer cut from RArmJ down, held as her idle holds it (zs:WaitUpArms at its start: the grip)
        // with the gun put on the aim (AimArm, level ahead); its muzzle on MPH's muzzle point, barrel forward, top up.
        // First-person size by eye against MPH's gun
        public const float CannonSize = 0.75f;

        public BodyArmCannon? Cannon(NativeBody body)
        {
            if (_muzzle is not Vector3 muzzle || _have < 0 || _elbow < 0) return null;
            HdGun.Anim? hold = body.Clip(BrawlZeroSuitBody.HoldClip) ?? body.Clip(BrawlZeroSuitBody.HoldFallback);
            void Pose(NativeBody b)
            {
                if (hold != null) b.Pose(hold, 0);
                else
                {
                    HdGun g = b.Data;
                    for (int i = 0; i < g.BoneCount; i++) b.World[i] = Matrix4.CreateTranslation(g.Bind[i]);
                }
                AimArm(b, null);
            }
            return BodyArmCannon.Held(body, "RArmJ", "RHaveN", Pose, b =>
            {
                Matrix4 have = Rot(b.World[_have]);
                return (Vector3.TransformPosition(muzzle, b.World[_have]), Vector3.TransformNormal(_barrel, have),
                    Vector3.TransformNormal(_gunTop, have));
            }, CannonSize);
        }

        // the middle of the front tenth of the gun along its barrel (its vertices skinned to the grip bone alone)
        static Vector3? GunFront(NativeBody body, int have, Vector3 barrel)
        {
            if (have < 0) return null;
            HdGun g = body.Data;
            var pts = new List<Vector3>();
            foreach (HdGun.Material m in g.Materials)
            {
                for (int v = 0; v < m.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    int slot = (int)m.Vertices[o + 11];
                    if (m.Vertices[o + 15] < 0.999f || slot < 0 || slot >= m.Palette.Length || m.Palette[slot] != have) continue;
                    pts.Add(Vector3.TransformPosition(new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]), g.InvBind[have]));
                }
            }
            if (pts.Count < 8) return null;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3 q in pts)
            {
                lo = Math.Min(lo, Vector3.Dot(q, barrel));
                hi = Math.Max(hi, Vector3.Dot(q, barrel));
            }
            Vector3 sum = Vector3.Zero;
            int n = 0;
            foreach (Vector3 q in pts)
            {
                if (Vector3.Dot(q, barrel) < hi - 0.1f * (hi - lo)) continue;
                sum += q;
                n++;
            }
            if (n == 0) return null;
            Vector3 c = sum / n;
            return c + barrel * (hi - Vector3.Dot(c, barrel));   // (on the front face)
        }

        // rows: x = forward, y = up, z = forward x up (an orthonormal frame, row vectors)
        static Matrix4 Frame(Vector3 forward, Vector3 up)
        {
            Vector3 z = Vector3.Cross(forward, up);
            return new Matrix4(new Vector4(forward, 0), new Vector4(up, 0), new Vector4(z, 0), Vector4.UnitW);
        }

        // the Paralyzer's barrel (its long axis, toward the end farther from the grip) and its top (from the grip toward
        // the barrel's middle), in the grip bone's frame: from the vertices skinned to it alone
        static (Vector3 Barrel, Vector3 Top) GunAxes(NativeBody body, int have)
        {
            var fallback = (Vector3.UnitZ, Vector3.UnitY);
            if (have < 0) return fallback;
            HdGun g = body.Data;
            var pts = new List<Vector3>();
            foreach (HdGun.Material m in g.Materials)
            {
                for (int v = 0; v < m.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    int slot = (int)m.Vertices[o + 11];
                    if (m.Vertices[o + 15] < 0.999f || slot < 0 || slot >= m.Palette.Length || m.Palette[slot] != have) continue;
                    pts.Add(Vector3.TransformPosition(new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]), g.InvBind[have]));
                }
            }
            if (pts.Count < 8) return fallback;
            Vector3 c = Vector3.Zero;
            foreach (Vector3 q in pts) c += q;
            c /= pts.Count;
            // the long axis: power iteration on the spread
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vector3 q in pts)
            {
                Vector3 r = q - c;
                xx += r.X * r.X; xy += r.X * r.Y; xz += r.X * r.Z; yy += r.Y * r.Y; yz += r.Y * r.Z; zz += r.Z * r.Z;
            }
            Vector3 axis = Vector3.UnitZ;
            for (int k = 0; k < 32; k++)
            {
                axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z,
                    xz * axis.X + yz * axis.Y + zz * axis.Z).Normalized();
            }
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3 q in pts)
            {
                float t = Vector3.Dot(q, axis);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }
            if (-lo > hi) axis = -axis;
            Vector3 top = c - axis * Vector3.Dot(c, axis);
            if (top.LengthSquared < 1e-8f) top = Vector3.Cross(axis, Vector3.UnitX);
            return (axis, top.Normalized());
        }

        // ---- springs ----

        // a one-bone chain's tip: the middle of the skin it moves (weighted), in its own frame
        static Vector3? SkinTip(NativeBody body, int bone)
        {
            HdGun g = body.Data;
            Vector3 sum = Vector3.Zero;
            float wsum = 0;
            foreach (HdGun.Material m in g.Materials)
            {
                for (int v = 0; v < m.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    for (int k = 0; k < 4; k++)
                    {
                        int slot = (int)m.Vertices[o + 11 + k];
                        float w = m.Vertices[o + 15 + k];
                        if (w <= 0 || slot < 0 || slot >= m.Palette.Length || m.Palette[slot] != bone) continue;
                        sum += new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]) * w;
                        wsum += w;
                    }
                }
            }
            if (wsum <= 0) return null;
            Vector3 tip = Vector3.TransformPosition(sum / wsum, g.InvBind[bone]);
            return tip.LengthSquared > 1e-8f ? tip : null;
        }

        void Spring(NativeBody body, PlayerEntity p)
        {
            Sim sim;
            lock (_sims)
            {
                if (!_sims.TryGetValue(p.SlotIndex, out sim!)) _sims[p.SlotIndex] = sim = new Sim();
            }
            long now = Stopwatch.GetTimestamp();
            float dt = sim.Ready ? Math.Min(0.05f, (now - sim.Stamp) / (float)Stopwatch.Frequency) : 0;
            sim.Stamp = now;
            if (sim.X.Length != _points)
            {
                sim.X = new Vector3[_points];
                sim.V = new Vector3[_points];
                sim.TipPrev = new Vector3[_points];
                sim.TipVel = new Vector3[_points];
                sim.Ready = false;
            }
            var facing = new Vector3(p.Field70, 0, p.Field74);
            float heading = facing.LengthSquared > 1e-8f ? MathF.Atan2(facing.X, facing.Z) : 0;
            Matrix4 toWorld = Matrix4.CreateScale(body.Scale) * Matrix4.CreateRotationY(heading) * Matrix4.CreateTranslation(p.Position);
            Matrix4 toModel = toWorld.Inverted();
            int k = 0;
            bool reset = !sim.Ready;
            foreach (Chain c in _chains)
            {
                for (int j = 0; j < c.Bones.Length; j++, k++)
                {
                    int bone = c.Bones[j];
                    Vector3 root = At(body, bone);
                    Vector3 tip = Tip(body, c, j);
                    Vector3 rootW = Vector3.TransformPosition(root, toWorld), tipW = Vector3.TransformPosition(tip, toWorld);
                    float len = (tipW - rootW).Length;
                    if (len < 1e-6f) continue;
                    // (smoothed: the seat moves on game ticks while the body is drawn every frame, so the raw speed
                    // jumps between none and double each frame and would shake the springs -- owner 10-15: the chest
                    // "trembles/shakes as she falls")
                    Vector3 raw = dt > 0 ? (tipW - sim.TipPrev[k]) / dt : Vector3.Zero;
                    Vector3 vel = sim.TipVel[k] + (raw - sim.TipVel[k]) * (dt > 0 ? 1 - MathF.Exp(-dt / VelSmooth) : 0);
                    if (reset || (tipW - sim.TipPrev[k]).Length > 1.5f)
                    {
                        // first frame, or a teleport (respawn): start at rest
                        sim.X[k] = sim.V[k] = Vector3.Zero;
                        vel = Vector3.Zero;
                    }
                    else if (dt > 0)
                    {
                        Vector3 dv = vel - sim.TipVel[k];
                        if (dv.Length > 20) dv = Vector3.Zero;
                        sim.V[k] -= dv * (c.Shift ? c.Gain : c.Gain * len * c.Omega / 3f);
                        var horiz = new Vector3(vel.X, 0, vel.Z);
                        Vector3 bias = -horiz * (c.Drag * len);
                        int steps = Math.Max(1, (int)MathF.Ceiling(dt * 240));
                        float h = dt / steps;
                        for (int s = 0; s < steps; s++)
                        {
                            Vector3 acc = -(c.Omega * c.Omega) * (sim.X[k] - bias) - c.Damping * sim.V[k];
                            sim.V[k] += acc * h;
                            sim.X[k] += sim.V[k] * h;
                        }
                        float max = c.MaxFrac * len;
                        if (sim.X[k].Length > max)
                        {
                            sim.X[k] = sim.X[k].Normalized() * max;
                            sim.V[k] *= 0.5f;
                        }
                    }
                    sim.TipPrev[k] = tipW;
                    sim.TipVel[k] = vel;
                    if (c.Shift)
                    {
                        Vector3 off = Vector3.TransformVector(sim.X[k], toModel);
                        body.SetWorld(bone, body.World[bone] * Matrix4.CreateTranslation(off));
                        continue;
                    }
                    Vector3 want = Vector3.TransformPosition(tipW + sim.X[k], toModel);
                    if (Hair && c.Clear != null) want = Clear(body, root, want, c.Clear[j]);
                    TurnOnto(body, bone, tip - root, want - root);
                }
            }
            sim.Ready = true;
        }

        // ---- the ponytail kept off her body ----

        // Her back, neck and head as a height map seen from behind (owner 10-15: "keep her ponytail from clipping through
        // her back or neck etc"): her skin on those bones (and the collar bones), at bind, sampled into cells across (x) and
        // up (y), each holding the hindmost point (lowest z) and the bone it moves with. A hair point is tested in each
        // bone's bind frame; in front of that bone's back surface (less than its hair thickness behind it, and not past
        // the front of her) it is pushed straight back onto it.
        const float CellX = 0.35f, CellY = 0.3f, BackX0 = -3.5f, BackY0 = 9f, Through = 1.5f;
        const int BackCols = 20, BackRows = 40;
        float[] _backZ = Array.Empty<float>();
        int[] _backBone = Array.Empty<int>();
        int[] _backBones = Array.Empty<int>();

        bool BuildBack(NativeBody body)
        {
            HdGun g = body.Data;
            var set = new List<int>();
            foreach (string n in new[] { "HipN", "BodyN", "BustN", "NeckN", "HeadN", "LShoulderN", "RShoulderN" })
            {
                if (body.Bone(n) >= 0) set.Add(body.Bone(n));
            }
            if (set.Count == 0) return false;
            _backZ = new float[BackCols * BackRows];
            _backBone = new int[BackCols * BackRows];
            Array.Fill(_backZ, float.NaN);
            Array.Fill(_backBone, -1);
            var pos = new Vector3[3];
            var dom = new int[3];
            foreach (HdGun.Material m in g.Materials)
            {
                for (int tri = 0; tri + 2 < m.VertexCount; tri += 3)
                {
                    bool ok = true;
                    for (int v = 0; v < 3 && ok; v++)
                    {
                        int o = (tri + v) * HdGun.FloatsPerVertex;
                        pos[v] = new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]);
                        float best = 0;
                        dom[v] = -1;
                        for (int k = 0; k < 4; k++)
                        {
                            int slot = (int)m.Vertices[o + 11 + k];
                            float w = m.Vertices[o + 15 + k];
                            if (w > best && slot >= 0 && slot < m.Palette.Length) { best = w; dom[v] = m.Palette[slot]; }
                        }
                        ok = dom[v] >= 0 && set.Contains(dom[v]);
                    }
                    if (!ok) continue;
                    // the triangle sampled finely enough to leave no cell it crosses empty
                    float span = Math.Max((pos[1] - pos[0]).Length, Math.Max((pos[2] - pos[0]).Length, (pos[2] - pos[1]).Length));
                    int steps = Math.Clamp((int)MathF.Ceiling(span / (0.5f * CellY)), 1, 64);
                    for (int a = 0; a <= steps; a++)
                    {
                        for (int b = 0; a + b <= steps; b++)
                        {
                            float u = a / (float)steps, w2 = b / (float)steps;
                            Vector3 q = pos[0] + (pos[1] - pos[0]) * u + (pos[2] - pos[0]) * w2;
                            int cell = Cell(q);
                            if (cell < 0 || !float.IsNaN(_backZ[cell]) && q.Z >= _backZ[cell]) continue;
                            _backZ[cell] = q.Z;
                            _backBone[cell] = dom[u > 0.5f ? 1 : w2 > 0.5f ? 2 : 0];   // (the nearest corner's bone)
                        }
                    }
                }
            }
            var used = new HashSet<int>(_backBone);
            used.Remove(-1);
            _backBones = new int[used.Count];
            used.CopyTo(_backBones);
            return _backBones.Length > 0;
        }

        static int Cell(Vector3 q)
        {
            int cx = (int)MathF.Floor((q.X - BackX0) / CellX), cy = (int)MathF.Floor((q.Y - BackY0) / CellY);
            return cx < 0 || cy < 0 || cx >= BackCols || cy >= BackRows ? -1 : cy * BackCols + cx;
        }

        // each ponytail bone's thickness: the mean distance of the skin it moves (mostly) from its line
        static float[] HairThickness(NativeBody body, Chain c)
        {
            HdGun g = body.Data;
            int n = c.Bones.Length;
            var sum = new float[n];
            var cnt = new int[n];
            var a = new Vector3[n];
            var e = new Vector3[n];
            for (int j = 0; j < n; j++) a[j] = BindAt(g, c.Bones[j]);
            for (int j = 0; j < n; j++) e[j] = j + 1 < n ? a[j + 1] : j > 0 ? a[j] + (a[j] - a[j - 1]) : a[j];
            foreach (HdGun.Material m in g.Materials)
            {
                for (int v = 0; v < m.VertexCount; v++)
                {
                    int o = v * HdGun.FloatsPerVertex;
                    int slot = (int)m.Vertices[o + 11];
                    if (m.Vertices[o + 15] < 0.5f || slot < 0 || slot >= m.Palette.Length) continue;
                    int j = Array.IndexOf(c.Bones, m.Palette[slot]);
                    if (j < 0) continue;
                    var q = new Vector3(m.Vertices[o], m.Vertices[o + 1], m.Vertices[o + 2]);
                    Vector3 d = e[j] - a[j];
                    float s = d.LengthSquared > 1e-8f ? Math.Clamp(Vector3.Dot(q - a[j], d) / d.LengthSquared, 0, 1) : 0;
                    sum[j] += (q - (a[j] + d * s)).Length;
                    cnt[j]++;
                }
            }
            var r = new float[n];
            for (int j = 0; j < n; j++) r[j] = cnt[j] > 0 ? sum[j] / cnt[j] : 0.3f;
            return r;
        }

        // point p (model space, posed) pushed back out of her by `thick`; how far it was in (0: clear)
        Vector3 Push(NativeBody body, Vector3 p, float thick, out float depth)
        {
            depth = 0;
            foreach (int b in _backBones)
            {
                Matrix4 toPose = body.Data.InvBind[b] * body.World[b];
                Vector3 q = Vector3.TransformPosition(p, toPose.Inverted());
                int cell = Cell(q);
                if (cell < 0 || _backBone[cell] != b) continue;
                float wall = _backZ[cell] - thick;
                if (q.Z <= wall || q.Z > _backZ[cell] + Through) continue;
                depth = Math.Max(depth, q.Z - wall);
                q.Z = wall;
                p = Vector3.TransformPosition(q, toPose);
            }
            return p;
        }

        // a ponytail bone's new tip `want` (its line from `root`) kept off her: the tip and the line's middle, its length kept
        Vector3 Clear(NativeBody body, Vector3 root, Vector3 want, float thick)
        {
            float len = (want - root).Length;
            Vector3 mid = (root + want) * 0.5f, mid2 = Push(body, mid, thick, out float dm);
            if (dm > 0) want += (mid2 - mid) * 2;
            want = Push(body, want, thick, out _);
            Vector3 d = want - root;
            return d.LengthSquared > 1e-10f ? root + d.Normalized() * len : want;
        }

        // without the springs: the posed ponytail kept off her
        public void Collide(NativeBody body)
        {
            foreach (Chain c in _chains)
            {
                if (c.Clear == null) continue;
                for (int j = 0; j < c.Bones.Length; j++)
                {
                    Vector3 root = At(body, c.Bones[j]), tip = Tip(body, c, j);
                    Vector3 want = Clear(body, root, tip, c.Clear[j]);
                    if ((want - tip).LengthSquared > 1e-8f) TurnOnto(body, c.Bones[j], tip - root, want - root);
                }
            }
        }

        // dev (Tools -zsbrawl torso): how far into her the posed ponytail goes, per bone (line middle, tip)
        public float[] HairDepth(NativeBody body)
        {
            var list = new List<float>();
            foreach (Chain c in _chains)
            {
                if (c.Clear == null) continue;
                for (int j = 0; j < c.Bones.Length; j++)
                {
                    Vector3 root = At(body, c.Bones[j]), tip = Tip(body, c, j);
                    Push(body, (root + tip) * 0.5f, c.Clear[j], out float dm);
                    Push(body, tip, c.Clear[j], out float dt);
                    list.Add(dm);
                    list.Add(dt);
                }
            }
            return list.ToArray();
        }

        public string HairInfo()
        {
            int cells = 0;
            foreach (float z in _backZ) if (!float.IsNaN(z)) cells++;
            foreach (Chain c in _chains)
            {
                if (c.Clear == null) continue;
                // (her back's line down her middle: height, hindmost z)
                var mid = new List<string>();
                for (int row = 0; row < BackRows; row += 2)
                {
                    int cell = row * BackCols + (int)((0 - BackX0) / CellX);
                    if (!float.IsNaN(_backZ[cell])) mid.Add($"{BackY0 + (row + 0.5f) * CellY:F1}:{_backZ[cell]:F2}");
                }
                return $"back map {cells} cells on {_backBones.Length} bones; ponytail thickness {string.Join(", ", Array.ConvertAll(c.Clear, x => x.ToString("F2")))}; middle {string.Join(" ", mid)}";
            }
            return "no ponytail collider";
        }

        // where a chain bone's tip is now: its next bone; the last one carries on its parent's line by the same length; a
        // single bone's from its skin
        Vector3 Tip(NativeBody body, Chain c, int j)
        {
            int bone = c.Bones[j];
            if (j + 1 < c.Bones.Length) return At(body, c.Bones[j + 1]);
            if (j > 0)
            {
                Vector3 prev = At(body, c.Bones[j - 1]), me = At(body, bone);
                return me + (me - prev);
            }
            return Vector3.TransformPosition(_tipLocal[bone], body.World[bone]);
        }

        static Vector3 At(NativeBody body, int bone) => body.World[bone].ExtractTranslation();

        static Matrix4 Rot(Matrix4 m) => Matrix4.CreateFromQuaternion(m.ExtractRotation());

        static void TurnOnto(NativeBody body, int bone, Vector3 from, Vector3 to)
        {
            if (from.LengthSquared < 1e-12f || to.LengthSquared < 1e-12f) return;
            body.Turn(bone, DsRetarget.Onto(from, to));
        }
    }
}
