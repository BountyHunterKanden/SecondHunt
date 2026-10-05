using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Import.Beyond
{
    // Quaternion / matrix helpers of the Beyond animation port (mp4pose.py's qmul, q2m, trs). Quaternions are
    // (w, x, y, z); matrices 4x4 row major (m[4 * row + col]) for column vectors, translation in column 3.
    public static class ChprMath
    {
        public static double[] QMul(double[] a, double[] b)
        {
            double w1 = a[0], x1 = a[1], y1 = a[2], z1 = a[3], w2 = b[0], x2 = b[1], y2 = b[2], z2 = b[3];
            return new[]
            {
                w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2, w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
                w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2, w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
            };
        }

        // 3x3 rotation (row major) of q / |q|
        public static double[] Q2M(double[] q)
        {
            double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            double w = q[0] / n, x = q[1] / n, y = q[2] / n, z = q[3] / n;
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
            };
        }

        // T * R * S: rotation q (wxyz), translation t, optional per-axis scale s (scales the rotation's columns)
        public static double[] Trs(double[] q, double[] t, double[]? s = null)
        {
            double[] r = Q2M(q);
            var m = new double[16];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) m[4 * i + j] = s == null ? r[3 * i + j] : r[3 * i + j] * s[j];
                m[4 * i + 3] = t[i];
            }
            m[15] = 1.0;
            return m;
        }

        // a x b, each element accumulated with fused multiply-adds in k order: bit-identical to numpy's float64 `@` (its
        // BLAS kernel) on every rest matrix of Samus's skeleton, where a plain sum differs in the last bit on 7%
        public static double[] MatMul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double s = a[4 * i] * b[j];
                    for (int k = 1; k < 4; k++) s = Math.FusedMultiplyAdd(a[4 * i + k], b[4 * k + j], s);
                    c[4 * i + j] = s;
                }
            }
            return c;
        }

        // general 4x4 inverse (Gauss-Jordan, partial pivoting)
        public static double[] Invert(double[] m)
        {
            var a = (double[])m.Clone();
            var inv = new double[16];
            for (int i = 0; i < 4; i++) inv[5 * i] = 1.0;
            for (int c = 0; c < 4; c++)
            {
                int p = c;
                for (int r = c + 1; r < 4; r++)
                {
                    if (Math.Abs(a[4 * r + c]) > Math.Abs(a[4 * p + c])) p = r;
                }
                if (a[4 * p + c] == 0) throw new InvalidOperationException("singular matrix");
                if (p != c)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        (a[4 * c + k], a[4 * p + k]) = (a[4 * p + k], a[4 * c + k]);
                        (inv[4 * c + k], inv[4 * p + k]) = (inv[4 * p + k], inv[4 * c + k]);
                    }
                }
                double d = a[4 * c + c];
                for (int k = 0; k < 4; k++)
                {
                    a[4 * c + k] /= d;
                    inv[4 * c + k] /= d;
                }
                for (int r = 0; r < 4; r++)
                {
                    if (r == c) continue;
                    double f = a[4 * r + c];
                    if (f == 0) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        a[4 * r + k] -= f * a[4 * c + k];
                        inv[4 * r + k] -= f * inv[4 * c + k];
                    }
                }
            }
            return inv;
        }
    }

    // One decoded Beyond animation, laid out as mp4anim_export.py's <anim>.npz: per frame and joint the LOCAL-to-parent
    // rotation (quaternion x y z w), translation and scale, already composed on the rest pose, plus the raw deltas.
    // Arrays are float32 and flat: Rot[(f * Joints.Length + j) * 4 + c], Trans / Scale [(f * J + j) * 3 + c].
    public sealed class ChprAnimation
    {
        public string Name = "";
        public int Index, Set, Frames;
        public double RateFactor, FpsAssumed, DurationAssumed;
        public string[] Joints = Array.Empty<string>();          // ordinal-sorted names of every joint the anim drives
        public string[] Parents = Array.Empty<string>();         // their skeleton parents' names ("" = none / not a skeleton node)
        public float[] RestRotXyzw = Array.Empty<float>(), RestTrans = Array.Empty<float>();
        public float[] RotXyzw = Array.Empty<float>(), Trans = Array.Empty<float>(), Scale = Array.Empty<float>();
        public float[] DeltaRotXyzw = Array.Empty<float>(), DeltaTrans = Array.Empty<float>(), DeltaScale = Array.Empty<float>();
        public bool[] Animated = Array.Empty<bool>();            // [j * 3 + c]: c = rot, trans, scale has a keyed track
        public string[] VisNames = Array.Empty<string>();
        public int[] VisValues = Array.Empty<int>();
        public int AnimatedChannels;

        public int JointIndex(string name) => Array.IndexOf(Joints, name);

        public (float X, float Y, float Z, float W) Rot(int f, int j)
        {
            int o = (f * Joints.Length + j) * 4;
            return (RotXyzw[o], RotXyzw[o + 1], RotXyzw[o + 2], RotXyzw[o + 3]);
        }

        public (float X, float Y, float Z) Translation(int f, int j)
        {
            int o = (f * Joints.Length + j) * 3;
            return (Trans[o], Trans[o + 1], Trans[o + 2]);
        }

        public (float X, float Y, float Z) ScaleAt(int f, int j)
        {
            int o = (f * Joints.Length + j) * 3;
            return (Scale[o], Scale[o + 1], Scale[o + 2]);
        }
    }

    // Pose composition (mp4pose.Rig.sample + mp4anim_export.export_anim). Animation values are DELTAS on the rest pose
    // (identity / zero / zero = rest):
    //   local rotation    R = dR * R_rest   (dR in the PARENT frame, applied on top of the rest rotation)
    //   local translation T = T_rest + dT   (parent frame)
    //   local scale       S = 1 + dS
    // Evidence (from the Python's study): with this rule fp_idle_samus holds the cannon in front of the camera pointing
    // straight forward; R_rest * dR points it at the floor. Joints an anim does not drive stay at rest.
    public static class ChprPose
    {
        // decoded anim -> {(joint, slot): per-frame values}; rotations wxyz, the others xyz
        public static Dictionary<(string Joint, ChprSlot Slot), double[][]> Sample(ChprDecodedAnim a)
        {
            int nf = a.Frames;
            var o = new Dictionary<(string, ChprSlot), double[][]>();
            foreach (var kv in a.Channels) o[kv.Key] = ChprAnimCodec.SampleTrack(kv.Value.Track, kv.Value.Keys, nf);
            foreach (var kv in a.Consts)
            {
                if (o.ContainsKey(kv.Key)) continue;
                double[] v = (double[])kv.Value.Clone();
                if (kv.Key.Slot == ChprSlot.Rot)
                {
                    double n = 0;
                    foreach (double x in v) n += x * x;
                    n = Math.Sqrt(n);
                    for (int i = 0; i < v.Length; i++) v[i] /= n;
                }
                var rows = new double[nf][];
                for (int f = 0; f < nf; f++) rows[f] = v;
                o[kv.Key] = rows;
            }
            return o;
        }

        // numpy's `dst[:, k] = rows` for rows of `width` values (a single value broadcasts)
        static void Put(double[,,] dst, int k, double[][] rows, int frames, int width)
        {
            for (int f = 0; f < frames; f++)
            {
                double[] r = rows[f];
                if (r.Length != width && r.Length != 1)
                {
                    throw new ChprAnimException($"could not broadcast input array from shape ({frames},{r.Length}) into shape ({frames},{width})");
                }
                for (int c = 0; c < width; c++) dst[f, k, c] = r.Length == 1 ? r[0] : r[c];
            }
        }

        public static ChprAnimation Export(ChprSkeleton rig, ChprDecodedAnim a)
        {
            var S = Sample(a);
            int nf = a.Frames;
            string[] joints = S.Keys.Select(k => k.Joint).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            int J = joints.Length;
            var dRot = new double[nf, J, 4];
            var dTr = new double[nf, J, 3];
            var dSc = new double[nf, J, 3];
            for (int f = 0; f < nf; f++)
            {
                for (int k = 0; k < J; k++) dRot[f, k, 0] = 1.0;
            }
            var animated = new bool[J * 3];
            for (int k = 0; k < J; k++)
            {
                string j = joints[k];
                if (S.TryGetValue((j, ChprSlot.Rot), out var r)) Put(dRot, k, r, nf, 4);
                if (S.TryGetValue((j, ChprSlot.Trans), out var t)) Put(dTr, k, t, nf, 3);
                if (S.TryGetValue((j, ChprSlot.Scale), out var s)) Put(dSc, k, s, nf, 3);
                animated[3 * k] = a.Channels.ContainsKey((j, ChprSlot.Rot));
                animated[3 * k + 1] = a.Channels.ContainsKey((j, ChprSlot.Trans));
                animated[3 * k + 2] = a.Channels.ContainsKey((j, ChprSlot.Scale));
            }
            var restQ = new double[J][];
            var restT = new double[J][];
            for (int k = 0; k < J; k++) (restQ[k], restT[k]) = rig.RestLocal(joints[k]);

            var an = new ChprAnimation
            {
                Name = a.Name, Index = a.Index, Set = a.Set, Frames = nf, Joints = joints,
                RateFactor = a.RateFactor, FpsAssumed = ChprCharacter.BaseFps * a.RateFactor,
                RestRotXyzw = new float[J * 4], RestTrans = new float[J * 3],
                RotXyzw = new float[nf * J * 4], Trans = new float[nf * J * 3], Scale = new float[nf * J * 3],
                DeltaRotXyzw = new float[nf * J * 4], DeltaTrans = new float[nf * J * 3], DeltaScale = new float[nf * J * 3],
                Animated = animated, AnimatedChannels = animated.Count(x => x),
            };
            an.DurationAssumed = (nf - 1) / an.FpsAssumed;
            for (int k = 0; k < J; k++)
            {
                double[] q = restQ[k];
                an.RestRotXyzw[4 * k] = (float)q[1]; an.RestRotXyzw[4 * k + 1] = (float)q[2];
                an.RestRotXyzw[4 * k + 2] = (float)q[3]; an.RestRotXyzw[4 * k + 3] = (float)q[0];
                for (int c = 0; c < 3; c++) an.RestTrans[3 * k + c] = (float)restT[k][c];
            }
            var dq = new double[4];
            for (int f = 0; f < nf; f++)
            {
                for (int k = 0; k < J; k++)
                {
                    int o4 = (f * J + k) * 4, o3 = (f * J + k) * 3;
                    for (int c = 0; c < 4; c++) dq[c] = dRot[f, k, c];
                    double[] rot = ChprMath.QMul(dq, restQ[k]);
                    an.RotXyzw[o4] = (float)rot[1]; an.RotXyzw[o4 + 1] = (float)rot[2]; an.RotXyzw[o4 + 2] = (float)rot[3]; an.RotXyzw[o4 + 3] = (float)rot[0];
                    an.DeltaRotXyzw[o4] = (float)dq[1]; an.DeltaRotXyzw[o4 + 1] = (float)dq[2]; an.DeltaRotXyzw[o4 + 2] = (float)dq[3]; an.DeltaRotXyzw[o4 + 3] = (float)dq[0];
                    for (int c = 0; c < 3; c++)
                    {
                        an.Trans[o3 + c] = (float)(restT[k][c] + dTr[f, k, c]);
                        an.Scale[o3 + c] = (float)(1.0 + dSc[f, k, c]);
                        an.DeltaTrans[o3 + c] = (float)dTr[f, k, c];
                        an.DeltaScale[o3 + c] = (float)dSc[f, k, c];
                    }
                }
            }
            an.Parents = joints.Select(j => rig.Index.TryGetValue(j, out int i) && rig.Parent[i] >= 0 ? rig.Names[rig.Parent[i]] : "").ToArray();
            var vis = a.BoolOrder.OrderBy(x => x, StringComparer.Ordinal).ToList();    // stable, as Python's sorted()
            an.VisNames = vis.ToArray();
            an.VisValues = vis.Select(x => a.Bools[x]).ToArray();
            return an;
        }
    }
}
