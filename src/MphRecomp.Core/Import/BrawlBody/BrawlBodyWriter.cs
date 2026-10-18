using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenTK.Mathematics;

namespace MphRecomp.Import.BrawlBody
{
    // A Brawl fighter on HER OWN skeleton (docs/ZERO_SUIT_SAMUS.md section 8; owner 2026-10-15: Brawl Zero Suit Samus on her
    // own rig, her Brawl run and idle, vanilla Samus's strafes / backwards / aiming), written in the native bodies' format so
    // NativeBody.Load reads it unchanged (Import/Bodies/BodyImporter's layout, the "MPHGUN2" variant: full inverse binds,
    // since a Brawl bind carries rotations). Into one folder bodies/<Name>/:
    //   body.bin    skeleton + per-material skinned vertices (GxShader layout, palettes of at most 32 bones) + the clips
    //               sampled per Brawl frame (60 a second)
    //   body.cfg    height / feet / root / game / speed lines (NativeBody) + "material <index> <name>" lines: each written
    //               material's MDL0 material, whose GX program the app reads from body.brres (Mdl0Gx)
    // Brawl's model space is already MPH's (Y up, +Z the fighter's front, +X her left), so nothing is converted. A bone's
    // local matrix is S * Rx * Ry * Rz * T (row vectors; rotations in degrees, X first). Root motion is taken out: the
    // bones above the pelvis (TopN, TransN) stay on their bind; the pelvis keeps its own sway and bob.
    public sealed class BrawlBodySource
    {
        public string[] Names = Array.Empty<string>();
        public int[] Parent = Array.Empty<int>();
        public Vector3[] Scale = Array.Empty<Vector3>(), Rotation = Array.Empty<Vector3>(), Translation = Array.Empty<Vector3>();
        public readonly List<BrawlBodyPart> Parts = new();
        public readonly List<BrawlBodyClip> Clips = new();
        public string Pelvis = "HipN";
        public string[] Pinned = { "TopN", "TransN" };
        public string[] Feet = { "LFootJ", "RFootJ" };
        public string Source = "";

        public int Bone(string name) => Array.IndexOf(Names, name);
    }

    // one material's triangles (3 corners each); positions and normals in the bind pose's model space
    public sealed class BrawlBodyPart
    {
        public string Material = "";
        public readonly List<BrawlBodyCorner> Corners = new();
    }

    public struct BrawlBodyCorner
    {
        public Vector3 Position, Normal;
        public Vector2 Uv;          // GX's (t down)
        public Vector4 Color;
        public (int Bone, float Weight)[] Skin;
    }

    // a clip's every frame: per frame per bone its local scale, rotation (degrees) and translation; Has[bone] = the clip
    // keys that bone (the others stay on their bind)
    public sealed class BrawlBodyClip
    {
        public string Name = "";
        public int Frames;
        public bool Loop;
        public Vector3[,] S = new Vector3[0, 0], R = new Vector3[0, 0], T = new Vector3[0, 0];
        public bool[] Has = Array.Empty<bool>();
        // a clip made as whole local matrices instead of S/R/T keys (one retargeted from another skeleton, PC tools):
        // [frame, bone], every bone (Has unused)
        public Matrix4[,]? Locals;
    }

    public static class BrawlBodyWriter
    {
        public const float Fps = 60f;

