using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.PrimeMusic;

namespace MphRead
{
    // The Prime games' streamed music (docs/PRIME_MUSIC.md), from the user's own discs:
    //   -primemusic scan <disc> [folder]          every track in <folder>Audio/ (folder "MP1/" etc. on Trilogy): format,
    //                                             channels, length, loop, level, clipping, and how smooth its loop seam is
    //   -primemusic wav <disc> <file> <out.wav> [seconds] [start]
    //                                             renders one track (a DSP pair by its left file) through the loop
    internal static class PrimeMusicTool
    {
        public static void Run(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "";
            if (mode == "scan" && args.Length >= 3) Scan(args[2], args.Length > 3 ? args[3] : "");
            else if (mode == "wav" && args.Length >= 5)
            {
                Wav(args[2], args[3], args[4], args.Length > 5 ? Single.Parse(args[5]) : 30, args.Length > 6 ? Single.Parse(args[6]) : 0);
            }
            else if (mode == "recipes" && args.Length >= 8) Recipes(args[2], args[3], args[4], args[5], args[6], args[7]);
            else if (mode == "importtest" && args.Length >= 6) ImportTest(args[2], args[3], args[4], args[5]);
            else if (mode == "hash" && args.Length >= 4) Hash(args[2], args[3]);
            else Console.WriteLine("usage: -primemusic scan <disc> [folder] | wav <disc> <file> <out.wav> [seconds] [start] | hash <disc> <part> | recipes <disc> <folder> <pick.tsv> <recipes dir> <game> <discId>");
        }

        // one track on a disc: its format and files (a DSP pair = left, right)
        public sealed record DiscTrack(string Format, string[] Files)
        {
            public string Name => Path.GetFileName(Files[0]);
        }

        public static List<DiscTrack> Tracks(IFileSource disc, string folder)
        {
            string audio = folder + "Audio/";
            var paths = disc.Paths.Where(p => p.StartsWith(audio, StringComparison.OrdinalIgnoreCase)).ToList();
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var tracks = new List<DiscTrack>();
            foreach (string p in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".rsf") tracks.Add(new("rsf", new[] { p }));
                else if (ext == ".fsb") tracks.Add(new("fsb3", new[] { p }));
                else if (ext == ".dsp")
                {
                    byte[] head = new byte[0x60];
                    using (Stream s = disc.OpenRead(p)) s.ReadExactly(head, 0, (int)Math.Min(head.Length, disc.Length(p)));
                    if (MphRecomp.Import.Retro.RetroStream.IsRetroStream(head))
                    {
                        tracks.Add(new("rs03", new[] { p }));
                        continue;
                    }
                    string stem = p[..^4];
                    char last = Char.ToUpperInvariant(stem[^1]);
                    string other = stem[..^1] + (last == 'L' ? (Char.IsUpper(stem[^1]) ? "R" : "r") : (Char.IsUpper(stem[^1]) ? "L" : "l")) + p[^4..];
                    if (last == 'L' && set.Contains(other)) tracks.Add(new("dsp", new[] { p, paths.First(x => x.Equals(other, StringComparison.OrdinalIgnoreCase)) }));
                    else if (last == 'R' && set.Contains(other)) continue;
                    else tracks.Add(new("dsp", new[] { p }));
                }
            }
            return tracks;
        }

        public static IMusicStream Open(IFileSource disc, DiscTrack t) =>
            MusicFormats.Open(t.Format, t.Files.Select(disc.ReadFile).ToArray());

        // -primemusic hash <disc> <part>: every disc path containing <part> with its SHA-1 (to write an edition's file entries)
        static void Hash(string discPath, string part)
        {
            using IFileSource disc = DiscImage.Open(discPath);
            foreach (string p in disc.Paths.Where(p => p.Contains(part, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(disc.ReadFile(p))).ToLowerInvariant()}  {p}");
            }
        }

