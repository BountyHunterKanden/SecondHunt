using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRecomp.Import.BrawlBody;
using OpenTK.Mathematics;

namespace MphRead
{
    // Humanoid clips from a glTF binary (.glb) on the Quaternius "Universal Animation Library" skeleton (CC0 1.0, public
    // domain: owner OK 2026-10-15 as Zero Suit's human movement source, docs/ZERO_SUIT_SAMUS.md section 8) put on a Brawl
    // fighter's skeleton (-zsbrawl proto --ual), as DsRetarget puts Samus's DS clips on it: each mapped bone takes its source
    // bone's turn in model space since the source rest (the file's own node pose, a T-pose), trued for limbs by the fixed
    // turn laying her bind direction (bone -> aim child) onto the source rest one:
    //   R_dst(t) = R_dst_bind * A * R_src_rest^-1 * R_src(t)    (row vectors, model space)
    // The spine, neck, head and pelvis aren't trued: they keep her own posture and take the source's turns from its rest.
    // Unmapped bones (fingers, twist bones, hair) keep `keep`'s local pose (her Wait1's first frame: her gun grip). Only
    // the pelvis moves, by the source's pelvis travel scaled by the legs' length ratio. Both skeletons face +Z with their
    // left at +X, Y up.
    internal static class UalRetarget
    {
        static readonly (string Dst, string Src, string? DstChild, string? SrcChild)[] Map =
        {
            ("HipN", "pelvis", "", ""), ("BodyN", "spine_01", "", ""), ("BustN", "spine_03", "", ""), ("NeckN", "neck_01", "", ""),
            ("HeadN", "Head", "", ""),
            ("LShoulderN", "clavicle_l", "LShoulderJ", "upperarm_l"), ("LShoulderJ", "upperarm_l", "LArmJ", "lowerarm_l"),
            ("LArmJ", "lowerarm_l", "LHandN", "hand_l"), ("LHandN", "hand_l", null, null),
            ("RShoulderN", "clavicle_r", "RShoulderJ", "upperarm_r"), ("RShoulderJ", "upperarm_r", "RArmJ", "lowerarm_r"),
            ("RArmJ", "lowerarm_r", "RHandN", "hand_r"), ("RHandN", "hand_r", null, null),
            ("LLegJ", "thigh_l", "LKneeJ", "calf_l"), ("LKneeJ", "calf_l", "LFootJ", "foot_l"), ("LFootJ", "foot_l", "LToeN", "ball_l"),
            ("LToeN", "ball_l", null, null),
            ("RLegJ", "thigh_r", "RKneeJ", "calf_r"), ("RKneeJ", "calf_r", "RFootJ", "foot_r"), ("RFootJ", "foot_r", "RToeN", "ball_r"),
            ("RToeN", "ball_r", null, null),
        };

