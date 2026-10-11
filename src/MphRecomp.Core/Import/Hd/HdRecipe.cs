using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRecomp.Import.Hd
{
    // recipes/hd/<Name>.json -- which pieces of the user's own Prime / Echoes disc make one HD model folder hd/<Name>/
    // (RetroTrophy; prime_to_trophy.py's arguments), and the hashes of the paks it was authored against:
    //   game     "mp1" / "mp2" / "mp3"             discId   the disc it was made from (GM8E01 / G2ME01 / R3ME01)
    //   paks     [{"name", "sha1"}, ...]           searched in this order for every resource (the reference converter
    //                                              took the first .pak in name order holding it)
    //   ancs     the character's ANCS id (hex)     char     its character name in that ANCS ("PowerSuit")
    //   cmdl     instead of ancs + char: a static model by its own CMDL id (Echoes' and Prime 3's suit balls); "char"
    //            then defaults to the reference converter's placeholder "x", which its convert.json and gx.json carry
    //   chars    Prime 3 ("mp3"), instead of ancs: the CHAR ids (64-bit hex) of the parts sharing one skeleton, merged
    //            into one model (Samus = top + bottom + gun); "char" names it; paks are "MP3/<name>" in the disc's tree
    //   height   the model's height in trophy units (default 17.5), unless "scale" fixes the scale
    //   attach   [{"cmdl", "locator", "bone"}]     a separate model the game hangs on a locator of the skeleton (Prime 1's
    //                                              suits end at the right elbow; the cannon is its own CMDL at GUN_LCTR),
    //                                              riding one DS bone whole
    //   static   a model without a skeleton (a morph ball): only the mesh, textures, GX programs and convert.json
    //   matset   the model's material set          scale    a fixed scale (a ball keeps its suit's size)
    //   add      [{"cmdl", "matset"}]              other models joined to a static one where they stand (a shell + core)
    //   core     [r, g, b] (0..255)                a ball's core glow (the game draws it as a particle; the app lights it)
    //   rigidHeadBand  points Retro blends only between the helmet and the neck / collar right under it ride the
    //                  helmet solidly in the rig layout (Fusion's tubes bent at every head turn when blended)
    public sealed class HdRecipe
    {
        public sealed record PakRef(string Name, string Sha1);
        public sealed record Attach(uint Cmdl, string Locator, string Bone);
        public sealed record Add(uint Cmdl, int MatSet);

        public string Name = "", Game = "", DiscId = "", Char = "";
        public PakRef[] Paks = Array.Empty<PakRef>();
        public uint Ancs;
        public ulong? Cmdl;
        public ulong[] Chars = Array.Empty<ulong>();
        public double Height = 17.5;
        public double? Scale;
        public bool Static, RigidHeadBand;
        public int MatSet;
        public Attach[] Attaches = Array.Empty<Attach>();
        public Add[] Adds = Array.Empty<Add>();
        public int[]? Core;

        // the folder's file stem: "Dol" + the item name, like every imported HD model
        public string Stem => "Dol" + Name;

        static uint Hex(string s) => UInt32.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static ulong Hex64(string s) => UInt64.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static HdRecipe From(ImportRecipe r)
        {
            JsonElement root = r.Root;
            string? cmdl = r.OptStr("cmdl");
            bool chars = root.TryGetProperty("chars", out JsonElement ce);
            var h = new HdRecipe
            {
                Name = r.Name, Game = r.Game, DiscId = r.OptStr("discId") ?? "",
                Cmdl = cmdl != null ? Hex64(cmdl) : null, Ancs = cmdl != null || chars ? 0 : r.Hex("ancs"), Char = cmdl != null ? r.OptStr("char") ?? "x" : r.Str("char"),
                Chars = chars ? ce.EnumerateArray().Select(x => Hex64(x.GetString()!)).ToArray() : Array.Empty<ulong>(),
                Static = r.Bool("static"), MatSet = r.Int("matset"), RigidHeadBand = r.Bool("rigidHeadBand"),
            };
            if (h.Game == "mp3" && h.Cmdl == null && h.Chars.Length == 0) throw new InvalidDataException($"{r.Id}: a Prime 3 character names its CHAR ids (\"chars\")");
            if (h.Cmdl != null && !h.Static) throw new InvalidDataException($"{r.Id}: a model by its CMDL id has no skeleton: it must be static");
            if (root.TryGetProperty("height", out JsonElement he)) h.Height = he.GetDouble();
            if (root.TryGetProperty("scale", out JsonElement se)) h.Scale = se.GetDouble();
            var paks = new List<PakRef>();
            foreach (JsonElement p in root.GetProperty("paks").EnumerateArray())
            {
                var pr = new PakRef(p.GetProperty("name").GetString()!, (p.GetProperty("sha1").GetString() ?? "").ToLowerInvariant());
                if (pr.Sha1.Length != 40) throw new InvalidDataException($"{r.Id}: {pr.Name}: sha1 must be a SHA-1");
                paks.Add(pr);
            }
            if (paks.Count == 0) throw new InvalidDataException($"{r.Id}: no paks");
            h.Paks = paks.ToArray();
            if (root.TryGetProperty("attach", out JsonElement ae))
            {
                var l = new List<Attach>();
                foreach (JsonElement a in ae.EnumerateArray())
                {
                    l.Add(new Attach(Hex(a.GetProperty("cmdl").GetString()!), a.GetProperty("locator").GetString()!, a.GetProperty("bone").GetString()!));
                }
                h.Attaches = l.ToArray();
            }
            if (root.TryGetProperty("add", out JsonElement de))
            {
                var l = new List<Add>();
                foreach (JsonElement a in de.EnumerateArray())
                {
                    l.Add(new Add(Hex(a.GetProperty("cmdl").GetString()!), a.TryGetProperty("matset", out JsonElement m) ? m.GetInt32() : 0));
                }
                h.Adds = l.ToArray();
            }
            if (root.TryGetProperty("core", out _)) h.Core = r.Ints("core");
            if (h.Static && h.Attaches.Length > 0) throw new InvalidDataException($"{r.Id}: a static model has no skeleton to attach to");
            return h;
        }
    }
}
