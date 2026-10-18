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
    //                  pass: 0 opaque, 1 blended, 2 additive, 3 sky, 4 prop), sorted by pass then material; then the
    //                  vertices: position 3 (MPH units, as the collision), normal 3, uv0 2 (GX TEX0: the lightmap's short
    //                  UVs on lightmapped materials), uv1 2 (GX TEX1: the float UVs), colour 3 (white; blue packs alpha).
    //                  Sky batches are the area's sky model (AreaAttributes OverrideSky), centred on the origin: the
    //                  device draws them around the camera, behind everything, as Retro does. Prop batches are the
    //                  weapon crate's model (EchoesCrates), about its own origin and at its actor scale: the device draws
    //                  it at each crate that's up (readers that predate it skip pass 4). Pass 5: the arena's fluid
    //                  surfaces (Dark Water: a quad at each pool's surface, both faces, with a material made here, see
    //                  Fluid). Passes 6 and up: one per breakable (EchoesBreakables), its model already placed where the
    //                  arena puts it; the device draws each while it's up (readers that predate them skip passes > 4).
    //   arena.gx.json  GxJson materials (the gun import's conversion, GunWriter.cs) + per layer "uv" (0 = TEX0 / aUv,
    //                  1 = TEX1 / aUv1, from the texgen's source) + per material "pass"; the sky's materials follow the
    //                  area's; "mips": each texture's mip count on the disc (1 = Retro samples it unmipmapped)
    //   <txtr>.png     the textures, rows as GX stores them (t = 0 is the first row, for both UV sets: the lightmap
    //                  atlases prove it, 0 of 46k Crossfire samples land on their magenta filler this way)
    public static class ArenaHdWriter
    {
        public const string Dir = "hd", Mesh = "arena.bin", Materials = "arena.gx.json";
        public const int FloatsPerVertex = 13;
        public const int SkyPass = 3, PropPass = 4, FluidPass = 5, FirstPlacedPass = 6;
        static readonly byte[] Magic = "MPHARN1\0"u8.ToArray();

        public sealed class Result
        {
            public int Triangles, Batches, Materials, Textures, Undecoded, SkyTriangles, PropTriangles, PlacedTriangles;
            public long Bytes;
            public Vector3 PropMin, PropMax;
        }

        // a model placed in the area (an actor's or platform's): its CMDL, the script object's transform (position,
        // rotation in degrees, scale: Echoes area space) and the pass it draws in
        public sealed record Placed(uint Model, float[] Transform, int Pass)
        {
            // a skinned model's rigid part (Shooting Gallery's turret, EchoesTurret.ImportLook): Skin is its CSKR, and only
            // the triangles that follow Bone (TriangleBone) are written, about Origin (the bone's bind position, model
            // space) and not placed by Transform; the device places them each frame
            public uint Skin { get; init; } = uint.MaxValue;
            public uint? Bone { get; init; }
            public Vector3 Origin { get; init; }
        }

        // the bone a skinned model's triangle follows: its first corner's heaviest (uint.MaxValue: not skinned)
        public static uint TriangleBone(Cmdl g, int t)
        {
            if (g.Skin == null) return uint.MaxValue;
            int p = g.Triangles[3 * t].P;
            if (p >= g.Skin.Count || g.Skin[p].Length == 0) return uint.MaxValue;
            (uint bone, float w) = g.Skin[p][0];
            foreach ((uint b, float x) in g.Skin[p]) if (x > w) (bone, w) = (b, x);
            return bone;
        }

        // a fluid's surface: the rectangle min..max (Echoes area space, metres, Z up) at height z, and its look
        public sealed record Fluid(float[] Min, float[] Max, float Z, FluidLook Look);

        // Retro's fluid shading, simplified to two TEV stages (judged by eye against Echoes): the colour map times the
        // base colour, plus the gloss map times the gloss colour, at alpha; each map tiled every Scale metres and
        // scrolled across in Cycle seconds
        public sealed record FluidLook(uint ColorMap, float ColorScale, float ColorCycle, uint GlossMap, float GlossScale, float GlossCycle,
            float[] Base, float[] Gloss, float Alpha);

        // where a placed model's point (normal) goes: Retro's actor transform, scale then X, Y, Z rotation then position
        public static Vector3 Place(float[] x, Vector3 p) => Turn(x, new Vector3(p.X * x[6], p.Y * x[7], p.Z * x[8])) + new Vector3(x[0], x[1], x[2]);

        public static Vector3 PlaceNormal(float[] x, Vector3 n) => Turn(x, new Vector3(n.X / x[6], n.Y / x[7], n.Z / x[8]));

        static Vector3 Turn(float[] x, Vector3 v)
        {
            float ax = MathHelper.DegreesToRadians(x[3]), ay = MathHelper.DegreesToRadians(x[4]), az = MathHelper.DegreesToRadians(x[5]);
            float c = MathF.Cos(ax), s = MathF.Sin(ax);
            v = new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
            c = MathF.Cos(ay); s = MathF.Sin(ay);
            v = new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
            c = MathF.Cos(az); s = MathF.Sin(az);
            return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        // sky: the area's sky model (CMDL id), or uint.MaxValue for none; prop: a model drawn at the arena's props (the
        // weapon crate), at propScale (its actor scale), or uint.MaxValue for none; placed: models placed in the area,
        // each in its own pass (the breakables); fluids: surfaces drawn in the fluid pass
        public static Result Write(Mrea area, Pak pak, Func<float, float, float, Vector3> map, string outDir, uint sky = uint.MaxValue,
            uint prop = uint.MaxValue, float propScale = 1, IReadOnlyList<Placed>? placed = null, IReadOnlyList<Fluid>? fluids = null)
        {
            var res = new Result();
            Cmdl geo = area.ReadGeometry();
            Cmdl? skyModel = sky != uint.MaxValue && pak.Contains(sky) ? Cmdl.Load(pak.Get, sky) : null;
            Cmdl? propModel = prop != uint.MaxValue && pak.Contains(prop) ? Cmdl.Load(pak.Get, prop) : null;
            Directory.CreateDirectory(outDir);
            int Pass(Cmdl.Material m) => m.BlendSrc == 1 && m.BlendDst == 0 ? 0 : m.BlendDst == 1 ? 2 : 1;
            var verts = new List<float>(geo.TriangleCount * 3 * FloatsPerVertex);
            var batches = new List<(int Mat, int Start, int Count, int Pass)>();
            float white = Render.GxShader.PackBlueAlpha(1, 1);
            Vector2 Uv(float[]? a, int i) => a != null && i >= 0 && 2 * i + 1 < a.Length ? new Vector2(a[2 * i], a[2 * i + 1]) : Vector2.Zero;
            // triangles grouped by (pass, material); a sky's (a prop's) triangles all go to the sky (prop) pass, opaque
            // materials first
            int Emit(Cmdl g, int matBase, int? onePass, float posScale = 1, float[]? place = null, uint? bone = null, Vector3 origin = default)
            {
                var order = Enumerable.Range(0, g.TriangleCount).Where(t => bone == null || TriangleBone(g, t) == bone)
                    .OrderBy(t => Pass(g.Materials[g.TriangleMaterial[t]])).ThenBy(t => g.TriangleMaterial[t]).ToList();
                foreach (int t in order)
                {
                    int mi = g.TriangleMaterial[t];
                    Cmdl.Material m = g.Materials[mi];
                    int pass = onePass ?? Pass(m);
                    int vtx = verts.Count / FloatsPerVertex;
                    if (batches.Count == 0 || batches[^1].Mat != matBase + mi || batches[^1].Pass != pass) batches.Add((matBase + mi, vtx, 0, pass));
                    bool shortUv0 = (m.Flags & 0x2000) != 0;
                    for (int k = 0; k < 3; k++)
                    {
                        Cmdl.Corner c = g.Triangles[3 * t + k];
                        var mp = new Vector3(g.Positions[3 * c.P], g.Positions[3 * c.P + 1], g.Positions[3 * c.P + 2]);
                        var mn = 3 * c.N + 2 < g.Normals.Length ? new Vector3(g.Normals[3 * c.N], g.Normals[3 * c.N + 1], g.Normals[3 * c.N + 2]) : Vector3.UnitZ;
                        if (bone != null) mp -= origin;
                        else if (place != null) { mp = Place(place, mp); mn = PlaceNormal(place, mn); }
                        Vector3 p = map(mp.X, mp.Y, mp.Z) * posScale;
                        Vector3 n = map(mn.X, mn.Y, mn.Z);
                        if (n.LengthSquared > 1e-12f) n.Normalize();
                        Vector2 uv0 = shortUv0 ? Uv(g.ShortUvs, c.T0) : Uv(g.Uv0, c.T0);
                        Vector2 uv1 = Uv(g.Uv0, c.T1);
                        verts.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, uv0.X, uv0.Y, uv1.X, uv1.Y, 1f, 1f, white });
                    }
                    var b = batches[^1];
                    batches[^1] = (b.Mat, b.Start, b.Count + 3, b.Pass);
                }
                return order.Count;
            }
            Emit(geo, 0, null);
            res.Triangles = verts.Count / FloatsPerVertex / 3;
            if (skyModel != null)
            {
                Emit(skyModel, geo.Materials.Length, SkyPass);
                res.SkyTriangles = skyModel.TriangleCount;
            }
            int propBase = geo.Materials.Length + (skyModel?.Materials.Length ?? 0);
            if (propModel != null)
            {
                int first = verts.Count;
                Emit(propModel, propBase, PropPass, propScale);
                res.PropTriangles = propModel.TriangleCount;
                res.PropMin = new Vector3(float.MaxValue);
                res.PropMax = new Vector3(float.MinValue);
                for (int i = first; i < verts.Count; i += FloatsPerVertex)
                {
                    var v = new Vector3(verts[i], verts[i + 1], verts[i + 2]);
                    res.PropMin = Vector3.ComponentMin(res.PropMin, v);
                    res.PropMax = Vector3.ComponentMax(res.PropMax, v);
                }
            }
            // the fluid surfaces: one material each (the look's maps), a quad both ways round at the surface; TEX1 tiles
            // the colour map, TEX0 the gloss map
            int fluidBase = propBase + (propModel?.Materials.Length ?? 0);
            IReadOnlyList<Fluid> fluidList = fluids ?? Array.Empty<Fluid>();
            for (int fi = 0; fi < fluidList.Count; fi++)
            {
                Fluid f = fluidList[fi];
                int vtx = verts.Count / FloatsPerVertex;
                Vector3 up = map(0, 0, 1).Normalized();
                void Corner(float x, float y)
                {
                    Vector3 p = map(x, y, f.Z);
                    verts.AddRange(new[] { p.X, p.Y, p.Z, up.X, up.Y, up.Z, x / f.Look.GlossScale, y / f.Look.GlossScale,
                        x / f.Look.ColorScale, y / f.Look.ColorScale, 1f, 1f, white });
                }
                float x0 = f.Min[0], y0 = f.Min[1], x1 = f.Max[0], y1 = f.Max[1];
                // counter-clockwise seen from above (map keeps Echoes' Z-up winding), then the same quad seen from below
                Corner(x0, y0); Corner(x1, y0); Corner(x1, y1); Corner(x0, y0); Corner(x1, y1); Corner(x0, y1);
                Corner(x0, y0); Corner(x1, y1); Corner(x1, y0); Corner(x0, y0); Corner(x0, y1); Corner(x1, y1);
                batches.Add((fluidBase + fi, vtx, 12, FluidPass));
            }
            // the placed models: each model's materials once, its triangles in its own pass per placement
            int placedBase = fluidBase + fluidList.Count;
            var placedModels = new List<(uint Id, uint Skin, Cmdl Model, int MatBase)>();
            foreach (Placed pl in placed ?? Array.Empty<Placed>())
            {
                int k = placedModels.FindIndex(m => m.Id == pl.Model && m.Skin == pl.Skin);
                if (k < 0)
                {
                    if (!pak.Contains(pl.Model) || pl.Skin != uint.MaxValue && !pak.Contains(pl.Skin)) continue;
                    placedModels.Add((pl.Model, pl.Skin, Cmdl.Load(pak.Get, pl.Model, pl.Skin == uint.MaxValue ? null : pl.Skin),
                        placedBase + placedModels.Sum(m => m.Model.Materials.Length)));
                    k = placedModels.Count - 1;
                }
                res.PlacedTriangles += Emit(placedModels[k].Model, placedModels[k].MatBase, pl.Pass, place: pl.Transform, bone: pl.Bone, origin: pl.Origin);
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
            Cmdl.Material[] all = geo.Materials.Concat(skyModel?.Materials ?? Array.Empty<Cmdl.Material>())
                .Concat(propModel?.Materials ?? Array.Empty<Cmdl.Material>()).ToArray();
            var mats = new List<object>();
            var lightmaps = new HashSet<uint>();
            void AddMat(Cmdl.Material m, int mi, bool isPlaced)
            {
                bool isProp = !isPlaced && mi >= propBase, isSky = !isPlaced && !isProp && mi >= geo.Materials.Length;
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
                    var layer = new Dictionary<string, object>
                    {
                        ["name"] = $"{m.TexIds[ti]:x8}", ["texmap"] = ti, ["texcoord"] = tcs.Count > 0 ? tcs[0] : ti, ["env"] = env,
                        ["scroll"] = scroll, ["mtx"] = new[] { 1, 0, 0, 0, 1, 0 }, ["uv"] = src >= 5 ? 1 : 0,
                    };
                    // a filmstrip (modes 4/5: the turret's four bars pulsing, Shooting Gallery): GxLayer.UvShift steps it
                    if (anim != null && anim.Mode is 4 or 5 && anim.Params.Length >= 4)
                    {
                        layer["strip"] = new double[] { anim.Mode, anim.Params[0], anim.Params[1], anim.Params[2], anim.Params[3] };
                    }
                    layers.Add(layer);
                }
                int pass = isPlaced ? FirstPlacedPass : isProp ? PropPass : isSky ? SkyPass : Pass(m);
                mats.Add(new Dictionary<string, object>
                {
                    ["name"] = isPlaced ? $"placed{mi}" : isProp ? $"prop{mi - propBase}" : isSky ? $"sky{mi - geo.Materials.Length}" : $"arena{mi}", ["stages"] = stages, ["konst"] = konst.Take(4).ToList(),
                    ["lit"] = m.Channels.Length > 0 && (m.Channels[0] & 1) != 0, ["layers"] = layers,
                    ["blend"] = Pass(m) != 0, ["blendSrc"] = m.BlendSrc, ["blendDst"] = m.BlendDst, ["depthWrite"] = !isSky && Pass(m) == 0,
                    ["alphaTest"] = (m.Flags & 0x20) != 0, ["cull"] = 2, ["pass"] = pass, ["flags"] = m.Flags,
                });
            }
            for (int mi = 0; mi < all.Length; mi++) AddMat(all[mi], mi, false);
            // the fluids' materials (made here: Retro's fluid shader isn't a CMDL material)
            foreach (Fluid f in fluidList)
            {
                FluidLook L = f.Look;
                static Dictionary<string, object> Stage(int[] c, int[] a, int tex, int kc) => new()
                {
                    ["c"] = c, ["cop"] = 0, ["cbias"] = 0, ["cscale"] = 0, ["cclamp"] = 1, ["cdest"] = 0,
                    ["a"] = a, ["aop"] = 0, ["abias"] = 0, ["ascale"] = 0, ["aclamp"] = 1, ["adest"] = 0,
                    ["texmap"] = tex, ["texcoord"] = tex, ["ras"] = 7, ["kc"] = kc, ["ka"] = 28,
                };
                // GX inputs: colour 8 TEXC, 14 KONST, 15 ZERO, 0 CPREV; alpha 6 KONST, 7 ZERO, 0 APREV; kc 12/13 = K0/K1 rgb,
                // ka 28 = K0 alpha
                var stages = new List<object>
                {
                    Stage(new[] { 15, 8, 14, 15 }, new[] { 7, 7, 7, 6 }, 0, 12),   // colour map x base
                    Stage(new[] { 15, 8, 14, 0 }, new[] { 7, 7, 7, 0 }, 1, 13),    // + gloss map x gloss
                };
                var konst = new List<object>
                {
                    new double[] { L.Base[0], L.Base[1], L.Base[2], L.Alpha }, new double[] { L.Gloss[0], L.Gloss[1], L.Gloss[2], 1 },
                    new double[] { 0, 0, 0, 0 }, new double[] { 0, 0, 0, 0 },
                };
                var layers = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = $"{L.ColorMap:x8}", ["texmap"] = 0, ["texcoord"] = 0, ["env"] = false,
                        ["scroll"] = new double[] { 1.0 / L.ColorCycle, 0 }, ["mtx"] = new[] { 1, 0, 0, 0, 1, 0 }, ["uv"] = 1 },
                    new Dictionary<string, object> { ["name"] = $"{L.GlossMap:x8}", ["texmap"] = 1, ["texcoord"] = 1, ["env"] = false,
                        ["scroll"] = new double[] { 0, 1.0 / L.GlossCycle }, ["mtx"] = new[] { 1, 0, 0, 0, 1, 0 }, ["uv"] = 0 },
                };
                mats.Add(new Dictionary<string, object>
                {
                    ["name"] = $"fluid{mats.Count - fluidBase}", ["stages"] = stages, ["konst"] = konst, ["lit"] = false, ["layers"] = layers,
                    ["blend"] = true, ["blendSrc"] = 4, ["blendDst"] = 5, ["depthWrite"] = false, ["alphaTest"] = false, ["cull"] = 0,
                    ["pass"] = FluidPass, ["flags"] = 0,
                });
            }
            // the placed models' materials
            Cmdl.Material[] placedAll = placedModels.SelectMany(m => m.Model.Materials).ToArray();
            for (int mi = 0; mi < placedAll.Length; mi++) AddMat(placedAll[mi], mi, true);
            res.Materials = mats.Count;

            // textures, rows as stored; a lightmap atlas's magenta filler grown over from its charts' edges
            var mips = new SortedDictionary<string, int>();
            var texIds = new SortedSet<uint>(all.Concat(placedAll).SelectMany(m => m.TexIds));
            foreach (Fluid f in fluidList) { texIds.Add(f.Look.ColorMap); texIds.Add(f.Look.GlossMap); }
            foreach (uint id in texIds)
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
