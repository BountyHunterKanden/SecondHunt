using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Hd
{
    // A Metroid Prime 4: Beyond character from the user's own RomFS, turned into an HD model folder the app loads like the
    // Retro imports (RetroTrophy writes the same set). Port of mp4_to_trophy.py,
    // our own converter; its model, materials and textures come from Core's clean-room Beyond readers (Import/Beyond,
    // docs/cleanroom/G_beyond_spec.md) through a Source, in the numbers the reference converter read. Writes:
    //   <Stem>.dae           the mesh, one <triangles> block per material, Y up and facing +Z, UVs as the game's
    //   <guid>_*.png         Beyond's PBR maps baked into the app's GX look (colour, normal map, metal reflections, glow)
    //   env_studio_*.png     the two sphere maps the metal reflects
    //   <Stem>.gx.json       each material's GX program
    //   <Stem>.joints.txt    the rig landmarks: Beyond's joints (bind = A-pose) folded onto the DS bones, + the recipe's marks
    //   <Stem>.convert.json  the character, the scale and the folder name
    // Prime 4's rig is far richer than the DS one (500 joints: fingers, twist bones, the Shock Coil's slides, Lockjaw): each
    // joint is folded onto the DS bone it moves with (Map); joints of things MPH's hunter doesn't have (Lockjaw, VFX) are
    // dropped with the mesh they carry. The result's single part carries that DS skin for the layout step (RetroLayout).
    // The arithmetic follows the reference's numpy types (single precision for the texture bakes, double for positions), so
    // an import matches the reference converter's files.
    public static class BeyondTrophy
    {
        const string Crlf = "\r\n";

        // one mesh as the reader gives it: per vertex xyz / xyz / uv (/ uv), 4 joints (indices into Source.SkinNames) and
        // 4 weights; triangles as vertex indices. The name carries the material slot ("..._MatID<n>")
        public sealed class Mesh
        {
            public string Name = "";
            public float[] P = Array.Empty<float>(), N = Array.Empty<float>(), Uv0 = Array.Empty<float>();
            public float[]? Uv1;
            public int[] Joints = Array.Empty<int>();
            public float[] Weights = Array.Empty<float>();
            public int[] Indices = Array.Empty<int>();
        }

        // a material slot: its instance's textures by type (BCLR, METL, NMAP, ICAN: the uv set and the texture id) and colours
        public sealed class Material
        {
            public string Mati = "";
            public readonly Dictionary<string, (int Uv, string Id)> Tex = new(StringComparer.Ordinal);
            public readonly Dictionary<string, double[]> Col = new(StringComparer.Ordinal);
        }

        public sealed class Source
        {
            public List<Mesh> Meshes = new();
            public List<Material> Mats = new();
            public string[] SkinNames = Array.Empty<string>();                              // per skin joint index
            public Dictionary<string, string?> Parent = new(StringComparer.Ordinal);         // joint -> parent joint
            public Dictionary<string, double[]> JointPos = new(StringComparer.Ordinal);      // bind-pose joint positions (Y up)
            public Func<string, (byte[] Rgba, int W, int H)?> Texture = _ => null;            // a texture's top mip, RGBA8

            // the SHA-1 of every asset the Source read (FromGame), the recipe's "assetsSha1": the files' revision check, so the
            // multi-GB archive holding them needn't be hashed whole
            public string AssetsSha1 = "";

            // the Source from the game's own archives (clean-room readers): the model's top-level meshes, its material slots'
            // instances, the character file's skeleton (bind pose: a skinned joint's own bind matrix, else the rest pose).
            // AssetsSha1 hashes, in this order, each asset's bytes then its side data: the model, the character file, the
            // material instances in slot order, then the textures in the order the materials first name them
            public static Source FromGame(IReadOnlyList<BeyondArchive> paks, Guid smdl, Guid chpr)
            {
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
                (byte[] Asset, byte[] Side) Take(Guid id, string what)
                {
                    BeyondArchive a = BeyondPakModelSource.Holder(paks, id, what);
                    BeyondArchive.Entry e = a.GetEntry(id);
                    byte[] asset = a.ReadAsset(e), side = e.HasSideData ? a.ReadSideData(e) : Array.Empty<byte>();
                    hash.AppendData(asset);
                    hash.AppendData(side);
                    return (asset, side);
                }
                Take(smdl, "model");
                Take(chpr, "character file");
                var model = new BeyondPakModelSource(paks, smdl, chpr);
                var src = new Source { SkinNames = model.Skin.Names };
                foreach (BeyondModelMesh m in model.ModelMeshes)
                {
                    src.Meshes.Add(new Mesh
                    {
                        Name = m.Name, P = m.Positions, N = m.Normals, Uv0 = m.Uv0 ?? new float[2 * m.VertexCount], Uv1 = m.Uv1,
                        Joints = m.Joints, Weights = m.Weights, Indices = m.Indices,
                    });
                }
                foreach (Guid id in model.Model.MaterialSlots)
                {
                    var mi = BeyondMaterialInstance.Parse(Take(id, "material").Asset, $"MATI {id}");
                    var mat = new Material { Mati = $"MATI/{id}.mati" };
                    foreach (BeyondMaterialInstance.Parameter q in mi.Parameters)
                    {
                        if (q.IsTexture) mat.Tex[q.Tag] = (q.UvSet, q.Texture.ToString());
                        // a colour as the converter reads it: the single-precision value's shortest decimal
                        else if (q.Format is 3 or 4 or 10 or 12)
                        {
                            mat.Col[q.Tag] = q.Values.Take(3).Select(f => Double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).ToArray();
                        }
                    }
                    src.Mats.Add(mat);
                }
                var c = Chpr.Parse(BeyondPakModelSource.Holder(paks, chpr, "character file").ReadAsset(chpr));
                var sk = ChprSkeleton.Build(c);
                for (int i = 0; i < sk.Names.Length; i++)
                {
                    string nm = sk.Names[i];
                    if (src.JointPos.ContainsKey(nm)) continue;
                    src.Parent[nm] = sk.Parent[i] >= 0 && sk.Parent[i] < sk.Names.Length ? sk.Names[sk.Parent[i]] : null;
                    double[] w = sk.BindWorld.TryGetValue(i, out double[]? bw) ? bw : sk.RestWorld[i];
                    src.JointPos[nm] = new double[] { (float)w[3], (float)w[7], (float)w[11] };
                }
                // every texture read (and hashed) now, decoded when the converter asks for it
                var raw = new Dictionary<string, (byte[] Asset, byte[] Side)>(StringComparer.Ordinal);
                foreach (Material m in src.Mats)
                {
                    foreach ((_, string id) in m.Tex.Values)
                    {
                        if (!raw.ContainsKey(id) && paks.Any(p => p.Contains(Guid.Parse(id)))) raw[id] = Take(Guid.Parse(id), "texture");
                    }
                }
                src.AssetsSha1 = System.Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                var cache = new Dictionary<string, (byte[], int, int)?>(StringComparer.Ordinal);
                src.Texture = id =>
                {
                    if (cache.TryGetValue(id, out var hit)) return hit;
                    (byte[], int, int)? r = null;
                    if (raw.TryGetValue(id, out var b))
                    {
                        var t = BeyondTexture.Parse(b.Asset, b.Side, $"TXTR {id}");
                        // only a flat picture counts (an array of slices, as the Legacy suit's, is no map: the reference
                        // found no <id>.png for it and went without)
                        if (t.Shape == 1) r = (t.DecodeRgba(), t.Width, t.Height);
                    }
                    return cache[id] = r;
                };
                return src;
            }
        }

        // the look (mp4_to_trophy.py's constants; a recipe may pin older ones: "look")
        public sealed class Look
        {
            public double Darken = 0.55, Spec = 2.4, Sat = 1.35, MSat = 1.15, Glint = 1.8, Glow = 1.8, Gleam = 0.3, Ao = 1.0;

            // the recipe's "look": {"spec": 1.5, ...}
            public static Look From(IReadOnlyDictionary<string, double> d)
            {
                var l = new Look();
                foreach ((string k, double v) in d)
                {
                    switch (k)
                    {
                    case "darken": l.Darken = v; break;
                    case "spec": l.Spec = v; break;
                    case "sat": l.Sat = v; break;
                    case "msat": l.MSat = v; break;
                    case "glint": l.Glint = v; break;
                    case "glow": l.Glow = v; break;
                    case "gleam": l.Gleam = v; break;
                    case "ao": l.Ao = v; break;
                    default: throw new InvalidDataException($"look: unknown constant \"{k}\"");
                    }
                }
                return l;
            }
        }

        internal static readonly string[] Ds =
        {
            "Pelvis", "Spine_1", "Spine_2", "Head_1", "L_hip", "L_knee", "L_ankle", "R_hip", "R_knee", "R_ankle", "L_shoulder", "L_elbow",
            "L_wrist", "R_shoulder", "R_elbow", "L_varias2_SDK", "R_varias2_SDK",
        };

        // ---- Prime 4 joint -> DS bone (null = not on the DS hunter: dropped with its mesh); "$1" = the first group
        static readonly (Regex Pat, string? To)[] Map = BuildMap();

        static (Regex, string?)[] BuildMap()
        {
            var l = new List<(string, string?)>
            {
                (@"^(lockjaw|VFX|ParticleState|ParticleSignal|SoundState|SoundSignal|UserState|Portal|root\.|cinematic|s1_|visor_|viola_visor)", null),
                (@"_vis$", null),
                (@"^(Skeleton_Root|root)$", "Pelvis"), (@"^(hip_C0|pelvis_bind|pelvis_C0)", "Pelvis"),
                // Samus (default / psychic / viola / legacy suits): *_l_bind / *_r_bind limbs, four spine joints, a face rig in the helmet
                (@"^spine_0[12]_bind", "Spine_1"), (@"^spine_0[34]_bind", "Spine_2"), (@"^clavicle_[lr]_bind", "Spine_2"),
            };
            foreach (char side in "LR")
            {
                string s = Char.ToLowerInvariant(side).ToString(), S = side.ToString();
                l.Add(($"^(hip_{s}_bind|outer_thigh_plate_{S}0)", $"{S}_hip"));
                l.Add(($"^(knee_{s}_bind|knee_guard_{S}0)", $"{S}_knee"));
                l.Add(($"^(ankle_{s}_bind|ball_{s}_bind|toe_{s}_end|ankle_side_spur_{S}0|ankle_guard_{S}0)", $"{S}_ankle"));
                l.Add(($"^(varia_{s}_bind|legacy_pauldron_{S}0|viola_{S}0)", $"{S}_varias2_SDK"));
                l.Add(($"^(shoulder_{s}_bind|arm_{S}0_upper_twist)", $"{S}_shoulder"));
            }
            l.AddRange(new (string, string?)[]
            {
                (@"^(elbow_l_bind|arm_L0_forearm_twist)", "L_elbow"), (@"^(wrist_l_bind|meta_L0|(pinky|index|ring|middle|thumb)_L0|grip_jnt)", "L_wrist"),
                (@"^(elbow_r_bind|wrist_r_bind|gun_|hydraulic_R0)", "R_elbow"),
                // Space Pirates: reverse-jointed ("dogleg") legs, a gun in the right forearm, a blade on the left
                (@"^dogleg_(L|R)0_(hip|thigh)_jnt", "$1_hip"), (@"^dogleg_(L|R)0_knee_jnt", "$1_knee"), (@"^dogleg_(L|R)0_(ankle|toe)_jnt", "$1_ankle"),
                (@"^collar_(L|R)0_shoulder_jnt", "Spine_2"),
                // Sylux
                (@"^leg_and_foot_(L|R)0_hip_jnt", "$1_hip"), (@"^leg_and_foot_(L|R)0_knee", "$1_knee"), (@"^kneepad_(L|R)0", "$1_knee"),
                (@"^leg_and_foot_(L|R)0_(ankle|toe|toeTip)_jnt", "$1_ankle"),
                (@"^spine_C0_(hip|0)_jnt", "Spine_1"), (@"^spine_C0_[12]_jnt", "Spine_2"), (@"^neck_C0_head_jnt", "Head_1"), (@"^neck_C0_[0-9]_jnt", "Spine_2"),
                (@"^shoulder_(L|R)0_shoulder_jnt", "Spine_2"), (@"^pauldron_(L|R)0", "$1_varias2_SDK"), (@"^arm_(L|R)0_upper_jnt", "$1_shoulder"),
                (@"^arm_L0_forearm", "L_elbow"), (@"^(arm_L0_wrist|wrist_offset_L|meta_L|finger_L|thumb_L)", "L_wrist"),
                (@"^(arm_R0_forearm|arm_R0_wrist|wrist_offset_R|meta_R|finger_R|thumb_R|shock|GunFX)", "R_elbow"),
            });
            return l.Select(x => (new Regex(x.Item1, RegexOptions.CultureInvariant), x.Item2)).ToArray();
        }

        // the DS bone j is named for itself (no walk up to a parent, no Spine_2 default; null = none)
        internal static string? OwnDsName(string j)
        {
            foreach ((Regex pat, string? to) in Map)
            {
                Match m = pat.Match(j);
                if (m.Success) return to == null ? null : to.Contains('$') ? m.Result(to) : to;
            }
            return null;
        }

        internal static string? DsName(string j, Dictionary<string, string?> parent)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(j))
            {
                foreach ((Regex pat, string? to) in Map)
                {
                    Match m = pat.Match(j);
                    if (m.Success) return to == null ? null : to.Contains('$') ? m.Result(to) : to;
                }
                if (!parent.TryGetValue(j, out string? p) || String.IsNullOrEmpty(p)) break;
                j = p;
            }
            return "Spine_2";
        }

        // alsoMats: materials whose look is written though no kept triangle uses them (BeyondBody's Lockjaw part)
        public static RetroTrophy.Result Convert(Source src, HdRecipe h, Look look, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default, IEnumerable<int>? alsoMats = null)
        {
            var result = new RetroTrophy.Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);
            string name = h.Stem, label = String.IsNullOrEmpty(h.Char) ? name : h.Char;
            double H = h.Height;

            // ---- geometry: every mesh; drop what rides a dropped joint
            string?[] jds = src.SkinNames.Select(n => DsName(n, src.Parent)).ToArray();
            int nd = Ds.Length;
            var P = new List<float>(); var N = new List<float>(); var Uv0 = new List<float>(); var Uv1 = new List<float>();
            var SB = new List<int>(); var SW = new List<float>(); var Tri = new List<int>(); var Tm = new List<int>();
            int nv = 0, dropped = 0;
            var matOf = new Regex(@"MatID([0-9]+)", RegexOptions.CultureInvariant);
            int MatId(Mesh m) => Int32.Parse(matOf.Match(m.Name).Groups[1].Value, CultureInfo.InvariantCulture);
            // some models carry two sets of meshes ("..._LOD0_1_MatIDn" and "..._LOD0_MatIDn"): the "_LOD0_" set wins, the
            // other keeps only the materials the fuller one lacks (compared by the material instance itself)
            bool twoSets = src.Meshes.Any(m => m.Name.Contains("_LOD0_Mat", StringComparison.Ordinal))
                && src.Meshes.Any(m => m.Name.Contains("_LOD0_1_", StringComparison.Ordinal));
            var fullMats = twoSets
                ? src.Meshes.Where(m => !m.Name.Contains("_LOD0_1_", StringComparison.Ordinal)).Select(m => src.Mats[MatId(m)].Mati).ToHashSet()
                : new HashSet<string>();
            var wds = new float[nd + 1];
            var order = new int[nd];
            var neg = new float[nd];
            foreach (Mesh mesh in src.Meshes)
            {
                if (twoSets && mesh.Name.Contains("_LOD0_1_", StringComparison.Ordinal) && fullMats.Contains(src.Mats[MatId(mesh)].Mati)) continue;
                int mid = MatId(mesh), count = mesh.P.Length / 3;
                float[] u1 = mesh.Uv1 ?? mesh.Uv0;
                var keep = new bool[count];
                for (int v = 0; v < count; v++)
                {
                    // the vertex's weights folded onto the DS bones (+ the dropped joints' share); mostly dropped = dropped
                    Array.Clear(wds);
                    for (int k = 0; k < 4; k++)
                    {
                        string? d = jds[mesh.Joints[4 * v + k]];
                        wds[d != null ? Array.IndexOf(Ds, d) : nd] += mesh.Weights[4 * v + k];
                    }
                    keep[v] = h.Static || PairwiseSum17(wds) > wds[nd];
                    // the 4 strongest DS bones (numpy's argsort of the negated weights: its tie order decides 50/50 vertices), normalised
                    for (int k = 0; k < nd; k++) neg[k] = -wds[k];
                    NumpyArgsort(neg, nd, order);
                    float sum = 0f;
                    for (int k = 0; k < 4; k++) sum += wds[order[k]];
                    sum = Math.Max(sum, 1e-9f);
                    for (int k = 0; k < 4; k++)
                    {
                        float w = wds[order[k]] / sum;
                        SW.Add(w);
                        SB.Add(w > 0 ? order[k] : -1);
                    }
                }
                for (int i = 0; i < 3 * count; i++)
                {
                    P.Add(mesh.P[i]);
                    N.Add(mesh.N[i]);
                }
                for (int i = 0; i < 2 * count; i++)
                {
                    Uv0.Add(mesh.Uv0[i]);
                    Uv1.Add(u1[i]);
                }
                for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                    if (!(keep[a] && keep[b] && keep[c]))
                    {
                        dropped++;
                        continue;
                    }
                    Tri.Add(a + nv); Tri.Add(b + nv); Tri.Add(c + nv);
                    Tm.Add(mid);
                }
                nv += count;
            }
            // only the vertices kept triangles use, in their order
            var remap = new int[nv];
            Array.Fill(remap, -1);
            foreach (int v in Tri) remap[v] = 0;
            int nu = 0;
            var used = new List<int>();
            for (int v = 0; v < nv; v++)
            {
                if (remap[v] < 0) continue;
                remap[v] = nu++;
                used.Add(v);
            }
            for (int i = 0; i < Tri.Count; i++) Tri[i] = remap[Tri[i]];
            float[] p = Pick(P, used, 3), n = Pick(N, used, 3), uv0 = Pick(Uv0, used, 2), uv1 = Pick(Uv1, used, 2), sw = Pick(SW, used, 4);
            int[] sb = used.SelectMany(v => Enumerable.Range(0, 4).Select(k => SB[4 * v + k])).ToArray();
            int[] tri = Tri.ToArray(), tm = Tm.ToArray();
            float feet = Single.PositiveInfinity, top = Single.NegativeInfinity;
            for (int i = 1; i < p.Length; i += 3)
            {
                feet = Math.Min(feet, p[i]);
                top = Math.Max(top, p[i]);
            }
            double s = h.Scale ?? H / (double)(top - feet);
            var pt = new double[p.Length];   // trophy space: Y up, faces +Z, left at +X (as the game's)
            for (int i = 0; i < p.Length; i += 3)
            {
                pt[i] = p[i] * s;
                pt[i + 1] = ((double)p[i + 1] - feet) * s;
                pt[i + 2] = p[i + 2] * s;
            }
            result.Scale = s;
            result.Feet = feet;
            Say(0.2f, $"{name}: {used.Count} vertices, {tri.Length / 3} triangles kept ({dropped} dropped with Lockjaw / VFX joints), scale {s.ToString("F3", CultureInfo.InvariantCulture)}");
            ct.ThrowIfCancellationRequested();

            // ---- materials: Beyond's PBR maps baked into GX programs + textures
            var gx = new List<PyJson.Obj>();
            var texCache = new Dictionary<string, (byte[] Rgba, int W, int H)?>(StringComparer.Ordinal);
            (byte[] Rgba, int W, int H)? Tex(string id)
            {
                if (!texCache.TryGetValue(id, out var t)) texCache[id] = t = src.Texture(id);
                return t;
            }
            bool envWritten = false;
            int written = 0;
            int[] mids = tm.Concat(alsoMats ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            foreach (int mid in mids)
            {
                ct.ThrowIfCancellationRequested();
                Material mm = src.Mats[mid];
                var layers = new List<PyJson.Obj>();
                var stages = new List<(int[] C, int[] A, int Texmap, int Texcoord, int Ras, int Cdest, int Kc)>();
                int Layer(string fname, bool env = false)
                {
                    layers.Add(new PyJson.Obj
                    {
                        { "name", fname }, { "texmap", layers.Count }, { "texcoord", layers.Count }, { "env", env },
                        { "scroll", new List<object?> { 0.0, 0.0 } }, { "mtx", new List<object?> { 1, 0, 0, 0, 1, 0 } },
                    });
                    return layers.Count - 1;
                }
                void Save(byte[] rgb, int w, int hh, string fname)
                {
                    PngOut.Write(Path.Combine(outDir, fname + ".png"), FlipRgb(rgb, w, hh), w, hh);
                    written++;
                }
                bool hasBase = mm.Tex.TryGetValue("BCLR", out var bas);
                bool hasGlow = mm.Tex.TryGetValue("ICAN", out var glow);
                bool hasMetl = mm.Tex.TryGetValue("METL", out var metl);
                bool hasNmap = mm.Tex.TryGetValue("NMAP", out var nmap);
                var bc = hasBase ? Tex(bas.Id) : null;
                if (bc is (byte[] B, int bw, int bh))
                {
                    int np = bw * bh;
                    byte[]? M = hasMetl ? Sized(Tex(metl.Id), bw, bh, metl.Id) : null;
                    byte[]? Nm = hasNmap ? Sized(Tex(nmap.Id), bw, bh, nmap.Id) : null;
                    // a two-channel normal map comes as grey + alpha (x = red, y = alpha)
                    bool two = false;
                    if (Nm != null)
                    {
                        int same = 0;
                        for (int i = 0; i < np; i++) if (Math.Abs(Nm[4 * i] / 255f - Nm[4 * i + 1] / 255f) < 1e-3f) same++;
                        two = (double)same / np > 0.95;
                    }
                    float metalMax = 0f;
                    if (M != null) for (int i = 0; i < np; i++) metalMax = Math.Max(metalMax, M[4 * i + 2] / 255f);
                    bool shiny = M != null && metalMax > 0.05;
                    float fSat = (float)look.Sat, fSatD = (float)(look.Sat - look.MSat), fDark = (float)look.Darken, fSpec = (float)look.Spec, fGlint = (float)look.Glint;
                    var diff = new byte[3 * np];
                    var nrm = Nm != null ? new byte[3 * np] : null;
                    var spec = shiny ? new byte[3 * np] : null;
                    var glint = shiny ? new byte[3 * np] : null;
                    double metalSum = 0, glossSum = 0;
                    for (int i = 0; i < np; i++)
                    {
                        float r = B[4 * i] / 255f, g = B[4 * i + 1] / 255f, b = B[4 * i + 2] / 255f;
                        float metal = M != null ? M[4 * i + 2] / 255f : 0f, rough = M != null ? M[4 * i + 1] / 255f : 1f;
                        float occl = M != null ? Pow(M[4 * i] / 255f, look.Ao) : 1f;
                        float shade = occl;
                        if (Nm != null)
                        {
                            float nx = -(Nm[4 * i] / 255f * 2 - 1), ny = (two ? Nm[4 * i + 3] : Nm[4 * i + 1]) / 255f * 2 - 1;
                            float nz = MathF.Sqrt(Math.Clamp(1 - nx * nx - ny * ny, 0f, 1f));
                            shade = occl * (0.85f + 0.15f * Pow(nz, 3));   // a little cavity: the light is per pixel now
                            nrm![3 * i] = U8(nx * 0.5f + 0.5f); nrm[3 * i + 1] = U8(ny * 0.5f + 0.5f); nrm[3 * i + 2] = U8(nz * 0.5f + 0.5f);
                        }
                        float sat = fSat - fSatD * metal;   // paint saturated, metal as it is
                        float grey = (r + g + b) / 3f;
                        float sr = Math.Clamp(grey + (r - grey) * sat, 0f, 1f), sg = Math.Clamp(grey + (g - grey) * sat, 0f, 1f), sb2 = Math.Clamp(grey + (b - grey) * sat, 0f, 1f);
                        float k = (1 - fDark * metal) * shade;
                        if (M == null)
                        {
                            // no METL: the reference's metal and occlusion are float64 arrays (np.zeros / np.ones), so its
                            // saturation and shading run in double (the float32 colours and cavity promoted)
                            double dsat = look.Sat, dg = grey;
                            diff[3 * i] = U8D(Math.Clamp(dg + (double)(r - grey) * dsat, 0, 1) * shade);
                            diff[3 * i + 1] = U8D(Math.Clamp(dg + (double)(g - grey) * dsat, 0, 1) * shade);
                            diff[3 * i + 2] = U8D(Math.Clamp(dg + (double)(b - grey) * dsat, 0, 1) * shade);
                        }
                        else
                        {
                            diff[3 * i] = U8(sr * k); diff[3 * i + 1] = U8(sg * k); diff[3 * i + 2] = U8(sb2 * k);
                        }
                        if (shiny)
                        {
                            float gloss = Pow(Math.Clamp(1 - rough, 0f, 1f), 1.5), cs = Math.Clamp(shade, 0f, 1f);
                            float tk = metal * gloss * fSpec * cs;   // metal reflects in its own colour
                            spec![3 * i] = U8(sr * tk); spec[3 * i + 1] = U8(sg * tk); spec[3 * i + 2] = U8(sb2 * tk);
                            byte gl = U8(metal * Pow(gloss, 1.5) * fGlint * cs);
                            glint![3 * i] = gl; glint[3 * i + 1] = gl; glint[3 * i + 2] = gl;
                            metalSum += metal;
                            glossSum += gloss;
                        }
                    }
                    Save(diff, bw, bh, bas.Id + "_diffuse");
                    int t = Layer(bas.Id + "_diffuse");
                    if (nrm != null)
                    {
                        Save(nrm, bw, bh, nmap.Id + "_nrm");
                        layers[Layer(nmap.Id + "_nrm")].Add("nmap", true);
                    }
                    stages.Add((new[] { 15, 8, 10, 15 }, new[] { 7, 7, 7, 6 }, t, t, 0, 0, 12));                // colour x lighting
                    if (shiny)
                    {
                        if (!envWritten && !File.Exists(Path.Combine(outDir, "env_studio_room.png")))
                        {
                            (byte[] room, byte[] hot) = StudioSpheres(256);
                            PngOut.Write(Path.Combine(outDir, "env_studio_room.png"), Rgba(room), 256, 256);
                            PngOut.Write(Path.Combine(outDir, "env_studio_hot.png"), Rgba(hot), 256, 256);
                        }
                        envWritten = true;
                        int te = Layer("env_studio_room", env: true);
                        stages.Add((new[] { 15, 15, 15, 8 }, new[] { 7, 7, 7, 0 }, te, te, 7, 2, 12));              // REG1 = room
                        Save(spec!, bw, bh, bas.Id + "_spec");
                        int ts = Layer(bas.Id + "_spec");
                        stages.Add((new[] { 15, 8, 4, 0 }, new[] { 7, 7, 7, 0 }, ts, ts, 7, 0, 12));                // + tint x REG1
                        int th = Layer("env_studio_hot", env: true);
                        stages.Add((new[] { 15, 15, 15, 8 }, new[] { 7, 7, 7, 0 }, th, th, 7, 3, 12));              // REG2 = glints
                        Save(glint!, bw, bh, bas.Id + "_glint");
                        int tg = Layer(bas.Id + "_glint");
                        stages.Add((new[] { 15, 8, 6, 0 }, new[] { 7, 7, 7, 0 }, tg, tg, 7, 0, 12));                // + glint mask x REG2
                        Say(0.3f, $"  mat{mid}: metal {(metalSum / np * 100).ToString("F0", CultureInfo.InvariantCulture)}% of the texture, "
                            + $"gloss {(glossSum / np).ToString("F2", CultureInfo.InvariantCulture)} -> studio reflections + glints");
                    }
                }
                else
                {
                    stages.Add((new[] { 15, 15, 15, 10 }, new[] { 7, 7, 7, 6 }, -1, 0, 0, 0, 12));
                }
                var gtex = hasGlow ? Tex(glow.Id) : null;
                if (gtex is (byte[] G0, int gw0, int gh0))
                {
                    byte[] G = G0;
                    int gw = gw0, gh = gh0;
                    string fname = glow.Id;
                    // a glow read through uv1 is a sheet of energy ramps (Sylux's thin strip): it shows only where the colour
                    // texture's alpha marks the lights
                    bool ramp = (Math.Min(gw, gh) <= 16 || glow.Uv == 1) && hasBase;
                    var sel = Enumerable.Range(0, tm.Length).Where(x => tm[x] == mid).ToArray();
                    if (glow.Uv == 1 && !Affine(sel, tri, uv0, uv1))
                    {
                        int size = ramp ? (bc?.W ?? throw new InvalidDataException($"{name}: mat{mid}: the glow's ramp needs the colour texture {bas.Id}")) : Math.Max(gw, gh);
                        (G, double cover) = Rebake(G0, gw0, gh0, sel, tri, uv0, uv1, size);
                        gw = gh = size;
                        fname = glow.Id + "_uv0";
                        Say(0.35f, $"  mat{mid}: glow {glow.Id} re-baked from uv1 onto uv0 ({(cover * 100).ToString("F0", CultureInfo.InvariantCulture)}% of the texture covered)");
                    }
                    if (ramp)
                    {
                        // the ramp only colours the strips: WHERE they are is the colour texture's alpha
                        byte[] A = Sized(bc, gw, gh, bas.Id) ?? throw new InvalidDataException($"{name}: mat{mid}: no colour texture for the glow ramp");
                        int np = gw * gh, lit = 0;
                        for (int i = 0; i < np; i++) if (A[4 * i + 3] / 255f > 0.25f) lit++;
                        double litShare = (double)lit / np;
                        float fGlow = (float)look.Glow, gleam = (float)look.Gleam;
                        var outp = new byte[3 * np];
                        for (int i = 0; i < np; i++)
                        {
                            float r = G[4 * i] / 255f, g = G[4 * i + 1] / 255f, b = G[4 * i + 2] / 255f;
                            float grey = (r + g + b) / 3f, mask = Math.Clamp(A[4 * i + 3] / 255f * 1.6f, 0f, 1f);
                            for (int c = 0; c < 3; c++)
                            {
                                float x = c == 0 ? r : c == 1 ? g : b;
                                float v = Math.Clamp(grey + (x - grey) * 1.8f, 0f, 1f) * mask * fGlow;
                                if (litShare > 0.15) v *= gleam;   // a glow over whole panels is a gleam, not a light
                                outp[3 * i + c] = U8(v);
                            }
                        }
                        fname = glow.Id + "_masked";
                        Save(outp, gw, gh, fname);
                        Say(0.4f, $"  mat{mid}: glow ramp masked by the colour texture's alpha ({(litShare * 100).ToString("F1", CultureInfo.InvariantCulture)}% lit"
                            + (litShare > 0.15 ? ", a subtle gleam" : "") + ")");
                    }
                    else
                    {
                        PngOut.Write(Path.Combine(outDir, fname + ".png"), FlipRows(G, gw, gh, 4), gw, gh);
                        written++;
                    }
                    int tl = Layer(fname);
                    object?[] ic = GlowHue(mm);
                    bool white = ic.All(x => System.Convert.ToDouble(x, CultureInfo.InvariantCulture) == 1.0);
                    stages.Add((new[] { 15, 8, white ? 12 : 14, 0 }, new[] { 7, 7, 7, 0 }, tl, tl, 7, 0, 13));   // + glow (x ICNC)
                }
                var hue = GlowHue(mm);
                var konst = new List<object?>
                {
                    new List<object?> { 1, 1, 1, 1 }, new List<object?> { hue[0], hue[1], hue[2], 1 },
                    new List<object?> { 0, 0, 0, 0 }, new List<object?> { 0, 0, 0, 0 },
                };
                var full = stages.Select(st => (object?)new PyJson.Obj
                {
                    { "c", st.C.Cast<object?>().ToList() }, { "cop", 0 }, { "cbias", 0 }, { "cscale", 0 }, { "cclamp", 1 }, { "cdest", st.Cdest },
                    { "a", st.A.Cast<object?>().ToList() }, { "aop", 0 }, { "abias", 0 }, { "ascale", 0 }, { "aclamp", 1 }, { "adest", 0 },
                    { "texmap", st.Texmap }, { "texcoord", st.Texcoord }, { "ras", st.Ras }, { "kc", st.Kc }, { "ka", 0x1C },
                }).ToList();
                gx.Add(new PyJson.Obj
                {
                    { "name", $"{name}_mat{mid}" }, { "stages", full }, { "konst", konst }, { "lit", true }, { "layers", layers.Cast<object?>().ToList() },
                    { "blend", false }, { "blendSrc", 1 }, { "blendDst", 0 }, { "depthWrite", true }, { "alphaTest", false }, { "cull", 2 },
                });
            }
            var root = new PyJson.Obj
            {
                { "source", $"{label} (Retro, Metroid Prime 4: Beyond -- the owner's own dump)" },
                { "materials", gx.Cast<object?>().ToList() },
            };
            File.WriteAllText(Path.Combine(outDir, name + ".gx.json"), PyJson.Dump(root, 1, Crlf));
            result.Materials = gx.Count;
            result.Textures = written;
            Say(0.5f, $"{gx.Count} materials, {written} textures");
            ct.ThrowIfCancellationRequested();

            // ---- the .dae: one geometry per material, per corner
            File.WriteAllText(Path.Combine(outDir, name + ".dae"), Dae(gx, mids, tm, tri, pt, n, uv0).Replace("\n", Crlf));
            ct.ThrowIfCancellationRequested();

            string folder = h.Name;
            if (h.Static)
            {
                var cj = new PyJson.Obj { { "character", label }, { "scale", s }, { "trophy", folder }, { "static", true } };
                if (h.Core != null) cj.Add("core", h.Core.Select(c => (object?)(c / 255.0)).ToList());
                File.WriteAllText(Path.Combine(outDir, name + ".convert.json"), Compact(cj));
                Say(1f, $"{name}: static model, {tri.Length / 3} triangles, {gx.Count} materials");
                result.Parts.Add(new RetroTrophy.Part { Model = new Cmdl(), P = pt, N = n.Select(x => (double)x).ToArray() });
                return result;
            }

            // ---- landmarks: Beyond's joints folded onto the DS skeleton (bind = Beyond's A-pose)
            double[] T(double[] v) => new[] { v[0] * s, (v[1] - feet) * s, v[2] * s };
            string? First(params string[] js) => js.FirstOrDefault(j => src.JointPos.ContainsKey(j));
            string rootJ = First("hip_C0_0_jnt", "pelvis_bind", "pelvis_C0_0_jnt") ?? throw new InvalidDataException($"{name}: no root joint");
            var jt = new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                ["Skeleton_Root"] = T(src.JointPos[rootJ]), ["Pelvis"] = T(src.JointPos[rootJ]), ["Spine_1"] = T(src.JointPos[rootJ]),
            };
            var picks = new List<(string Ds, string[] Js)> { ("Spine_2", new[] { "spine_C0_1_jnt", "spine_03_bind" }), ("Head_1", new[] { "neck_C0_head_jnt" }) };
            foreach ((string S, string sl) in new[] { ("L", "l"), ("R", "r") })
            {
                picks.Add(($"{S}_hip", new[] { $"leg_and_foot_{S}0_hip_jnt", $"hip_{sl}_bind", $"dogleg_{S}0_hip_jnt" }));
                picks.Add(($"{S}_knee", new[] { $"leg_and_foot_{S}0_knee_jnt", $"knee_{sl}_bind", $"dogleg_{S}0_knee_jnt" }));
                picks.Add(($"{S}_ankle", new[] { $"leg_and_foot_{S}0_ankle_jnt", $"ankle_{sl}_bind", $"dogleg_{S}0_ankle_jnt" }));
                picks.Add(($"{S}_shoulder", new[] { $"arm_{S}0_upper_jnt", $"shoulder_{sl}_bind" }));
                picks.Add(($"{S}_elbow", new[] { $"arm_{S}0_forearm_jnt", $"elbow_{sl}_bind" }));
                picks.Add(($"{S}_varias2_SDK", new[] { $"pauldron_{S}0_0_jnt", $"varia_{sl}_bind" }));
            }
            picks.Add(("L_wrist", new[] { "arm_L0_wrist_jnt", "wrist_l_bind" }));
            foreach ((string d, string[] js) in picks)
            {
                string? j = First(js);
                if (j != null) jt[d] = T(src.JointPos[j]);
            }
            int nuv = p.Length / 3;
            var dom = new string[nuv];
            for (int v = 0; v < nuv; v++) dom[v] = sb[4 * v] >= 0 ? Ds[sb[4 * v]] : Ds[^1];
            var centers = new List<(string, double[])>();
            foreach (string leaf in new[] { "Head_1", "L_ankle", "R_ankle", "L_wrist", "R_elbow" })
            {
                double x = 0, y = 0, z = 0;
                int c = 0;
                for (int v = 0; v < nuv; v++)
                {
                    if (dom[v] != leaf) continue;
                    x += pt[3 * v]; y += pt[3 * v + 1]; z += pt[3 * v + 2];
                    c++;
                }
                if (c > 10) centers.Add((leaf + ".center", new[] { x / c, y / c, z / c }));
            }
            double[] hj = jt["Head_1"];
            var lines = new List<string>
            {
                $"# {name}: Retro's own joints of {label} (Metroid Prime 4: Beyond, bind pose = A-pose), folded onto the DS skeleton (mp4_to_trophy.py)",
            };
            string F4(double v, int w) => v.ToString("F4", CultureInfo.InvariantCulture).PadLeft(w);
            // the DS skeleton's order, then the leaf centres and the head's facing (the head looks straight ahead)
            foreach (string k in new[] { "Skeleton_Root" }.Concat(Ds))
            {
                if (jt.TryGetValue(k, out double[]? v)) lines.Add($"{k,-24} {F4(v[0], 10)} {F4(v[1], 10)} {F4(v[2], 10)}");
            }
            foreach ((string k, double[] v) in centers.Concat(new[]
            {
                ("Head_1.fwd", new[] { hj[0], hj[1], hj[2] + 0.1 * H }), ("Head_1.up", new[] { hj[0], hj[1] + 0.1 * H, hj[2] }),
            }))
            {
                lines.Add($"{k,-24} {F4(v[0], 10)} {F4(v[1], 10)} {F4(v[2], 10)}");
            }
            string joints = String.Join("\n", lines) + "\n";
            // the recipe's own marks (Sylux: the feet turned to the game's Idle foot direction, foot_fix.py's result)
            if (h.Marks.Count > 0)
            {
                joints += "\n# foot_fix: " + h.MarksNote + "\n" + String.Join("\n", h.Marks.Select(m => $"{m.Name,-24}{F4(m.X, 10)}{F4(m.Y, 10)}{F4(m.Z, 10)}")) + "\n";
            }
            File.WriteAllText(Path.Combine(outDir, name + ".joints.txt"), joints.Replace("\n", Crlf));
            File.WriteAllText(Path.Combine(outDir, name + ".convert.json"), Compact(new PyJson.Obj { { "character", label }, { "scale", s }, { "trophy", folder } }));

            // the DS skin for the layout step: one part, "bones" = the DS skeleton (Retro's axes: Z up)
            var skel = new Cinf();
            for (int b = 0; b < nd; b++)
            {
                double[] at = jt.TryGetValue(Ds[b], out double[]? q) ? q : jt["Pelvis"];
                skel.Bones[(uint)b] = new Cinf.Bone { Id = (uint)b, Parent = UInt32.MaxValue, Name = Ds[b], X = -at[0] / s, Y = at[2] / s, Z = at[1] / s };
            }
            var skin = new List<(uint Bone, float Weight)[]>(nuv);
            var rpos = new float[3 * nuv];
            for (int v = 0; v < nuv; v++)
            {
                skin.Add(Enumerable.Range(0, 4).Where(k => sb[4 * v + k] >= 0).Select(k => ((uint)sb[4 * v + k], sw[4 * v + k])).ToArray());
                rpos[3 * v] = (float)(-pt[3 * v] / s); rpos[3 * v + 1] = (float)(pt[3 * v + 2] / s); rpos[3 * v + 2] = (float)(pt[3 * v + 1] / s);
            }
            result.Skeleton = skel;
            result.Parts.Add(new RetroTrophy.Part
            {
                Model = new Cmdl { Positions = rpos, Skin = skin }, P = pt, N = n.Select(x => (double)x).ToArray(),
            });
            var per = Ds.Select(d => (d, dom.Count(x => x == d))).Where(x => x.Item2 > 0);
            Say(1f, $"{name}: {gx.Count} materials, {written} textures, {lines.Count - 1} landmarks; vertices per DS bone: "
                + String.Join(", ", per.Select(x => $"{x.d} {x.Item2}")));
            return result;
        }

        // numpy 1.x's default argsort (introsort without SIMD): spans longer than 16 are split once around a median of three,
        // shorter ones insertion-sorted. Not stable: a 50/50 vertex's order follows it, so the reference's skin does too.
        static void NumpyArgsort(float[] v, int n, int[] idx)
        {
            for (int k = 0; k < n; k++) idx[k] = k;
            var stack = new Stack<(int L, int R)>();
            int pl = 0, pr = n - 1;
            void Swap(int a, int b) => (idx[a], idx[b]) = (idx[b], idx[a]);
            while (true)
            {
                while (pr - pl > 15)
                {
                    int pm = pl + ((pr - pl) >> 1);
                    if (v[idx[pm]] < v[idx[pl]]) Swap(pm, pl);
                    if (v[idx[pr]] < v[idx[pm]]) Swap(pr, pm);
                    if (v[idx[pm]] < v[idx[pl]]) Swap(pm, pl);
                    float vp = v[idx[pm]];
                    int pi = pl, pj = pr - 1;
                    Swap(pm, pj);
                    while (true)
                    {
                        do pi++; while (v[idx[pi]] < vp);
                        do pj--; while (vp < v[idx[pj]]);
                        if (pi >= pj) break;
                        Swap(pi, pj);
                    }
                    Swap(pi, pr - 1);
                    // the larger side waits; the smaller one goes on
                    if (pi - pl < pr - pi) { stack.Push((pi + 1, pr)); pr = pi - 1; }
                    else { stack.Push((pl, pi - 1)); pl = pi + 1; }
                }
                for (int i = pl + 1; i <= pr; i++)
                {
                    int vi = idx[i], j = i;
                    while (j > pl && v[vi] < v[idx[j - 1]]) { idx[j] = idx[j - 1]; j--; }
                    idx[j] = vi;
                }
                if (stack.Count == 0) break;
                (pl, pr) = stack.Pop();
            }
        }

        // numpy's float32 sum of 17 values (its pairwise summation: 8 running sums, then the rest)
        static float PairwiseSum17(float[] a)
        {
            float r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            r0 += a[8]; r1 += a[9]; r2 += a[10]; r3 += a[11]; r4 += a[12]; r5 += a[13]; r6 += a[14]; r7 += a[15];
            float res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            res += a[16];
            return res;
        }

        static float[] Pick(List<float> src, List<int> used, int w)
        {
            var r = new float[used.Count * w];
            for (int i = 0; i < used.Count; i++)
            {
                for (int k = 0; k < w; k++) r[w * i + k] = src[w * used[i] + k];
            }
            return r;
        }

        // x ** e in single precision, correctly rounded (numpy's powf); e = 1 is x itself
        static float Pow(float x, double e) => e == 1.0 ? x : (float)Math.Pow(x, e);

        // ... from a float64 array
        static byte U8D(double v)
        {
            double x = v * 255;
            return x <= 0 ? (byte)0 : x >= 255 ? (byte)255 : (byte)x;
        }

        // a 0..1 value written as numpy's clip(v * 255, 0, 255).astype(uint8): truncated
        static byte U8(float v)
        {
            float x = v * 255f;
            return x <= 0f ? (byte)0 : x >= 255f ? (byte)255 : (byte)x;
        }

        static byte[] FlipRows(byte[] px, int w, int h, int ch)
        {
            var r = new byte[px.Length];
            int row = w * ch;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * row, r, (h - 1 - y) * row, row);
            return r;
        }

        // an RGB image upside down as RGBA (opaque), for PngOut
        static byte[] FlipRgb(byte[] rgb, int w, int h) => Rgba(FlipRows(rgb, w, h, 3));

        static byte[] Rgba(byte[] rgb)
        {
            var r = new byte[rgb.Length / 3 * 4];
            for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
            {
                r[j] = rgb[i]; r[j + 1] = rgb[i + 1]; r[j + 2] = rgb[i + 2]; r[j + 3] = 255;
            }
            return r;
        }

        // a map read at the colour texture's size (the reference resized it with PIL's bilinear filter: Image.resize, RGBA)
        static byte[]? Sized((byte[] Rgba, int W, int H)? t, int w, int h, string id)
        {
            if (t is not (byte[] px, int tw, int th)) return null;
            if (tw == w && th == h) return px;
            // PIL resizes RGBA premultiplied (RGBa) and divides the alpha back out after
            var pm = new byte[px.Length];
            for (int i = 0; i < px.Length; i += 4)
            {
                int al = px[i + 3];
                for (int c = 0; c < 3; c++)
                {
                    int tmp = px[i + c] * al + 128;
                    pm[i + c] = (byte)(((tmp >> 8) + tmp) >> 8);
                }
                pm[i + 3] = (byte)al;
            }
            byte[] o = PilBilinear(pm, tw, th, w, h);
            for (int i = 0; i < o.Length; i += 4)
            {
                int al = o[i + 3];
                if (al == 255 || al == 0) continue;
                for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, 255 * o[i + c] / al);
            }
            return o;
        }

        // PIL's resample with its bilinear (triangle) filter, 8-bit: the filter widened by the scale when shrinking,
        // weights normalised and rounded to 22-bit fixed point; across first, then down
        static byte[] PilBilinear(byte[] src, int sw, int sh, int dw, int dh)
        {
            const int Bits = 22;
            static (int[] Min, int[] Count, int[][] K) Coeffs(int inSize, int outSize)
            {
                double scale = (double)inSize / outSize, fs = Math.Max(scale, 1.0), support = 1.0 * fs, ss = 1.0 / fs;
                var mins = new int[outSize]; var counts = new int[outSize]; var ks = new int[outSize][];
                for (int xx = 0; xx < outSize; xx++)
                {
                    double center = (xx + 0.5) * scale;
                    int xmin = Math.Max((int)(center - support + 0.5), 0);
                    int xmax = Math.Min((int)(center + support + 0.5), inSize) - xmin;
                    var k = new double[xmax];
                    double ww = 0;
                    for (int x = 0; x < xmax; x++)
                    {
                        double v = Math.Abs((x + xmin - center + 0.5) * ss);
                        k[x] = v < 1 ? 1 - v : 0;
                        ww += k[x];
                    }
                    var ki = new int[xmax];
                    for (int x = 0; x < xmax; x++)
                    {
                        double kv = ww != 0 ? k[x] / ww : k[x];
                        ki[x] = kv < 0 ? (int)(-0.5 + kv * (1 << Bits)) : (int)(0.5 + kv * (1 << Bits));
                    }
                    mins[xx] = xmin; counts[xx] = xmax; ks[xx] = ki;
                }
                return (mins, counts, ks);
            }
            static byte Clip(long v) { v >>= Bits; return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }
            byte[] cur = src;
            int cw = sw;
            if (dw != sw)
            {
                (int[] mn, int[] cn, int[][] kk) = Coeffs(sw, dw);
                var o = new byte[dw * sh * 4];
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < dw; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            long acc = 1 << (Bits - 1);
                            for (int i = 0; i < cn[x]; i++) acc += (long)cur[(y * sw + mn[x] + i) * 4 + c] * kk[x][i];
                            o[(y * dw + x) * 4 + c] = Clip(acc);
                        }
                cur = o; cw = dw;
            }
            if (dh != sh)
            {
                (int[] mn, int[] cn, int[][] kk) = Coeffs(sh, dh);
                var o = new byte[cw * dh * 4];
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < cw; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            long acc = 1 << (Bits - 1);
                            for (int i = 0; i < cn[y]; i++) acc += (long)cur[((mn[y] + i) * cw + x) * 4 + c] * kk[y][i];
                            o[(y * cw + x) * 4 + c] = Clip(acc);
                        }
                cur = o;
            }
            return cur;
        }

        // the ICNC colour as a glow hue (normalised when brighter than 1); ints when the material has none, as the reference
        static object?[] GlowHue(Material mm)
        {
            if (!mm.Col.TryGetValue("ICNC", out double[]? c)) return new object?[] { 1, 1, 1 };
            double m = Math.Max(c[0], Math.Max(c[1], c[2]));
            return m > 1 ? new object?[] { c[0] / m, c[1] / m, c[2] / m } : new object?[] { c[0], c[1], c[2] };
        }

        // uv1 an affine copy of uv0 over the material's triangles (least squares, worst miss under 1e-3)
        static bool Affine(int[] sel, int[] tri, float[] uv0, float[] uv1)
        {
            var ata = new double[3, 3];
            var atb = new double[3, 2];
            foreach (int t in sel)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = tri[3 * t + c];
                    double[] x = { uv0[2 * v], uv0[2 * v + 1], 1 };
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++) ata[i, j] += x[i] * x[j];
                        atb[i, 0] += x[i] * uv1[2 * v]; atb[i, 1] += x[i] * uv1[2 * v + 1];
                    }
                }
            }
            double[,]? m = Solve3(ata, atb);
            if (m == null) return false;
            foreach (int t in sel)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = tri[3 * t + c];
                    for (int k = 0; k < 2; k++)
                    {
                        double f = uv0[2 * v] * m[0, k] + uv0[2 * v + 1] * m[1, k] + m[2, k];
                        if (Math.Abs(f - uv1[2 * v + k]) > 1e-3) return false;
                    }
                }
            }
            return true;
        }

        static double[,]? Solve3(double[,] a, double[,] b)
        {
            var m = (double[,])a.Clone();
            var r = (double[,])b.Clone();
            for (int c = 0; c < 3; c++)
            {
                int p = c;
                for (int i = c + 1; i < 3; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[p, c])) p = i;
                if (Math.Abs(m[p, c]) < 1e-12) return null;
                for (int j = 0; j < 3; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
                for (int j = 0; j < 2; j++) (r[c, j], r[p, j]) = (r[p, j], r[c, j]);
                for (int i = 0; i < 3; i++)
                {
                    if (i == c) continue;
                    double f = m[i, c] / m[c, c];
                    for (int j = 0; j < 3; j++) m[i, j] -= f * m[c, j];
                    for (int j = 0; j < 2; j++) r[i, j] -= f * r[c, j];
                }
            }
            var x = new double[3, 2];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) x[i, j] = r[i, j] / m[i, i];
            return x;
        }

        // the glow texture as laid out on uv1, painted into uv0's layout (triangle by triangle, barycentric); later
        // triangles paint over earlier ones. Returns RGBA (rows as the source's) and the share of pixels painted.
        static (byte[] Rgba, double Cover) Rebake(byte[] src, int sw, int sh, int[] sel, int[] tri, float[] uv0, float[] uv1, int size)
        {
            var o = new byte[size * size * 4];
            var painted = new bool[size * size];
            Span<float> a0 = stackalloc float[6];
            Span<float> a1 = stackalloc float[6];
            foreach (int t in sel)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = tri[3 * t + c];
                    a0[2 * c] = uv0[2 * v] * size; a0[2 * c + 1] = uv0[2 * v + 1] * size;
                    a1[2 * c] = uv1[2 * v]; a1[2 * c + 1] = uv1[2 * v + 1];
                }
                int lox = (int)Math.Floor(Math.Min(a0[0], Math.Min(a0[2], a0[4]))), loy = (int)Math.Floor(Math.Min(a0[1], Math.Min(a0[3], a0[5])));
                int hix = (int)Math.Ceiling(Math.Max(a0[0], Math.Max(a0[2], a0[4]))), hiy = (int)Math.Ceiling(Math.Max(a0[1], Math.Max(a0[3], a0[5])));
                if (Math.Max(hix - lox, hiy - loy) > size) continue;
                // the 2x2 system [e1 e2] l = q - a0, solved like LAPACK (partial pivoting), in double
                float e1x = a0[2] - a0[0], e1y = a0[3] - a0[1], e2x = a0[4] - a0[0], e2y = a0[5] - a0[1];
                if (Math.Abs((double)e1x * e2y - (double)e2x * e1y) < 1e-9) continue;
                double m00 = e1x, m01 = e2x, m10 = e1y, m11 = e2y;
                bool swap = Math.Abs(m10) > Math.Abs(m00);
                if (swap)
                {
                    (m00, m10) = (m10, m00);
                    (m01, m11) = (m11, m01);
                }
                // (the back substitution multiplies by the pivots' reciprocals, as numpy's OpenBLAS does: bit-identical)
                double l = m10 * (1.0 / m00), u = m11 - l * m01, ru = 1.0 / u, r0 = 1.0 / m00;
                for (int y = loy; y <= hiy; y++)
                {
                    for (int x = lox; x <= hix; x++)
                    {
                        double bx = x + 0.5 - a0[0], by = y + 0.5 - a0[1];
                        double y0 = swap ? by : bx, y1 = (swap ? bx : by) - l * y0;
                        double l2 = y1 * ru, l1 = (y0 - m01 * l2) * r0, lz = 1 - l1 - l2;
                        if (!(lz >= -0.02 && l1 >= -0.02 && l2 >= -0.02)) continue;
                        double us = lz * a1[0] + l1 * a1[2] + l2 * a1[4], vs = lz * a1[1] + l1 * a1[3] + l2 * a1[5];
                        int sx = (int)(PyMod1(us) * sw) % sw, sy = (int)(PyMod1(vs) * sh) % sh;
                        int px = ((x % size) + size) % size, py = ((y % size) + size) % size;
                        Buffer.BlockCopy(src, 4 * (sy * sw + sx), o, 4 * (py * size + px), 4);
                        painted[py * size + px] = true;
                    }
                }
            }
            return (o, painted.Count(x => x) / (double)painted.Length);
        }

        // numpy's mod(x, 1): the remainder with the divisor's sign
        static double PyMod1(double x)
        {
            double m = x % 1.0;
            return m < 0 ? m + 1.0 : m;
        }

        // two sphere maps (matcaps) for the app's env texgen: a soft studio room (sky bright, floor dark, a rim) for the
        // coloured reflection, and a HOT one (key-light glints only) for white highlights. RGB, rows top-down
        static (byte[] Room, byte[] Hot) StudioSpheres(int n)
        {
            static double[] Unit(double x, double y, double z)
            {
                double l = Math.Sqrt(x * x + y * y + z * z);
                return new[] { x / l, y / l, z / l };
            }
            double[] k1 = Unit(-0.45, 0.55, 0.7), k2 = Unit(0.85, 0.25, -0.15), k3 = Unit(0.6, 0.7, 0.4);
            var room = new byte[n * n * 3];
            var hot = new byte[n * n * 3];
            for (int yy = 0; yy < n; yy++)
            {
                for (int xx = 0; xx < n; xx++)
                {
                    double nx = (xx + 0.5) / n * 2 - 1, ny = 1 - (yy + 0.5) / n * 2, r2 = nx * nx + ny * ny, nz = Math.Sqrt(Math.Clamp(1 - r2, 0, 1));
                    double Lobe(double[] d, double p) => Math.Pow(Math.Clamp(nx * d[0] + ny * d[1] + nz * d[2], 0, 1), p);
                    double rm = 0.05 + 0.28 * Math.Pow(Math.Clamp((ny + 0.4) / 1.3, 0, 1), 1.6) + 0.30 * Lobe(k1, 10) + 0.35 * Lobe(k2, 6) + 0.25 * Math.Pow(1 - nz, 3);
                    double ht = 1.3 * Lobe(k1, 60) + 0.6 * Lobe(k3, 90) + 0.35 * Lobe(k2, 30);
                    bool inside = r2 <= 1.0;
                    byte br = (byte)(Math.Clamp(inside ? rm : 0, 0, 1) * 255), bh = (byte)(Math.Clamp(inside ? ht : 0, 0, 1) * 255);
                    int i = 3 * (yy * n + xx);
                    room[i] = room[i + 1] = room[i + 2] = br;
                    hot[i] = hot[i + 1] = hot[i + 2] = bh;
                }
            }
            return (room, hot);
        }

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

        static string Dae(List<PyJson.Obj> gx, int[] mids, int[] tm, int[] tri, double[] pt, float[] n, float[] uv0)
        {
            var libImg = new StringBuilder();
            var libFx = new StringBuilder();
            var libMat = new StringBuilder();
            var geo = new StringBuilder();
            var nodes = new StringBuilder();
            for (int gi = 0; gi < gx.Count; gi++)
            {
                int mid = mids[gi];
                var corners = new List<int>();
                int count = 0;
                for (int t = 0; t < tm.Length; t++)
                {
                    if (tm[t] != mid) continue;
                    corners.Add(tri[3 * t]); corners.Add(tri[3 * t + 1]); corners.Add(tri[3 * t + 2]);
                    count++;
                }
                string mn = (string)gx[gi].First(kv => kv.Key == "name").Value!;
                var layers = (List<object?>)gx[gi].First(kv => kv.Key == "layers").Value!;
                if (layers.Count > 0)
                {
                    string b = (string)((PyJson.Obj)layers[0]!).First(kv => kv.Key == "name").Value!;
                    libImg.Append($"<image id=\"img{gi}\" name=\"{b}\"><init_from>{b}.png</init_from></image>");
                }
                int nc = corners.Count;
                geo.Append($"<geometry id=\"g{gi}\" name=\"{mn}\"><mesh>\n");
                geo.Append($"<source id=\"g{gi}_p\"><float_array id=\"g{gi}_pa\" count=\"{3 * nc}\">");
                Arr(geo, corners.SelectMany(c => new[] { pt[3 * c], pt[3 * c + 1], pt[3 * c + 2] }));
                geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_pa\" count=\"{nc}\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>\n");
                geo.Append($"<source id=\"g{gi}_n\"><float_array id=\"g{gi}_na\" count=\"{3 * nc}\">");
                Arr(geo, corners.SelectMany(c => new double[] { n[3 * c], n[3 * c + 1], n[3 * c + 2] }));
                geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_na\" count=\"{nc}\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>\n");
                geo.Append($"<source id=\"g{gi}_t\"><float_array id=\"g{gi}_ta\" count=\"{2 * nc}\">");
                Arr(geo, corners.SelectMany(c => new double[] { uv0[2 * c], uv0[2 * c + 1] }));
                geo.Append($"</float_array><technique_common><accessor source=\"#g{gi}_ta\" count=\"{nc}\" stride=\"2\"><param name=\"S\" type=\"float\"/><param name=\"T\" type=\"float\"/></accessor></technique_common></source>\n");
                geo.Append($"<vertices id=\"g{gi}_v\"><input semantic=\"POSITION\" source=\"#g{gi}_p\"/></vertices>\n");
                geo.Append($"<triangles material=\"{mn}\" count=\"{count}\"><input semantic=\"VERTEX\" source=\"#g{gi}_v\" offset=\"0\"/><input semantic=\"NORMAL\" source=\"#g{gi}_n\" offset=\"0\"/><input semantic=\"TEXCOORD\" source=\"#g{gi}_t\" offset=\"0\" set=\"0\"/><p>");
                geo.Append(String.Join(" ", Enumerable.Range(0, nc)));
                geo.Append("</p></triangles>\n</mesh></geometry>");
                libFx.Append($"<effect id=\"{mn}-fx\"><profile_COMMON><technique sid=\"common\"><lambert><diffuse><color>1 1 1 1</color></diffuse></lambert></technique></profile_COMMON></effect>");
                libMat.Append($"<material id=\"{mn}\" name=\"{mn}\"><instance_effect url=\"#{mn}-fx\"/></material>");
                nodes.Append($"<node id=\"n{gi}\" name=\"{mn}\"><instance_geometry url=\"#g{gi}\"><bind_material><technique_common><instance_material symbol=\"{mn}\" target=\"#{mn}\"/></technique_common></bind_material></instance_geometry></node>");
            }
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n"
                + "<asset><up_axis>Y_UP</up_axis></asset>\n"
                + $"<library_images>{libImg}</library_images><library_effects>{libFx}</library_effects><library_materials>{libMat}</library_materials>\n"
                + $"<library_geometries>{geo}</library_geometries>\n"
                + $"<library_visual_scenes><visual_scene id=\"scene\">{nodes}</visual_scene></library_visual_scenes><scene><instance_visual_scene url=\"#scene\"/></scene>\n"
                + "</COLLADA>\n";
        }

        // json.dump without an indent: {"key": value, ...}
        static string Compact(PyJson.Obj o) =>
            "{" + String.Join(", ", o.Select(kv => PyJson.Dump(kv.Key) + ": " + (kv.Value is System.Collections.IList l && kv.Value is not string
                ? "[" + String.Join(", ", l.Cast<object?>().Select(x => PyJson.Dump(x))) + "]" : PyJson.Dump(kv.Value)))) + "}";
    }
}
