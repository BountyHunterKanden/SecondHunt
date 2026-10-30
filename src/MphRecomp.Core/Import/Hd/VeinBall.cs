using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Import.Hd
{
    // "veins" on a static ball recipe: Echoes' final-form Dark Samus's see-through morph ball (owner 2026-10-18; the
    // mockups in extract_out/characters_mockups/darksamus/form2), built at import from the user's own disc:
    //   shell   the ball's own mesh wearing her final form's see-through shell material ("shell": its index in her model)
    //   vents   the ball's own mesh again, its vent glow alone, additive (the app draws it front faces only: HdBall.Veins)
    //   veins   her own red vessels ("red") from the final-form model (ANCS char "char"), posed by "pose" at its first frame
    //           (curled up) and pulled in about her middle onto a soft sphere inside the shell
    //   lines   her own blue shell lines ("blue"), the outward-facing ones, on a sphere just inside the shell
    //   heart   the compact knot of her red vessels in her chest, at the ball's centre, bigger (the app beats it: HdBall.Beat)
    // Every material is hers or the ball's, unchanged but for the vent and line textures, which keep only their bright lines
    // (VentTexture / LineTexture). What is new is only where the pieces sit.
    //   "veins": {"ancs": "39900a11", "char": "Battle_3", "pose": "<clip>", "shell": 3, "red": 7, "blue": [4, 5], "vent": 1,
    //             "middle": [2, ..., 8]}   (vent: the texture of the ball's material; middle: her materials that find her middle)
    public static class VeinBall
    {
        public sealed record Spec(uint Ancs, string Char, string Pose, int Shell, int Red, int[] Blue, int Vent, int[] Middle);

        const double VeinRadius = 0.445;    // the red vessels' outer sphere (the ball's radius is 0.51 in its own units)
        const double VeinFalloff = 0.2;     // r' = VeinRadius * (r / rmax)^falloff: the curl's depth kept, squeezed
        const double LineRadius = 0.47;     // the blue lines' sphere
        const double LineFacing = 0.25;     // the blue lines kept: facing out from her middle at least this much
        public const double HeartScale = 2.3;

        // Parts[0] (the ball as the game draws it) becomes the shell + vents; the vessels, lines and heart follow it
        public static void Build(Func<uint, byte[]> res, HdRecipe h, RetroTrophy.Result result, double s, Action<float, string> say)
        {
            Spec v = h.Veins!;
            RetroTrophy.Part ballPart = result.Parts[0];
            Cmdl ball = ballPart.Model;
            Vector3d bc = Centre(ball.Positions);

            (uint mid, uint sid, uint kid) = Ancs.FindCharacter(res(v.Ancs), v.Char);
            Cmdl body = Cmdl.Load(res, mid, sid);
            Cinf skel = Cinf.Load(res(kid));
            double[] posed = Pose(res, v, body, skel);
            if (v.Shell >= body.Materials.Length || v.Red >= body.Materials.Length || v.Blue.Any(b => b >= body.Materials.Length))
                throw new System.IO.InvalidDataException($"{h.Name}: veins: {v.Char} has {body.Materials.Length} materials");
            if (v.Vent >= ball.Materials[0].TexIds.Length) throw new System.IO.InvalidDataException($"{h.Name}: veins: the ball's material has no texture {v.Vent}");

            // shell + vents: the ball's corners in two parts (the app knows the vents by name: <stem>_vent0)
            int[] all = Enumerable.Range(0, ball.TriangleCount).ToArray(), zero = new int[all.Length];
            RetroTrophy.Mat vent = VentMat(ball.Materials[0], body.Materials[v.Blue[0]], v.Vent);
            result.Parts[0] = new RetroTrophy.Part { Model = Clone(ball, all, zero), Mats = new[] { RetroTrophy.Mat.From(body.Materials[v.Shell]) }, P = ballPart.P, N = ballPart.N, Tag = "mat" };
            result.Parts.Insert(1, new RetroTrophy.Part { Model = Clone(ball, all, zero), Mats = new[] { vent }, P = ballPart.P, N = ballPart.N, Tag = "vent" });
            result.TexFilters[vent.TexIds[0]] = VentTexture;
            result.TexFilters[result.Parts[0].Mats[0].TexIds[0]] = ShellTexture;
            foreach (int b in v.Blue) result.TexFilters[body.Materials[b].TexIds[0]] = LineTexture;

            // her middle in the curl: the middle of the 2..98 percentile box of her inner materials ("middle": all but
            // the glow sheets)
            int[] inner = Enumerable.Range(0, body.TriangleCount).Where(t => v.Middle.Contains(body.TriangleMaterial[t])).ToArray();
            Vector3d c = PercentileMiddle(posed, body, inner);

            // the heart: the red material's compact knot (the largest connected piece under 0.18 across, over 40 triangles)
            int[] red = Enumerable.Range(0, body.TriangleCount).Where(t => body.TriangleMaterial[t] == v.Red).ToArray();
            var heart = new HashSet<int>();
            foreach (int[] comp in Components(body, red))
            {
                (Vector3d lo, Vector3d hi) = Box(posed, body, comp);
                Vector3d ext = hi - lo;
                if (Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) < 0.18 && comp.Length > 40) heart.UnionWith(comp);
            }
            int[] vessels = red.Where(t => !heart.Contains(t)).ToArray();
            int[] heartTris = red.Where(heart.Contains).ToArray();
            if (heartTris.Length == 0) say(0.15f, $"veins: no heart found in {v.Char}'s material {v.Red}");

            // the vessels: pulled in about her middle, the farthest at VeinRadius
            var rs = vessels.SelectMany(t => Corners(body, t)).Distinct().Select(p => (Pos(posed, p) - c).Length).OrderBy(x => x).ToArray();
            double rmax = rs.Length > 0 ? rs[(int)Math.Min(rs.Length - 1, Math.Round(0.995 * (rs.Length - 1)))] : 1;
            double[] veinP = Map(posed, body, vessels, p =>
            {
                Vector3d d = p - c;
                double r = d.Length;
                return r < 1e-9 ? bc : bc + d / r * (VeinRadius * Math.Pow(r / rmax, VeinFalloff));
            });
            AddPart(result, body, vessels, veinP, s, "vein", v.Red);

            // the blue lines: the outward-facing ones (her limbs stack in the curl), on their sphere
            int[] blue = Enumerable.Range(0, body.TriangleCount).Where(t => v.Blue.Contains(body.TriangleMaterial[t]) && Facing(posed, body, t, c) > LineFacing).ToArray();
            double[] lineP = Map(posed, body, blue, p =>
            {
                Vector3d d = p - c;
                double r = d.Length;
                return r < 1e-9 ? bc : bc + d / r * LineRadius;
            });
            AddPart(result, body, blue, lineP, s, "line", v.Blue);

            // the heart: centred on the ball, HeartScale times her own (beating in the app around the ball's centre)
            if (heartTris.Length > 0)
            {
                Vector3d hc = Vector3d.Zero;
                int[] hp = heartTris.SelectMany(t => Corners(body, t)).Distinct().ToArray();
                foreach (int p in hp) hc += Pos(posed, p);
                hc /= hp.Length;
                double[] heartP = Map(posed, body, heartTris, p => bc + (p - hc) * HeartScale);
                AddPart(result, body, heartTris, heartP, s, "heart", v.Red);
            }
            say(0.2f, $"veins: {v.Char} posed by {v.Pose}: shell = its material {v.Shell}, {vessels.Length} vessel, {blue.Length} line, "
                + $"{heartTris.Length} heart triangles; vents from the ball's texture {v.Vent}");
        }

        // the vents: the ball's glow texture alone (unlit), added -- one stage: out = tex
        static RetroTrophy.Mat VentMat(Cmdl.Material ballMat, Cmdl.Material additive, int tex)
        {
            Cmdl.TevStage src = ballMat.Stages.First(st => st.Tex == tex);
            var st = new Cmdl.TevStage
            {
                ColorIn = new[] { 15, 8, 12, 15 }, AlphaIn = new[] { 7, 7, 7, 7 },
                ColorOp = new Cmdl.TevOp { Clamp = 1 }, AlphaOp = new Cmdl.TevOp { Clamp = 1 },
                Tex = 0, TexCoord = 0, Ras = 255, RawColorOp = additive.Stages[0].RawColorOp, RawAlphaOp = additive.Stages[0].RawAlphaOp,
            };
            return new RetroTrophy.Mat
            {
                Flags = additive.Flags, TexIds = new[] { (ulong)ballMat.TexIds[tex] }, BlendSrc = additive.BlendSrc, BlendDst = additive.BlendDst,
                Channels = additive.Channels, Stages = new[] { st }, TexGens = new[] { ballMat.TexGens[src.TexCoord] },
            };
        }

        // her shell's rim map (its alpha: 0.43 facing the camera, 1 at the rim) remapped to 0.45 facing .. 0.95 at the rim (was
        // 0.2 .. 0.7, the mockup's; owner 10-18: "just like slightly more opaque", so the ball's shape reads), her vessels
        // still showing through
        public static void ShellTexture(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                double t = Math.Clamp((rgba[i] / 255.0 - 0.43) / 0.57, 0, 1);
                rgba[i] = (byte)Math.Round((0.45 + 0.5 * t) * 255);
            }
        }

        // the vent glow without its dim wash: only the bright vents, at 0.7 (the app adds the additive parts twice)
        public static void VentTexture(byte[] rgba)
        {
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double l = (rgba[i] + rgba[i + 1] + rgba[i + 2]) / (3 * 255.0);
                double k = Math.Clamp((l - 0.18) / 0.3, 0, 1) * 0.35;
                for (int ch = 0; ch < 3; ch++) rgba[i + ch] = (byte)Math.Clamp(rgba[i + ch] * k, 0, 255);
            }
        }

        // the blue shell lines alone: the mid-bright lines kept (the brightest specks are her chest lights, left out), tinted
        // her blue
        public static void LineTexture(byte[] rgba)
        {
            double[] tint = { 0.35, 0.6, 1.0 };
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double l = (rgba[i] + rgba[i + 1] + rgba[i + 2]) / (3 * 255.0);
                double line = l < 0.5 ? Math.Pow(Math.Clamp((l - 0.10) / 0.24, 0, 1), 1.5) : 0;
                for (int ch = 0; ch < 3; ch++)
                {
                    double col = 0.6 * tint[ch] + 0.4 * rgba[i + ch] / 255.0;
                    rgba[i + ch] = (byte)Math.Clamp(col * line * 0.65 * 255, 0, 255);
                }
            }
        }

        // one part from some of the body's triangles at new positions; its normals smoothed over the new shape
        static void AddPart(RetroTrophy.Result result, Cmdl body, int[] tris, double[] retroP, double s, string tag, params int[] mats)
        {
            if (tris.Length == 0) return;
            int[] map = Enumerable.Repeat(-1, body.Materials.Length).ToArray();
            for (int k = 0; k < mats.Length; k++) map[mats[k]] = k;
            var m = Clone(body, tris, tris.Select(t => map[body.TriangleMaterial[t]]).ToArray());
            for (int k = 0; k < m.Triangles.Length; k++) m.Triangles[k].N = m.Triangles[k].P;
            var n = new double[retroP.Length];
            for (int t = 0; t < m.TriangleCount; t++)
            {
                int a = m.Triangles[3 * t].P, b = m.Triangles[3 * t + 1].P, d = m.Triangles[3 * t + 2].P;
                Vector3d fn = Vector3d.Cross(Pos(retroP, b) - Pos(retroP, a), Pos(retroP, d) - Pos(retroP, a));
                foreach (int p in new[] { a, b, d })
                {
                    n[3 * p] += fn.X; n[3 * p + 1] += fn.Y; n[3 * p + 2] += fn.Z;
                }
            }
            var nf = new float[n.Length];
            for (int p = 0; p + 2 < n.Length; p += 3)
            {
                double len = Math.Sqrt(n[p] * n[p] + n[p + 1] * n[p + 1] + n[p + 2] * n[p + 2]);
                if (len > 1e-12) { nf[p] = (float)(n[p] / len); nf[p + 1] = (float)(n[p + 1] / len); nf[p + 2] = (float)(n[p + 2] / len); }
            }
            result.Parts.Add(new RetroTrophy.Part
            {
                Model = m, Mats = mats.Select(i => RetroTrophy.Mat.From(body.Materials[i])).ToArray(),
                P = RetroTrophy.ToTrophy(retroP.Select(x => (float)x).ToArray(), s, null), N = RetroTrophy.ToTrophy(nf, 1.0, null), Tag = tag,
            });
        }

        // a copy of a model holding only the given triangles (by index, in order) with new material numbers
        static Cmdl Clone(Cmdl m, int[] tris, int[] mats)
        {
            var c = new Cmdl
            {
                Version = m.Version, Flags = m.Flags, MaterialSets = m.MaterialSets, Materials = m.Materials, Positions = m.Positions,
                Normals = m.Normals, Colors = m.Colors, Uv0 = m.Uv0, ShortUvs = m.ShortUvs,
                Triangles = new Cmdl.Corner[tris.Length * 3], TriangleMaterial = mats,
            };
            for (int k = 0; k < tris.Length; k++)
                for (int j = 0; j < 3; j++) c.Triangles[3 * k + j] = m.Triangles[3 * tris[k] + j];
            return c;
        }

        // the body at the pose clip's first frame (skinned; Retro space)
        static double[] Pose(Func<uint, byte[]> res, Spec v, Cmdl m, Cinf skel)
        {
            Ancs a = Ancs.Parse(res(v.Ancs));
            uint animId = 0;
            foreach ((string name, Ancs.MetaAnim meta) in a.Set!.Anims)
            {
                if (name != v.Pose) continue;
                List<Ancs.MetaAnim> ps = Ancs.Prims(meta);
                if (ps.Count == 1) { animId = ps[0].AnimId; break; }
            }
            if (animId == 0) throw new System.IO.InvalidDataException($"veins: no single clip '{v.Pose}' in ANCS {v.Ancs:x8}");
            Retro.Anim anim = Retro.Anim.Load(res(animId), true);
            anim.Sample(0, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> tr);
            bool HasParent(uint b) => skel.Bones[b].Parent != b && skel.Bones.ContainsKey(skel.Bones[b].Parent);
            int Depth(uint b) => HasParent(b) ? 1 + Depth(skel.Bones[b].Parent) : 0;
            var world = new Dictionary<uint, Matrix4d>();
            foreach (uint b in skel.SortedIds().OrderBy(Depth))
            {
                Cinf.Bone bn = skel.Bones[b];
                Vector3d bind = HasParent(b) ? new Vector3d(bn.X - skel.Bones[bn.Parent].X, bn.Y - skel.Bones[bn.Parent].Y, bn.Z - skel.Bones[bn.Parent].Z) : new Vector3d(bn.X, bn.Y, bn.Z);
                Quaterniond q = rot.TryGetValue(b, out double[]? r) ? new Quaterniond(r[0], r[1], r[2], r[3]) : Quaterniond.Identity;
                Vector3d p = tr.TryGetValue(b, out double[]? t) ? new Vector3d(t[0], t[1], t[2]) : bind;
                Matrix4d local = Matrix4d.CreateFromQuaternion(q) * Matrix4d.CreateTranslation(p);
                world[b] = HasParent(b) ? local * world[bn.Parent] : local;
            }
            var outP = new double[m.Positions.Length];
            for (int i = 0; i < m.PositionCount; i++)
            {
                var v4 = new Vector4d(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2], 1);
                Vector3d acc = Vector3d.Zero;
                double ws = 0;
                if (m.Skin != null && i < m.Skin.Count)
                {
                    foreach ((uint b, float w) in m.Skin[i])
                    {
                        if (!world.TryGetValue(b, out Matrix4d mm)) continue;
                        Cinf.Bone bn = skel.Bones[b];
                        acc += w * (v4 * (Matrix4d.CreateTranslation(-bn.X, -bn.Y, -bn.Z) * mm)).Xyz;
                        ws += w;
                    }
                }
                Vector3d pos = ws > 0 ? acc / ws : v4.Xyz;
                outP[3 * i] = pos.X; outP[3 * i + 1] = pos.Y; outP[3 * i + 2] = pos.Z;
            }
            return outP;
        }

        static Vector3d Pos(double[] p, int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
        static IEnumerable<int> Corners(Cmdl m, int t) => new[] { m.Triangles[3 * t].P, m.Triangles[3 * t + 1].P, m.Triangles[3 * t + 2].P };

        // new positions for the triangles' corners (the rest left at zero: unused)
        static double[] Map(double[] posed, Cmdl m, int[] tris, Func<Vector3d, Vector3d> f)
        {
            var r = new double[posed.Length];
            foreach (int p in tris.SelectMany(t => Corners(m, t)).Distinct())
            {
                Vector3d q = f(Pos(posed, p));
                r[3 * p] = q.X; r[3 * p + 1] = q.Y; r[3 * p + 2] = q.Z;
            }
            return r;
        }

        static double Facing(double[] posed, Cmdl m, int t, Vector3d c)
        {
            Vector3d a = Pos(posed, m.Triangles[3 * t].P), b = Pos(posed, m.Triangles[3 * t + 1].P), d = Pos(posed, m.Triangles[3 * t + 2].P);
            Vector3d n = Vector3d.Cross(b - a, d - a), o = (a + b + d) / 3 - c;
            double ln = n.Length, lo = o.Length;
            return ln < 1e-12 || lo < 1e-9 ? 0 : Vector3d.Dot(n / ln, o / lo);
        }

        static Vector3d Centre(float[] p)
        {
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                var q = new Vector3d(p[i], p[i + 1], p[i + 2]);
                lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q);
            }
            return (lo + hi) / 2;
        }

        static (Vector3d, Vector3d) Box(double[] posed, Cmdl m, int[] tris)
        {
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
            foreach (int p in tris.SelectMany(t => Corners(m, t)))
            {
                Vector3d q = Pos(posed, p);
                lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q);
            }
            return (lo, hi);
        }

        static Vector3d PercentileMiddle(double[] posed, Cmdl m, int[] tris)
        {
            int[] ps = tris.SelectMany(t => Corners(m, t)).Distinct().ToArray();
            double Mid(int axis)
            {
                double[] xs = ps.Select(p => posed[3 * p + axis]).OrderBy(x => x).ToArray();
                double Q(double f) => xs[(int)Math.Round(f * (xs.Length - 1))];
                return (Q(0.02) + Q(0.98)) / 2;
            }
            return new Vector3d(Mid(0), Mid(1), Mid(2));
        }

        // connected pieces of triangles sharing positions
        static List<int[]> Components(Cmdl m, int[] tris)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
                while (p != x) { int g = parent[p]; parent[x] = g; x = p; p = g; }
                return x;
            }
            foreach (int t in tris)
            {
                int a = Find(m.Triangles[3 * t].P);
                parent[Find(m.Triangles[3 * t + 1].P)] = a;
                parent[Find(m.Triangles[3 * t + 2].P)] = Find(a);
            }
            return tris.GroupBy(t => Find(m.Triangles[3 * t].P)).Select(g => g.ToArray()).ToList();
        }
    }
}
