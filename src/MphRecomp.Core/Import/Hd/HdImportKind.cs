using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Hd
{
    // the "hd" item kind: recipes/hd/<Name>.json -> <root>/hd/<Name>/, an HD model folder from the user's own Prime 1 /
    // Echoes / Prime 3 disc (RetroTrophy), or Prime 4's RomFS (BeyondTrophy: Sylux). Characters also need their rig layout (Retro's own skin per rig vertex) and rig caches,
    // made at import by the steps that follow (docs/CLEANROOM_QUEUE.md CR1); static models (balls) are complete here.
    public sealed class HdImportKind : IImportKind
    {
        public string Kind => "hd";

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            HdRecipe h = HdRecipe.From(recipe);
            if (h.Game == "mp4") yield break;   // Prime 4: the assets themselves are checked as they're read (Run)
            foreach (HdRecipe.PakRef p in h.Paks) yield return (p.Name, p.Sha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            HdRecipe h = HdRecipe.From(recipe);
            if (h.Game == "mp4")
            {
                // Prime 4: the RomFS paks read in place (DuplicateData.pak is 3.85 GB), our clean-room readers (Import/Beyond)
                var archives = new List<BeyondArchive>();
                try
                {
                    foreach (HdRecipe.PakRef p in h.Paks)
                    {
                        if (!ctx.Source.Contains(p.Name)) throw new FileNotFoundException($"{p.Name} is not in these files");
                        archives.Add(new BeyondArchive(ctx.Source.OpenRead(p.Name), p.Name));
                    }
                    progress(0.02f, "reading the model, materials and textures");
                    var src = BeyondTrophy.Source.FromGame(archives, Guid.Parse(h.Smdl), Guid.Parse(h.Chpr));
                    if (src.AssetsSha1 != h.AssetsSha1)
                    {
                        throw new InvalidDataException($"{recipe.Name}'s files are a different revision (SHA-1 {src.AssetsSha1}, the recipe was made from {h.AssetsSha1})");
                    }
                    RetroTrophy.Result conv4 = BeyondTrophy.Convert(src, h, BeyondTrophy.Look.From(h.Look), outDir, progress, ct);
                    if (!h.Static) HdRigBuild.Run(Path.Combine(outDir, h.Stem + ".dae"), conv4, h, progress, ct);
                    BeyondFlourish.Write(archives, h, conv4, outDir, progress);
                }
                finally
                {
                    foreach (BeyondArchive a in archives) a.Dispose();
                }
                return;
            }
            if (h.Game is not ("mp1" or "mp2" or "mp3")) throw new NotSupportedException($"{recipe.Id}: HD models from game '{h.Game}' are not importable yet");
            if (h.Game == "mp3")
            {
                // Prime 3's paks (64-bit ids), searched in the recipe's order
                var set = new Pak3Set(h.Paks.Select(p => ctx.Shared("pak3:" + p.Name, () => new Pak3(ctx.File(p.Name), p.Name))));
                RetroTrophy.Result conv3 = RetroTrophy.Convert3(set, h, outDir, progress, ct);
                if (!h.Static) HdRigBuild.Run(Path.Combine(outDir, h.Stem + ".dae"), conv3, h, progress, ct);
                return;
            }
            Pak[] paks = h.Paks.Select(p => ctx.Pak(p.Name)).ToArray();
            byte[] Res(uint id)
            {
                foreach (Pak p in paks)
                {
                    if (p.Contains(id)) return p.Get(id);
                }
                throw new KeyNotFoundException($"{id:x8} is in none of {String.Join(", ", h.Paks.Select(p => p.Name))}");
            }
            RetroTrophy.Result conv = RetroTrophy.Convert(Res, h, outDir, progress, ct);
            if (!h.Static) HdRigBuild.Run(Path.Combine(outDir, h.Stem + ".dae"), conv, h, progress, ct);
        }
    }
}
