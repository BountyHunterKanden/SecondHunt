using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Line-of-sight through a trophy's surface: does the straight line from a surface vertex to a point inside the
    // body cross any other part of the surface? Used for bone ownership -- a bone may only take surface it can SEE
    // from inside the body (the standard auto-rigging rule): a breast the arm rests against in the sculpt is seen
    // from inside the chest, but from inside the arm the line has to leave the arm and enter the torso, so the arm
    // can't take it however close it is. A bounding-volume tree over the triangles keeps each test to a few dozen
    // triangle checks on a 100k-triangle trophy.
    public sealed class TrophySight
    {
        readonly Vector3[] _v;
        readonly int[] _tri;         // triangle corner indices (3 per triangle), reordered into tree leaf order
        readonly List<Node> _nodes = new();
        const int LeafSize = 8;

        struct Node { public Vector3 Min, Max; public int Left, Right, Start, Count; }

        // skip: triangles to leave out (hidden pieces); null = all
        public TrophySight(Vector3[] vertices, int[] tris, Func<int, bool>? skip = null)
        {
            _v = vertices;
            var ids = new List<int>();
            for (int t = 0; t + 2 < tris.Length; t += 3) if (skip == null || !skip(t)) ids.Add(t);
            var cen = new Vector3[tris.Length / 3];
            foreach (int t in ids) cen[t / 3] = (vertices[tris[t]] + vertices[tris[t + 1]] + vertices[tris[t + 2]]) / 3f;
            var order = ids.ToArray();
            Build(order, 0, order.Length, tris, cen);
            _tri = new int[order.Length * 3];
            for (int i = 0; i < order.Length; i++) { _tri[3 * i] = tris[order[i]]; _tri[3 * i + 1] = tris[order[i] + 1]; _tri[3 * i + 2] = tris[order[i] + 2]; }
        }

        int Build(int[] order, int start, int count, int[] tris, Vector3[] cen)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = start; i < start + count; i++)
                for (int c = 0; c < 3; c++) { var p = _v[tris[order[i] + c]]; mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
            int id = _nodes.Count;
            _nodes.Add(new Node { Min = mn, Max = mx, Left = -1, Right = -1, Start = start, Count = count });
            if (count <= LeafSize) return id;
            var ext = mx - mn; int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
            Array.Sort(order, start, count, Comparer<int>.Create((a, b) => cen[a / 3][axis].CompareTo(cen[b / 3][axis])));
            int half = count / 2;
            int l = Build(order, start, half, tris, cen), r = Build(order, start + half, count - half, tris, cen);
            var nd = _nodes[id]; nd.Left = l; nd.Right = r; _nodes[id] = nd;
            return id;
        }

        // true when the open segment from vertex `from` (index into the vertices) to point `to` crosses a triangle that
        // doesn't touch `from` itself; hits within `endSlack` of `to` don't count (the end sits inside a part, near its
        // own surface)
        public bool Blocked(int from, Vector3 to, float endSlack = 0f)
        {
            var p = _v[from]; var d = to - p; float len = d.Length;
            if (len < 1e-9f) return false;
            float tEnd = Math.Max(0f, 1f - endSlack / len);
            var inv = new Vector3(1f / (MathF.Abs(d.X) > 1e-12f ? d.X : 1e-12f), 1f / (MathF.Abs(d.Y) > 1e-12f ? d.Y : 1e-12f), 1f / (MathF.Abs(d.Z) > 1e-12f ? d.Z : 1e-12f));
            Span<int> stack = stackalloc int[64]; int sp = 0; stack[sp++] = 0;
            if (_nodes.Count == 0) return false;
            while (sp > 0)
            {
                var nd = _nodes[stack[--sp]];
                if (!SegBox(p, inv, tEnd, nd.Min, nd.Max)) continue;
                if (nd.Left < 0)
                {
                    for (int i = nd.Start; i < nd.Start + nd.Count; i++)
                    {
                        int a = _tri[3 * i], b = _tri[3 * i + 1], c = _tri[3 * i + 2];
                        if (a == from || b == from || c == from) continue;
                        if (SegHitsTri(p, d, tEnd, _v[a], _v[b], _v[c])) return true;
                    }
                    continue;
                }
                if (sp + 2 > stack.Length) return false;
                stack[sp++] = nd.Left; stack[sp++] = nd.Right;
            }
            return false;
        }

        // is q inside the closed surface? (odd number of crossings along a ray; majority of three rays, so a small hole
        // or a grazing hit doesn't flip it)
        public bool Inside(Vector3 q)
        {
            if (_nodes.Count == 0) return false;
            float far = (_nodes[0].Max - _nodes[0].Min).Length * 2f + 1f;
            int odd = 0;
            foreach (var dir in Rays)
            {
                var d = dir * far;
                var inv = new Vector3(1f / d.X, 1f / d.Y, 1f / d.Z);
                int hits = 0;
                Span<int> stack = stackalloc int[64]; int sp = 0; stack[sp++] = 0;
                while (sp > 0)
                {
                    var nd = _nodes[stack[--sp]];
                    if (!SegBox(q, inv, 1f, nd.Min, nd.Max)) continue;
                    if (nd.Left < 0)
                    {
                        for (int i = nd.Start; i < nd.Start + nd.Count; i++)
                            if (SegHitsTri(q, d, 1f, _v[_tri[3 * i]], _v[_tri[3 * i + 1]], _v[_tri[3 * i + 2]])) hits++;
                        continue;
                    }
                    if (sp + 2 > stack.Length) break;
                    stack[sp++] = nd.Left; stack[sp++] = nd.Right;
                }
                if ((hits & 1) == 1) odd++;
            }
            return odd >= 2;
        }
        static readonly Vector3[] Rays = { new(0.5773f, 0.5774f, 0.5774f), new(-0.2672f, 0.8018f, -0.5345f), new(0.6247f, -0.3123f, -0.7157f) };

        static bool SegBox(Vector3 p, Vector3 inv, float tEnd, Vector3 mn, Vector3 mx)
        {
            float t0 = 0f, t1 = tEnd;
            for (int a = 0; a < 3; a++)
            {
                float ta = (mn[a] - p[a]) * inv[a], tb = (mx[a] - p[a]) * inv[a];
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }

        // Moller-Trumbore on the segment p + t*d, 0 < t < tEnd
        static bool SegHitsTri(Vector3 p, Vector3 d, float tEnd, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a; var e2 = c - a;
            var pv = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-14f) return false;
            float inv = 1f / det;
            var tv = p - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            var qv = Vector3.Cross(tv, e1);
            float w = Vector3.Dot(d, qv) * inv;
            if (w < 0f || u + w > 1f) return false;
            float t = Vector3.Dot(e2, qv) * inv;
            return t > 1e-5f && t < tEnd;
        }
    }
}
