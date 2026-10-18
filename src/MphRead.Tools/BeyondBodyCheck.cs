using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MphRecomp.Campaign;
using MphRecomp.Import;
using MphRecomp.Import.Beyond;
using OpenTK.Mathematics;

namespace MphRead
{
    // -beyondbody [Name] [--out dir] [--mp4 romfs] [--joints regex] [--bones clips --render png] [-v]: the "bodies" import of a Prime 4 character
    // (Import/Bodies/BeyondBody, the code the device runs) from the owner's own RomFS into <out>/bodies/<Name>, then reads
    // it back as the app does (NativeBody.Load) and prints its bones, draws and clips; --joints lists the bones whose name
    // matches, with their parents and bind positions (for the clip map)
    internal static class BeyondBodyCheck
    {
        public static void Run(string[] args)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string which = args.Length >= 2 && !args[1].StartsWith("-") ? args[1] : "MP4Sylux";
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-beyondbody")));
            string romfs = Arg("--mp4", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden", "dump", "010019A01E2F2000", "romfs"));
            bool verbose = args.Contains("-v");
            string dir = Path.Combine(outRoot, "bodies", which);
            // --usable <data root>: would the app use <root>/bodies/<Name> (ImportedContent, the app's own recipes)?
            if (Arg("--usable", "") is string ur && ur.Length > 0)
            {
                foreach (bool pub in new[] { false, true })
                {
                    MphRecomp.Config.RecompSettings.PublicBuild = pub;
                    Console.WriteLine($"  public {pub}: exists {NativeBody.Exists(Path.Combine(ur, "bodies", which))}, usable {ImportedContent.Usable(Path.Combine(ur, "bodies"), which)}");
                }
                RecipeStore emb = RecipeStore.Embedded();
                foreach (ImportRecipe r in emb.All.Where(r => r.Name.StartsWith(which, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"    embedded {r.Id} {r.Game} {r.Sha1}");
                return;
            }
            // --keysold: the "keys too old" message per key generation, from a throwaway prod.keys of zeros (no real key values)
            if (args.Contains("--keysold"))
            {
                string fake = Path.Combine(Path.GetTempPath(), "mph-fake-prod.keys");
                File.WriteAllText(fake, "header_key = " + new string('0', 64) + Environment.NewLine);
                var keys = MphRecomp.Import.Switch.SwitchKeys.FromFile(fake, Path.Combine(Path.GetTempPath(), "mph-no-title.keys"));
                foreach (int gen in new[] { 3, 0x13, 0x14, 0x17, 0x20 })
                {
                    try { keys.KeyAreaKey(0, MphRecomp.Import.Switch.SwitchKeys.Revision(gen)); }
                    catch (MphRecomp.Import.Switch.SwitchKeyRequiredException e) { Console.WriteLine($"  generation 0x{gen:x2}: firmware '{e.Firmware}': {e.Message}"); }
                }
                File.Delete(fake);
                return;
            }
            // --public: a public build's PRIME 4 import end to end -- every item IMPORT GAMES offers (ImportGames.Offered), imported
            // by the app's own ImportService + recipes from the RomFS folder into a fresh <out>, then whether the app would use
            // each (ImportedContent: public builds take only what the import made)
            if (args.Contains("--public"))
            {
                MphRecomp.Config.RecompSettings.PublicBuild = true;
                Ready();
                if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
                var svc = new ImportService(outRoot);
                ImportGameInfo game = svc.Games.First(g => g.Id == "mp4");
                foreach (ImportItemInfo it in game.Items)
                    Console.WriteLine($"  {(MphRecomp.Frontend.ImportGames.Offered(it) ? "OFFERED" : "held   ")} {it.Id}");
                List<string> ids = game.Items.Where(MphRecomp.Frontend.ImportGames.Offered).Select(i => i.Id).ToList();
                var progress = new Progress(verbose);
                ImportReport rep = svc.Import("mp4", romfs, progress, default, ids);
                bool ok = true;
                foreach (ImportItemInfo it in rep.Items)
                {
                    bool use = ImportedContent.Usable(outRoot, it.Kind, it.Name);
                    ok &= use && it.Status == ItemImportStatus.Complete;
                    Console.WriteLine($"  {(use ? "PASS" : "FAIL")} {it.Id,-26} {it.Status,-10} {it.Seconds,6:F1} s  usable {use}  {it.Message}");
                }
                Console.WriteLine($"  public PRIME 4 import: {(ok ? "all PASS" : "FAIL")} ({rep.Items.Count} items into {outRoot})");
                return;
            }
            // --keys <chpr file> <clip|*> <joint regex> [--slot Rot|Trans|Scale] [--changed] [--wrapped]: a clip's raw keys on
            // the matching joints (frame, flags, value); --changed: only tracks whose keys ChprAnimCodec.Unwrap carries on
            if (Array.IndexOf(args, "--keys") is int ki && ki > 0 && ki + 3 < args.Length)
            {
                ChprCharacter ch = ChprCharacter.Load(File.ReadAllBytes(args[ki + 1]));
                var kre = new Regex(args[ki + 3], RegexOptions.CultureInvariant);
                string slot = Arg("--slot", "Rot");
                bool changed = args.Contains("--changed");
                IEnumerable<string> clips = args[ki + 2] == "*" ? ch.AnimNames : new[] { args[ki + 2] };
                foreach (string clip in clips)
                {
                    ChprDecodedAnim raw;
                    try { raw = ch.DecodeRaw(ch.FindAnim(clip)); }
                    catch (Exception ex) { if (!changed) Console.WriteLine($"  {clip}: {ex.Message}"); continue; }
                    foreach (var kv in raw.Channels.Where(c => (slot == "*" || c.Key.Slot.ToString() == slot) && kre.IsMatch(c.Key.Joint)).OrderBy(c => c.Key.Joint, StringComparer.Ordinal))
                    {
                        List<ChprAnimCodec.Key> un = ChprAnimCodec.Unwrap(kv.Value.Track, kv.Value.Keys);
                        int diff = Enumerable.Range(0, un.Count).Count(i => un[i].V0 != kv.Value.Keys[i].V0 || un[i].V1 != kv.Value.Keys[i].V1 || un[i].V2 != kv.Value.Keys[i].V2);
                        if (changed && diff == 0) continue;
                        Console.WriteLine($"  {clip} {kv.Key.Joint} {kv.Key.Slot}: w{kv.Value.Track.Width} e{kv.Value.Track.Exponent}, {kv.Value.Keys.Count} keys, {diff} unwrapped");
                        foreach (ChprAnimCodec.Key k in args.Contains("--wrapped") ? kv.Value.Keys : un)
                        {
                            double[] q = ChprAnimCodec.Dequant(kv.Value.Track, k);
                            Console.WriteLine($"    f{k.Frame,3} flags {k.Flags} raw ({k.V0},{k.V1},{k.V2}) m{k.Mask}  ({String.Join(",", q.Select(x => x.ToString("0.000")))})");
                        }
                    }
                }
                return;
            }
            // --probe <joint regex> <clip regex>: the recipe's gameplay character -- its joints and clips that match, each
            // clip's decode, and the model's skin joints that the body leaves out (with the triangles each one carries)
            if (Array.IndexOf(args, "--probe") is int pi && pi > 0 && pi + 2 < args.Length)
            {
                Probe(repo, romfs, which, new Regex(args[pi + 1], RegexOptions.CultureInvariant), new Regex(args[pi + 2], RegexOptions.CultureInvariant));
                return;
            }
            // --bones/--render: BodyTest's posed render of the folder, with the MPH ROM's paths set up first (the DS clips
            // the Beyond map retargets at load come from it; run from the folder that holds paths.txt)
            if (Arg("--bones", "") is string bones && bones.Length > 0)
            {
                Paths.UpdatePaths();
                Paths.ChooseMphPath();
                BodyTest.RunAfterPose = true;
                BodyTest.Run(new[] { "-bodytest", which, "--out", outRoot, "--bones", bones, "--render", Arg("--render", "") });
                return;
            }
            if (!args.Contains("--noimport"))
            {
                RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
                List<ImportRecipe> recipes = store.All.Where(r => r.Kind == "bodies" && r.Game == "mp4" && r.Name.Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
                if (recipes.Count == 0)
                {
                    Console.WriteLine($"  -beyondbody: no Prime 4 body recipe '{which}'");
                    return;
                }
                var svc = new ImportService(outRoot, store);
                var progress = new Progress(verbose);
                ImportReport rep = svc.Import("mp4", romfs, progress, default, recipes.Select(r => r.Id).ToList());
                foreach (ImportItemInfo it in rep.Items) Console.WriteLine($"  {it.Id,-24} {it.Status,-12} {it.Seconds,6:F2} s  {it.Message}");
                foreach (string l in progress.Lines.Where(l => !l.StartsWith("look: ", StringComparison.Ordinal))) Console.WriteLine("    " + l);
            }
            if (!NativeBody.Exists(dir))
            {
                Console.WriteLine($"  FAIL no body.bin in {dir}");
                return;
            }
            NativeBody body = NativeBody.Load(dir);
            HdGun g = body.Data;
            Console.WriteLine($"  {dir}: {g.BoneCount} bones, {g.Materials.Length} draws ({g.Materials.Sum(m => m.VertexCount) / 3} triangles), "
                + $"height {body.Height:0.###} feet {body.Feet:0.###}, game {body.Game}");
            foreach (HdGun.Anim a in g.Anims.Values) Console.WriteLine($"    clip {a.Name}: {a.Keys} keys, {a.Duration:0.00} s");
            string? rx = Arg("--joints", "");
            if (rx.Length > 0)
            {
                var re = new Regex(rx, RegexOptions.CultureInvariant);
                foreach (int i in g.Order)
                {
                    if (!re.IsMatch(g.Names[i])) continue;
                    var at = g.Bind[i];
                    Console.WriteLine($"    [{i,3}] {g.Names[i],-36} parent {(g.Parent[i] >= 0 ? g.Names[g.Parent[i]] : "-"),-36} bind ({at.X:0.###}, {at.Y:0.###}, {at.Z:0.###})");
                }
            }
            // --track <clip>: the --joints bones' model-space range over the clip (does a foot slide or lift)
            if (Arg("--track", "") is string tc && tc.Length > 0 && Ready() && BodyClips.For(body) is var _ && g.Anims.TryGetValue(tc, out HdGun.Anim? ta) && rx.Length > 0)
            {
                var re = new Regex(rx, RegexOptions.CultureInvariant);
                var tlo = new Dictionary<int, Vector3>();
                var thi = new Dictionary<int, Vector3>();
                for (int f = 0; f <= 60; f++)
                {
                    body.Pose(ta, ta.Duration * f / 60);
                    for (int i = 0; i < g.BoneCount; i++)
                    {
                        if (!re.IsMatch(g.Names[i])) continue;
                        Vector3 at = body.World[i].ExtractTranslation();
                        tlo[i] = tlo.TryGetValue(i, out Vector3 l) ? Vector3.ComponentMin(l, at) : at;
                        thi[i] = thi.TryGetValue(i, out Vector3 h) ? Vector3.ComponentMax(h, at) : at;
                    }
                }
                foreach (int i in tlo.Keys)
                {
                    Vector3 d = thi[i] - tlo[i];
                    Console.WriteLine($"    {tc} {g.Names[i],-28} from {tlo[i]} to {thi[i]}  range ({d.X:0.###}, {d.Y:0.###}, {d.Z:0.###})");
                }
            }
            // --curve <clip> (with --joints rx): each matching bone's local turn away from the idle's first frame (degrees)
            // at 20 steps through the clip
            if (Arg("--curve", "") is string cc && cc.Length > 0 && g.Anims.TryGetValue(cc, out HdGun.Anim? ca) && rx.Length > 0
                && g.Anims.TryGetValue("idle_sylux", out HdGun.Anim? ci))
            {
                var re = new Regex(rx, RegexOptions.CultureInvariant);
                Console.WriteLine($"    {cc}: {ca.Duration:0.00} s");
                for (int i = 0; i < g.BoneCount; i++)
                {
                    if (!re.IsMatch(g.Names[i])) continue;
                    g.Sample(ci, 0, i, out Quaternion q0, out _);
                    var row = new System.Text.StringBuilder();
                    for (int f = 0; f <= 20; f++)
                    {
                        g.Sample(ca, ca.Duration * f / 20, i, out Quaternion q, out _);
                        float dot = MathF.Min(1, MathF.Abs(Vector4.Dot(new Vector4(q.Normalized().Xyz, q.Normalized().W), new Vector4(q0.Normalized().Xyz, q0.Normalized().W))));
                        row.Append($"{2 * MathF.Acos(dot) * 180 / MathF.PI,4:0}");
                    }
                    Console.WriteLine($"    {g.Names[i],-30}{row}");
                }
            }
            // --tilt <clip regex> <bone>: the bone's up axis away from world up (degrees, after AfterPose) through each clip,
            // as pitch (toward +Z) / roll (toward +X)
            if (Array.IndexOf(args, "--tilt") is int ti && ti > 0 && ti + 2 < args.Length)
            {
                var tre = new Regex(args[ti + 1], RegexOptions.CultureInvariant);
                int tb = body.Bone(args[ti + 2]);
                BodyClips? tm = BodyClips.For(body);
                foreach (HdGun.Anim a in g.Anims.Values.Where(a => tre.IsMatch(a.Name)))
                {
                    var row = new System.Text.StringBuilder();
                    for (int f = 0; f <= 12; f++)
                    {
                        body.Pose(a, a.Duration * f / 12);
                        tm?.AfterPose?.Invoke(body, null);
                        Vector3 up = body.World[tb].Row1.Xyz.Normalized();
                        row.Append($" {MathF.Asin(up.Z) * 180 / MathF.PI,5:0.0}/{MathF.Asin(up.X) * 180 / MathF.PI,5:0.0}");
                    }
                    Console.WriteLine($"    {a.Name,-30}{row}");
                }
            }
            // --extent <clip>: the posed mesh's box at the clip's start (which way the model lies) and each bone's axes
            if (Arg("--extent", "") is string ec && ec.Length > 0 && g.Anims.TryGetValue(ec, out HdGun.Anim? ea))
            {
                BodyClips? em = BodyClips.For(body); // first: making the map may pose the body itself
                body.Pose(ea, 0);
                em?.AfterPose?.Invoke(body, null);
                var lo = new Vector3(Single.MaxValue);
                var hi = new Vector3(Single.MinValue);
                var pts = new List<Vector3>();
                Vector3 tail = new(0, 0, Single.MaxValue), nose = new(0, 0, Single.MinValue), left = new(Single.MaxValue, 0, 0), right = new(Single.MinValue, 0, 0);
                var pal = new float[32 * 16];
                for (int m = 0; m < g.Materials.Length; m++)
                {
                    HdGun.Material mat = g.Materials[m];
                    body.FillPalette(m, pal);
                    for (int v = 0; v < mat.VertexCount; v++)
                    {
                        int o = v * HdGun.FloatsPerVertex;
                        var p = new Vector4(mat.Vertices[o], mat.Vertices[o + 1], mat.Vertices[o + 2], 1);
                        Vector4 acc = Vector4.Zero;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = mat.Vertices[o + 15 + k];
                            if (w <= 0) continue;
                            int bi = (int)mat.Vertices[o + 11 + k] * 16;
                            var M = new Matrix4(pal[bi], pal[bi + 1], pal[bi + 2], pal[bi + 3], pal[bi + 4], pal[bi + 5], pal[bi + 6], pal[bi + 7],
                                pal[bi + 8], pal[bi + 9], pal[bi + 10], pal[bi + 11], pal[bi + 12], pal[bi + 13], pal[bi + 14], pal[bi + 15]);
                            acc += w * (p * M);
                        }
                        lo = Vector3.ComponentMin(lo, acc.Xyz);
                        hi = Vector3.ComponentMax(hi, acc.Xyz);
                        pts.Add(acc.Xyz);
                        if (acc.Z < tail.Z) tail = acc.Xyz;
                        if (acc.Z > nose.Z) nose = acc.Xyz;
                        if (acc.X < left.X) left = acc.Xyz;
                        if (acc.X > right.X) right = acc.Xyz;
                    }
                }
                var blo = new Vector3(Single.MaxValue);
                var bhi = new Vector3(Single.MinValue);
                Vector3 btail = new(0, 0, Single.MaxValue), bnose = new(0, 0, Single.MinValue);
                foreach (HdGun.Material mat in g.Materials)
                {
                    for (int v = 0; v < mat.VertexCount; v++)
                    {
                        var p = new Vector3(mat.Vertices[v * HdGun.FloatsPerVertex], mat.Vertices[v * HdGun.FloatsPerVertex + 1], mat.Vertices[v * HdGun.FloatsPerVertex + 2]);
                        blo = Vector3.ComponentMin(blo, p);
                        bhi = Vector3.ComponentMax(bhi, p);
                        if (p.Z < btail.Z) btail = p;
                        if (p.Z > bnose.Z) bnose = p;
                    }
                }
                Console.WriteLine($"    bind ends: -Z {btail} +Z {bnose} (slope {MathF.Atan2(bnose.Y - btail.Y, bnose.Z - btail.Z) * 180 / MathF.PI:0.0} deg)");
                Console.WriteLine($"    bind box ({blo.X:0.###}, {blo.Y:0.###}, {blo.Z:0.###}) .. ({bhi.X:0.###}, {bhi.Y:0.###}, {bhi.Z:0.###})");
                for (int i = 0; i < g.BoneCount; i++)
                {
                    Matrix4 B = g.InvBind[i].Inverted();
                    Console.WriteLine($"      bind {g.Names[i],-23} at {B.ExtractTranslation()} X {B.Row0.Xyz} Y {B.Row1.Xyz} Z {B.Row2.Xyz}");
                }
                {
                    // principal axes of the posed mesh's vertices (power iteration on the covariance, deflated)
                    Vector3 mean = Vector3.Zero;
                    foreach (Vector3 q in pts) mean += q;
                    mean /= pts.Count;
                    var cov = new Matrix3();
                    foreach (Vector3 q in pts)
                    {
                        Vector3 d = q - mean;
                        cov.Row0 += d.X * d; cov.Row1 += d.Y * d; cov.Row2 += d.Z * d;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 v = k == 0 ? Vector3.UnitZ : k == 1 ? Vector3.UnitX : Vector3.UnitY;
                        for (int it = 0; it < 200; it++) v = (cov * v).Normalized();
                        float lam = Vector3.Dot(v, cov * v);
                        Console.WriteLine($"    {ec} t0 axis {k}: {v} spread {MathF.Sqrt(lam / pts.Count):0.###}, off the floor {MathF.Asin(Math.Clamp(v.Y, -1, 1)) * 180 / MathF.PI:0.0} deg");
                        cov.Row0 -= lam * v.X * v; cov.Row1 -= lam * v.Y * v; cov.Row2 -= lam * v.Z * v;
                    }
                }
                Console.WriteLine($"    {ec} t0 ends: -Z {tail} +Z {nose} (slope {MathF.Atan2(nose.Y - tail.Y, nose.Z - tail.Z) * 180 / MathF.PI:0.0} deg); -X {left} +X {right} (slope {MathF.Atan2(right.Y - left.Y, right.X - left.X) * 180 / MathF.PI:0.0} deg)");
                Console.WriteLine($"    {ec} t0 box ({lo.X:0.###}, {lo.Y:0.###}, {lo.Z:0.###}) .. ({hi.X:0.###}, {hi.Y:0.###}, {hi.Z:0.###})");
                for (int i = 0; i < g.BoneCount; i++)
                {
                    Matrix4 W = body.World[i];
                    Console.WriteLine($"      {g.Names[i],-28} at {W.ExtractTranslation()} X {W.Row0.Xyz} Y {W.Row1.Xyz} Z {W.Row2.Xyz}");
                }
            }
        }

        static bool Ready()
        {
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            return true;
        }

        static void Probe(string repo, string romfs, string which, Regex jrx, Regex arx)
        {
            RecipeStore store = RecipeStore.FromDirectory(Path.Combine(repo, "recipes"));
            ImportRecipe r = store.All.First(x => x.Kind == "bodies" && x.Game == "mp4" && x.Name.Equals(which, StringComparison.OrdinalIgnoreCase));
            MphRecomp.Import.Hd.HdRecipe h = MphRecomp.Import.Hd.HdRecipe.From(r);
            var paks = h.Paks.Select(p => new MphRecomp.Import.Beyond.BeyondArchive(File.OpenRead(Path.Combine(romfs, p.Name)), p.Name)).ToList();
            try
            {
                var cid = Guid.Parse(h.FlourishChpr);
                var ch = MphRecomp.Import.Beyond.ChprCharacter.Load(MphRecomp.Import.Beyond.BeyondPakModelSource.Holder(paks, cid, "character file").ReadAsset(cid));
                var sk = ch.Skeleton;
                Console.WriteLine($"  gameplay character: {sk.Names.Length} joints, {ch.AnimNames.Length} clips");
                for (int i = 0; i < sk.Names.Length; i++)
                {
                    if (!jrx.IsMatch(sk.Names[i])) continue;
                    (double[] rq, double[] rt) = sk.RestLocal(sk.Names[i]);
                    Console.WriteLine($"    joint [{i}] {sk.Names[i]} <- {(sk.Parent[i] >= 0 ? sk.Names[sk.Parent[i]] : "-")}  rest wxyz ({rq[0]:0.###}, {rq[1]:0.###}, {rq[2]:0.###}, {rq[3]:0.###}) t ({rt[0]:0.###}, {rt[1]:0.###}, {rt[2]:0.###})");
                }
                for (int i = 0; i < ch.AnimNames.Length; i++)
                {
                    if (!arx.IsMatch(ch.AnimNames[i])) continue;
                    var d = ch.Decode(i);
                    string js = d.Ok && d.Animation != null ? $"{d.Animation.Frames} frames, {d.Animation.Joints.Count(jrx.IsMatch)}/{d.Animation.Joints.Length} joints match" : $"{d.Status} {d.Message}";
                    Console.WriteLine($"    clip {ch.AnimNames[i]}: {js}");
                    if (!(d.Ok && d.Animation is MphRecomp.Import.Beyond.ChprAnimation an)) continue;
                    // the matched joints' local rotation / translation at its first and middle frames (rot, trans keyed: R T)
                    for (int j = 0; j < an.Joints.Length; j++)
                    {
                        if (!jrx.IsMatch(an.Joints[j])) continue;
                        foreach (int f in new[] { 0, an.Frames / 2 })
                        {
                            var q = an.Rot(f, j);
                            var t = an.Translation(f, j);
                            Console.WriteLine($"      {an.Joints[j],-28} f{f,-3} {(an.Animated[j * 3] ? "R" : "-")}{(an.Animated[j * 3 + 1] ? "T" : "-")} "
                                + $"rot ({q.X:0.###}, {q.Y:0.###}, {q.Z:0.###}, {q.W:0.###}) trans ({t.X:0.###}, {t.Y:0.###}, {t.Z:0.###})");
                        }
                    }
                }
                var mid = Guid.Parse(h.Chpr);
                var msk = MphRecomp.Import.Beyond.ChprCharacter.Load(MphRecomp.Import.Beyond.BeyondPakModelSource.Holder(paks, mid, "character file").ReadAsset(mid)).Skeleton;
                for (int i = 0; i < msk.Names.Length; i++)
                {
                    if (!jrx.IsMatch(msk.Names[i])) continue;
                    (double[] rq, double[] rt) = msk.RestLocal(msk.Names[i]);
                    Console.WriteLine($"    model joint [{i}] {msk.Names[i]} <- {(msk.Parent[i] >= 0 ? msk.Names[msk.Parent[i]] : "-")}  rest wxyz ({rq[0]:0.###}, {rq[1]:0.###}, {rq[2]:0.###}, {rq[3]:0.###}) t ({rt[0]:0.###}, {rt[1]:0.###}, {rt[2]:0.###}) bind {(msk.BindWorld.ContainsKey(i) ? "yes" : "no")}");
                }
                var src = MphRecomp.Import.Hd.BeyondTrophy.Source.FromGame(paks, Guid.Parse(h.Smdl), Guid.Parse(h.Chpr));
                var per = new Dictionary<string, int>();
                foreach (var m in src.Meshes)
                {
                    if (m.Name.Contains("_LOD0_1_", StringComparison.Ordinal)) continue;
                    for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                    {
                        int v = m.Indices[t], best = 0;
                        for (int k = 1; k < 4; k++) if (m.Weights[4 * v + k] > m.Weights[4 * v + best]) best = k;
                        string jn = src.SkinNames[m.Joints[4 * v + best]];
                        if (MphRecomp.Import.Hd.BeyondTrophy.DsName(jn, src.Parent) != null) continue;
                        string key = $"{m.Name} / {jn} (in gameplay: {sk.Index.ContainsKey(jn)})";
                        per[key] = per.GetValueOrDefault(key) + 1;
                    }
                }
                foreach (var kv in per.OrderBy(x => x.Key)) Console.WriteLine($"    dropped {kv.Value,5} tris  {kv.Key}");
            }
            finally
            {
                foreach (var p in paks) p.Dispose();
            }
        }

        sealed class Progress : IProgress<(string item, float fraction, string message)>
        {
            readonly bool _verbose;
            public readonly List<string> Lines = new();
            public Progress(bool verbose) => _verbose = verbose;
            public void Report((string item, float fraction, string message) v)
            {
                Lines.Add(v.message);
                if (_verbose) Console.WriteLine($"      [{v.fraction * 100,5:F1}%] {v.item}: {v.message}");
            }
        }
    }
}
