using System;
using System.IO;
using MphRecomp.Import.Brawl;
using MphRecomp.Import.Retro;

namespace MphRead
{
    // -brawlsndcheck <smashbros_sound.brsar> <reference dir>: the clean-room sound archive reader (docs/cleanroom/
    // F_brawl_sound_spec.md, Core Import/Brawl/BrawlSoundArchive.cs) against reference decodes made separately (one
    // <sound name>.wav each, mono PCM16): rate, sample count and every sample equal, plus the spec's check values
    internal static class BrawlSoundCheck
    {
        static readonly (string Name, int Rate, int Volume, bool Loops, int Count)[] Checks =
        {
            ("snd_se_szerosuit_smash_L01", 15000, 46, false, 10630),
            ("snd_se_szerosuit_012", 15000, 46, true, 8502),
            ("snd_se_szerosuit_013", 15000, 46, false, 13403),
            ("snd_se_common_Electric_hit_s", 18000, 53, false, 6197),
            ("snd_se_common_Electric_hit_m", 18000, 53, false, 8023),
            ("snd_se_common_Electric_hit_l", 18000, 53, false, 12856),
            ("snd_se_common_Electric_hit_ll", 18000, 53, false, 22709),
        };

        public static void Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: -brawlsndcheck <smashbros_sound.brsar> <reference dir>");
                return;
            }
            using FileStream s = File.OpenRead(args[1]);
            BrawlSoundArchive archive = BrawlSoundArchive.Open(s);
            int fails = 0;
            foreach ((string name, int rate, int volume, bool loops, int count) in Checks)
            {
                BrawlSoundSample? got = archive.Decode(name);
                short[]? want = MusyX.ReadWav(Path.Combine(args[2], name + ".wav"), out int wantRate);
                string why = "";
                if (got == null) why = "not found";
                else if (want == null) why = "no reference";
                else if (got.Channels.Length != 1) why = $"{got.Channels.Length} channels";
                else if (got.Rate != rate || got.Rate != wantRate) why = $"rate {got.Rate} (spec {rate}, reference {wantRate})";
                else if (got.Volume != volume || got.Loops != loops || loops && got.LoopStart != 0) why = $"volume {got.Volume} loops {got.Loops} @ {got.LoopStart}";
                else if (got.Channels[0].Length != count || want.Length != count) why = $"{got.Channels[0].Length} samples (spec {count}, reference {want.Length})";
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (got.Channels[0][i] != want[i])
                        {
                            why = $"sample {i}: {got.Channels[0][i]} vs {want[i]}";
                            break;
                        }
                    }
                }
                if (why != "") fails++;
                Console.WriteLine($"{(why == "" ? "ok  " : "FAIL")} {name}{(why == "" ? $": {count} samples equal" : ": " + why)}");
            }
            Console.WriteLine(archive.Find("no_such_sound") == -1 && archive.Decode("no_such_sound") == null ? "ok   unknown name -> -1 / null" : "FAIL unknown name");
            Console.WriteLine(fails == 0 ? "PASS" : $"FAIL ({fails})");
        }
    }
}
