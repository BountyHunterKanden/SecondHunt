using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Guns;
using MphRecomp.Import.Hd;

namespace MphRecomp.Import.Bodies
{
    // A Metroid Prime 4: Beyond character on ITS OWN skeleton with ITS OWN clips, from the user's own RomFS (owner
    // 2026-10-17, Beyond Sylux: "We have Syluxs real rig ... we should only be using vanilla rig for movement like running
    // strafe and backwards. why cant we get the clean results we got for trooper and dark samus"). As BodyImporter's Retro
    // bodies, into one folder bodies/<Name>/ that NativeBody loads:
    //   body.bin      "MPHGUN2" (Gun4Builder's layout): the gameplay character's joints the mesh is skinned to (+ their
    //                 ancestors) with their full inverse binds, the mesh with Prime 4's own skin (no fold onto the DS
    //                 bones), and the recipe's "flourish" clips (its idle + flourishes) per key, local rotation xyzw /
    //                 translation / scale
    //   body.gx.json  the HD import's look (BeyondTrophy.Convert's GX programs), one entry per draw    <tex>.png  its textures
    //   body.cfg      height (the bind mesh's), feet (the idle's lowest point), game mp4
    // The model is the recipe's (the cinematic one, the HD import's); the clips come from its "flourish" character (the
    // gameplay one): both skeletons name the same joints. Prime 4's space is MPH's (Y up, facing +Z, +X left): positions
    // stay in the game's units, NativeBody scales them to the player's height. What the HD import drops (Lockjaw, VFX:
    // BeyondTrophy's map) goes here too. The DS moves (running, strafing, jumps) are vanilla Sylux's clips put on this
    // skeleton when the body loads (Campaign/BeyondSyluxBody, DsRetarget).
    public static class BeyondBody
    {
        public const int MaxPalette = 32;

        sealed class Tri
        {
            public int Material;
            public double[] Pos = new double[9], Nrm = new double[9], Uv = new double[6];
            public List<(int Joint, double Weight)>[] Skin = new List<(int, double)>[3];
        }

        // part: "body" (the character: every triangle the HD import keeps) or "lockjaw" (Sylux's alt form, owner 2026-10-17
        // "we still need to get lockjaw": the triangles the body leaves to his lockjaw_* joints, on the same skeleton and
        // scale, with the recipe's flourish clips = its Lockjaw clips)
        public static GunImporter.Result Export(IReadOnlyList<BeyondArchive> paks, HdRecipe h, string outDir, Action<float, string>? progress = null,
            CancellationToken ct = default, string part = "body")
        {
            var result = new GunImporter.Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            if (h.FlourishClips.Length == 0 || h.FlourishIdle.Length == 0) throw new InvalidDataException($"{h.Name}: a Prime 4 body needs the recipe's \"flourish\" (idle + clips)");
            Directory.CreateDirectory(outDir);

            // ---- the model and its look (the HD import's own converter, into a scratch folder)
            Say(0.02f, "reading the model, materials and textures");
            var src = BeyondTrophy.Source.FromGame(paks, Guid.Parse(h.Smdl), Guid.Parse(h.Chpr));
            if (src.AssetsSha1 != h.AssetsSha1) throw new InvalidDataException($"{h.Name}'s files are a different revision (SHA-1 {src.AssetsSha1}, the recipe was made from {h.AssetsSha1})");
            string lookDir = Path.Combine(outDir, ".look");
            if (Directory.Exists(lookDir)) Directory.Delete(lookDir, true);
            // (the Lockjaw part's material: every material of the model, the look writes only those asked for or kept)
            IEnumerable<int>? also = part == "lockjaw" ? Enumerable.Range(0, src.Mats.Count) : null;
            BeyondTrophy.Convert(src, h, BeyondTrophy.Look.From(h.Look), lookDir, (f, m) => progress?.Invoke(0.05f + 0.35f * f, "look: " + m), ct, also);
            var gxByName = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            foreach (JsonNode? m in JsonNode.Parse(File.ReadAllText(Path.Combine(lookDir, h.Stem + ".gx.json")))!["materials"]!.AsArray())
            {
                if (m?["name"]?.GetValue<string>() is string n) gxByName[n] = m;
            }
            ct.ThrowIfCancellationRequested();

            // ---- the gameplay character: skeleton + clips
            Guid cid = Guid.Parse(h.FlourishChpr);
            byte[] cbytes = BeyondPakModelSource.Holder(paks, cid, "character file").ReadAsset(cid);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(cbytes)).ToLowerInvariant();
            if (sha != h.FlourishChprSha1) throw new InvalidDataException($"{h.Name}: character {h.FlourishChpr} has SHA-1 {sha}, the recipe was made from {h.FlourishChprSha1}");
            ChprCharacter character = ChprCharacter.Load(cbytes);
            ChprSkeleton sk = character.Skeleton;
            string[] sNames = sk.Names;
            int[] sParent = sk.Parent;
            // the model's own skeleton (its binds: the mesh was skinned on them)
            Guid mid0 = Guid.Parse(h.Chpr);
            ChprSkeleton msk = ChprCharacter.Load(BeyondPakModelSource.Holder(paks, mid0, "character file").ReadAsset(mid0)).Skeleton;

