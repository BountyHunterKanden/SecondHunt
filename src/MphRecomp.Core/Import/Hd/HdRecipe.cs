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
    //   model    with ancs + char: a different CMDL drawn on that character's skeleton, its skin copied from the character's
    //            own model, each position taking the weights of the nearest one there (the beta Phazon Suit: an unskinned
    //            reshape of the final suit's mesh, 2781 of its 3944 positions exactly on the final suit's, the rest within 5 cm)
    //   height   the model's height in trophy units (default 17.5), unless "scale" fixes the scale
    //   attach   [{"cmdl", "locator", "bone"}]     a separate model the game hangs on a locator of the skeleton (Prime 1's
    //                                              suits end at the right elbow; the cannon is its own CMDL at GUN_LCTR),
    //                                              riding one DS bone whole
    //   static   a model without a skeleton (a morph ball): only the mesh, textures, GX programs and convert.json
    //   matset   the model's material set          scale    a fixed scale (a ball keeps its suit's size)
    //   add      [{"cmdl", "matset"}]              other models joined to a static one where they stand (a shell + core)
    //   core     [r, g, b] (0..255)                a ball's core glow (the game draws it as a particle; the app lights it)
        //   veins    {"ancs", "char", "pose", "shell", "red", "blue", "vent", "middle"}  a static ball rebuilt see-through with a
        //            character's own vessels inside (VeinBall: Echoes' final-form Dark Samus)
    //   rigidHeadBand  points Retro blends only between the helmet and the neck / collar right under it ride the
    //                  helmet solidly in the rig layout (Fusion's tubes bent at every head turn when blended)
    //   hold     [{"bone", "cut", "anchor": [Retro bones], "scale"}]  a piece of the MPH hunter's own model held in the
    //            hand: the game part of <bone> beyond <cut> (Weavel's gun without his forearm), seated at the middle of the
    //            anchor bones (the palm) -- written as an "attach" landmark line (TrophyLandmarks) (Zero Suit Samus)
    //   clips    [{"char", "names": [...]}]        a Prime 3 body's clips borrowed from another CHAR in the same paks, put
    //            on the body's skeleton by bone name (BodyClipRetarget): Zero Suit Samus has only cutscene shots of her own,
    //            so she moves with the Federation's people (one disc, nothing mixed in)
    //   skip     [clip names]                      a Prime 3 body's own clips left out (Dark Samus: her two side dashes, the
    //            only clips Trilogy re-tuned and none we play: without them both Corruption discs make the same body)
    //   Metroid Prime 4: Beyond ("mp4", BeyondTrophy; paks are RomFS paths, the dump's own folder tree):
    //   smdl / chpr  the character's model and skeleton (GUIDs); "char" labels it (the reference's --char)
    //   assetsSha1   the SHA-1 of the assets the import reads (BeyondTrophy.Source.AssetsSha1): the revision check, in place
    //                of the paks' own (their "sha1" is left out: DuplicateData.pak is 3.85 GB)
    //   look     {"spec": 1.5, ...}               the PBR -> GX bake's constants (BeyondTrophy.Look), where not the defaults
    //   marks    {"note", "joints": {"L_ankle.fwd": [x, y, z], ...}}  rig marks appended to the joints file (Sylux: its
    //            feet turned to the game's Idle foot direction, foot_fix.py's result)
    //   flourish {"chpr", "chprSha1", "idle", "clips": [...]}  the character's own clips for the idle flourish, baked onto
    //            the DS bones (BeyondFlourish -> <stem>.flourish.bin; played in turn)
    public sealed class HdRecipe
    {
        public sealed record PakRef(string Name, string Sha1);
        public sealed record Attach(uint Cmdl, string Locator, string Bone);
        public sealed record Add(uint Cmdl, int MatSet);
        public sealed record Hold(string Bone, double Cut, string[] Anchor, double Scale);
        public sealed record Borrow(ulong Char, string[] Names);
        public sealed record Mark(string Name, double X, double Y, double Z);

        public string Name = "", Game = "", DiscId = "", Char = "";
        public PakRef[] Paks = Array.Empty<PakRef>();
        public uint Ancs;
        public ulong? Cmdl;
        public uint? Model;
        public ulong[] Chars = Array.Empty<ulong>();
        public double Height = 17.5;
        public double? Scale;
        public bool Static, RigidHeadBand;
        public string[] Skip = Array.Empty<string>();
        public int MatSet;
        public Attach[] Attaches = Array.Empty<Attach>();
        public Add[] Adds = Array.Empty<Add>();
        public Hold[] Holds = Array.Empty<Hold>();
        public Borrow[] Borrows = Array.Empty<Borrow>();
        public int[]? Core;
        public VeinBall.Spec? Veins;
        // "recolor": textures recoloured as they are written (HdRecolor; the beta Phazon Suit's ball)
        public HdRecolor? Recolor;
        // Prime 4 ("mp4")
        public string Smdl = "", Chpr = "", AssetsSha1 = "";
        public readonly Dictionary<string, double> Look = new(StringComparer.Ordinal);
        public readonly List<Mark> Marks = new();
        public string MarksNote = "";
        // "flourish": {"chpr", "chprSha1", "idle", "clips": [...]}: Prime 4 clips baked onto the DS bones for the idle
        // flourish (BeyondFlourish; Beyond Sylux's taunts, owner 2026-10-17), from the gameplay character (its own skeleton)
        public string FlourishChpr = "", FlourishChprSha1 = "", FlourishIdle = "";
        public string[] FlourishClips = Array.Empty<string>();

        // the folder's file stem: "Dol" + the item name, like every imported HD model; StemName instead for a conversion
        // that isn't an HD model folder (a first-person gun's look: its materials are "<character>_mat<i>")
        public string? StemName;
        public string Stem => StemName ?? "Dol" + Name;

        static uint Hex(string s) => UInt32.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static ulong Hex64(string s) => UInt64.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static HdRecipe From(ImportRecipe r)
        {
            JsonElement root = r.Root;
            string? cmdl = r.OptStr("cmdl");
            bool chars = root.TryGetProperty("chars", out JsonElement ce);
            bool mp4 = r.Game == "mp4";
            var h = new HdRecipe
            {
                Name = r.Name, Game = r.Game, DiscId = r.OptStr("discId") ?? "",
                Cmdl = cmdl != null ? Hex64(cmdl) : null, Ancs = cmdl != null || chars || mp4 ? 0 : r.Hex("ancs"), Char = cmdl != null ? r.OptStr("char") ?? "x" : r.Str("char"),
                Chars = chars ? ce.EnumerateArray().Select(x => Hex64(x.GetString()!)).ToArray() : Array.Empty<ulong>(),
                Static = r.Bool("static"), MatSet = r.Int("matset"), RigidHeadBand = r.Bool("rigidHeadBand"),
            };
            if (mp4)
            {
                h.Smdl = r.Str("smdl").ToLowerInvariant();
                h.Chpr = r.Str("chpr").ToLowerInvariant();
                h.AssetsSha1 = r.Str("assetsSha1").ToLowerInvariant();
                if (h.AssetsSha1.Length != 40) throw new InvalidDataException($"{r.Id}: assetsSha1 must be a SHA-1");
                if (root.TryGetProperty("look", out JsonElement lk)) foreach (JsonProperty kv in lk.EnumerateObject()) h.Look[kv.Name] = kv.Value.GetDouble();
                if (root.TryGetProperty("marks", out JsonElement mk))
                {
                    h.MarksNote = mk.GetProperty("note").GetString() ?? "";
                    foreach (JsonProperty kv in mk.GetProperty("joints").EnumerateObject())
                    {
                        double[] v = kv.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                        if (v.Length != 3) throw new InvalidDataException($"{r.Id}: mark {kv.Name} needs x, y, z");
                        h.Marks.Add(new Mark(kv.Name, v[0], v[1], v[2]));
                    }
                }
                if (root.TryGetProperty("flourish", out JsonElement fl))
                {
                    h.FlourishChpr = fl.GetProperty("chpr").GetString()!.ToLowerInvariant();
                    h.FlourishChprSha1 = fl.GetProperty("chprSha1").GetString()!.ToLowerInvariant();
                    h.FlourishIdle = fl.GetProperty("idle").GetString()!;
                    h.FlourishClips = fl.GetProperty("clips").EnumerateArray().Select(x => x.GetString()!).ToArray();
                }
            }
            if (h.Game == "mp3" && h.Cmdl == null && h.Chars.Length == 0) throw new InvalidDataException($"{r.Id}: a Prime 3 character names its CHAR ids (\"chars\")");
            if (h.Cmdl != null && !h.Static) throw new InvalidDataException($"{r.Id}: a model by its CMDL id has no skeleton: it must be static");
            string? model = r.OptStr("model");
            if (model != null)
            {
                if (h.Ancs == 0 || h.Static) throw new InvalidDataException($"{r.Id}: \"model\" goes on a skinned ancs + char character");
                h.Model = Hex(model);
            }
            if (root.TryGetProperty("height", out JsonElement he)) h.Height = he.GetDouble();
            if (root.TryGetProperty("scale", out JsonElement se)) h.Scale = se.GetDouble();
            var paks = new List<PakRef>();
            foreach (JsonElement p in root.GetProperty("paks").EnumerateArray())
            {
                // (a Prime 4 pak is many GB: its assets are checked instead, assetsSha1)
                string sha1 = mp4 && !p.TryGetProperty("sha1", out _) ? "" : (p.GetProperty("sha1").GetString() ?? "").ToLowerInvariant();
                var pr = new PakRef(p.GetProperty("name").GetString()!, sha1);
                if (!mp4 && pr.Sha1.Length != 40) throw new InvalidDataException($"{r.Id}: {pr.Name}: sha1 must be a SHA-1");
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
            if (root.TryGetProperty("recolor", out JsonElement rc)) h.Recolor = HdRecolor.Parse(rc);
            if (root.TryGetProperty("veins", out JsonElement ve))
            {
                if (!h.Static) throw new InvalidDataException($"{r.Id}: \"veins\" rebuilds a static ball");
                int[] Ints(string k) => ve.GetProperty(k).EnumerateArray().Select(x => x.GetInt32()).ToArray();
                h.Veins = new VeinBall.Spec(Hex(ve.GetProperty("ancs").GetString()!), ve.GetProperty("char").GetString()!, ve.GetProperty("pose").GetString()!,
                    ve.GetProperty("shell").GetInt32(), ve.GetProperty("red").GetInt32(), Ints("blue"), ve.GetProperty("vent").GetInt32(), Ints("middle"));
            }
            if (root.TryGetProperty("hold", out JsonElement ho))
            {
                var l = new List<Hold>();
                foreach (JsonElement a in ho.EnumerateArray())
                {
                    string[] anchor = a.GetProperty("anchor").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    if (anchor.Length == 0) throw new InvalidDataException($"{r.Id}: a held part names its anchor bones");
                    l.Add(new Hold(a.GetProperty("bone").GetString()!, a.GetProperty("cut").GetDouble(), anchor,
                        a.TryGetProperty("scale", out JsonElement sc) ? sc.GetDouble() : 1.0));
                }
                h.Holds = l.ToArray();
            }
            if (root.TryGetProperty("clips", out JsonElement cl))
            {
                var l = new List<Borrow>();
                foreach (JsonElement a in cl.EnumerateArray())
                {
                    l.Add(new Borrow(Hex64(a.GetProperty("char").GetString()!),
                        a.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray()));
                }
                h.Borrows = l.ToArray();
            }
            if (root.TryGetProperty("skip", out JsonElement sk)) h.Skip = sk.EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (h.Static && h.Holds.Length > 0) throw new InvalidDataException($"{r.Id}: a static model has no hand to hold a part");
            if (h.Static && h.Attaches.Length > 0) throw new InvalidDataException($"{r.Id}: a static model has no skeleton to attach to");
            return h;
        }
    }
}
