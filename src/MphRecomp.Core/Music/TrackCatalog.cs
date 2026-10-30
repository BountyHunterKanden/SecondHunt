using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MphRecomp.Import;

namespace MphRecomp.PrimeMusic
{
    // The Prime games' songs as import items (docs/PRIME_MUSIC.md): one recipe a song, recipes/tracks/<Name>.json:
    //   { "game": "mp1", "discId": "GM8E01", "format": "dsp", "title": "Chozo Ruins", "group": "Chozo Ruins",
    //     "role": "area", "files": [ { "name": "Audio/rui_samusL.dsp", "sha1": "..." }, ... ], "gain": 0.9 }
    // ("loop": false for an RSF that doesn't loop.) The import copies the disc's files as they are into
    // <import root>/tracks/<Name>/, so each song imports, updates and deletes on its own. Only names, paths, hashes and
    // a level: never game data (docs/IMPORT_PIPELINE.md section 3).
    public sealed class Track
    {
        public string Name = "", Game = "", Title = "", Group = "", Role = "", Format = "";
        public string[] Files = Array.Empty<string>();   // the disc paths, in the format's order (a DSP pair: left, right)
        public float Gain = 1;
        public bool Loop = true;
        // where it comes from: an imported Prime song ("tracks"), or MPH's own (HuntersMusic, "mph": Seq is a MusicId,
        // or a SeqId when SeqOnly, played by MphRead's sequence player from the ROM)
        public string Source = TrackCatalog.Kind;
        public int Seq = -1;
        public bool SeqOnly;
        public bool Hunters => Source == HuntersMusic.Kind;

        public string Id => Source + ":" + Name;
    }

    public static class TrackCatalog
    {
        public const string Kind = "tracks";

        static readonly Lazy<IReadOnlyList<Track>> s_all = new(() => RecipeStore.Embedded().All.Where(r => r.Kind == Kind).Select(FromRecipe).ToList());

        // every song a recipe names, imported or not, in recipe order (game, then file name)
        public static IReadOnlyList<Track> All => s_all.Value;

        public static Track? Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static Track FromRecipe(ImportRecipe r)
        {
            var t = new Track
            {
                Name = r.Name, Game = r.Game, Format = r.Str("format"), Title = r.OptStr("title") ?? r.Name,
                Group = r.OptStr("group") ?? "", Role = r.OptStr("role") ?? "", Loop = r.Bool("loop", true),
                Files = r.Root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString() ?? "").ToArray(),
            };
            if (r.Root.TryGetProperty("gain", out JsonElement g)) t.Gain = g.GetSingle();
            return t;
        }

        public static string Folder(string importRoot, string name) => Path.Combine(importRoot, Kind, name);

        // imported and current (ImportedContent: a public build only counts a complete import of the current recipe)
        public static bool Usable(string? importRoot, Track t) =>
            importRoot != null && (t.Hunters || ImportedContent.Usable(importRoot, Kind, t.Name)); // (MPH's own: the ROM)

        // the imported copy of a song, ready to play
        public static IMusicStream Open(string importRoot, Track t)
        {
            string dir = Folder(importRoot, t.Name);
            byte[][] files = t.Files.Select(f => File.ReadAllBytes(Path.Combine(dir, Path.GetFileName(f)))).ToArray();
            return MusicFormats.Open(t.Format, files, t.Loop);
        }
    }

    public sealed class TrackImportKind : IImportKind
    {
        public string Kind => TrackCatalog.Kind;

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            foreach (JsonElement f in recipe.Root.GetProperty("files").EnumerateArray())
            {
                yield return (f.GetProperty("name").GetString() ?? "", f.GetProperty("sha1").GetString() ?? "");
            }
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            Track t = TrackCatalog.FromRecipe(recipe);
            if (!MusicFormats.All.Contains(t.Format)) throw new NotSupportedException($"{recipe.Id}: music format '{t.Format}'");
            var data = new byte[t.Files.Length][];
            for (int i = 0; i < t.Files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress((float)i / t.Files.Length, t.Title);
                data[i] = ctx.File(t.Files[i]);
                if (!MusicFormats.Looks(t.Format, data[i])) throw new InvalidDataException($"{t.Files[i]} is not a {t.Format} file");
            }
            // it must play: open it and decode its first block
            IMusicStream m = MusicFormats.Open(t.Format, data, t.Loop);
            m.OpenReader().Read(new short[256 * m.Channels], 256);
            for (int i = 0; i < data.Length; i++)
            {
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(t.Files[i])), data[i]);
            }
        }
    }
}