        static void Scan(string discPath, string folder)
        {
            using IFileSource disc = DiscImage.Open(discPath);
            Console.WriteLine($"{disc.GameId} {disc.Title}: {folder}Audio/");
            List<DiscTrack> tracks = Tracks(disc, folder);
            int bad = 0;
            Console.WriteLine($"{"track",-44} {"fmt",-4} ch {"sec",7} {"loop",-15} {"rms dB",7} {"peak",6} clips  seam hist");
            foreach (DiscTrack t in tracks)
            {
                try
                {
                    byte[][] files = t.Files.Select(disc.ReadFile).ToArray();
                    IMusicStream m = MusicFormats.Open(t.Format, files);
                    Stats s = Measure(m, t.Format == "dsp" ? files : null);
                    string loop = m.Loops ? $"{m.LoopStart / (double)m.SampleRate,6:0.0}-{m.LoopEnd / (double)m.SampleRate,6:0.0}" : "no";
                    Console.WriteLine($"{t.Name,-44} {t.Format,-4} {m.Channels,2} {m.SampleCount / (double)m.SampleRate,7:0.0} {loop,-15} {s.RmsDb,7:0.0} {s.Peak,6} {s.Clips,5}  {(m.Loops ? s.Seam.ToString("0.00") : "-"),5} {s.Hist}");
                }
                catch (Exception ex)
                {
                    bad++;
                    Console.WriteLine($"{t.Name,-44} {t.Format,-4} FAILED: {ex.Message}");
                }
            }
            Console.WriteLine($"{tracks.Count} tracks, {bad} failed");
        }

        // The level the songs are brought to: Echoes' arena music as the arenas play it next to MPH's own music (ArenaMusic:
        // each stream at its object volume / 127), the mean of the seven Selection tracks' RMS + 20 log10(volume / 127)
        // from the 2026-10-14 scan of G2ME01: -17.9, -19.6, -20.7, -20.1, -19.8, -17.5, -18.9 dB
        const double TargetDb = -19.2;

        static void Recipes(string discPath, string folder, string pickPath, string recipesDir, string game, string discId)
        {
            using IFileSource disc = DiscImage.Open(discPath);
            List<DiscTrack> tracks = Tracks(disc, folder);
            string outDir = Path.Combine(recipesDir, TrackCatalog.Kind);
            Directory.CreateDirectory(outDir);
            static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
            int n = 0;
            foreach (string line in File.ReadAllLines(pickPath).Where(l => l.Trim().Length > 0))
            {
                string[] c = line.Split('\t');
                string name = c[0], first = c[1], title = c[2], group = c[3], role = c[4];
                List<DiscTrack> from = first.Contains('/') ? Tracks(disc, first[..first.IndexOf("Audio/", StringComparison.OrdinalIgnoreCase)]) : tracks;
                DiscTrack t = from.First(x => Path.GetFileName(x.Files[0]).Equals(Path.GetFileName(first), StringComparison.OrdinalIgnoreCase));
                byte[][] files = t.Files.Select(disc.ReadFile).ToArray();
                IMusicStream m = MusicFormats.Open(t.Format, files);
                Stats s = Measure(m, null);
                double gain = Math.Min(Math.Pow(10, (TargetDb - s.RmsDb) / 20), 32767.0 / Math.Max(1, s.Peak));
                var lines = new List<string>
                {
                    "{",
                    $"  \"game\": {Q(game)},", $"  \"discId\": {Q(discId)},", $"  \"format\": {Q(t.Format)},",
                    $"  \"title\": {Q(title)},", $"  \"group\": {Q(group)},", $"  \"role\": {Q(role)},",
                    "  \"files\": [",
                };
                for (int i = 0; i < t.Files.Length; i++)
                {
                    string sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(files[i])).ToLowerInvariant();
                    lines.Add($"    {{ \"name\": {Q(t.Files[i])}, \"sha1\": {Q(sha1)} }}" + (i < t.Files.Length - 1 ? "," : ""));
                }
                lines.Add("  ],");
                lines.Add($"  \"gain\": {gain.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
                lines.Add("}");
                File.WriteAllText(Path.Combine(outDir, name + ".json"), String.Join("\n", lines) + "\n");
                Console.WriteLine($"{name,-40} {t.Format,-4} {m.SampleCount / (double)m.SampleRate,6:0.0} s  {s.RmsDb,6:0.0} dB  gain {gain:0.000}  {title}");
                n++;
            }
            Console.WriteLine($"{n} recipes -> {outDir}");
        }