            // ---- triangles: BeyondTrophy.Convert's (the same meshes and the same drop rule), each corner's own skin
            var matOf = new System.Text.RegularExpressions.Regex(@"MatID([0-9]+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            int MatId(BeyondTrophy.Mesh m) => Int32.Parse(matOf.Match(m.Name).Groups[1].Value, CultureInfo.InvariantCulture);
            bool twoSets = src.Meshes.Any(m => m.Name.Contains("_LOD0_Mat", StringComparison.Ordinal))
                && src.Meshes.Any(m => m.Name.Contains("_LOD0_1_", StringComparison.Ordinal));
            var fullMats = twoSets
                ? src.Meshes.Where(m => !m.Name.Contains("_LOD0_1_", StringComparison.Ordinal)).Select(m => src.Mats[MatId(m)].Mati).ToHashSet()
                : new HashSet<string>();
            bool lockjaw = part == "lockjaw";
            if (!lockjaw && part != "body") throw new InvalidDataException($"{h.Name}: unknown body part '{part}'");
            bool[] droppedJ = src.SkinNames.Select(n => BeyondTrophy.DsName(n, src.Parent) == null).ToArray();
            // the joints this part is skinned to (the rest drop its triangles)
            bool[] inPart = src.SkinNames.Select((n, k) => lockjaw ? n.StartsWith("lockjaw_", StringComparison.Ordinal) : !droppedJ[k]).ToArray();
            // a skin joint by name in the gameplay skeleton; one it lacks rides its nearest ancestor it has
            var jointOf = new int[src.SkinNames.Length];
            int stand = 0;
            for (int k = 0; k < jointOf.Length; k++)
            {
                string? nm = src.SkinNames[k];
                int j = -1;
                for (int guard = 0; nm != null && guard < 200; guard++)
                {
                    if (sk.Index.TryGetValue(nm, out j)) break;
                    j = -1;
                    nm = src.Parent.TryGetValue(nm, out string? p) ? p : null;
                }
                if (j < 0 && inPart[k]) throw new InvalidDataException($"{h.Name}: skin joint '{src.SkinNames[k]}' is not in the character's skeleton");
                if (j >= 0 && sNames[j] != src.SkinNames[k]) stand++;
                jointOf[k] = j;
            }
            if (stand > 0) Say(0.42f, $"{stand} skin joints not in the character's skeleton ride an ancestor");
            var tris = new List<Tri>();
            int dropped = 0;
            // the body's bind height (its kept triangles), for every part: one scale for the character and its alt form
            double lo = Double.MaxValue, hi = Double.MinValue;
            foreach (BeyondTrophy.Mesh mesh in src.Meshes)
            {
                if (twoSets && mesh.Name.Contains("_LOD0_1_", StringComparison.Ordinal) && fullMats.Contains(src.Mats[MatId(mesh)].Mati)) continue;
                int mat = MatId(mesh), count = mesh.P.Length / 3;
                var keep = new bool[count];
                var keepBody = new bool[count];
                var skin = new List<(int, double)>[count];
                for (int v = 0; v < count; v++)
                {
                    double kept = 0, gone = 0, bodyW = 0, notBody = 0;
                    var l = new List<(int, double)>(4);
                    for (int k = 0; k < 4; k++)
                    {
                        float wt = mesh.Weights[4 * v + k];
                        if (!(wt > 0)) continue;
                        int sj = mesh.Joints[4 * v + k];
                        if (droppedJ[sj]) notBody += wt;
                        else bodyW += wt;
                        if (!inPart[sj]) gone += wt;
                        else
                        {
                            kept += wt;
                            int j = jointOf[sj];
                            int at = l.FindIndex(x => x.Item1 == j);
                            if (at >= 0) l[at] = (j, l[at].Item2 + wt);
                            else l.Add((j, wt));
                        }
                    }
                    keep[v] = kept > gone;
                    keepBody[v] = bodyW > notBody;
                    skin[v] = l.Select(x => (x.Item1, x.Item2 / Math.Max(kept, 1e-9))).ToList();
                }
                for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                    if (keepBody[a] && keepBody[b] && keepBody[c])
                    {
                        foreach (int v in new[] { a, b, c })
                        {
                            lo = Math.Min(lo, mesh.P[3 * v + 1]);
                            hi = Math.Max(hi, mesh.P[3 * v + 1]);
                        }
                    }
                    if (!(keep[a] && keep[b] && keep[c]))
                    {
                        dropped++;
                        continue;
                    }
                    var tri = new Tri { Material = mat };
                    int[] cs = { a, b, c };
                    for (int ci = 0; ci < 3; ci++)
                    {
                        int v = cs[ci];
                        for (int i = 0; i < 3; i++)
                        {
                            tri.Pos[3 * ci + i] = mesh.P[3 * v + i];
                            tri.Nrm[3 * ci + i] = mesh.N[3 * v + i];
                        }
                        tri.Uv[2 * ci] = mesh.Uv0[2 * v];
                        tri.Uv[2 * ci + 1] = mesh.Uv0[2 * v + 1];
                        tri.Skin[ci] = skin[v];
                    }
                    tris.Add(tri);
                }
            }
            if (tris.Count == 0) throw new InvalidDataException($"{h.Name}: no triangle is drawn");
            Say(0.45f, lockjaw ? $"Lockjaw triangles {tris.Count} ({dropped} are the body's / VFX)" : $"triangles {tris.Count} ({dropped} dropped with Lockjaw / VFX joints)");

