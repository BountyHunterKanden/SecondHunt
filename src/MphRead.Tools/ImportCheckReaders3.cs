using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.Import.Retro3;

namespace MphRead
{
    // -importcheck readers3 [folder] [--out dir] [--dump MP3/SamusGun.pak[,MP3/other.pak]] [--sweep all|none|<pak,...>] [--sample N]
    // Corruption's readers beyond what the gun exercises. folder: the Trilogy disc's extracted file tree (default
    // brawl_extract/prime/trilogy/DATA/files).
    //   sweep: decompresses every resource of the swept paks (CMPD blocks: raw / zlib / LZO segments; each must come to
    //          its blocks' stated size) and writes hashes.tsv (SHA-1 of every resource's bytes) for the Python Pak3
    //   dump:  for every resource of the dumped paks -- every ANIM (the stored integer tracks + the resampled keys +
    //          samples), every TXTR (unflipped PNG), every CMDL (material set 0: arrays, triangles, skin order, passes and
    //          their GX conversion), every CINF, every CHAR (its ids and animation set) and the SKIN of each CHAR's
    //          model / skin pairs -- plus manifest.txt, for a side-by-side check against the Python reference decoders
    //          (prime3_pak / prime3_anim / prime3_gun / prime3_char / prime_model / prime_tex).
    //   sample: from every pak, the first N textures of each format, models of each kind (every material set of a
    //          multi-set one: the first and the last) and characters (with their skins and skeletons). Writes only under --out.
    internal static partial class ImportCheck
    {
        static void Readers3(string[] args, string repo)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string folder = args.Length >= 3 && !args[2].StartsWith("-") ? args[2] : Path.Combine(repo, "brawl_extract", "prime", "trilogy", "DATA", "files");
            string outDir = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck", "readers-mp3")));
            string[] dump = Arg("--dump", "MP3/SamusGun.pak").Split(',', StringSplitOptions.RemoveEmptyEntries);
            string sweepArg = Arg("--sweep", "all");
            int sample = Int32.Parse(Arg("--sample", "3"));
            string? root = FolderSource.Locate(folder, "MP3");
            if (root == null)
            {
                Console.WriteLine($"  readers3: no MP3 folder under {folder}");
                return;
            }
            Directory.CreateDirectory(outDir);
            using var src = new FolderSource(root);
            using Pak3Set set = Pak3Set.FromSource(src);
            Console.WriteLine($"  readers3: {root} ({(src.GameId.Length > 0 ? src.GameId : "no disc id")}), {set.Paks.Count} paks; output {outDir}");

