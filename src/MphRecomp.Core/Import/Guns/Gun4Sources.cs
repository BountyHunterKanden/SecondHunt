using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MphRecomp.Import.Beyond;
using MphRecomp.Import.Hd;

namespace MphRecomp.Import.Guns
{
    // Gun4Builder's inputs from the user's own Prime 4 RomFS (the in-app import): the model and its skin from our Beyond
    // readers (BeyondPakModelSource, level 0: both of its mesh lists), its look from BeyondTrophy's
    // static conversion of the same model (what gun_export4b.py read: mp4_to_trophy.py --static), and the character
    // project decoded by ChprCharacter.
    public static class Gun4Sources
    {
        public sealed class Inputs
        {
            public BeyondPakModelSource Model = null!;
            public Gun4Look Look = null!;
            public ChprCharacter Character = null!;
            public string ChprSha1 = "";        // the CHPR asset's SHA-1 (the recipe's chprSha1)
        }

        // workDir: a scratch folder for the look's conversion (the textures stay there until the builder copies them)
        public static Inputs FromGame(IReadOnlyList<BeyondArchive> archives, Gun4Recipe r, string workDir,
            Action<float, string>? progress = null, System.Threading.CancellationToken ct = default)
        {
            Guid model = Guid.Parse(r.Model), chpr = Guid.Parse(r.Chpr);
            byte[] chprBytes = BeyondPakModelSource.Holder(archives, chpr, "character file").ReadAsset(chpr);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(chprBytes)).ToLowerInvariant();
            if (r.ChprSha1.Length > 0 && sha != r.ChprSha1)
            {
                throw new InvalidDataException($"guns/{r.Name}: character file {r.Chpr} has SHA-1 {sha}, the recipe was made from {r.ChprSha1}");
            }
            // the skin's joint names come from the character file the model is skinned to (the look's, when it has its own)
            Guid skinChpr = r.SkinChpr.Length > 0 ? Guid.Parse(r.SkinChpr) : chpr;
            var src = new BeyondPakModelSource(archives, model, skinChpr, BeyondModelFile.Selection.TopLevel, $"SMDL {model}");
            BeyondTrophy.Source conv = BeyondTrophy.Source.FromGame(archives, model, skinChpr);
            var h = new HdRecipe { Name = r.Name, Game = "mp4", Char = r.Character, StemName = r.Character, Static = true };
            BeyondTrophy.Convert(conv, h, BeyondTrophy.Look.From(r.Look), workDir, progress, ct);
            return new Inputs
            {
                Model = src, Look = Gun4Look.Load(workDir, r.Character), Character = ChprCharacter.Load(chprBytes), ChprSha1 = sha,
            };
        }
    }

    // A static conversion's look (a folder: <character>.dae + <character>.gx.json + PNGs: BeyondTrophy with a static
    // recipe, or the reference mp4_to_trophy.py --static). The .dae's geometries ("<character>_mat<m>") hold the model's
    // LOD0 triangles of material m as a triangle soup (meshes in order) with the conversion's normals and UVs; the gx.json
    // holds the materials. As gun_export4b.py reads them.
    public sealed class Gun4Look : IBeyondLookSource
    {
        readonly List<BeyondLookMaterial> _mats = new();
        string _dir = "";
        public string DaePath = "", GxPath = "";
        public IReadOnlyList<BeyondLookMaterial> Materials => _mats;

        static readonly Regex Geometry = new("<geometry id=\"(g\\d+)\" name=\"([^\"]+)\">(.*?)</geometry>", RegexOptions.Singleline);
        static readonly Regex MatSuffix = new(@"_mat(\d+)$");

        public static Gun4Look Load(string dir, string? character = null)
        {
            string Pick(string ext)
            {
                if (!String.IsNullOrEmpty(character) && File.Exists(Path.Combine(dir, character + ext))) return Path.Combine(dir, character + ext);
                string[] f = Directory.GetFiles(dir, "*" + ext);
                Array.Sort(f, StringComparer.Ordinal);
                return f.Length > 0 ? f[0] : throw new FileNotFoundException($"no *{ext} in {dir}");
            }
            var look = new Gun4Look { _dir = dir, DaePath = Pick(".dae"), GxPath = Pick(".gx.json") };
            var gx = new Dictionary<string, object?>(StringComparer.Ordinal);
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(look.GxPath)))
            {
                foreach (JsonElement m in doc.RootElement.GetProperty("materials").EnumerateArray())
                {
                    gx[m.GetProperty("name").GetString() ?? ""] = Tree(m);
                }
            }
            string d = File.ReadAllText(look.DaePath);
            foreach (Match g in Geometry.Matches(d))
            {
                string gid = g.Groups[1].Value, nm = g.Groups[2].Value, body = g.Groups[3].Value;
                double[] Arr(string s)
                {
                    Match a = Regex.Match(body, $"id=\"{gid}_{s}\"[^>]*>([^<]*)<");
                    if (!a.Success) throw new InvalidDataException($"{nm}: no {gid}_{s}");
                    return a.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => Double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                }
                Match mm = MatSuffix.Match(nm);
                if (!mm.Success) throw new InvalidDataException($"geometry {nm}: no _mat<n> suffix");
                var mat = new BeyondLookMaterial
                {
                    Name = nm, MaterialId = Int32.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture), Normals = Arr("na"), Uv = Arr("ta"),
                    Gx = gx.TryGetValue(nm, out object? t) ? t : null,
                };
                int positions = Arr("pa").Length;
                if (positions != mat.Normals.Length) throw new InvalidDataException($"{nm}: {positions / 3} positions, {mat.CornerCount} normals");
                look._mats.Add(mat);
            }
            return look;
        }

        public bool WriteTexture(string name, string path)
        {
            string src = Path.Combine(_dir, name + ".png");
            if (!File.Exists(src)) return false;
            File.Copy(src, path, true);
            return true;
        }

        // a JSON value as Python's json.load reads it, for PyJson.Dump: objects keep their key order, numbers with a
        // fraction or exponent are floats, the others ints
        public static object? Tree(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => ToObj(e),
            JsonValueKind.Array => e.EnumerateArray().Select(Tree).ToList(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) >= 0
                ? Double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
                : (object)Int64.Parse(e.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

        static PyJson.Obj ToObj(JsonElement e)
        {
            var o = new PyJson.Obj();
            foreach (JsonProperty p in e.EnumerateObject()) o.Add(p.Name, Tree(p.Value));
            return o;
        }
    }
}
