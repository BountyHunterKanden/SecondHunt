using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas.Editor
{
    // MAP MAKER E0 (docs/MAP_EDITOR.md section 3): the block world, Sauerbraten's model. An octree in a cube; every leaf
    // is solid or empty, and a solid leaf has 12 edges, each holding two 0-8 steps (low nibble: where the edge starts,
    // high nibble: where it ends, along its own axis), so pushing a corner in makes slopes, wedges and bevels.
    // Positions are whole numbers of the finest grid step (Unit world units); a node of size s covers s x s x s steps.
    public sealed class BlockWorld
    {
        public const float Unit = 0.25f;          // the finest grid step, in world units
        public const int Size = 512;              // steps on a side: 128 units, about the largest MP arena (checked in E1)
        public static readonly Vector3 Origin = new Vector3(-64, -32, -64);
        public const byte FullEdge = 0x80;        // starts at 0, ends at 8: an undented edge

        public sealed class Cube
        {
            public Cube[]? Children;              // 8, index x + 2y + 4z; null = a leaf
            public bool Solid;
            public readonly byte[] Edges = Full();
            public readonly byte[] Tex = new byte[6];   // texture id per face (index axis * 2 + side); 0 = the automatic one

            public bool HasTex
            {
                get
                {
                    foreach (byte t in Tex) if (t != 0) return true;
                    return false;
                }
            }

            public bool IsFull
            {
                get
                {
                    foreach (byte e in Edges) if (e != FullEdge) return false;
                    return true;
                }
            }
        }

        public Cube Root { get; private set; } = new Cube();
        public int Version { get; private set; }   // bumped on every edit (the editor re-meshes)

        static byte[] Full()
        {
            var e = new byte[12];
            Array.Fill(e, FullEdge);
            return e;
        }

        public static Vector3 ToWorld(Vector3i p) => Origin + (Vector3)p * Unit;
        public static Vector3 ToWorld(Vector3 p) => Origin + p * Unit;

        // a new world: one floor slab, 64 x 64 units, 2 units thick, its top at height 0
        public static BlockWorld Starter()
        {
            var w = new BlockWorld();
            int g = 8;                                     // 2 units
            int y = (int)((0 - Origin.Y) / Unit) - g;     // the slab under height 0
            int lo = (int)((-32 - Origin.X) / Unit), hi = (int)((32 - Origin.X) / Unit);
            for (int x = lo; x < hi; x += g)
            {
                for (int z = lo; z < hi; z += g) w.SetSolid(new Vector3i(x, y, z), g, true, merge: false);
            }
            w.MergeAll();
            return w;
        }

        public static bool InWorld(Vector3i p, int size) => p.X >= 0 && p.Y >= 0 && p.Z >= 0
            && p.X + size <= Size && p.Y + size <= Size && p.Z + size <= Size;

        // the node exactly at p with this size (a power of two, p aligned to it), splitting leaves on the way down: a
        // smaller grid splits blocks (a dented cube's children come out whole: an E0 simplification)
        public Cube At(Vector3i p, int size)
        {
            Cube c = Root;
            int s = Size;
            Vector3i m = Vector3i.Zero;
            while (s > size)
            {
                if (c.Children == null)
                {
                    c.Children = new Cube[8];
                    for (int i = 0; i < 8; i++)
                    {
                        c.Children[i] = new Cube { Solid = c.Solid };
                        Array.Copy(c.Tex, c.Children[i].Tex, 6);
                    }
                }
                s /= 2;
                int ix = p.X >= m.X + s ? 1 : 0, iy = p.Y >= m.Y + s ? 1 : 0, iz = p.Z >= m.Z + s ? 1 : 0;
                m += new Vector3i(ix, iy, iz) * s;
                c = c.Children[ix + 2 * iy + 4 * iz];
            }
            return c;
        }

        // the leaf holding the step p, with its corner and size
        public (Cube Leaf, Vector3i Min, int Size) LeafAt(Vector3i p)
        {
            Cube c = Root;
            int s = Size;
            Vector3i m = Vector3i.Zero;
            while (c.Children != null)
            {
                s /= 2;
                int ix = p.X >= m.X + s ? 1 : 0, iy = p.Y >= m.Y + s ? 1 : 0, iz = p.Z >= m.Z + s ? 1 : 0;
                m += new Vector3i(ix, iy, iz) * s;
                c = c.Children[ix + 2 * iy + 4 * iz];
            }
            return (c, m, s);
        }

        public void SetSolid(Vector3i p, int size, bool solid, bool merge = true)
        {
            if (!InWorld(p, size)) return;
            Cube c = At(p, size);
            c.Children = null;
            c.Solid = solid;
            Array.Fill(c.Edges, FullEdge);
            Array.Clear(c.Tex);
            if (merge) MergeAll();
            Version++;
        }

        // the texture id of one face of a leaf (the block must be a leaf of this size); false when there is no such block
        public bool PaintFace(Vector3i p, int size, int a, int s, int tex)
        {
            if (!InWorld(p, size)) return false;
            Cube c = At(p, size);
            if (c.Children != null || !c.Solid) return false;
            c.Tex[a * 2 + s] = (byte)tex;
            MergeAll();
            Version++;
            return true;
        }

        // paints the grid cell (p, g): its face (a, s), or all six faces (a < 0). A bigger whole block is split down to the
        // cell (a dented one is painted whole), smaller blocks inside the cell are all painted (for one face: those on its plane)
        public bool PaintCell(Vector3i p, int g, int a, int s, int tex)
        {
            if (!InWorld(p, g)) return false;
            (Cube leaf, Vector3i lmin, int lsize) = LeafAt(p);
            bool any = false;
            void Paint(Cube c)
            {
                if (a < 0) Array.Fill(c.Tex, (byte)tex);
                else c.Tex[a * 2 + s] = (byte)tex;
                any = true;
            }
            if (lsize >= g)
            {
                if (!leaf.Solid) return false;
                Paint(lsize > g && leaf.IsFull ? At(p, g) : leaf);
            }
            else
            {
                foreach ((Cube c, Vector3i min, int size) in Leaves())
                {
                    if (!c.Solid) continue;
                    if (min.X < p.X || min.Y < p.Y || min.Z < p.Z || min.X + size > p.X + g || min.Y + size > p.Y + g || min.Z + size > p.Z + g) continue;
                    if (a >= 0 && min[a] + (s == 1 ? size : 0) != p[a] + (s == 1 ? g : 0)) continue;
                    Paint(c);
                }
            }
            if (!any) return false;
            MergeAll();
            Version++;
            return true;
        }

        // all six faces of a block
        public bool PaintBlock(Vector3i p, int size, int tex)
        {
            if (!InWorld(p, size)) return false;
            Cube c = At(p, size);
            if (c.Children != null || !c.Solid) return false;
            Array.Fill(c.Tex, (byte)tex);
            MergeAll();
            Version++;
            return true;
        }

        // ---- edges and corners ----

        // the edge along axis a through the corner whose bits on the other two axes are bu ((a+1)%3) and bv ((a+2)%3)
        public static int EdgeIndex(int a, int bu, int bv) => a * 4 + bu + 2 * bv;

        static int Bit(int corner, int axis) => (corner >> axis) & 1;

        // a corner (bits x | y << 1 | z << 2) of a leaf, in steps of 1/8 of the cube (0..8 per axis)
        public static Vector3 CornerEighths(Cube c, int corner)
        {
            var r = new Vector3();
            for (int a = 0; a < 3; a++)
            {
                int u = (a + 1) % 3, v = (a + 2) % 3;
                byte e = c.Edges[EdgeIndex(a, Bit(corner, u), Bit(corner, v))];
                r[a] = Bit(corner, a) == 1 ? e >> 4 : e & 0xF;
            }
            return r;
        }

        // no volume left: every edge along some axis has met itself
        public static bool Collapsed(Cube c)
        {
            for (int a = 0; a < 3; a++)
            {
                bool all = true;
                for (int i = 0; i < 4; i++)
                {
                    byte e = c.Edges[a * 4 + i];
                    if ((e & 0xF) < (e >> 4)) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        // push one corner of a face (face: axis a, side s = 0 or 1; corner bits) in (into the block) or out by one eighth
        public bool PushCorner(Vector3i p, int size, int a, int s, int corner, bool inward)
        {
            if (!InWorld(p, size)) return false;
            Cube c = At(p, size);
            if (c.Children != null || !c.Solid) return false;
            int u = (a + 1) % 3, v = (a + 2) % 3;
            int i = EdgeIndex(a, Bit(corner, u), Bit(corner, v));
            int lo = c.Edges[i] & 0xF, hi = c.Edges[i] >> 4;
            if (s == 1) hi = Math.Clamp(hi + (inward ? -1 : 1), lo, 8);
            else lo = Math.Clamp(lo + (inward ? 1 : -1), 0, hi);
            c.Edges[i] = (byte)(lo | hi << 4);
            if (Collapsed(c))
            {
                c.Solid = false;
                Array.Fill(c.Edges, FullEdge);
            }
            MergeAll();
            Version++;
            return true;
        }

        // the whole face in or out by one eighth (its four corners)
        public bool PushFace(Vector3i p, int size, int a, int s, bool inward)
        {
            bool any = false;
            for (int corner = 0; corner < 8; corner++)
            {
                if (Bit(corner, a) != s) continue;
                Cube c = At(p, size);
                if (!c.Solid || c.Children != null) break;
                int u = (a + 1) % 3, v = (a + 2) % 3;
                int i = EdgeIndex(a, Bit(corner, u), Bit(corner, v));
                int lo = c.Edges[i] & 0xF, hi = c.Edges[i] >> 4;
                if (s == 1) hi = Math.Clamp(hi + (inward ? -1 : 1), lo, 8);
                else lo = Math.Clamp(lo + (inward ? 1 : -1), 0, hi);
                c.Edges[i] = (byte)(lo | hi << 4);
                any = true;
            }
            if (any)
            {
                Cube c = At(p, size);
                if (Collapsed(c))
                {
                    c.Solid = false;
                    Array.Fill(c.Edges, FullEdge);
                }
                MergeAll();
                Version++;
            }
            return any;
        }

        // Sauerbraten's remip, the simple part: eight identical whole children become one block again
        public void MergeAll() => Merge(Root);

        static void Merge(Cube c)
        {
            if (c.Children == null) return;
            foreach (Cube k in c.Children) Merge(k);
            Cube first = c.Children[0];
            if (first.Children != null || (first.Solid && !first.IsFull)) return;
            foreach (Cube k in c.Children)
            {
                if (k.Children != null || k.Solid != first.Solid || (k.Solid && !k.IsFull)) return;
                if (k.Solid && !k.Tex.AsSpan().SequenceEqual(first.Tex)) return;
            }
            c.Solid = first.Solid;
            Array.Fill(c.Edges, FullEdge);
            Array.Copy(first.Tex, c.Tex, 6);
            c.Children = null;
        }

        // every leaf: (leaf, corner in steps, size in steps)
        public IEnumerable<(Cube Leaf, Vector3i Min, int Size)> Leaves()
        {
            var stack = new Stack<(Cube, Vector3i, int)>();
            stack.Push((Root, Vector3i.Zero, Size));
            while (stack.Count > 0)
            {
                (Cube c, Vector3i m, int s) = stack.Pop();
                if (c.Children == null)
                {
                    yield return (c, m, s);
                    continue;
                }
                int h = s / 2;
                for (int i = 0; i < 8; i++) stack.Push((c.Children[i], m + new Vector3i(i & 1, (i >> 1) & 1, i >> 2) * h, h));
            }
        }

        // ---- save / load: the block tree as the .shmap package's blocks.bin (MapDocument), and E0's old map.smm ----

        const uint LegacyMagic = 0x314D4D53;  // "SMM1": E0's file, no textures
        const uint Magic = 0x32424D53;        // "SMB2": blocks.bin

        public void Write(Stream stream)
        {
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Size);
            Write(w, Root);
        }

        // kinds: 0 empty, 1 whole block, 2 split into eight, 3 dented block (12 edges), 4 / 5 the same with face textures
        static void Write(BinaryWriter w, Cube c)
        {
            if (c.Children != null)
            {
                w.Write((byte)2);
                foreach (Cube k in c.Children) Write(w, k);
            }
            else if (!c.Solid)
            {
                w.Write((byte)0);
            }
            else
            {
                bool full = c.IsFull, tex = c.HasTex;
                w.Write((byte)(full ? (tex ? 4 : 1) : (tex ? 5 : 3)));
                if (!full) w.Write(c.Edges);
                if (tex) w.Write(c.Tex);
            }
        }

        public static BlockWorld? Read(Stream stream)
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            uint magic = r.ReadUInt32();
            if ((magic != Magic && magic != LegacyMagic) || r.ReadInt32() != Size) return null;
            return new BlockWorld { Root = Read(r) };
        }

        // E0's map.smm
        public static BlockWorld? LoadLegacy(string path)
        {
            if (!File.Exists(path)) return null;
            using FileStream f = File.OpenRead(path);
            return Read(f);
        }

        static Cube Read(BinaryReader r)
        {
            byte kind = r.ReadByte();
            var c = new Cube();
            switch (kind)
            {
            case 2:
                c.Children = new Cube[8];
                for (int i = 0; i < 8; i++) c.Children[i] = Read(r);
                break;
            case 1:
            case 3:
            case 4:
            case 5:
                c.Solid = true;
                if (kind == 3 || kind == 5) r.Read(c.Edges, 0, 12);
                if (kind >= 4) r.Read(c.Tex, 0, 6);
                break;
            }
            return c;
        }
    }
}
