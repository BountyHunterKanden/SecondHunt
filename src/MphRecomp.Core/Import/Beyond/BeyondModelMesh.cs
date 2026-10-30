using System;

namespace MphRecomp.Import.Beyond
{
    // One draw mesh built from a Beyond SMDL draw record (spec 4.4-4.5): merged vertices, per-vertex attributes as the
    // reference export writes them, and a triangle list.
    public sealed class BeyondModelMesh
    {
        public const int SkinSlots = 4;

        public int Number;               // position in its selection (4.5.5)
        public string Name = "";         // "Mesh<k>_LOD<levels>_MatID<slot>"
        public int Record;               // the draw record it was built from
        public int[] Levels = Array.Empty<int>();   // level numbers listing the record, ascending
        public int MaterialSlot;

        public float[] Positions = Array.Empty<float>();   // x y z per vertex
        public float[] Normals = Array.Empty<float>();     // x y z per vertex, normalised (0 0 0 when absent or zero)
        public float[]? Uv0, Uv1, Uv2;                     // u v per vertex, or null when the layout has none
        public float[] Colors = Array.Empty<float>();      // r g b a per vertex (1 1 1 1 when absent)
        // skin: SkinSlots per vertex, the kept slots first in slot order, then unused ones (index 0, weight 0)
        public int[] Joints = Array.Empty<int>();          // skinned-bone table indices
        public float[] Weights = Array.Empty<float>();
        public string[] JointNames = Array.Empty<string>();   // the skinned-bone table
        public int[] Indices = Array.Empty<int>();         // triangle list

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;

        // the converters' mesh: Lod = the first level (or -1), 4 influences per vertex, joints naming the skin table
        public BeyondMesh ToBeyondMesh() => new()
        {
            Number = Number,
            Lod = Levels.Length > 0 ? Levels[0] : -1,
            MaterialId = MaterialSlot,
            Name = Name,
            Positions = Positions,
            Indices = Indices,
            InfluencesPerVertex = SkinSlots,
            Joints = Joints,
            Weights = Weights,
            JointNames = JointNames,
        };
    }
}