            // ---- sweep
            var sweep = sweepArg == "none" ? new List<Pak3>()
                : sweepArg == "all" ? set.Paks
                : set.Paks.Where(p => sweepArg.Split(',').Any(x => p.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase))).ToList();
            var sw = Stopwatch.StartNew();
            long total = 0, compressed = 0, bytes = 0, failures = 0, sizeMismatch = 0;
            var blockKinds = new Dictionary<string, long>();
            using (var hashes = new StreamWriter(Path.Combine(outDir, "hashes.tsv")))
            {
                foreach (Pak3 pak in sweep)
                {
                    var pakSw = Stopwatch.StartNew();
                    long pakBytes = 0;
                    foreach (ulong id in pak.Order)
                    {
                        Pak3.Entry e = pak.Resources[id];
                        total++;
                        byte[] data;
                        try
                        {
                            data = pak.Get(id);
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            if (failures <= 10) Console.WriteLine($"    FAIL {pak.Name} {id:x16} {e.Type}: {ex.GetType().Name} {ex.Message}");
                            hashes.WriteLine($"{pak.Name}\t{id:x16}\t{e.Type}\t{(e.Compressed ? 1 : 0)}\t-1\terror");
                            continue;
                        }
                        bytes += data.Length; pakBytes += data.Length;
                        if (e.Compressed)
                        {
                            compressed++;
                            byte[] raw = pak.Raw(id);
                            long stated = Pak3.CmpdSize(raw);
                            if (data.Length != stated)
                            {
                                sizeMismatch++;
                                if (sizeMismatch <= 10) Console.WriteLine($"    size {pak.Name} {id:x16} {e.Type}: {data.Length} bytes, stated {stated}");
                            }
                            int nb = (int)Be.U32(raw, 4), o = 8 + 8 * nb;
                            for (int i = 0; i < nb; i++)
                            {
                                int csz = (int)(Be.U32(raw, 8 + 8 * i) & 0xFFFFFF), dsz = (int)Be.U32(raw, 12 + 8 * i);
                                string k = csz == dsz ? "raw" : o + 1 < raw.Length && raw[o] == 0x78 && raw[o + 1] is 0xDA or 0x9C or 0x01 ? "zlib" : "lzo";
                                blockKinds[k] = blockKinds.GetValueOrDefault(k) + 1;
                                o += csz;
                            }
                        }
                        hashes.WriteLine($"{pak.Name}\t{id:x16}\t{e.Type}\t{(e.Compressed ? 1 : 0)}\t{data.Length}\t{Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant()}");
                    }
                    Console.WriteLine($"    {pak.Name,-22} {pak.Order.Count,6} resources -> {pakBytes / 1048576.0,8:F1} MB in {pakSw.Elapsed.TotalSeconds,6:F1} s");
                }
            }
            sw.Stop();
            Console.WriteLine($"  sweep: {total} resources ({compressed} compressed; blocks " + String.Join(", ", blockKinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))
                + $") -> {bytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1} s; {failures} failed to decompress, {sizeMismatch} not their stated size");

            // ---- dumps
            var manifest = new List<(string Pak, ulong Id, string Type, string File)>();
            var counts = new Dictionary<string, int>();
            void Add(string pakName, ulong id, string type, string file)
            {
                manifest.Add((pakName, id, type, file));
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
            void DumpOne(string pakName, ulong id, string type, byte[] data, int matSet = 0)
            {
                switch (type)
                {
                case "ANIM":
                    {
                        string file = $"anim3_{id:x16}.json";
                        WriteDump(Path.Combine(outDir, file), w => Anim3Dump(w, data));
                        Add(pakName, id, type, file);
                        break;
                    }
                case "TXTR":
                    {
                        byte[]? rgba = Txtr.Decode(data, out _, out int tw, out int th);
                        string file = "undecoded";
                        if (rgba != null)
                        {
                            file = $"txtr_{id:x16}.png";
                            PngOut.Write(Path.Combine(outDir, file), rgba, tw, th);
                        }
                        Add(pakName, id, type, file);
                        break;
                    }
                case "CMDL":
                    {
                        string file = $"cmdl3_{id:x16}_m{matSet}.json";
                        WriteDump(Path.Combine(outDir, file), w => Cmdl3Dump(w, data, matSet));
                        Add(pakName, id, type, file + "|" + matSet);
                        break;
                    }
                case "CINF":
                    {
                        string file = $"cinf3_{id:x16}.json";
                        WriteDump(Path.Combine(outDir, file), w => CinfDump(w, data));
                        Add(pakName, id, type, file);
                        break;
                    }
                case "CHAR":
                    {
                        string file = $"char3_{id:x16}.json";
                        WriteDump(Path.Combine(outDir, file), w => Char3Dump(w, data, set));
                        Add(pakName, id, type, file);
                        // the skins of its model / skin pairs, and its skeleton
                        try
                        {
                            (string _, List<(string Type, ulong Id)> ids) = Char3.CharIds(data, set.TypeOf);
                            List<ulong> cm = ids.Where(x => x.Type == "CMDL").Select(x => x.Id).ToList();
                            List<ulong> cs = ids.Where(x => x.Type == "CSKR").Select(x => x.Id).ToList();
                            for (int i = 0; i < Math.Min(cm.Count, cs.Count); i++)
                            {
                                if (manifest.Any(m => m.Type == "SKIN" && m.Id == cs[i])) continue;
                                string sf = $"skin3_{cs[i]:x16}_{cm[i]:x16}.json";
                                ulong mid = cm[i], sid = cs[i];
                                WriteDump(Path.Combine(outDir, sf), w => Skin3Dump(w, set.Get(mid), set.Get(sid)));
                                Add(pakName, sid, "SKIN", sf + "|" + mid.ToString("x16"));
                            }
                            foreach ((string t, ulong v) in ids)
                            {
                                if (t == "CINF" && !manifest.Any(m => m.Type == "CINF" && m.Id == v))
                                {
                                    Pak3 kp = set.PakOf(v)!;
                                    DumpOne(kp.Name, v, "CINF", kp.Get(v));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    CHAR {id:x16}: ids {ex.Message}");
                        }
                        break;
                    }
                }
            }
            foreach (string dp in dump)
            {
                Pak3? pak = set.Paks.FirstOrDefault(p => p.Name.Equals(dp, StringComparison.OrdinalIgnoreCase));
                if (pak == null)
                {
                    Console.WriteLine($"  dump: no pak {dp}");
                    continue;
                }
                foreach (ulong id in pak.Order)
                {
                    string type = pak.Resources[id].Type;
                    if (type == "CINF" && manifest.Any(m => m.Type == "CINF" && m.Id == id)) continue;
                    if (type is "ANIM" or "TXTR" or "CMDL" or "CINF" or "CHAR") DumpOne(pak.Name, id, type, pak.Get(id));
                }
            }
            // ---- a sample from every pak (each id where the set resolves it): the first N textures of each format,
            // the first N models of each kind (short normals / short UVs, material sets one per section / packed,
            // see-through, glow...; the first and the last set of a multi-set one), the first N characters with their
            // skins and skeletons
            if (sample > 0)
            {
                var sampleSw = Stopwatch.StartNew();
                var perKey = new Dictionary<string, int>();
                bool Take(string key)
                {
                    int n = perKey.GetValueOrDefault(key);
                    if (n >= sample) return false;
                    perKey[key] = n + 1;
                    return true;
                }
                var dumped = new HashSet<ulong>(manifest.Select(m => m.Id));
                int cmdlErrors = 0, cmdls = 0;
                foreach (Pak3 pak in set.Paks)
                {
                    foreach (ulong id in pak.Order)
                    {
                        if (set.PakOf(id) != pak || dumped.Contains(id)) continue;
                        string type = pak.Resources[id].Type;
                        if (type is not ("TXTR" or "CMDL" or "CHAR")) continue;
                        byte[] data = pak.Get(id);
                        if (type == "TXTR")
                        {
                            if (Take("TXTR " + Be.U32(data, 0))) DumpOne(pak.Name, id, type, data);
                        }
                        else if (type == "CHAR")
                        {
                            if (Take("CHAR")) DumpOne(pak.Name, id, type, data);
                        }
                        else
                        {
                            cmdls++;
                            Cmdl3 m;
                            try
                            {
                                m = Cmdl3.Load(data);
                            }
                            catch (Exception)
                            {
                                if (cmdlErrors++ < sample) DumpOne(pak.Name, id, type, data);   // the Python must fail on it too
                                continue;
                            }
                            var feats = new SortedSet<string>(StringComparer.Ordinal);
                            foreach (Cmdl3.Material mm in m.Materials)
                            {
                                foreach (Cmdl3.Pass p in mm.Passes)
                                {
                                    if (p.Type is "TRAN" or "INCA") feats.Add(p.Type.Trim() + (p.Anims.Count > 0 ? "~" + p.Anims[0].Mode : ""));
                                }
                                if (mm.Int("OPAC") != null) feats.Add("OPAC");
                                if (!mm.Passes.Any(p => p.Type == "CLR ")) feats.Add("noCLR");
                            }
                            string key = $"CMDL f{m.Flags & 6:x} " + (m.MaterialSets > 1 ? (m.Packed ? "packed" : "sets") : "1") + " " + String.Join("+", feats);
                            if (!Take(key)) continue;
                            DumpOne(pak.Name, id, type, data);
                            if (m.MaterialSets > 1) DumpOne(pak.Name, id, type, data, m.MaterialSets - 1);
                        }
                        dumped.Add(id);
                    }
                }
                Console.WriteLine($"  sample: {cmdls} CMDLs loaded ({cmdlErrors} failed) in {sampleSw.Elapsed.TotalSeconds:F1} s; {perKey.Count} kinds sampled: "
                    + String.Join(", ", perKey.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"[{k.Key}] {k.Value}")));
            }
            using (var mf = new StreamWriter(Path.Combine(outDir, "manifest.txt")))
            {
                mf.WriteLine(root);
                foreach ((string pak, ulong id, string type, string file) in manifest) mf.WriteLine($"{pak}\t{id:x16}\t{type}\t{file}");
            }
            Console.WriteLine("  dumped: " + String.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}")) + $"; manifest {Path.Combine(outDir, "manifest.txt")}");
        }

        static void Anim3Dump(Utf8JsonWriter w, byte[] data)
        {
            Anim3.Raw raw = Anim3.Parse(data);
            w.WriteStartObject("raw");
            w.WriteNumber("kinds", raw.Kinds); w.WriteNumber("reduced", raw.Reduced); w.WriteNumber("bones", raw.Bones); w.WriteNumber("blob", raw.Blob);
            w.WriteNumber("rot_mul", raw.RotMul); w.WriteNumber("trans_mul", raw.TransMul); w.WriteNumber("scale_mul", raw.ScaleMul); w.WriteNumber("duration", raw.Duration);
            w.WriteStartArray("times"); foreach (double t in raw.Times) w.WriteNumberValue(t); w.WriteEndArray();
            w.WriteStartArray("flags"); foreach (byte f in raw.Flags) w.WriteNumberValue(f); w.WriteEndArray();
            w.WriteStartArray("widths"); foreach (byte f in raw.Widths) w.WriteNumberValue(f); w.WriteEndArray();
            w.WriteStartArray("live"); foreach (byte f in raw.Live) w.WriteNumberValue(f); w.WriteEndArray();
            w.WriteStartArray("heads"); foreach (int h in raw.Heads) w.WriteNumberValue(h); w.WriteEndArray();
            w.WriteStartArray("values");
            for (int k = 0; k < raw.Values.GetLength(0); k++)
            {
                w.WriteStartArray();
                for (int i = 0; i < raw.Values.GetLength(1); i++) w.WriteNumberValue(raw.Values[k, i]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            Anim a = Anim3.FromRaw(raw);
            w.WriteNumber("duration", a.Duration);
            w.WriteNumber("tick", a.Tick);
            w.WriteNumber("keys", a.Keys);
            Keys(w, "rot", a.Rot);
            Keys(w, "trans", a.Trans);
            Keys(w, "scale", a.Scale);
            w.WriteStartArray("samples");
            for (int k = 0; k < 5 && a.Keys > 1; k++)
            {
                double t = a.Duration * (k + 0.37) / 5;
                a.Sample(t, out Dictionary<uint, double[]> rot, out Dictionary<uint, double[]> trans);
                w.WriteStartObject();
                w.WriteNumber("t", t);
                foreach ((string n, Dictionary<uint, double[]> d) in new[] { ("rot", rot), ("trans", trans) })
                {
                    w.WriteStartObject(n);
                    foreach (KeyValuePair<uint, double[]> kv in d.OrderBy(x => x.Key))
                    {
                        w.WriteStartArray(kv.Key.ToString());
                        foreach (double x in kv.Value) w.WriteNumberValue(x);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void StageDump(Utf8JsonWriter w, Cmdl.TevStage s)
        {
            w.WriteStartObject();
            w.WriteStartArray("color_in"); foreach (int x in s.ColorIn) w.WriteNumberValue(x); w.WriteEndArray();
            w.WriteStartArray("alpha_in"); foreach (int x in s.AlphaIn) w.WriteNumberValue(x); w.WriteEndArray();
            foreach ((string n, Cmdl.TevOp op) in new[] { ("color_op", s.ColorOp), ("alpha_op", s.AlphaOp) })
            {
                w.WriteStartObject(n);
                w.WriteNumber("clamp", op.Clamp); w.WriteNumber("op", op.Op); w.WriteNumber("bias", op.Bias); w.WriteNumber("scale", op.Scale); w.WriteNumber("out", op.Out);
                w.WriteEndObject();
            }
            w.WriteNumber("kalpha", s.KAlpha); w.WriteNumber("kcolor", s.KColor); w.WriteNumber("ras", s.Ras);
            w.WriteNumber("tex", s.Tex); w.WriteNumber("texcoord", s.TexCoord);
            w.WriteEndObject();
        }

        static void Cmdl3Dump(Utf8JsonWriter w, byte[] data, int matSet = 0)
        {
            Cmdl3 m = Cmdl3.Load(data, matSet);
            w.WriteNumber("version", m.Version); w.WriteNumber("flags", m.Flags); w.WriteNumber("matsets", m.MaterialSets); w.WriteBoolean("packed", m.Packed);
            void Floats(string n, float[] f) { w.WriteStartArray(n); foreach (float x in f) w.WriteNumberValue((double)x); w.WriteEndArray(); }
            Floats("positions", m.Positions);
            Floats("normals", m.Normals);
            Floats("uv0", m.Uv0);
            w.WriteStartArray("tris");
            foreach (Cmdl.Corner c in m.Triangles)
            {
                w.WriteNumberValue(c.P); w.WriteNumberValue(c.N); w.WriteNumberValue(c.T0); w.WriteNumberValue(c.T1); w.WriteNumberValue(c.T2); w.WriteNumberValue(c.T3);
            }
            w.WriteEndArray();
            w.WriteStartArray("tri_mat"); foreach (int t in m.TriangleMaterial) w.WriteNumberValue(t); w.WriteEndArray();
            w.WriteStartArray("skin_order");
            foreach ((int p, int n, int t0) in m.SkinOrder) { w.WriteNumberValue(p); w.WriteNumberValue(n); w.WriteNumberValue(t0); }
            w.WriteEndArray();
            w.WriteStartArray("materials");
            foreach (Cmdl3.Material mm in m.Materials)
            {
                w.WriteStartObject();
                w.WriteNumber("flags", mm.Flags); w.WriteNumber("vtx", mm.VertexAttributes);
                w.WriteStartArray("passes");
                foreach (Cmdl3.Pass p in mm.Passes)
                {
                    w.WriteStartObject();
                    w.WriteString("type", p.Type); w.WriteNumber("settings", p.Settings); w.WriteString("tex", p.Tex.ToString("x16")); w.WriteNumber("uvsrc", p.UvSource);
                    w.WriteStartArray("anims");
                    foreach (Cmdl.UvAnim u in p.Anims)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("mode", u.Mode);
                        w.WriteStartArray("params"); foreach (double x in u.Params) w.WriteNumberValue(x); w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartObject("colors");
                foreach ((string t, byte[] c) in mm.Colors) { w.WriteStartArray(t); foreach (byte x in c) w.WriteNumberValue(x); w.WriteEndArray(); }
                w.WriteEndObject();
                w.WriteStartObject("ints");
                foreach ((string t, uint v) in mm.Ints) w.WriteNumber(t, v);
                w.WriteEndObject();
                // the GX conversion (prime3_char.to_gx_material)
                GxMaterial3 g = Material3Gx.ToGx(mm);
                w.WriteStartObject("gx");
                w.WriteNumber("flags", g.Flags);
                w.WriteStartArray("tex"); foreach (int x in g.Tex) w.WriteNumberValue(x); w.WriteEndArray();
                w.WriteStartArray("tex_ids"); foreach (ulong x in g.TexIds) w.WriteStringValue(x.ToString("x16")); w.WriteEndArray();
                w.WriteNumber("vtx", g.VertexAttributes); w.WriteNumber("group", g.Group);
                w.WriteStartArray("konst"); foreach (int[] k in g.Konst) { w.WriteStartArray(); foreach (int x in k) w.WriteNumberValue(x); w.WriteEndArray(); } w.WriteEndArray();
                w.WriteNumber("blend_dst", g.BlendDst); w.WriteNumber("blend_src", g.BlendSrc);
                w.WriteStartArray("channels"); foreach (uint x in g.Channels) w.WriteNumberValue(x); w.WriteEndArray();
                w.WriteStartArray("stages"); foreach (Cmdl.TevStage s in g.Stages) StageDump(w, s); w.WriteEndArray();
                w.WriteStartArray("texgens");
                foreach (Cmdl.TexGen tg in g.TexGens)
                {
                    w.WriteStartObject();
                    w.WriteNumber("type", tg.Type); w.WriteNumber("src", tg.Src); w.WriteNumber("mtx", tg.Mtx); w.WriteBoolean("normalize", tg.Normalize); w.WriteNumber("post", tg.Post);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("uv_anims");
                foreach (Cmdl.UvAnim u in g.UvAnims)
                {
                    w.WriteStartObject();
                    w.WriteNumber("mode", u.Mode);
                    w.WriteStartArray("params"); foreach (double x in u.Params) w.WriteNumberValue(x); w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void Skin3Dump(Utf8JsonWriter w, byte[] cmdl, byte[] skin)
        {
            Cmdl3 m = Cmdl3.Load(cmdl);
            List<(uint Bone, float Weight)[]> per = Skin3.Load(skin, m.PositionCount, m.SkinOrder, m.TrianglePositions());
            w.WriteNumber("positions", m.PositionCount);
            w.WriteStartArray("skin");
            foreach ((uint Bone, float Weight)[] ws in per)
            {
                w.WriteStartArray();
                foreach ((uint b, float x) in ws) { w.WriteNumberValue(b); w.WriteNumberValue((double)x); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        static void MetaDump(Utf8JsonWriter w, Char3.Meta m)
        {
            w.WriteStartArray();
            w.WriteStringValue(m.Kind);
            if (m.Kind == "play")
            {
                w.WriteStringValue(m.AnimId.ToString("x16")); w.WriteStringValue(m.PrimName); w.WriteNumberValue(m.PrimId);
            }
            else if (m.Kind is "blend" or "phaseblend")
            {
                MetaDump(w, m.Children[0]); MetaDump(w, m.Children[1]); w.WriteNumberValue((double)m.BlendWeight);
            }
            else if (m.Kind == "random")
            {
                w.WriteStartArray();
                for (int i = 0; i < m.Children.Count; i++) { w.WriteStartArray(); MetaDump(w, m.Children[i]); w.WriteNumberValue(m.Weights[i]); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray();
                foreach (Char3.Meta c in m.Children) MetaDump(w, c);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        static void Char3Dump(Utf8JsonWriter w, byte[] data, Pak3Set set)
        {
            (string name, List<(string Type, ulong Id)> ids) = Char3.CharIds(data, set.TypeOf);
            w.WriteString("name", name);
            w.WriteStartArray("ids");
            foreach ((string t, ulong v) in ids) { w.WriteStartArray(); w.WriteStringValue(t); w.WriteStringValue(v.ToString("x16")); w.WriteEndArray(); }
            w.WriteEndArray();
            List<(string Name, Char3.Meta Anim)> anims;
            try
            {
                anims = Char3.Animations(data, id => set.TypeOf(id) == "ANIM");
            }
            catch (InvalidDataException e)
            {
                w.WriteString("anims_error", e.Message);
                return;
            }
            w.WriteStartArray("anims");
            foreach ((string nm, Char3.Meta m) in anims)
            {
                w.WriteStartArray();
                w.WriteStringValue(nm);
                MetaDump(w, m);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
    }
}