        // the in-app import of every song of a game from one disc (an edition's disc too: Trilogy for mp1/mp2,
        // Corruption for mp3), each made song opened and decoded the way the app plays it, then one deleted again
        static void ImportTest(string game, string discPath, string recipesDir, string outRoot)
        {
            RecipeStore store = RecipeStore.FromDirectory(recipesDir);
            List<ImportRecipe> songs = store.ForGame(game).Where(r => r.Kind == TrackCatalog.Kind).ToList();
            if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
            var svc = new ImportService(outRoot, store);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ImportReport rep = svc.Import(game, discPath, null, default, songs.Select(r => r.Id).ToList());
            Console.WriteLine($"{game} from {rep.DiscId}: {rep.Items.Count(i => i.Status == ItemImportStatus.Complete)}/{songs.Count} songs in {clock.Elapsed.TotalSeconds:0.0} s");
            int played = 0;
            foreach (ImportItemInfo i in rep.Items)
            {
                if (i.Status != ItemImportStatus.Complete)
                {
                    Console.WriteLine($"  {i.Status,-14} {i.Id}: {i.Message}");
                    continue;
                }
                Track t = TrackCatalog.FromRecipe(songs.First(r => r.Id == i.Id));
                IMusicStream m = TrackCatalog.Open(outRoot, t);
                IMusicReader r = m.OpenReader();
                var buf = new short[4096 * m.Channels];
                int frames = 0;
                while (frames < m.SampleRate * 5 && !r.Finished) frames += r.Read(buf, 4096);
                if (frames > 0) played++;
            }
            Console.WriteLine($"  {played} played back from {outRoot}");
            ImportItemInfo? first = rep.Items.FirstOrDefault(i => i.Status == ItemImportStatus.Complete);
            if (first != null)
            {
                svc.Remove(game, new[] { first.Id });
                Console.WriteLine($"  delete {first.Id}: folder {(Directory.Exists(TrackCatalog.Folder(outRoot, first.Name)) ? "STILL THERE" : "gone")}");
            }
        }

        readonly record struct Stats(double RmsDb, int Peak, int Clips, double Seam, string Hist);