            // ---- bones: the joints the skin uses and their ancestors, parents first
            var need = new HashSet<int>();
            foreach (Tri t in tris)
            {
                foreach (List<(int Joint, double Weight)> ws in t.Skin)
                {
                    foreach ((int j0, _) in ws)
                    {
                        for (int j = j0; j >= 0 && need.Add(j); j = sParent[j]) { }
                    }
                }
            }
            var order = new List<int>();
            var placed = new HashSet<int>();
            void Visit(int j)
            {
                if (placed.Contains(j)) return;
                if (sParent[j] >= 0) Visit(sParent[j]);
                placed.Add(j);
                order.Add(j);
            }
            foreach (int j in need.OrderBy(x => x)) Visit(j);
            var bix = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) bix[order[i]] = i;
            int nb = order.Count;
            // each bone's bind: the model's own (its skinned bind, else its rest), else the character's rest
            var bindW = new double[nb][];
            double worst = 0;
            string worstName = "";
            for (int bi = 0; bi < nb; bi++)
            {
                string nm = sNames[order[bi]];
                double[]? mb = null;
                if (msk.Index.TryGetValue(nm, out int mj)) mb = msk.BindWorld.TryGetValue(mj, out double[]? bw) ? bw : msk.RestWorld[mj];
                bindW[bi] = mb ?? sk.RestWorld[order[bi]];
                double[] cr = sk.RestWorld[order[bi]];
                double d = Math.Sqrt(Sq(bindW[bi][3] - cr[3]) + Sq(bindW[bi][7] - cr[7]) + Sq(bindW[bi][11] - cr[11]));
                if (d > worst) { worst = d; worstName = nm; }
            }
            Say(0.5f, $"bones {nb}; the model's binds vs the character's rest: at most {worst:0.####} apart ({worstName})");

