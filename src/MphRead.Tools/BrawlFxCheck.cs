using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using MphRecomp.Import;
using MphRecomp.Import.Brawl;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRead
{
    // -brawlfxcheck <ef_szerosuit.pac> <reference dir>: the clean-room equivalence harness for docs/cleanroom/
    // E_brawl_effects_spec.md (Zero Suit beam + alt form, docs/BRAWL_PARALYZER.md). Runs MphRecomp.Import.Brawl on the
    // player's own Brawl file and compares its output with reference exports kept outside the implementer's view.
    //   part 1: every texture (resource-file TEX0 + particle REFT) pixel-equal to <reference>/<name>.png ('#' -> '_');
    //           the reference stores I4/I8 with alpha 255, as the spec's required output does
    //   part 2: every mesh with <reference>/<mesh>.dae: triangles (positions x bind matrix, reversed winding, normals x
    //           bind 3x3 not renormalised, (s, 1 - t)) as the same set; every CHR0 in chr_ref.txt within 0.001 per frame
    //   part 3: every effect's emitter/particle/animation fields equal to <reference>/reff_values.txt; the effect list
    internal static class BrawlFxCheck
    {
        public static void Run(string[] args)
        {
            if (args.Length >= 4 && args[1] == "import")
            {
                Import(args[2], args[3]);
                return;
            }
            if (args.Length >= 3 && args[1] == "dump")
            {
                Dump(args[2], args.Length >= 4 ? args[3] : "Paralyzer");
                return;
            }
            if (args.Length < 3)
            {
                Console.WriteLine("usage: -brawlfxcheck <ef_szerosuit.pac> <reference dir>");
                return;
            }
            string pac = args[1], refDir = args[2];
            var errors = new List<string>();
            BrawlArchive archive = BrawlArchive.Read(File.ReadAllBytes(pac));
            Console.WriteLine($"archive '{archive.Name}' v{archive.Version:X}: {archive.Entries.Count} entries");
            foreach (BrawlArchiveEntry e in archive.Entries)
            {
                Console.WriteLine($"  [{e.Number}] kind {e.Kind} index {e.Index} {e.Magic} {e.Length} bytes");
            }
            Textures(archive, refDir, errors);
            Meshes(archive, refDir, errors);
            EffectFiles(archive, refDir, errors);
            foreach (string e in errors) Console.WriteLine("  FAIL " + e);
            Console.WriteLine(errors.Count == 0 ? "PASS brawlfxcheck" : $"FAIL brawlfxcheck ({errors.Count})");
        }

        // -brawlfxcheck import <Brawl disc image or extracted folder> <output root>: the recipes/brawl items through the app's
        // ImportService (the folder must hold effect/; a plain .iso needs the Wii key file the app loads, not this tool)
        // -brawlfxcheck dump <pac> [name filter]: what the runtime needs to know about each matching effect, readable
        private static void Dump(string pac, string filter)
        {
            BrawlArchive archive = BrawlArchive.Read(File.ReadAllBytes(pac));
            BrawlEffectFile file = BrawlEffects.Read(archive.DataOf(archive.Entries.First(e => e.Magic == "REFF")));
            static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
            static string V(Vector3 v) => $"({F(v.X)},{F(v.Y)},{F(v.Z)})";
            foreach (BrawlArchiveEntry ae in archive.Entries.Where(x => x.Magic == "bres"))
            {
                BrawlResFile res = BrawlResFile.Read(archive.DataOf(ae));
                if (!res.Folders.Any(f => f.Name == BrawlResFile.Models)) continue;
                string tmp = Path.Combine(Path.GetTempPath(), $"brawlfx_{ae.Number}.brres");
                File.WriteAllBytes(tmp, archive.DataOf(ae).ToArray());
                foreach (BrawlMesh mesh in BrawlMesh.ReadAll(res))
                {
                    Console.WriteLine($"## entry [{ae.Number}] mesh {mesh.Name}: materials [{string.Join(",", mesh.Materials)}]");
                    foreach (BrawlBone b in mesh.Bones) Console.WriteLine($"  bone {b.Number} {b.Name} facing {b.FacingMode} S{V(b.Scale)} R{V(b.Rotation)} T{V(b.Translation)} parent {b.Parent}");
                    foreach (BrawlDrawStep st in mesh.DrawSteps)
                    {
                        BrawlMeshObject o = mesh.ObjectByNumber(st.Object)!;
                        Vector3[] ps = mesh.PositionArrayByNumber(o.PositionArray)!.Values.ToArray();
                        Vector3 lo = ps.Aggregate(Vector3.Min), hi = ps.Aggregate(Vector3.Max);
                        Console.WriteLine($"  draw {st.MaterialName} obj {o.Name} bone {st.Bone} prio {st.Priority} seethrough {st.SeeThrough} tris {o.Triangles.Count} box {V(lo)}..{V(hi)}");
                    }
                }
                if (res.Folders.Any(f => f.Name == BrawlResFile.MeshAnimations))
                {
                    foreach (BrawlMeshAnimation a in BrawlMeshAnimation.ReadAll(res)) Console.WriteLine($"  anim {a.Name} frames {a.FrameCount} loops {a.Loops} bones [{string.Join(",", a.Bones)}]");
                }
                foreach (MphRecomp.Assets.GxMaterial m in MphRecomp.Assets.Mdl0Gx.Read(tmp))
                {
                    Console.WriteLine($"  mat {m.Name}: stages {m.NumTevStage} blend {m.BlendEnable} {m.BlendSrc}/{m.BlendDst} sub {m.BlendSubtract} depth {m.DepthTest}/{m.DepthWrite} cull {m.CullMode} "
                        + $"chan lit {m.Lit} mat ({m.MatColor},{F(m.MatColorA)}) tex [{string.Join(",", m.TexNames.Where(t => t != null))}]");
                    Console.WriteLine($"    reg {string.Join(" ", m.Reg)} konst {string.Join(" ", m.Konst)}");
                    for (int i = 0; i < m.NumTevStage; i++)
                    {
                        var t = m.Stages[i];
                        Console.WriteLine($"    st{i} tex {t.TexMap} ras {t.RasChan} c {t.Ca},{t.Cb},{t.Cc},{t.Cd} op {t.Cop} sc {t.Cscale} -> {t.Cdest} | a {t.Aa},{t.Ab},{t.Ac},{t.Ad} op {t.Aop} -> {t.Adest} | k {t.KcSel:X}/{t.KaSel:X}");
                    }
                    for (int i = 0; i < 8; i++) if (m.Layers[i] is { } L) Console.WriteLine($"    layer{i} {L.Name} wrap {L.WrapS}/{L.WrapT} scale {L.Scale} trans {L.Trans} rot {F(L.RotDeg)} map {L.MapMode}");
                }
            }
            foreach (BrawlEffect fx in file.Effects.Where(f => f.Name.Contains(filter)))
            {
                BrawlEmitter e = fx.Emitter;
                BrawlParticle p = fx.Particle;
                Console.WriteLine($"== {fx.Name}");
                Console.WriteLine($"  emit flags 0x{e.EmissionFlags:X} general 0x{e.GeneralFlags:X} life {e.EmitterLife} ptcl life {e.ParticleLife} "
                    + $"(rnd {e.ParticleLifeRandom}%) count {F(e.EmitCount)} (rnd {e.EmitCountRandom}%) start {e.EmitStartFrame} ahead {e.RunAheadFrames} "
                    + $"interval {e.EmitInterval} (rnd {e.EmitIntervalRandom}%) follow {e.ParticleFollow}/{e.ChildParticleFollow}/{e.ChildEmitterFollow} div {e.EmitSubdivision}");
                Console.WriteLine($"  shape [{string.Join(",", e.ShapeParams.Select(F))}] speed centre {F(e.SpeedFromCentre)} yaxis {F(e.SpeedFromYAxis)} "
                    + $"random {F(e.SpeedRandomDirection)} normal {F(e.SpeedAlongNormal)}/{F(e.NormalSpread)} dir {F(e.SpeedAlongDirection)}/{F(e.DirectionSpread)} "
                    + $"{V(e.Direction)} speed rnd {e.InitialSpeedRandom}% momentum rnd {e.MomentumRandom}%");
                Console.WriteLine($"  emitter S{V(e.EmitterScale)} R{V(e.EmitterRotation)} T{V(e.EmitterTranslation)} seed {e.RandomSeed} "
                    + $"draw 0x{e.DrawFlags:X} shape {e.ParticleShape} opts [{string.Join(",", e.ShapeOptions)}] zoff {F(e.DepthOffset)}");
                Console.WriteLine($"  blend {e.BlendMode} src {e.SrcFactor} dst {e.DstFactor} logic {e.LogicOp} depth {e.DepthCompare} acmp {e.AlphaCompare0}/{e.AlphaCompare1} "
                    + $"op {e.AlphaCombine} ref {p.AlphaRef0}/{p.AlphaRef1} colour srcs [{string.Join(",", e.ColourSources)}] konst [{string.Join(",", e.KonstSources)}] "
                    + $"flick {e.AlphaFlickerKind}/{e.AlphaFlickerPeriod}/{e.AlphaFlickerRandom}/{e.AlphaFlickerAmplitude} light {e.LightingMode}");
                for (int s = 0; s < e.TevStageCount; s++)
                {
                    BrawlTevStage t = e.TevStages[s];
                    Console.WriteLine($"  tev{s} c [{string.Join(",", t.ColourInputs)}] op {t.ColourOperation} b {t.ColourBias} s {t.ColourScale} cl {t.ColourClamp} out {t.ColourOutput} "
                        + $"| a [{string.Join(",", t.AlphaInputs)}] op {t.AlphaOperation} b {t.AlphaBias} s {t.AlphaScale} cl {t.AlphaClamp} out {t.AlphaOutput} | k {t.KonstColourSelect}/{t.KonstAlphaSelect}");
                }
                Console.WriteLine($"  ptcl colours [{string.Join(" ", p.Colours.Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}"))}] size ({F(p.Size.X)},{F(p.Size.Y)}) "
                    + $"scale ({F(p.Scale.X)},{F(p.Scale.Y)}) rot {V(p.Rotation)} rot rnd [{string.Join(",", p.RotationRandom)}] rot off {V(p.RotationOffset)}");
                Console.WriteLine($"  textures [{string.Join(",", p.TextureNames)}] tscale {string.Join(" ", p.TextureScale.Select(v => $"({F(v.X)},{F(v.Y)})"))} "
                    + $"trot {V(p.TextureRotation)} ttrans {string.Join(" ", p.TextureTranslation.Select(v => $"({F(v.X)},{F(v.Y)})"))} wrap 0x{p.TextureWrap:X} flip {p.TextureFlip}");
                foreach (BrawlEffectTrack t in fx.ParticleTracks.Concat(fx.EmitterTracks))
                {
                    Console.WriteLine($"  {(t.IsParticleTrack ? "P" : "E")}{t.Index} kind {t.Kind} type {t.ValueType} bits 0x{t.ComponentBits:X} proc 0x{t.ProcessFlags:X} loop {t.LoopCount} "
                        + $"frames {t.FrameCount} keys {t.Keys.Count} ranges {t.RangeCount} names [{string.Join(",", t.Names)}]");
                    foreach (BrawlTrackKey k in t.Keys)
                    {
                        string v = k.Floats != null ? string.Join(",", k.Floats.Select(F)) : Convert.ToHexString(k.RawValue);
                        Console.WriteLine($"      f{k.Frame} A {Convert.ToHexString(k.FlagsA)} B {Convert.ToHexString(k.FlagsB)} = {v}");
                    }
                    if (t.RangesPart.Length > 0) Console.WriteLine($"      ranges {Convert.ToHexString(t.RangesPart)}");
                    if (t.RandomPart.Length > 0) Console.WriteLine($"      random {Convert.ToHexString(t.RandomPart)}");
                    if (t.ExtraPart.Length > 0) Console.WriteLine($"      extra {Convert.ToHexString(t.ExtraPart)}");
                }
            }
        }

        private static void Import(string source, string outRoot)
        {
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
            var svc = new ImportService(outRoot, store);
            ImportReport rep = svc.Import("ssbb", source);
            foreach (ImportItemInfo it in rep.Items) Console.WriteLine($"  {it.Id,-20} {it.Status,-12} {it.Seconds,6:F2} s  {it.Message}");
            bool ok = rep.Items.Count > 0 && rep.Items.All(i => i.Status == ItemImportStatus.Complete);
            Console.WriteLine(ok ? "PASS brawlfxcheck import" : "FAIL brawlfxcheck import");
        }

        private static void Textures(BrawlArchive archive, string refDir, List<string> errors)
        {
            var textures = new List<BrawlTexture>();
            var skipped = new List<BrawlSkippedTexture>();
            foreach (BrawlArchiveEntry e in archive.Entries)
            {
                ReadOnlyMemory<byte> data = archive.DataOf(e);
                if (e.Magic == "REFT")
                {
                    BrawlParticleTextureFile file = BrawlTextures.ReadParticleTextures(data);
                    textures.AddRange(file.Textures);
                    skipped.AddRange(file.Skipped);
                }
                else if (e.Magic == "bres")
                {
                    BrawlResFile res = BrawlResFile.Read(data);
                    if (res.Folders.Any(f => f.Name == BrawlResFile.Textures))
                    {
                        textures.AddRange(BrawlTextures.ReadResources(res, out List<BrawlSkippedTexture> s));
                        skipped.AddRange(s);
                    }
                }
            }
            foreach (BrawlSkippedTexture s in skipped)
            {
                Console.WriteLine($"  texture {s.Name} {BrawlPixels.FormatName(s.Format)}: skipped (colour table)");
            }
            int checkedCount = 0;
            foreach (BrawlTexture t in textures)
            {
                string path = Path.Combine(refDir, t.Name.Replace('#', '_') + ".png");
                if (!File.Exists(path))
                {
                    errors.Add($"texture {t.Name}: no reference {Path.GetFileName(path)}");
                    continue;
                }
                using Image<Rgba32> img = Image.Load<Rgba32>(path);
                if (img.Width != t.Width || img.Height != t.Height)
                {
                    errors.Add($"texture {t.Name}: size {t.Width}x{t.Height}, reference {img.Width}x{img.Height}");
                    continue;
                }
                int bad = 0, first = -1;
                for (int y = 0; y < t.Height; y++)
                {
                    for (int x = 0; x < t.Width; x++)
                    {
                        Rgba32 p = img[x, y];
                        int i = (y * t.Width + x) * 4;
                        if (t.Rgba[i] != p.R || t.Rgba[i + 1] != p.G || t.Rgba[i + 2] != p.B || t.Rgba[i + 3] != p.A)
                        {
                            if (bad++ == 0) first = y * t.Width + x;
                        }
                    }
                }
                checkedCount++;
                Console.WriteLine($"  texture {t.Name} {BrawlPixels.FormatName(t.Format)} {t.Width}x{t.Height}: "
                    + (bad == 0 ? "equal" : $"{bad} pixels differ (first at {first % t.Width},{first / t.Width})"));
                if (bad != 0) errors.Add($"texture {t.Name}: {bad} pixels differ");
            }
            var skippedNames = skipped.Select(s => s.Name.Replace('#', '_') + ".png").ToHashSet();
            int refCount = Directory.GetFiles(refDir, "*.png").Select(Path.GetFileName)
                .Count(f => f != "sheet.png" && !skippedNames.Contains(f!));
            if (checkedCount != refCount) errors.Add($"textures: checked {checkedCount}, reference has {refCount}");
        }

        // one triangle corner: position, normal, texture coordinate (NaN where absent)
        private readonly record struct Corner(Vector3 P, Vector3 N, Vector2 T);

        // part 2: every mesh with a <name>.dae reference (spec 6.6 proof) and every CHR0 in chr_ref.txt (spec 7.4 proof)
        private static void Meshes(BrawlArchive archive, string refDir, List<string> errors)
        {
            var anims = new Dictionary<string, (BrawlMeshAnimation Anim, BrawlMesh? Mesh)>();
            int meshCount = 0;
            foreach (BrawlArchiveEntry e in archive.Entries)
            {
                if (e.Magic != "bres") continue;
                BrawlResFile res = BrawlResFile.Read(archive.DataOf(e));
                List<BrawlMesh> meshes = res.Folders.Any(f => f.Name == BrawlResFile.Models) ? BrawlMesh.ReadAll(res) : new();
                if (res.Folders.Any(f => f.Name == BrawlResFile.MeshAnimations))
                {
                    foreach (BrawlMeshAnimation a in BrawlMeshAnimation.ReadAll(res)) anims[a.Name] = (a, meshes.FirstOrDefault());
                }
                foreach (BrawlMesh mesh in meshes)
                {
                    string dae = Path.Combine(refDir, mesh.Name + ".dae");
                    if (!File.Exists(dae)) continue;
                    meshCount++;
                    CheckMesh(mesh, XDocument.Load(dae), errors);
                }
            }
            if (meshCount == 0) errors.Add("meshes: no mesh had a reference .dae");
            string chrRef = Path.Combine(refDir, "chr_ref.txt");
            if (File.Exists(chrRef)) CheckAnimations(anims, File.ReadAllLines(chrRef), errors);
            else errors.Add("animations: no chr_ref.txt");
        }

        private static void CheckMesh(BrawlMesh mesh, XDocument dae, List<string> errors)
        {
            XNamespace ns = dae.Root!.Name.Namespace;
            foreach (BrawlDrawStep step in mesh.DrawSteps)
            {
                BrawlMeshObject obj = mesh.ObjectByNumber(step.Object)!;
                BrawlBone bone = mesh.BoneByNumber(step.Bone)!;
                XElement? geo = dae.Descendants(ns + "geometry").FirstOrDefault(g => (string?)g.Attribute("name") == obj.Name);
                if (geo == null)
                {
                    errors.Add($"{mesh.Name}/{obj.Name}: not in the reference");
                    continue;
                }
                XElement tris = geo.Descendants(ns + "triangles").Single();
                if ((string?)tris.Attribute("material") != step.MaterialName)
                {
                    errors.Add($"{mesh.Name}/{obj.Name}: material {step.MaterialName}, reference {tris.Attribute("material")}");
                }
                List<Corner[]> reference = ReferenceTriangles(geo, tris, ns);
                BrawlVertexArray? pos = mesh.PositionArrayByNumber(obj.PositionArray);
                BrawlVertexArray? nrm = obj.NormalArray < 0 ? null : mesh.NormalArrayByNumber(obj.NormalArray);
                BrawlVertexArray? tex = obj.Tex0Array < 0 ? null : mesh.TexCoordArrayByNumber(obj.Tex0Array);
                float[] m = bone.Bind;
                Corner Make(BrawlMeshVertex v)
                {
                    Vector3 p = pos!.Values[v.Position];
                    var pw = new Vector3(m[0] * p.X + m[1] * p.Y + m[2] * p.Z + m[3], m[4] * p.X + m[5] * p.Y + m[6] * p.Z + m[7],
                        m[8] * p.X + m[9] * p.Y + m[10] * p.Z + m[11]);
                    var nw = new Vector3(float.NaN);
                    if (nrm != null && v.Normal >= 0)
                    {
                        Vector3 n = nrm.Values[v.Normal]; // bind 3x3 x n, NOT renormalised (spec 6.6)
                        nw = new Vector3(m[0] * n.X + m[1] * n.Y + m[2] * n.Z, m[4] * n.X + m[5] * n.Y + m[6] * n.Z,
                            m[8] * n.X + m[9] * n.Y + m[10] * n.Z);
                    }
                    var t = new Vector2(float.NaN);
                    if (tex != null && v.Tex0 >= 0) t = new Vector2(tex.Values[v.Tex0].X, 1 - tex.Values[v.Tex0].Y);
                    return new Corner(pw, nw, t);
                }
                int unmatched = 0;
                var used = new bool[reference.Count];
                foreach ((BrawlMeshVertex a, BrawlMeshVertex b, BrawlMeshVertex c) in obj.Triangles)
                {
                    Corner[] ours = { Make(c), Make(b), Make(a) }; // the reference stores the opposite winding
                    int hit = -1;
                    for (int i = 0; i < reference.Count && hit < 0; i++)
                    {
                        if (used[i]) continue;
                        for (int r = 0; r < 3; r++)
                        {
                            if (Same(ours[0], reference[i][r]) && Same(ours[1], reference[i][(r + 1) % 3])
                                && Same(ours[2], reference[i][(r + 2) % 3]))
                            {
                                hit = i;
                                break;
                            }
                        }
                    }
                    if (hit < 0) unmatched++;
                    else used[hit] = true;
                }
                int left = used.Count(u => !u);
                Console.WriteLine($"  mesh {mesh.Name}/{obj.Name} ({step.MaterialName}, bone {bone.Name}): {obj.Triangles.Count} triangles, "
                    + (unmatched == 0 && left == 0 ? "equal" : $"{unmatched} unmatched, {left} reference left"));
                if (unmatched != 0 || left != 0) errors.Add($"{mesh.Name}/{obj.Name}: {unmatched} unmatched, {left} reference left");
            }
        }

        private static bool Same(Corner a, Corner b) => Close(a.P, b.P) && Close(a.N, b.N)
            && (float.IsNaN(a.T.X) ? float.IsNaN(b.T.X) : !float.IsNaN(b.T.X) && Vector2.Distance(a.T, b.T) < 0.001f);

        private static bool Close(Vector3 a, Vector3 b) => float.IsNaN(a.X) ? float.IsNaN(b.X)
            : !float.IsNaN(b.X) && MathF.Abs(a.X - b.X) < 0.001f && MathF.Abs(a.Y - b.Y) < 0.001f && MathF.Abs(a.Z - b.Z) < 0.001f;

        private static List<Corner[]> ReferenceTriangles(XElement geo, XElement tris, XNamespace ns)
        {
            float[] Floats(string sourceId)
            {
                XElement src = geo.Descendants(ns + "source").Single(s => (string?)s.Attribute("id") == sourceId.TrimStart('#'));
                return src.Element(ns + "float_array")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            }
            XElement verts = geo.Descendants(ns + "vertices").Single();
            float[]? p = null, n = null, t = null;
            int po = 0, no = -1, to = -1, stride = 0;
            foreach (XElement input in tris.Elements(ns + "input"))
            {
                int offset = (int)input.Attribute("offset")!;
                stride = Math.Max(stride, offset + 1);
                string sem = (string)input.Attribute("semantic")!, src = (string)input.Attribute("source")!;
                if (sem == "VERTEX")
                {
                    po = offset;
                    p = Floats((string)verts.Elements(ns + "input").Single(i => (string?)i.Attribute("semantic") == "POSITION").Attribute("source")!);
                }
                else if (sem == "NORMAL") { no = offset; n = Floats(src); }
                else if (sem == "TEXCOORD") { to = offset; t = Floats(src); }
            }
            int[] idx = tris.Element(ns + "p")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            var list = new List<Corner[]>();
            for (int k = 0; k + stride * 3 <= idx.Length; k += stride * 3)
            {
                var tri = new Corner[3];
                for (int c = 0; c < 3; c++)
                {
                    int b = k + c * stride;
                    int pi = idx[b + po];
                    var cp = new Vector3(p![pi * 3], p[pi * 3 + 1], p[pi * 3 + 2]);
                    var cn = new Vector3(float.NaN);
                    if (no >= 0) { int ni = idx[b + no]; cn = new Vector3(n![ni * 3], n[ni * 3 + 1], n[ni * 3 + 2]); }
                    var ct = new Vector2(float.NaN);
                    if (to >= 0) { int ti = idx[b + to]; ct = new Vector2(t![ti * 2], t[ti * 2 + 1]); }
                    tri[c] = new Corner(cp, cn, ct);
                }
                list.Add(tri);
            }
            return list;
        }

        // chr_ref.txt: "CHR0 <name> frames <n> loop <bool>", then "<bone> <frame> S x y z R x y z T x y z" lines
        private static void CheckAnimations(Dictionary<string, (BrawlMeshAnimation Anim, BrawlMesh? Mesh)> anims, string[] lines,
            List<string> errors)
        {
            (BrawlMeshAnimation Anim, BrawlMesh? Mesh)? cur = null;
            string curName = "";
            int values = 0, bad = 0;
            float worst = 0;
            foreach (string line in lines)
            {
                string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0) continue;
                if (w[0] == "CHR0")
                {
                    curName = w[1];
                    if (!anims.TryGetValue(curName, out var found))
                    {
                        errors.Add($"animation {curName}: not read");
                        cur = null;
                        continue;
                    }
                    cur = found;
                    int frames = int.Parse(w[3]);
                    bool loops = bool.Parse(w[5]);
                    if (found.Anim.FrameCount != frames || found.Anim.Loops != loops)
                    {
                        errors.Add($"animation {curName}: {found.Anim.FrameCount} frames loop {found.Anim.Loops}, reference {frames} {loops}");
                    }
                    continue;
                }
                if (cur == null) continue;
                string boneName = w[0];
                int frame = int.Parse(w[1]);
                float F(int i) => float.Parse(w[i], CultureInfo.InvariantCulture);
                Vector3 rs = new(F(3), F(4), F(5)), rr = new(F(7), F(8), F(9)), rt = new(F(11), F(12), F(13));
                BrawlBonePose? pose = cur.Value.Anim.Evaluate(boneName, frame);
                if (pose == null)
                {
                    BrawlBone? rest = cur.Value.Mesh?.BoneByName(boneName);
                    if (rest == null)
                    {
                        errors.Add($"animation {curName}: bone {boneName} neither animated nor in the mesh");
                        continue;
                    }
                    pose = new BrawlBonePose(rest.Scale, rest.Rotation, rest.Translation);
                }
                BrawlBonePose v = pose.Value;
                float d = new[] { v.Scale - rs, v.Rotation - rr, v.Translation - rt }
                    .Max(x => Math.Max(MathF.Abs(x.X), Math.Max(MathF.Abs(x.Y), MathF.Abs(x.Z))));
                values += 9;
                worst = Math.Max(worst, d);
                if (d >= 0.001f && bad++ < 5) errors.Add($"animation {curName}: {boneName} frame {frame} off by {d}");
            }
            Console.WriteLine($"  animations: {values} values, worst difference {worst}" + (bad == 0 ? ", all within 0.001" : $", {bad} poses off"));
        }

        // part 3: the effect file (REFF) and effect list (EFLS) against <reference>/reff_values.txt: "== <name> <Node>"
        // blocks of "Prop = value" lines for every effect, its emitter, particle, animation list and each track
        private sealed class RefNode
        {
            public string Kind = "", Name = "";
            public readonly Dictionary<string, string> Props = new();
        }

        private static Dictionary<string, List<RefNode>> ReadEffectReference(string path)
        {
            var effects = new Dictionary<string, List<RefNode>>();
            List<RefNode>? cur = null;
            RefNode? node = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("== ") && line.EndsWith(">"))
                {
                    int lt = line.LastIndexOf(" <", StringComparison.Ordinal);
                    node = new RefNode { Name = line[3..lt], Kind = line[(lt + 2)..^1] };
                    if (node.Kind == "REFFEntryNode") effects[node.Name] = cur = new List<RefNode>();
                    cur?.Add(node);
                    continue;
                }
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (node != null && eq > 0) node.Props[line[..eq]] = line[(eq + 3)..];
            }
            return effects;
        }

        private static float[] RefNumbers(string s) => s.Trim('(', ')').Split(',')
            .Select(v => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

        private static void EffectFiles(BrawlArchive archive, string refDir, List<string> errors)
        {
            string path = Path.Combine(refDir, "reff_values.txt");
            BrawlArchiveEntry? reffEntry = archive.Entries.FirstOrDefault(e => e.Magic == "REFF");
            if (reffEntry == null || !File.Exists(path))
            {
                if (reffEntry != null) errors.Add("effects: no reff_values.txt");
                return;
            }
            Dictionary<string, List<RefNode>> reference = ReadEffectReference(path);
            BrawlEffectFile file = BrawlEffects.Read(archive.DataOf(reffEntry));
            int values = 0, bad = 0;
            var enumNames = new Dictionary<string, Dictionary<string, string>>();
            void Num(string effect, RefNode n, string prop, params double[] ours)
            {
                if (!n.Props.TryGetValue(prop, out string? s))
                {
                    if (bad++ < 20) errors.Add($"effect {effect}: reference has no {n.Kind}.{prop}");
                    return;
                }
                float[] r = RefNumbers(s);
                values += ours.Length;
                bool ok = r.Length == ours.Length;
                for (int i = 0; ok && i < r.Length; i++) ok = Math.Abs(ours[i] - r[i]) <= 1e-4 * Math.Max(1, Math.Abs(r[i]));
                if (!ok && bad++ < 20) errors.Add($"effect {effect}: {n.Kind}.{prop} = {string.Join(",", ours)}, reference {s}");
            }
            // enum-like fields: each raw value must always carry the same reference name (and the reverse)
            void Enum(string prop, RefNode n, object ours)
            {
                if (!n.Props.TryGetValue(prop, out string? s)) return;
                values++;
                var map = enumNames.TryGetValue(prop, out var m) ? m : enumNames[prop] = new();
                string key = ours is byte[] b ? Convert.ToHexString(b) : Convert.ToString(ours, CultureInfo.InvariantCulture)!;
                if (map.TryGetValue(key, out string? had) && had != s)
                {
                    if (bad++ < 20) errors.Add($"{prop}: raw {key} read as both '{had}' and '{s}'");
                }
                else if (map.Any(kv => kv.Value == s && kv.Key != key))
                {
                    if (bad++ < 20) errors.Add($"{prop}: '{s}' from raw {key} and {map.First(kv => kv.Value == s).Key}");
                }
                map[key] = s;
            }
            static double[] V2(Vector2 v) => new double[] { v.X, v.Y };
            static double[] V3(Vector3 v) => new double[] { v.X, v.Y, v.Z };

            foreach (BrawlEffect fx in file.Effects)
            {
                if (!reference.TryGetValue(fx.Name, out List<RefNode>? nodes))
                {
                    errors.Add($"effect {fx.Name}: not in the reference");
                    continue;
                }
                RefNode? em = nodes.FirstOrDefault(n => n.Kind.StartsWith("REFFEmitterNode"));
                RefNode? pa = nodes.FirstOrDefault(n => n.Kind == "REFFParticleNode");
                RefNode? al = nodes.FirstOrDefault(n => n.Kind == "REFFAnimationListNode");
                if (em == null || pa == null || al == null)
                {
                    errors.Add($"effect {fx.Name}: reference lacks emitter/particle/animations");
                    continue;
                }
                BrawlEmitter e = fx.Emitter;
                string N = fx.Name;
                Num(N, em, "EmitFlag", e.EmissionFlags);
                Num(N, em, "EmitLife", e.EmitterLife);
                Num(N, em, "PtclLife", e.ParticleLife);
                Num(N, em, "PtclLifeRandom", e.ParticleLifeRandom);
                Num(N, em, "InheritChildPtclTranslate", e.ChildParticleFollow);
                Num(N, em, "EmitIntervalRandom", e.EmitIntervalRandom);
                Num(N, em, "EmitRandom", e.EmitCountRandom);
                Num(N, em, "Emit", e.EmitCount);
                Num(N, em, "EmitStart", e.EmitStartFrame);
                Num(N, em, "EmitPast", e.RunAheadFrames);
                Num(N, em, "EmitInterval", e.EmitInterval);
                Num(N, em, "InheritPtclTranslate", e.ParticleFollow);
                Num(N, em, "InheritChildEmitTranslate", e.ChildEmitterFollow);
                for (int i = 0; i < 6; i++) Num(N, em, $"CommonParam{i + 1}", e.ShapeParams[i]);
                Num(N, em, "EmitEmitDiv", e.EmitSubdivision);
                Num(N, em, "VelInitVelocityRandom", e.InitialSpeedRandom);
                Num(N, em, "VelMomentumRandom", e.MomentumRandom);
                Num(N, em, "VelPowerRadiationDir", e.SpeedFromCentre);
                Num(N, em, "VelPowerYAxis", e.SpeedFromYAxis);
                Num(N, em, "VelPowerRandomDir", e.SpeedRandomDirection);
                Num(N, em, "VelPowerNormalDir", e.SpeedAlongNormal);
                Num(N, em, "VelDiffusionEmitterNormal", e.NormalSpread);
                Num(N, em, "VelPowerSpecDir", e.SpeedAlongDirection);
                Num(N, em, "VelDiffusionSpecDir", e.DirectionSpread);
                Num(N, em, "VelSpecDir", V3(e.Direction));
                Num(N, em, "Scale", V3(e.EmitterScale));
                Num(N, em, "Rotate", V3(e.EmitterRotation));
                Num(N, em, "Translate", V3(e.EmitterTranslation));
                Num(N, em, "LodNear", e.FadeNear);
                Num(N, em, "LodFar", e.FadeFar);
                Num(N, em, "LodMinEmit", e.FadeMinEmission);
                Num(N, em, "LodAlpha", e.FadeAlpha);
                Num(N, em, "RandomSeed", e.RandomSeed);
                Num(N, em, "TevStageCount", e.TevStageCount);
                Num(N, em, "AlphaFlickCycle", e.AlphaFlickerPeriod);
                Num(N, em, "AlphaFlickRandom", e.AlphaFlickerRandom);
                Num(N, em, "AlphaFlickAmplitude", e.AlphaFlickerAmplitude);
                Num(N, em, "Radius", e.LightRadius);
                Num(N, em, "Position", V3(e.LightPosition));
                Num(N, em, "IndTexScaleExp", e.IndirectScaleExponent);
                Num(N, em, "PivotX", e.IndirectPivotX);
                Num(N, em, "PivotY", e.IndirectPivotY);
                Num(N, em, "ZOffset", e.DepthOffset);
                Enum("CommonFlag", em, e.GeneralFlags);
                Enum("mFlags", em, e.DrawFlags);
                Enum("AlphaComparison0", em, e.AlphaCompare0);
                Enum("AlphaComparison1", em, e.AlphaCompare1);
                Enum("AlphaCompareOperation", em, e.AlphaCombine);
                Enum("FlagClamp", em, e.TevClamp);
                Enum("IndirectTargetStage", em, e.IndirectStage);
                Enum("BlendType", em, e.BlendMode);
                Enum("SrcFactor", em, e.SrcFactor);
                Enum("DstFactor", em, e.DstFactor);
                Enum("Operation", em, e.LogicOp);
                Enum("RasterColor", em, e.ColourSources[0]);
                for (int i = 1; i < 4; i++) Enum($"TevColor{i}", em, e.ColourSources[i]);
                for (int i = 0; i < 4; i++) Enum($"TevKColor{i + 1}", em, e.KonstSources[i]);
                Enum("ZCompareFunc", em, e.DepthCompare);
                Enum("AlphaFlickType", em, e.AlphaFlickerKind);
                Enum("Mode", em, e.LightingMode);
                Enum("LightType", em, e.LightKind);
                Enum("ParticleType", em, e.ParticleShape);
                if (e.Reserved8C.Any(b => b != 0)) errors.Add($"effect {N}: emitter +0x8C not zero");

                BrawlParticle p = fx.Particle;
                Num(N, pa, "Size", V2(p.Size));
                Num(N, pa, "Scale", V2(p.Scale));
                Num(N, pa, "Rotate", V3(p.Rotation));
                for (int i = 0; i < 3; i++) Num(N, pa, $"TextureScale{i + 1}", V2(p.TextureScale[i]));
                Num(N, pa, "TextureRotate", V3(p.TextureRotation));
                for (int i = 0; i < 3; i++) Num(N, pa, $"TextureTranslate{i + 1}", V2(p.TextureTranslation[i]));
                Num(N, pa, "TextureWrap", p.TextureWrap);
                Num(N, pa, "TextureReverse", p.TextureFlip);
                Num(N, pa, "AlphaCompareRef0", p.AlphaRef0);
                Num(N, pa, "AlphaCompareRef1", p.AlphaRef1);
                for (int i = 0; i < 3; i++) Num(N, pa, $"RotateOffsetRandom{i + 1}", p.RotationRandom[i]);
                Num(N, pa, "RotateOffset", V3(p.RotationOffset));
                for (int i = 0; i < 3; i++)
                {
                    string want = pa.Props.TryGetValue($"Texture{i + 1}Name", out string? t) ? t : "";
                    values++;
                    if (p.TextureNames[i] != want && bad++ < 20) errors.Add($"effect {N}: texture {i + 1} '{p.TextureNames[i]}', reference '{want}'");
                }

                Num(N, al, "PtclTrackCount", fx.ParticleTrackCount);
                Num(N, al, "PtclInitTrackCount", fx.ParticleTracksAtBirth);
                Num(N, al, "EmitTrackCount", fx.EmitterTrackCount);
                Num(N, al, "EmitInitTrackCount", fx.EmitterTracksAtBirth);
                List<RefNode> refTracks = nodes.Where(n => n.Kind == "REFFAnimationNode").ToList();
                List<BrawlEffectTrack> tracks = fx.ParticleTracks.Concat(fx.EmitterTracks).ToList();
                if (refTracks.Count != tracks.Count)
                {
                    errors.Add($"effect {N}: {tracks.Count} tracks, reference {refTracks.Count}");
                    continue;
                }
                for (int i = 0; i < tracks.Count; i++)
                {
                    BrawlEffectTrack t = tracks[i];
                    RefNode r = refTracks[i];
                    string TN = $"{N} track {i}";
                    values++;
                    if ((t.IsParticleTrack ? "Particle" : "Emitter") != r.Props.GetValueOrDefault("Type") && bad++ < 20)
                    {
                        errors.Add($"effect {TN}: particle {t.IsParticleTrack}, reference {r.Props.GetValueOrDefault("Type")}");
                    }
                    if (t.Magic != BrawlEffects.TrackMagic) errors.Add($"effect {TN}: magic 0x{t.Magic:X2}");
                    Num(TN, r, "KindEnable", t.ComponentBits);
                    Num(TN, r, "LoopCount", t.LoopCount);
                    Num(TN, r, "RandomSeed", t.RandomSeed);
                    Num(TN, r, "FrameCount", t.FrameCount);
                    Num(TN, r, "KeyTableSize", t.KeysSize);
                    Num(TN, r, "RangeTableSize", t.RangesSize);
                    Num(TN, r, "RandomTableSize", t.RandomSize);
                    Num(TN, r, "NameTableSize", t.NamesSize);
                    Num(TN, r, "InfoTableSize", t.ExtraSize);
                    // the reference's kind names are shifted, so only their consistency per raw kind is checked
                    Enum("KindType", r, (t.IsParticleTrack ? "P" : "E") + t.Kind);
                    Enum("CurveFlag", r, t.ValueType);
                    Enum("ProcessFlag", r, t.ProcessFlags);
                    if (t.Kind == 26 && t.Names.Count == 0) errors.Add($"effect {TN}: child track without names");
                }
            }
            foreach (string name in reference.Keys.Where(k => file.Find(k) == null)) errors.Add($"effect {name}: in the reference, not read");
            Console.WriteLine($"  effects: {file.Effects.Count} read ('{file.SetName}' v{file.Version}), {values} values, "
                + (bad == 0 ? "all equal" : $"{bad} differ"));

            BrawlArchiveEntry? listEntry = archive.Entries.FirstOrDefault(x => x.Magic == "EFLS");
            if (listEntry == null)
            {
                errors.Add("effect list: no EFLS entry");
                return;
            }
            BrawlEffectList list = BrawlEffectList.Read(archive.DataOf(listEntry));
            foreach (BrawlEffectListEntry le in list.Entries)
            {
                Console.WriteLine($"  effect list: model file {le.ModelFile} x{le.ModelFileRepeat} {le.Name ?? "(no name)"} extra {le.Extra}");
                if (le.Name != null && file.Find(le.Name) == null) errors.Add($"effect list: {le.Name} not in the effect file");
            }
            void Model(string name, int want)
            {
                BrawlEffectListEntry? le = list.Find(name);
                if (le == null) errors.Add($"effect list: no {name}");
                else if (le.ModelFile != want) errors.Add($"effect list: {name} model file {le.ModelFile}, expected {want}");
            }
            if (file.Find("PtcSzerosuitParalyzerBulletS") != null)
            {
                if (list.Entries.Count != 16) errors.Add($"effect list: {list.Entries.Count} entries, expected 16");
                Model("PtcSzerosuitParalyzerBulletS", 1);
                Model("PtcSzerosuitParalyzerBulletL", 2);
            }
        }
    }
}
