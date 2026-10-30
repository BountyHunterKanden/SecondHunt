using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MphRead;
using MphRead.Formats;
using OpenTK.Mathematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRecomp.Multiplayer
{
    // Limoki's Dark Samus stand-in (2026-10-27): her edit of MPH Samus lod0 with repainted textures and new Shoulders/Visor.
    // Built at load from the user's own Samus_lod0 plus recipes/standin/DarkSamus: the recipe holds ROM corner references
    // with her deltas, her own new vertices, and her five PNGs, never ROM geometry or pixels. Same skeleton as Samus, so
    // Samus's clips drive it. A source-hash mismatch (another ROM's Samus) or a missing recipe builds nothing and the
    // seat keeps the LookAlikes recolour.
    public static class DarkSamusStandin
    {
        public const string SourceModel = "Samus_lod0";
        // her body, then her morph ball (owner 10-27: her own sphere and paint, hung on SamusAlt_lod0's ball node), then
        // (dev only, --es dsball original) the previous ball paint (#120D22 base, blue lines B), for the owner to compare
        // with her second paint (10-28 20:12)
        public static readonly string[] ModelNames = { "DarkSamusStandin_lod0", "DarkSamusStandinAlt_lod0", "DarkSamusStandinAltOriginal_lod0" };
        static string ResourceDir(string name) => name == ModelNames[0] ? "recipes/standin/DarkSamus/" : "recipes/standin/DarkSamusBall/";

#pragma warning disable CA2255 // registers the model with MphRead's loader before any scene loads; no other caller needed
        [ModuleInitializer]
        internal static void Init()
        {
            Func<string, Model?>? previous = Read.HostBuildModel;
            Read.HostBuildModel = name => Array.IndexOf(ModelNames, name) >= 0 ? Build(null, name) : previous?.Invoke(name);
        }
#pragma warning restore CA2255

        static readonly bool?[] _available = new bool?[3];

        // whether the stand-in (body / ball) builds from this ROM (checked once; the build itself is cached by MphRead's
        // model cache)
        public static bool Available => Builds(0);
        public static bool BallAvailable => Builds(1);
        public static bool OriginalBallAvailable => Builds(2);

        static bool Builds(int i)
        {
            if (_available[i] == null)
            {
                try
                {
                    _available[i] = Read.GetModelInstance(ModelNames[i]).Model.Name == ModelNames[i];
                }
                catch (Exception)
                {
                    _available[i] = false;
                }
            }
            return _available[i]!.Value;
        }

        // the source mesh's identity: per mesh its material name, texture size and display list (integer data only)
        public static string SourceHash(Model rom)
        {
            var sb = new StringBuilder();
            foreach (Mesh mesh in rom.Meshes)
            {
                Material mat = rom.Materials[mesh.MaterialId];
                sb.Append(mat.Name).Append('|');
                if (mat.TextureId >= 0)
                {
                    Texture t = rom.Recolors[0].Textures[mat.TextureId];
                    sb.Append(t.Width).Append('x').Append(t.Height);
                }
                sb.Append('|');
                foreach (RenderInstruction ins in rom.RenderInstructionLists[mesh.DlistId])
                {
                    sb.Append(((uint)ins.Code).ToString("x"));
                    foreach (uint a in ins.Arguments) sb.Append(',').Append(a.ToString("x"));
                    sb.Append(';');
                }
                sb.Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }

        // dir: a recipe folder on disk (the dev tool), null = the one embedded in this library
        public static Model? Build(string? dir, string name, Action<string>? log = null)
        {
            Func<string, Stream?> open = dir != null
                ? file => File.Exists(Path.Combine(dir, file)) ? File.OpenRead(Path.Combine(dir, file)) : null
                : file => OpenEmbedded(ResourceDir(name) + file);
            if (name == ModelNames[2])
            {
                Func<string, Stream?> edited = open;
                open = file => edited(file switch { "ball_v2.png" => "ball_lines.png", "ball_v2_glow.png" => "ball_glow.png", _ => file });
            }
            try
            {
                return BuildFrom(open, name, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Dark Samus stand-in: not built ({ex.GetType().Name}: {ex.Message})");
                return null;
            }
        }

        // %(RecursiveDir) puts the build machine's separator into the resource name
        static Stream? OpenEmbedded(string path)
        {
            Assembly asm = typeof(DarkSamusStandin).Assembly;
            string? found = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == path);
            return found == null ? null : asm.GetManifestResourceStream(found);
        }

        sealed class Corner
        {
            public int Matrix;
            public Vector3 Pos, Normal;
            public Vector2 Uv;
            public float R, G, B, Mode; // mode: 1 COLOR, 0 DIF_AMB, 2 none (DsDisplayList's colour alpha)
        }

        static Model? BuildFrom(Func<string, Stream?> open, string name, Action<string>? log)
        {
            using Stream? rs = open("recipe.json");
            if (rs == null)
            {
                log?.Invoke("Dark Samus stand-in: no recipe");
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(rs);
            JsonElement root = doc.RootElement;
            Model src = Read.GetModelInstance(root.GetProperty("source").GetString()!).Model;
            string hash = SourceHash(src);
            var hashes = new List<string> { root.GetProperty("sourceHash").GetString()! };
            if (root.TryGetProperty("sourceHashes", out JsonElement more))
            {
                hashes.AddRange(more.EnumerateArray().Select(e => e.GetString()!));
            }
            if (!hashes.Contains(hash))
            {
                log?.Invoke($"Dark Samus stand-in: source {src.Name} hash {hash} isn't one the recipe was made from");
                return null;
            }

            // the ROM's corners in DsDisplayList order (what the recipe's [mesh, index] refer to)
            var romCorners = new List<Corner[]>();
            var romDifAmb = new List<uint>();   // per ROM mesh: its most used DIF_AMB argument (ambient + flags for new corners)
            for (int m = 0; m < src.Meshes.Count; m++)
            {
                float[] v = Render.DsDisplayList.Decode(src, src.Meshes[m], isRoom: false);
                var list = new Corner[v.Length / Render.DsDisplayList.Stride];
                for (int i = 0; i < list.Length; i++)
                {
                    int o = i * Render.DsDisplayList.Stride;
                    list[i] = new Corner
                    {
                        Pos = new Vector3(v[o], v[o + 1], v[o + 2]), Normal = new Vector3(v[o + 3], v[o + 4], v[o + 5]),
                        R = v[o + 6], G = v[o + 7], B = v[o + 8], Mode = v[o + 9],
                        Uv = new Vector2(v[o + 10], v[o + 11]), Matrix = (int)v[o + 12],
                    };
                }
                romCorners.Add(list);
                romDifAmb.Add(src.RenderInstructionLists[src.Meshes[m].DlistId].Where(i => i.Code == InstructionCode.DIF_AMB)
                    .GroupBy(i => i.Arguments[0]).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(0x8000u | (0x18u << 16)));
            }

            // "lift": her base textures brightened (colour kept): Limoki's navy reads near black under the DS lights,
            // where Blender's shading still shows her shape (owner 10-28)
            float lift = root.TryGetProperty("lift", out JsonElement jl) ? jl.GetSingle() : 1f;
            var meshes = root.GetProperty("meshes").EnumerateArray().ToList();
            var rawMaterials = new List<RawMaterial>();
            var rawMeshes = new List<RawMesh>();
            var dlists = new List<DisplayList>();
            var lists = new List<IReadOnlyList<RenderInstruction>>();
            var textures = new List<Texture>();
            var textureData = new List<IReadOnlyList<TextureData>>();
            int tris = 0;
            for (int k = 0; k < meshes.Count; k++)
            {
                JsonElement jm = meshes[k];
                string matName = jm.GetProperty("material").GetString()!;
                string baseName = jm.GetProperty("base").GetString()!;
                int baseMesh = Enumerable.Range(0, src.Meshes.Count).First(m => src.Materials[src.Meshes[m].MaterialId].Name == baseName);
                Material baseMat = src.Materials[src.Meshes[baseMesh].MaterialId];

                (Texture tex, List<TextureData> data) = LoadPng(open, jm.GetProperty("image").GetString()!, lift: lift);
                textures.Add(tex);
                textureData.Add(data);

                bool newPart = true;
                var corners = new List<Corner>();
                foreach (JsonElement jc in jm.GetProperty("corners").EnumerateArray())
                {
                    int a = jc[0].GetInt32(), b = jc[1].GetInt32();
                    float F(int i) => jc.GetArrayLength() > i ? jc[i].GetSingle() : 0f;
                    if (a >= 0)
                    {
                        newPart = false;
                        Corner r = romCorners[a][b];
                        corners.Add(new Corner
                        {
                            Matrix = r.Matrix, Pos = r.Pos + new Vector3(F(2), F(3), F(4)), Normal = r.Normal,
                            Uv = r.Uv + new Vector2(F(5), F(6)), R = r.R + F(7), G = r.G + F(8), B = r.B + F(9),
                            Mode = r.Mode,
                        });
                    }
                    else
                    {
                        int matrix = -1;
                        for (int i = 0; i < src.NodeMatrixIds.Count; i++)
                        {
                            if (src.NodeMatrixIds[i] == b) { matrix = i; break; }
                        }
                        if (matrix < 0)
                        {
                            throw new InvalidDataException($"node {b} has no matrix id");
                        }
                        corners.Add(new Corner
                        {
                            Matrix = matrix, Pos = new Vector3(F(2), F(3), F(4)), Uv = new Vector2(F(5), F(6)),
                            R = F(7), G = F(8), B = F(9), Normal = new Vector3(F(10), F(11), F(12)),
                            Mode = romCorners[baseMesh].Length > 0 ? romCorners[baseMesh].GroupBy(c => c.Mode).MaxBy(g => g.Count())!.Key : 2,
                        });
                    }
                }
                tris += corners.Count / 3;

                bool lit = baseMat.InitLighting != 0;
                CullingMode culling = newPart ? CullingMode.Neither : baseMat.Culling;
                // "texgen": "none": her own uvs on a base that maps its texture by normals (the ball's reflection material)
                bool ownUv = jm.TryGetProperty("texgen", out JsonElement jt) && jt.GetString() == "none";
                lists.Add(DisplayListFor(corners, lit, tex.Width, tex.Height, romDifAmb[baseMesh]));
                dlists.Add(src.DisplayLists[src.Meshes[baseMesh].DlistId]);
                rawMaterials.Add(MaterialRaw(baseMat, matName, textures.Count - 1, lit ? baseMat.InitLighting : (byte)0, culling, ownUv));
                rawMeshes.Add(MeshRaw(rawMaterials.Count - 1, lists.Count - 1));

                // her glow map (v2): the same corners again, unlit and white, under the glow PNG's own alpha, so the masked
                // parts draw full-bright over the body in the translucent pass (its depth test is LEQUAL: same depth passes)
                if (jm.TryGetProperty("glow", out JsonElement jg))
                {
                    float strength = jm.TryGetProperty("glowStrength", out JsonElement js) ? js.GetSingle() : 1f;
                    (Texture glowTex, List<TextureData> glowData) = LoadPng(open, jg.GetString()!, strength);
                    textures.Add(glowTex);
                    textureData.Add(glowData);
                    // only the triangles whose texture box holds some glow (the body's map lights a few percent of it)
                    var bright = new List<Corner>();
                    for (int t = 0; t + 2 < corners.Count; t += 3)
                    {
                        if (!Glows(glowData, glowTex.Width, glowTex.Height, corners[t].Uv, corners[t + 1].Uv, corners[t + 2].Uv)) continue;
                        for (int c = t; c < t + 3; c++)
                        {
                            Corner r = corners[c];
                            bright.Add(new Corner { Matrix = r.Matrix, Pos = r.Pos, Normal = r.Normal, Uv = r.Uv, R = 1, G = 1, B = 1, Mode = 1 });
                        }
                    }
                    lists.Add(DisplayListFor(bright, lit: false, glowTex.Width, glowTex.Height, romDifAmb[baseMesh]));
                    dlists.Add(src.DisplayLists[src.Meshes[baseMesh].DlistId]);
                    rawMaterials.Add(MaterialRaw(baseMat, matName + "_glow", textures.Count - 1, 0, culling, ownUv));
                    rawMeshes.Add(MeshRaw(rawMaterials.Count - 1, lists.Count - 1));
                    tris += bright.Count / 3;
                }
            }

            Header header = Patch(src.Header, (72, (ushort)rawMaterials.Count), (96, (ushort)rawMeshes.Count),
                (40, (ushort)textures.Count), (48, (ushort)0));
            var nodes = src.RawNodes.ToList();
            bool hung = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                // every mesh hangs off the first node the ROM's meshes did (Samus: Dummy_Root, her ball: MorphBall; the
                // vanilla ball's glow plane node gets none): vertices pick their bone by matrix id
                if (nodes[i].MeshCount == 0) continue;
                nodes[i] = Patch(nodes[i], (76, (ushort)(hung ? 0 : rawMeshes.Count)), (78, (ushort)0));
                hung = true;
            }
            var recolor = new Recolor("limoki", textures, Array.Empty<Palette>(), textureData, Array.Empty<IReadOnlyList<PaletteData>>());
            // one look for every recolour slot MPH gives the source (the seat's recolour index stays in range), then the
            // host's extra sets made from hers (LookAlikes: Dark Samus's team and 2nd-8th copy colours)
            int own = Metadata.GetModelByName(src.Name)?.Recolors.Count ?? src.Recolors.Count;
            var recolors = Enumerable.Repeat(recolor, Math.Max(1, own)).ToList();
            if (Read.HostExtraRecolors?.Invoke(name, recolors) is IEnumerable<Recolor> extra) recolors.AddRange(extra);
            var anims = new AnimationResults();
            anims.NodeAnimationGroups.AddRange(src.AnimationGroups.Node);
            anims.MaterialAnimationGroups.AddRange(src.AnimationGroups.Material);
            anims.TexcoordAnimationGroups.AddRange(src.AnimationGroups.Texcoord);
            anims.TextureAnimationGroups.AddRange(src.AnimationGroups.Texture);
            anims.NodeGroupOffsets.AddRange(src.AnimationGroups.Offsets.Node);
            anims.MaterialGroupOffsets.AddRange(src.AnimationGroups.Offsets.Material);
            anims.TexcoordGroupOffsets.AddRange(src.AnimationGroups.Offsets.Texcoord);
            anims.TextureGroupOffsets.AddRange(src.AnimationGroups.Offsets.Texture);
            log?.Invoke($"Dark Samus stand-in: built {name} from {src.Name} ({hash[..12]}): {rawMeshes.Count} meshes, {tris} tris, {textures.Count} textures");
            return new Model(name, false, header, nodes, rawMeshes, rawMaterials, dlists, lists, anims,
                src.TextureMatrices, recolors, src.NodeMatrixIds, src.NodePos, src.NodeInitPos, src.NodePosCounts, src.NodePosScales);
        }

        static List<RenderInstruction> DisplayListFor(List<Corner> corners, bool lit, int width, int height, uint difAmb)
        {
            var list = new List<RenderInstruction>(corners.Count * 5 + 4);
            int matrix = -1;
            uint colour = uint.MaxValue, normal = uint.MaxValue, tex = uint.MaxValue;
            bool open = false;
            foreach (Corner c in corners)
            {
                if (c.Matrix != matrix)
                {
                    // MPH restores the bone matrix between whole triangles; corners of one triangle never straddle a change
                    // here, since rigid one-bone weights put each triangle on one bone except at seams, where it's harmless
                    list.Add(new RenderInstruction(InstructionCode.MTX_RESTORE, (uint)c.Matrix));
                    matrix = c.Matrix;
                }
                if (!open)
                {
                    list.Add(new RenderInstruction(InstructionCode.BEGIN_VTXS, 0));
                    open = true;
                }
                uint rgb = Channel(c.R) | Channel(c.G) << 5 | Channel(c.B) << 10;
                if (c.Mode == 1f && rgb != colour)
                {
                    list.Add(new RenderInstruction(InstructionCode.COLOR, rgb));
                    colour = rgb;
                }
                else if (c.Mode == 0f && (difAmb & ~0x7FFFu | rgb) != colour)
                {
                    colour = difAmb & ~0x7FFFu | rgb;
                    list.Add(new RenderInstruction(InstructionCode.DIF_AMB, colour));
                }
                if (lit)
                {
                    uint n = Ten(c.Normal.X) | Ten(c.Normal.Y) << 10 | Ten(c.Normal.Z) << 20;
                    if (n != normal)
                    {
                        list.Add(new RenderInstruction(InstructionCode.NORMAL, n));
                        normal = n;
                    }
                }
                uint t = Texel(c.Uv.X * width) | Texel(c.Uv.Y * height) << 16;
                if (t != tex)
                {
                    list.Add(new RenderInstruction(InstructionCode.TEXCOORD, t));
                    tex = t;
                }
                list.Add(new RenderInstruction(InstructionCode.VTX_16, Fx(c.Pos.X) | Fx(c.Pos.Y) << 16, Fx(c.Pos.Z)));
            }
            if (open)
            {
                list.Add(new RenderInstruction(InstructionCode.END_VTXS));
            }
            return list;
        }

        // whether any texel in the uv box of a triangle has alpha (texture repeat wraps)
        static bool Glows(List<TextureData> data, int width, int height, Vector2 a, Vector2 b, Vector2 c)
        {
            int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) * width), x1 = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) * width);
            int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) * height), y1 = (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) * height);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (data[((y % height + height) % height) * width + (x % width + width) % width].Alpha > 0) return true;
                }
            }
            return false;
        }

        static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 31), 0, 31);

        static uint Ten(float v) => (uint)Math.Clamp((int)MathF.Round(v * 512), -512, 511) & 0x3FF;

        static uint Texel(float texels) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(texels * 16), short.MinValue, short.MaxValue);

        static uint Fx(float v) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(v * 4096), short.MinValue, short.MaxValue);

        // her PNG as a direct-colour texture (DS 1-bit alpha), its colour times lift (kept below the cap as a whole);
        // glow: a glow map drawn see-through over the body, standing in for Blender's added emission
        static (Texture, List<TextureData>) LoadPng(Func<string, Stream?> open, string file, float? glow = null, float lift = 1f)
        {
            static uint Five(float v) => (uint)Math.Clamp((int)MathF.Round(v / 255f * 31), 0, 31);
            using Stream s = open(file) ?? throw new FileNotFoundException(file);
            using Image<Rgba32> img = Image.Load<Rgba32>(s);
            var data = new List<TextureData>(img.Width * img.Height);
            for (int y = 0; y < img.Height; y++)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    Rgba32 p = img[x, y];
                    float max = Math.Max(p.R, Math.Max(p.G, p.B));
                    if (glow is float g)
                    {
                        // her glow map as she painted it, its colour at its own alpha over her armour (Blender's added
                        // glow at strength 1, near enough on her near-black paint). Her strengths aren't applied: every way
                        // of boosting them read brighter than her render (owner 10-28: "the glow is way too intense").
                        // Alpha at most 250: the app's layered draw puts a texel at 0.99+ in the solid pass (depth
                        // "less"), where the glow lies at her body's own depth and lost to it but in stripes (the
                        // shoulders' web); that cap is the fix, nothing else is changed
                        _ = g;
                        byte a = Math.Min(p.A, (byte)250);
                        uint gc = Five(p.R) | Five(p.G) << 5 | Five(p.B) << 10 | (a > 0 ? 0x8000u : 0);
                        data.Add(new TextureData(gc, a));
                        continue;
                    }
                    bool solid = p.A >= 128;
                    float l = max * lift > 255 ? 255f / max : lift;
                    uint c = Five(p.R * l) | Five(p.G * l) << 5 | Five(p.B * l) << 10 | (solid ? 0x8000u : 0);
                    data.Add(new TextureData(c, solid ? (byte)255 : (byte)0));
                }
            }
            return (new Texture(TextureFormat.DirectRgb, (ushort)img.Width, (ushort)img.Height), data);
        }

        // the base ROM material's settings under her name and texture (RawMaterial's layout, as ArenaModelWriter writes it)
        static RawMaterial MaterialRaw(Material m, string name, int texture, byte lighting, CullingMode culling, bool ownUv = false)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            var nameBytes = new byte[64];
            Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 63), nameBytes, 0);
            w.Write(nameBytes);
            w.Write(lighting);
            w.Write((byte)culling);
            w.Write(m.Alpha);
            w.Write(m.Wireframe);
            w.Write((short)-1); w.Write((short)texture);
            w.Write((byte)m.XRepeat); w.Write((byte)m.YRepeat);
            foreach (ColorRgb c in new[] { m.Diffuse, m.Ambient, m.Specular })
            {
                w.Write(c.Red); w.Write(c.Green); w.Write(c.Blue);
            }
            w.Write((byte)0);
            w.Write((uint)m.PolygonMode);
            w.Write((byte)m.RenderMode); w.Write((byte)m.AnimationFlags); w.Write((ushort)0);
            w.Write(ownUv ? (uint)TexgenMode.None : (uint)m.TexgenMode);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((uint)m.MatrixId);
            w.Write((int)MathF.Round(m.ScaleS * 4096)); w.Write((int)MathF.Round(m.ScaleT * 4096));
            w.Write((ushort)MathF.Round(m.RotateZ / (2f * MathF.PI) * 65536f)); w.Write((ushort)0);
            w.Write((int)MathF.Round(m.TranslateS * 4096)); w.Write((int)MathF.Round(m.TranslateT * 4096));
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0);
            return Read.ReadStruct<RawMaterial>(ms.ToArray());
        }

        static RawMesh MeshRaw(int material, int dlist)
        {
            var b = new byte[4];
            BitConverter.TryWriteBytes(b.AsSpan(0), (ushort)material);
            BitConverter.TryWriteBytes(b.AsSpan(2), (ushort)dlist);
            return Read.ReadStruct<RawMesh>(b);
        }

        // a copy of a raw struct with ushort fields replaced at byte offsets (RawFormats' sequential layout)
        static T Patch<T>(T value, params (int Offset, ushort Value)[] fields) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
            foreach ((int offset, ushort v) in fields)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(offset), v);
            }
            return Read.ReadStruct<T>(bytes);
        }
    }
}
