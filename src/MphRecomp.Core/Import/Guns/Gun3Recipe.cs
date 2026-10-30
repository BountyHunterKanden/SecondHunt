using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MphRecomp.Import.Guns
{
    // recipes/guns/<Suit>.json for Corruption's first-person gun (game "mp3") -- gun_export3.py's constants and
    // arguments, and the pak hash it was authored against:
    //   game     "mp3"                          discId   the disc it was made from (R3ME01, Metroid Prime Trilogy)
    //   pak      "MP3/SamusGun.pak"             pakSha1  SHA-1 of that .pak file as stored on the disc
    //   char     the gun CHAR (16 hex)          cmdl / cskr / cinf   its model, skin, skeleton (16 hex each)
    //   gunSet   the gun CMDL's material set (Corruption packs its 5 beam looks as sets)
    //   lights   the materials that take MPH's per-weapon light colour
    //   animAliases   [[name, existing name], ...]: extra names GunViewmodel looks for, added when absent
    public sealed class Gun3Recipe
    {
        public string Name = "", Game = "", DiscId = "", Pak = "MP3/SamusGun.pak", PakSha1 = "";
        public ulong Char, Cmdl, Cskr, Cinf;
        public int GunSet;
        public int[] Lights = Array.Empty<int>();
        public readonly List<(string Name, string From)> AnimAliases = new();

        public static bool Applies(ImportRecipe r) => r.Game == "mp3";

        static ulong Hex64(ImportRecipe r, string key) => UInt64.Parse(r.Str(key), NumberStyles.HexNumber);

        public static Gun3Recipe From(ImportRecipe r)
        {
            var g = new Gun3Recipe
            {
                Name = r.Name, Game = r.Game, DiscId = r.OptStr("discId") ?? "", Pak = r.OptStr("pak") ?? "MP3/SamusGun.pak",
                PakSha1 = (r.OptStr("pakSha1") ?? "").ToLowerInvariant(),
                Char = Hex64(r, "char"), Cmdl = Hex64(r, "cmdl"), Cskr = Hex64(r, "cskr"), Cinf = Hex64(r, "cinf"),
                GunSet = r.Int("gunSet"), Lights = r.Ints("lights"),
            };
            if (r.Root.TryGetProperty("animAliases", out JsonElement al))
            {
                foreach (JsonElement pair in al.EnumerateArray())
                {
                    if (pair.GetArrayLength() != 2) throw new InvalidDataException($"{r.Id}: animAliases entries are [name, from]");
                    g.AnimAliases.Add((pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
                }
            }
            if (g.PakSha1.Length != 40) throw new InvalidDataException($"{r.Id}: pakSha1 must be a SHA-1");
            return g;
        }
    }
}
