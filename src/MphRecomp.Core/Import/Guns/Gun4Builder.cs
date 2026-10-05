using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MphRecomp.Import.Beyond;

namespace MphRecomp.Import.Guns
{
    // Metroid Prime 4: Beyond's first-person arm cannon WITH Beyond's own animations, as MPHGUN2 (port of
    // brawl_extract/rig_work/gun/scripts/gun_export4b.py; everything it does after reading its inputs):
    //   inputs  the model's geometry + skin (IBeyondModelSource), its converted look (IBeyondLookSource: per-corner
    //           normals/UVs, gx.json materials, textures), Samus's character project (ChprCharacter: the game's
    //           skeleton, bind matrices and decoded animations; joints are matched by NAME)
    //   writes  gun.bin      "MPHGUN2": bones (parent, full inverse bind matrix, name) + per-draw skinned vertices
    //                        (GxShader layout, palettes of at most 32 bones) + the recipe's roles baked per frame as
    //                        local rotation xyzw / translation / scale per bone
    //           gun.gx.json  one look material per draw
    //           gun.cfg      "scale", "glow <texture unit>" (the incandescence stage), "root <16 floats>" (the idle pose
    //                        laid along Prime 1's gun), "muzzle 0 0 0.583"
    //           <tex>.png    the textures the draws use
    // What is drawn: the model's LOD list names its meshes twice (a full list and a cheaper regrouping, glTF Mesh0-12 and
    // Mesh13-22 in the default suit); "auto" keeps the first list without its last mesh (an effect shell over the barrel
    // front, the "fuzz"). The overlay meshes that redraw triangles with an emissive material keep only the first copy.
    // The left arm and hand (the model carries both arms) and the shoulders go.
    // Animations: a gun animation carries only the cannon's joints; the arm joints it leaves alone hold the idle
    // animation's first frame, the rest hold the rest pose.
    // Placement: in the idle pose, the barrel axis (the gun joints' principal axis, pointing away from the shoulder), the
    // muzzle (the front tip's centre), the top rail (up) and the length map onto Prime 1's gun: barrel on MPH gun space +Z,
    // muzzle at LBEAM (0, 0, 0.583), back at z 0.022 -- gun.cfg "root", applied before MPH's own gun matrix.
    // Matrices are 4x4 row major for column vectors (ChprMath), as the Python's numpy arrays; gun.bin and gun.cfg store
    // the row-vector transposes OpenTK reads.
    public static class Gun4Builder
    {
        public const string SourceText = "Beyond first-person arm (Prime 4 Model Dumper glTF + decoded CHPR animations)";
        public const string ScaleText = "0.5973";      // gun.cfg "scale": the gun's size in MPH's gun space
        public const double MuzzleZ = 0.583, BackZ = 0.022;   // Prime 1's gun on gun-space +Z: LBEAM, back of the forearm
        public const int MaxPalette = 32;               // bones one draw may name (GxShader)
        // the left arm, hand and the shoulders (by a triangle's main joint)
        static readonly Regex Dropped = new(@"shoulder|_l_bind|arm_L0_forearm|(thumb|index|middle|pinky|ring)_L\d_|meta_L\d|wrist_l",
            RegexOptions.CultureInvariant);
        // the colour combiner of the look's incandescence stage (ADD of the texture over the lit colour): its texture
        // unit takes MPH's per-weapon light colour
        static readonly int[] GlowCombiner = { 15, 8, 12, 0 };

        sealed class Tri
        {
            public int Material;                     // look material index
            public int Main;                         // main joint (skeleton index): most total weight over the 3 corners
            public double[] Pos = new double[9];
            public double[] Nrm = new double[9];
            public double[] Uv = new double[6];
            public List<(int Joint, double Weight)>[] Skin = new List<(int, double)>[3];
        }