            // ---- clips: per bone per frame the clip's local, else the idle's first frame, else the rest
            ChprAnimation Anim(string name)
            {
                int ai = character.FindAnim(name);
                if (ai < 0) throw new InvalidDataException($"{h.Name}: the character has no animation '{name}'");
                ChprAnimResult r = character.Decode(ai);
                if (!r.Ok || r.Animation == null) throw new InvalidDataException($"{h.Name}: animation '{name}': {r.Status} {r.Message}");
                return r.Animation;
            }
            ChprAnimation idle = Anim(h.FlourishIdle);
            Dictionary<string, int> idleAt = JointMap(idle);
            float[] FramesFor(ChprAnimation an)
            {
                Dictionary<string, int> at = JointMap(an);
                int n = an.Frames, ja = an.Joints.Length;
                var F = new float[n * nb * 10];
                for (int bi = 0; bi < nb; bi++)
                {
                    string nm = sNames[order[bi]];
                    for (int f = 0; f < n; f++)
                    {
                        int o = (f * nb + bi) * 10;
                        if (at.TryGetValue(nm, out int i))
                        {
                            Array.Copy(an.RotXyzw, (f * ja + i) * 4, F, o, 4);
                            Array.Copy(an.Trans, (f * ja + i) * 3, F, o + 4, 3);
                            Array.Copy(an.Scale, (f * ja + i) * 3, F, o + 7, 3);
                        }
                        else if (idleAt.TryGetValue(nm, out i))
                        {
                            Array.Copy(idle.RotXyzw, i * 4, F, o, 4);
                            Array.Copy(idle.Trans, i * 3, F, o + 4, 3);
                            Array.Copy(idle.Scale, i * 3, F, o + 7, 3);
                        }
                        else
                        {
                            (double[] q, double[] tr) = sk.RestLocal(nm);
                            F[o] = (float)q[1]; F[o + 1] = (float)q[2]; F[o + 2] = (float)q[3]; F[o + 3] = (float)q[0];
                            F[o + 4] = (float)tr[0]; F[o + 5] = (float)tr[1]; F[o + 6] = (float)tr[2];
                            F[o + 7] = 1; F[o + 8] = 1; F[o + 9] = 1;
                        }
                    }
                }
                return F;
            }
            var baked = new List<(string Name, float[] F, int Frames, double Fps)>();
            foreach (string clip in h.FlourishClips.Prepend(h.FlourishIdle))
            {
                ct.ThrowIfCancellationRequested();
                ChprAnimation an = clip == h.FlourishIdle ? idle : Anim(clip);
                float[] F = FramesFor(an);
                baked.Add((clip, F, an.Frames, an.FpsAssumed));
                // scale the native body's pose leaves out (NativeBody poses rotation + translation)
                double sc = 0;
                for (int k = 0; k < F.Length; k += 10) sc = Math.Max(sc, Math.Max(Math.Abs(F[k + 7] - 1), Math.Max(Math.Abs(F[k + 8] - 1), Math.Abs(F[k + 9] - 1))));
                Say(0.55f, $"  {clip}: {an.Frames} frames at {an.FpsAssumed:0.#} fps; scale off 1 by at most {sc:0.###}");
            }

