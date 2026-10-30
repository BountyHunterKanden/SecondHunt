using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using MphRead.Entities;
using MphRecomp.Arenas;
using MphRecomp.Import;
using MphRecomp.Import.Disc;

namespace MphRead
{
    // -importcheck arenas [Key|all] [--out dir] [--mp2 disc.iso] [--recipes dir|embedded] [-v] [--noplay]
    // The golden check (docs/IMPORT_PIPELINE.md rule 4) for Echoes' multiplayer arenas and their music (docs/
    // CLEANROOM_QUEUE.md CR2): runs the in-app "arenas" and "music" importers (Core Import/Echoes, the code the device
    // runs) on the owner's Echoes disc into a scratch folder, then compares every file byte for byte with today's PC
    // import in the desktop extraction (Tools -echoesarena import: _archives/echoes_m0*/, its hd/, levels/entities|
    // nodeData/echoes_m0*, _archives/echoes_music/; preview.png there is PC-only). Then plays each arena the device's way
    // from the import folder (EchoesArena.ImportRoot -> RoomMetadata's hostFolder): Battle, 300 frames.
    //
    // -importcheck arenas --write-recipes [--mp2 disc.iso]
    // (Re)writes recipes/arenas/<Key>.json from the desktop extraction's arena.json (scale + the jump pads a tuning run
    // solved: ArenaCannons.Tune) and recipes/music/EchoesArenaMusic.json (ArenaMusic.Files), with the disc's SHA-1s. Run
    // it after -echoesarena import changes the pads, then the check.
    internal static partial class ImportCheck
    {
        const string ArenaDisc = "G2ME01";

        static void Arenas(string[] args, string repo)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;   // the reference files, and MphRead's own data for the matches
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string disc = Arg("--mp2", Path.Combine(repo, "brawl_extract", "prime", "echoes_retail.rvz"));
            string refRoot = Paths.FileSystem;
            if (args.Contains("--write-recipes"))
            {
                WriteArenaRecipes(repo, disc, refRoot);
                return;
            }
            string which = args.Length >= 3 && !args[2].StartsWith("-") ? args[2] : "all";
            bool verbose = args.Contains("-v");
            string outRoot = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck", DateTime.Now.ToString("yyyyMMdd-HHmmss"))));
            if (outRoot.StartsWith(Path.GetFullPath(refRoot), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  -importcheck: --out {outRoot} is inside the reference outputs; pick another folder");
                return;
            }
            string recipeArg = Arg("--recipes", Path.Combine(repo, "recipes"));
            RecipeStore store = recipeArg == "embedded" || !Directory.Exists(recipeArg) ? RecipeStore.Embedded() : RecipeStore.FromDirectory(recipeArg);
            List<ImportRecipe> recipes = store.All.Where(r => (r.Kind == EchoesArena.ImportKind || r.Kind == ArenaMusic.ImportKind)
                && (which == "all" || r.Name.Equals(which, StringComparison.OrdinalIgnoreCase) || EchoesArena.Find(which)?.Key == r.Name)).ToList();
            Console.WriteLine($"  importcheck arenas {which}: {recipes.Count} recipes from {store.Origin}; output {outRoot}; reference {refRoot}");
            if (recipes.Count == 0) return;
            if (!File.Exists(disc))
            {
                Console.WriteLine($"  [SKIP] no Echoes disc at '{disc}'");
                return;
            }
            var svc = new ImportService(outRoot, store);
            var progress = new HdProgress { Verbose = verbose };
            var sw = Stopwatch.StartNew();
            ImportReport rep;
            try
            {
                rep = svc.Import("mp2", disc, progress, default, recipes.Select(r => r.Id).ToList());
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
            {
                Console.WriteLine($"  [REFUSED] mp2 from {disc}: {e.Message}");
                return;
            }
            Console.WriteLine($"  mp2 ({rep.DiscId}, {Path.GetFileName(disc)}): {rep.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{rep.Items.Count} items imported in {sw.Elapsed.TotalSeconds:F2} s");
            foreach (ImportItemInfo it in rep.Items) Console.WriteLine($"    {it.Id,-32} {it.Status,-12} {it.Seconds,6:F2} s  {it.Message}");
            Console.WriteLine();

            int pass = 0, pcOnly = 0;
            var failed = new List<string>();
            foreach (ImportRecipe r in recipes)
            {
                bool ok;
                if (r.Kind == ArenaMusic.ImportKind)
                {
                    ok = CompareFolder(r.Id, ArenaMusic.ImportedFolder(outRoot), name => Path.Combine(ArenaMusic.Folder(refRoot), name),
                        ArenaMusic.Files.ToList(), ref pcOnly);
                }
                else
                {
                    EchoesArena.Def d = EchoesArena.All.First(x => x.Key == r.Name);
                    string arch = Path.Combine(refRoot, "_archives", d.Archive), hd = Path.Combine(arch, ArenaHdWriter.Dir);
                    string RefOf(string name) => name.EndsWith("_Ent.bin") ? Path.Combine(refRoot, "levels", "entities", name)
                        : name.EndsWith("_Node.bin") ? Path.Combine(refRoot, "levels", "nodeData", name)
                        : name.StartsWith(d.Archive + "_") || name == "arena.json" ? Path.Combine(arch, name)
                        : Path.Combine(hd, name);
                    var expected = new List<string> { $"{d.Archive}_Model.bin", $"{d.Archive}_Anim.bin", $"{d.Archive}_Collision.bin",
                        $"{d.Archive}_Ent.bin", $"{d.Archive}_Node.bin", "arena.json" };
                    if (Directory.Exists(hd)) expected.AddRange(Directory.GetFiles(hd).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal));
                    ok = CompareFolder(r.Id, EchoesArena.ItemDir(outRoot, d), RefOf, expected, ref pcOnly);
                    if (File.Exists(Path.Combine(arch, "preview.png")))
                    {
                        Console.WriteLine($"    PC-ONLY {d.Archive}/preview.png (-echoesarena preview)");
                        pcOnly++;
                    }
                }
                if (verbose)
                {
                    foreach (string l in progress.Log(r.Id)) Console.WriteLine($"      log: {l}");
                }
                if (ok) pass++;
                else failed.Add(r.Name);
                Console.WriteLine($"  => {(ok ? "PASS" : "FAIL")} {r.Id}");
            }
            if (!args.Contains("--noplay")) PlayImported(outRoot, recipes, failed);
            Console.WriteLine($"  SUMMARY: {pass}/{recipes.Count} arena + music items PASS ({pcOnly} PC-only reference files not imported)"
                + (failed.Count > 0 ? "; FAIL: " + String.Join(", ", failed.Distinct()) : ""));
        }