        public static GunImporter.Result Build(IBeyondModelSource model, IBeyondLookSource look, ChprCharacter character, Gun4Recipe recipe,
            string outDir, Action<float, string>? progress = null, CancellationToken ct = default)
        {
            var result = new GunImporter.Result();
            void Say(float f, string msg)
            {
                result.Log.Add(msg);
                progress?.Invoke(f, msg);
            }
            Directory.CreateDirectory(outDir);
            ChprSkeleton sk = character.Skeleton;
            string[] sNames = sk.Names;
            // the parents the bind matrices (RestWorld) were composed along (ChprSkeleton.Parent), so the posed rest
            // reproduces them; differs from ParentCorrected on no first-person joint
            int[] sParent = sk.Parent;
            IReadOnlyList<BeyondMesh> meshes = model.Meshes;
            IReadOnlyList<BeyondLookMaterial> mats = look.Materials;

            // ---- which meshes are drawn
            var allowed = new HashSet<int>();
            if (recipe.Meshes == "auto")
            {
                int split = AutoSplit(meshes);
                for (int m = 0; m < split - 1; m++) allowed.Add(meshes[m].Number);
                Say(0.02f, $"mesh lists: {meshes[0].Number}-{meshes[split - 1].Number} and {meshes[split].Number}-{meshes[^1].Number}; "
                    + $"drawing {meshes[0].Number}-{meshes[split - 2].Number} (Mesh{meshes[split - 1].Number} is the effect shell)");
            }
            else
            {
                foreach (string part in recipe.Meshes.Split(','))
                {
                    string[] lh = part.Split('-');
                    int lo = Int32.Parse(lh[0].Trim(), CultureInfo.InvariantCulture);
                    int hi = lh.Length > 1 && lh[1].Trim().Length > 0 ? Int32.Parse(lh[1].Trim(), CultureInfo.InvariantCulture) : lo;
                    for (int m = lo; m <= hi; m++) allowed.Add(m);
                }
                Say(0.02f, $"drawing meshes {recipe.Meshes}");
            }
            ct.ThrowIfCancellationRequested();

            // ---- the model's LOD0 triangles matched one to one with the look's corners, with their skin
            var tris = new List<Tri>();
            var seen = new HashSet<TriKey>();
            int dropOther = 0, dropArm = 0, dropOverlay = 0;
            List<BeyondMesh> lod0 = meshes.Where(m => m.Lod == 0).ToList();
            for (int gi = 0; gi < mats.Count; gi++)
            {
                BeyondLookMaterial g = mats[gi];
                int corners = g.CornerCount;
                if (g.Uv.Length != 2 * corners) throw new InvalidDataException($"{g.Name}: {corners} corner normals, {g.Uv.Length / 2} UVs");
                int k = 0;
                foreach (BeyondMesh p in lod0)
                {
                    if (p.MaterialId != g.MaterialId) continue;
                    bool drawn = allowed.Contains(p.Number);
                    for (int t = 0; t < p.TriangleCount; t++)
                    {
                        if (3 * (k + 1) > corners)
                        {
                            throw new InvalidDataException($"{g.Name}: the look has {corners / 3} triangles, the model more ({p.Name})");
                        }
                        int kk = k++;
                        if (!drawn)
                        {
                            dropOther++;
                            continue;
                        }
                        var skin = new List<(int, double)>[3];
                        for (int c = 0; c < 3; c++) skin[c] = SkinOf(p, p.Indices[3 * t + c], sk.Index);
                        int main = MainJoint(skin);
                        if (Dropped.IsMatch(sNames[main]))
                        {
                            dropArm++;
                            continue;
                        }
                        if (!seen.Add(TriKey.Of(p.Positions, p.Indices[3 * t], p.Indices[3 * t + 1], p.Indices[3 * t + 2], 1e5)))
                        {
                            dropOverlay++;
                            continue;
                        }
                        var tri = new Tri { Material = gi, Main = main, Skin = skin };
                        for (int c = 0; c < 3; c++)
                        {
                            int v = p.Indices[3 * t + c];
                            for (int i = 0; i < 3; i++)
                            {
                                tri.Pos[3 * c + i] = p.Positions[3 * v + i];
                                tri.Nrm[3 * c + i] = g.Normals[9 * kk + 3 * c + i];
                            }
                            tri.Uv[2 * c] = g.Uv[6 * kk + 2 * c];
                            tri.Uv[2 * c + 1] = g.Uv[6 * kk + 2 * c + 1];
                        }
                        tris.Add(tri);
                    }
                }
                if (3 * k != corners)
                {
                    throw new InvalidDataException($"{g.Name}: the model has {k} triangles of material {g.MaterialId}, the look {corners / 3}");
                }
            }
            if (tris.Count == 0) throw new InvalidDataException("no triangle is drawn");
            Say(0.1f, $"triangles kept {tris.Count}; dropped other set / shell {dropOther}, left arm / shoulders {dropArm}, overlay redraw {dropOverlay}");
            ct.ThrowIfCancellationRequested();

            // ---- bones: every joint the kept vertices use, and their ancestors; parents first
            var need = new HashSet<int>();
            foreach (Tri t in tris)
            {
                foreach (List<(int Joint, double Weight)> ws in t.Skin)
                {
                    foreach ((int j0, _) in ws)
                    {
                        int j = j0;
                        while (j >= 0 && need.Add(j)) j = sParent[j];
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
            Say(0.12f, $"bones {nb}");

            // ---- animations: local rotation xyzw / translation / scale per exported bone per frame
            var anims = new Dictionary<string, ChprAnimation>(StringComparer.Ordinal);
            ChprAnimation Anim(string name)
            {
                if (anims.TryGetValue(name, out ChprAnimation? a)) return a;
                int ai = character.FindAnim(name);
                if (ai < 0) throw new InvalidDataException($"the character project has no animation '{name}'");
                ChprAnimResult r = character.Decode(ai);
                if (!r.Ok || r.Animation == null) throw new InvalidDataException($"animation '{name}': {r.Status} {r.Message}");
                anims[name] = r.Animation;
                return r.Animation;
            }
            ChprAnimation idle = Anim(recipe.IdleAnim);
            Dictionary<string, int> idleAt = JointMap(idle);
            float[] FramesFor(ChprAnimation an, int n)
            {
                Dictionary<string, int> at = JointMap(an);
                int ja = an.Joints.Length, ji = idle.Joints.Length;
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
            var baked = new List<(string Role, float[] F, int Frames, double Fps, string Src)>();
            float[]? idleF = null;
            foreach ((string role, string src) in recipe.Roles)
            {
                ct.ThrowIfCancellationRequested();
                ChprAnimation an = Anim(src);
                int n = role == recipe.Idle ? 1 : an.Frames;
                float[] F = FramesFor(an, n);
                if (role == recipe.Idle) idleF = F;
                baked.Add((role, F, n, an.FpsAssumed, src));
            }
            if (idleF == null) throw new InvalidDataException($"the idle role '{recipe.Idle}' is not in the recipe's roles");
            Say(0.3f, $"animations: {baked.Count} roles");

            // ---- the idle pose -> placement along Prime 1's gun
            var Wb = new double[nb][];
            for (int bi = 0; bi < nb; bi++)
            {
                int o = bi * 10;
                // the rotation is built in float32 like the Python's (its frames are a float32 array)
                float[] r = QMat32(idleF[o], idleF[o + 1], idleF[o + 2], idleF[o + 3]);
                var L = new double[16];
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++) L[4 * i + j] = r[3 * i + j] * idleF[o + 7 + j];
                    L[4 * i + 3] = idleF[o + 4 + i];
                }
                L[15] = 1;
                int pj = sParent[order[bi]];
                Wb[bi] = pj >= 0 ? ChprMath.MatMul(Wb[bix[pj]], L) : L;
            }
            var IB = new double[nb][];
            var skinM = new double[nb][];
            for (int bi = 0; bi < nb; bi++)
            {
                IB[bi] = ChprMath.Invert(sk.RestWorld[order[bi]]);
                skinM[bi] = ChprMath.MatMul(Wb[bi], IB[bi]);
            }
            // every kept corner in the idle pose
            var PP = new double[tris.Count * 9];
            for (int ti = 0; ti < tris.Count; ti++)
            {
                Tri t = tris[ti];
                for (int c = 0; c < 3; c++)
                {
                    double x = t.Pos[3 * c], y = t.Pos[3 * c + 1], z = t.Pos[3 * c + 2];
                    double ox = 0, oy = 0, oz = 0;
                    foreach ((int j, double wt) in t.Skin[c])
                    {
                        double[] M = skinM[bix[j]];
                        ox += wt * MulPoint(M, 0, x, y, z);
                        oy += wt * MulPoint(M, 1, x, y, z);
                        oz += wt * MulPoint(M, 2, x, y, z);
                    }
                    PP[9 * ti + 3 * c] = ox; PP[9 * ti + 3 * c + 1] = oy; PP[9 * ti + 3 * c + 2] = oz;
                }
            }
            var gunPts = new List<int>();     // corner indices into PP / 3
            var topPts = new List<int>();
            for (int ti = 0; ti < tris.Count; ti++)
            {
                string mj = sNames[tris[ti].Main];
                if (mj.StartsWith("fps_gun", StringComparison.Ordinal)) gunPts.AddRange(new[] { 3 * ti, 3 * ti + 1, 3 * ti + 2 });
                if (mj.Contains("top_rail", StringComparison.Ordinal) || mj.Contains("top_panel", StringComparison.Ordinal)) topPts.AddRange(new[] { 3 * ti, 3 * ti + 1, 3 * ti + 2 });
            }
            if (gunPts.Count == 0) throw new InvalidDataException("no triangle rides an fps_gun joint: no barrel to place");
            if (topPts.Count == 0) throw new InvalidDataException("no triangle rides a top_rail / top_panel joint: no up direction");
            double[] cen = Mean(PP, gunPts);
            double[] elbow = sk.Index.TryGetValue("fps_gun_bind", out int gb) && bix.TryGetValue(gb, out int gbi) ? Translation(Wb[gbi]) : cen;
            double[] ax = PrincipalAxis(PP, gunPts, cen);
            // the barrel points away from the shoulder
            double[] behind = sk.Index.TryGetValue("fps_shoulder_r_bind", out int sh) && bix.TryGetValue(sh, out int shi) ? Translation(Wb[shi]) : elbow;
            if (Dot(Sub(cen, behind), ax) < 0) ax = new[] { -ax[0], -ax[1], -ax[2] };
            int np = tris.Count * 3;
            var proj = new double[np];
            double front = Double.NegativeInfinity, back = Double.PositiveInfinity;
            for (int i = 0; i < np; i++)
            {
                proj[i] = (PP[3 * i] - cen[0]) * ax[0] + (PP[3 * i + 1] - cen[1]) * ax[1] + (PP[3 * i + 2] - cen[2]) * ax[2];
                front = Math.Max(front, proj[i]);
                back = Math.Min(back, proj[i]);
            }
            double cut = front - 0.02 * (front - back);
            var tip = new List<int>();
            for (int i = 0; i < np; i++)
            {
                if (proj[i] > cut) tip.Add(i);
            }
            double[] tc = Sub(Mean(PP, tip), cen);
            double ta = Dot(tc, ax);
            double[] off = { tc[0] - ta * ax[0], tc[1] - ta * ax[1], tc[2] - ta * ax[2] };
            double[] muzzle = { cen[0] + ax[0] * front + off[0], cen[1] + ax[1] * front + off[1], cen[2] + ax[2] * front + off[2] };
            double[] up = Sub(Mean(PP, topPts), cen);
            double ua = Dot(up, ax);
            up = new[] { up[0] - ua * ax[0], up[1] - ua * ax[1], up[2] - ua * ax[2] };
            double un = Math.Sqrt(up[0] * up[0] + up[1] * up[1] + up[2] * up[2]);
            up = new[] { up[0] / un, up[1] / un, up[2] / un };
            double[] X = { up[1] * ax[2] - up[2] * ax[1], up[2] * ax[0] - up[0] * ax[2], up[0] * ax[1] - up[1] * ax[0] };
            double scale = (MuzzleZ - BackZ) / (front - back);
            // row-vector root: v' = ((v - muzzle) [X up axis]) k + (0, 0, MuzzleZ)
            double[] rrow = Identity(), troot = Identity(), sroot = Identity(), tout = Identity();
            for (int i = 0; i < 3; i++)
            {
                rrow[4 * i] = X[i]; rrow[4 * i + 1] = up[i]; rrow[4 * i + 2] = ax[i];
                troot[12 + i] = -muzzle[i];
                sroot[5 * i] = scale;
            }
            tout[14] = MuzzleZ;
            double[] root = ChprMath.MatMul(ChprMath.MatMul(ChprMath.MatMul(troot, rrow), sroot), tout);
            Say(0.4f, String.Format(CultureInfo.InvariantCulture, "idle-pose cannon: length {0:F3} -> x{1:F4}, axis ({2:F3}, {3:F3}, {4:F3}), up ({5:F3}, {6:F3}, {7:F3})",
                front - back, scale, ax[0], ax[1], ax[2], up[0], up[1], up[2]));
            ct.ThrowIfCancellationRequested();

            // ---- gun.bin (MPHGUN2)
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
                // the row-vector layout (the transpose), row major -> OpenTK Matrix4(m11..m44)
                for (int c = 0; c < 4; c++)
                {
                    for (int r = 0; r < 4; r++) w.Write((float)IB[bi][4 * r + c]);
                }
                Name(sNames[order[bi]]);
            }
            // draws: per look material (in its order), split so that no draw names more than MaxPalette bones
            const float whiteBa = (float)(255 + 0.999);
            var byMat = new Dictionary<string, List<Tri>>(StringComparer.Ordinal);
            foreach (Tri t in tris)
            {
                string mn = mats[t.Material].Name;
                if (!byMat.TryGetValue(mn, out List<Tri>? l)) byMat[mn] = l = new List<Tri>();
                l.Add(t);
            }
            var chunks = new List<(string Material, List<int> Palette, List<float> Rows)>();
            foreach (BeyondLookMaterial g in mats)
            {
                if (!byMat.TryGetValue(g.Name, out List<Tri>? list)) continue;
                var pal = new List<int>();
                var palSet = new HashSet<int>();
                var rows = new List<float>();
                foreach (Tri t in list)
                {
                    var bones = new HashSet<int>();
                    foreach (List<(int Joint, double Weight)> ws in t.Skin)
                    {
                        foreach ((int j, _) in ws) bones.Add(bix[j]);
                    }
                    if (palSet.Count + bones.Count(x => !palSet.Contains(x)) > MaxPalette)
                    {
                        chunks.Add((g.Name, pal, rows));
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
                        List<(int Joint, double Weight)> ws = t.Skin[c].OrderBy(x => -x.Weight).Take(4).ToList();
                        double tot = 0;
                        foreach ((_, double wt) in ws) tot += wt;
                        double nx = t.Nrm[3 * c], ny = t.Nrm[3 * c + 1], nz = t.Nrm[3 * c + 2];
                        double nn = Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12;
                        rows.Add((float)t.Pos[3 * c]); rows.Add((float)t.Pos[3 * c + 1]); rows.Add((float)t.Pos[3 * c + 2]);
                        rows.Add((float)(nx / nn)); rows.Add((float)(ny / nn)); rows.Add((float)(nz / nn));
                        rows.Add((float)t.Uv[2 * c]); rows.Add((float)t.Uv[2 * c + 1]);
                        rows.Add(1f); rows.Add(1f); rows.Add(whiteBa);
                        for (int i = 0; i < 4; i++) rows.Add(i < ws.Count ? pal.IndexOf(bix[ws[i].Joint]) : 0f);
                        for (int i = 0; i < 4; i++) rows.Add(i < ws.Count ? (float)(ws[i].Weight / tot) : 0f);
                    }
                }
                chunks.Add((g.Name, pal, rows));
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
            foreach ((string role, float[] F, int n, double fps, string src) in baked)
            {
                double tick = 1.0 / fps;
                Name(role);
                w.Write((float)(n > 1 ? Math.Max(n - 1, 1) * tick : 0.0));
                w.Write((float)tick);
                w.Write((uint)n);
                foreach (float f in F) w.Write(f);
                result.Log.Add($"  {role,-20} <- {src} ({n} frames @ {fps.ToString(CultureInfo.InvariantCulture)} fps)");
            }
            w.Flush();
            File.WriteAllBytes(Path.Combine(outDir, "gun.bin"), ms.ToArray());
            result.Bones = nb; result.Materials = chunks.Count; result.Animations = baked.Count; result.Bytes = (int)ms.Length;
            Say(0.8f, $"gun.bin: {nb} bones, {chunks.Count} draws, {tris.Count} triangles, {ms.Length / 1048576.0:F1} MB");
            ct.ThrowIfCancellationRequested();

            // ---- gun.gx.json (one entry per draw) + textures; gun.cfg
            var gx = new List<object?>();
            foreach ((string mn, _, _) in chunks)
            {
                BeyondLookMaterial g = mats.First(m => m.Name == mn);
                if (g.Gx == null) throw new InvalidDataException($"{mn}: the look has no gx material");
                gx.Add(g.Gx);
            }
            File.WriteAllText(Path.Combine(outDir, "gun.gx.json"), PyJson.Dump(new PyJson.Obj { { "source", SourceText }, { "materials", gx } }));
            var texNames = new SortedSet<string>(StringComparer.Ordinal);
            var glow = new SortedSet<int>();
            foreach (object? m in gx)
            {
                foreach (object? l in Items(Field(m, "layers"))) texNames.Add(Field(l, "name") as string ?? throw new InvalidDataException("a gx layer has no name"));
                foreach (object? s in Items(Field(m, "stages")))
                {
                    List<object?> cc = Items(Field(s, "c"));
                    double tm = Number(Field(s, "texmap"));
                    if (cc.Count == GlowCombiner.Length && cc.Select(Number).SequenceEqual(GlowCombiner.Select(x => (double)x)) && tm >= 0) glow.Add((int)tm);
                }
            }
            foreach (string tn in texNames)
            {
                ct.ThrowIfCancellationRequested();
                if (look.WriteTexture(tn, Path.Combine(outDir, tn + ".png"))) result.Textures++;
                else Say(0.9f, $"  missing texture {tn}");
            }
            var cfg = new StringBuilder();
            cfg.Append("scale ").Append(ScaleText).Append('\n');
            // (the Python takes set.pop() of the units: the lowest, for GX's 0-7)
            if (glow.Count > 0) cfg.Append("glow ").Append(glow.Min.ToString(CultureInfo.InvariantCulture)).Append('\n');
            cfg.Append("root ").Append(String.Join(" ", root.Select(v => FormatG(v, 7)))).Append('\n');
            cfg.Append("muzzle 0 0 ").Append(FormatG(MuzzleZ, 7)).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "gun.cfg"), cfg.ToString());
            result.Scale = Double.Parse(ScaleText, CultureInfo.InvariantCulture);
            Say(1f, $"gun.gx.json: {gx.Count} draws, {result.Textures} textures");
            return result;
        }

