using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MphRead;
using MphRecomp.Arenas;
using MphRecomp.Import.Retro;

// Echoes' multiplayer arenas and their music, made in-app from the player's own Echoes disc (docs/CLEANROOM_QUEUE.md CR2,
// docs/MP2_MULTIPLAYER_IMPORT.md phase 6). The conversion is Core Arenas/EchoesArena (the code the PC's -echoesarena
// import runs); a recipe adds what only a PC run can make: the jump pads' launches, solved there against MphRead's own
// player physics (ArenaCannons.Tune), as tuning values.
namespace MphRecomp.Import.Echoes
{
    // recipes/arenas/<Key>.json (Key = EchoesArena.Def.Key):
    //   { "game": "mp2", "discId": "G2ME01", "pak": "Metroid6.pak", "pakSha1": "...", "scale": 0.5925926,
    //     "audioPak": "AudioGrp.pak", "audioPakSha1": "...",
    //     "crates": [ "Imperialist", ... ],
    //     "pads": [ { "slot": [x,y,z], "pad": [..], "launch": [..], "lockFrames": 0, "altOk": true, "dropped": false,
    //                 "plan": "...", "landed": [..], "miss": 0.01, "altMiss": 2.8, "altLanded": [..] }, ... ] }
    // crates: the weapon crates' weapons (MPH ItemType names, equal chance; absent: EchoesArena.CratePools', empty: no
    // crates). audioPak: the sound lookup + audio groups for Shooting Gallery's turret sounds (EchoesTurret.ImportSounds;
    // absent: no turret sounds). hudPak: the turret HUD's textures (NoARAM.pak; EchoesTurret.ImportHud; absent: none). pads: one per cannon in ArenaCannons.Find's order; slot is the check that it's the same cannon, the rest is
    // what the tuning run left in arena.json (so the import writes the same arena.json)
    public sealed class ArenaRecipe
    {
        public EchoesArena.Def Def = null!;
        public string Pak = "", PakSha1 = "";
        public string AudioPak = "", AudioPakSha1 = "";
        public string HudPak = "", HudPakSha1 = "";
        public float Scale = EchoesArena.DefaultScale;
        public readonly List<ArenaCannons.Cannon> Pads = new();
        public List<ItemType>? Crates;

        public static ArenaRecipe From(ImportRecipe r)
        {
            var a = new ArenaRecipe
            {
                Def = EchoesArena.All.FirstOrDefault(d => d.Key == r.Name) ?? throw new InvalidDataException($"{r.Id}: no Echoes arena '{r.Name}'"),
                Pak = r.Str("pak"),
                PakSha1 = r.Str("pakSha1"),
            };
            if (r.Root.TryGetProperty("scale", out JsonElement s)) a.Scale = s.GetSingle();
            if (r.Root.TryGetProperty("audioPak", out JsonElement ap))
            {
                a.AudioPak = ap.GetString() ?? "";
                a.AudioPakSha1 = r.Str("audioPakSha1");
            }
            if (r.Root.TryGetProperty("hudPak", out JsonElement hp))
            {
                a.HudPak = hp.GetString() ?? "";
                a.HudPakSha1 = r.Str("hudPakSha1");
            }
            if (r.Root.TryGetProperty("crates", out JsonElement crates))
            {
                a.Crates = new List<ItemType>();
                foreach (JsonElement c in crates.EnumerateArray())
                {
                    string name = c.GetString() ?? "";
                    if (!Enum.TryParse(name, out ItemType t) || t == ItemType.None) throw new InvalidDataException($"{r.Id}: crates: no MPH item '{name}'");
                    a.Crates.Add(t);
                }
            }
            if (r.Root.TryGetProperty("pads", out JsonElement pads))
            {
                foreach (JsonElement p in pads.EnumerateArray())
                {
                    a.Pads.Add(new ArenaCannons.Cannon
                    {
                        Slot = Vec(p, "slot"), Pad = Vec(p, "pad"), Launch = Vec(p, "launch"),
                        // R7b (Echoes arenas): the target Tune landed on (older recipes: none, Find's stays)
                        Target = p.TryGetProperty("target", out _) ? Vec(p, "target") : Array.Empty<float>(),
                        LockFrames = p.GetProperty("lockFrames").GetUInt16(),
                        AltOk = p.GetProperty("altOk").GetBoolean(), Dropped = p.GetProperty("dropped").GetBoolean(),
                        Plan = p.GetProperty("plan").GetString() ?? "",
                        Landed = Vec(p, "landed"), Miss = p.GetProperty("miss").GetSingle(),
                        AltMiss = p.GetProperty("altMiss").GetSingle(), AltLanded = Vec(p, "altLanded"),
                    });
                }
            }
            return a;
        }