        // every expected file byte-identical to its reference; files the import made beyond them are listed as EXTRA
        static bool CompareFolder(string id, string dir, Func<string, string> refOf, List<string> expected, ref int pcOnly)
        {
            bool ok = true;
            int same = 0;
            long bytes = 0;
            foreach (string name in expected)
            {
                string a = refOf(name), b = Path.Combine(dir, name);
                if (!File.Exists(a))
                {
                    Console.WriteLine($"    NO-REF  {name} (no {a})");
                    ok = false;
                    continue;
                }
                if (!File.Exists(b))
                {
                    Console.WriteLine($"    MISSING {name}");
                    ok = false;
                    continue;
                }
                byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
                if (x.AsSpan().SequenceEqual(y))
                {
                    same++;
                    bytes += x.Length;
                    continue;
                }
                int at = 0;
                while (at < Math.Min(x.Length, y.Length) && x[at] == y[at]) at++;
                Console.WriteLine($"    DIFF    {name}: {x.Length} vs {y.Length} bytes, first difference at 0x{at:x}");
                ok = false;
            }
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Where(n => !expected.Contains(n)))
                {
                    Console.WriteLine($"    EXTRA   {f}");
                }
            }
            Console.WriteLine($"    {id}: {same}/{expected.Count} files byte-identical ({bytes / 1024} KB)");
            return ok;
        }

        // the device's way: arenas registered from the import folder (RoomMetadata paths inside it), a Battle started in
        // each, 300 frames
        static void PlayImported(string outRoot, List<ImportRecipe> recipes, List<string> failed)
        {
            EchoesArena.ImportRoot = outRoot;
            EchoesArena.EnsureRegistered();
            Console.WriteLine();
            Console.WriteLine($"  play from {outRoot} (EchoesArena.ImportRoot):");
            foreach (ImportRecipe r in recipes.Where(r => r.Kind == EchoesArena.ImportKind))
            {
                EchoesArena.Def d = EchoesArena.All.First(x => x.Key == r.Name);
                string result;
                MphRecomp.Campaign.CampaignHost? host = null;
                try
                {
                    (RoomMetadata? meta, _) = Metadata.GetRoomByName(d.Room);
                    if (meta == null) throw new Exception("not registered");
                    string dir = EchoesArena.ItemDir(outRoot, d);
                    foreach (string? p in new[] { meta.ModelPath, meta.AnimationPath, meta.CollisionPath, meta.EntityPath, meta.NodePath, EchoesArena.HdDir(d.Room) })
                    {
                        if (p == null || !Path.GetFullPath(p).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new Exception($"reads {p}, not the import folder");
                    }
                    var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, d.Room, Hunter.Samus, 3, 1);
                    host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
                    var input = new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None };
                    for (int f = 0; f < 300 && !host.Ended; f++) host.Step(input);
                    int pads = 0;
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e is JumpPadEntity) pads++;
                    }
                    result = $"ok    id {host.RoomId}, {host.Frame} f, {PlayerEntity.Players.Count} players, {pads} jump pads";
                }
                catch (Exception ex)
                {
                    failed.Add(r.Name);
                    result = $"FAIL  {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    host?.Dispose();
                }
                Console.WriteLine($"    {d.InGameName,-20} Battle  {result}");
            }
        }

        // ---- recipes from today's PC import ----

        static void WriteArenaRecipes(string repo, string discPath, string refRoot)
        {
            using IFileSource gc = DiscImage.Open(discPath);
            if (gc.GameId != ArenaDisc) Console.WriteLine($"  note: {gc.GameId} is not {ArenaDisc}, the disc these arenas were made from");
            string Sha1(string path) => Convert.ToHexString(SHA1.HashData(gc.ReadFile(path))).ToLowerInvariant();
            var opts = new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            void Save(string path, Action<Utf8JsonWriter> body)
            {
                var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, opts))
                {
                    w.WriteStartObject();
                    w.WriteString("game", "mp2");
                    w.WriteString("discId", ArenaDisc);
                    body(w);
                    w.WriteEndObject();
                }
                ms.WriteByte((byte)'\n');
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, ms.ToArray());
                Console.WriteLine($"  wrote {Path.GetRelativePath(repo, path)}");
            }
            static void Vec(Utf8JsonWriter w, string name, float[] v)
            {
                w.WriteStartArray(name);
                foreach (float f in v) w.WriteNumberValue(f);
                w.WriteEndArray();
            }

            string pakSha1 = Sha1("Metroid6.pak");
            string? audioSha1 = null, hudSha1 = null;   // AudioGrp.pak: only for an arena with the turret (its sounds)
            foreach (EchoesArena.Def d in EchoesArena.All)
            {
                string json = Path.Combine(refRoot, "_archives", d.Archive, "arena.json");
                if (!File.Exists(json))
                {
                    Console.WriteLine($"  [SKIP] {d.Key}: no {json} (run -echoesarena import first)");
                    continue;
                }
                EchoesArena.Info info = JsonSerializer.Deserialize<EchoesArena.Info>(File.ReadAllText(json))!;
                Save(Path.Combine(repo, "recipes", EchoesArena.ImportKind, d.Key + ".json"), w =>
                {
                    w.WriteString("pak", "Metroid6.pak");
                    w.WriteString("pakSha1", pakSha1);
                    w.WriteNumber("scale", info.Scale);
                    if (info.Turret != null)
                    {
                        w.WriteString("audioPak", "AudioGrp.pak");
                        w.WriteString("audioPakSha1", audioSha1 ??= Sha1("AudioGrp.pak"));
                        w.WriteString("hudPak", "NoARAM.pak");
                        w.WriteString("hudPakSha1", hudSha1 ??= Sha1("NoARAM.pak"));
                    }
                    if (info.CrateWeapons.Count > 0)
                    {
                        w.WriteStartArray("crates");
                        foreach (string c in info.CrateWeapons) w.WriteStringValue(c);
                        w.WriteEndArray();
                    }
                    w.WriteStartArray("pads");
                    foreach (ArenaCannons.Cannon c in info.Cannons)
                    {
                        w.WriteStartObject();
                        Vec(w, "slot", c.Slot);
                        Vec(w, "pad", c.Pad);
                        Vec(w, "target", c.Target);   // R7b (Echoes arenas): Tune may fall back from Find's target
                        Vec(w, "launch", c.Launch);
                        w.WriteNumber("lockFrames", c.LockFrames);
                        w.WriteBoolean("altOk", c.AltOk);
                        w.WriteBoolean("dropped", c.Dropped);
                        w.WriteString("plan", c.Plan);
                        Vec(w, "landed", c.Landed);
                        w.WriteNumber("miss", c.Miss);
                        w.WriteNumber("altMiss", c.AltMiss);
                        Vec(w, "altLanded", c.AltLanded);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
            }
            Save(Path.Combine(repo, "recipes", ArenaMusic.ImportKind, ArenaMusic.ImportName + ".json"), w =>
            {
                w.WriteString("format", "rs03");
                w.WriteStartArray("files");
                foreach (string file in ArenaMusic.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("name", "Audio/" + file);
                    w.WriteString("sha1", Sha1("Audio/" + file));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
