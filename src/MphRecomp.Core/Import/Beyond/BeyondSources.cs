using System;
using System.Collections.Generic;

namespace MphRecomp.Import.Beyond
{
    // The input seam of the Beyond converters (first: Guns/Gun4Builder, the first-person arm): a Beyond model's geometry
    // and skin (IBeyondModelSource) and its converted look (IBeyondLookSource), whoever reads them. Today they come from
    // test-only adapters over the Prime 4 Model Dumper's glTF and mp4_to_trophy.py --static's conversion
    // (MphRead.Tools Gun4TestSources.cs, never shipped); the in-app path implements them on our own Beyond readers
    // (RomFS pak + SMDL/CMDL + MATI + TXTR, and the material conversion). Campaign's file (the gun builder's contract);
    // the rigging session's readers implement the interfaces.

    // One drawable mesh of a Beyond model: one material slot, one level of detail, an indexed triangle list and its skin.
    public sealed class BeyondMesh
    {
        // its place in the model's flattened mesh lists, in the order the model's LOD lists name them (the Model Dumper's
        // glTF "Mesh<k>": LOD0's first mesh list, then its cheaper regrouping, ...). Gun4Builder's mesh selection
        // ("auto", or the recipe's "0-11,21") speaks in these numbers.
        public int Number;
        public int Lod;              // level of detail, 0 = full (-1 = not known: never matched to the look)
        public int MaterialId;       // the model's material slot (the dumper's "MatID<m>")
        public string Name = "";     // for messages ("Mesh0_LOD0_MatID8")
        // x y z per vertex, model space, as stored (float32)
        public float[] Positions = Array.Empty<float>();
        // triangle list, 3 vertex indices per triangle, in the model's order
        public int[] Indices = Array.Empty<int>();
        // skin: InfluencesPerVertex slots per vertex, [vertex * InfluencesPerVertex + k], in the model's slot order (the
        // order matters: ties between equal weights keep the first). Joints index JointNames; a weight <= 0 is an unused
        // slot.
        public int InfluencesPerVertex;
        public int[] Joints = Array.Empty<int>();
        public float[] Weights = Array.Empty<float>();
        // the joint table Joints index: CHPR skeleton node names (ChprSkeleton.Names; matched by name)
        public string[] JointNames = Array.Empty<string>();

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;
    }

    public interface IBeyondModelSource
    {
        string Name { get; }                          // for messages
        IReadOnlyList<BeyondMesh> Meshes { get; }     // every mesh of every LOD, by Number ascending
    }

    // One converted material: what one draw of the model looks like in MPH's GX terms, and the per-corner attributes the
    // conversion made for it.
    public sealed class BeyondLookMaterial
    {
        public string Name = "";     // "FpsMP4DefaultSuit_mat0": the converted material (its gun.gx.json entry's "name")
        public int MaterialId;       // the model material slot it draws (BeyondMesh.MaterialId)
        // per triangle corner, for EVERY triangle of the model's LOD0 meshes with this MaterialId -- meshes by Number
        // ascending, each mesh's triangles in index order, 3 corners each (the converter's triangle soup; the builder
        // matches it one to one with the model's triangles and drops what it does not draw afterwards):
        public double[] Normals = Array.Empty<double>();   // x y z per corner
        public double[] Uv = Array.Empty<double>();        // u v per corner, in the converted textures' space
        // the material's gx.json entry as an ordered JSON tree (PyJson.Obj / List<object?> / string / long / double /
        // bool / null), written to gun.gx.json as is. It must have "stages" (each with "c" and "texmap") and "layers"
        // (each with "name": a texture WriteTexture can make).
        public object? Gx;

        public int CornerCount => Normals.Length / 3;
    }

    public interface IBeyondLookSource
    {
        // in draw order (the conversion's material order): gun.bin's draws and gun.gx.json follow it
        IReadOnlyList<BeyondLookMaterial> Materials { get; }
        // write the texture a gx layer names ("<guid>_diffuse") to path as a PNG (stored the way the converted look
        // stores it); false = the look has no such texture
        bool WriteTexture(string name, string path);
    }
}