        // the clips wanted (name in the file -> loops)
        public static BrawlBodyClip[] Clips(string glb, BrawlBodySource dst, BrawlBodyClip keep, IReadOnlyList<string> names, string prefix)
        {
            var g = Glb.Read(glb);
            int n = dst.Names.Length;
            int[] order = BrawlBodyWriter.Order(dst.Parent);
            Matrix4[] bind = BrawlBodyWriter.BindWorld(dst);
            var keepLocal = new Matrix4[n];
            BrawlBodyWriter.FrameLocals(dst, keep, 0, keepLocal);

            // the source's rest in model space
            Matrix4[] rest = g.World(g.RestLocals());
            var srcOf = new int[n];
            var corr = new Matrix4[n];
            var mapped = new bool[n];
            Array.Fill(srcOf, -1);
            for (int i = 0; i < n; i++) corr[i] = Matrix4.Identity;
            foreach (int i in order)
            {
                if (dst.Parent[i] >= 0) corr[i] = corr[dst.Parent[i]];
                int k = Array.FindIndex(Map, e => e.Dst == dst.Names[i]);
                if (k < 0) continue;
                (string _, string s, string? dc, string? sc) = Map[k];
                int si = g.Node(s);
                if (si < 0) throw new InvalidDataException("no source bone " + s);
                srcOf[i] = si;
                mapped[i] = true;
                if (dc == "") corr[i] = Matrix4.Identity;
                else if (dc != null)
                {
                    int di = dst.Bone(dc), sci = g.Node(sc!);
                    Vector3 from = bind[di].ExtractTranslation() - bind[i].ExtractTranslation();
                    Vector3 to = rest[sci].ExtractTranslation() - rest[si].ExtractTranslation();
                    corr[i] = Onto(from, to);
                }
            }
            int dPel = dst.Bone("HipN"), sPel = g.Node("pelvis");
            float legRatio = Leg(bind, dst.Bone("LLegJ"), dst.Bone("LKneeJ"), dst.Bone("LFootJ"))
                / Math.Max(1e-5f, Leg(rest, g.Node("thigh_l"), g.Node("calf_l"), g.Node("foot_l")));
            var restInv = new Matrix4[rest.Length];
            for (int s = 0; s < rest.Length; s++) restInv[s] = Rot(rest[s]).Inverted();
            var bindLocal = new Matrix4[n];
            for (int i = 0; i < n; i++) bindLocal[i] = dst.Parent[i] >= 0 ? bind[i] * bind[dst.Parent[i]].Inverted() : bind[i];

            var res = new List<BrawlBodyClip>();
            var world = new Matrix4[n];
            foreach (string name in names)
            {
                Glb.Animation a = g.Animations.FirstOrDefault(x => x.Name == name) ?? throw new InvalidDataException("no clip " + name);
                int frames = Math.Max(2, (int)MathF.Round(a.Duration * BrawlBodyWriter.Fps) + 1);
                var clip = new BrawlBodyClip
                {
                    Name = prefix + name, Frames = frames, Loop = name.EndsWith("_Loop", StringComparison.Ordinal),
                    Has = Enumerable.Repeat(true, n).ToArray(), Locals = new Matrix4[frames, n],
                };
                for (int f = 0; f < frames; f++)
                {
                    Matrix4[] cur = g.World(g.Sample(a, Math.Min(a.Duration, f / BrawlBodyWriter.Fps)));
                    foreach (int i in order)
                    {
                        Matrix4 parent = dst.Parent[i] >= 0 ? world[dst.Parent[i]] : Matrix4.Identity;
                        Matrix4 local;
                        if (mapped[i])
                        {
                            Matrix4 r = Rot(bind[i]) * corr[i] * restInv[srcOf[i]] * Rot(cur[srcOf[i]]);
                            Vector3 at = i == dPel
                                ? bind[i].ExtractTranslation() + (cur[sPel].ExtractTranslation() - rest[sPel].ExtractTranslation()) * legRatio
                                : Vector3.TransformPosition(bindLocal[i].ExtractTranslation(), parent);
                            world[i] = r * Matrix4.CreateTranslation(at);
                            local = dst.Parent[i] >= 0 ? world[i] * parent.Inverted() : world[i];
                            local = Matrix4.CreateFromQuaternion(local.ExtractRotation()) * Matrix4.CreateTranslation(local.ExtractTranslation());
                        }
                        else
                        {
                            local = keepLocal[i];
                            world[i] = dst.Parent[i] >= 0 ? local * parent : local;
                        }
                        clip.Locals[f, i] = local;
                    }
                }
                res.Add(clip);
            }
            return res.ToArray();
        }

        static Matrix4 Rot(Matrix4 m) => Matrix4.CreateFromQuaternion(m.ExtractRotation());

        static Matrix4 Onto(Vector3 from, Vector3 to)
        {
            if (from.LengthSquared < 1e-12f || to.LengthSquared < 1e-12f) return Matrix4.Identity;
            from.Normalize();
            to.Normalize();
            Vector3 axis = Vector3.Cross(from, to);
            float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
            if (axis.LengthSquared < 1e-12f) return Matrix4.Identity;
            return Matrix4.CreateFromAxisAngle(axis.Normalized(), MathF.Acos(dot));
        }