        // ---- the mesh lists: the first k meshes and the rest cover the same triangles (all but 1%) -> k

        static int AutoSplit(IReadOnlyList<BeyondMesh> meshes)
        {
            int n = meshes.Count;
            var span = new Dictionary<TriKey, (int First, int Last)>();
            for (int m = 0; m < n; m++)
            {
                BeyondMesh p = meshes[m];
                for (int t = 0; t < p.TriangleCount; t++)
                {
                    TriKey key = TriKey.Of(p.Positions, p.Indices[3 * t], p.Indices[3 * t + 1], p.Indices[3 * t + 2], 1e4);
                    span[key] = span.TryGetValue(key, out (int First, int Last) s) ? (s.First, m) : (m, m);
                }
            }
            int total = span.Count;
            for (int k = 1; k < n; k++)
            {
                int x = 0;
                foreach ((int first, int last) in span.Values)
                {
                    if ((first < k) != (last >= k)) x++;
                }
                if (x < 0.01 * total)
                {
                    if (k < 2) throw new InvalidDataException("auto mesh split: the first mesh list has a single mesh (no effect shell to drop)");
                    return k;
                }
            }
            throw new InvalidDataException("auto mesh split: no two mesh lists covering the same triangles (give the recipe's meshes)");
        }

        // a triangle by its corners' positions rounded to `scale` (numpy's round: rint(x * 10^d)), corners sorted
        readonly struct TriKey : IEquatable<TriKey>
        {
            readonly long _a0, _a1, _a2, _b0, _b1, _b2, _c0, _c1, _c2;

            TriKey(long[] v)
            {
                _a0 = v[0]; _a1 = v[1]; _a2 = v[2]; _b0 = v[3]; _b1 = v[4]; _b2 = v[5]; _c0 = v[6]; _c1 = v[7]; _c2 = v[8];
            }

            public static TriKey Of(float[] P, int i0, int i1, int i2, double scale)
            {
                long R(int v, int c) => (long)Math.Round(P[3 * v + c] * scale, MidpointRounding.ToEven);
                var cs = new[] { (R(i0, 0), R(i0, 1), R(i0, 2)), (R(i1, 0), R(i1, 1), R(i1, 2)), (R(i2, 0), R(i2, 1), R(i2, 2)) };
                Array.Sort(cs);
                return new TriKey(new[] { cs[0].Item1, cs[0].Item2, cs[0].Item3, cs[1].Item1, cs[1].Item2, cs[1].Item3, cs[2].Item1, cs[2].Item2, cs[2].Item3 });
            }

            public bool Equals(TriKey o) => _a0 == o._a0 && _a1 == o._a1 && _a2 == o._a2 && _b0 == o._b0 && _b1 == o._b1 && _b2 == o._b2
                && _c0 == o._c0 && _c1 == o._c1 && _c2 == o._c2;

            public override bool Equals(object? o) => o is TriKey k && Equals(k);

            public override int GetHashCode() => HashCode.Combine(HashCode.Combine(_a0, _a1, _a2), HashCode.Combine(_b0, _b1, _b2), HashCode.Combine(_c0, _c1, _c2));
        }

