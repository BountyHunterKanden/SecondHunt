using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;

namespace MphRecomp.Assets
{
    // Minimal Collada (.dae) reader for USER-SUPPLIED HD models (e.g. hunter trophies the user
    // exported from their own Super Smash Bros. Brawl copy via BrawlCrate). Our own code, no
    // third-party dependency -> keeps the app clean + publishable; the Nintendo assets themselves
    // are never bundled, only read from the user's files at runtime.
    //
    // Scope: static geometry for rendering -- positions, normals, per-corner UVs, and each mesh's
    // material + resolved diffuse-texture file. Some trophies (Kanden, Sylux) are register-coloured
    // metal + a single reflection map; others (Weavel) get their colour from real diffuse textures.
    // This reader exposes both so the renderer can pick the right path per material. Skin/skeleton
    // are ignored for now (bind pose); they come in when we retarget to MPH's rig.
    public sealed class DaeMesh
    {
        public string Material = "";
        public string? TextureFile;             // resolved diffuse image file for this material, if any
        public string? SpecFile;                // weathering/spec (_s) map declared by the material, if any
        public string? IncandFile;              // incandescence/emissive (_incand) map, if any
        public string? OpacityFile;             // opacity/cutout (_o) map declared by the material, if any
        public Vector4 FlatColor = Vector4.One; // single colour applied to the whole part (0..1 rgba)
        public readonly List<Vector3> Positions = new(); // one per triangle corner (already expanded)
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> Uvs = new();        // per corner; (0,0) when the mesh has no TEXCOORD
        public readonly List<Vector4> Colors = new();     // per corner vertex colour (rgba); FlatColor when the mesh has none
    }

    public sealed class DaeModel
    {
        public readonly List<DaeMesh> Meshes = new();
        public string? TextureFile;             // the first image (reflection map for metal trophies)
        public string? ReflectionFile;          // the image whose name looks like a reflection/env map
        public Vector3 Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity);
        public int TriangleCount => Meshes.Sum(m => m.Positions.Count) / 3;
        public Vector3 Size => Max - Min;

