using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRead;
using MphRead.Formats;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // A DS room model for an imported arena, written with MphRead's own packer (Repack.PackModel): node "rmMain" (the
    // room part RoomEntity draws) over one geometry node, one untextured unlit material, triangles with per-corner
    // colours. It stands in until the HD arena draw exists, and it is what RoomEntity needs to set the room up.
    public static class ArenaModelWriter
    {
        public const string RoomNode = "rmMain";
        const int TrianglesPerMesh = 1024;

        // Material: -1 = the untextured unlit material; otherwise an index into the textured materials passed to Write.
        // Uv is in texture repeats (1 = the whole picture); the writer turns it into texels with a whole-repeat shift per triangle.
        public readonly record struct Corner(Vector3 Position, Vector3 Color, int Material = -1, Vector2 Uv = default);

        // a textured material: a DS texture + palette copied into the model, tiled, optionally sliding (whole repeats per 8 s loop)
        public sealed class TexMaterial
        {
            public string Name { get; init; } = "";
            public Texture Texture { get; init; }
            public IReadOnlyList<TextureData> Data { get; init; } = Array.Empty<TextureData>();
            public IReadOnlyList<PaletteData>? Palette { get; init; }
            public int ScrollS { get; init; }
            public int ScrollT { get; init; }
        }

        public const int ScrollFrames = 240;   // one scroll loop: 240 animation steps at 30 per second

        // corners: 3 per triangle, in MPH units. Returns (model file, animation file).
        public static (byte[] Model, byte[] Anim) Write(string name, IReadOnlyList<Corner> corners, IReadOnlyList<TexMaterial>? texMaterials = null)
        {
            texMaterials ??= Array.Empty<TexMaterial>();
            float maxAbs = 1;
            foreach (Corner c in corners)
            {
                maxAbs = MathF.Max(maxAbs, MathF.Max(MathF.Abs(c.Position.X), MathF.Max(MathF.Abs(c.Position.Y), MathF.Abs(c.Position.Z))));
            }
            // VTX_16 holds 4.12 fixed point (|v| < 8) times the model scale, a power of two
            int scale = 1;
            while (maxAbs / scale >= 7.9f) scale *= 2;

            // the model's materials: the untextured one (when used), then one per textured material in use
            int triCount = corners.Count / 3;
            var used = new SortedSet<int>();
            bool plain = false;
            for (int t = 0; t < triCount; t++)
            {
                int m = corners[t * 3].Material;
                if (m < 0) plain = true; else used.Add(m);
            }
            var materials = new List<Material>();
            var textures = new List<Repack.TextureInfo>();
            var palettes = new List<Repack.PaletteInfo>();
            var materialIndex = new Dictionary<int, int>();   // caller's material (-1 = untextured) -> model material
            if (plain || used.Count == 0)
            {
                materialIndex[-1] = materials.Count;
                materials.Add(new Material(MaterialRaw(name, -1, -1, repeat: false)));
            }
            var scrolling = new List<(string Name, int S, int T)>();
            foreach (int m in used)
            {
                TexMaterial tm = texMaterials[m];
                int texId = textures.Count;
                textures.Add(Repack.ConvertData(tm.Texture, tm.Data));
                int palId = -1;
                if (tm.Texture.Format != TextureFormat.DirectRgb && tm.Palette != null)
                {
                    palId = palettes.Count;
                    palettes.Add(new Repack.PaletteInfo(tm.Palette.Select(d => d.Data).ToList()));
                }
                materialIndex[m] = materials.Count;
                materials.Add(new Material(MaterialRaw(tm.Name, palId, texId, repeat: true)));
                if (tm.ScrollS != 0 || tm.ScrollT != 0) scrolling.Add((tm.Name, tm.ScrollS, tm.ScrollT));
            }

            // triangles grouped by material, a mesh per 1024
            var byMaterial = new SortedDictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int m = corners[t * 3].Material;
                if (!byMaterial.TryGetValue(m, out List<int>? tl)) byMaterial[m] = tl = new List<int>();
                tl.Add(t);
            }
            var renders = new List<IReadOnlyList<RenderInstruction>>();
            var meshes = new List<Mesh>();
            foreach ((int m, List<int> tris) in byMaterial)
            {
                int width = 1, height = 1;
                if (m >= 0)
                {
                    width = texMaterials[m].Texture.Width;
                    height = texMaterials[m].Texture.Height;
                }
                for (int first = 0; first < tris.Count; first += TrianglesPerMesh)
                {
                    int last = Math.Min(tris.Count, first + TrianglesPerMesh);
                    var list = new List<RenderInstruction> { new(InstructionCode.BEGIN_VTXS, 0) };
                    uint lastColor = uint.MaxValue;
                    for (int n = first; n < last; n++)
                    {
                        int t = tris[n];
                        // whole repeats cancelled per triangle so the texel coordinates stay small
                        float u0 = 0, v0 = 0;
                        if (m >= 0)
                        {
                            u0 = MathF.Floor(MathF.Min(corners[t * 3].Uv.X, MathF.Min(corners[t * 3 + 1].Uv.X, corners[t * 3 + 2].Uv.X)));
                            v0 = MathF.Floor(MathF.Min(corners[t * 3].Uv.Y, MathF.Min(corners[t * 3 + 1].Uv.Y, corners[t * 3 + 2].Uv.Y)));
                        }
                        for (int i = t * 3; i < t * 3 + 3; i++)
                        {
                            Corner c = corners[i];
                            uint color = Channel(c.Color.X) | Channel(c.Color.Y) << 5 | Channel(c.Color.Z) << 10;
                            if (color != lastColor)
                            {
                                list.Add(new RenderInstruction(InstructionCode.COLOR, color));
                                lastColor = color;
                            }
                            if (m >= 0)
                            {
                                uint us = Texel((c.Uv.X - u0) * width), vs = Texel((c.Uv.Y - v0) * height);
                                list.Add(new RenderInstruction(InstructionCode.TEXCOORD, us | vs << 16));
                            }
                            uint x = Fx(c.Position.X / scale), y = Fx(c.Position.Y / scale), z = Fx(c.Position.Z / scale);
                            list.Add(new RenderInstruction(InstructionCode.VTX_16, x | y << 16, z));
                        }
                    }
                    list.Add(new RenderInstruction(InstructionCode.END_VTXS));
                    while (list.Count % 4 != 0) list.Add(new RenderInstruction(InstructionCode.NOP));   // 4 commands per packed word
                    meshes.Add(new Mesh(MeshRaw(material: materialIndex[m], dlist: renders.Count)));
                    renders.Add(list);
                }
            }
            var nodes = new List<Node>
            {
                new(NodeRaw(RoomNode, parent: -1, child: 1, next: -1, meshCount: 0, meshId: 0)),
                new(NodeRaw("geo_arena", parent: 0, child: -1, next: -1, meshCount: meshes.Count, meshId: 0)),
            };
            var dlists = new List<DisplayList>();
            for (int i = 0; i < renders.Count; i++) dlists.Add(default);
            var options = new Repack.RepackOptions
            {
                Compare = false,
                ComputeBounds = Repack.ComputeBounds.Uncapped,
                IsRoom = true,
                Texture = Repack.RepackTexture.Inline,
                WriteFile = false
            };
            (byte[] model, _) = Repack.PackModel(scale, Array.Empty<int>(), Array.Empty<int>(), materials,
                textures, palettes, nodes, meshes, renders, dlists, options);
            byte[] anim = Repack.PackAnim(new NodeAnimationGroup?[] { null }, new MaterialAnimationGroup?[] { null },
                new TexcoordAnimationGroup?[] { ScrollGroup(scrolling) }, new TextureAnimationGroup?[] { null }, fhPad: false);
            return (model, anim);
        }

        // the lava / water slide: a texcoord animation group the room entity loops (30 steps a second)
        static TexcoordAnimationGroup? ScrollGroup(List<(string Name, int S, int T)> scrolling)
        {
            if (scrolling.Count == 0) return null;
            var translations = new List<float> { 0 };   // entry 0: standing still
            var animations = new Dictionary<string, TexcoordAnimation>();
            foreach ((string name, int sRepeats, int tRepeats) in scrolling)
            {
                int sIndex = 0, tIndex = 0;
                if (sRepeats != 0)
                {
                    sIndex = translations.Count;
                    for (int f = 0; f < ScrollFrames; f++) translations.Add(sRepeats * f / (float)ScrollFrames);
                }
                if (tRepeats != 0)
                {
                    tIndex = translations.Count;
                    for (int f = 0; f < ScrollFrames; f++) translations.Add(tRepeats * f / (float)ScrollFrames);
                }
                animations.Add(name, TexcoordRaw(name, sRepeats != 0 ? ScrollFrames : 1, tRepeats != 0 ? ScrollFrames : 1, sIndex, tIndex));
            }
            var raw = new byte[28];
            BitConverter.TryWriteBytes(raw.AsSpan(0), (uint)ScrollFrames);
            BitConverter.TryWriteBytes(raw.AsSpan(16), (uint)animations.Count);
            return new TexcoordAnimationGroup(Read.ReadStruct<RawTexcoordAnimationGroup>(raw), new List<float> { 1 }, new List<float> { 0 },
                translations, animations);
        }

        static TexcoordAnimation TexcoordRaw(string name, int lenS, int lenT, int indexS, int indexT)
        {
            var b = new byte[60];
            Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 31), b, 0);
            b[32] = 1; b[33] = 1;                                        // scale blend
            BitConverter.TryWriteBytes(b.AsSpan(34), (ushort)1);         // scale LUT: one entry (1.0), at index 0
            BitConverter.TryWriteBytes(b.AsSpan(36), (ushort)1);
            b[42] = 1; b[43] = 0xFF;                                     // rotate blend, MPH's 0xFF
            BitConverter.TryWriteBytes(b.AsSpan(44), (ushort)1);         // rotation LUT: one entry (0)
            b[48] = 1; b[49] = 1;                                        // translate blend
            BitConverter.TryWriteBytes(b.AsSpan(50), (ushort)lenS);
            BitConverter.TryWriteBytes(b.AsSpan(52), (ushort)lenT);
            BitConverter.TryWriteBytes(b.AsSpan(54), (ushort)indexS);
            BitConverter.TryWriteBytes(b.AsSpan(56), (ushort)indexT);
            return Read.ReadStruct<TexcoordAnimation>(b);
        }

        // 12.4 fixed point texels
        static uint Texel(float texels) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(texels * 16), -2048 * 16 + 1, 2047 * 16);

        static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 31), 0, 31);

        static uint Fx(float v) => (uint)(ushort)(short)Math.Clamp((int)MathF.Round(v * 4096), short.MinValue, short.MaxValue);

        // the raw structs, laid out as MphRead's RawFormats declares them (and Repack.WriteNode / WriteMaterial write them)
        static RawNode NodeRaw(string name, short parent, short child, short next, int meshCount, int meshId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteName(w, name);
            w.Write(parent); w.Write(child); w.Write(next); w.Write((ushort)0);
            w.Write(1u);                                     // enabled
            w.Write((ushort)meshCount); w.Write((ushort)meshId);
            w.Write(4096); w.Write(4096); w.Write(4096);     // scale
            w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((ushort)0);
            w.Write(0); w.Write(0); w.Write(0);              // position
            w.Write(0);                                      // bounding radius
            for (int i = 0; i < 6; i++) w.Write(0);          // min / max bounds
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)0);
            for (int i = 0; i < 12 + 12; i++) w.Write(0);    // transform + runtime pointers / unused
            return Read.ReadStruct<RawNode>(ms.ToArray());
        }

        static RawMaterial MaterialRaw(string name, int palette, int texture, bool repeat)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteName(w, name);
            w.Write((byte)0);                                // lighting off: the corner colours are the light
            w.Write((byte)CullingMode.Neither);
            w.Write((byte)31);                               // alpha
            w.Write((byte)0);                                // wireframe
            w.Write((short)palette); w.Write((short)texture);
            w.Write((byte)(repeat ? 1 : 0)); w.Write((byte)(repeat ? 1 : 0));   // repeat s, t (0 clamp, 1 repeat)
            for (int i = 0; i < 9; i++) w.Write((byte)31);   // diffuse, ambient, specular
            w.Write((byte)0);
            w.Write((uint)PolygonMode.Modulate);
            w.Write((byte)RenderMode.Normal); w.Write((byte)0); w.Write((ushort)0);
            w.Write((uint)TexgenMode.None);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u);                                     // matrix id
            w.Write(4096); w.Write(4096);                    // scale s, t
            w.Write((ushort)0); w.Write((ushort)0);          // rotate z
            w.Write(0); w.Write(0);                          // translate s, t
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

        static void WriteName(BinaryWriter w, string name)
        {
            var b = new byte[64];
            Encoding.ASCII.GetBytes(name, 0, Math.Min(name.Length, 63), b, 0);
            w.Write(b);
        }
    }
}