        static float[] Vec(JsonElement o, string key)
        {
            float[] v = o.GetProperty(key).EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (v.Length != 3) throw new InvalidDataException($"{key}: expected 3 numbers, got {v.Length}");
            return v;
        }
    }

    // the "arenas" item kind: recipes/arenas/<Key>.json -> <root>/arenas/<Key>/, one arena's room files (stand-in model,
    // collision, entities, bot navigation, arena.json) and its HD look, all in that folder; EchoesArena.RegisterImported
    // reads them from there
    public sealed class EchoesArenaImportKind : IImportKind
    {
        public string Kind => EchoesArena.ImportKind;

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe)
        {
            ArenaRecipe a = ArenaRecipe.From(recipe);
            yield return (a.Pak, a.PakSha1);
            if (a.AudioPak != "") yield return (a.AudioPak, a.AudioPakSha1);
            if (a.HudPak != "") yield return (a.HudPak, a.HudPakSha1);
        }

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            ArenaRecipe a = ArenaRecipe.From(recipe);
            if (recipe.Game != "mp2") throw new NotSupportedException($"{recipe.Id}: arenas from game '{recipe.Game}' are not importable");
            EchoesArena.Import(ctx.Pak(a.Pak), a.Def, EchoesArena.Layout.Folder(outDir), a.Scale, a.Pads, progress: progress, ct: ct, crates: a.Crates,
                audio: a.AudioPak != "" ? ctx.Pak(a.AudioPak) : null, hud: a.HudPak != "" ? ctx.Pak(a.HudPak) : null);
        }
    }

    // the "music" item kind: recipes/music/<Name>.json -> <root>/music/<Name>/, a game's music streams copied as they are
    // on the disc (played by the app's own decoder, Import/Retro/RetroStream.cs: clean room D):
    //   { "game": "mp2", "discId": "G2ME01", "format": "rs03", "files": [ { "name": "Audio/x.dsp", "sha1": "..." }, ... ] }
    public sealed class MusicImportKind : IImportKind
    {
        public string Kind => ArenaMusic.ImportKind;

        static IEnumerable<(string Name, string Sha1)> Files(ImportRecipe r)
        {
            foreach (JsonElement f in r.Root.GetProperty("files").EnumerateArray())
            {
                yield return (f.GetProperty("name").GetString() ?? "", f.GetProperty("sha1").GetString() ?? "");
            }
        }

        public IEnumerable<(string Path, string Sha1)> Sources(ImportRecipe recipe) => Files(recipe);

        public void Run(ImportContext ctx, ImportRecipe recipe, string outDir, Action<float, string> progress, CancellationToken ct)
        {
            string format = recipe.Str("format");
            if (format != "rs03") throw new NotSupportedException($"{recipe.Id}: music format '{format}' is not importable");
            List<(string Name, string Sha1)> files = Files(recipe).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string file = Path.GetFileName(files[i].Name);
                progress((float)i / files.Count, file);
                byte[] data = ctx.File(files[i].Name);
                if (!RetroStream.IsRetroStream(data)) throw new InvalidDataException($"{files[i].Name} is not an RS03 stream");
                File.WriteAllBytes(Path.Combine(outDir, file), data);
            }
        }
    }
}
