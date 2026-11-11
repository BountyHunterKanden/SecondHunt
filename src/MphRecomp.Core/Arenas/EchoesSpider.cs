using System;
using System.Collections.Generic;
using System.IO;
using MphRecomp.Import;
using MphRecomp.Import.Retro;

namespace MphRecomp.Arenas
{
    // The spider ball's Echoes sound (docs/SPIDER_BALL.md S2): the morph ball group's loop sa2_b_spidlach_lp (game id
    // 0x2641 in AudioGrp.pak's ATBL, AGSC 51674564 GRPSamusMorphball_MP), which Echoes' CMorphBall starts and stops with
    // the Spider state. Rendered at import into hd/spider_loop.wav for the arenas with track (Sidehopper Station, Spider
    // Complex); played from each player's attached state (MphRecomp.App CampaignRenderer.SpiderFx.cs).
    public static class EchoesSpider
    {
        public const string LoopFile = "spider_loop.wav";
        const uint SoundLookup = 0xb7c1b3a7, SoundGroup = 0x51674564, LoopId = 0x2641;

        // the arenas whose collision has spider track (the -spiderball check: 14 / 638 triangles)
        public static bool HasTrack(string key) => key is "M01_SidehopperStation" or "M05_SpiderComplex";

        // the loop's WAV beside the HD look; nothing (and any old one gone) when the arena has no track or no audio pak
        internal static void ImportSound(Pak? audio, string hdDir, List<string> notes, bool any)
        {
            string file = Path.Combine(hdDir, LoopFile);
            if (File.Exists(file)) File.Delete(file);
            if (!any) return;
            if (audio == null || !audio.Contains(SoundLookup) || !audio.Contains(SoundGroup))
            {
                notes.Add("spider ball sound: no AudioGrp.pak (or its morph ball group); none written");
                return;
            }
            short[] lookup = MusyX.Lookup(audio.Get(SoundLookup));
            Agsc g = Agsc.Parse(audio.Get(SoundGroup));
            int m = MusyX.Translate(lookup, LoopId);
            if (m < 0 || g.Render((ushort)m) is not Agsc.Rendered x || x.Loop.Length == 0)
            {
                notes.Add($"spider ball sound: loop ({LoopId:x4}, sfx {m}) not rendered");
                return;
            }
            MusyX.WriteWav(file, x.Loop, x.Rate);
            notes.Add($"spider ball sound: loop (sfx {m}, {x.Loop.Length / (float)x.Rate:0.00} s)");
        }

        // Echoes' look (docs/SPIDER_BALL.md S1): the magnet beads (PART 22b005a1 SpiderBallMagnetEffect) and the arcs
        // (SWHC 4735d6e1 SpiderElectric), both in TestAnim.pak; written to hd/spider_fx/<id>.<kind> + <txtr>.png for
        // RetroParticles.Load(echoes: true)
        public const string FxDir = "spider_fx";
        public const uint BeadsId = 0x22b005a1, ArcsId = 0x4735d6e1;
        public static readonly uint[] FxRoots = { BeadsId, ArcsId };

        internal static void ImportFx(Pak? pak, string hdDir, List<string> notes, bool any)
        {
            string dir = Path.Combine(hdDir, FxDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (!any) return;
            if (pak == null)
            {
                notes.Add("spider ball look: no TestAnim.pak; the drawn stand-in is used");
                return;
            }
            Directory.CreateDirectory(dir);
            FxWalk(pak, FxRoots, dir, notes);
        }

        // the effects in `roots` and everything they pull in (PART/ELSC/SWHC, their textures as PNG) into `dir`, or a dry
        // run when `dir` is null; returns the files found
        public static int FxWalk(Pak pak, IEnumerable<uint> roots, string? dir, List<string> notes)
        {
            var done = new HashSet<uint>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<uint>(roots);
            var missing = new List<uint>();
            var bad = new List<string>();
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id)) continue;
                if (!pak.Contains(id))
                {
                    missing.Add(id);
                    continue;
                }
                string type = pak.TypeOf(id);
                if (type is not ("PART" or "ELSC" or "SWHC")) continue;
                byte[] b = pak.Get(id);
                IEnumerable<(string Type, uint Id)> deps;
                try
                {
                    deps = type switch
                    {
                        "PART" => Part.Load(b, echoes: true).Dependencies(),
                        "ELSC" => Elsc.Load(b).Dependencies(),
                        _ => Part.Swoosh.Load(b).Texture is uint t and not 0 and not UInt32.MaxValue ? new[] { ("TXTR", t) } : Array.Empty<(string, uint)>(),
                    };
                }
                catch (Exception e)
                {
                    bad.Add($"{type} {id:x8}: {e.Message}");
                    continue;
                }
                if (dir != null) File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.{type.ToLowerInvariant()}"), b);
                foreach ((string t, uint dep) in deps)
                {
                    if (dep == 0 || dep == UInt32.MaxValue) continue;
                    if (t == "TXTR") textures.Add(dep);
                    else if (t is "PART" or "ELSC" or "SWHC") todo.Enqueue(dep);
                }
            }
            int pngs = 0;
            foreach (uint t in textures)
            {
                if (!pak.Contains(t))
                {
                    missing.Add(t);
                    continue;
                }
                byte[] tb = pak.Get(t);
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                if (rgba == null)
                {
                    bad.Add($"TXTR {t:x8} format {Txtr.Header(tb).Format}");
                    continue;
                }
                if (dir != null) PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
                pngs++;
            }
            int files = done.Count - missing.Count - bad.Count;
            notes.Add($"spider ball look: {files} effect files, {pngs} textures"
                + (missing.Count > 0 ? $"; not in the pak: {string.Join(", ", missing.ConvertAll(m => m.ToString("x8")))}" : "")
                + (bad.Count > 0 ? $"; unread: {string.Join(", ", bad)}" : ""));
            return files;
        }
    }
}