        static float Leg(Matrix4[] w, int a, int b, int c) =>
            (w[b].ExtractTranslation() - w[a].ExtractTranslation()).Length + (w[c].ExtractTranslation() - w[b].ExtractTranslation()).Length;

        // the little of glTF binary these clips need: nodes (TRS), the animations' rotation / translation / scale channels
        // (LINEAR or STEP), float accessors
        sealed class Glb
        {
            public string[] Names = Array.Empty<string>();
            public int[] Parent = Array.Empty<int>();
            public Vector3[] T = Array.Empty<Vector3>(), S = Array.Empty<Vector3>();
            public Quaternion[] R = Array.Empty<Quaternion>();
            public int[] Order = Array.Empty<int>();
            public readonly List<Animation> Animations = new();

            public sealed class Channel
            {
                public int Node;
                public string Path = "";
                public float[] Times = Array.Empty<float>();
                public float[] Values = Array.Empty<float>();
                public bool Step;
            }

            public sealed class Animation
            {
                public string Name = "";
                public float Duration;
                public readonly List<Channel> Channels = new();
            }

            public int Node(string name) => Array.IndexOf(Names, name);

            public static Glb Read(string path)
            {
                byte[] b = File.ReadAllBytes(path);
                int jsonLen = BitConverter.ToInt32(b, 12);
                using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
                int binAt = 20 + jsonLen + 8;
                JsonElement root = doc.RootElement;
                JsonElement nodes = root.GetProperty("nodes");
                int n = nodes.GetArrayLength();
                var g = new Glb
                {
                    Names = new string[n], Parent = Enumerable.Repeat(-1, n).ToArray(),
                    T = new Vector3[n], S = new Vector3[n], R = new Quaternion[n],
                };
                for (int i = 0; i < n; i++)
                {
                    JsonElement e = nodes[i];
                    g.Names[i] = e.TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? "" : "";
                    g.T[i] = e.TryGetProperty("translation", out JsonElement t) ? new Vector3(F(t, 0), F(t, 1), F(t, 2)) : Vector3.Zero;
                    g.S[i] = e.TryGetProperty("scale", out JsonElement s) ? new Vector3(F(s, 0), F(s, 1), F(s, 2)) : Vector3.One;
                    g.R[i] = e.TryGetProperty("rotation", out JsonElement r) ? new Quaternion(F(r, 0), F(r, 1), F(r, 2), F(r, 3)) : Quaternion.Identity;
                    if (e.TryGetProperty("children", out JsonElement ch)) foreach (JsonElement c in ch.EnumerateArray()) g.Parent[c.GetInt32()] = i;
                }
                g.Order = BrawlBodyWriter.Order(g.Parent);
                JsonElement acc = root.GetProperty("accessors"), views = root.GetProperty("bufferViews");
                float[] Floats(int a)
                {
                    JsonElement x = acc[a];
                    JsonElement v = views[x.GetProperty("bufferView").GetInt32()];
                    int count = x.GetProperty("count").GetInt32();
                    int comps = x.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException("accessor type") };
                    if (x.GetProperty("componentType").GetInt32() != 5126) throw new InvalidDataException("non-float accessor");
                    int off = binAt + (v.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0)
                        + (x.TryGetProperty("byteOffset", out JsonElement xo) ? xo.GetInt32() : 0);
                    int stride = v.TryGetProperty("byteStride", out JsonElement st) ? st.GetInt32() : comps * 4;
                    var o = new float[count * comps];
                    for (int k = 0; k < count; k++) for (int c = 0; c < comps; c++) o[k * comps + c] = BitConverter.ToSingle(b, off + k * stride + c * 4);
                    return o;
                }
                foreach (JsonElement a in root.GetProperty("animations").EnumerateArray())
                {
                    var anim = new Animation { Name = a.GetProperty("name").GetString() ?? "" };
                    JsonElement samplers = a.GetProperty("samplers");
                    foreach (JsonElement c in a.GetProperty("channels").EnumerateArray())
                    {
                        JsonElement tgt = c.GetProperty("target");
                        if (!tgt.TryGetProperty("node", out JsonElement nd)) continue;
                        JsonElement smp = samplers[c.GetProperty("sampler").GetInt32()];
                        string interp = smp.TryGetProperty("interpolation", out JsonElement ip) ? ip.GetString() ?? "LINEAR" : "LINEAR";
                        if (interp == "CUBICSPLINE") throw new InvalidDataException("cubic spline channel");
                        var ch = new Channel
                        {
                            Node = nd.GetInt32(), Path = tgt.GetProperty("path").GetString() ?? "",
                            Times = Floats(smp.GetProperty("input").GetInt32()), Values = Floats(smp.GetProperty("output").GetInt32()),
                            Step = interp == "STEP",
                        };
                        anim.Duration = Math.Max(anim.Duration, ch.Times.Length > 0 ? ch.Times[^1] : 0);
                        anim.Channels.Add(ch);
                    }
                    g.Animations.Add(anim);
                }
                return g;
            }

