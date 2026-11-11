using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // The DEATH look option's FIRST HUNT value (owner 2026-11-07, docs/IDEAS_SURVEY.md D1): the cart's multiplayer death
    // collapse (samus_hi_yellow's clip 0, PlayerEntity.DeathClip in MPH's order) retargeted onto each MPH hunter's rig and
    // added to its body models as clip DeathClip, which PlayerEntity.DeathCollapse plays. Nothing of the cart is copied
    // into ours: it's baked at run time from the user's own First Hunt import, and without one the option dies as MPH.
    //
    // The retarget: the cart's body is MPH Samus's rig with its limbs flattened onto Pelvis / Spine_2, so each of its
    // nodes has an MPH twin (Map). Every frame, each twin turns in model space by the same amount the cart's node turned
    // from its Idle pose (world delta); a node with no twin turns with its nearest twin above it (Trace's collars turn
    // with Spine_2). Bones keep the hunter's own lengths. The hips follow the cart's pelvis, its travel scaled by hip
    // height, and the body is lifted where a joint would sink below the hunter's own feet. The result is keyed every
    // frame (blend 1) on every node the hunter's Idle animates.
    public static class DeathCollapse
    {
        // hunter node -> the cart's node it follows
        static readonly Dictionary<string, string> Map = new()
        {
            ["Skeleton_Root"] = "Pelvis", ["Pelvis"] = "Pelvis",
            ["L_hip"] = "Geo_L_Thigh", ["L_knee"] = "Geo_L_Calf", ["L_ankle"] = "Geo_L_Foot",
            ["R_hip"] = "Geo_R_Thigh", ["R_knee"] = "Geo_R_Calf", ["R_ankle"] = "Geo_R_Foot",
            ["Spine_2"] = "Spine_2", ["Head_1"] = "Geo_Head",
            ["L_shoulder"] = "L_shoulder", ["L_elbow"] = "L_elbow", ["L_wrist"] = "L_wrist",
            ["R_shoulder"] = "R_shoulder", ["R_elbow"] = "R_elbow", ["R_wrist"] = "R_wrist",
            ["L_varias2_SDK"] = "Geo_L_Shoulder", ["R_varias2_SDK"] = "Geo_R_Shoulder",
        };

        const int Idle = (int)PlayerAnimation.Idle;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Model, object> _done = new(); // (a replay seek's old scenes' models go)

        // the app's data folder when RECOMP SETTINGS' DEATH is FIRST HUNT (null = no collapse): each match's scene
        // (CampaignHost.StartMatch) prepares its fresh models from it. Dev only for now (owner 2026-11-09: First Hunt
        // isn't public yet).
        public static string? Root { get; set; }

        // DEATH is RAGDOLL: each match runs the ragdoll (PlayerRagdoll.cs). Independent of First Hunt (owner 2026-11-09:
        // "ragdoll in next release without first hunt at all"), every build.
        public static bool Ragdoll { get; set; }

        // Root and Ragdoll for a DEATH value (CampaignLaunch, and the keyframe prepare through SetFrom)
        public static void Set(string? death, string dataDir)
        {
            Root = death == "firsthunt" && !Config.RecompSettings.PublicBuild ? dataDir : null;
            Ragdoll = death == "ragdoll";
        }

        // Set as RECOMP SETTINGS in dataDir has it, read without touching the file (no backup restore or move-aside from
        // a second process). The keyframe prepare's own process (App ReplayPrepHost) sets them as CampaignLaunch does: a
        // prepare without the clip walks a different match start and the viewer refuses its keyframes (owner's Combat
        // Hall replay, 2026-11-07).
        public static void SetFrom(string dataDir) => Set(DeathFrom(dataDir), dataDir);

        private static string? DeathFrom(string dataDir)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(
                    System.IO.Path.Combine(dataDir, "recomp_settings.json")));
                foreach (System.Text.Json.JsonProperty p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Equals(nameof(Config.RecompSettings.DeathAnimation), StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : null;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or InvalidOperationException) { }
            return null;
        }

        // Give every hunter's body models (both LODs) the collapse. False (nothing changed) without a First Hunt import.
        public static bool Prepare(string? importRoot)
        {
            if (!FirstHunt.FirstHuntGame.Register(importRoot)) return false;
            Model fh = PlayerEntity.FirstHuntBody().Model;
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel, Hunter.Guardian })
            {
                Add(Read.GetModelInstance(Metadata.HunterModels[h][0]).Model, fh);
                Add(Read.GetModelInstance(Metadata.HunterModels[h][1]).Model, fh);
            }
            return true;
        }

        // Append the collapse to one body model as clip DeathClip (once per model). False if its rig doesn't fit.
        public static bool Add(Model target, Model fh)
        {
            if (_done.TryGetValue(target, out _)) return true;
            AnimationGroups g = target.AnimationGroups;
            if (g.Node is not List<NodeAnimationGroup> nodes || g.Material is not List<MaterialAnimationGroup> mats
                || g.Texcoord is not List<TexcoordAnimationGroup> uvs || g.Texture is not List<TextureAnimationGroup> texs
                || nodes.Count != PlayerEntity.DeathClip || nodes.Count <= Idle)
            {
                return false;
            }
            NodeAnimationGroup? clip = Bake(target, fh);
            if (clip == null) return false;
            nodes.Add(clip);
            Pad(mats, MaterialAnimationGroup.Empty);
            Pad(uvs, TexcoordAnimationGroup.Empty);
            Pad(texs, TextureAnimationGroup.Empty);
            _done.AddOrUpdate(target, target);
            return true;
        }

        static void Pad<T>(List<T> list, Func<T> empty)
        {
            while (list.Count <= PlayerEntity.DeathClip) list.Add(empty());
        }

        // The retargeted clip, or null when the rig has no hips or the cart's body has no collapse.
        public static NodeAnimationGroup? Bake(Model target, Model fh)
        {
            if (fh.AnimationGroups.Node.Count <= PlayerEntity.DeathClip) return null;
            var src = new DsSkeleton(fh);
            var dst = new DsSkeleton(target);
            int frames = fh.AnimationGroups.Node[PlayerEntity.DeathClip].FrameCount;
            int srcHips = src.IndexOf("Pelvis");
            int hips = dst.IndexOf("Pelvis") is int p && p >= 0 ? p : dst.IndexOf("Skeleton_Root");
            if (frames <= 0 || srcHips < 0 || hips < 0) return null;

            // the twin each node turns with: its own, else its nearest mapped ancestor's (-1: keeps its Idle pose)
            var twin = new int[dst.Count];
            for (int i = 0; i < dst.Count; i++)
            {
                twin[i] = -1;
                for (int a = i; a >= 0 && twin[i] < 0; a = dst.Parent[a])
                    if (Map.TryGetValue(dst.Names[a], out string? s)) twin[i] = src.IndexOf(s);
            }

            var srcPose = new SkeletonPose(src.Count); var srcIdle = new Matrix4[src.Count]; var srcW = new Matrix4[src.Count];
            DsSkeleton.Sample(fh, Idle, 0, srcPose); src.Fk(srcPose, srcIdle);
            var idle = new SkeletonPose(dst.Count); var dstIdle = new Matrix4[dst.Count];
            DsSkeleton.Sample(target, Idle, 0, idle); dst.Fk(idle, dstIdle);
            float reach = dstIdle[hips].ExtractTranslation().Y / MathF.Max(0.01f, srcIdle[srcHips].ExtractTranslation().Y);
            float floor = float.MaxValue;
            for (int i = 0; i < dst.Count; i++) if (idle.Animated[i]) floor = MathF.Min(floor, dstIdle[i].ExtractTranslation().Y);
            Vector3 scale = target.Scale;
            // the scale each node's children inherit (Trace's and Noxus's Dummy_Root shrinks the whole body)
            var grown = new float[dst.Count];
            for (int i = 0; i < dst.Count; i++) grown[i] = dstIdle[i].Row0.Xyz.Length;

            var rot = new List<float>(); var trans = new List<float>(); var scales = new List<float>();
            var anims = new Dictionary<string, NodeAnimation>();
            var r = new Vector3[dst.Count, frames]; var t = new Vector3[dst.Count, frames];
            var world = new Matrix3[dst.Count]; var pos = new Vector3[dst.Count];
            for (int f = 0; f < frames; f++)
            {
                DsSkeleton.Sample(fh, PlayerEntity.DeathClip, f, srcPose); src.Fk(srcPose, srcW);
                // world rotations: the twin's turn since Idle on top of the hunter's Idle; positions down the hunter's bones
                foreach (int i in Order(dst))
                {
                    Matrix3 rest = Rot(dstIdle[i]);
                    world[i] = twin[i] >= 0 ? rest * Rot(srcIdle[twin[i]]).Inverted() * Rot(srcW[twin[i]]) : rest;
                    if (dst.Parent[i] < 0) { pos[i] = dstIdle[i].ExtractTranslation(); continue; }
                    // the bone from the parent, as the Idle pose has it, carried by the parent's turn
                    int pa = dst.Parent[i];
                    Vector3 bone = (dstIdle[i].ExtractTranslation() - dstIdle[pa].ExtractTranslation()) * Rot(dstIdle[pa]).Inverted();
                    pos[i] = pos[pa] + bone * world[pa];
                }
                // the hips follow the cart's pelvis; nothing sinks below the feet
                Vector3 hipsAt = dstIdle[hips].ExtractTranslation() + (srcW[srcHips].ExtractTranslation() - srcIdle[srcHips].ExtractTranslation()) * reach;
                Vector3 shift = hipsAt - pos[hips];
                float low = float.MaxValue;
                for (int i = 0; i < dst.Count; i++) if (dst.Parent[i] >= 0 && idle.Animated[i]) low = MathF.Min(low, pos[i].Y + shift.Y);
                if (low < floor) shift.Y += floor - low;
                for (int i = 0; i < dst.Count; i++) if (dst.Parent[i] >= 0) pos[i] += shift;
                // back to the game's local channels
                for (int i = 0; i < dst.Count; i++)
                {
                    int pa = dst.Parent[i];
                    Matrix3 local = pa >= 0 ? world[i] * world[pa].Inverted() : world[i];
                    Vector3 offset = pa >= 0 ? (pos[i] - pos[pa]) * world[pa].Inverted() / grown[pa] : pos[i];
                    Vector3 e = Euler(local);
                    if (f > 0) e = new Vector3(Near(e.X, r[i, f - 1].X), Near(e.Y, r[i, f - 1].Y), Near(e.Z, r[i, f - 1].Z));
                    r[i, f] = e;
                    t[i, f] = offset * scale; // (Local divides by the model scale)
                }
            }
            for (int i = 0; i < dst.Count; i++)
            {
                if (!idle.Animated[i]) continue;
                var a = new NodeAnim
                {
                    ScaleLength = 1,
                    ScaleIndex = (ushort)scales.Count,
                    RotateBlend = 1, RotateLength = (ushort)frames,
                    TranslateBlend = 1, TranslateLength = (ushort)frames,
                };
                scales.Add(idle.S[i].X); scales.Add(idle.S[i].Y); scales.Add(idle.S[i].Z);
                for (int axis = 0; axis < 3; axis++)
                {
                    a.RotateIndex[axis] = (ushort)rot.Count;
                    for (int f = 0; f < frames; f++) rot.Add(r[i, f][axis]);
                    a.TranslateIndex[axis] = (ushort)trans.Count;
                    for (int f = 0; f < frames; f++) trans.Add(t[i, f][axis]);
                }
                if (rot.Count > ushort.MaxValue || trans.Count > ushort.MaxValue) return null;
                anims[dst.Names[i]] = a.Build();
            }
            return new NodeAnimationGroup(RawGroup(frames), scales, rot, trans, anims);
        }

        static IEnumerable<int> Order(DsSkeleton s)
        {
            var placed = new bool[s.Count];
            for (int done = 0; done < s.Count;)
                for (int i = 0; i < s.Count; i++)
                    if (!placed[i] && (s.Parent[i] < 0 || placed[s.Parent[i]])) { placed[i] = true; done++; yield return i; }
        }

        // a world matrix's rotation, its scale taken out
        static Matrix3 Rot(Matrix4 m)
        {
            var x = new Vector3(m.Row0.X, m.Row0.Y, m.Row0.Z); var y = new Vector3(m.Row1.X, m.Row1.Y, m.Row1.Z); var z = new Vector3(m.Row2.X, m.Row2.Y, m.Row2.Z);
            return new Matrix3(x.Normalized(), y.Normalized(), z.Normalized());
        }

        // the game's X-then-Y-then-Z angles of a rotation (row vectors: M = Rx * Ry * Rz)
        public static Vector3 Euler(Matrix3 m)
        {
            float cb = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            float b = MathF.Atan2(-m.M13, cb);
            if (cb > 1e-5f)
            {
                return new Vector3(MathF.Atan2(m.M23, m.M33), b, MathF.Atan2(m.M12, m.M11));
            }
            return new Vector3(MathF.Atan2(-m.M32, m.M22), b, 0f); // straight up or down: X and Z turn the same way
        }

        // the same angle, within half a turn of the last frame's (so the game's frame blending never spins)
        static float Near(float a, float prev)
        {
            while (a - prev > MathF.PI) a -= MathF.Tau;
            while (a - prev < -MathF.PI) a += MathF.Tau;
            return a;
        }

        static RawNodeAnimationGroup RawGroup(int frames)
        {
            Span<uint> raw = stackalloc uint[5];
            raw[0] = (uint)frames;
            return MemoryMarshal.Read<RawNodeAnimationGroup>(MemoryMarshal.AsBytes(raw));
        }

        // NodeAnimation has no constructor (it's read from the file): its 48 bytes, laid out as RawFormats has them
        sealed class NodeAnim
        {
            public ushort ScaleLength, ScaleIndex, RotateLength, TranslateLength;
            public byte RotateBlend, TranslateBlend;
            public readonly ushort[] RotateIndex = new ushort[3], TranslateIndex = new ushort[3];

            public NodeAnimation Build()
            {
                Span<byte> b = stackalloc byte[48];
                b.Clear();
                for (int axis = 0; axis < 3; axis++)
                {
                    // scale: constant (blend 0, one value each)
                    Write(b, 4 + axis * 2, ScaleLength);
                    Write(b, 10 + axis * 2, (ushort)(ScaleIndex + axis));
                    b[16 + axis] = RotateBlend;
                    Write(b, 20 + axis * 2, RotateLength);
                    Write(b, 26 + axis * 2, RotateIndex[axis]);
                    b[32 + axis] = TranslateBlend;
                    Write(b, 36 + axis * 2, TranslateLength);
                    Write(b, 42 + axis * 2, TranslateIndex[axis]);
                }
                return MemoryMarshal.Read<NodeAnimation>(b);
            }

            static void Write(Span<byte> b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        }
    }
}
