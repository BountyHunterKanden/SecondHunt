using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Dark Samus's float on MPH Samus's skeleton (her stand-in body and the recoloured Samus: MphRecomp.App
    // MatchSkins.cs sets HostFloatPose for those seats). The Echoes / Corruption bodies float on their own clips (App
    // Campaign/DarkSamusBody.cs, Core Campaign/DarkSamusFloat.cs); this builds the same hover on the vanilla rig with our
    // own pose numbers (broad angles matched to the Corruption float's, set in model space so the two rigs line up: Tools
    // -dsstandin float / corr measure both) and sine motion. Standing: the hips square, the chest turned to her left and
    // hunched forward toward the gun side, the head down and to her left, the gun arm straight out, the left arm held
    // out, the front leg raised and the back one hanging, the feet pointed with the toes just into the floor, a slow bob
    // with the front foot lifting. Moving: as Corruption's lean (NativeBody.Lean), four still lean poses blended by the
    // move direction against her facing, smoothed, the state cross-fading with the standing pose. The aim tilts the upper
    // body as vanilla's does. Jumps, the morph and unmorph stay vanilla; the pose eases in and out around them. Off
    // (default) = unchanged.
    public partial class PlayerEntity
    {
        public bool HostFloatPose { get; set; }
        // true (default): the feet held where they hang (the pelvis bobbing over them), pointed, with the deepest toe just
        // into the floor, as our Corruption float does; false: she hovers, the feet riding the bob clear of the floor
        public static bool HostFloatFeetPinned { get; set; } = true;

        private float _floatWeight = 0;
        private float _floatTime = 0;
        private float _floatLeanOn = 0; // the lean state's cross-fade (0..1)
        private bool _floatLeaning = false;
        private Vector2 _floatLeanDir = Vector2.Zero; // x: right, y: forward, smoothed
        private readonly AnimationInfo _floatLegAnim = new AnimationInfo();
        private readonly AnimationInfo _floatUpperAnim = new AnimationInfo();

        private const float _floatEase = 5; // weight per second (about 0.2 s in or out)
        private const float _floatLeanTime = 0.1f, _floatLeanFade = 0.12f; // seconds: the lean direction's smoothing, its state's fade
        private const float _floatSink = 0.02f; // pinned feet: the deepest toe tip below the floor
        private const float _floatHover = 0.07f; // hovering: the deepest toe tip over the floor, before the bob (±_floatBob)
        private const float _floatRestHeight = 1.09f; // the standing pose's mean pelvis height, the lean poses' heights' base
        private const float _floatCycle = 1, _floatLength = 2; // seconds: the bob, the slow sway
        private const float _floatBob = 0.032f, _floatBobLag = -0.056f, _floatRaise = 0.05f, _floatDrift = 0.006f; // the pelvis over its pinned feet
        private const float _floatFrontBase = 0.03f, _floatFrontLift = 0.085f, _floatFrontLead = 0.21f, _floatBackLift = 0.012f;
        private const float _floatFootSwing = 0.013f, _floatFrontSwingLag = 0.04f, _floatBackSwingLag = 0.15f;
        // standing, the upper spine's forward lean (degrees, swinging with the bob a little ahead of it) and its tip toward
        // the gun side (swaying slowly)
        private const float _floatHunch = 7.5f, _floatHunchBob = 4.6f, _floatHunchLag = -0.09f, _floatSpineRoll = 11.5f, _floatSpineSway = 1;
        // the gun arm: upper arm and cannon (from straight down) the same in every pose, so the cannon holds still
        private static readonly Vector2 _floatGunArm = new Vector2(92, 2), _floatCannon = new Vector2(90, 0);
        private static readonly Vector3 _floatCannonTip = new Vector3(0.3f, 0, 0); // R_elbow-local (the rig has no R_wrist)

        // a pose in model space (+X her right, +Y up, -Z forward), degrees
        private sealed class FloatPose
        {
            // the hips' and shoulders' lines from her left to her right: yaw (+ = facing turned to her left), drop (+ = her
            // right side lower)
            public Vector2 Hips, Shoulders;
            // the pelvis's up (the hips' middle to Spine_1) and the upper spine (Spine_1 to Spine_2) as LegDir: forward,
            // toward her right, from straight down (about ±180 = up)
            public Vector2 Pelvis, Spine;
            // the same for the left thigh, shin, foot (ankle to toe tip: the rig has no toe bone); the right's; the left
            // upper arm, forearm
            public Vector2[] Limbs = [];
            public Vector3 Head; // its forward down, to her left; roll (+ = top toward her right)
            public float Height; // the pelvis over the standing pose's mean
        }

        // standing: the legs where they hang before the pinned feet bend them (the pelvis raised and bobbing over them);
        // its spine is _floatHunch and _floatSpineRoll
        private static readonly FloatPose _floatStand = new FloatPose
        {
            Hips = new Vector2(-1, -9), Shoulders = new Vector2(12, 15), Pelvis = new Vector2(172, -9),
            Limbs = [new(62, -9), new(-24, 9), new(0, 2), new(48, 13), new(-43, 1), new(4, -1), new(-15, -46), new(61, 0)],
            Head = new Vector3(22, 25, -5)
        };
        // each limb segment's swing standing (degrees, lag in bob cycles): forward, toward her right
        private static readonly (float Fwd, float FwdLag, float Side, float SideLag)[] _floatSwing =
            [default, default, (5, 0.19f, 0, 0), default, default, default, (3.5f, 0.27f, 3, 0.3f), (5, 0.48f, 4, 0.16f)];
        private static readonly (float Degrees, float Lag)[] _floatFootRoll = [(4, 0.53f), (4, -0.38f)]; // each foot rocking
        // moving: forward, back, to her left, to her right (still poses, the legs trailing)
        private static readonly FloatPose[] _floatLeans =
        [
            new FloatPose
            {
                Hips = new(7, -5), Shoulders = new(-10, -17), Pelvis = new(147, -8), Spine = new(148, -2),
                Limbs = [new(-17, -9), new(-62, 2), new(-18, 4), new(24, 7), new(-54, 0), new(-30, -5), new(-5, -24), new(55, 3)],
                Head = new(33, 1, 1), Height = 0.04f
            },
            new FloatPose
            {
                Hips = new(-20, 28), Shoulders = new(-39, -1), Pelvis = new(-168, 26), Spine = new(-147, 20),
                Limbs = [new(71, -6), new(19, -3), new(15, 11), new(94, 20), new(-26, -21), new(-43, -18), new(19, -65), new(69, 4)],
                Head = new(9, -27, 20), Height = 0.01f
            },
            new FloatPose
            {
                Hips = new(18, -34), Shoulders = new(29, -17), Pelvis = new(166, -37), Spine = new(168, -27),
                Limbs = [new(32, 37), new(-41, 50), new(20, 39), new(63, 30), new(-72, 17), new(-8, 42), new(-42, 28), new(-3, -18)],
                Head = new(40, 51, -24), Height = -0.12f
            },
            new FloatPose
            {
                Hips = new(-19, 17), Shoulders = new(-46, 22), Pelvis = new(169, 21), Spine = new(-176, 32),
                Limbs = [new(39, -30), new(-26, -39), new(1, -39), new(58, 13), new(-54, -40), new(-26, -29), new(-64, -65), new(37, -46)],
                Head = new(30, -60, 29), Height = -0.03f
            }
        ];

        // the toe tip on each ankle, ankle-local (Samus_lod0's most forward low corner: -dsstandin dump)
        private static readonly Vector3[] _floatToe = [new Vector3(0.298f, 0.0596f, 0.0066f), new Vector3(0.298f, 0.0596f, -0.0066f)];
        private static readonly string[][] _floatLegNodes = [["L_hip", "L_knee", "L_ankle"], ["R_hip", "R_knee", "R_ankle"]];
        private static Matrix4[] _floatClip = [];
        private static readonly Vector3[] _floatAnklePin = new Vector3[2], _floatKneePin = new Vector3[2];

        private static bool FloatAnim(int anim)
        {
            var a = (PlayerAnimation)anim;
            return a is not (PlayerAnimation.None or PlayerAnimation.Morph or PlayerAnimation.Unmorph or PlayerAnimation.Unused17
                or PlayerAnimation.JumpNeutral or PlayerAnimation.JumpBack or PlayerAnimation.JumpForward
                or PlayerAnimation.JumpLeft or PlayerAnimation.JumpRight);
        }

        // the steps' clips (walks, turn, landings) swapped for the idle, so no step rhythm shows through (the walks' upper
        // body bounced the cannon): drawBiped's first pass (the legs up to the spine) takes HostFloatLegAnim, its second
        // (the upper body) HostFloatUpperAnim
        private AnimationInfo HostFloatLegAnim(Model model) => FloatStill(model, _bipedModel1.AnimInfo, _floatLegAnim);

        private AnimationInfo HostFloatUpperAnim(Model model) => FloatStill(model, _bipedModel2.AnimInfo, _floatUpperAnim);

        private AnimationInfo FloatStill(Model model, AnimationInfo info, AnimationInfo still)
        {
            if (!HostFloatPose || _floatWeight <= 0)
            {
                return info;
            }
            var a = (PlayerAnimation)info.Index[0];
            if (a is not (PlayerAnimation.WalkForward or PlayerAnimation.WalkBackward or PlayerAnimation.WalkLeft
                or PlayerAnimation.WalkRight or PlayerAnimation.Turn or PlayerAnimation.LandNeutral or PlayerAnimation.LandLeft
                or PlayerAnimation.LandRight or PlayerAnimation.Idle))
            {
                return info;
            }
            int idle = (int)PlayerAnimation.Idle;
            if (idle >= model.AnimationGroups.Node.Count)
            {
                return info;
            }
            NodeAnimationGroup group = model.AnimationGroups.Node[idle];
            still.Node.Slot = 0;
            still.Node.Group = group;
            still.Index[0] = idle;
            still.FrameCount[0] = group.FrameCount;
            still.Frame[0] = group.FrameCount > 0 ? (int)(_floatTime * 30) % group.FrameCount : 0;
            return still;
        }

        // drawBiped, after the clips (model space: +X right, +Y up, -Z forward), before the placement
        private void HostFloatPoseModel(Model model)
        {
            int anim = _bipedModel1.AnimInfo.Index[0];
            float dt = MathF.Min(_scene.FrameTime, 0.1f);
            bool on = HostFloatPose && _health > 0 && FloatAnim(anim);
            _floatWeight = on ? MathF.Min(1, _floatWeight + _floatEase * dt) : MathF.Max(0, _floatWeight - _floatEase * dt);
            _floatTime += dt;
            // the lean as Corruption's (NativeBody.Lean): while a walk plays, its direction from how she's actually moving
            // against her facing (the walk's own direction when she's still), smoothed while it lasts, the state fading
            var a = (PlayerAnimation)anim;
            bool leaning = on && (a is PlayerAnimation.WalkForward or PlayerAnimation.WalkBackward or PlayerAnimation.WalkLeft
                or PlayerAnimation.WalkRight);
            if (leaning)
            {
                Vector2 want = a switch
                {
                    PlayerAnimation.WalkForward => Vector2.UnitY,
                    PlayerAnimation.WalkBackward => -Vector2.UnitY,
                    PlayerAnimation.WalkLeft => -Vector2.UnitX,
                    _ => Vector2.UnitX
                };
                var move = new Vector2(Speed.X, Speed.Z);
                float speed = move.Length;
                if (speed > 0.002f) // Speed is per frame
                {
                    var forward = new Vector2(_field70, _field74);
                    var right = new Vector2(-_field74, _field70);
                    want = new Vector2(Vector2.Dot(move, right), Vector2.Dot(move, forward)) / speed;
                }
                _floatLeanDir = _floatLeaning ? Vector2.Lerp(_floatLeanDir, want, 1 - MathF.Exp(-dt / _floatLeanTime)) : want;
            }
            _floatLeaning = leaning;
            _floatLeanOn = leaning ? MathF.Min(1, _floatLeanOn + dt / _floatLeanFade) : MathF.Max(0, _floatLeanOn - dt / _floatLeanFade);
            if (!HostFloatPose || _floatWeight <= 0)
            {
                return;
            }
            // the aim's pitch, as drawBiped gives the spine
            float limit = Fixed.ToFloat(2896);
            float sin = _facingVector.Y;
            float cos = MathF.Sqrt(MathF.Max(0, 1 - sin * sin));
            if (MathF.Abs(sin) > limit)
            {
                cos = limit;
                sin = sin <= 0 ? -limit : limit;
            }
            float aim = MathHelper.RadiansToDegrees(MathF.Atan2(sin, cos));
            ApplyHostFloatPose(model, _floatWeight, _floatTime, FloatLeanWeights(_floatLeanDir) * _floatLeanOn, aim);
        }

        // the lean poses' weights (forward, back, left, right) for a direction (x: right, y: forward), as Corruption's: a
        // full-length direction's sum to 1, a shorter one's (passing through standing) to its length
        public static Vector4 FloatLeanWeights(Vector2 dir)
        {
            float sum = MathF.Abs(dir.X) + MathF.Abs(dir.Y);
            float m = sum > 1e-4f ? MathF.Min(1, dir.Length) / sum : 0;
            return new Vector4(MathF.Max(0, dir.Y), MathF.Max(0, -dir.Y), MathF.Max(0, -dir.X), MathF.Max(0, dir.X)) * m;
        }

        // the pose itself, `weight` (0..1) of the way from the clips' pose; `lean` the lean poses' weights (the rest
        // standing), `aim` the aim's pitch in degrees (+ = up); public for the PC check (Tools -dsstandin float)
        public static void ApplyHostFloatPose(Model model, float weight, float time, Vector4 lean, float aim)
        {
            int root = BipedNodeIndex(model, "Skeleton_Root");
            int pelvis = BipedNodeIndex(model, "Pelvis");
            int spine1 = BipedNodeIndex(model, "Spine_1");
            int spine2 = BipedNodeIndex(model, "Spine_2");
            int head = BipedNodeIndex(model, "Head_1");
            int gunArm = BipedNodeIndex(model, "R_shoulder");
            int cannon = BipedNodeIndex(model, "R_elbow");
            int shoulder = BipedNodeIndex(model, "L_shoulder");
            int elbow = BipedNodeIndex(model, "L_elbow");
            int wrist = BipedNodeIndex(model, "L_wrist");
            int lHip = BipedNodeIndex(model, "L_hip");
            int rHip = BipedNodeIndex(model, "R_hip");
            if (root < 0 || pelvis < 0 || spine1 < 0 || spine2 < 0 || head < 0 || gunArm < 0 || cannon < 0 || shoulder < 0
                || elbow < 0 || wrist < 0 || lHip < 0 || rHip < 0)
            {
                return;
            }
            for (int leg = 0; leg < 2; leg++)
            {
                foreach (string name in _floatLegNodes[leg])
                {
                    if (BipedNodeIndex(model, name) < 0)
                    {
                        return;
                    }
                }
            }
            // the clips' pose, for the ease in and out
            if (_floatClip.Length < model.Nodes.Count)
            {
                _floatClip = new Matrix4[model.Nodes.Count];
            }
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                _floatClip[i] = model.Nodes[i].Animation;
            }
            float Behind(float lag) => MathF.Sin(MathF.Tau * (time / _floatCycle - lag));
            float slow = MathF.Sin(MathF.Tau * time / _floatLength);
            Vector3 At(int index) => model.Nodes[index].Animation.ExtractTranslation();
            float stand = MathF.Max(0, 1 - (lean.X + lean.Y + lean.Z + lean.W));
            // a direction blended over the poses: summed by weight, renormalized
            Vector3 Blend(Vector3 standing, int part, int item)
            {
                Vector3 v = standing * stand;
                for (int k = 0; k < 4; k++)
                {
                    if (lean[k] > 0)
                    {
                        v += FloatPart(_floatLeans[k], part, item) * lean[k];
                    }
                }
                return v.LengthSquared > 1e-8f ? v.Normalized() : standing;
            }
            Vector3 Limb(int i)
            {
                Vector2 d = _floatStand.Limbs[i];
                (float fwd, float fwdLag, float side, float sideLag) = _floatSwing[i];
                return Blend(LegDir(d.X + fwd * Behind(fwdLag), d.Y + side * Behind(sideLag)), 4, i);
            }

            // the pelvis (the hips' line, then its up), about itself
            Vector3 hips = Blend(FloatLine(_floatStand.Hips), 0, 0);
            Vector3 pelvisUp = Blend(LegDir(_floatStand.Pelvis.X, _floatStand.Pelvis.Y), 2, 0);
            FloatTurn(model, root, At(pelvis), FloatFrame(At(rHip) - At(lHip), At(spine1) - (At(lHip) + At(rHip)) / 2, hips, pelvisUp));
            // the upper body (the upper spine, then the shoulders' line), about Spine_1
            Vector3 spine = Blend(LegDir(180 - _floatHunch - _floatHunchBob * Behind(_floatHunchLag), _floatSpineRoll + _floatSpineSway * slow), 3, 0);
            Vector3 shoulders = Blend(FloatLine(_floatStand.Shoulders), 1, 0);
            FloatTurn(model, spine1, At(spine1), FloatFrame(At(spine2) - At(spine1), At(gunArm) - At(shoulder), spine, shoulders));
            // the head: its forward and up (Head_1's Z and Y, as the Corruption rig's)
            (Vector3 standF, Vector3 standU) = FloatHead(_floatStand.Head);
            Vector3 headF = Blend(standF, 5, 0);
            Vector3 headU = Blend(standU, 6, 0);
            Matrix4 headNow = model.Nodes[head].Animation;
            FloatTurn(model, head, At(head), FloatFrame(Vector3.TransformVector(Vector3.UnitZ, headNow),
                Vector3.TransformVector(Vector3.UnitY, headNow), headF, headU));
            // the gun arm straight out, the free arm
            FloatAim(model, gunArm, At(cannon), LegDir(_floatGunArm.X, _floatGunArm.Y), 1);
            FloatAim(model, cannon, Vector3.TransformPosition(_floatCannonTip, model.Nodes[cannon].Animation),
                LegDir(_floatCannon.X, _floatCannon.Y), 1);
            FloatAim(model, shoulder, At(elbow), Limb(6), 1);
            FloatAim(model, elbow, At(wrist), Limb(7), 1);
            // the aim, tilting everything from the spine up as vanilla's does
            FloatTurn(model, spine1, At(spine1), FloatPitch(-aim));

            // the legs hung on their directions, where the feet are pinned
            for (int leg = 0; leg < 2; leg++)
            {
                int hip = BipedNodeIndex(model, _floatLegNodes[leg][0]);
                int knee = BipedNodeIndex(model, _floatLegNodes[leg][1]);
                int ankle = BipedNodeIndex(model, _floatLegNodes[leg][2]);
                FloatAim(model, hip, At(knee), Limb(leg * 3), 1);
                FloatAim(model, knee, At(ankle), Limb(leg * 3 + 1), 1);
                _floatAnklePin[leg] = At(ankle);
                _floatKneePin[leg] = At(knee);
            }

            // standing, the pelvis bobbing and drifting over the pinned feet; the knees bend and straighten to keep the feet
            // there, the front one lifting a little ahead of the bob (the lean poses are still)
            var move = stand * new Vector3(_floatDrift * slow, _floatBob * Behind(_floatBobLag) + _floatRaise, 0);
            TurnBipedSubtree(model, root, Matrix4.CreateTranslation(move));
            float pin = HostFloatFeetPinned ? 1 : 0;
            float front = 0.5f * (1 + Behind(-_floatFrontLead));
            float lowest = Single.MaxValue;
            for (int leg = 0; leg < 2; leg++)
            {
                int hip = BipedNodeIndex(model, _floatLegNodes[leg][0]);
                int knee = BipedNodeIndex(model, _floatLegNodes[leg][1]);
                int ankle = BipedNodeIndex(model, _floatLegNodes[leg][2]);
                Vector3 lift = stand * (leg == 0
                    ? new Vector3(0, _floatFrontBase + _floatFrontLift * front, -_floatFootSwing * Behind(_floatFrontSwingLag))
                    : new Vector3(0, _floatBackLift * (1 - front), -_floatFootSwing * Behind(_floatBackSwingLag)));
                Vector3 target = _floatAnklePin[leg] + lift + move * (1 - pin);
                Vector3 hipAt = At(hip);
                float upperLen = (At(knee) - hipAt).Length, lowerLen = (At(ankle) - At(knee)).Length;
                Vector3 to = target - hipAt;
                if (to.LengthSquared < 1e-8f)
                {
                    continue;
                }
                float reach = Math.Clamp(to.Length, MathF.Abs(upperLen - lowerLen) + 1e-3f, upperLen + lowerLen - 1e-3f);
                Vector3 axis = to.Normalized();
                Vector3 bend = _floatKneePin[leg] + move - hipAt; // the knee stays on the side it hung on
                bend -= axis * Vector3.Dot(bend, axis);
                if (bend.LengthSquared < 1e-8f)
                {
                    bend = -Vector3.UnitZ - axis * Vector3.Dot(-Vector3.UnitZ, axis);
                }
                bend.Normalize();
                float along = (upperLen * upperLen - lowerLen * lowerLen + reach * reach) / (2 * reach);
                Vector3 kneeTo = hipAt + axis * along + bend * MathF.Sqrt(MathF.Max(0, upperLen * upperLen - along * along));
                FloatAim(model, hip, At(knee), (kneeTo - hipAt).Normalized(), 1);
                FloatAim(model, knee, At(ankle), (hipAt + axis * reach - kneeTo).Normalized(), 1);
                FloatAim(model, ankle, Vector3.TransformPosition(_floatToe[leg], model.Nodes[ankle].Animation), Limb(leg * 3 + 2), 1);
                Vector3 toe = Vector3.TransformPosition(_floatToe[leg], model.Nodes[ankle].Animation);
                Vector3 foot = toe - At(ankle);
                if (foot.LengthSquared > 1e-8f)
                {
                    (float degrees, float lag) = _floatFootRoll[leg];
                    FloatTurn(model, ankle, At(ankle), Quaternion.FromAxisAngle(foot.Normalized(),
                        MathHelper.DegreesToRadians(stand * degrees * Behind(lag))));
                }
                // hovering: the toe's height without the bob and the lift, so the bob shows against the floor
                float toeY = Vector3.TransformPosition(_floatToe[leg], model.Nodes[ankle].Animation).Y;
                lowest = MathF.Min(lowest, HostFloatFeetPinned ? toeY : toeY - (move.Y - stand * _floatRaise) - lift.Y);
            }

            // standing, the deepest toe tip just into the floor (pinned) or clear of it (hovering); leaning, the pelvis at
            // the standing pose's mean height plus the lean poses' own
            float height = lean.X * _floatLeans[0].Height + lean.Y * _floatLeans[1].Height + lean.Z * _floatLeans[2].Height
                + lean.W * _floatLeans[3].Height;
            float shift = stand * ((HostFloatFeetPinned ? -_floatSink : _floatHover) - lowest)
                + (1 - stand) * (_floatRestHeight - At(pelvis).Y) + height;
            Matrix4 up = Matrix4.CreateTranslation(0, shift, 0);
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                model.Nodes[i].Animation *= up;
            }

            // eased: each joint's change from the clips' pose taken `weight` of the way
            float w = weight * weight * (3 - 2 * weight);
            if (w < 0.999f)
            {
                for (int i = 0; i < model.Nodes.Count; i++)
                {
                    Matrix4 change = _floatClip[i].Inverted() * model.Nodes[i].Animation;
                    Quaternion turn = Quaternion.Slerp(Quaternion.Identity, change.ExtractRotation(), w);
                    model.Nodes[i].Animation = _floatClip[i] * Matrix4.CreateFromQuaternion(turn) * Matrix4.CreateTranslation(change.ExtractTranslation() * w);
                }
            }
        }

        // one of a pose's directions: 0 hips, 1 shoulders, 2 pelvis up, 3 spine, 4 limb `item`, 5 head forward, 6 head up
        private static Vector3 FloatPart(FloatPose pose, int part, int item) => part switch
        {
            0 => FloatLine(pose.Hips),
            1 => FloatLine(pose.Shoulders),
            2 => LegDir(pose.Pelvis.X, pose.Pelvis.Y),
            3 => LegDir(pose.Spine.X, pose.Spine.Y),
            4 => LegDir(pose.Limbs[item].X, pose.Limbs[item].Y),
            5 => FloatHead(pose.Head).F,
            _ => FloatHead(pose.Head).U
        };

        // a line's direction from (yaw, drop) degrees: see FloatPose.Hips
        private static Vector3 FloatLine(Vector2 line)
        {
            float y = MathHelper.DegreesToRadians(line.X), d = MathHelper.DegreesToRadians(line.Y);
            return new Vector3(MathF.Cos(d) * MathF.Cos(y), -MathF.Sin(d), -MathF.Cos(d) * MathF.Sin(y));
        }

        // the head's forward and up from (down, to her left, roll) degrees: the up square to the forward with the roll's
        // sideways lean, the one nearest upright
        private static (Vector3 F, Vector3 U) FloatHead(Vector3 h)
        {
            float d = MathHelper.DegreesToRadians(h.X), l = MathHelper.DegreesToRadians(h.Y);
            var f = new Vector3(-MathF.Sin(l) * MathF.Cos(d), -MathF.Sin(d), -MathF.Cos(l) * MathF.Cos(d));
            Vector3 e1 = Vector3.UnitY - f * f.Y;
            if (e1.LengthSquared < 1e-6f)
            {
                return (f, Vector3.UnitZ);
            }
            e1.Normalize();
            Vector3 e2 = Vector3.Cross(f, e1);
            float r = MathF.Sin(MathHelper.DegreesToRadians(h.Z)), len = MathF.Sqrt(e1.X * e1.X + e2.X * e2.X);
            if (len < 1e-4f)
            {
                return (f, e1);
            }
            float phi = MathF.Atan2(e2.X, e1.X), spread = MathF.Acos(Math.Clamp(r / len, -1, 1));
            static float Wrap(float x) => x - MathF.Tau * MathF.Round(x / MathF.Tau);
            float a1 = Wrap(phi - spread), a2 = Wrap(phi + spread);
            float a = MathF.Abs(a1) < MathF.Abs(a2) ? a1 : a2;
            return (f, e1 * MathF.Cos(a) + e2 * MathF.Sin(a));
        }

        // the turn taking a frame (a, then b squared to it) onto another (toA, then toB squared to it)
        private static Matrix4 FloatFrame(Vector3 a, Vector3 b, Vector3 toA, Vector3 toB)
        {
            static Matrix3 Basis(Vector3 x, Vector3 y)
            {
                x.Normalize();
                y = (y - x * Vector3.Dot(y, x)).Normalized();
                return new Matrix3(x, y, Vector3.Cross(x, y));
            }
            Matrix3 from = Basis(a, b);
            from.Transpose();
            return new Matrix4(from * Basis(toA, toB)); // row vectors: into the first frame's axes, out along the second's
        }

        // turns in model space, in degrees: the top forward, the top toward her right, the face toward her left
        private static Quaternion FloatPitch(float degrees) => Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(-degrees));
        private static Quaternion FloatRoll(float degrees) => Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.DegreesToRadians(-degrees));
        private static Quaternion FloatYaw(float degrees) => Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(degrees));

        // a direction from straight down, forward (-Z) by `forward` degrees and out (+X) by `side`
        private static Vector3 LegDir(float forward, float side)
        {
            float f = MathHelper.DegreesToRadians(forward), s = MathHelper.DegreesToRadians(side);
            return new Vector3(MathF.Sin(s), -MathF.Cos(f) * MathF.Cos(s), -MathF.Sin(f) * MathF.Cos(s)).Normalized();
        }

        // a joint and everything below it turned about `pivot` (model space)
        private static void FloatTurn(Model model, int index, Vector3 pivot, Quaternion turn)
        {
            FloatTurn(model, index, pivot, Matrix4.CreateFromQuaternion(turn));
        }

        private static void FloatTurn(Model model, int index, Vector3 pivot, Matrix4 turn)
        {
            TurnBipedSubtree(model, index, Matrix4.CreateTranslation(-pivot) * turn * Matrix4.CreateTranslation(pivot));
        }

        // a joint turned (shortest arc, `w` of the way) so the segment to `tip` points along `target`
        private static void FloatAim(Model model, int index, Vector3 tip, Vector3 target, float w)
        {
            Vector3 pivot = model.Nodes[index].Animation.ExtractTranslation();
            Vector3 d = tip - pivot;
            if (d.LengthSquared < 1e-8f)
            {
                return;
            }
            d.Normalize();
            float dot = Math.Clamp(Vector3.Dot(d, target), -1, 1);
            Vector3 axis = Vector3.Cross(d, target);
            if (axis.LengthSquared < 1e-10f)
            {
                return;
            }
            FloatTurn(model, index, pivot, Quaternion.FromAxisAngle(axis.Normalized(), MathF.Acos(dot) * w));
        }
    }
}