            static float F(JsonElement a, int i) => a[i].GetSingle();

            public (Vector3 T, Quaternion R, Vector3 S)[] RestLocals()
            {
                var o = new (Vector3, Quaternion, Vector3)[Names.Length];
                for (int i = 0; i < o.Length; i++) o[i] = (T[i], R[i], S[i]);
                return o;
            }

            public (Vector3 T, Quaternion R, Vector3 S)[] Sample(Animation a, float t)
            {
                (Vector3 T, Quaternion R, Vector3 S)[] o = RestLocals();
                foreach (Channel c in a.Channels)
                {
                    int k = 0;
                    while (k + 1 < c.Times.Length && c.Times[k + 1] <= t) k++;
                    int k2 = Math.Min(k + 1, c.Times.Length - 1);
                    float span = c.Times[k2] - c.Times[k];
                    float w = c.Step || span <= 0 ? 0 : Math.Clamp((t - c.Times[k]) / span, 0, 1);
                    if (c.Path == "rotation")
                    {
                        var q0 = new Quaternion(c.Values[k * 4], c.Values[k * 4 + 1], c.Values[k * 4 + 2], c.Values[k * 4 + 3]);
                        var q1 = new Quaternion(c.Values[k2 * 4], c.Values[k2 * 4 + 1], c.Values[k2 * 4 + 2], c.Values[k2 * 4 + 3]);
                        o[c.Node].R = Quaternion.Slerp(q0, q1, w);
                    }
                    else if (c.Path is "translation" or "scale")
                    {
                        var v0 = new Vector3(c.Values[k * 3], c.Values[k * 3 + 1], c.Values[k * 3 + 2]);
                        var v1 = new Vector3(c.Values[k2 * 3], c.Values[k2 * 3 + 1], c.Values[k2 * 3 + 2]);
                        if (c.Path == "translation") o[c.Node].T = Vector3.Lerp(v0, v1, w);
                        else o[c.Node].S = Vector3.Lerp(v0, v1, w);
                    }
                }
                return o;
            }

            // model space, row vectors (world = local * parent), scale left in (the armature's own turn too)
            public Matrix4[] World((Vector3 T, Quaternion R, Vector3 S)[] l)
            {
                var w = new Matrix4[Names.Length];
                foreach (int i in Order)
                {
                    Matrix4 m = Matrix4.CreateScale(l[i].S) * Matrix4.CreateFromQuaternion(l[i].R) * Matrix4.CreateTranslation(l[i].T);
                    w[i] = Parent[i] >= 0 ? m * w[Parent[i]] : m;
                }
                return w;
            }
        }
    }
}