        // the whole track once, then across the loop seam. Seam = the step at the seam over the track's 99.9th
        // percentile step (under 1 for a seamless loop; a click or a misplaced loop point shows as many times that).
        // Hist (DSP files): whether the decoder history our reader keeps for the loop is the one the file's header
        // stores (= the hardware's).
        static Stats Measure(IMusicStream m, byte[][]? dspFiles)
        {
            IMusicReader r = m.OpenReader();
            int ch = m.Channels;
            var buf = new short[4096 * ch];
            double sum = 0;
            long n = 0;
            int peak = 0, clips = 0;
            var last = new short[ch];
            var hist = new long[65536];
            long stepN = 0;
            var atLoop = new short[2 * ch];   // the two samples before the loop start, per channel
            long total = m.Loops ? m.LoopEnd : m.SampleCount;
            while (n < total && !r.Finished)
            {
                int got = r.Read(buf, (int)Math.Min(4096, total - n));
                if (got == 0) break;
                for (int f = 0; f < got; f++)
                {
                    long at = n + f;
                    for (int c = 0; c < ch; c++)
                    {
                        int v = buf[f * ch + c];
                        sum += (double)v * v;
                        int a = Math.Abs(v);
                        if (a > peak) peak = a;
                        if (a >= 32767) clips++;
                        if (at > 0) { hist[Math.Abs(v - last[c])]++; stepN++; }
                        last[c] = (short)v;
                        if (at == m.LoopStart - 1) atLoop[c] = (short)v;
                        if (at == m.LoopStart - 2) atLoop[ch + c] = (short)v;
                    }
                }
                n += got;
            }
            double seam = 0;
            if (m.Loops)
            {
                long k = 0, want = stepN - stepN / 1000;
                int p999 = 0;
                for (int i = 0; i < hist.Length; i++) { k += hist[i]; if (k >= want) { p999 = i; break; } }
                // up to the loop's last sample, then the jump
                IMusicReader s = m.OpenReader();
                var chunk = new short[4096 * ch];
                long pos = 0;
                while (pos < m.LoopEnd - 1)
                {
                    int got = s.Read(chunk, (int)Math.Min(4096, m.LoopEnd - 1 - pos));
                    if (got == 0) break;
                    pos += got;
                }
                var one = new short[ch];
                var after = new short[ch];
                s.Read(one, 1);
                s.Read(after, 1);
                int step = 0;
                for (int c = 0; c < ch; c++) step = Math.Max(step, Math.Abs(after[c] - one[c]));
                seam = step / (double)Math.Max(1, p999);
            }
            string hcheck = "-";
            if (m.Loops && dspFiles != null)
            {
                hcheck = "ok";
                for (int c = 0; c < dspFiles.Length; c++)
                {
                    short h1 = (short)(dspFiles[c][0x46] << 8 | dspFiles[c][0x47]), h2 = (short)(dspFiles[c][0x48] << 8 | dspFiles[c][0x49]);
                    bool start0 = m.LoopStart == 0;
                    if (start0 ? h1 != 0 || h2 != 0 : h1 != atLoop[c] || h2 != atLoop[ch + c]) hcheck = $"diff({h1},{h2} vs {atLoop[c]},{atLoop[ch + c]})";
                }
            }
            double rms = Math.Sqrt(sum / Math.Max(1, n * ch));
            return new Stats(20 * Math.Log10(Math.Max(1, rms) / 32768), peak, clips, seam, hcheck);
        }

        static void Wav(string discPath, string file, string outPath, float seconds, float start)
        {
            using IFileSource disc = DiscImage.Open(discPath);
            DiscTrack t = Tracks(disc, "").Concat(new[] { "MP1/", "MP2/", "MP3/", "fe/" }.SelectMany(f => Tracks(disc, f)))
                .First(x => x.Files.Any(f => f.EndsWith(file, StringComparison.OrdinalIgnoreCase)));
            IMusicStream m = Open(disc, t);
            IMusicReader r = m.OpenReader();
            int ch = m.Channels;
            var skip = new short[4096 * ch];
            long skipFrames = (long)(start * m.SampleRate);
            while (skipFrames > 0 && !r.Finished) skipFrames -= r.Read(skip, (int)Math.Min(4096, skipFrames));
            int frames = (int)(seconds * m.SampleRate);
            var pcm = new short[frames * ch];
            int done = 0;
            while (done < frames && !r.Finished)
            {
                int got = r.Read(pcm.AsSpan(done * ch), Math.Min(4096, frames - done));
                if (got == 0) break;
                done += got;
            }
            using var w = new BinaryWriter(File.Create(outPath));
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + done * ch * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)ch);
            w.Write(m.SampleRate);
            w.Write(m.SampleRate * ch * 2);
            w.Write((short)(ch * 2));
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(done * ch * 2);
            for (int i = 0; i < done * ch; i++) w.Write(pcm[i]);
            Console.WriteLine($"{t.Name} ({t.Format}, {ch} ch, {m.SampleRate} Hz, loop {(m.Loops ? $"{m.LoopStart}-{m.LoopEnd}" : "none")}): {done / (double)m.SampleRate:0.0} s -> {outPath}");
        }
    }
}
