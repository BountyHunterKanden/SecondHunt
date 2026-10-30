using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Import.Effects;
using MphRecomp.Import;
using MphRecomp.Import.Disc;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // -dronesoundlist [--mp1 disc.iso] [--out dir]
    // Renders every sfx of Prime 1's "Drones" sound group (AudioGrp.pak AGSC 98b283bf) offline, one WAV each
    // (sfx_<musyx id>.wav, plus _loop.wav for a looping one), and prints a table: for picking the Sentry Drone's own sounds.
    public static class DroneSoundList
    {
        public static void Run(string[] args)
        {
            string Arg(string n, string d) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
            string repo = Directory.GetCurrentDirectory();
            string mp1 = Arg("--mp1", Path.Combine(repo, "brawl_extract", "prime", "mp1.iso"));
            string outDir = Arg("--out", Path.Combine(repo, "out", "dronesounds"));
            Directory.CreateDirectory(outDir);
            if (Array.IndexOf(args, "--events") >= 0) { Events(mp1, outDir); return; }
            using IFileSource src = DiscImage.Open(mp1);
            var audio = new Pak(src.ReadFile("AudioGrp.pak"), "AudioGrp.pak");
            short[] lookup = MusyX.Lookup(audio.Get(0xb7c1b3a7));
            Agsc drones = Agsc.Parse(audio.Get(0x98b283bf));
            Console.WriteLine($"group {drones.Group}: {drones.Sounds.Count} sfx, {drones.Samples.Count} samples");
            for (int id = 0; id < lookup.Length; id++)
            {
                int m = lookup[id];
                if (m >= 0 && drones.Sounds.ContainsKey((ushort)m)) Console.WriteLine($"  sound {id} -> musyx {m}");
            }
            if (Array.IndexOf(args, "--macro") >= 0)
            {
                foreach (int mm in new[] { 231, 232 })
                {
                    var sf = drones.Sounds[(ushort)mm];
                    byte[] cmds = drones.Macros[sf.Macro];
                    Console.WriteLine($"sfx {mm}: macro {sf.Macro}, key {sf.Key}, vel {sf.Velocity}, voices {sf.MaxVoices}");
                    for (int c = 0; c + 8 <= cmds.Length; c += 8) Console.WriteLine("  " + BitConverter.ToString(cmds, c, 8));
                }
                return;
            }
            foreach (ushort m in drones.Sounds.Keys.OrderBy(x => x))
            {
                Agsc.Rendered? r = drones.Render(m);
                if (r == null || r.Pcm.Length == 0) { Console.WriteLine($"musyx {m}: no render"); continue; }
                MusyX.WriteWav(Path.Combine(outDir, $"sfx_{m}.wav"), r.Pcm, r.Rate);
                if (r.Loop.Length > 0) MusyX.WriteWav(Path.Combine(outDir, $"sfx_{m}_loop.wav"), r.Loop, r.Rate);
                Console.WriteLine($"musyx {m}: {r.Pcm.Length / (float)r.Rate:0.00} s @ {r.Rate} Hz, loop {r.Loop.Length / (float)r.Rate:0.00} s, key {drones.Sounds[m].Key}, peak {r.Pcm.Max(x => Math.Abs((int)x))}");
            }
        }
    
        // every sound event of every clip of the Alpha drone's animation set (clip, time, game sound id)
        static void Events(string mp1, string outDir)
        {
            using IFileSource src = DiscImage.Open(mp1);
            var paks = new[] { "Metroid3.pak", "AudioGrp.pak" }.Select(n => new Pak(src.ReadFile(n), n)).ToList();
            Func<uint, byte[]> res = id => paks.First(p => p.Contains(id)).Get(id);
            foreach ((uint ancs, string who) in new[] { (0x5e058399u, "AlphaDrone") })
            {
                var set = Ancs.Parse(res(ancs)).Set!;
                var evs = new List<BodyFx.Event>();
                var snd = new List<BodyFx.Sound>();
                foreach ((string clip, Ancs.MetaAnim meta) in set.Anims)
                {
                    uint anim = Ancs.Prims(meta).First().AnimId;
                    uint evnt = set.Resources.FirstOrDefault(x => x.Anim == anim).Evnt;
                    if (evnt == 0 || evnt == UInt32.MaxValue) evnt = Be.U32(res(anim), 8);
                    if (evnt == 0 || evnt == UInt32.MaxValue) continue;
                    BodyFx.ReadEvnt(res(evnt), clip, evs, snd);
                }
                foreach (BodyFx.Sound s in snd) Console.WriteLine($"{who} {s.Clip} t={s.Time:0.00} sound {s.Id} {s.Name}");
            }
        }
    }
}
