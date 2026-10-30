using System;
using System.Collections.Generic;

namespace MphRecomp.Import.Beyond
{
    // IBeyondModelSource on our own readers: a model (SMDL) and its character file (CHPR), each taken from the first
    // archive of the set that holds it (the model is often in Preload/DuplicateData.pak, the character file in a world
    // pak). Built once, when constructed; the archives are not needed afterwards. The importer uses the top-level
    // selection (spec 4.5.5); EveryRecord names the meshes of the other levels too.
    // Clean-room: written from docs/cleanroom/G_beyond_spec.md alone.
    public sealed class BeyondPakModelSource : IBeyondModelSource
    {
        public string Name { get; }
        public IReadOnlyList<BeyondMesh> Meshes { get; }
        // the readers' own results, for callers that want more than BeyondMesh carries (normals, UVs, colours, materials)
        public BeyondModelFile Model { get; }
        public BeyondSkinTable Skin { get; }
        public IReadOnlyList<BeyondModelMesh> ModelMeshes { get; }

        public BeyondPakModelSource(IReadOnlyList<BeyondArchive> archives, Guid modelId, Guid characterId,
            BeyondModelFile.Selection selection = BeyondModelFile.Selection.TopLevel, string? name = null)
        {
            Name = name ?? $"SMDL {modelId}";
            Model = BeyondModelFile.Read(Holder(archives, modelId, "model"), modelId);
            Skin = BeyondSkinTable.FromChpr(Holder(archives, characterId, "character file").ReadAsset(characterId));
            List<BeyondModelMesh> built = Model.BuildMeshes(selection, Skin);
            var meshes = new List<BeyondMesh>(built.Count);
            foreach (BeyondModelMesh m in built) meshes.Add(m.ToBeyondMesh());
            ModelMeshes = built;
            Meshes = meshes;
        }

        // the first archive of the set that holds the id
        public static BeyondArchive Holder(IReadOnlyList<BeyondArchive> archives, Guid id, string what = "asset")
        {
            foreach (BeyondArchive a in archives)
            {
                if (a.Contains(id)) return a;
            }
            throw new KeyNotFoundException($"{what} {id} is in none of the {archives.Count} archives given");
        }
    }
}