        // ---- skin

        // a vertex's influences with weight > 0 as (skeleton index, weight), in the model's slot order
        static List<(int, double)> SkinOf(BeyondMesh p, int v, Dictionary<string, int> skel)
        {
            int K = p.InfluencesPerVertex;
            var l = new List<(int, double)>(K);
            for (int s = 0; s < K; s++)
            {
                float wt = p.Weights[v * K + s];
                if (!(wt > 0)) continue;
                string nm = p.JointNames[p.Joints[v * K + s]];
                if (!skel.TryGetValue(nm, out int j)) throw new InvalidDataException($"{p.Name}: joint '{nm}' is not in the character's skeleton");
                l.Add((j, wt));
            }
            return l;
        }

        // the joint with the most total weight over the corners (the first such, in first-seen order)
        static int MainJoint(List<(int Joint, double Weight)>[] skin)
        {
            var seenOrder = new List<int>();
            var sum = new Dictionary<int, double>();
            foreach (List<(int Joint, double Weight)> ws in skin)
            {
                foreach ((int j, double wt) in ws)
                {
                    if (sum.TryGetValue(j, out double s)) sum[j] = s + wt;
                    else
                    {
                        sum[j] = wt;
                        seenOrder.Add(j);
                    }
                }
            }
            if (seenOrder.Count == 0) throw new InvalidDataException("a triangle has no skin");
            int best = seenOrder[0];
            foreach (int j in seenOrder)
            {
                if (sum[j] > sum[best]) best = j;
            }
            return best;
        }

