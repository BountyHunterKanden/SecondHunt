using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRecomp.Import.Effects
{
    // Dark Samus's Corruption Hyper Mode effects (docs/MP3_HYPER_EFFECTS_FORMATS.md), written beside her body in
    // <body>/hyper/ by the body import when its recipe asks for them (ids only; the bytes come from the player's disc):
    //   "hyperfx": { "beam": "06fe1d75af37114a", "ballspikes": "aab065969602e73f", "fire": "04f4f7c2c217a6e0", ... }
    // Each role names a WPSC (a shot), PART, SWHC, ELSC (lightning) or CAUD (a sound). Out, every id folded to 32 bits
    // (Part.Fold) and every file still in Corruption's own layout (read with mp3: true):
    //   <id>.wpsc / .part / .swhc / .elsc / .crsc: the roles and everything they reach (child systems, swooshes, hit
    //   tables); <txtr>.png: their textures (RGBA, the first row the top one); <role>_<n>.wav: a sound's samples (mono
    //   PCM16; several = alternatives); hyper.cfg:
    //     role <role> <type> <id>          sound <role> <n> <volume> <loop 0/1>
    // Core Campaign/RetroParticles.cs + RetroElectric.cs run them; the app draws them (CampaignRenderer.HyperFx.cs).
    // Added for the Dark Samus Hyper Mode thread, 2026-10-12.
    public static class HyperFx
    {
        public const string Folder = "hyper";

        public static (string Role, ulong Id)[]? From(ImportRecipe recipe)
        {
            if (!recipe.Root.TryGetProperty("hyperfx", out JsonElement fx)) return null;
            return fx.EnumerateObject().Select(p => (p.Name, UInt64.Parse(p.Value.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                .ToArray();
        }

        // the recipe's Hyper effects, if it has any (a no-op otherwise); returns the files written
        public static int Export(Pak3Set res, ImportRecipe recipe, string outDir)
        {
            return From(recipe) is { } roles ? Export(res, roles, outDir) : 0;
        }

        public static int Export(Pak3Set res, (string Role, ulong Id)[] roles, string outDir)
        {
            string dir = Path.Combine(outDir, Folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            // 32-bit keys back to the disc's ids (only the ones we reach are checked for clashes)
            var full = new Dictionary<uint, ulong>();
            var clash = new HashSet<uint>();
            foreach (Pak3 pk in res.Paks)
            {
                foreach (ulong id in pk.Order)
                {
                    uint f = Part.Fold(id);
                    if (full.TryGetValue(f, out ulong had) && had != id) clash.Add(f);
                    else full[f] = id;
                }
            }
            ulong Full(uint f) => clash.Contains(f) ? throw new InvalidDataException($"{f:x8}: two Corruption ids fold to it")
                : full.TryGetValue(f, out ulong id) ? id : throw new InvalidDataException($"{f:x8}: not in the paks");

            var written = new HashSet<ulong>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<ulong>();
            var cfg = new StringBuilder("# Dark Samus's Corruption Hyper Mode effects (Import/Effects/HyperFx.cs)\n");
            int files = 0;
            foreach ((string role, ulong id) in roles)
            {
                string type = res.TypeOf(id) ?? throw new InvalidDataException($"hyperfx {role}: {id:x16} is in none of the paks");
                if (Clash(id)) throw new InvalidDataException($"hyperfx {role}: {id:x16} folds onto another id");
                if (type == "CAUD")
                {
                    Caud3 c = Caud3.Load(res.Get(id));
                    for (int i = 0; i < c.Entries.Count; i++)
                    {
                        (short[] pcm, int rate, bool _) = Caud3.Decode(res.Get(c.Entries[i].Csmp));
                        MusyX.WriteWav(Path.Combine(dir, SoundFile(role, i)), pcm, rate);
                        files++;
                        cfg.Append(Inv($"sound {role} {i} {c.Entries[i].Volume:R} {(c.Loops ? 1 : 0)}\n"));
                    }
                    continue;
                }
                cfg.Append(Inv($"role {role} {type} {Part.Fold(id):x8}\n"));
                todo.Enqueue(id);
            }
            bool Clash(ulong id) => clash.Contains(Part.Fold(id));

            while (todo.Count > 0)
            {
                ulong id = todo.Dequeue();
                if (!written.Add(id)) continue;
                string type = res.TypeOf(id) ?? throw new InvalidDataException($"{id:x16} is in none of the paks");
                byte[] b = res.Get(id);
                IEnumerable<(string Type, uint Id)> deps;
                switch (type)
                {
                    case "PART": deps = Part.Load(b, mp3: true).Dependencies(); break;
                    case "SWHC":
                    {
                        uint t = Part.Swoosh.Load(b, mp3: true).Texture;
                        deps = t is 0 or UInt32.MaxValue ? Array.Empty<(string, uint)>() : new[] { ("TXTR", t) };
                        break;
                    }
                    case "ELSC": deps = Elsc.Load(b, mp3: true).Dependencies(); break;
                    case "WPSC": deps = WpscDeps(Wpsc.Load(b, mp3: true)); break;
                    case "CRSC":
                    {
                        // only the default hit effect (its sounds are CAUD ids, asked for as roles)
                        Crsc c = Crsc.Load(b, mp3: true);
                        deps = c.DefaultEffect is 0 or UInt32.MaxValue ? Array.Empty<(string, uint)>() : new[] { ("PART", c.DefaultEffect) };
                        break;
                    }
                    case "USRC": deps = Array.Empty<(string, uint)>(); break;   // a tuning curve (PhazonBallParams)
                    default: throw new InvalidDataException($"{id:x16}: a {type} is not an effect");
                }
                File.WriteAllBytes(Path.Combine(dir, $"{Part.Fold(id):x8}.{type.ToLowerInvariant()}"), b);
                files++;
                foreach ((string t, uint dep) in deps)
                {
                    if (dep == 0 || dep == UInt32.MaxValue) continue;
                    if (t == "TXTR") textures.Add(dep);
                    else if (t is "PART" or "SWHC" or "ELSC" or "CRSC") todo.Enqueue(Full(dep));
                }
            }
            foreach (uint t in textures)
            {
                byte[] tb = res.Get(Full(t));
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                uint fmt = Txtr.Header(tb).Format;
                if (rgba == null) throw new InvalidDataException($"texture {Full(t):x16}: format {fmt} is not decoded");
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
                files++;
            }
            File.WriteAllText(Path.Combine(dir, "hyper.cfg"), cfg.ToString());
            return files + 1;
        }

        // GX RGBA8 (Txtr.cs leaves it to its callers): 4x4 tiles of 64 bytes, first the 16 alpha+red pairs, then the 16
        // green+blue pairs; rows top first
        internal static byte[] Rgba8(byte[] t, out int w, out int h)
        {
            (_, w, h) = Txtr.Header(t);
            int pw = (w + 3) / 4 * 4, ph = (h + 3) / 4 * 4;
            var img = new byte[w * h * 4];
            int o = 12;
            for (int ty = 0; ty < ph; ty += 4)
            {
                for (int tx = 0; tx < pw; tx += 4)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int x = tx + i % 4, y = ty + i / 4;
                        if (x >= w || y >= h || o + 33 + i * 2 >= t.Length) continue;
                        int p = (y * w + x) * 4;
                        img[p + 3] = t[o + i * 2];
                        img[p] = t[o + i * 2 + 1];
                        img[p + 1] = t[o + 32 + i * 2];
                        img[p + 2] = t[o + 33 + i * 2];
                    }
                    o += 64;
                }
            }
            return img;
        }

        static IEnumerable<(string, uint)> WpscDeps(Wpsc w)
        {
            foreach ((string tag, string type) in new[] { ("APSM", "PART"), ("APS2", "PART"), ("ASW1", "SWHC"), ("ASW2", "SWHC"), ("ASW3", "SWHC"), ("COLR", "CRSC") })
            {
                if (w.AssetId(tag) is uint id and not 0) yield return (type, id);
            }
            if (w["B1TX"] is Part.Elem t && t.Id != 0 && t.Id != UInt32.MaxValue) yield return ("TXTR", t.Id);
        }

        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        public static string SoundFile(string role, int n) => Inv($"{role}_{n}.wav");

        // a USRC's first key curve (PhazonBallParams: the Hyper Ball's arc odds by distance, a key per 0.1 unit); after its
        // KEYE/KEYP tag: u32, u32, u8 loop, u8, i32 loop end, i32 loop start, i32 count, then count big-endian floats
        public static float[]? Curve(byte[] usrc)
        {
            for (int i = 0; i + 4 <= usrc.Length; i++)
            {
                if (usrc[i] != 'K' || usrc[i + 1] != 'E' || usrc[i + 2] != 'Y' || (usrc[i + 3] != 'E' && usrc[i + 3] != 'P')) continue;
                int at = i + 4 + 4 + 4 + 2 + 4 + 4;
                if (at + 4 > usrc.Length) return null;
                int n = BinaryPrimitives.ReadInt32BigEndian(usrc.AsSpan(at));
                at += 4;
                if (n <= 0 || at + n * 4L > usrc.Length) return null;
                var k = new float[n];
                for (int j = 0; j < n; j++) k[j] = BinaryPrimitives.ReadSingleBigEndian(usrc.AsSpan(at + 4 * j));
                return k;
            }
            return null;
        }

        // hyper.cfg back (the app and the tests)
        public sealed class Config
        {
            public readonly Dictionary<string, (string Type, uint Id)> Roles = new(StringComparer.Ordinal);
            public readonly Dictionary<string, List<(int N, float Volume, bool Loop)>> Sounds = new(StringComparer.Ordinal);

            public static Config? Load(string bodyDir)
            {
                string path = Path.Combine(bodyDir, Folder, "hyper.cfg");
                if (!File.Exists(path)) return null;
                var c = new Config();
                CultureInfo ci = CultureInfo.InvariantCulture;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length == 0 || w[0].StartsWith('#')) continue;
                    if (w[0] == "role" && w.Length >= 4)
                    {
                        c.Roles[w[1]] = (w[2], UInt32.Parse(w[3], NumberStyles.HexNumber, ci));
                    }
                    else if (w[0] == "sound" && w.Length >= 5)
                    {
                        if (!c.Sounds.TryGetValue(w[1], out var l)) c.Sounds[w[1]] = l = new();
                        l.Add((Int32.Parse(w[2], ci), Single.Parse(w[3], ci), w[4] == "1"));
                    }
                }
                return c;
            }

            public uint Id(string role) => Roles.TryGetValue(role, out var r) ? r.Id : 0;
        }
    }
}