            // ---- the idle's first frame posed: where the feet stand
            float[] idleF = baked[0].F;
            var Wi = new double[nb][];
            for (int bi = 0; bi < nb; bi++)
            {
                int o = bi * 10;
                double[] L = ChprMath.Trs(new double[] { idleF[o + 3], idleF[o], idleF[o + 1], idleF[o + 2] },
                    new double[] { idleF[o + 4], idleF[o + 5], idleF[o + 6] }, new double[] { idleF[o + 7], idleF[o + 8], idleF[o + 9] });
                int pj = sParent[order[bi]];
                Wi[bi] = pj >= 0 ? ChprMath.MatMul(Wi[bix[pj]], L) : L;
            }
            var IB = new double[nb][];
            var skinM = new double[nb][];
            for (int bi = 0; bi < nb; bi++)
            {
                IB[bi] = ChprMath.Invert(bindW[bi]);
                skinM[bi] = ChprMath.MatMul(Wi[bi], IB[bi]);
            }
            double poseLo = Double.MaxValue;
            foreach (Tri t in tris)
            {
                for (int c = 0; c < 3; c++)
                {
                    double x = t.Pos[3 * c], y = t.Pos[3 * c + 1], z = t.Pos[3 * c + 2];
                    double py = 0;
                    foreach ((int j, double wt) in t.Skin[c])
                    {
                        double[] M = skinM[bix[j]];
                        py += wt * (M[4] * x + M[5] * y + M[6] * z + M[7]);
                    }
                    poseLo = Math.Min(poseLo, py);
                }
            }
            Say(0.6f, $"bind height {hi - lo:0.###} (feet {lo:0.###}); the idle's lowest point {poseLo:0.###}");

