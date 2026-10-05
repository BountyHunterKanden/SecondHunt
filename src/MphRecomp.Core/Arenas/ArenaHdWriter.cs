using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // The arena's own look for the device (docs/MP2_MULTIPLAYER_IMPORT.md phase 2): Echoes' world mesh with its Retro
    // materials as GX programs, drawn on the Odin by the same GX-TEV shader as the HD suit and the Prime guns
    // (MphRead.Android/CampaignArena.cs). Written next to the DS stand-in, in _archives/<archive>/hd/:
    //   arena.bin      "MPHARN1\0", floats per vertex (13), vertex count, batch count; batches (material, start, count,
    //                  pass: 0 opaque, 1 blended, 2 additive, 3 sky), sorted by pass then material; then the vertices:
    //                  position 3 (MPH units, as the collision), normal 3, uv0 2 (GX TEX0: the lightmap's short UVs on
    //                  lightmapped materials), uv1 2 (GX TEX1: the float UVs), colour 3 (white; blue packs alpha).
    //                  Sky batches are the area's sky model (AreaAttributes OverrideSky), centred on the origin: the
    //                  device draws them around the camera, behind everything, as Retro does.
    //   arena.gx.json  GxJson materials (the gun import's conversion, GunWriter.cs) + per layer "uv" (0 = TEX0 / aUv,
    //                  1 = TEX1 / aUv1, from the texgen's source) + per material "pass"; the sky's materials follow the
    //                  area's; "mips": each texture's mip count on the disc (1 = Retro samples it unmipmapped)
    //   <txtr>.png     the textures, rows as GX stores them (t = 0 is the first row, for both UV sets: the lightmap
    //                  atlases prove it, 0 of 46k Crossfire samples land on their magenta filler this way)
    public static class ArenaHdWriter
    {
        public const string Dir = "hd", Mesh = "arena.bin", Materials = "arena.gx.json";
        public const int FloatsPerVertex = 13;
        public const int SkyPass = 3;
        static readonly byte[] Magic = "MPHARN1\0"u8.ToArray();

        public sealed class Result
        {
            public int Triangles, Batches, Materials, Textures, Undecoded, SkyTriangles;
            public long Bytes;
        }

        // sky: the area's sky model (CMDL id), or uint.MaxValue for none
        public static Result Write(Mrea area, Pak pak, Func<float, float, float, Vector3> map, string outDir, uint sky = uint.MaxValue)
        {
            var res = new Result();
            Cmdl geo = area.ReadGeometry();
            Cmdl? skyModel = sky != uint.MaxValue && pak.Contains(sky) ? Cmdl.Load(pak.Get, sky) : null;
            Directory.CreateDirectory(outDir);
            int Pass(Cmdl.Material m) => m.BlendSrc == 1 && m.BlendDst == 0 ? 0 : m.BlendDst == 1 ? 2 : 1;
            var verts = new List<float>(geo.TriangleCount * 3 * FloatsPerVertex);
            var batches = new List<(int Mat, int Start, int Count, int Pass)>();
            float white = Render.GxShader.PackBlueAlpha(1, 1);
            Vector2 Uv(float[]? a, int i) => a != null && i >= 0 && 2 * i + 1 < a.Length ? new Vector2(a[2 * i], a[2 * i + 1]) : Vector2.Zero;
            // triangles grouped by (pass, material); a sky's triangles all go to the sky pass, opaque materials first
            void Emit(Cmdl g, int matBase, bool isSky)
            {
                var order = Enumerable.Range(0, g.TriangleCount)
                    .OrderBy(t => Pass(g.Materials[g.TriangleMaterial[t]])).ThenBy(t => g.TriangleMaterial[t]).ToList();
                foreach (int t in order)
                {
                    int mi = g.TriangleMaterial[t];
                    Cmdl.Material m = g.Materials[mi];
                    int pass = isSky ? SkyPass : Pass(m);
                    int vtx = verts.Count / FloatsPerVertex;
                    if (batches.Count == 0 || batches[^1].Mat != matBase + mi || batches[^1].Pass != pass) batches.Add((matBase + mi, vtx, 0, pass));
                    bool shortUv0 = (m.Flags & 0x2000) != 0;
                    for (int k = 0; k < 3; k++)
                    {
                        Cmdl.Corner c = g.Triangles[3 * t + k];
                        Vector3 p = map(g.Positions[3 * c.P], g.Positions[3 * c.P + 1], g.Positions[3 * c.P + 2]);
                        Vector3 n = 3 * c.N + 2 < g.Normals.Length ? map(g.Normals[3 * c.N], g.Normals[3 * c.N + 1], g.Normals[3 * c.N + 2]) : Vector3.UnitY;
                        if (n.LengthSquared > 1e-12f) n.Normalize();
                        Vector2 uv0 = shortUv0 ? Uv(g.ShortUvs, c.T0) : Uv(g.Uv0, c.T0);
                        Vector2 uv1 = Uv(g.Uv0, c.T1);
                        verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, uv0.X, uv0.Y, uv1.X, uv1.Y, 1f, 1f, white });
                    }
                    var b = batches[^1];
                    batches[^1] = (b.Mat, b.Start, b.Count + 3, b.Pass);
                }
            }
            Emit(geo, 0, false);
            res.Triangles = verts.Count / FloatsPerVertex / 3;
            if (skyModel != null)
            {
                Emit(skyModel, geo.Materials.Length, true);
                res.SkyTriangles = skyModel.TriangleCount;
            }
            res.Batches = batches.Count;

            using (var fs = File.Create(Path.Combine(outDir, Mesh)))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(FloatsPerVertex); w.Write(verts.Count / FloatsPerVertex); w.Write(batches.Count);
                foreach (var b in batches) { w.Write(b.Mat); w.Write(b.Start); w.Write(b.Count); w.Write(b.Pass); }
                foreach (float f in verts) w.Write(f);
                res.Bytes = fs.Length;
            }

            // materials: GunWriter's Retro -> GX conversion, plus which UV set each layer reads
            Cmdl.Material[] all = skyModel == null ? geo.Materials : geo.Materials.Concat(skyModel.Materials).ToArray();
            var mats = new List<object>();
            var lightmaps = new HashSet<uint>();
            for (int mi = 0; mi < all.Length; mi++)
            {
                Cmdl.Material m = all[mi];
                bool isSky = mi >= geo.Materials.Length;
                var konst = m.Konst.Select(c => (object)c.Select(x => x / 255.0).ToArray()).ToList();
                while (konst.Count < 4) konst.Add(new double[] { 0, 0, 0, 0 });
                var stages = m.Stages.Select(st => (object)new Dictionary<string, object>
                {
                    ["c"] = st.ColorIn, ["cop"] = st.ColorOp.Op, ["cbias"] = st.ColorOp.Bias, ["cscale"] = st.ColorOp.Scale, ["cclamp"] = st.ColorOp.Clamp, ["cdest"] = st.ColorOp.Out,
                    ["a"] = st.AlphaIn, ["aop"] = st.AlphaOp.Op, ["abias"] = st.AlphaOp.Bias, ["ascale"] = st.AlphaOp.Scale, ["aclamp"] = st.AlphaOp.Clamp, ["adest"] = st.AlphaOp.Out,
                    ["texmap"] = st.Tex == 255 ? -1 : st.Tex, ["texcoord"] = st.TexCoord, ["ras"] = st.Ras == 4 ? 0 : st.Ras == 5 ? 1 : 7,
                    ["kc"] = st.KColor, ["ka"] = st.KAlpha,
                }).ToList();
                var layers = new List<object>();
                for (int ti = 0; ti < m.TexIds.Length; ti++)
                {
                    List<int> tcs = m.Stages.Where(s => s.Tex == ti).Select(s => s.TexCoord).ToList();
                    int src = 4, mtx = 30;
                    if (tcs.Count > 0 && tcs[0] < m.TexGens.Length) { src = m.TexGens[tcs[0]].Src; mtx = m.TexGens[tcs[0]].Mtx; }
                    bool env = src is 0 or 1;
                    double[] scroll = { 0, 0 };
                    Cmdl.UvAnim? anim = mtx != 30 && mtx / 3 < m.UvAnims.Length ? m.UvAnims[mtx / 3] : null;
                    if (anim != null && anim.Mode == 2 && anim.Params.Length >= 4) scroll = new[] { anim.Params[2], -anim.Params[3] };
                    if (anim != null && anim.Mode is 0 or 1) env = true;
                    if (src == 4 && (m.Flags & 0x800) != 0) lightmaps.Add(m.TexIds[ti]);
                    layers.Add(new Dictionary<string, object>
                    {
                        ["name"] = $"{m.TexIds[ti]:x8}", ["texmap"] = ti, ["texcoord"] = tcs.Count > 0 ? tcs[0] : ti, ["env"] = env,
                        ["scroll"] = scroll, ["mtx"] = new[] { 1, 0, 0, 0, 1, 0 }, ["uv"] = src >= 5 ? 1 : 0,
                    });
                }
                int pass = isSky ? SkyPass : Pass(m);
                mats.Add(new Dictionary<string, object>
                {
                    ["name"] = isSky ? $"sky{mi - geo.Materials.Length}" : $"arena{mi}", ["stages"] = stages, ["konst"] = konst.Take(4).ToList(),
                    ["lit"] = m.Channels.Length > 0 && (m.Channels[0] & 1) != 0, ["layers"] = layers,
                    ["blend"] = Pass(m) != 0, ["blendSrc"] = m.BlendSrc, ["blendDst"] = m.BlendDst, ["depthWrite"] = !isSky && pass == 0,
                    ["alphaTest"] = (m.Flags & 0x20) != 0, ["cull"] = 2, ["pass"] = pass, ["flags"] = m.Flags,
                });
            }
            res.Materials = mats.Count;

            // textures, rows as stored; a lightmap atlas's magenta filler grown over from its charts' edges
            var mips = new SortedDictionary<string, int>();
            foreach (uint id in new SortedSet<uint>(all.SelectMany(m => m.TexIds)))
            {
                if (!pak.Contains(id)) { res.Undecoded++; continue; }
                byte[] txtr = pak.Get(id);
                byte[]? rgba = Txtr.Decode(txtr, out _, out int tw, out int th);
                if (rgba == null) { res.Undecoded++; continue; }
                if (lightmaps.Contains(id)) FillAtlas(rgba, tw, th);
                PngOut.Write(Path.Combine(outDir, $"{id:x8}.png"), rgba, tw, th);
                mips[$"{id:x8}"] = (int)Be.U32(txtr, 8);
                res.Textures++;
            }
            File.WriteAllText(Path.Combine(outDir, Materials), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["source"] = "Metroid Prime 2: Echoes, Metroid6.pak (the owner's disc; never shipped)",
                ["materials"] = mats,
                ["mips"] = mips,
            }));
            return res;
        }

        // Retro's lightmap atlases fill their unused texels with magenta (255, 0, 255). Each pass gives the filler texels
        // next to a chart that chart's edge colour, so filtering at a chart's border never mixes the filler in.
        static void FillAtlas(byte[] rgba, int w, int h, int passes = 8)
        {
            var filler = new bool[w * h];
            var todo = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (rgba[4 * i] == 255 && rgba[4 * i + 1] == 0 && rgba[4 * i + 2] == 255) { filler[i] = true; todo.Add(i); }
            }
            for (int pass = 0; pass < passes && todo.Count > 0; pass++)
            {
                var next = new List<int>();
                var grown = new List<(int I, int R, int G, int B)>();
                foreach (int i in todo)
                {
                    int x = i % w, y = i / w, r = 0, g = 0, b = 0, n = 0;
                    void Add(int j) { if (!filler[j]) { r += rgba[4 * j]; g += rgba[4 * j + 1]; b += rgba[4 * j + 2]; n++; } }
                    if (x > 0) Add(i - 1);
                    if (x < w - 1) Add(i + 1);
                    if (y > 0) Add(i - w);
                    if (y < h - 1) Add(i + w);
                    if (n > 0) grown.Add((i, r / n, g / n, b / n)); else next.Add(i);
                }
                foreach ((int i, int r, int g, int b) in grown)
                {
                    rgba[4 * i] = (byte)r; rgba[4 * i + 1] = (byte)g; rgba[4 * i + 2] = (byte)b;
                    filler[i] = false;
                }
                todo = next;
            }
        }
    }
}
