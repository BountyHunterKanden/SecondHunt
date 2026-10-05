using System;
using System.Collections.Generic;
using MphRead;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Per-node local channels of one pose: scale, rotation (radians, applied X then Y then Z like the
    // game), translation. Arrays are indexed by the model's node index.
    public sealed class SkeletonPose
    {
        public readonly Vector3[] S, R, T;
        public readonly bool[] Animated;
        public SkeletonPose(int n)
        {
            S = new Vector3[n]; R = new Vector3[n]; T = new Vector3[n]; Animated = new bool[n];
            for (int i = 0; i < n; i++) S[i] = Vector3.One;
        }
        public void CopyFrom(SkeletonPose o)
        {
            Array.Copy(o.S, S, S.Length); Array.Copy(o.R, R, R.Length); Array.Copy(o.T, T, T.Length); Array.Copy(o.Animated, Animated, Animated.Length);
        }
    }

    // A DS hunter skeleton as plain data + a forward-kinematics solver that reproduces the game's
    // posing math (Model.AnimateNode / AnimateNodes / PlayerDraw's two-layer split + spine aim) WITHOUT
    // mutating the shared MphRead Model -- and with one extra degree of freedom the game never needs:
    // a per-node LENGTH factor on the translation channel, so the same clips can drive a skeleton whose
    // bones are longer or shorter than the DS rig's (retargeting onto a Brawl trophy's proportions).
    // With all length factors = 1 it must equal MphRead's matrices exactly (checked by -animtest).
    public sealed class DsSkeleton
    {
        public readonly string[] Names;
        public readonly int[] Parent;
        public readonly int Count;
        public readonly int Spine;          // "Spine_1": legs layer up to here, torso layer above, aim tilt here
        public readonly bool[] AboveSpine;  // strict descendants of Spine_1 (torso layer)
        readonly int[] _order;              // parents before children
        readonly Vector3 _modelScale;

        public DsSkeleton(Model m)
        {
            Count = m.Nodes.Count;
            Names = new string[Count]; Parent = new int[Count];
            for (int i = 0; i < Count; i++) { Names[i] = m.Nodes[i].Name; Parent[i] = m.Nodes[i].ParentIndex; }
            _modelScale = m.Scale;
            Spine = Array.IndexOf(Names, "Spine_1");
            AboveSpine = new bool[Count];
            for (int i = 0; i < Count; i++)
                for (int p = Parent[i]; p >= 0; p = Parent[p])
                    if (p == Spine) { AboveSpine[i] = true; break; }
            var order = new List<int>();
            var placed = new bool[Count];
            while (order.Count < Count)
                for (int i = 0; i < Count; i++)
                    if (!placed[i] && (Parent[i] < 0 || placed[Parent[i]])) { placed[i] = true; order.Add(i); }
            _order = order.ToArray();
        }

        public int IndexOf(string name) => Array.IndexOf(Names, name);
        public Vector3 ModelScale => _modelScale;

        // The local matrices Fk chains (parent-local; the spine aim folded into Spine_1's; identity where not animated):
        // world[i] = local[i] * world[parent] for animated nodes, identity otherwise. Their translation row IS the node's
        // offset in model units / model scale -- a page that moves joints swaps just that row (Pose Studio).
        public void Locals(SkeletonPose p, Matrix4[] local, float aimFacingY = 0f, float[]? lengths = null, Vector3?[]? offsets = null)
        {
            Matrix4 aim = Matrix4.CreateRotationZ(HunterRig.SpineAngle(aimFacingY));
            for (int i = 0; i < Count; i++)
            {
                if (!p.Animated[i]) { local[i] = Matrix4.Identity; continue; }
                var m = offsets != null && offsets.Length > i && offsets[i] is Vector3 o ? LocalWithOffset(p, i, o) : Local(p, i, lengths != null && lengths.Length > i ? lengths[i] : 1f);
                local[i] = i == Spine ? aim * m : m;
            }
        }

        // Sample every node's channels from a clip at an integer frame (the game's LUT lookup).
        public static void Sample(Model m, int clip, int frame, SkeletonPose into, bool[]? onlyNodes = null)
        {
            NodeAnimationGroup g = m.AnimationGroups.Node[clip];
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                if (onlyNodes != null && !onlyNodes[i]) continue;
                if (!g.Animations.TryGetValue(m.Nodes[i].Name, out NodeAnimation a))
                {
                    into.Animated[i] = false; into.S[i] = Vector3.One; into.R[i] = Vector3.Zero; into.T[i] = Vector3.Zero;
                    continue;
                }
                int f = frame, n = g.FrameCount;
                into.Animated[i] = true;
                into.S[i] = new Vector3(m.InterpolateAnimation(g.Scales, a.ScaleLutIndexX, f, a.ScaleBlendX, a.ScaleLutLengthX, n),
                                        m.InterpolateAnimation(g.Scales, a.ScaleLutIndexY, f, a.ScaleBlendY, a.ScaleLutLengthY, n),
                                        m.InterpolateAnimation(g.Scales, a.ScaleLutIndexZ, f, a.ScaleBlendZ, a.ScaleLutLengthZ, n));
                into.R[i] = new Vector3(m.InterpolateAnimation(g.Rotations, a.RotateLutIndexX, f, a.RotateBlendX, a.RotateLutLengthX, n, isRotation: true),
                                        m.InterpolateAnimation(g.Rotations, a.RotateLutIndexY, f, a.RotateBlendY, a.RotateLutLengthY, n, isRotation: true),
                                        m.InterpolateAnimation(g.Rotations, a.RotateLutIndexZ, f, a.RotateBlendZ, a.RotateLutLengthZ, n, isRotation: true));
                into.T[i] = new Vector3(m.InterpolateAnimation(g.Translations, a.TranslateLutIndexX, f, a.TranslateBlendX, a.TranslateLutLengthX, n),
                                        m.InterpolateAnimation(g.Translations, a.TranslateLutIndexY, f, a.TranslateBlendY, a.TranslateLutLengthY, n),
                                        m.InterpolateAnimation(g.Translations, a.TranslateLutIndexZ, f, a.TranslateBlendZ, a.TranslateLutLengthZ, n));
            }
        }

        // The two-layer pose the game draws: legs clip for the root..Spine_1, torso clip above it.
        public void SampleBiped(Model m, BipedAnimator a, SkeletonPose into)
        {
            var legs = new bool[Count]; var torso = new bool[Count];
            for (int i = 0; i < Count; i++) { if (AboveSpine[i]) torso[i] = true; else legs[i] = true; }
            Sample(m, a.Legs.AnimInfo.Index[0], a.Legs.AnimInfo.Frame[0], into, legs);
            Sample(m, a.Torso.AnimInfo.Index[0], a.Torso.AnimInfo.Frame[0], into, torso);
        }

        // Some nodes re-sampled from the LEGS layer's clip (its own frame): an off hand that keeps standing / walking while
        // the torso layer fires (the Space Pirates fire one-handed; the hunter's shooting clips bring both arms to the gun)
        public void SampleFromLegs(Model m, BipedAnimator a, SkeletonPose into, bool[] nodes) =>
            Sample(m, a.Legs.AnimInfo.Index[0], a.Legs.AnimInfo.Frame[0], into, nodes);

        // a node and everything below it
        public bool[] Subtree(string root)
        {
            var mark = new bool[Count]; int r = IndexOf(root);
            if (r < 0) return mark;
            for (int i = 0; i < Count; i++)
                for (int p = i; p >= 0; p = Parent[p]) if (p == r) { mark[i] = true; break; }
            return mark;
        }

        // Local matrix exactly as Model.AnimateNode builds it: Scale * RotX * RotY * RotZ * Translate
        // (row-vector convention), translation divided by the model scale -- times our length factor.
        public Matrix4 Local(SkeletonPose p, int i, float length = 1f)
        {
            var t = p.T[i] * length;
            var m = Matrix4.CreateTranslation(t.X / _modelScale.X, t.Y / _modelScale.Y, t.Z / _modelScale.Z);
            m = Matrix4.CreateRotationX(p.R[i].X) * Matrix4.CreateRotationY(p.R[i].Y) * Matrix4.CreateRotationZ(p.R[i].Z) * m;
            return Matrix4.CreateScale(p.S[i]) * m;
        }

        Matrix4 LocalWithOffset(SkeletonPose p, int i, Vector3 offset)
        {
            var m = Matrix4.CreateTranslation(offset.X / _modelScale.X, offset.Y / _modelScale.Y, offset.Z / _modelScale.Z);
            m = Matrix4.CreateRotationX(p.R[i].X) * Matrix4.CreateRotationY(p.R[i].Y) * Matrix4.CreateRotationZ(p.R[i].Z) * m;
            return Matrix4.CreateScale(p.S[i]) * m;
        }

        // World (model-space) matrices. aimFacingY applies the game's spine tilt (HunterRig's clamp).
        // lengths: per-node translation factors (null = the DS rig's own proportions).
        // offsets: per-node REST OFFSET overrides (parent-local, model units) -- a retargeted skeleton's own
        // measured bone vectors; where set they replace the clip's (constant) bone translation, so the
        // joints sit exactly where the trophy's are while the clip still drives every rotation.
        public void Fk(SkeletonPose p, Matrix4[] world, float aimFacingY = 0f, float[]? lengths = null, Vector3?[]? offsets = null)
        {
            Matrix4 aim = Matrix4.CreateRotationZ(HunterRig.SpineAngle(aimFacingY));
            foreach (int i in _order)
            {
                if (!p.Animated[i]) { world[i] = Matrix4.Identity; continue; } // AnimateNodes: unanimated -> identity
                Matrix4 w = offsets?[i] is Vector3 o ? LocalWithOffset(p, i, o) : Local(p, i, lengths?[i] ?? 1f);
                if (Parent[i] >= 0) w *= world[Parent[i]];
                if (i == Spine) w = aim * w;                                    // PlayerDraw: AfterTransform on Spine_1
                world[i] = w;
            }
        }
    }
}