        static float[] Floats(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        static int[] Ints(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToArray();

        public static DaeModel Load(string path)
        {
            var doc = XDocument.Load(path);
            XNamespace c = doc.Root!.Name.Namespace; // tolerate documents with/without the collada ns
            var model = new DaeModel();

            // library_images: image id -> file. Also remember the first + the reflection map.
            var imgIdToFile = new Dictionary<string, string>();
            foreach (var im in doc.Descendants(c + "image"))
            {
                string id = (string?)im.Attribute("id") ?? "";
                string? file = im.Element(c + "init_from")?.Value?.Trim();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file)) continue;
                imgIdToFile[id] = file!;
                model.TextureFile ??= file;
                int rdot = file!.LastIndexOf('.'); string rstem = rdot > 0 ? file[..rdot] : file;
                if (file.IndexOf("reflect", StringComparison.OrdinalIgnoreCase) >= 0
                    || rstem.EndsWith("Ref", StringComparison.OrdinalIgnoreCase)) model.ReflectionFile = file;
            }
            model.ReflectionFile ??= model.TextureFile;

            // newparam surfaces (sid -> image id) and samplers (sid -> surface sid), collected globally
            // (sids are unique across the file), so we can walk sampler -> surface -> image.
            var surfToImg = new Dictionary<string, string>();
            var sampToSurf = new Dictionary<string, string>();
            foreach (var np in doc.Descendants(c + "newparam"))
            {
                string sid = (string?)np.Attribute("sid") ?? "";
                var surf = np.Element(c + "surface");
                var samp = np.Element(c + "sampler2D");
                if (surf != null) { var f = surf.Element(c + "init_from")?.Value?.Trim(); if (f != null) surfToImg[sid] = f; }
                if (samp != null) { var s = samp.Element(c + "source")?.Value?.Trim(); if (s != null) sampToSurf[sid] = s; }
            }
            // sampler sid -> image file (sampler -> surface -> image -> init_from). Global: sids are
            // consistent across effects, and each maps to one image.
            string? SamplerFile(string samplerSid)
            {
                string surf = sampToSurf.TryGetValue(samplerSid, out var s) ? s : samplerSid;
                string imgId = surfToImg.TryGetValue(surf, out var im) ? im : surf;
                return imgIdToFile.TryGetValue(imgId, out var file) ? file : null;
            }
            // per effect: the diffuse sampler (bound to <diffuse>) + every declared sampler (so we can
            // recover the spec/incand maps BrawlCrate declares but can't bind to Collada's phong model).
            var effectToDiffuse = new Dictionary<string, string>();
            var effectToSamplers = new Dictionary<string, List<string>>();
            foreach (var fx in doc.Descendants(c + "effect"))
            {
                string id = (string?)fx.Attribute("id") ?? "";
                if (id == "") continue;
                var tex = fx.Descendants(c + "diffuse").Descendants(c + "texture").FirstOrDefault()
                          ?? fx.Descendants(c + "texture").FirstOrDefault();
                string? samp = (string?)tex?.Attribute("texture");
                if (samp != null) effectToDiffuse[id] = samp;
                effectToSamplers[id] = fx.Descendants(c + "newparam")
                    .Where(np => np.Element(c + "sampler2D") != null)
                    .Select(np => (string?)np.Attribute("sid") ?? "").Where(s => s != "").ToList();
            }
            // material id -> effect id
            var matToEffect = new Dictionary<string, string>();
            foreach (var mat in doc.Descendants(c + "material"))
            {
                string id = (string?)mat.Attribute("id") ?? "";
                string? fx = (string?)mat.Element(c + "instance_effect")?.Attribute("url");
                if (id != "" && fx != null) matToEffect[id] = fx.TrimStart('#');
            }
            // resolve a material's diffuse file, and (by name convention) its spec (_s) / incand maps
            // from the effect's declared samplers.
            static string Stem(string file) { int dot = file.LastIndexOf('.'); return dot > 0 ? file[..dot] : file; }
            static bool Suffix(string file, string suffix) => Stem(file).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            // Texture ROLE by name, so we can pick the true base-colour map even when an exporter scrambles
            // the phong <diffuse> slot (BrawlCrate binds spec/aux maps there for some materials).
            static bool IsSpec(string f) { string s = Stem(f); return Suffix(f, "_s") || s.EndsWith("Spc", StringComparison.OrdinalIgnoreCase) || s.Equals("Spe", StringComparison.OrdinalIgnoreCase); }
            static bool IsRefl(string f) { string s = Stem(f); return f.IndexOf("reflect", StringComparison.OrdinalIgnoreCase) >= 0 || s.EndsWith("Ref", StringComparison.OrdinalIgnoreCase); }
            (string? diff, string? spec, string? incand, string? opacity) ResolveTextures(string materialId)
            {
                if (!matToEffect.TryGetValue(materialId, out var fx)) return (null, null, null, null);
                string? diff = null, spec = null, incand = null, opacity = null;
                if (effectToSamplers.TryGetValue(fx, out var samps))
                    foreach (var sid in samps)
                    {
                        string? f = SamplerFile(sid);
                        if (f == null) continue;
                        if (Suffix(f, "_incand")) incand ??= f;
                        else if (Suffix(f, "_o")) opacity ??= f;     // hair cutout mask (weavel_hair_o)
                        else if (IsSpec(f)) spec ??= f;              // spec/weathering (_s, *Spc, Spe)
                        else if (IsRefl(f)) { /* reflection/env map -> handled globally as ReflectionFile */ }
                        else diff ??= f;                             // first TRUE base-colour map (SamusTexA, weavel_c)
                    }
                // Only fall back to the exporter's <diffuse> binding if no base map was identified by role
                // -- that binding is unreliable (spec maps land in it), so the role pick wins when present.
                if (diff == null && effectToDiffuse.TryGetValue(fx, out var ds)) diff = SamplerFile(ds);
                return (diff, spec, incand, opacity);
            }

            foreach (XElement geom in doc.Descendants(c + "geometry"))
            {
                XElement? mesh = geom.Element(c + "mesh");
                if (mesh == null) continue;

                // sources: id -> (float array, stride)
                var sources = new Dictionary<string, (float[] data, int stride)>();
                foreach (XElement src in mesh.Elements(c + "source"))
                {
                    var arr = src.Element(c + "float_array");
                    var acc = src.Element(c + "technique_common")?.Element(c + "accessor");
                    if (arr == null || acc == null) continue;
                    int stride = int.Parse(acc.Attribute("stride")?.Value ?? "1", CultureInfo.InvariantCulture);
                    sources[src.Attribute("id")!.Value] = (Floats(arr.Value), stride);
                }
                // <vertices> maps the VERTEX input to a POSITION source
                var vertsEl = mesh.Element(c + "vertices");
                string? posSourceId = vertsEl?.Elements(c + "input")
                    .FirstOrDefault(i => (string?)i.Attribute("semantic") == "POSITION")?.Attribute("source")?.Value.TrimStart('#');

                // a single flat vertex colour if the mesh carries one (count==1 source with RGB/A params)
                Vector4 flat = Vector4.One;
                var colorSrc = sources.FirstOrDefault(kv => kv.Key.Contains("Color", StringComparison.OrdinalIgnoreCase));
                if (colorSrc.Value.data is { Length: >= 3 } cd)
                    flat = new Vector4(cd[0], cd[1], cd.Length > 2 ? cd[2] : 0, cd.Length > 3 ? cd[3] : 1f);

                foreach (XElement tris in mesh.Elements(c + "triangles").Concat(mesh.Elements(c + "polylist")))
                {
                    string matName = tris.Attribute("material")?.Value ?? "";
                    var (diffF, specF, incandF, opacityF) = ResolveTextures(matName);
                    var dm = new DaeMesh { Material = matName, FlatColor = flat, TextureFile = diffF, SpecFile = specF, IncandFile = incandF, OpacityFile = opacityF };
                    var inputs = tris.Elements(c + "input").ToList();
                    int stride = inputs.Max(i => int.Parse(i.Attribute("offset")!.Value, CultureInfo.InvariantCulture)) + 1;
                    int posOff = -1, normOff = -1, uvOff = -1, colOff = -1;
                    (float[] data, int stride) posArr = default, normArr = default, uvArr = default, colArr = default;
                    foreach (var inp in inputs)
                    {
                        string sem = (string)inp.Attribute("semantic")!;
                        int off = int.Parse(inp.Attribute("offset")!.Value, CultureInfo.InvariantCulture);
                        string srcId = ((string)inp.Attribute("source")!).TrimStart('#');
                        if (sem == "VERTEX") { posOff = off; if (posSourceId != null && sources.TryGetValue(posSourceId, out var ps)) posArr = ps; }
                        else if (sem == "NORMAL") { normOff = off; if (sources.TryGetValue(srcId, out var ns)) normArr = ns; }
                        else if (sem == "TEXCOORD" && uvOff < 0) { uvOff = off; if (sources.TryGetValue(srcId, out var us)) uvArr = us; }
                        else if (sem == "COLOR" && colOff < 0) { colOff = off; if (sources.TryGetValue(srcId, out var cs)) colArr = cs; }
                    }
                    if (posArr.data == null) continue;
                    int[] p = Ints(tris.Element(c + "p")!.Value);
                    int corners = p.Length / stride;
                    for (int i = 0; i < corners; i++)
                    {
                        int pi = p[i * stride + posOff];
                        var pos = new Vector3(posArr.data[pi * 3], posArr.data[pi * 3 + 1], posArr.data[pi * 3 + 2]);
                        Vector3 nrm = Vector3.UnitY;
                        if (normOff >= 0 && normArr.data != null)
                        {
                            int ni = p[i * stride + normOff];
                            nrm = new Vector3(normArr.data[ni * 3], normArr.data[ni * 3 + 1], normArr.data[ni * 3 + 2]);
                        }
                        Vector2 uv = Vector2.Zero;
                        if (uvOff >= 0 && uvArr.data != null)
                        {
                            int ui = p[i * stride + uvOff]; int us = uvArr.stride;
                            // Collada UV origin is bottom-left; flip V for GL textures.
                            uv = new Vector2(uvArr.data[ui * us], 1f - uvArr.data[ui * us + 1]);
                        }
                        Vector4 col = flat;
                        if (colOff >= 0 && colArr.data != null)
                        {
                            int ci = p[i * stride + colOff]; int cs = colArr.stride;
                            col = new Vector4(colArr.data[ci * cs], colArr.data[ci * cs + 1], colArr.data[ci * cs + 2], cs > 3 ? colArr.data[ci * cs + 3] : 1f);
                        }
                        dm.Positions.Add(pos); dm.Normals.Add(nrm); dm.Uvs.Add(uv); dm.Colors.Add(col);
                        model.Min = Vector3.Min(model.Min, pos); model.Max = Vector3.Max(model.Max, pos);
                    }
                    if (dm.Positions.Count > 0) model.Meshes.Add(dm);
                }
            }
            return model;
        }
    }
}