        static Dictionary<string, int> JointMap(ChprAnimation a)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < a.Joints.Length; i++) d[a.Joints[i]] = i;
            return d;
        }

        // ---- math (column vectors, 4x4 row major)

        // the Python's qmat on float32 components: 3x3 row major, not normalized
        static float[] QMat32(float x, float y, float z, float w) => new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
        };

        static double MulPoint(double[] M, int row, double x, double y, double z)
        {
            double s = M[4 * row] * x;
            s = Math.FusedMultiplyAdd(M[4 * row + 1], y, s);
            s = Math.FusedMultiplyAdd(M[4 * row + 2], z, s);
            return s + M[4 * row + 3];
        }

        static double[] Identity()
        {
            var m = new double[16];
            m[0] = m[5] = m[10] = m[15] = 1;
            return m;
        }

        static double[] Translation(double[] m) => new[] { m[3], m[7], m[11] };

        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        static double[] Mean(double[] pts, List<int> which)
        {
            double x = 0, y = 0, z = 0;
            foreach (int i in which)
            {
                x += pts[3 * i]; y += pts[3 * i + 1]; z += pts[3 * i + 2];
            }
            return new[] { x / which.Count, y / which.Count, z / which.Count };
        }

        // the points' principal direction about their centre (the SVD's first right singular vector, up to sign): the
        // eigenvector of the largest eigenvalue of their scatter matrix (cyclic Jacobi)
        static double[] PrincipalAxis(double[] pts, List<int> which, double[] c)
        {
            var a = new double[3, 3];
            foreach (int i in which)
            {
                double dx = pts[3 * i] - c[0], dy = pts[3 * i + 1] - c[1], dz = pts[3 * i + 2] - c[2];
                a[0, 0] += dx * dx; a[0, 1] += dx * dy; a[0, 2] += dx * dz;
                a[1, 1] += dy * dy; a[1, 2] += dy * dz; a[2, 2] += dz * dz;
            }
            a[1, 0] = a[0, 1]; a[2, 0] = a[0, 2]; a[2, 1] = a[1, 2];
            var v = new double[3, 3];
            v[0, 0] = v[1, 1] = v[2, 2] = 1;
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double offd = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (offd <= 1e-300 || offd <= 1e-18 * (Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]))) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (a[p, q] == 0) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double cs = 1 / Math.Sqrt(t * t + 1), sn = t * cs;
                        for (int r = 0; r < 3; r++)
                        {
                            double arp = a[r, p], arq = a[r, q];
                            a[r, p] = cs * arp - sn * arq;
                            a[r, q] = sn * arp + cs * arq;
                        }
                        for (int r = 0; r < 3; r++)
                        {
                            double apr = a[p, r], aqr = a[q, r];
                            a[p, r] = cs * apr - sn * aqr;
                            a[q, r] = sn * apr + cs * aqr;
                        }
                        for (int r = 0; r < 3; r++)
                        {
                            double vrp = v[r, p], vrq = v[r, q];
                            v[r, p] = cs * vrp - sn * vrq;
                            v[r, q] = sn * vrp + cs * vrq;
                        }
                    }
                }
            }
            int best = 0;
            for (int i = 1; i < 3; i++)
            {
                if (a[i, i] > a[best, best]) best = i;
            }
            double nx = v[0, best], ny = v[1, best], nz = v[2, best], n = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return new[] { nx / n, ny / n, nz / n };
        }

        // ---- the look's gx trees (PyJson.Obj / List<object?> / numbers)

        static object? Field(object? o, string key)
        {
            if (o is PyJson.Obj obj)
            {
                foreach (KeyValuePair<string, object?> kv in obj)
                {
                    if (kv.Key == key) return kv.Value;
                }
            }
            throw new InvalidDataException($"gx material: no '{key}'");
        }

        static List<object?> Items(object? o) => o is System.Collections.IEnumerable e && o is not string
            ? e.Cast<object?>().ToList() : throw new InvalidDataException("gx material: a list expected");

        static double Number(object? o) => o switch
        {
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            _ => throw new InvalidDataException("gx material: a number expected"),
        };

        // Python's format(v, ".<p>g")
        public static string FormatG(double v, int p)
        {
            if (Double.IsNaN(v)) return "nan";
            if (Double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            if (v == 0) return BitConverter.DoubleToInt64Bits(v) < 0 ? "-0" : "0";
            string e = v.ToString("E" + (p - 1), CultureInfo.InvariantCulture);     // "-d.dddddE+ddd", correctly rounded
            int ei = e.IndexOf('E');
            int exp = Int32.Parse(e[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            string mant = e[..ei];
            bool neg = mant[0] == '-';
            string digits = mant.TrimStart('-').Replace(".", "");
            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            if (exp < -4 || exp >= p)
            {
                string m = digits.TrimEnd('0');
                sb.Append(m[0]);
                if (m.Length > 1) sb.Append('.').Append(m, 1, m.Length - 1);
                sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
            string s;
            if (exp >= 0)
            {
                s = digits[..(exp + 1)] + (digits.Length > exp + 1 ? "." + digits[(exp + 1)..] : "");
            }
            else
            {
                s = "0." + new string('0', -exp - 1) + digits;
            }
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return sb.Append(s).ToString();
        }
    }
}
