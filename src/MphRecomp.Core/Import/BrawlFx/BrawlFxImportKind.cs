using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MphRecomp.Import.Brawl;

namespace MphRecomp.Import.BrawlFx
{
    // Brawl effects from the user's own Brawl disc (recipes/brawl/*.json, docs/BRAWL_PARALYZER.md B1):
    //   files     the effect archives the recipe reads, with their SHA-1
    //   archive   the fighter's archive, copied as it is (the runtime reads it with Import/Brawl); its bullet models'
    //             resource files also as models/<n>.brres
    //   common    the common archive's particle textures the effects name, written as PNG (common/<name>.png); the
    //             rest of the 1.4 MB common archive is not kept
    //   effects   the effect names the item needs: the import fails when the archive lacks one
    // or, for a sound item (recipes/brawl/ParalyzerSounds.json):
    //   soundArchive  the disc's sound archive (sound/smashbros_sound.brsar, ~107 MB: read through a stream, only the
    //                 sounds named are decoded; Import/Brawl/BrawlSoundArchive.cs)
    //   sounds        the sound names: each written as sounds/<name>.wav (mono PCM16), plus sounds.txt, a line per sound:
    //                 "<name> <rate> <volume 0-127> <loop start sample, or -1>"
    public sealed class BrawlFxImportKind : IImportKind
    {
        public const string ImportKind = "brawl";

        public string Kind => ImportKind;

        static IEnumerable<(string Name, string Sha1)> Files(ImportRecipe r)
        {
            foreach (JsonElement f in r.Root.GetProperty("files").EnumerateArray())
            {
                yield return (f.GetProperty("name").GetString() ?? "", f.GetProperty("sha1").GetString() ?? "");
            }
        }

        static List<string> Strings(ImportRecipe r, string key)
        {
            var list = new List<string>();
            if (r.Root.TryGetProperty(key, out JsonElement e))
            {
                foreach (JsonElement x in e.EnumerateArray()) list.Add(x.GetString() ?? "");
            }
            return list;
        }

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe) => Files(recipe);

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            if (recipe.OptStr("soundArchive") is string soundArchive)
            {
                RunSounds(ctx, recipe, soundArchive, outDir, progress, ct);
                return;
            }
            string archivePath = recipe.Str("archive"), commonPath = recipe.Str("commonArchive");
            progress(0, Path.GetFileName(archivePath));
            byte[] pac = ctx.File(archivePath);
            BrawlArchive archive = BrawlArchive.Read(pac);
            BrawlArchiveEntry reff = archive.Entries.FirstOrDefault(e => e.Magic == "REFF")
                ?? throw new InvalidDataException($"{archivePath} has no effect file");
            BrawlEffectFile effects = BrawlEffects.Read(archive.DataOf(reff));
            List<string> missing = Strings(recipe, "effects").Where(n => effects.Find(n) == null).ToList();
            if (missing.Count > 0) throw new InvalidDataException($"{archivePath} lacks effects {String.Join(", ", missing)}");
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(archivePath)), pac);
            // the bullet models' resource files again on their own (models/<n>.brres, n = the effect list's model file):
            // the material reader (Assets/Mdl0Gx) reads a file
            string models = Path.Combine(outDir, "models");
            Directory.CreateDirectory(models);
            int model = 0;
            foreach (BrawlArchiveEntry e in archive.Entries.Where(e => e.Magic == "bres"))
            {
                BrawlResFile res = BrawlResFile.Read(archive.DataOf(e));
                if (!res.Folders.Any(f => f.Name == BrawlResFile.Models)) continue;
                File.WriteAllBytes(Path.Combine(models, $"{model++}.brres"), archive.DataOf(e).ToArray());
            }

            ct.ThrowIfCancellationRequested();
            progress(0.3f, Path.GetFileName(commonPath));
            BrawlArchive common = BrawlArchive.Read(ctx.File(commonPath));
            BrawlArchiveEntry reft = common.Entries.FirstOrDefault(e => e.Magic == "REFT")
                ?? throw new InvalidDataException($"{commonPath} has no particle textures");
            Dictionary<string, BrawlTexture> textures = BrawlTextures.ReadParticleTextures(common.DataOf(reft)).Textures
                .ToDictionary(t => t.Name, StringComparer.Ordinal);
            string dir = Path.Combine(outDir, "common");
            Directory.CreateDirectory(dir);
            foreach (string name in Strings(recipe, "commonTextures"))
            {
                ct.ThrowIfCancellationRequested();
                if (!textures.TryGetValue(name, out BrawlTexture? t)) throw new InvalidDataException($"{commonPath} lacks texture {name}");
                // the format rides in the file name's suffix: an intensity-only texture also gives its intensity as alpha
                // (the hardware rule, spec 4.1), which the runtime applies
                PngOut.Write(Path.Combine(dir, $"{name}.{BrawlPixels.FormatName(t.Format)}.png"), t.Rgba, t.Width, t.Height);
            }
            progress(1, "");
        }

        static void RunSounds(ImportContext ctx, ImportRecipe recipe, string archivePath, string outDir, Action<float, string> progress,
            CancellationToken ct)
        {
            List<string> names = Strings(recipe, "sounds");
            string dir = Path.Combine(outDir, "sounds");
            Directory.CreateDirectory(dir);
            var list = new List<string>();
            using Stream s = ctx.Source.OpenRead(archivePath);
            BrawlSoundArchive archive = BrawlSoundArchive.Open(s);
            for (int i = 0; i < names.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress(i / (float)names.Count, names[i]);
                BrawlSoundSample sound = archive.Decode(names[i]) ?? throw new InvalidDataException($"{archivePath} lacks sound {names[i]}");
                MphRecomp.Import.Retro.MusyX.WriteWav(Path.Combine(dir, names[i] + ".wav"), sound.Channels[0], sound.Rate);
                list.Add($"{names[i]} {sound.Rate} {sound.Volume} {(sound.Loops ? sound.LoopStart : -1)}");
            }
            File.WriteAllLines(Path.Combine(outDir, "sounds.txt"), list);
            progress(1, "");
        }
    }
}
