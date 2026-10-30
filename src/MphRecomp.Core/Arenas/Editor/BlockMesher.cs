using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using Cube = MphRecomp.Arenas.Editor.BlockWorld.Cube;

namespace MphRecomp.Arenas.Editor
{
    // MAP MAKER E0: blocks -> triangles. Every solid leaf gives its six faces (corners from the edge steps), two triangles
    // each; a whole flat face against whole solid neighbours is hidden. The same triangles are the editor's picture, the
    // picking surface, the arena model and the collision.
    public static class BlockMesher
    {
        public readonly struct Tri
        {
            public readonly Vector3 A, B, C;
            public readonly Vector3 Normal;
            public readonly Vector3 Color;
            public readonly Vector3i Min;     // the leaf it came from, in grid steps
            public readonly int Size;
            public readonly int Axis, Side;   // the leaf face
            public readonly int Tex;          // texture library id (1..), 0 = none (flat colour)
            public readonly Vector2 UvA, UvB, UvC;   // in texture repeats (one repeat = the whole picture), planar from world space

            public Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n, Vector3 color, Vector3i min, int size, int axis, int side,
                int tex = 0, Vector2 uvA = default, Vector2 uvB = default, Vector2 uvC = default)
            {
                A = a; B = b; C = c; Normal = n; Color = color; Min = min; Size = size; Axis = axis; Side = side;
                Tex = tex; UvA = uvA; UvB = uvB; UvC = uvC;
            }
        }

