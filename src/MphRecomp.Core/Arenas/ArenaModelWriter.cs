using System;
using System.Collections.Generic;
using System.IO;
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

        public readonly record struct Corner(Vector3 Position, Vector3 Color);

        // corners: 3 per triangle, in MPH units. Returns (model file, animation file).
        public static (byte[] Model, byte[] Anim) Write(string name, IReadOnlyList<Corner> corners)
        {
            float maxAbs = 1;
            foreach (Corner c in corners)
            {
                maxAbs = MathF.Max(maxAbs, MathF.Max(MathF.Abs(c.Position.X), MathF.Max(MathF.Abs(c.Position.Y), MathF.Abs(c.Position.Z))));
            }
            // VTX_16 holds 4.12 fixed point (|v| < 8) times the model scale, a power of two
            int scale = 1;
            while (maxAbs / scale >= 7.9f) scale *= 2;

            var renders = new List<IReadOnlyList<RenderInstruction>>();
            var meshes = new List<Mesh>();
            int triCount = corners.Count / 3;
            for (int first = 0; first < triCount; first += TrianglesPerMesh)
            {
                int last = Math.Min(triCount, first + TrianglesPerMesh);
                var list = new List<RenderInstruction> { new(InstructionCode.BEGIN_VTXS, 0) };
                uint lastColor = uint.MaxValue;
                for (int i = first * 3; i < last * 3; i++)
                {
                    Corner c = corners[i];
                    uint color = Channel(c.Color.X) | Channel(c.Color.Y) << 5 | Channel(c.Color.Z) << 10;
                    if (color != lastColor)
                    {
                        list.Add(new RenderInstruction(InstructionCode.COLOR, color));
                        lastColor = color;
                    }
                    uint x = Fx(c.Position.X / scale), y = Fx(c.Position.Y / scale), z = Fx(c.Position.Z / scale);
                    list.Add(new RenderInstruction(InstructionCode.VTX_16, x | y << 16, z));
                }
                list.Add(new RenderInstruction(InstructionCode.END_VTXS));
                while (list.Count % 4 != 0) list.Add(new RenderInstruction(InstructionCode.NOP));   // 4 commands per packed word
                meshes.Add(new Mesh(MeshRaw(material: 0, dlist: renders.Count)));
                renders.Add(list);
            }
            var nodes = new List<Node>
            {
                new(NodeRaw(RoomNode, parent: -1, child: 1, next: -1, meshCount: 0, meshId: 0)),
                new(NodeRaw("geo_arena", parent: 0, child: -1, next: -1, meshCount: meshes.Count, meshId: 0)),
            };
            var materials = new List<Material> { new(MaterialRaw(name)) };
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
                Array.Empty<Repack.TextureInfo>(), Array.Empty<Repack.PaletteInfo>(), nodes, meshes, renders, dlists, options);
            byte[] anim = Repack.PackAnim(new NodeAnimationGroup?[] { null }, new MaterialAnimationGroup?[] { null },
                new TexcoordAnimationGroup?[] { null }, new TextureAnimationGroup?[] { null }, fhPad: false);
            return (model, anim);
        }

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

        static RawMaterial MaterialRaw(string name)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteName(w, name);
            w.Write((byte)0);                                // lighting off: the corner colours are the light
            w.Write((byte)CullingMode.Neither);
            w.Write((byte)31);                               // alpha
            w.Write((byte)0);                                // wireframe
            w.Write((short)-1); w.Write((short)-1);          // palette, texture
            w.Write((byte)0); w.Write((byte)0);              // repeat
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
