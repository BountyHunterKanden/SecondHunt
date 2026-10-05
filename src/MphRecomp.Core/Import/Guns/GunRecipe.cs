using System;
using System.IO;

namespace MphRecomp.Import.Guns
{
    // recipes/guns/<Suit>.json -- which pieces of the user's own disc make one first-person gun (gun_export.py's
    // arguments), and the SamusGun.pak hash it was authored against:
    //   game     "mp1" / "mp2"             discId   the disc it was made from (GM8E01 / G2ME01)
    //   pak      "SamusGun.pak"            pakSha1  SHA-1 of that .pak file as stored on the disc
    //   ancs     the gun's ANCS id (hex)   char     its character ("Power")
    //   echoes   Echoes formats            gunSet   the gun's CMDL material set
    //   arm      the suit's forearm CMDL (hex; optional)       armSet   its material set (Echoes: one per suit)
    //   lights   the materials that take MPH's per-weapon light colour
    public sealed class GunRecipe
    {
        public string Name = "", Game = "", DiscId = "", Pak = "SamusGun.pak", PakSha1 = "";
        public uint Ancs;
        public string Char = "Power";
        public bool Echoes;
        public int GunSet, ArmSet;
        public string? Arm;         // as written (8 hex digits): it is also part of gun.gx.json's "source"
        public int[] Lights = Array.Empty<int>();

        public uint ArmId => UInt32.Parse(Arm!, System.Globalization.NumberStyles.HexNumber);

        public static GunRecipe From(ImportRecipe r)
        {
            var g = new GunRecipe
            {
                Name = r.Name, Game = r.Game, DiscId = r.OptStr("discId") ?? "", Pak = r.OptStr("pak") ?? "SamusGun.pak",
                PakSha1 = (r.OptStr("pakSha1") ?? "").ToLowerInvariant(), Ancs = r.Hex("ancs"), Char = r.OptStr("char") ?? "Power",
                Echoes = r.Bool("echoes"), GunSet = r.Int("gunSet"), Arm = r.OptStr("arm"), ArmSet = r.Int("armSet"), Lights = r.Ints("lights"),
            };
            if (g.Arm != null && g.Arm.Length == 0) g.Arm = null;
            if (g.PakSha1.Length != 40) throw new InvalidDataException($"{r.Id}: pakSha1 must be a SHA-1");
            return g;
        }
    }
}