        static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.3f, -1, 0.2f));

        // merge: whole flat faces that touch in one plane with one texture become single rectangles (two triangles), the way
        // the game's own arenas are built; the picture, the budget and the compile use that. The editor's picking uses the
        // unmerged list (every triangle knows its leaf).
        public static List<Tri> Build(BlockWorld world, TextureLibrary? textures = null, bool merge = false)
        {
            var tris = new List<Tri>();
            // (axis, side, plane, texture) -> the faces lying in it: u, v and the edge lengths, in grid steps
            var planes = new Dictionary<(int A, int S, int Plane, int Tex), List<Rect>>();
            foreach ((Cube leaf, Vector3i min, int size) in world.Leaves())
            {
                if (!leaf.Solid) continue;
                for (int a = 0; a < 3; a++)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        bool full = FaceFull(leaf, a, s);
                        if (full && Hidden(world, min, size, a, s)) continue;
                        if (merge && textures != null && full)
                        {
                            int id = leaf.Tex[a * 2 + s];
                            if (id == 0) id = TextureLibrary.AutoFor(AxisNormal(a, s));
                            if (textures.Get(id) != null)
                            {
                                var key = (a, s, min[a] + (s == 1 ? size : 0), id);
                                if (!planes.TryGetValue(key, out List<Rect>? list)) planes[key] = list = new List<Rect>();
                                list.Add(new Rect(min[(a + 1) % 3], min[(a + 2) % 3], size, size, min, size));
                                continue;
                            }
                        }
                        EmitFace(tris, leaf, min, size, a, s, textures);
                    }
                }
            }
            foreach (((int a, int s, int plane, int id), List<Rect> rects) in planes)
            {
                TextureLibrary.Entry entry = textures!.Get(id)!;
                foreach (Rect r in MergeRects(rects)) EmitRect(tris, r, a, s, plane, entry);
            }
            return tris;
        }

        readonly record struct Rect(int U, int V, int W, int H, Vector3i Min, int Size);

        const int MaxMergedSteps = 256;   // 64 units: texture coordinates stay inside the DS's range (12.4 fixed)

        static Vector3 AxisNormal(int a, int s)
        {
            var n = Vector3.Zero;
            n[a] = s == 1 ? 1 : -1;
            return n;
        }

        // runs along u of equal height, then along v of equal width, until nothing joins
        static List<Rect> MergeRects(List<Rect> rects)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                int before = rects.Count;
                rects = Join(rects, alongU: true);
                rects = Join(rects, alongU: false);
                if (rects.Count == before) break;
            }
            return rects;
        }

        static List<Rect> Join(List<Rect> rects, bool alongU)
        {
            // along u: same v and height, ordered by u; along v: same u and width, ordered by v
            rects.Sort((x, y) =>
            {
                int c = alongU ? x.V.CompareTo(y.V) : x.U.CompareTo(y.U);
                if (c == 0) c = alongU ? x.H.CompareTo(y.H) : x.W.CompareTo(y.W);
                if (c == 0) c = alongU ? x.U.CompareTo(y.U) : x.V.CompareTo(y.V);
                return c;
            });
            var result = new List<Rect>(rects.Count);
            Rect cur = rects[0];
            for (int i = 1; i < rects.Count; i++)
            {
                Rect n = rects[i];
                bool joins = alongU
                    ? n.V == cur.V && n.H == cur.H && n.U == cur.U + cur.W && cur.W + n.W <= MaxMergedSteps
                    : n.U == cur.U && n.W == cur.W && n.V == cur.V + cur.H && cur.H + n.H <= MaxMergedSteps;
                if (joins) cur = alongU ? cur with { W = cur.W + n.W } : cur with { H = cur.H + n.H };
                else
                {
                    result.Add(cur);
                    cur = n;
                }
            }
            result.Add(cur);
            return result;
        }

        static void EmitRect(List<Tri> tris, Rect r, int a, int s, int plane, TextureLibrary.Entry entry)
        {
            int u = (a + 1) % 3, v = (a + 2) % 3;
            Vector3 P(int bu, int bv)
            {
                var steps = Vector3i.Zero;
                steps[a] = plane;
                steps[u] = r.U + bu * r.W;
                steps[v] = r.V + bv * r.H;
                return BlockWorld.ToWorld(steps);
            }
            Vector3[] p = s == 1 ? new[] { P(0, 0), P(1, 0), P(1, 1), P(0, 1) } : new[] { P(0, 1), P(1, 1), P(1, 0), P(0, 0) };
            Vector3 n = AxisNormal(a, s);
            var color = new Vector3(Light(n));
            foreach (int[] t in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
            {
                Vector3 pa = p[t[0]], pb = p[t[1]], pc = p[t[2]];
                tris.Add(new Tri(pa, pb, pc, n, color, r.Min, r.Size, a, s, entry.Id, Uv(pa, a, entry), Uv(pb, a, entry), Uv(pc, a, entry)));
            }
        }

        // the 4 corners of face (a, s), wound so the face looks out of the block
        public static int[] FaceCorners(int a, int s)
        {
            int u = (a + 1) % 3, v = (a + 2) % 3;
            int C(int bu, int bv) => (s << a) | (bu << u) | (bv << v);
            return s == 1 ? new[] { C(0, 0), C(1, 0), C(1, 1), C(0, 1) } : new[] { C(0, 1), C(1, 1), C(1, 0), C(0, 0) };
        }

        public static Vector3 CornerWorld(Cube leaf, Vector3i min, int size, int corner)
            => BlockWorld.ToWorld((Vector3)min + BlockWorld.CornerEighths(leaf, corner) * (size / 8f));

        // the face is the whole flat square of the cube (all four corners undented)
        static bool FaceFull(Cube leaf, int a, int s)
        {
            foreach (int corner in FaceCorners(a, s))
            {
                Vector3 e = BlockWorld.CornerEighths(leaf, corner);
                for (int k = 0; k < 3; k++)
                {
                    if (e[k] != ((corner >> k) & 1) * 8) return false;
                }
            }
            return true;
        }

        // the neighbour across face (a, s) covers it completely with whole solid blocks
        static bool Hidden(BlockWorld world, Vector3i min, int size, int a, int s)
        {
            Vector3i n = min;
            n[a] += s == 1 ? size : -size;
            if (!BlockWorld.InWorld(n, size)) return false;   // the world's edge: drawn
            return Covers(world, n, size, a, 1 - s);
        }

        // the region (min, size) shows a whole solid face on side s of axis a
        static bool Covers(BlockWorld world, Vector3i min, int size, int a, int s)
        {
            (Cube node, int nodeSize) = Find(world, min, size);
            if (node.Children == null)
            {
                if (!node.Solid) return false;
                if (nodeSize > size) return node.IsFull;      // inside (or on) a bigger whole block
                return FaceFull(node, a, s);
            }
            int h = size / 2;
            for (int i = 0; i < 8; i++)
            {
                int bit = (i >> a) & 1;
                if (bit != s) continue;
                var c = min + new Vector3i(i & 1, (i >> 1) & 1, i >> 2) * h;
                if (!Covers(world, c, h, a, s)) return false;
            }
            return true;
        }

        // the node at (min, size), or the leaf above it; never splits
        static (Cube Node, int Size) Find(BlockWorld world, Vector3i p, int size)
        {
            Cube c = world.Root;
            int s = BlockWorld.Size;
            Vector3i m = Vector3i.Zero;
            while (s > size && c.Children != null)
            {
                s /= 2;
                int ix = p.X >= m.X + s ? 1 : 0, iy = p.Y >= m.Y + s ? 1 : 0, iz = p.Z >= m.Z + s ? 1 : 0;
                m += new Vector3i(ix, iy, iz) * s;
                c = c.Children[ix + 2 * iy + 4 * iz];
            }
            return (c, s);
        }

        static void EmitFace(List<Tri> tris, Cube leaf, Vector3i min, int size, int a, int s, TextureLibrary? textures)
        {
            int[] k = FaceCorners(a, s);
            Vector3 p0 = CornerWorld(leaf, min, size, k[0]), p1 = CornerWorld(leaf, min, size, k[1]);
            Vector3 p2 = CornerWorld(leaf, min, size, k[2]), p3 = CornerWorld(leaf, min, size, k[3]);
            // split along the diagonal that keeps the face convex (the fourth corner on or behind the first triangle)
            Vector3 n012 = Vector3.Cross(p1 - p0, p2 - p0);
            bool split02 = n012.LengthSquared < 1e-12f || Vector3.Dot(n012, p3 - p0) <= 1e-6f;
            if (split02)
            {
                Add(tris, p0, p1, p2, min, size, a, s, leaf, textures);
                Add(tris, p0, p2, p3, min, size, a, s, leaf, textures);
            }
            else
            {
                Add(tris, p1, p2, p3, min, size, a, s, leaf, textures);
                Add(tris, p1, p3, p0, min, size, a, s, leaf, textures);
            }
        }

        static void Add(List<Tri> tris, Vector3 a, Vector3 b, Vector3 c, Vector3i min, int size, int axis, int side, Cube leaf, TextureLibrary? textures)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared < 1e-8f) return;
            n.Normalize();
            TextureLibrary.Entry? entry = null;
            if (textures != null)
            {
                int id = leaf.Tex[axis * 2 + side];
                entry = textures.Get(id != 0 ? id : TextureLibrary.AutoFor(n));
            }
            if (entry == null)
            {
                tris.Add(new Tri(a, b, c, n, Shade(n, (a + b + c) / 3), min, size, axis, side));
                return;
            }
            tris.Add(new Tri(a, b, c, n, new Vector3(Light(n)), min, size, axis, side, entry.Id, Uv(a, axis, entry), Uv(b, axis, entry), Uv(c, axis, entry)));
        }

        // planar projection along the face's axis, anchored at the world origin so neighbouring faces line up
        public static Vector2 Uv(Vector3 p, int axis, TextureLibrary.Entry t)
        {
            (float u, float v) = axis == 1 ? (p.X, p.Z) : axis == 0 ? (p.Z, -p.Y) : (p.X, -p.Y);
            return new Vector2(u * TextureLibrary.TexelsPerUnit / t.Width, v * TextureLibrary.TexelsPerUnit / t.Height);
        }

        static float Light(Vector3 n) => 0.55f + 0.45f * MathF.Max(0, Vector3.Dot(n, -LightDir));

        // floors tinted by height (one colour per unit, five in turn: steps of 1, 2 or 4 all differ)
        static readonly Vector3[] FloorTints =
        {
            new(0.62f, 0.66f, 0.70f), new(0.58f, 0.70f, 0.58f), new(0.72f, 0.66f, 0.52f), new(0.66f, 0.58f, 0.72f), new(0.54f, 0.68f, 0.72f),
        };

        // floors, walls and ceilings in their own colour, a 4-unit checker, and the light baked in (the model is unlit)
        static Vector3 Shade(Vector3 n, Vector3 centre)
        {
            Vector3 baseColor = n.Y > 0.7f ? FloorTints[((int)MathF.Floor(centre.Y + 0.01f) % 5 + 5) % 5]
                : n.Y < -0.7f ? new Vector3(0.42f, 0.40f, 0.38f)
                : new Vector3(0.70f, 0.58f, 0.42f);
            int cx = (int)MathF.Floor(centre.X / 4), cy = (int)MathF.Floor(centre.Y / 4), cz = (int)MathF.Floor(centre.Z / 4);
            float checker = ((cx + cy + cz) & 1) == 0 ? 1f : 0.86f;
            float light = Light(n);
            return Vector3.Clamp(baseColor * checker * light, Vector3.Zero, Vector3.One);
        }
    }
}
