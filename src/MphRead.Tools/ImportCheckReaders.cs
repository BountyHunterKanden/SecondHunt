using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // -importcheck readers <disc.iso> [--echoes] [--out dir] [--sample N]
    // The Retro readers beyond what the guns exercise: decompresses every resource of every .pak on the disc (zlib /
    // Echoes LZO segments: each must inflate to its stated size), parses every ANCS, and dumps a sample of decodes --
    // every TXTR format (the first N of each, unflipped PNG), every ANIM kind (compressed / uncompressed), CMDLs (with
    // skins) and CINFs of the ANCS characters -- plus a manifest, for a side-by-side check against the Python reference
    // decoders (prime_tex / prime_anim / prime_model / ancs_dump). Writes only under --out.
    internal static partial class ImportCheck
    {
        static void Readers(string[] args, string repo)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, k); return i > 0 && i + 1 < args.Length ? args[i + 1] : d; }
            if (args.Length < 3 || args[2].StartsWith("-"))
            {
                Console.WriteLine("  -importcheck readers <disc.iso> [--echoes] [--out dir] [--sample N]");
                return;
            }
            string iso = args[2];
            bool echoes = args.Contains("--echoes");
            int sample = Int32.Parse(Arg("--sample", "6"));
            string outDir = Path.GetFullPath(Arg("--out", Path.Combine(Path.GetTempPath(), "mph-importcheck", "readers-" + Path.GetFileNameWithoutExtension(iso))));
            Directory.CreateDirectory(outDir);
            using FileStream fs = File.OpenRead(iso);
            using var disc = new GcDisc(fs);
            List<string> paks = disc.Files.Keys.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Console.WriteLine($"  readers: {disc.GameId} '{disc.Title}', {disc.Files.Count} files, {paks.Count} paks; echoes={echoes}; output {outDir}");

            var manifest = new List<(string Pak, uint Id, string Type, string File)>();
            var seen = new HashSet<uint>();
            var perFormat = new Dictionary<uint, int>();
            var perAnimKind = new Dictionary<uint, int>();
            int cmdls = 0, cinfs = 0;
            long total = 0, bytes = 0, compressed = 0, failures = 0, sizeMismatch = 0, ancsOk = 0, ancsFail = 0;
            var sw = Stopwatch.StartNew();
            var ancsFiles = new List<(string Pak, uint Id, Ancs A)>();
            foreach (string pakName in paks)
            {
                Pak pak = new Pak(disc.ReadFile(pakName), pakName);
                foreach (uint id in pak.Order)
                {
                    Pak.Entry e = pak.Resources[id];
                    total++;
                    byte[] data;
                    try
                    {
                        data = pak.Get(id);
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        if (failures <= 10) Console.WriteLine($"    FAIL {pakName} {id:x8} {e.Type}: {ex.GetType().Name} {ex.Message}");
                        continue;
                    }
                    bytes += data.Length;
                    if (e.Compressed)
                    {
                        compressed++;
                        int full = (int)MphRecomp.Import.Be.U32(pak.Data, e.Offset);
                        if (data.Length != full)
                        {
                            sizeMismatch++;
                            if (sizeMismatch <= 10) Console.WriteLine($"    size {pakName} {id:x8} {e.Type}: {data.Length} bytes, stated {full}");
                        }
                    }
                    if (!seen.Add(id)) continue;
                    string file = "";
                    if (e.Type == "TXTR")
                    {
                        uint fmt = MphRecomp.Import.Be.U32(data, 0);
                        perFormat.TryGetValue(fmt, out int n);
                        if (n < sample)
                        {
                            perFormat[fmt] = n + 1;
                            byte[]? rgba = Txtr.Decode(data, out _, out int w, out int h);
                            if (rgba != null)
                            {
                                file = $"txtr_{id:x8}.png";
                                PngOut.Write(Path.Combine(outDir, file), rgba, w, h);
                            }
                            else file = "undecoded";
                        }
                    }
                    else if (e.Type == "ANIM")
                    {
                        uint kind = MphRecomp.Import.Be.U32(data, 0);
                        perAnimKind.TryGetValue(kind, out int n);
                        if (n < sample)
                        {
                            perAnimKind[kind] = n + 1;
                            file = $"anim_{id:x8}.json";
                            WriteDump(Path.Combine(outDir, file), w => AnimDump(w, data, echoes));
                        }
                    }
                    else if (e.Type == "ANCS")
                    {
                        file = $"ancs_{id:x8}.json";
                        Ancs? parsed = null;
                        WriteDump(Path.Combine(outDir, file), w => parsed = AncsDump(w, data));
                        if (parsed != null)
                        {
                            ancsOk++;
                            ancsFiles.Add((pakName, id, parsed));
                        }
                        else ancsFail++;
                    }
                    if (file.Length > 0) manifest.Add((pakName, id, e.Type, file));
                }
                // CMDL + CSKR and CINF of the first characters (resolved within this pak, as the gun exporter does)
                foreach ((string p, uint aid, Ancs a) in ancsFiles.Where(x => x.Pak == pakName))
                {
                    foreach (Ancs.Character c in a.Characters)
                    {
                        if (cmdls < sample * 2 && pak.Contains(c.Cmdl) && pak.Contains(c.Cskr) && seen.Contains(c.Cmdl) && !manifest.Any(m => m.Id == c.Cmdl && m.Type == "CMDL"))
                        {
                            cmdls++;
                            string file = $"cmdl_{c.Cmdl:x8}.json";
                            WriteDump(Path.Combine(outDir, file), w => CmdlDump(w, pak.Get, c.Cmdl, c.Cskr));
                            manifest.Add((pakName, c.Cmdl, "CMDL", file + "|" + c.Cskr.ToString("x8")));
                        }
                        if (cinfs < sample * 2 && pak.Contains(c.Cinf) && !manifest.Any(m => m.Id == c.Cinf && m.Type == "CINF"))
                        {
                            cinfs++;
                            string file = $"cinf_{c.Cinf:x8}.json";
                            WriteDump(Path.Combine(outDir, file), w => CinfDump(w, pak.Get(c.Cinf)));
                            manifest.Add((pakName, c.Cinf, "CINF", file));
                        }
                    }
                }
            }
            sw.Stop();
            using (var mf = new StreamWriter(Path.Combine(outDir, "manifest.txt")))
            {
                mf.WriteLine(iso);
                mf.WriteLine(echoes ? "echoes" : "mp1");
                foreach ((string pak, uint id, string type, string file) in manifest) mf.WriteLine($"{pak}\t{id:x8}\t{type}\t{file}");
            }
            Console.WriteLine($"  resources: {total} ({compressed} compressed) -> {bytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1} s; "
                + $"{failures} failed to decompress, {sizeMismatch} not their stated size");
            Console.WriteLine($"  ANCS: {ancsOk} parsed, {ancsFail} failed; TXTR formats sampled: "
                + String.Join(", ", perFormat.OrderBy(k => k.Key).Select(k => $"{Txtr.FormatNames[Math.Min(k.Key, 10)]} {k.Value}"))
                + "; ANIM kinds sampled: " + String.Join(", ", perAnimKind.OrderBy(k => k.Key).Select(k => $"type {k.Key} {k.Value}"))
                + $"; {cmdls} CMDL, {cinfs} CINF dumped");
            Console.WriteLine($"  manifest: {Path.Combine(outDir, "manifest.txt")} ({manifest.Count} entries)");
        }

        // one JSON object per decode; a decode that throws is recorded as {"error": ...}
        static void WriteDump(string path, Action<Utf8JsonWriter> body)
        {
            var ms = new MemoryStream();
            try
            {
                using var w = new Utf8JsonWriter(ms);
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            catch (Exception e)
            {
                ms = new MemoryStream();
                using var w = new Utf8JsonWriter(ms);
                w.WriteStartObject();
                w.WriteString("error", e.GetType().Name + ": " + e.Message);
                w.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
        }

        static void Keys(Utf8JsonWriter w, string name, Dictionary<uint, List<double[]>> table)
        {
            w.WriteStartObject(name);
            foreach (KeyValuePair<uint, List<double[]>> kv in table.OrderBy(k => k.Key))
            {
                w.WriteStartArray(kv.Key.ToString());
                foreach (double[] v in kv.Value)
                {
                    w.WriteStartArray();
                    foreach (double x in v) w.WriteNumberValue(x);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        static void AnimDump(Utf8JsonWriter w, byte[] data, bool echoes)
        {
            Anim a = Anim.Load(data, echoes);
            w.WriteNumber("duration", a.Duration);
            w.WriteNumber("tick", a.Tick);
            w.WriteNumber("keys", a.Keys);
            Keys(w, "rot", a.Rot);
            Keys(w, "trans", a.Trans);
            Keys(w, "scale", a.Scale);
            // a few samples between keys too
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

        static Ancs? AncsDump(Utf8JsonWriter w, byte[] data)
        {
            Ancs a = Ancs.Parse(data);
            w.WriteStartArray("chars");
            foreach (Ancs.Character c in a.Characters)
            {
                w.WriteStartObject();
                w.WriteNumber("id", c.Id); w.WriteNumber("ver", c.Version); w.WriteString("name", c.Name);
                w.WriteNumber("cmdl", c.Cmdl); w.WriteNumber("cskr", c.Cskr); w.WriteNumber("cinf", c.Cinf);
                w.WriteStartArray("anim_names");
                foreach ((uint idx, string nm) in c.AnimNames) { w.WriteStartArray(); w.WriteNumberValue(idx); w.WriteStringValue(nm); w.WriteEndArray(); }
                w.WriteEndArray();
                w.WriteStartArray("pas");
                foreach ((uint sid, uint np, uint na) in c.PasStates) { w.WriteStartArray(); w.WriteNumberValue(sid); w.WriteNumberValue(np); w.WriteNumberValue(na); w.WriteEndArray(); }
                w.WriteEndArray();
                w.WriteStartObject("particles");
                foreach (KeyValuePair<string, List<uint>> kv in c.Particles) { w.WriteStartArray(kv.Key); foreach (uint x in kv.Value) w.WriteNumberValue(x); w.WriteEndArray(); }
                w.WriteEndObject();
                w.WriteNumber("effects", c.Effects.Count);
                w.WriteNumber("aabbs", c.Aabbs.Count);
                w.WriteStartArray("frozen"); w.WriteNumberValue(c.Frozen.Item1); w.WriteNumberValue(c.Frozen.Item2); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (a.Set == null)
            {
                w.WriteNull("animset");
                return a;
            }
            w.WriteStartObject("animset");
            w.WriteNumber("ver", a.Set.Version);
            w.WriteStartArray("anims");
            foreach ((string nm, Ancs.MetaAnim m) in a.Set.Anims)
            {
                w.WriteStartArray();
                w.WriteStringValue(nm);
                w.WriteStringValue(m.Kind);
                w.WriteStartArray();
                foreach (Ancs.MetaAnim p in Ancs.Prims(m)) { w.WriteStartArray(); w.WriteNumberValue(p.AnimId); w.WriteNumberValue(p.PrimId); w.WriteStringValue(p.PrimName); w.WriteEndArray(); }
                w.WriteEndArray();
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("res");
            foreach ((uint an, uint ev) in a.Set.Resources) { w.WriteStartArray(); w.WriteNumberValue(an); w.WriteNumberValue(ev); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteEndObject();
            return a;
        }

        static void CmdlDump(Utf8JsonWriter w, Func<uint, byte[]> res, uint cmdl, uint cskr)
        {
            Cmdl m = Cmdl.Load(res, cmdl, cskr);
            w.WriteNumber("version", m.Version); w.WriteNumber("flags", m.Flags); w.WriteNumber("matsets", m.MaterialSets);
            void Floats(string n, float[] f) { w.WriteStartArray(n); foreach (float x in f) w.WriteNumberValue((double)x); w.WriteEndArray(); }
            Floats("positions", m.Positions);
            Floats("normals", m.Normals);
            Floats("uv0", m.Uv0);
            w.WriteBoolean("short_uv_used", m.ShortUvs != null);
            w.WriteStartArray("tris");
            foreach (Cmdl.Corner c in m.Triangles)
            {
                w.WriteNumberValue(c.P); w.WriteNumberValue(c.N); w.WriteNumberValue(c.T0); w.WriteNumberValue(c.T1); w.WriteNumberValue(c.T2); w.WriteNumberValue(c.T3);
            }
            w.WriteEndArray();
            w.WriteStartArray("tri_mat"); foreach (int t in m.TriangleMaterial) w.WriteNumberValue(t); w.WriteEndArray();
            w.WriteStartArray("skin");
            foreach ((uint Bone, float Weight)[] ws in m.Skin!)
            {
                w.WriteStartArray();
                foreach ((uint b, float x) in ws) { w.WriteNumberValue(b); w.WriteNumberValue((double)x); }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("materials");
            foreach (Cmdl.Material mm in m.Materials)
            {
                w.WriteStartObject();
                w.WriteNumber("flags", mm.Flags);
                w.WriteStartArray("tex"); foreach (int t in mm.Tex) w.WriteNumberValue(t); w.WriteEndArray();
                w.WriteNumber("vtx", mm.VertexAttributes);
                if (mm.EchoesUnknown != null) { w.WriteStartArray("echoes_unk"); foreach (uint x in mm.EchoesUnknown) w.WriteNumberValue(x); w.WriteEndArray(); }
                w.WriteNumber("group", mm.Group);
                w.WriteStartArray("konst"); foreach (byte[] k in mm.Konst) { w.WriteStartArray(); foreach (byte x in k) w.WriteNumberValue(x); w.WriteEndArray(); } w.WriteEndArray();
                w.WriteNumber("blend_dst", mm.BlendDst); w.WriteNumber("blend_src", mm.BlendSrc);
                if (mm.IndTex is uint it) w.WriteNumber("ind_tex", it);
                w.WriteStartArray("channels"); foreach (uint x in mm.Channels) w.WriteNumberValue(x); w.WriteEndArray();
                w.WriteStartArray("stages");
                foreach (Cmdl.TevStage s in mm.Stages)
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
                    w.WriteStartArray("raw_ops"); w.WriteNumberValue(s.RawColorOp); w.WriteNumberValue(s.RawAlphaOp); w.WriteEndArray();
                    w.WriteNumber("tex", s.Tex); w.WriteNumber("texcoord", s.TexCoord);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("texgens");
                foreach (Cmdl.TexGen g in mm.TexGens)
                {
                    w.WriteStartObject();
                    w.WriteNumber("type", g.Type); w.WriteNumber("src", g.Src); w.WriteNumber("mtx", g.Mtx); w.WriteBoolean("normalize", g.Normalize); w.WriteNumber("post", g.Post);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("uv_anims");
                foreach (Cmdl.UvAnim u in mm.UvAnims)
                {
                    w.WriteStartObject();
                    w.WriteNumber("mode", u.Mode);
                    w.WriteStartArray("params"); foreach (double x in u.Params) w.WriteNumberValue(x); w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("tex_ids"); foreach (uint x in mm.TexIds) w.WriteNumberValue(x); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void CinfDump(Utf8JsonWriter w, byte[] data)
        {
            Cinf c = Cinf.Load(data);
            w.WriteStartArray("bones");
            foreach (uint id in c.SortedIds())
            {
                Cinf.Bone b = c.Bones[id];
                w.WriteStartArray();
                w.WriteNumberValue(id);
                if (b.Name == null) w.WriteNullValue(); else w.WriteStringValue(b.Name);
                w.WriteNumberValue(b.X); w.WriteNumberValue(b.Y); w.WriteNumberValue(b.Z);
                w.WriteNumberValue(b.Parent);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
    }
}
