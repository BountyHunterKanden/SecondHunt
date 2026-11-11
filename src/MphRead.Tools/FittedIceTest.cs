using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead
{
    // Fitted ice (frozen bodies wear vanilla's ice shell fitted node by node to their own body; MphRecomp.Core FittedIce.cs):
    //   -fittedice measure    how far vanilla's ice shells (samus_ice on Samus_lod0, nox_ice on Noxus_lod0 / Trace_lod0)
    //                         sit outside the body's surface, per bone and overall (bind pose, world units)
    //   -fittedice frames     the shells through their own bind frames vs the body's, and posed as the game poses them
    //   -fittedice host       the host-drawn fit (HD suits, native bodies) run on the DS bodies, gaps in band
    //   -fittedice check      every DS body's fitted shell: ice material, the shell's triangles, vanilla's on its own
    //                         body, posed gap to the body's surface in band
    internal static class FittedIceTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string mode = args.Length > 1 ? args[1] : "measure";
            if (mode == "measure")
            {
                Measure("samus_ice", "Samus_lod0");
                Measure("nox_ice", "Nox_lod0");
                Measure("nox_ice", "Trace_lod0");
                Measure("samus_ice", "Kanden_lod0");
                return;
            }
            if (mode == "host")
            {
                // the host-drawn path (SuitIce.cs) on the DS bodies themselves: their posed frames as the bind, as an HD suit
                // (ShellOnSkeleton) and as a native body (ShellOnModel); the gaps should match -fittedice check's
                int fails = 0;
                foreach (string body in new[] { "Samus_lod0", "Kanden_lod0", "Trace_lod0", "Sylux_lod0", "Nox_lod0", "Spire_lod0",
                    "Weavel_lod0", "Guardian_lod0" })
                {
                    if (Environment.GetEnvironmentVariable("FITTEDICE_ONLY") is string only && only != body) continue;
                    MphRecomp.Render.FittedIce.IceReference r = MphRecomp.Render.FittedIce.ReferenceOf(body);
                    Model posed = MphRecomp.Render.FittedIce.Posed(body);
                    List<Vector3[]> surface = MphRecomp.Render.FittedIce.SurfaceOf(posed);
                    List<Vector3>[] local = MphRecomp.Render.FittedIce.CloudsOf(posed);
                    Matrix4[] world = posed.Nodes.Select(n => n.Animation).ToArray();
                    string[] names = posed.Nodes.Select(n => n.Name).ToArray();
                    // (on a skeleton = an HD suit, always on Samus's DS rig: Samus's shell only)
                    var sk = r.Ice == "samus_ice" ? MphRecomp.Render.FittedIce.ShellOnSkeleton(r, names, local, world, surface) : null;
                    var clouds = new List<Vector3>[r.Names.Length];
                    for (int i = 0; i < clouds.Length; i++)
                    {
                        int j = Array.IndexOf(names, r.Names[i]);
                        clouds[i] = j >= 0 ? local[j].Select(v => Vector3.TransformPosition(v, world[j])).ToList() : new List<Vector3>();
                    }
                    var md = MphRecomp.Render.FittedIce.ShellOnModel(r, clouds, surface);
                    foreach ((string how, MphRecomp.Render.FittedIce.IceCorner[]? c) in new[] { ("skeleton", sk), ("model", md) })
                    {
                        if (c == null && how == "skeleton" && r.Ice != "samus_ice") continue;
                        if (c == null) { Console.WriteLine($"  FAIL {body} {how}: no shell"); fails++; continue; }
                        var tris = Enumerable.Range(0, c.Length / 3).Select(t => new[] { c[3 * t].Pos, c[3 * t + 1].Pos, c[3 * t + 2].Pos }).ToList();
                        Stat gap = Gap(tris, surface);
                        bool ok = tris.Count == r.Shell.Length / 3 && gap.Median > 0.02f && gap.Median < 0.2f && gap.P90 < 0.35f && gap.Max < 0.6f;
                        if (!ok) fails++;
                        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {body,-14} {how,-8} tris {tris.Count,3} gap median {gap.Median:0.000} p90 {gap.P90:0.000} max {gap.Max:0.000} inside {gap.Inside:0}%");
                    }
                }
                Console.WriteLine(fails == 0 ? "FITTED ICE HOST PASS" : $"FITTED ICE HOST FAIL ({fails})");
                if (fails != 0) Environment.ExitCode = 1;
                return;
            }
            if (mode == "nodes")
            {
                foreach (string name in new[] { "samus_ice", "nox_ice", "Samus_lod0", "Kanden_lod0", "Trace_lod0", "Sylux_lod0", "Nox_lod0",
                    "Spire_lod0", "Weavel_lod0", "Guardian_lod0", MphRecomp.Multiplayer.DarkSamusStandin.ModelNames[0] })
                {
                    Model m;
                    try { m = Read.GetModelInstance(name, noCache: true).Model; }
                    catch (Exception ex) { Console.WriteLine($"{name}: {ex.Message}"); continue; }
                    m.ComputeNodeMatrices(0);
                    var counts = Triangles(m).SelectMany(t => t).GroupBy(c => c.Node).ToDictionary(g => g.Key, g => g.Count());
                    Console.WriteLine($"{name} ({m.Nodes.Count} nodes, scale {m.Scale.X}): " + string.Join(" ", m.Nodes.Select((n, i) =>
                    {
                        Vector3 j = m.Nodes[i].Transform.ExtractTranslation() * m.Scale.X;
                        return $"{i}:{n.Name}<{n.ParentIndex}>[{(counts.TryGetValue(i, out int c) ? c : 0)}]({j.X:0.00},{j.Y:0.00},{j.Z:0.00})";
                    })));
                }
                return;
            }
            if (mode == "frames")
            {
                // the shell's corners through its own bind matrices and through the body's (by node index, as it's posed)
                foreach ((string iceName, string bodyName) in new[] { ("samus_ice", "Samus_lod0"), ("nox_ice", "Nox_lod0") })
                {
                    Model ice = Read.GetModelInstance(iceName, noCache: true).Model, body = Read.GetModelInstance(bodyName, noCache: true).Model;
                    ice.ComputeNodeMatrices(0);
                    body.ComputeNodeMatrices(0);
                    float worst = 0, sum = 0;
                    int n = 0;
                    foreach (Mesh mesh in ice.Meshes)
                    {
                        float[] v = MphRecomp.Render.DsDisplayList.Decode(ice, mesh, isRoom: false);
                        for (int o = 0; o + MphRecomp.Render.DsDisplayList.Stride <= v.Length; o += MphRecomp.Render.DsDisplayList.Stride)
                        {
                            int node = ice.NodeMatrixIds[(int)v[o + 12]];
                            var p = new Vector3(v[o], v[o + 1], v[o + 2]);
                            Vector3 own = Vector3.TransformPosition(p, ice.Nodes[node].Transform) * ice.Scale.X;
                            Vector3 bod = Vector3.TransformPosition(p, body.Nodes[node].Transform) * body.Scale.X;
                            float d = (own - bod).Length;
                            worst = MathF.Max(worst, d);
                            sum += d;
                            n++;
                        }
                    }
                    Console.WriteLine($"{iceName} on {bodyName}: own vs body frames mean {sum / n:0.0000} worst {worst:0.0000} ({n} corners)");
                    // posed as the game poses it: the body animated, the shell given the body's node matrices by index
                    ModelInstance inst = Read.GetModelInstance(bodyName, noCache: true);
                    Model bm = inst.Model;
                    foreach (int anim in new[] { 0, 1, 4 })
                    {
                        if (anim >= bm.AnimationGroups.Node.Count) continue;
                        inst.SetAnimation(anim, slot: 0, SetFlags.Node);
                        inst.AnimInfo.Frame[0] = 0;
                        bm.ComputeNodeMatrices(0);
                        bm.AnimateNodes(0, false, Matrix4.Identity, bm.Scale, inst.AnimInfo);
                        List<Vector3[]> bodyTris = new();
                        foreach (Mesh mesh in bm.Meshes)
                        {
                            float[] v = MphRecomp.Render.DsDisplayList.Decode(bm, mesh, isRoom: false);
                            int st = MphRecomp.Render.DsDisplayList.Stride;
                            for (int o = 0; o + 3 * st <= v.Length; o += 3 * st)
                            {
                                bodyTris.Add(Enumerable.Range(0, 3).Select(k => Vector3.TransformPosition(new Vector3(v[o + k * st], v[o + k * st + 1], v[o + k * st + 2]),
                                    bm.Nodes[bm.NodeMatrixIds[(int)v[o + k * st + 12]]].Animation)).ToArray());
                            }
                        }
                        var dist = new List<float>();
                        foreach (Mesh mesh in ice.Meshes)
                        {
                            float[] v = MphRecomp.Render.DsDisplayList.Decode(ice, mesh, isRoom: false);
                            for (int o = 0; o + MphRecomp.Render.DsDisplayList.Stride <= v.Length; o += MphRecomp.Render.DsDisplayList.Stride)
                            {
                                Vector3 w = Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), bm.Nodes[ice.NodeMatrixIds[(int)v[o + 12]]].Animation);
                                dist.Add(bodyTris.Min(t => (w - Closest(w, t[0], t[1], t[2])).Length));
                            }
                        }
                        dist.Sort();
                        Console.WriteLine($"  posed anim {anim}: shell to body surface median {dist[dist.Count / 2]:0.0000} p90 {dist[dist.Count * 9 / 10]:0.0000} max {dist[^1]:0.0000}");
                    }
                }
                return;
            }
            if (mode == "dump")
            {
                foreach (string name in new[] { "samus_ice", "nox_ice" })
                {
                    Model m = Read.GetModelInstance(name).Model;
                    Console.WriteLine($"== {name}: {m.Meshes.Count} meshes, {m.Materials.Count} materials, {m.Recolors.Count} recolors, "
                        + $"{m.Recolors[0].Textures.Count} textures, {m.Recolors[0].Palettes.Count} palettes, tex mtx {m.TextureMatrices.Count}, "
                        + $"anim groups node {m.AnimationGroups.Node.Count} mat {m.AnimationGroups.Material.Count} "
                        + $"texcoord {m.AnimationGroups.Texcoord.Count} tex {m.AnimationGroups.Texture.Count}");
                    foreach (Material mat in m.Materials)
                    {
                        Console.WriteLine($"  mat {mat.Name}: tex {mat.TextureId} pal {mat.PaletteId} light {mat.Lighting} cull {mat.Culling} "
                            + $"alpha {mat.Alpha} dif {mat.Diffuse.Red},{mat.Diffuse.Green},{mat.Diffuse.Blue} amb {mat.Ambient.Red},{mat.Ambient.Green},{mat.Ambient.Blue} "
                            + $"spec {mat.Specular.Red} poly {mat.PolygonMode} render {mat.RenderMode} texgen {mat.TexgenMode} mtx {mat.MatrixId} "
                            + $"scale {mat.ScaleS},{mat.ScaleT} rot {mat.RotateZ} tr {mat.TranslateS},{mat.TranslateT} anim {mat.AnimationFlags} tcanim {mat.TexcoordAnimationId}");
                    }
                    foreach (Mesh mesh in m.Meshes)
                    {
                        var codes = m.RenderInstructionLists[mesh.DlistId].GroupBy(i => i.Code).Select(g => $"{g.Key} {g.Count()}");
                        Console.WriteLine($"  mesh mat {mesh.MaterialId} dlist {mesh.DlistId}: {string.Join(", ", codes)}");
                    }
                    for (int i = 0; i < m.Nodes.Count; i++)
                    {
                        Node n = m.Nodes[i];
                        if (n.MeshCount > 0) Console.WriteLine($"  node {i} {n.Name}: meshes {n.MeshId}+{n.MeshCount}");
                    }
                    Texture t = m.Recolors[0].Textures[0];
                    Console.WriteLine($"  texture 0: {t.Width}x{t.Height} {t.Format}");
                    IReadOnlyList<ColorRgba> px = m.Recolors[0].GetPixels(0, 0);
                    Console.WriteLine($"  texture 0 alpha: mean {px.Average(c => c.Alpha):0} min {px.Min(c => c.Alpha)} max {px.Max(c => c.Alpha)}; rgb mean {px.Average(c => c.Red):0},{px.Average(c => c.Green):0},{px.Average(c => c.Blue):0}");
                }
                return;
            }
            if (mode == "check")
            {
                Check();
                return;
            }
            throw new ArgumentException($"unknown mode {mode}");
        }

        // every DS hunter (and the Dark Samus stand-in when its recipe is on hand) gets vanilla's shell fitted to it: the ice
        // material, the shell's own triangles, vanilla's shell exactly on the body it was made for, and posed as the game
        // poses it (anim 0 frame 0, the body's node matrices by the shell's matrix ids) its corners sit just outside the
        // body's surface, as vanilla's do on its own body (median 0.09-0.10)
        static void Check()
        {
            var bodies = new List<string> { "Samus_lod0", "Kanden_lod0", "Trace_lod0", "Sylux_lod0", "Nox_lod0", "Spire_lod0",
                "Weavel_lod0", "Guardian_lod0", MphRecomp.Multiplayer.DarkSamusStandin.ModelNames[0] };
            int fails = 0;
            string? only = Environment.GetEnvironmentVariable("FITTEDICE_ONLY"); // (one body: FITTEDICE_ONLY=Trace_lod0)
            foreach (string body in bodies.Where(b => only == null || b == only))
            {
                string iceName = MphRecomp.Render.FittedIce.IceFor(body);
                Model ice = Read.GetModelInstance(iceName).Model;
                int shellTris = MphRecomp.Render.FittedIce.ShellOf(ice).Length / 3;
                ModelInstance inst;
                try { inst = Read.GetModelInstance(body, noCache: true); }
                catch (Exception ex) when (body.StartsWith("DarkSamusStandin", StringComparison.Ordinal))
                {
                    Console.WriteLine($"  {body}: skipped ({ex.Message})");
                    continue;
                }
                Pose(inst);
                List<Vector3[]> surface = PosedTris(inst.Model, inst.Model, opaqueOnly: true);
                Stat vanilla = Gap(PosedTris(inst.Model, ice, opaqueOnly: false), surface);
                foreach (bool facets in new[] { false, true })
                {
                    string name = MphRecomp.Render.FittedIce.NameFor(body, facets);
                    Model fit = Read.GetModelInstance(name).Model;
                    Material mat = fit.Materials[0];
                    bool matOk = fit.Materials.Count == 1 && mat.TexgenMode == TexgenMode.Normal && mat.RenderMode == RenderMode.Translucent
                        && mat.Culling == CullingMode.Back && mat.Diffuse.Red == 31 && mat.Ambient.Red == 23 && mat.Lighting != 0
                        && fit.Recolors[0].Textures.Count > 0 && fit.Nodes.Count == inst.Model.Nodes.Count;
                    List<Vector3[]> shell = PosedTris(inst.Model, fit, opaqueOnly: false);
                    bool countOk = shell.Count == shellTris;
                    Stat gap = Gap(shell, surface);
                    bool same = true;
                    if (body == MphRecomp.Render.FittedIce.ReferenceFor(iceName))
                    {
                        // on its own body: vanilla's shell, corner for corner (display list rounding aside)
                        List<Vector3[]> own = PosedTris(inst.Model, ice, opaqueOnly: false);
                        same = own.Count == shell.Count && own.Zip(shell).All(p => Enumerable.Range(0, 3).All(k => (p.First[k] - p.Second[k]).Length < 0.003f));
                    }
                    bool gapOk = gap.Median > 0.02f && gap.Median < 0.2f && gap.P90 < 0.35f && gap.Max < 0.6f;
                    bool ok = matOk && countOk && same && gapOk;
                    if (!ok) fails++;
                    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {name,-40} tris {shell.Count,3} (shell {shellTris}) gap median {gap.Median:0.000} p90 {gap.P90:0.000} "
                        + $"max {gap.Max:0.000} inside {gap.Inside:0}% | vanilla shell here {vanilla.Median:0.000} / {vanilla.P90:0.000} / {vanilla.Max:0.000} inside {vanilla.Inside:0}%"
                        + $"{(same ? "" : " NOT VANILLA ON ITS OWN BODY")} material {(matOk ? "ice" : "WRONG")}");
                    if (!ok && !facets) Breakdown(inst.Model, fit, ice, surface);
                }
            }
            Console.WriteLine(fails == 0 ? "FITTED ICE CHECK PASS" : $"FITTED ICE CHECK FAIL ({fails})");
            if (fails != 0) Environment.ExitCode = 1;
        }

        static void Pose(ModelInstance inst)
        {
            Model m = inst.Model;
            inst.SetAnimation(0, slot: 0, SetFlags.Node);
            inst.AnimInfo.Frame[0] = 0;
            m.ComputeNodeMatrices(0);
            m.AnimateNodes(0, false, Matrix4.Identity, m.Scale, inst.AnimInfo);
        }

        // a model's triangles in the world, posed by the body's node animations through the model's own matrix ids
        static List<Vector3[]> PosedTris(Model body, Model m, bool opaqueOnly)
        {
            var tris = new List<Vector3[]>();
            int st = MphRecomp.Render.DsDisplayList.Stride;
            foreach (Mesh mesh in m.Meshes)
            {
                Material mat = m.Materials[mesh.MaterialId];
                if (opaqueOnly && (mat.RenderMode == RenderMode.Translucent && mat.Alpha < 31 || mat.Alpha == 0)) continue;
                float[] v = MphRecomp.Render.DsDisplayList.Decode(m, mesh, isRoom: false);
                for (int o = 0; o + 3 * st <= v.Length; o += 3 * st)
                {
                    tris.Add(Enumerable.Range(0, 3).Select(k => Vector3.TransformPosition(new Vector3(v[o + k * st], v[o + k * st + 1], v[o + k * st + 2]),
                        body.Nodes[m.NodeMatrixIds[(int)v[o + k * st + 12]]].Animation)).ToArray());
                }
            }
            return tris;
        }

        // a failing body's gap per node: the fitted shell's and vanilla's
        static void Breakdown(Model body, Model fit, Model ice, List<Vector3[]> surface)
        {
            foreach (Model m in new[] { fit, ice })
            {
                var byNode = new SortedDictionary<int, List<Vector3[]>>();
                int st = MphRecomp.Render.DsDisplayList.Stride;
                foreach (Mesh mesh in m.Meshes)
                {
                    float[] v = MphRecomp.Render.DsDisplayList.Decode(m, mesh, isRoom: false);
                    for (int o = 0; o + st <= v.Length; o += st)
                    {
                        int node = m.NodeMatrixIds[(int)v[o + 12]];
                        if (!byNode.TryGetValue(node, out List<Vector3[]>? l)) byNode[node] = l = new();
                        l.Add(new[] { Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), body.Nodes[node].Animation) });
                    }
                }
                Console.WriteLine($"    {(m == fit ? "fitted " : "vanilla")}: " + string.Join(" ", byNode.Select(k =>
                {
                    Stat g = Gap(k.Value, surface);
                    return $"{body.Nodes[k.Key].Name} {g.Median:0.00}/{g.Max:0.00}";
                })));
            }
        }

        record struct Stat(float Median, float P90, float Max, float Inside);

        // each shell corner's distance to the nearest point of the surface (inside = on the inner side of that triangle)
        static Stat Gap(List<Vector3[]> shell, List<Vector3[]> surface)
        {
            var d = new List<float>();
            int inside = 0;
            foreach (Vector3 w in shell.SelectMany(t => t))
            {
                float best = float.MaxValue;
                Vector3[]? bt = null;
                Vector3 bq = default;
                foreach (Vector3[] t in surface)
                {
                    Vector3 q = Closest(w, t[0], t[1], t[2]);
                    float dd = (w - q).LengthSquared;
                    if (dd < best) { best = dd; bt = t; bq = q; }
                }
                d.Add(MathF.Sqrt(best));
                if (bt != null && Vector3.Dot(w - bq, Vector3.Cross(bt[1] - bt[0], bt[2] - bt[0])) < 0) inside++;
            }
            d.Sort();
            return d.Count == 0 ? new Stat(0, 0, 0, 0) : new Stat(d[d.Count / 2], d[d.Count * 9 / 10], d[^1], inside * 100f / d.Count);
        }

        sealed class Corner
        {
            public int Node;
            public Vector3 World, Normal;
        }

        static List<Corner[]> Triangles(Model m) => m.Meshes.SelectMany(mesh => TrianglesOf(m, mesh)).ToList();

        static List<Corner[]> TrianglesOf(Model m, Mesh mesh)
        {
            var tris = new List<Corner[]>();
            {
                float[] v = MphRecomp.Render.DsDisplayList.Decode(m, mesh, isRoom: false);
                int n = v.Length / MphRecomp.Render.DsDisplayList.Stride;
                for (int t = 0; t + 2 < n; t += 3)
                {
                    var tri = new Corner[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int o = (t + k) * MphRecomp.Render.DsDisplayList.Stride;
                        int node = m.NodeMatrixIds[(int)v[o + 12]];
                        Matrix4 x = m.Nodes[node].Transform;
                        tri[k] = new Corner
                        {
                            Node = node,
                            World = Vector3.TransformPosition(new Vector3(v[o], v[o + 1], v[o + 2]), x) * m.Scale.X,
                            Normal = Vector3.TransformNormal(new Vector3(v[o + 3], v[o + 4], v[o + 5]), x).Normalized(),
                        };
                    }
                    tris.Add(tri);
                }
            }
            return tris;
        }

        static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            float den = 1 / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }

        static void Measure(string iceName, string bodyName)
        {
            Model ice = Read.GetModelInstance(iceName).Model;
            Model body = Read.GetModelInstance(bodyName).Model;
            ice.ComputeNodeMatrices(0);
            body.ComputeNodeMatrices(0);
            List<Corner[]> iceTris = Triangles(ice), bodyTris = Triangles(body);
            float minY = bodyTris.SelectMany(t => t).Min(c => c.World.Y), maxY = bodyTris.SelectMany(t => t).Max(c => c.World.Y);
            Console.WriteLine($"== {iceName} ({ice.Nodes.Count} nodes, {iceTris.Count} tris, scale {ice.Scale.X}) on {bodyName} "
                + $"({body.Nodes.Count} nodes, {bodyTris.Count} tris, scale {body.Scale.X}), body height {maxY - minY:0.000}");
            bool sameNodes = ice.Nodes.Count <= body.Nodes.Count
                && Enumerable.Range(0, ice.Nodes.Count).All(i => ice.Nodes[i].Name == body.Nodes[i].Name);
            Console.WriteLine($"  node names line up by index: {sameNodes}");
            // each ice corner (deduplicated) to the nearest body surface point: signed by the body triangle's own normal
            var seen = new HashSet<(int, int, int)>();
            var byNode = new Dictionary<int, List<float>>();
            var all = new List<float>();
            foreach (Corner c in iceTris.SelectMany(t => t))
            {
                var key = ((int)MathF.Round(c.World.X * 1e4f), (int)MathF.Round(c.World.Y * 1e4f), (int)MathF.Round(c.World.Z * 1e4f));
                if (!seen.Add(key)) continue;
                float best = float.MaxValue, sign = 1;
                foreach (Corner[] t in bodyTris)
                {
                    Vector3 q = Closest(c.World, t[0].World, t[1].World, t[2].World);
                    float d = (c.World - q).LengthSquared;
                    if (d < best)
                    {
                        best = d;
                        Vector3 fn = Vector3.Cross(t[1].World - t[0].World, t[2].World - t[0].World);
                        if (fn.LengthSquared < 1e-14f) fn = t[0].Normal + t[1].Normal + t[2].Normal;
                        sign = Vector3.Dot(c.World - q, fn) >= 0 ? 1 : -1;
                    }
                }
                float dist = MathF.Sqrt(best) * sign;
                all.Add(dist);
                if (!byNode.TryGetValue(c.Node, out List<float>? l)) byNode[c.Node] = l = new List<float>();
                l.Add(dist);
            }
            static string Stats(List<float> l)
            {
                var s = l.Order().ToList();
                float P(float q) => s[Math.Clamp((int)(q * (s.Count - 1)), 0, s.Count - 1)];
                return $"n {s.Count,4} mean {s.Average(),7:0.0000} median {P(0.5f),7:0.0000} p10 {P(0.1f),7:0.0000} p90 {P(0.9f),7:0.0000} "
                    + $"min {s[0],7:0.0000} max {s[^1],7:0.0000} inside {s.Count(x => x < 0) * 100f / s.Count,3:0}%";
            }
            Console.WriteLine($"  ALL           {Stats(all)}");
            Console.WriteLine($"  ALL / height  mean {all.Average() / (maxY - minY):0.0000} median {all.Order().ElementAt(all.Count / 2) / (maxY - minY):0.0000}");
            foreach ((int node, List<float> l) in byNode.OrderBy(k => k.Key))
            {
                Console.WriteLine($"  {ice.Nodes[node].Name,-12}  {Stats(l)}");
            }
        }
    }
}
