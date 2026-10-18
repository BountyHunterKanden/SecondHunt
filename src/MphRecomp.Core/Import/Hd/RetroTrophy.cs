using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Hd
{
    // A Retro character (or a static model: a morph ball) from the user's own Prime 1 / Echoes / Prime 3 disc, turned into
    // an HD model folder the app loads like the Brawl trophies (TrophyRigIO, HdBall, GxJson). Port of prime_to_trophy.py's
    // first pass (brawl_extract/rig_work/session16/scripts; Prime 3 through prime3_char.py's load_chars: Convert3; the
    // --graft branch is not ported). Writes:
    //   <Stem>.dae          the mesh, one <triangles> block per Retro material, Y up and facing +Z like the Brawl
    //                       trophies, UVs exactly Retro's
    //   <texid>.png         every texture the materials name, stored upside down like the Brawl trophies'
    //   <Stem>.gx.json      each material's GX program for the app's GX interpreter
    //   <Stem>.joints.txt   the rig landmarks: Retro's own joints (bind = T-pose) in DS bone names (not for static models)
    //   <Stem>.convert.json the character, the scale and the folder name (+ "static" and the ball's core colour)
    // The parts (main model, joined and attached models) are returned for the skin layout step that follows the rig.
    // Retro's axes (X right, Y forward, Z up) become the trophy's (X left, Y up, Z forward): (x, y, z) -> (-x, z, y).
    public static class RetroTrophy
    {
        // the text files keep the reference converter's line endings (Python on Windows: CRLF), so an import is byte-identical
        // to it and the rig caches, keyed on the .dae's hash, stay valid
        const string Crlf = "\r\n";

        public sealed class Part
        {
            public Cmdl Model = null!;                    // the geometry (Prime 3's merged into Prime 1's shape)
            public Mat[] Mats = Array.Empty<Mat>();       // its materials, per Model.TriangleMaterial index
            public double[] P = Array.Empty<double>();   // xyz per position, trophy space
            public double[] N = Array.Empty<double>();   // xyz per normal
            public string Tag = "mat";
            public string? Owner;                         // the DS bone a joined part rides whole (null: skinned / static)
        }

        // a material as both games give it to the GX conversion: Prime 1 / Echoes' stored TEV program, or Prime 3's texture
        // passes turned into one (Material3Gx); texture ids are Prime 3's 64 bits (Prime 1's widened)
        public sealed class Mat
        {
            public uint Flags;
            public ulong[] TexIds = Array.Empty<ulong>();
            public int[][] Konst = Array.Empty<int[]>();   // rgba 0..255
            public int BlendDst, BlendSrc;
            public uint[] Channels = Array.Empty<uint>();
            public Cmdl.TevStage[] Stages = Array.Empty<Cmdl.TevStage>();
            public Cmdl.TexGen[] TexGens = Array.Empty<Cmdl.TexGen>();
            public Cmdl.UvAnim[] UvAnims = Array.Empty<Cmdl.UvAnim>();

            public static Mat From(Cmdl.Material m) => new()
            {
                Flags = m.Flags, TexIds = m.TexIds.Select(x => (ulong)x).ToArray(), Konst = m.Konst.Select(c => c.Select(x => (int)x).ToArray()).ToArray(),
                BlendDst = m.BlendDst, BlendSrc = m.BlendSrc, Channels = m.Channels, Stages = m.Stages, TexGens = m.TexGens, UvAnims = m.UvAnims,
            };

            public static Mat From(GxMaterial3 m) => new()
            {
                Flags = m.Flags, TexIds = m.TexIds, Konst = m.Konst, BlendDst = m.BlendDst, BlendSrc = m.BlendSrc, Channels = m.Channels,
                Stages = m.Stages, TexGens = m.TexGens, UvAnims = m.UvAnims,
            };
        }

        public sealed class Result
        {
            public readonly List<Part> Parts = new();
            public double Scale;
            public double Feet;   // Prime 4: the kept mesh's lowest point before scaling (source units; BeyondFlourish)
            public int Textures, Materials;
            public Cinf? Skeleton;
            public readonly List<string> Log = new();
        }

        // Prime 1 / Echoes: a character from its ANCS (or a static model by its CMDL id), with the joined and attached models
        public static Result Convert(Func<uint, byte[]> res, HdRecipe h, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);

            Cmdl m;
            Cinf? skel = null;
            if (h.Cmdl is ulong byId)
            {
                m = Cmdl.Load(res, checked((uint)byId), null, h.MatSet);
                Say(0.05f, $"model {byId:x8}: material set {h.MatSet} of {m.MaterialSets}");
            }
            else
            {
                (uint mid, uint sid, uint kid) = Ancs.FindCharacter(res(h.Ancs), h.Char);
                m = Cmdl.Load(res, mid, h.Static ? null : sid, h.MatSet);
                if (h.Model is uint other)
                {
                    Cmdl own = m;
                    m = Cmdl.Load(res, other, null, h.MatSet);
                    m.Skin = SkinFrom(own, m, out int exact);
                    Say(0.05f, $"{h.Char}: model {other:x8} on {mid:x8}'s skin, {exact} of {m.PositionCount} positions exactly on it");
                }
                skel = h.Static ? null : Cinf.Load(res(kid));
                if (h.Static) Say(0.05f, $"{h.Char}: model {mid:x8}, material set {h.MatSet} of {m.MaterialSets}");
            }
            result.Skeleton = skel;
            double s = Scale(m, h);
            result.Scale = s;

            // bind positions by bone name, in the skeleton's own order (a repeated name keeps its last position)
            var bpos0 = new Dictionary<string, double[]>(StringComparer.Ordinal);
            if (skel != null)
            {
                foreach (Cinf.Bone b in skel.Bones.Values) bpos0[b.Name ?? ""] = new[] { b.X, b.Y, b.Z };
            }

            result.Parts.Add(new Part { Model = m, Mats = m.Materials.Select(Mat.From).ToArray(), P = ToTrophy(m.Positions, s, null), N = ToTrophy(m.Normals, 1.0, null), Tag = "mat" });
            for (int k = 0; k < h.Adds.Length; k++)
            {
                HdRecipe.Add a = h.Adds[k];
                Cmdl g = Cmdl.Load(res, a.Cmdl, null, a.MatSet);
                result.Parts.Add(new Part { Model = g, Mats = g.Materials.Select(Mat.From).ToArray(), P = ToTrophy(g.Positions, s, null), N = ToTrophy(g.Normals, 1.0, null), Tag = $"add{k}_mat" });
                Say(0.1f, $"joined {a.Cmdl:x8} (material set {a.MatSet} of {g.MaterialSets}): {g.TriangleCount} triangles");
            }
            for (int k = 0; k < h.Attaches.Length; k++)
            {
                HdRecipe.Attach a = h.Attaches[k];
                Cmdl g = Cmdl.Load(res, a.Cmdl);
                if (!bpos0.TryGetValue(a.Locator, out double[]? at)) throw new InvalidDataException($"{h.Char}: no locator '{a.Locator}' in the skeleton");
                result.Parts.Add(new Part { Model = g, Mats = g.Materials.Select(Mat.From).ToArray(), P = ToTrophy(g.Positions, s, at), N = ToTrophy(g.Normals, 1.0, null), Tag = $"att{k}_mat", Owner = a.Bone });
                Say(0.1f, $"attached {a.Cmdl:x8} at {a.Locator} on {a.Bone}: {g.PositionCount} positions, {g.TriangleCount} triangles");
            }
            ct.ThrowIfCancellationRequested();
            return Finish(id => res(checked((uint)id)), result, h, outDir, Say, ct);
        }

        // Prime 3 (prime3_char.py's load_chars): the CHARs sharing one skeleton (Samus = top + bottom + gun parts) merged
        // into one Prime-1-shaped model -- each CHAR's model, its skin in the model's own vertex order (Skin3, with the
        // neighbour vote at split copies), its materials' texture passes as GX programs (Material3Gx) -- or a static model
        // by its CMDL id (a morph ball)
        public static Result Convert3(Pak3Set res, HdRecipe h, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);

            var m = new Cmdl { Version = 5 };
            var mats = new List<Mat>();
            Cinf? skel = null;
            if (h.Cmdl is ulong byId)
            {
                Cmdl3 m3 = Cmdl3.Load(res.Get(byId), h.MatSet);
                m.Positions = m3.Positions; m.Normals = m3.Normals; m.Uv0 = m3.Uv0;
                m.Triangles = m3.Triangles; m.TriangleMaterial = m3.TriangleMaterial; m.MaterialSets = m3.MaterialSets;
                mats.AddRange(m3.Materials.Select(x => Mat.From(Material3Gx.ToGx(x))));
                Say(0.05f, $"model {byId:x16}: {m3.TriangleCount} triangles");
            }
            else
            {
                var P = new List<float>();
                var N = new List<float>();
                var UV = new List<float>();
                var T = new List<Cmdl.Corner>();
                var TM = new List<int>();
                var skin = new List<(uint Bone, float Weight)[]>();
                ulong? cinf = null;
                int np = 0, nn = 0, nu = 0;
                foreach (ulong cid in h.Chars)
                {
                    (string cname, List<(string Type, ulong Id)> ids) = Char3.CharIds(res.Get(cid), res.TypeOf);
                    ulong? Of(string type) => ids.Where(x => x.Type == type).Select(x => (ulong?)x.Id).FirstOrDefault();
                    ulong cmdl = Of("CMDL") ?? throw new InvalidDataException($"{h.Name}: CHAR {cid:x16} names no model");
                    ulong cskr = Of("CSKR") ?? throw new InvalidDataException($"{h.Name}: CHAR {cid:x16} names no skin");
                    ulong ck = Of("CINF") ?? throw new InvalidDataException($"{h.Name}: CHAR {cid:x16} names no skeleton");
                    cinf ??= ck;
                    if (ck != cinf) throw new InvalidDataException($"{h.Name}: the parts must share one skeleton");
                    Cmdl3 m3 = Cmdl3.Load(res.Get(cmdl));
                    int n = m3.PositionCount;
                    foreach (Cmdl.Corner c in m3.Triangles)
                    {
                        T.Add(new Cmdl.Corner
                        {
                            P = c.P + np, N = c.N + nn,
                            T0 = c.T0 >= 0 ? c.T0 + nu : -1, T1 = c.T1 >= 0 ? c.T1 + nu : -1, T2 = c.T2 >= 0 ? c.T2 + nu : -1, T3 = c.T3 >= 0 ? c.T3 + nu : -1,
                        });
                    }
                    TM.AddRange(m3.TriangleMaterial.Select(x => x + mats.Count));
                    mats.AddRange(m3.Materials.Select(x => Mat.From(Material3Gx.ToGx(x))));
                    P.AddRange(m3.Positions); N.AddRange(m3.Normals); UV.AddRange(m3.Uv0);
                    skin.AddRange(Skin3.Load(res.Get(cskr), n, m3.SkinOrder, m3.TrianglePositions()));
                    np += n; nn += m3.Normals.Length / 3; nu += m3.Uv0.Length / 2;
                    Say(0.05f, $"{cname}: {n} positions, {m3.TriangleCount} triangles, {m3.Materials.Length} materials");
                }
                if (cinf == null) throw new InvalidDataException($"{h.Name}: no CHARs");
                m.Positions = P.ToArray(); m.Normals = N.ToArray(); m.Uv0 = UV.ToArray();
                m.Triangles = T.ToArray(); m.TriangleMaterial = TM.ToArray(); m.Skin = skin;
                skel = Cinf.Load(res.Get(cinf.Value));
            }
            result.Skeleton = skel;
            double s = Scale(m, h);
            result.Scale = s;
            result.Parts.Add(new Part { Model = m, Mats = mats.ToArray(), P = ToTrophy(m.Positions, s, null), N = ToTrophy(m.Normals, 1.0, null), Tag = "mat" });
            ct.ThrowIfCancellationRequested();
            return Finish(res.Get, result, h, outDir, Say, ct);
        }

        // the scale: fixed (a ball keeps its suit's size), else the recipe's height over the model's own (Retro's Z)
        // a recipe's "model": each of its positions takes the skin weights of the nearest skinned position of the character's
        // own model (same skeleton, same bind space); `exact` = how many sit right on one
        static List<(uint Bone, float Weight)[]> SkinFrom(Cmdl own, Cmdl other, out int exact)
        {
            List<(uint Bone, float Weight)[]> src = own.Skin ?? throw new InvalidDataException("the character's own model has no skin");
            int n = Math.Min(own.PositionCount, src.Count);
            var skin = new List<(uint Bone, float Weight)[]>(other.PositionCount);
            exact = 0;
            for (int i = 0; i < other.PositionCount; i++)
            {
                float x = other.Positions[3 * i], y = other.Positions[3 * i + 1], z = other.Positions[3 * i + 2];
                float best = Single.MaxValue;
                int bi = 0;
                for (int j = 0; j < n; j++)
                {
                    float dx = own.Positions[3 * j] - x, dy = own.Positions[3 * j + 1] - y, dz = own.Positions[3 * j + 2] - z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < best) { best = d; bi = j; }
                }
                if (best < 1e-6f) exact++;
                skin.Add(src[bi]);
            }
            return skin;
        }

        static double Scale(Cmdl m, HdRecipe h)
        {
            if (h.Scale is double fixedScale && fixedScale != 0) return fixedScale;
            double zmin = Double.PositiveInfinity, zmax = Double.NegativeInfinity;
            for (int i = 2; i < m.Positions.Length; i += 3)
            {
                double z = m.Positions[i];
                if (z < zmin) zmin = z;
                if (z > zmax) zmax = z;
            }
            return h.Height / (zmax - zmin);
        }

        // both games from here: textures, the .dae, the GX programs, and the joints + convert.json
        static Result Finish(Func<ulong, byte[]> res, Result result, HdRecipe h, string outDir, Action<float, string> Say, CancellationToken ct)
        {
            string name = h.Stem;
            Cmdl m = result.Parts[0].Model;
            double s = result.Scale;

            // textures (upside down, like the Brawl trophies' PNGs)
            var texIds = new SortedSet<ulong>(result.Parts.SelectMany(q => q.Mats).SelectMany(mm => mm.TexIds));
            foreach (ulong t in texIds)
            {
                ct.ThrowIfCancellationRequested();
                byte[] T = res(t);
                byte[]? rgba = Txtr.Decode(T, out uint fmt, out int tw, out int th);
                if (rgba == null)
                {
                    Say(0.3f, $"texture {t:x8}: {(fmt < Txtr.FormatNames.Length ? Txtr.FormatNames[fmt] : fmt.ToString(CultureInfo.InvariantCulture))} not decoded");
                    continue;
                }
                h.Recolor?.Apply(t, rgba);
                PngOut.Write(Path.Combine(outDir, $"{t:x8}.png"), Txtr.FlipRows(rgba, tw, th), tw, th);
                result.Textures++;
            }
            Say(0.4f, $"{result.Textures} textures");

            File.WriteAllText(Path.Combine(outDir, name + ".dae"), Dae(result.Parts, name, texIds).Replace("\n", Crlf));
            ct.ThrowIfCancellationRequested();

            List<GxMat> gx = GxPrograms(result.Parts, name, result.Log);
            BakeRegisterColours(h.Char, gx, result.Log);
            result.Materials = gx.Count;
            var root = new PyJson.Obj
            {
                { "source", $"{h.Char} (Retro, the owner's Prime disc)" },
                { "materials", gx.Select(x => (object?)x.ToJson()).ToList() },
            };
            File.WriteAllText(Path.Combine(outDir, name + ".gx.json"), PyJson.Dump(root, 1, Crlf));
            Say(0.7f, $"{gx.Count} materials");

            if (h.Static)
            {
                var cj = new PyJson.Obj { { "character", h.Char }, { "scale", s }, { "trophy", h.Name }, { "static", true } };
                if (h.Core != null) cj.Add("core", h.Core.Select(c => (object?)(c / 255.0)).ToList());
                File.WriteAllText(Path.Combine(outDir, name + ".convert.json"), Compact(cj));
                Say(1f, $"{name}: static model, {result.Parts.Sum(q => q.Model.TriangleCount)} triangles, {texIds.Count} textures");
                return result;
            }

            File.WriteAllText(Path.Combine(outDir, name + ".joints.txt"), Joints(result.Parts, result.Skeleton!, h, s, out int landmarks).Replace("\n", Crlf));
            var conv = new PyJson.Obj { { "character", h.Char }, { "scale", s }, { "trophy", h.Name } };
            File.WriteAllText(Path.Combine(outDir, name + ".convert.json"), Compact(conv));
            Say(1f, $"{name}: {m.PositionCount} positions, {m.TriangleCount} triangles, {gx.Count} materials, {texIds.Count} textures, "
                + $"{landmarks} landmarks; scale {s.ToString("F3", CultureInfo.InvariantCulture)}");
            return result;
        }

        // json.dump without an indent: {"key": value, ...}, lists as [a, b]
        static string Compact(object? v) => v switch
        {
            PyJson.Obj o => "{" + String.Join(", ", o.Select(kv => PyJson.Dump(kv.Key) + ": " + Compact(kv.Value))) + "}",
            string or bool or null => PyJson.Dump(v),
            System.Collections.IEnumerable e => "[" + String.Join(", ", e.Cast<object?>().Select(Compact)) + "]",
            _ => PyJson.Dump(v),
        };

        // Retro (Z up, facing +Y) -> trophy (Y up, facing +Z): (x, y, z) -> (-x, z, y) * s, after an optional offset
        static double[] ToTrophy(float[] xyz, double s, double[]? offset)
        {
            var r = new double[xyz.Length / 3 * 3];
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                double x = xyz[i], y = xyz[i + 1], z = xyz[i + 2];
                if (offset != null)
                {
                    x += offset[0]; y += offset[1]; z += offset[2];
                }
                r[i] = -x * s; r[i + 1] = z * s; r[i + 2] = y * s;
            }
            return r;
        }

        // ---- DS bone names (prime_to_trophy.py's RMAP / ds())

        static readonly Dictionary<string, string> RMap = new(StringComparer.Ordinal)
        {
            ["Skeleton_Root"] = "Pelvis", ["root"] = "Pelvis", ["L_ball"] = "L_ankle", ["R_ball"] = "R_ankle", ["Collar"] = "Spine_2",
            ["L_collar"] = "Spine_2", ["R_collar"] = "Spine_2", ["Neck_1"] = "Spine_2", ["L_varia_SDK"] = "L_varias2_SDK", ["R_varia_SDK"] = "R_varias2_SDK",
            // Prime 3: a third spine bone (upper chest), and its pads
            ["Spine_3"] = "Spine_2", ["L_shoulder_pad"] = "L_varias2_SDK", ["R_shoulder_pad"] = "R_varias2_SDK",
            ["L_varia_bottom_SDK"] = "L_varias2_SDK", ["R_varia_bottom_SDK"] = "R_varias2_SDK", ["L_varia_Bottom_SDK"] = "L_varias2_SDK", ["R_varia_Bottom_SDK"] = "R_varias2_SDK",
        };
        static readonly Regex HeadParts = new("eye|brow|lid|jaw|mouth|lip|tongue|cheek|hair", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex LeftHand = new("^L_(thumb|index|middle|ring|pinky)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex RightHand = new("^R_(thumb|index|middle|ring|pinky)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Ds(string j)
        {
            if (RMap.TryGetValue(j, out string? d)) return d;
            if (HeadParts.IsMatch(j)) return "Head_1";    // (Zero Suit's ponytail rides the head)
            if (LeftHand.IsMatch(j) || j == "L_wrist_input") return "L_wrist";
            if (RightHand.IsMatch(j) || j is "R_wrist" or "R_wrist_input" || j.EndsWith("LCTR", StringComparison.Ordinal)) return "R_elbow";
            return j;
        }

        // ---- the .dae (Collada 1.4, plain arrays): per material, its triangles with its own positions / normals / UVs

        static string G6(double v)
        {
            // Python's "%.6g"
            string t = v.ToString("G6", CultureInfo.InvariantCulture);
            int e = t.IndexOf('E');
            if (e < 0) return t;
            string exp = t[(e + 1)..];
            char sign = exp[0] == '-' ? '-' : '+';
            string digits = exp.TrimStart('+', '-').TrimStart('0').PadLeft(2, '0');
            return t[..e] + "e" + sign + digits;
        }

        static void Arr(StringBuilder sb, IEnumerable<double> values)
        {
            bool first = true;
            foreach (double v in values)
            {
                if (!first) sb.Append(' ');
                sb.Append(G6(v));
                first = false;
            }
        }

        // np.unique + return_inverse over one column of the corners
        static (int[] Unique, int[] Inverse) Unique(List<int> idx)
        {
            int[] u = idx.Distinct().OrderBy(x => x).ToArray();
            var at = new Dictionary<int, int>(u.Length);
            for (int i = 0; i < u.Length; i++) at[u[i]] = i;
            return (u, idx.Select(x => at[x]).ToArray());
        }

        // a numpy index: negative counts from the end (an untextured corner's uv index -1 reads the last UV)
        static int Wrap(int i, int n) => i < 0 ? i + n : i;

        static string Dae(List<Part> parts, string name, SortedSet<ulong> texIds)
        {
            var geo = new StringBuilder();
            var libMat = new StringBuilder();
            var libFx = new StringBuilder();
            var libImg = new StringBuilder();
            var nodes = new StringBuilder();
            foreach (ulong t in texIds) libImg.Append($"<image id=\"img{t:x8}\" name=\"{t:x8}\"><init_from>{t:x8}.png</init_from></image>");
            for (int pj = 0; pj < parts.Count; pj++)
            {
                Part q = parts[pj];
                Cmdl m = q.Model;
                int nuv = m.Uv0.Length / 2;
                for (int mi = 0; mi < q.Mats.Length; mi++)
                {
                    var sel = new List<int>();
                    for (int t = 0; t < m.TriangleMaterial.Length; t++)
                    {
                        if (m.TriangleMaterial[t] == mi) sel.Add(t);
                    }
                    if (sel.Count == 0) continue;
                    var pc = new List<int>(sel.Count * 3);
                    var nc = new List<int>(sel.Count * 3);
                    var uc = new List<int>(sel.Count * 3);
                    foreach (int t in sel)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            Cmdl.Corner cr = m.Triangles[3 * t + c];
                            pc.Add(cr.P); nc.Add(cr.N); uc.Add(cr.T0);
                        }
                    }
                    (int[] pi, int[] pinv) = Unique(pc);
                    (int[] ni, int[] ninv) = Unique(nc);
                    (int[] ui, int[] uinv) = Unique(uc);
                    string mn = $"{name}_{q.Tag}{mi}", gi = $"{pj}_{mi}";
                    Mat mm = q.Mats[mi];
                    int t0 = mm.Stages.Length > 0 ? mm.Stages[0].Tex : 255;
                    ulong? tbase = t0 < mm.TexIds.Length ? mm.TexIds[t0] : null;   // an untextured material (Light Suit's glow: konst colour)

                    geo.Append($"<geometry id=\"g{gi}\" name=\"{mn}\"><mesh>\n");
                    geo.Append($"<source id=\"g{gi}_pos\"><float_array id=\"g{gi}_pos_a\" count=\"{pi.Length * 3}\">");
                    Arr(geo, pi.SelectMany(i => new[] { q.P[3 * i], q.P[3 * i + 1], q.P[3 * i + 2] }));
                    geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_pos_a\" count=\"{pi.Length}\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>\n");
                    geo.Append($"<source id=\"g{gi}_nrm\"><float_array id=\"g{gi}_nrm_a\" count=\"{ni.Length * 3}\">");
                    Arr(geo, ni.SelectMany(i => new[] { q.N[3 * i], q.N[3 * i + 1], q.N[3 * i + 2] }));
                    geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_nrm_a\" count=\"{ni.Length}\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>\n");
                    geo.Append($"<source id=\"g{gi}_uv\"><float_array id=\"g{gi}_uv_a\" count=\"{ui.Length * 2}\">");
                    Arr(geo, ui.SelectMany(i => new[] { (double)m.Uv0[2 * Wrap(i, nuv)], (double)m.Uv0[2 * Wrap(i, nuv) + 1] }));
                    geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_uv_a\" count=\"{ui.Length}\" stride=\"2\"><param name=\"S\" type=\"float\"/><param name=\"T\" type=\"float\"/></accessor></technique_common></source>\n");
                    geo.Append($"<vertices id=\"g{gi}_v\"><input semantic=\"POSITION\" source=\"#g{gi}_pos\"/></vertices>\n");
                    geo.Append($"<triangles material=\"{mn}\" count=\"{sel.Count}\"><input semantic=\"VERTEX\" source=\"#g{gi}_v\" offset=\"0\"/><input semantic=\"NORMAL\" source=\"#g{gi}_nrm\" offset=\"1\"/><input semantic=\"TEXCOORD\" source=\"#g{gi}_uv\" offset=\"2\" set=\"0\"/><p>");
                    for (int k = 0; k < pinv.Length; k++)
                    {
                        if (k > 0) geo.Append(' ');
                        geo.Append(pinv[k]).Append(' ').Append(ninv[k]).Append(' ').Append(uinv[k]);
                    }
                    geo.Append("</p></triangles>\n</mesh></geometry>");
                    if (tbase is ulong b)
                    {
                        libFx.Append($"<effect id=\"{mn}-fx\"><profile_COMMON><newparam sid=\"s{gi}-surface\"><surface type=\"2D\"><init_from>img{b:x8}</init_from></surface></newparam><newparam sid=\"s{gi}-sampler\"><sampler2D><source>s{gi}-surface</source></sampler2D></newparam><technique sid=\"common\"><lambert><diffuse><texture texture=\"s{gi}-sampler\" texcoord=\"UVSET0\"/></diffuse></lambert></technique></profile_COMMON></effect>");
                    }
                    else
                    {
                        libFx.Append($"<effect id=\"{mn}-fx\"><profile_COMMON><technique sid=\"common\"><lambert><diffuse><color>1 1 1 1</color></diffuse></lambert></technique></profile_COMMON></effect>");
                    }
                    libMat.Append($"<material id=\"{mn}\" name=\"{mn}\"><instance_effect url=\"#{mn}-fx\"/></material>");
                    nodes.Append($"<node id=\"n{gi}\" name=\"{mn}\"><instance_geometry url=\"#g{gi}\"><bind_material><technique_common><instance_material symbol=\"{mn}\" target=\"#{mn}\"/></technique_common></bind_material></instance_geometry></node>");
                }
            }
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n"
                + "<asset><up_axis>Y_UP</up_axis></asset>\n"
                + $"<library_images>{libImg}</library_images>\n"
                + $"<library_effects>{libFx}</library_effects>\n"
                + $"<library_materials>{libMat}</library_materials>\n"
                + $"<library_geometries>{geo}</library_geometries>\n"
                + $"<library_visual_scenes><visual_scene id=\"scene\">{nodes}</visual_scene></library_visual_scenes>\n"
                + "<scene><instance_visual_scene url=\"#scene\"/></scene>\n"
                + "</COLLADA>\n";
        }

        // ---- the GX programs (see GxJson.cs): Retro's material, field for field, in the app's GX terms

        sealed class GxStage
        {
            public int[] C = new int[4], A = new int[4];
            public int Cop, Cbias, Cscale, Cclamp, Cdest, Aop, Abias, Ascale, Aclamp, Adest, TexMap, TexCoord, Ras, Kc, Ka;
        }

        sealed class GxLayer
        {
            public string Name = "";
            public int TexMap, TexCoord;
            public bool Env;
            public double[] Scroll = { 0.0, 0.0 };
            public double[] Mtx = { 1, 0, 0, 0, 1, 0 };
        }

        sealed class GxMat
        {
            public string Name = "";
            public List<GxStage> Stages = new();
            public double[][] Konst = Array.Empty<double[]>();
            public bool[] KonstPad = Array.Empty<bool>();   // a padding slot: written as ints, like the reference
            public bool Lit, Blend, AlphaTest;
            public List<GxLayer> Layers = new();
            public int BlendSrc, BlendDst;

            public PyJson.Obj ToJson() => new()
            {
                { "name", Name },
                {
                    "stages", Stages.Select(st => (object?)new PyJson.Obj
                    {
                        { "c", st.C }, { "cop", st.Cop }, { "cbias", st.Cbias }, { "cscale", st.Cscale }, { "cclamp", st.Cclamp }, { "cdest", st.Cdest },
                        { "a", st.A }, { "aop", st.Aop }, { "abias", st.Abias }, { "ascale", st.Ascale }, { "aclamp", st.Aclamp }, { "adest", st.Adest },
                        { "texmap", st.TexMap }, { "texcoord", st.TexCoord }, { "ras", st.Ras }, { "kc", st.Kc }, { "ka", st.Ka },
                    }).ToList()
                },
                { "konst", Konst.Select((k, i) => (object?)k.Select(x => KonstPad[i] ? (object?)(int)x : x).ToList()).ToList() },
                { "lit", Lit },
                {
                    "layers", Layers.Select(l => (object?)new PyJson.Obj
                    {
                        { "name", l.Name }, { "texmap", l.TexMap }, { "texcoord", l.TexCoord }, { "env", l.Env },
                        { "scroll", l.Scroll.Select(x => (object?)x).ToList() }, { "mtx", l.Mtx.Select(x => (object?)x).ToList() },
                    }).ToList()
                },
                { "blend", Blend }, { "blendSrc", BlendSrc }, { "blendDst", BlendDst }, { "depthWrite", !Blend },
                { "alphaTest", AlphaTest }, { "cull", 2 },
            };
        }

        static List<GxMat> GxPrograms(List<Part> parts, string name, List<string> log)
        {
            var gx = new List<GxMat>();
            foreach (Part q in parts)
            {
                Cmdl m = q.Model;
                int nuv = m.Uv0.Length / 2;
                for (int mi = 0; mi < q.Mats.Length; mi++)
                {
                    if (Array.IndexOf(m.TriangleMaterial, mi) < 0) continue;
                    Mat mm = q.Mats[mi];
                    var k = new List<double[]>();
                    foreach (int[] c in mm.Konst) k.Add(c.Select(x => x / 255.0).ToArray());
                    for (int i = 0; i < 4; i++) k.Add(new double[] { 0, 0, 0, 0 });
                    var g = new GxMat { Name = $"{name}_{q.Tag}{mi}", Konst = k.Take(4).ToArray(), KonstPad = Enumerable.Range(0, 4).Select(i => i >= mm.Konst.Length).ToArray() };
                    foreach (Cmdl.TevStage st in mm.Stages)
                    {
                        g.Stages.Add(new GxStage
                        {
                            C = (int[])st.ColorIn.Clone(), Cop = st.ColorOp.Op, Cbias = st.ColorOp.Bias, Cscale = st.ColorOp.Scale, Cclamp = st.ColorOp.Clamp, Cdest = st.ColorOp.Out,
                            A = (int[])st.AlphaIn.Clone(), Aop = st.AlphaOp.Op, Abias = st.AlphaOp.Bias, Ascale = st.AlphaOp.Scale, Aclamp = st.AlphaOp.Clamp, Adest = st.AlphaOp.Out,
                            TexMap = st.Tex == 255 ? -1 : st.Tex, TexCoord = st.TexCoord, Ras = st.Ras == 4 ? 0 : st.Ras == 5 ? 1 : 7, Kc = st.KColor, Ka = st.KAlpha,
                        });
                    }
                    for (int ti = 0; ti < mm.TexIds.Length; ti++)
                    {
                        // the texcoord generator of the stage sampling this texture: its source (UV / normal) and UV animation
                        List<int> tcs = mm.Stages.Where(st => st.Tex == ti).Select(st => st.TexCoord).ToList();
                        int src = 4, mtx = 30;
                        if (tcs.Count > 0 && tcs[0] < mm.TexGens.Length)
                        {
                            src = mm.TexGens[tcs[0]].Src; mtx = mm.TexGens[tcs[0]].Mtx;
                        }
                        bool env = src is 0 or 1;                          // position / normal: a reflection map
                        double[] scroll = { 0.0, 0.0 };
                        Cmdl.UvAnim? anim = null;
                        if (mtx != 30 && mtx / 3 < mm.UvAnims.Length) anim = mm.UvAnims[mtx / 3];
                        if (anim != null && anim.Mode == 2) scroll = new[] { anim.Params[2], -anim.Params[3] };   // UV scroll
                        if (anim != null && anim.Mode is 0 or 1) env = true;  // inverse modelview: the camera-space normal map
                        // the layer's UVs: Retro keeps up to 4 sets, all uv0 or an affine copy of it (Phazon's vein strips:
                        // uv1); the copy is the layer's static matrix in the app's UV space (s, t) = (u, 1 - v)
                        double[] lm = { 1, 0, 0, 0, 1, 0 };
                        if (!env && tcs.Count > 0 && src > 4 && src <= 7)
                        {
                            int k2 = src - 4;
                            var u0 = new List<(double U, double V)>();
                            var uk = new List<(double U, double V)>();
                            for (int t = 0; t < m.TriangleMaterial.Length; t++)
                            {
                                if (m.TriangleMaterial[t] != mi) continue;
                                for (int c = 0; c < 3; c++)
                                {
                                    Cmdl.Corner cr = m.Triangles[3 * t + c];
                                    int ik = k2 == 1 ? cr.T1 : k2 == 2 ? cr.T2 : cr.T3;
                                    if (ik < 0) continue;
                                    int i0 = Wrap(cr.T0, nuv);
                                    u0.Add((m.Uv0[2 * i0], m.Uv0[2 * i0 + 1]));
                                    uk.Add((m.Uv0[2 * ik], m.Uv0[2 * ik + 1]));
                                }
                            }
                            double worst = 0;
                            for (int i = 0; i < u0.Count; i++) worst = Math.Max(worst, Math.Max(Math.Abs(u0[i].U - uk[i].U), Math.Abs(u0[i].V - uk[i].V)));
                            if (u0.Count > 0 && worst > 1e-6)
                            {
                                // uk = [u v 1] @ X (least squares, 3 x 2)
                                double[,] X = LeastSquares(u0, uk, out double miss);
                                double a_ = X[0, 0], b_ = X[1, 0], c_ = X[2, 0], d_ = X[0, 1], e_ = X[1, 1], f_ = X[2, 1];
                                lm = new[] { a_, -b_, b_ + c_, -d_, e_, 1 - e_ - f_ };
                                log.Add($"{name}_{q.Tag}{mi} layer {ti}: uv{k2} = affine copy of uv0 (worst miss {miss.ToString("F3", CultureInfo.InvariantCulture)})");
                            }
                        }
                        if (tcs.Count > 0 && tcs[0] != ti) throw new InvalidDataException($"{name}_{q.Tag}{mi}: texmap != texcoord (the app's GX batches assume they match)");
                        g.Layers.Add(new GxLayer { Name = $"{mm.TexIds[ti]:x8}", TexMap = ti, TexCoord = tcs.Count > 0 ? tcs[0] : ti, Env = env, Scroll = scroll, Mtx = lm });
                    }
                    g.BlendSrc = mm.BlendSrc; g.BlendDst = mm.BlendDst;
                    g.Blend = !(mm.BlendSrc == 1 && mm.BlendDst == 0);
                    g.Lit = mm.Channels.Length > 0 && (mm.Channels[0] & 1) != 0;
                    g.AlphaTest = (mm.Flags & 0x20) != 0;
                    gx.Add(g);
                }
            }
            return gx;
        }

        // [u v 1] @ X ~= uk, X 3 x 2, by the normal equations (A is n x 3, well conditioned: UVs in [0, 1] plus a constant)
        static double[,] LeastSquares(List<(double U, double V)> u0, List<(double U, double V)> uk, out double worstMiss)
        {
            var ata = new double[3, 3];
            var atb = new double[3, 2];
            for (int i = 0; i < u0.Count; i++)
            {
                double[] a = { u0[i].U, u0[i].V, 1 };
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++) ata[r, c] += a[r] * a[c];
                    atb[r, 0] += a[r] * uk[i].U;
                    atb[r, 1] += a[r] * uk[i].V;
                }
            }
            var X = new double[3, 2];
            for (int col = 0; col < 2; col++)
            {
                // Gaussian elimination with partial pivoting on a copy
                var M = new double[3, 4];
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++) M[r, c] = ata[r, c];
                    M[r, 3] = atb[r, col];
                }
                for (int p = 0; p < 3; p++)
                {
                    int best = p;
                    for (int r = p + 1; r < 3; r++) if (Math.Abs(M[r, p]) > Math.Abs(M[best, p])) best = r;
                    if (best != p) for (int c = 0; c < 4; c++) (M[p, c], M[best, c]) = (M[best, c], M[p, c]);
                    if (M[p, p] == 0) throw new InvalidDataException("uv copy: singular fit");
                    for (int r = p + 1; r < 3; r++)
                    {
                        double f = M[r, p] / M[p, p];
                        for (int c = p; c < 4; c++) M[r, c] -= f * M[p, c];
                    }
                }
                for (int r = 2; r >= 0; r--)
                {
                    double v = M[r, 3];
                    for (int c = r + 1; c < 3; c++) v -= M[r, c] * X[c, col];
                    X[r, col] = v / M[r, r];
                }
            }
            worstMiss = 0;
            for (int i = 0; i < u0.Count; i++)
            {
                double pu = u0[i].U * X[0, 0] + u0[i].V * X[1, 0] + X[2, 0];
                double pv = u0[i].U * X[0, 1] + u0[i].V * X[1, 1] + X[2, 1];
                worstMiss = Math.Max(worstMiss, Math.Max(Math.Abs(pu - uk[i].U), Math.Abs(pv - uk[i].V)));
            }
            return X;
        }

        // Colours Retro's game code puts in a GX register at draw time, in no file (gx_regs.py): a stage reading a register
        // no earlier stage of its material writes would read 0 in the app, so the colour is baked into a free konst slot.
        // Prime 1's Power Trooper glow reads register 1, which the game sets from the trooper's vulnerability colour.
        static readonly Dictionary<string, int[]> Reg1Colour = new(StringComparer.Ordinal) { ["power_trooper"] = new[] { 253, 239, 86 } };

        static void BakeRegisterColours(string character, List<GxMat> materials, List<string> log)
        {
            const int C1 = 4, A1 = 5, KONST = 14, OutReg1 = 2, KcselK = 12;
            Reg1Colour.TryGetValue(character, out int[]? rgb);
            foreach (GxMat m in materials)
            {
                bool written = false;
                for (int si = 0; si < m.Stages.Count; si++)
                {
                    GxStage st = m.Stages[si];
                    if (!written && st.C.Contains(A1)) log.Add($"{m.Name} stage {si}: reads register 1's ALPHA as a colour -- not handled, reads 0");
                    if (!written && st.C.Contains(C1))
                    {
                        if (rgb == null)
                        {
                            log.Add($"{m.Name} stage {si}: reads GX register 1, set by the game's code; no colour known for '{character}' -> reads 0");
                        }
                        else
                        {
                            var used = new HashSet<int>(m.Stages.Where(x => x.C.Contains(KONST)).Select(x => x.Kc));
                            int slot = -1;
                            for (int kk = 0; kk < 4; kk++)
                            {
                                if (!m.Konst[kk].Any(x => x != 0) && !used.Contains(KcselK + kk))
                                {
                                    slot = kk;
                                    break;
                                }
                            }
                            if (slot < 0)
                            {
                                log.Add($"{m.Name} stage {si}: no free konst slot for the register colour -> reads 0");
                            }
                            else if (st.C.Contains(KONST) && st.Kc != KcselK + slot)
                            {
                                log.Add($"{m.Name} stage {si}: already reads another konst colour -> register left at 0");
                            }
                            else
                            {
                                m.Konst[slot] = new[] { rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0, 1.0 };
                                m.KonstPad[slot] = false;
                                st.C = st.C.Select(c => c == C1 ? KONST : c).ToArray();
                                st.Kc = KcselK + slot;
                                log.Add($"{m.Name} stage {si}: game-set register 1 colour ({rgb[0]}, {rgb[1]}, {rgb[2]}) -> konst {slot}");
                            }
                        }
                    }
                    if (st.Cdest == OutReg1) written = true;
                }
            }
        }

        // ---- the landmarks: Retro's joints (bind = its T-pose) in DS names, part centres from the skin

        static readonly (string Ds, string Retro)[] Landmarks =
        {
            ("Skeleton_Root", "Skeleton_Root"), ("Pelvis", "Pelvis"), ("Spine_1", "Spine_1"), ("Spine_2", "Spine_2"), ("Head_1", "Head_1"),
            ("L_hip", "L_hip"), ("L_knee", "L_knee"), ("L_ankle", "L_ankle"), ("R_hip", "R_hip"), ("R_knee", "R_knee"), ("R_ankle", "R_ankle"),
            ("L_shoulder", "L_shoulder"), ("L_elbow", "L_elbow"), ("L_wrist", "L_wrist"), ("R_shoulder", "R_shoulder"), ("R_elbow", "R_elbow"),
            ("L_varias2_SDK", "L_varia_SDK"), ("R_varias2_SDK", "R_varia_SDK"),
        };

        static string Joints(List<Part> parts, Cinf skel, HdRecipe h, double s, out int count)
        {
            Part main = parts[0];
            Cmdl m = main.Model;
            double[] P = main.P;
            int np0 = m.PositionCount;
            var bpos = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (uint b in skel.Bones.Keys.OrderBy(x => x)) bpos[skel.Bones[b].Name ?? ""] = new[] { skel.Bones[b].X, skel.Bones[b].Y, skel.Bones[b].Z };
            var J = new List<(string Key, double[] V)>();
            void Set(string key, double[] v)
            {
                int i = J.FindIndex(x => x.Key == key);
                if (i >= 0) J[i] = (key, v);
                else J.Add((key, v));
            }
            bool Has(string key) => J.Any(x => x.Key == key);
            double[] Get(string key) => J.First(x => x.Key == key).V;
            foreach ((string dn, string rn) in Landmarks)
            {
                if (bpos.TryGetValue(rn, out double[]? v)) Set(dn, new[] { -v[0] * s, v[2] * s, v[1] * s });
            }
            if (!Has("Pelvis") && Has("Skeleton_Root")) Set("Pelvis", Get("Skeleton_Root"));
            // each position's dominant bone (the first of the heaviest), in DS names
            var dom = new string[np0];
            List<(uint Bone, float Weight)[]> skin = m.Skin ?? throw new InvalidDataException($"{h.Char}: the model has no skin");
            for (int i = 0; i < np0; i++)
            {
                if (i >= skin.Count || skin[i].Length == 0)
                {
                    dom[i] = "?";
                    continue;
                }
                (uint Bone, float Weight) best = skin[i][0];
                foreach ((uint Bone, float Weight) bw in skin[i]) if (bw.Weight > best.Weight) best = bw;
                dom[i] = skel.Bones.TryGetValue(best.Bone, out Cinf.Bone? bb) ? Ds(bb.Name ?? "") : "?";
            }
            foreach (string leaf in new[] { "Head_1", "L_ankle", "R_ankle", "L_wrist", "R_elbow" })
            {
                // an attached cannon is part of its forearm
                double sx = 0, sy = 0, sz = 0;
                int n = 0;
                for (int i = 0; i < np0; i++)
                {
                    if (dom[i] != leaf) continue;
                    sx += P[3 * i]; sy += P[3 * i + 1]; sz += P[3 * i + 2]; n++;
                }
                foreach (Part q in parts.Skip(1))
                {
                    if (q.Owner != leaf) continue;
                    for (int i = 0; i + 2 < q.P.Length; i += 3)
                    {
                        sx += q.P[i]; sy += q.P[i + 1]; sz += q.P[i + 2]; n++;
                    }
                }
                if (n > 10) Set(leaf + ".center", new[] { sx / n, sy / n, sz / n });
            }
            // the head faces straight ahead (+Z) and up (+Y) in Retro's bind (TrophyRigger "<bone>.fwd" / ".up"): aiming it at
            // its centre instead is unstable, Retro's head pivot sits mid-helmet
            if (Has("Head_1"))
            {
                double[] hj = Get("Head_1");
                Set("Head_1.fwd", new[] { hj[0] + 0, hj[1] + 0, hj[2] + 0.1 * h.Height });
                Set("Head_1.up", new[] { hj[0] + 0, hj[1] + 0.1 * h.Height, hj[2] + 0 });
            }
            var sb = new StringBuilder();
            sb.Append($"# {h.Stem}: Retro's own joints of {h.Char} (bind pose = T-pose), from the owner's Prime disc (prime_to_trophy.py)\n");
            foreach ((string key, double[] v) in J)
            {
                sb.Append(key.PadRight(24)).Append(' ')
                    .Append(v[0].ToString("F4", CultureInfo.InvariantCulture).PadLeft(10)).Append(' ')
                    .Append(v[1].ToString("F4", CultureInfo.InvariantCulture).PadLeft(10)).Append(' ')
                    .Append(v[2].ToString("F4", CultureInfo.InvariantCulture).PadLeft(10)).Append('\n');
            }
            // a part of the MPH hunter's own model held in the hand (HdRecipe "hold"): seated at the middle of its anchor bones
            foreach (HdRecipe.Hold hd in h.Holds)
            {
                double ax = 0, ay = 0, az = 0;
                foreach (string an in hd.Anchor)
                {
                    if (!bpos.TryGetValue(an, out double[]? v)) throw new InvalidDataException($"{h.Char}: no bone '{an}' to hold {hd.Bone}'s part at");
                    ax += -v[0] * s; ay += v[2] * s; az += v[1] * s;
                }
                int k = hd.Anchor.Length;
                sb.Append("attach ").Append(hd.Bone).Append(' ').Append(hd.Cut.ToString("F4", CultureInfo.InvariantCulture))
                    .Append(' ').Append((ax / k).ToString("F4", CultureInfo.InvariantCulture))
                    .Append(' ').Append((ay / k).ToString("F4", CultureInfo.InvariantCulture))
                    .Append(' ').Append((az / k).ToString("F4", CultureInfo.InvariantCulture))
                    .Append(' ').Append(hd.Scale.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
            }
            count = J.Count;
            return sb.ToString();
        }
    }
}