            // ---- body.bin (MPHGUN2)
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("MPHGUN2\0"));
            void Name(string s)
            {
                byte[] b = Encoding.Latin1.GetBytes(s);
                int n = Math.Min(b.Length, 255);
                w.Write((byte)n);
                w.Write(b, 0, n);
            }
            w.Write((uint)nb);
            for (int bi = 0; bi < nb; bi++)
            {
                int pj = sParent[order[bi]];
                w.Write(pj >= 0 ? bix[pj] : -1);
                for (int c = 0; c < 4; c++)
                {
                    for (int r = 0; r < 4; r++) w.Write((float)IB[bi][4 * r + c]);
                }
                Name(sNames[order[bi]]);
            }
            // draws: per material (the look's order), split so no draw names more than MaxPalette bones
            const float whiteBa = (float)(255 + 0.999);
            var chunks = new List<(int Material, List<int> Palette, List<float> Rows)>();
            foreach (int mat in tris.Select(t => t.Material).Distinct().OrderBy(x => x))
            {
                var pal = new List<int>();
                var palSet = new HashSet<int>();
                var rows = new List<float>();
                foreach (Tri t in tris.Where(x => x.Material == mat))
                {
                    var ws3 = t.Skin.Select(s => s.OrderBy(x => -x.Weight).Take(4).ToList()).ToArray();
                    var bones = new HashSet<int>(ws3.SelectMany(s => s.Select(x => bix[x.Joint])));
                    if (palSet.Count + bones.Count(x => !palSet.Contains(x)) > MaxPalette)
                    {
                        chunks.Add((mat, pal, rows));
                        pal = new List<int>();
                        palSet = new HashSet<int>();
                        rows = new List<float>();
                    }
                    foreach (int b in bones.Where(x => !palSet.Contains(x)).OrderBy(x => x))
                    {
                        pal.Add(b);
                        palSet.Add(b);
                    }
                    for (int c = 0; c < 3; c++)
                    {
                        List<(int Joint, double Weight)> ws = ws3[c];
                        double tot = Math.Max(ws.Sum(x => x.Weight), 1e-12);
                        double nx = t.Nrm[3 * c], ny = t.Nrm[3 * c + 1], nz = t.Nrm[3 * c + 2];
                        double nn = Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12;
                        rows.Add((float)t.Pos[3 * c]); rows.Add((float)t.Pos[3 * c + 1]); rows.Add((float)t.Pos[3 * c + 2]);
                        rows.Add((float)(nx / nn)); rows.Add((float)(ny / nn)); rows.Add((float)(nz / nn));
                        // the game's own V: the body's draw samples (u, 1 - v) of the row-flipped PNGs (CampaignRetroBody.FlipV)
                        rows.Add((float)t.Uv[2 * c]); rows.Add((float)t.Uv[2 * c + 1]);
                        rows.Add(1f); rows.Add(1f); rows.Add(whiteBa);
                        for (int i = 0; i < 4; i++) rows.Add(i < ws.Count ? pal.IndexOf(bix[ws[i].Joint]) : 0f);
                        for (int i = 0; i < 4; i++) rows.Add(i < ws.Count ? (float)(ws[i].Weight / tot) : 0f);
                    }
                }
                chunks.Add((mat, pal, rows));
            }
            w.Write((uint)chunks.Count);
            foreach ((_, List<int> pal, List<float> rows) in chunks)
            {
                w.Write((uint)pal.Count);
                foreach (int b in pal) w.Write(b);
                w.Write((uint)(rows.Count / 19));
                foreach (float f in rows) w.Write(f);
            }
            w.Write((uint)baked.Count);
            foreach ((string name, float[] F, int n, double fps) in baked)
            {
                double tick = 1.0 / fps;
                Name(name);
                w.Write((float)(n > 1 ? (n - 1) * tick : 0.0));
                w.Write((float)tick);
                w.Write((uint)n);
                foreach (float f in F) w.Write(f);
            }
            w.Flush();
            File.WriteAllBytes(Path.Combine(outDir, "body.bin"), ms.ToArray());
            result.Bones = nb; result.Materials = chunks.Count; result.Animations = baked.Count; result.Bytes = (int)ms.Length;
            Say(0.8f, $"body.bin: {nb} bones, {chunks.Count} draws, {tris.Count} triangles, {baked.Count} clips, {ms.Length / 1048576.0:F1} MB");

            // ---- body.gx.json (one entry per draw) + the textures it names; body.cfg
            var gx = new JsonArray();
            var texNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((int mat, _, _) in chunks)
            {
                string mn = $"{h.Stem}_mat{mat}";
                if (!gxByName.TryGetValue(mn, out JsonNode? g)) throw new InvalidDataException($"{h.Name}: the look has no material {mn}");
                gx.Add(g.DeepClone());
                foreach (JsonNode? l in g["layers"]?.AsArray() ?? new JsonArray())
                {
                    if (l?["name"]?.GetValue<string>() is string tn) texNames.Add(tn);
                }
            }
            var root = new JsonObject { ["source"] = $"{h.Char} (Metroid Prime 4: Beyond, own skeleton -- the owner's own dump)", ["materials"] = gx };
            File.WriteAllText(Path.Combine(outDir, "body.gx.json"), root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (string tn in texNames)
            {
                string from = Path.Combine(lookDir, tn + ".png");
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(outDir, tn + ".png"), true);
                    result.Textures++;
                }
                else Say(0.9f, $"  missing texture {tn}");
            }
            Directory.Delete(lookDir, true);
            var cfg = new StringBuilder();
            cfg.Append("height ").Append((hi - lo).ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
            cfg.Append("feet ").Append(poseLo.ToString("F4", CultureInfo.InvariantCulture)).Append('\n');
            cfg.Append("root ").Append(sNames[order[0]]).Append('\n');
            cfg.Append("game mp4\n");
            File.WriteAllText(Path.Combine(outDir, "body.cfg"), cfg.ToString());
            Say(1f, $"body.gx.json: {gx.Count} draws, {result.Textures} textures");
            return result;
        }

        static double Sq(double x) => x * x;

        static Dictionary<string, int> JointMap(ChprAnimation an)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < an.Joints.Length; i++) d.TryAdd(an.Joints[i], i);
            return d;
        }
    }
}