        public static Matrix4 Local(Vector3 s, Vector3 rDeg, Vector3 t) =>
            Matrix4.CreateScale(s) * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(rDeg.X))
            * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(rDeg.Y)) * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rDeg.Z))
            * Matrix4.CreateTranslation(t);

        // parents before children
        public static int[] Order(int[] parent)
        {
            var order = new List<int>();
            var placed = new bool[parent.Length];
            while (order.Count < parent.Length)
            {
                for (int i = 0; i < parent.Length; i++)
                {
                    if (!placed[i] && (parent[i] < 0 || placed[parent[i]])) { placed[i] = true; order.Add(i); }
                }
            }
            return order.ToArray();
        }

        public static Matrix4[] BindWorld(BrawlBodySource src)
        {
            var w = new Matrix4[src.Names.Length];
            foreach (int i in Order(src.Parent))
            {
                Matrix4 l = Local(src.Scale[i], src.Rotation[i], src.Translation[i]);
                w[i] = src.Parent[i] >= 0 ? l * w[src.Parent[i]] : l;
            }
            return w;
        }

        // one clip frame's locals (pins and unkeyed bones on their bind)
        public static void FrameLocals(BrawlBodySource src, BrawlBodyClip c, int f, Matrix4[] local)
        {
            var pinned = new HashSet<int>(src.Pinned.Select(src.Bone).Where(b => b >= 0));
            for (int i = 0; i < src.Names.Length; i++)
            {
                if (c.Locals != null)
                {
                    local[i] = pinned.Contains(i) ? Local(src.Scale[i], src.Rotation[i], src.Translation[i]) : c.Locals[f, i];
                    continue;
                }
                local[i] = c.Has[i] && !pinned.Contains(i)
                    ? Local(c.S[f, i], c.R[f, i], c.T[f, i])
                    : Local(src.Scale[i], src.Rotation[i], src.Translation[i]);
            }
        }

        public static void World(int[] parent, int[] order, Matrix4[] local, Matrix4[] world)
        {
            foreach (int i in order) world[i] = parent[i] >= 0 ? local[i] * world[parent[i]] : local[i];
        }

        public sealed class Result
        {
            public float Height, Feet;
            public readonly Dictionary<string, float> Speeds = new();
            public int Materials, Vertices;
        }

        public static Result Write(BrawlBodySource src, string outDir, IEnumerable<string>? extraCfg = null)
        {
            Directory.CreateDirectory(outDir);
            int n = src.Names.Length;
            int[] order = Order(src.Parent);
            Matrix4[] bind = BindWorld(src);
            var res = new Result();
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (BrawlBodyPart p in src.Parts)
            {
                foreach (BrawlBodyCorner c in p.Corners) { lo = Math.Min(lo, c.Position.Y); hi = Math.Max(hi, c.Position.Y); }
            }
            res.Height = hi - lo;
            res.Feet = lo;

            using var w = new BinaryWriter(File.Create(Path.Combine(outDir, "body.bin")));
            w.Write(Encoding.ASCII.GetBytes("MPHGUN2\0"));
            w.Write(n);
            for (int i = 0; i < n; i++)
            {
                w.Write(src.Parent[i]);
                Matrix4 inv = bind[i].Inverted();
                foreach (Vector4 row in new[] { inv.Row0, inv.Row1, inv.Row2, inv.Row3 })
                {
                    w.Write(row.X); w.Write(row.Y); w.Write(row.Z); w.Write(row.W);
                }
                byte[] nm = Encoding.Latin1.GetBytes(src.Names[i]);
                w.Write((byte)nm.Length);
                w.Write(nm);
            }

            // materials: a part whose corners use more than 32 bones is written as several, split by triangle
            var written = new List<(string Material, List<BrawlBodyCorner> Corners)>();
            foreach (BrawlBodyPart p in src.Parts)
            {
                var bones = new HashSet<int>();
                var chunk = new List<BrawlBodyCorner>();
                for (int c = 0; c + 2 < p.Corners.Count; c += 3)
                {
                    var tri = new HashSet<int>(p.Corners.Skip(c).Take(3).SelectMany(k => k.Skin.Select(s => s.Bone)));
                    if (chunk.Count > 0 && bones.Union(tri).Count() > 32)
                    {
                        written.Add((p.Material, chunk));
                        chunk = new List<BrawlBodyCorner>();
                        bones.Clear();
                    }
                    bones.UnionWith(tri);
                    chunk.AddRange(p.Corners.Skip(c).Take(3));
                }
                if (chunk.Count > 0) written.Add((p.Material, chunk));
            }
            w.Write(written.Count);
            foreach ((string _, List<BrawlBodyCorner> corners) in written)
            {
                List<int> palette = corners.SelectMany(c => c.Skin.Select(s => s.Bone)).Distinct().OrderBy(b => b).ToList();
                if (palette.Count > 32) throw new InvalidDataException($"a triangle uses {palette.Count} bones");
                var slot = new Dictionary<int, int>();
                for (int k = 0; k < palette.Count; k++) slot[palette[k]] = k;
                w.Write(palette.Count);
                foreach (int b in palette) w.Write(b);
                w.Write(corners.Count);
                foreach (BrawlBodyCorner c in corners)
                {
                    w.Write(c.Position.X); w.Write(c.Position.Y); w.Write(c.Position.Z);
                    w.Write(c.Normal.X); w.Write(c.Normal.Y); w.Write(c.Normal.Z);
                    w.Write(c.Uv.X); w.Write(c.Uv.Y);
                    w.Write(Math.Clamp(c.Color.X, 0, 1)); w.Write(Math.Clamp(c.Color.Y, 0, 1));
                    w.Write(MathF.Round(Math.Clamp(c.Color.Z, 0f, 1f) * 255f) + Math.Clamp(c.Color.W, 0f, 1f) * 0.999f);
                    var skin = c.Skin.OrderByDescending(s => s.Weight).Take(4).ToArray();
                    float sum = skin.Sum(s => s.Weight);
                    for (int k = 0; k < 4; k++) w.Write(k < skin.Length ? (float)slot[skin[k].Bone] : 0f);
                    for (int k = 0; k < 4; k++) w.Write(k < skin.Length && sum > 0 ? skin[k].Weight / sum : 0f);
                    res.Vertices++;
                }
            }
            res.Materials = written.Count;

            // clips: a loop whose last frame is its first keeps its frames; another loop gets its first frame again at
            // the end, so the wrap is seamless
            w.Write(src.Clips.Count);
            var local = new Matrix4[n];
            var world = new Matrix4[n];
            foreach (BrawlBodyClip c in src.Clips)
            {
                bool closed = !c.Loop || Same(c, 0, c.Frames - 1);
                int keys = closed ? c.Frames : c.Frames + 1;
                byte[] nm = Encoding.Latin1.GetBytes(c.Name);
                w.Write((byte)nm.Length);
                w.Write(nm);
                w.Write((keys - 1) / Fps);
                w.Write(1 / Fps);
                w.Write(keys);
                for (int k = 0; k < keys; k++)
                {
                    FrameLocals(src, c, k % c.Frames, local);
                    for (int i = 0; i < n; i++)
                    {
                        Quaternion q = local[i].ExtractRotation();
                        Vector3 t = local[i].ExtractTranslation();
                        w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
                        w.Write(t.X); w.Write(t.Y); w.Write(t.Z);
                        w.Write(1f); w.Write(1f); w.Write(1f);
                    }
                }
                if (Speed(src, c, order) is float v && v > 0) res.Speeds[c.Name] = v;
            }

            var cfg = new List<string>
            {
                "height " + F(res.Height), "feet " + F(res.Feet), "root " + src.Pelvis, "game ssbb",
            };
            foreach ((string k, float v) in res.Speeds) cfg.Add($"speed {k} {F(v)}");
            for (int m = 0; m < written.Count; m++) cfg.Add($"material {m} {written[m].Material}");
            if (extraCfg != null) cfg.AddRange(extraCfg);
            File.WriteAllLines(Path.Combine(outDir, "body.cfg"), cfg);
            return res;
        }

        static bool Same(BrawlBodyClip c, int a, int b)
        {
            if (c.Locals != null)
            {
                for (int i = 0; i < c.Locals.GetLength(1); i++)
                {
                    Matrix4 d = c.Locals[a, i] - c.Locals[b, i];
                    if (d.Row0.Length + d.Row1.Length + d.Row2.Length > 1e-3f || d.Row3.Xyz.Length > 1e-4f) return false;
                }
                return true;
            }
            for (int i = 0; i < c.Has.Length; i++)
            {
                if (!c.Has[i]) continue;
                if ((c.R[a, i] - c.R[b, i]).Length > 0.01f || (c.T[a, i] - c.T[b, i]).Length > 1e-4f) return false;
            }
            return true;
        }

        // a stepping clip's own travel speed (model units a second): how fast the planted foot (the lower one) slides back
        // under the body, averaged over the frames it stays planted; null for a clip whose feet don't step
        public static float? Speed(BrawlBodySource src, BrawlBodyClip c, int[] order)
        {
            int lf = src.Bone(src.Feet[0]), rf = src.Bone(src.Feet[1]);
            if (lf < 0 || rf < 0 || c.Frames < 3) return null;
            int n = src.Names.Length;
            var local = new Matrix4[n];
            var world = new Matrix4[n];
            var feet = new (Vector3 L, Vector3 R)[c.Frames];
            for (int f = 0; f < c.Frames; f++)
            {
                FrameLocals(src, c, f, local);
                World(src.Parent, order, local, world);
                feet[f] = (world[lf].ExtractTranslation(), world[rf].ExtractTranslation());
            }
            double dist = 0;
            int frames = 0;
            for (int f = 1; f < c.Frames; f++)
            {
                bool left = feet[f].L.Y < feet[f].R.Y;
                bool leftBefore = feet[f - 1].L.Y < feet[f - 1].R.Y;
                if (left != leftBefore) continue;
                Vector3 a = left ? feet[f - 1].L : feet[f - 1].R, b = left ? feet[f].L : feet[f].R;
                dist += new Vector2(b.X - a.X, b.Z - a.Z).Length;
                frames++;
            }
            if (frames == 0) return null;
            float v = (float)(dist / frames * Fps);
            return v > 0.5f ? v : null;
        }

        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
