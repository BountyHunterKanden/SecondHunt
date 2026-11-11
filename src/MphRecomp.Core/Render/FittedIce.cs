using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MphRead;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRecomp.Render
{
    // Fitted ice (owner 2026-11-09, the Death look thread's option B): a frozen body wears vanilla's own ice shell, fitted to
    // that body. samus_ice / nox_ice is a chunky low-poly shell that covers the body only in pieces; vanilla poses it with
    // the body's node matrices by index, so on a body other than the one it was made for it floats off or sinks in. Here
    // each piece (the shell's corners on one node) is moved and scaled in that node's own space, from where it sits on the
    // body it was made for (Samus_lod0 / Nox_lod0: the reference) onto the same node of the target body: the centre of the
    // node's vertices and their spread per axis. Same triangles and material (samus_ice_img, normal texgen, translucent,
    // lit, back faces culled), so it keeps vanilla's shape and size; on the reference body it is vanilla's shell exactly.
    // Built at load from the user's ROM, on the body's nodes and matrix ids, so PlayerDraw / PlayerRagdoll pose it by
    // copying the body's node animations, as vanilla does its shell. The GL bodies (HD suits, native bodies) fit the same
    // shell with Fit (MphRecomp.App SuitIce.cs). Purely a look: nothing of it is sent, recorded or simulated.
    public static class FittedIce
    {
        // "FittedIce/<body model>" (the shell's own normals) or "FittedIceFacets/<body model>" (each triangle's own normal)
        public const string Prefix = "FittedIce/", FacetsPrefix = "FittedIceFacets/";

        public static string NameFor(string body, bool facets) => (facets ? FacetsPrefix : Prefix) + body;

#pragma warning disable CA2255 // registers the models with MphRead's loader before any scene loads; no other caller needed
        [ModuleInitializer]
        internal static void Init()
        {
            Func<string, Model?>? previous = Read.HostBuildModel;
            Read.HostBuildModel = name => name.StartsWith(Prefix, StringComparison.Ordinal) ? BuildNamed(name, Prefix.Length, false)
                : name.StartsWith(FacetsPrefix, StringComparison.Ordinal) ? BuildNamed(name, FacetsPrefix.Length, true)
                : previous?.Invoke(name);
        }
#pragma warning restore CA2255

        static Model? BuildNamed(string name, int skip, bool facets)
        {
            string body = name[skip..];
            Model src = Read.GetModelInstance(body).Model;
            string iceName = IceFor(body);
            Model ice = Read.GetModelInstance(iceName).Model;
            string refName = ReferenceFor(iceName);
            Model reference = Read.GetModelInstance(refName).Model;
            if (src.Nodes.Count < ice.Nodes.Count) return null; // (not a biped's skeleton: nothing to fit to)
            if (body == refName) return Build(name, src, ice, reference, facets);
            // one private copy of the body for its bind height and then its pose (a stand-in's copy is a whole rebuild from its
            // recipe), the reference posed once for every body (the Odin's freeze hitch, 11-09: 1.5 s mid-match)
            long t0 = Stopwatch.GetTimestamp();
            ModelInstance inst = Read.GetModelInstance(body, noCache: true);
            float height = BindHeight(inst.Model);
            Model ps = Posed(inst);
            (Model pr, List<Vector3[]> rs) = PosedReference(refName);
            long t1 = Stopwatch.GetTimestamp();
            Model m = Build(name, src, ice, reference, facets, (pr, ps, height), rs);
            Console.WriteLine($"MPHIce: {name} built in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms "
                + $"(body copy {Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:0} ms)");
            return m;
        }

        // a shell's reference body posed as the game poses it and its surface, read once: every fitted shell and every host
        // body's fit (ReferenceOf) start from it; read only after
        static readonly Dictionary<string, (Model Posed, List<Vector3[]> Surface)> _posedRefs = new();

        static (Model Posed, List<Vector3[]> Surface) PosedReference(string refName)
        {
            lock (_posedRefs)
            {
                if (!_posedRefs.TryGetValue(refName, out (Model, List<Vector3[]>) r))
                {
                    Model m = Posed(refName);
                    _posedRefs[refName] = r = (m, SurfaceOf(m));
                }
                return r;
            }
        }

        // vanilla's shell for a body: Noxus and Trace wear nox_ice, the rest samus_ice (PlayerEntity's _bipedIceModel)
        public static string IceFor(string body) => body.StartsWith("Nox", StringComparison.Ordinal)
            || body.StartsWith("Trace", StringComparison.Ordinal) ? "nox_ice" : "samus_ice";

        // the body each shell was made for (-fittedice frames: posed by its node matrices it sits ~0.1 outside that body)
        public static string ReferenceFor(string ice) => ice == "nox_ice" ? "Nox_lod0" : "Samus_lod0";

        // the body's bind-pose height in world units (a private copy: the cached model's node matrices stay as they are)
        public static float BindHeight(string body) => BindHeight(Read.GetModelInstance(body, noCache: true).Model);

        static float BindHeight(Model m)
        {
            m.ComputeNodeMatrices(0);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Mesh mesh in m.Meshes)
            {
                float[] v = DsDisplayList.Decode(m, mesh, isRoom: false);
                for (int o = 0; o + DsDisplayList.Stride <= v.Length; o += DsDisplayList.Stride)
                {
                    Matrix4 x = m.Nodes[m.NodeMatrixIds[(int)v[o + 12]]].Transform;
                    float y = Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), x).Y * m.Scale.X;
                    lo = MathF.Min(lo, y);
                    hi = MathF.Max(hi, y);
                }
            }
            return hi > lo ? hi - lo : 1.8f;
        }

        // one corner of a shell triangle: its node, and its position and normal in that node's space (or, for Fit's model
        // space mode, in the model's)
        public struct IceCorner
        {
            public int Node;
            public Vector3 Pos, Normal;
        }

        // the shell's triangles (three corners each), in node space
        public static IceCorner[] ShellOf(Model ice)
        {
            var corners = new List<IceCorner>();
            foreach (Mesh mesh in ice.Meshes)
            {
                float[] v = DsDisplayList.Decode(ice, mesh, isRoom: false);
                int n = v.Length / DsDisplayList.Stride / 3 * 3;
                for (int i = 0; i < n; i++)
                {
                    int o = i * DsDisplayList.Stride;
                    corners.Add(new IceCorner
                    {
                        Node = ice.NodeMatrixIds[(int)v[o + 12]],
                        Pos = new Vector3(v[o], v[o + 1], v[o + 2]),
                        Normal = new Vector3(v[o + 3], v[o + 4], v[o + 5]),
                    });
                }
            }
            return corners.ToArray();
        }

        // a DS body's vertices per node, in node space (its see-through parts left out: visor glass, glow cards)
        public static List<Vector3>[] CloudsOf(Model m)
        {
            var clouds = new List<Vector3>[m.Nodes.Count];
            for (int i = 0; i < clouds.Length; i++) clouds[i] = new List<Vector3>();
            foreach (Mesh mesh in m.Meshes)
            {
                Material mat = m.Materials[mesh.MaterialId];
                if (mat.RenderMode == RenderMode.Translucent && mat.Alpha < 31 || mat.Alpha == 0) continue;
                float[] v = DsDisplayList.Decode(m, mesh, isRoom: false);
                for (int o = 0; o + DsDisplayList.Stride <= v.Length; o += DsDisplayList.Stride)
                {
                    int node = m.NodeMatrixIds[(int)v[o + 12]];
                    if ((uint)node < (uint)clouds.Length) clouds[node].Add(new Vector3(v[o], v[o + 1], v[o + 2]));
                }
            }
            return clouds;
        }

        public static int[] ParentsOf(Model m) => m.Nodes.Select(n => n.ParentIndex).ToArray();

        const int MinPoints = 8;
        const float ClampLo = 0.6f, ClampHi = 1.6f; // each axis's scale against the whole body's (g)

        // (matrices here map column vectors: Mul(m, v) = m v, rows dotted with v)
        static Vector3 Mul(Matrix3 m, Vector3 v) => new(Vector3.Dot(m.Row0, v), Vector3.Dot(m.Row1, v), Vector3.Dot(m.Row2, v));

        sealed class Stats
        {
            public Vector3 Mean;
            public Matrix3 Cov;

            public static Stats? Of(List<Vector3> pts)
            {
                if (pts.Count < MinPoints) return null;
                Vector3 mean = Vector3.Zero;
                foreach (Vector3 p in pts) mean += p;
                mean /= pts.Count;
                Vector3 r0 = Vector3.Zero, r1 = Vector3.Zero, r2 = Vector3.Zero;
                foreach (Vector3 p in pts)
                {
                    Vector3 d = p - mean;
                    r0 += d * d.X;
                    r1 += d * d.Y;
                    r2 += d * d.Z;
                }
                return new Stats { Mean = mean, Cov = new Matrix3(r0 / pts.Count, r1 / pts.Count, r2 / pts.Count) };
            }

            public float Trace => Cov.M11 + Cov.M22 + Cov.M33;
        }

        static float Var(Matrix3 cov, Vector3 axis) => Vector3.Dot(axis, Mul(cov, axis));

        // the covariance's main axis and its two largest eigenvalues (power iteration)
        static (Vector3 Axis, float L1, float L2) Principal(Matrix3 c)
        {
            Vector3 a = Top(c, new Vector3(0.3f, 1f, 0.2f).Normalized());
            float l1 = Var(c, a);
            Matrix3 d = new(c.Row0 - a * (a.X * l1), c.Row1 - a * (a.Y * l1), c.Row2 - a * (a.Z * l1));
            Vector3 b = Top(d, Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ).Normalized());
            return (a, l1, Var(d, b));
        }

        static Vector3 Top(Matrix3 c, Vector3 v)
        {
            for (int i = 0; i < 64; i++)
            {
                Vector3 w = Mul(c, v);
                if (w.LengthSquared < 1e-24f) break;
                v = w.Normalized();
            }
            return v;
        }

        static Matrix3 RotY(float a)
        {
            float c = MathF.Cos(a), s = MathF.Sin(a);
            return new Matrix3(new Vector3(c, 0, s), Vector3.UnitY, new Vector3(-s, 0, c));
        }

        // the rotation turning unit vector a onto unit vector b
        static Matrix3 RotationBetween(Vector3 a, Vector3 b)
        {
            Vector3 k = Vector3.Cross(a, b);
            float s2 = k.LengthSquared, c = Vector3.Dot(a, b);
            if (s2 < 1e-12f)
            {
                if (c > 0) return Matrix3.Identity;
                Vector3 u = Vector3.Cross(a, MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY).Normalized();
                return new Matrix3(2 * u * u.X - Vector3.UnitX, 2 * u * u.Y - Vector3.UnitY, 2 * u * u.Z - Vector3.UnitZ);
            }
            // Rodrigues: I + [k] + [k]^2 (1 - c) / s^2
            var kx = new Matrix3(new Vector3(0, -k.Z, k.Y), new Vector3(k.Z, 0, -k.X), new Vector3(-k.Y, k.X, 0));
            Matrix3 kk = kx * kx;
            float f = (1 - c) / s2;
            return new Matrix3(Vector3.UnitX + kx.Row0 + kk.Row0 * f, Vector3.UnitY + kx.Row1 + kk.Row1 * f,
                Vector3.UnitZ + kx.Row2 + kk.Row2 * f);
        }

        // one node's map from the reference body's space onto the target's: p' = Mean + scaled(Rot (p - RefMean)), the
        // scale along Axes' rows
        sealed class NodeMap
        {
            public Vector3 RefMean, Mean;
            public Matrix3 Rot = Matrix3.Identity, Axes = Matrix3.Identity;
            public Vector3 Scale = Vector3.One;

            Vector3 Back(Vector3 a) => a.X * Axes.Row0 + a.Y * Axes.Row1 + a.Z * Axes.Row2;

            public Vector3 Pos(Vector3 p) => Mean + Back(Mul(Axes, Mul(Rot, p - RefMean)) * Scale);

            public Vector3 Normal(Vector3 n)
            {
                Vector3 r = Back(Mul(Axes, Mul(Rot, n)) / Scale);
                return r.LengthSquared > 1e-12f ? r.Normalized() : n;
            }
        }

        // the shell moved onto a target body piece by piece (its corners by node). reference / target: each node's vertices
        // on the body the shell was made for and on the one it goes on, in the same kind of space as the shell's corners.
        // Node space (model = false, DS bodies): the nodes' frames line up between bodies (vanilla poses one shell on all
        // of them), so each piece just moves to the target node's centre and scales per axis. Model space (model = true, a
        // body with its own skeleton and bind pose): the target is first turned about Y to face as the reference does (by
        // the hips: hipL / hipR), then each limb's main axis is turned onto the target's. Every scale stays within
        // ClampLo..ClampHi of the whole body's (g), so a thin or odd node can't blow its piece up. A node without enough
        // vertices on either body keeps its piece as it is about its joint, scaled by g (node space), or follows its
        // parent's map (model space).
        public static IceCorner[] Fit(IceCorner[] shell, List<Vector3>[] reference, List<Vector3>[] target, int[] parents,
            bool model = false, int hipL = -1, int hipR = -1)
        {
            int n = Math.Min(reference.Length, target.Length);
            var rs = new Stats?[n];
            var ts = new Stats?[n];
            var ratios = new List<float>();
            for (int i = 0; i < n; i++)
            {
                rs[i] = Stats.Of(reference[i]);
                ts[i] = Stats.Of(target[i]);
                if (rs[i] != null && ts[i] != null && rs[i]!.Trace > 1e-12f) ratios.Add(MathF.Sqrt(ts[i]!.Trace / rs[i]!.Trace));
            }
            ratios.Sort();
            float g = ratios.Count > 0 ? ratios[ratios.Count / 2] : 1f;
            Matrix3 turn = Matrix3.Identity;
            if (model && (uint)hipL < (uint)n && (uint)hipR < (uint)n && rs[hipL] != null && rs[hipR] != null
                && ts[hipL] != null && ts[hipR] != null)
            {
                Vector3 lr = rs[hipL]!.Mean - rs[hipR]!.Mean, lt = ts[hipL]!.Mean - ts[hipR]!.Mean;
                lr.Y = lt.Y = 0;
                if (lr.LengthSquared > 1e-12f && lt.LengthSquared > 1e-12f)
                {
                    float ang = MathF.Acos(Math.Clamp(Vector3.Dot(lr.Normalized(), lt.Normalized()), -1f, 1f));
                    Matrix3 a = RotY(ang), b = RotY(-ang);
                    turn = Vector3.Dot(Mul(a, lr), lt) >= Vector3.Dot(Mul(b, lr), lt) ? a : b;
                }
            }
            var maps = new NodeMap?[n];
            var fallback = new NodeMap { Rot = turn, Scale = new Vector3(g) };
            NodeMap MapOf(int i, int depth)
            {
                if ((uint)i >= (uint)n || depth > 64) return fallback;
                if (maps[i] != null) return maps[i]!;
                Stats? r = rs[i], t = ts[i];
                if (r == null || t == null)
                {
                    return maps[i] = model && (uint)parents[i] < (uint)n && parents[i] != i ? MapOf(parents[i], depth + 1) : fallback;
                }
                var m = new NodeMap { RefMean = r.Mean, Mean = t.Mean, Rot = turn };
                bool limb = false;
                if (model)
                {
                    Matrix3 rc = turn * r.Cov * Matrix3.Transpose(turn);
                    (Vector3 ar, float r1, float r2) = Principal(rc);
                    (Vector3 at, float t1, float t2) = Principal(t.Cov);
                    limb = r1 > 2.5f * r2 && t1 > 2.5f * t2;
                    if (limb)
                    {
                        // both axes pointed away from the parent's vertices, so both run down the limb
                        int pi = parents[i];
                        if ((uint)pi < (uint)n && rs[pi] != null && Vector3.Dot(ar, Mul(turn, r.Mean - rs[pi]!.Mean)) < 0) ar = -ar;
                        if ((uint)pi < (uint)n && ts[pi] != null && Vector3.Dot(at, t.Mean - ts[pi]!.Mean) < 0) at = -at;
                        m.Rot = RotationBetween(ar, at) * turn;
                        Vector3 u = Vector3.Cross(at, MathF.Abs(at.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX).Normalized();
                        m.Axes = new Matrix3(at, u, Vector3.Cross(at, u));
                    }
                }
                Matrix3 rcov = m.Rot * r.Cov * Matrix3.Transpose(m.Rot);
                float S(Vector3 axis)
                {
                    float vr = Var(rcov, axis), vt = Var(t.Cov, axis);
                    float k = vr > 1e-12f ? MathF.Sqrt(vt / vr) : g;
                    return Math.Clamp(k, ClampLo * g, ClampHi * g);
                }
                if (limb)
                {
                    float radial = MathF.Sqrt(S(m.Axes.Row1) * S(m.Axes.Row2));
                    m.Scale = new Vector3(S(m.Axes.Row0), radial, radial);
                }
                else
                {
                    m.Scale = new Vector3(S(m.Axes.Row0), S(m.Axes.Row1), S(m.Axes.Row2));
                }
                return maps[i] = m;
            }
            var output = new IceCorner[shell.Length];
            for (int i = 0; i < shell.Length; i++)
            {
                IceCorner c = shell[i];
                NodeMap m = MapOf(c.Node, 0);
                output[i] = new IceCorner { Node = c.Node, Pos = m.Pos(c.Pos), Normal = m.Normal(c.Normal) };
            }
            return output;
        }

        struct Corner
        {
            public int Matrix;
            public Vector3 Pos, Normal;
        }

        // the gap each corner keeps: Fit places the pieces, and then each corner is set at the distance outside the target's
        // surface that it sits outside the reference's (vanilla's own fit, corner by corner: -fittedice frames, median
        // 0.09-0.10), moved at most cap. refCorners / fitCorners: the shell's corners on the reference and the fitted ones on
        // the target, posed alike; the surfaces' triangles wound outward (front faces), posed the same way.
        public static Vector3[] Snap(Vector3[] refCorners, List<Vector3[]> refSurface, Vector3[] fitCorners, List<Vector3[]> tgtSurface,
            float cap, float gapScale = 1f)
        {
            var output = new Vector3[fitCorners.Length];
            SurfaceTri[] refTris = Prepared(refSurface), tgtTris = Prepared(tgtSurface);
            for (int i = 0; i < fitCorners.Length; i++)
            {
                (Vector3 rq, Vector3 rn) = Nearest(refCorners[i], refTris);
                float gap = Vector3.Dot(refCorners[i] - rq, rn) >= 0 ? (refCorners[i] - rq).Length : -(refCorners[i] - rq).Length;
                Vector3 p = fitCorners[i];
                (Vector3 q, Vector3 n) = Nearest(p, tgtTris);
                Vector3 d = p - q;
                // out along the way the corner already lies from the surface (an edge or a corner of it has no one normal),
                // else the face's
                Vector3 dir = d.LengthSquared > 1e-10f ? (Vector3.Dot(d, n) >= 0 ? d.Normalized() : -d.Normalized()) : n;
                Vector3 move = q + dir * gap * gapScale - p;
                float len = move.Length;
                output[i] = len > cap ? p + move * (cap / len) : p + move;
            }
            return output;
        }

        // a surface triangle as Nearest reads it: its outward normal and its box, worked out once per surface
        readonly struct SurfaceTri
        {
            public readonly Vector3 A, B, C, Normal, Lo, Hi;

            public SurfaceTri(Vector3[] t, Vector3 normal)
            {
                A = t[0];
                B = t[1];
                C = t[2];
                Normal = normal;
                Lo = Vector3.ComponentMin(Vector3.ComponentMin(A, B), C);
                Hi = Vector3.ComponentMax(Vector3.ComponentMax(A, B), C);
            }
        }

        // (degenerate triangles dropped: they have no normal)
        static SurfaceTri[] Prepared(List<Vector3[]> surface)
        {
            var tris = new List<SurfaceTri>(surface.Count);
            foreach (Vector3[] t in surface)
            {
                Vector3 fn = Vector3.Cross(t[1] - t[0], t[2] - t[0]);
                if (fn.LengthSquared >= 1e-14f) tris.Add(new SurfaceTri(t, fn.Normalized()));
            }
            return tris.ToArray();
        }

        // the nearest point of a surface to p and that triangle's outward normal (a triangle whose box lies farther than the
        // best so far can't be nearer: skipped)
        static (Vector3 Point, Vector3 Normal) Nearest(Vector3 p, SurfaceTri[] surface)
        {
            float best = float.MaxValue;
            Vector3 bq = p, bn = Vector3.UnitY;
            foreach (ref readonly SurfaceTri t in surface.AsSpan())
            {
                Vector3 o = Vector3.ComponentMax(Vector3.ComponentMax(t.Lo - p, p - t.Hi), Vector3.Zero);
                if (o.LengthSquared >= best) continue;
                Vector3 q = Closest(p, t.A, t.B, t.C);
                float d = (p - q).LengthSquared;
                if (d >= best) continue;
                best = d;
                bq = q;
                bn = t.Normal;
            }
            return (bq, bn);
        }

        static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            float den = 1 / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }

        // a DS body posed as the game first poses it (its first animation, frame 0: node Animation set), a private copy
        public static Model Posed(string body) => Posed(Read.GetModelInstance(body, noCache: true));

        static Model Posed(ModelInstance inst)
        {
            Model m = inst.Model;
            if (m.AnimationGroups.Node.Count > 0)
            {
                inst.SetAnimation(0, slot: 0, SetFlags.Node);
                inst.AnimInfo.Frame[0] = 0;
            }
            m.ComputeNodeMatrices(0);
            m.AnimateNodes(0, false, Matrix4.Identity, m.Scale, inst.AnimInfo);
            return m;
        }

        // a posed DS body's surface: its opaque triangles in the world
        public static List<Vector3[]> SurfaceOf(Model posed)
        {
            var tris = new List<Vector3[]>();
            int st = DsDisplayList.Stride;
            foreach (Mesh mesh in posed.Meshes)
            {
                Material mat = posed.Materials[mesh.MaterialId];
                if (mat.RenderMode == RenderMode.Translucent && mat.Alpha < 31 || mat.Alpha == 0) continue;
                float[] v = DsDisplayList.Decode(posed, mesh, isRoom: false);
                for (int o = 0; o + 3 * st <= v.Length; o += 3 * st)
                {
                    var t = new Vector3[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int b = o + k * st;
                        t[k] = Vector3.TransformPosition(new Vector3(v[b], v[b + 1], v[b + 2]), posed.Nodes[posed.NodeMatrixIds[(int)v[b + 12]]].Animation);
                    }
                    tris.Add(t);
                }
            }
            return tris;
        }

        // the most Snap moves a corner, as a fraction of the body's height (Fit has it close already; this only evens the gap)
        const float SnapCapOfHeight = 0.25f;

        // -------- the host-drawn bodies (MphRecomp.App SuitIce.cs): the same shell on a GL body's bind pose --------

        // what a host body's fit needs from the ROM, read once per shell on the thread that reads models (Read isn't thread
        // safe); the fits below then run anywhere
        public sealed class IceReference
        {
            public string Ice = "";
            public string[] Names = Array.Empty<string>(); // the shell's nodes
            public int[] Parents = Array.Empty<int>();
            public int HipL = -1, HipR = -1;
            public IceCorner[] Shell = Array.Empty<IceCorner>();      // node-local, as the shell file has them
            public IceCorner[] ShellPosed = Array.Empty<IceCorner>(); // on the reference posed as the game poses it
            public List<Vector3>[] Local = Array.Empty<List<Vector3>>(); // the reference's vertices by node, node-local
            public List<Vector3>[] Posed = Array.Empty<List<Vector3>>(); // and posed
            public List<Vector3[]> Surface = new();                      // its opaque surface, posed
        }

        public static IceReference ReferenceOf(string rig)
        {
            string iceName = IceFor(rig);
            Model ice = Read.GetModelInstance(iceName).Model;
            (Model posed, List<Vector3[]> surface) = PosedReference(ReferenceFor(iceName));
            var r = new IceReference
            {
                Ice = iceName, Names = ice.Nodes.Select(n => n.Name).ToArray(), Parents = ParentsOf(ice),
                HipL = ice.Nodes.ToList().FindIndex(n => n.Name == "L_hip"), HipR = ice.Nodes.ToList().FindIndex(n => n.Name == "R_hip"),
                Shell = ShellOf(ice), Local = CloudsOf(posed), Surface = surface
            };
            Matrix4 At(int node) => (uint)node < (uint)posed.Nodes.Count ? posed.Nodes[node].Animation : Matrix4.Identity;
            r.ShellPosed = r.Shell.Select(c => new IceCorner
            {
                Node = c.Node, Pos = Vector3.TransformPosition(c.Pos, At(c.Node)), Normal = Vector3.TransformVector(c.Normal, At(c.Node)).Normalized()
            }).ToArray();
            r.Posed = new List<Vector3>[r.Names.Length];
            for (int i = 0; i < r.Posed.Length; i++)
            {
                r.Posed[i] = i < r.Local.Length ? r.Local[i].Select(v => Vector3.TransformPosition(v, At(i))).ToList() : new List<Vector3>();
            }
            return r;
        }

        // a body on a DS skeleton (an HD campaign suit: its bones are the DS rig's nodes, whose frames line up with the DS
        // body's, as on the DS path). names: the skeleton's node names; clouds: each node's vertices in its own frame;
        // bindWorld: each node's bind frame; bindSurface: the body's triangles in bind space. The corners come back in
        // bind space on the skeleton's nodes (Node = skeleton index); null = no shell (not a biped's skeleton).
        public static IceCorner[]? ShellOnSkeleton(IceReference r, string[] names, List<Vector3>[] clouds, Matrix4[] bindWorld,
            List<Vector3[]> bindSurface)
        {
            int[] map = r.Names.Select(n => Array.IndexOf(names, n)).ToArray();
            if (map.Count(i => i >= 0) < map.Length / 2) return null;
            int Bone(int node)
            {
                for (int depth = 0; depth < 64 && (uint)node < (uint)map.Length; depth++, node = r.Parents[node])
                {
                    if (map[node] >= 0) return map[node];
                }
                return 0;
            }
            var target = new List<Vector3>[map.Length];
            for (int i = 0; i < map.Length; i++) target[i] = map[i] >= 0 && map[i] < clouds.Length ? clouds[map[i]] : new List<Vector3>();
            IceCorner[] fit = Fit(r.Shell, r.Local, target, r.Parents);
            for (int i = 0; i < fit.Length; i++)
            {
                int b = Bone(fit[i].Node);
                Matrix4 w = b < bindWorld.Length ? bindWorld[b] : Matrix4.Identity;
                fit[i] = new IceCorner { Node = b, Pos = Vector3.TransformPosition(fit[i].Pos, w), Normal = Vector3.TransformVector(fit[i].Normal, w).Normalized() };
            }
            return Snapped(r, fit, bindSurface);
        }

        // a body with its own skeleton (a native body: Trooper, Dark Samus, Prime 4 Sylux): clouds by the shell's nodes
        // (its bones' vertices gathered under their DS counterparts), in its bind (model) space, as is bindSurface. The
        // corners come back in bind space with Node = the shell's node.
        public static IceCorner[] ShellOnModel(IceReference r, List<Vector3>[] clouds, List<Vector3[]> bindSurface) =>
            Snapped(r, Fit(r.ShellPosed, r.Posed, clouds, r.Parents, model: true, r.HipL, r.HipR), bindSurface);

        // Snap for a host body, its gaps and cap scaled by the bodies' sizes (a native body's units aren't the DS body's; the
        // box's diagonal, as a crouched body's height would understate it)
        static IceCorner[] Snapped(IceReference r, IceCorner[] fit, List<Vector3[]> bindSurface)
        {
            List<Vector3[]> surface = Outward(bindSurface);
            float sr = SizeOf(r.Surface), scale = sr > 1e-6f ? SizeOf(surface) / sr : 1f;
            Vector3[] snapped = Snap(r.ShellPosed.Select(c => c.Pos).ToArray(), r.Surface, fit.Select(c => c.Pos).ToArray(), surface,
                SnapCapOfHeight * HeightOf(r.Surface) * scale, scale);
            for (int i = 0; i < fit.Length; i++) fit[i].Pos = snapped[i];
            return fit;
        }

        // a host body's triangles wound outward, as Snap reads them (an imported mesh may wind them either way): flipped
        // when they enclose a negative volume about their centre
        static List<Vector3[]> Outward(List<Vector3[]> tris)
        {
            Vector3 c = Vector3.Zero;
            int k = 0;
            foreach (Vector3[] t in tris) { c += t[0] + t[1] + t[2]; k += 3; }
            if (k == 0) return tris;
            c /= k;
            double vol = 0;
            foreach (Vector3[] t in tris) vol += Vector3.Dot(t[0] - c, Vector3.Cross(t[1] - c, t[2] - c));
            return vol >= 0 ? tris : tris.Select(t => new[] { t[0], t[2], t[1] }).ToList();
        }

        static float SizeOf(List<Vector3[]> surface)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (Vector3[] t in surface)
            {
                foreach (Vector3 v in t)
                {
                    lo = Vector3.ComponentMin(lo, v);
                    hi = Vector3.ComponentMax(hi, v);
                }
            }
            return hi.X > lo.X ? (hi - lo).Length : 0;
        }

        public static float HeightOf(List<Vector3[]> surface)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3[] t in surface)
            {
                foreach (Vector3 v in t)
                {
                    lo = MathF.Min(lo, v.Y);
                    hi = MathF.Max(hi, v.Y);
                }
            }
            return hi > lo ? hi - lo : 0;
        }

        // a DS body's fitted shell: vanilla's shell fitted node by node (Fit), on the body's own nodes and matrix ids
        public static Model Build(string name, Model src, Model ice, Model reference, bool facets,
            (Model Ref, Model Src, float Height)? posed = null, List<Vector3[]>? refSurface = null)
        {
            IceCorner[] shell = ShellOf(ice);
            IceCorner[] fit = Fit(shell, CloudsOf(reference), CloudsOf(src), ParentsOf(src));
            if (posed is (Model pr, Model ps, float height))
            {
                // even the gap, posed as the game poses both bodies, then back to each corner's node
                Vector3[] r = shell.Select(c => Vector3.TransformPosition(c.Pos, pr.Nodes[c.Node].Animation)).ToArray();
                Vector3[] f = fit.Select(c => Vector3.TransformPosition(c.Pos, ps.Nodes[c.Node].Animation)).ToArray();
                Vector3[] snapped = Snap(r, refSurface ?? SurfaceOf(pr), f, SurfaceOf(ps), SnapCapOfHeight * height);
                for (int i = 0; i < fit.Length; i++)
                {
                    fit[i].Pos = Vector3.TransformPosition(snapped[i], ps.Nodes[fit[i].Node].Animation.Inverted());
                }
            }
            var ids = src.NodeMatrixIds.ToList();
            int MatrixOf(int node)
            {
                for (int depth = 0; depth < 64 && (uint)node < (uint)src.Nodes.Count; depth++)
                {
                    int i = ids.IndexOf(node);
                    if (i >= 0) return i;
                    if (ids.Count < 31)
                    {
                        ids.Add(node); // (a node the body has no vertices on: the shell's piece there still follows it)
                        return ids.Count - 1;
                    }
                    node = src.Nodes[node].ParentIndex; // (no room for another matrix: its parent's)
                }
                return 0;
            }
            var corners = new List<Corner>(fit.Length);
            for (int t = 0; t + 2 < fit.Length; t += 3)
            {
                IceCorner a = fit[t], b = fit[t + 1], c = fit[t + 2];
                Vector3 face = Vector3.Cross(b.Pos - a.Pos, c.Pos - a.Pos);
                bool hasFace = a.Node == b.Node && b.Node == c.Node && face.LengthSquared > 1e-12f;
                if (hasFace) face.Normalize();
                foreach (IceCorner k in new[] { a, b, c })
                {
                    Vector3 nrm = facets && hasFace ? face
                        : k.Normal.LengthSquared > 1e-6f ? k.Normal.Normalized() : hasFace ? face : Vector3.Zero;
                    corners.Add(new Corner { Matrix = MatrixOf(k.Node), Pos = k.Pos, Normal = nrm });
                }
            }
            Material iceMat = ice.Materials[0];
            var lists = new List<IReadOnlyList<RenderInstruction>> { DisplayListFor(corners) };
            var dlists = new List<DisplayList> { ice.DisplayLists[ice.Meshes[0].DlistId] };
            var rawMeshes = new List<RawMesh> { MeshRaw(0, 0) };
            Header header = Patch(src.Header, (28, (ushort)ids.Count), (72, (ushort)1), (96, (ushort)1),
                (40, (ushort)ice.Recolors[0].Textures.Count), (48, (ushort)ice.Recolors[0].Palettes.Count),
                (98, (ushort)ice.TextureMatrices.Count));
            var nodes = src.RawNodes.ToList();
            bool hung = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                // the mesh hangs off the first node the body's meshes did: vertices pick their bone by matrix id
                if (nodes[i].MeshCount == 0) continue;
                nodes[i] = Patch(nodes[i], (76, (ushort)(hung ? 0 : 1)), (78, (ushort)0));
                hung = true;
            }
            var rawMaterials = new List<RawMaterial> { MaterialRaw(iceMat) };
            return new Model(name, false, header, nodes, rawMeshes, rawMaterials, dlists, lists, new AnimationResults(),
                ice.TextureMatrices, ice.Recolors, ids, src.NodePos, src.NodeInitPos, src.NodePosCounts, src.NodePosScales);
        }

        // the corners as MPH display list commands: the shell's own kind (bone, normal, position; no colour, no uvs: the
        // material's lit colours and the normal texgen do the rest)
        static List<RenderInstruction> DisplayListFor(List<Corner> corners)
        {
            var list = new List<RenderInstruction>(corners.Count * 3 + 4);
            int matrix = -1;
            uint normal = uint.MaxValue;
            list.Add(new RenderInstruction(InstructionCode.BEGIN_VTXS, 0));
            foreach (Corner c in corners)
            {
                if (c.Matrix != matrix)
                {
                    list.Add(new RenderInstruction(InstructionCode.MTX_RESTORE, (uint)c.Matrix));
                    matrix = c.Matrix;
                }
                uint nn = Ten(c.Normal.X) | Ten(c.Normal.Y) << 10 | Ten(c.Normal.Z) << 20;
                if (nn != normal)
                {
                    list.Add(new RenderInstruction(InstructionCode.NORMAL, nn));
                    normal = nn;
                }
                list.Add(new RenderInstruction(InstructionCode.VTX_16, Fx(c.Pos.X) | Fx(c.Pos.Y) << 16, Fx(c.Pos.Z)));
            }
            list.Add(new RenderInstruction(InstructionCode.END_VTXS));
            return list;
        }

        static uint Ten(float v) => (uint)Math.Clamp((int)MathF.Round(v * 512), -512, 511) & 0x3FF;

        static uint Fx(float v) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(v * 4096), short.MinValue, short.MaxValue);

        // the ice model's material as it is (RawMaterial's layout, as DarkSamusStandin.MaterialRaw writes it)
        static RawMaterial MaterialRaw(Material m)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            var nameBytes = new byte[64];
            Encoding.ASCII.GetBytes(m.Name, 0, Math.Min(m.Name.Length, 63), nameBytes, 0);
            w.Write(nameBytes);
            w.Write(m.InitLighting);
            w.Write((byte)m.Culling);
            w.Write(m.Alpha);
            w.Write(m.Wireframe);
            w.Write((short)m.PaletteId); w.Write((short)m.TextureId);
            w.Write((byte)m.XRepeat); w.Write((byte)m.YRepeat);
            foreach (ColorRgb c in new[] { m.Diffuse, m.Ambient, m.Specular })
            {
                w.Write(c.Red); w.Write(c.Green); w.Write(c.Blue);
            }
            w.Write((byte)0);
            w.Write((uint)m.PolygonMode);
            w.Write((byte)m.RenderMode); w.Write((byte)m.AnimationFlags); w.Write((ushort)0);
            w.Write((uint)m.TexgenMode);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((uint)m.MatrixId);
            w.Write((int)MathF.Round(m.ScaleS * 4096)); w.Write((int)MathF.Round(m.ScaleT * 4096));
            w.Write((ushort)MathF.Round(m.RotateZ / (2f * MathF.PI) * 65536f)); w.Write((ushort)0);
            w.Write((int)MathF.Round(m.TranslateS * 4096)); w.Write((int)MathF.Round(m.TranslateT * 4096));
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0);
            return Read.ReadStruct<RawMaterial>(ms.ToArray());
        }

        static RawMesh MeshRaw(int material, int dlist)
        {
            var b = new byte[4];
            BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)material);
            BitConverter.TryWriteBytes(b.AsSpan(2), (ushort)dlist);
            return Read.ReadStruct<RawMesh>(b);
        }

        // a copy of a raw struct with ushort fields replaced at byte offsets (RawFormats' sequential layout)
        static T Patch<T>(T value, params (int Offset, ushort Value)[] fields) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
            foreach ((int offset, ushort v) in fields)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(offset), v);
            }
            return Read.ReadStruct<T>(bytes);
        }
    }
}
