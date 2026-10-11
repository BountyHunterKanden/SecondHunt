using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Hd
{
    // the "hd" item kind: recipes/hd/<Name>.json -> <root>/hd/<Name>/, an HD model folder from the user's own Prime 1 /
    // Echoes / Prime 3 disc (RetroTrophy). Characters also need their rig layout (Retro's own skin per rig vertex) and rig caches,
    // made at import by the steps that follow (docs/CLEANROOM_QUEUE.md CR1); static models (balls) are complete here.
    public sealed class HdImportKind : IImportKind
    {
        public string Kind => "hd";

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            HdRecipe h = HdRecipe.From(recipe);
            foreach (HdRecipe.PakRef p in h.Paks) yield return (p.Name, p.Sha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            HdRecipe h = HdRecipe.From(recipe);
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
