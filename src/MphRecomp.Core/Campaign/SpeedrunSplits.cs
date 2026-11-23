using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using MphRead;
using MphRead.Formats;

namespace MphRecomp.Campaign
{
    // The speedrun timer's runs, splits and bests (owner 2026-11-16; RECOMP SETTINGS > CAMPAIGN > SPEEDRUN). One small
    // sidecar per save slot, speedrun<slot>.json beside campaign<slot>.json, so a death's reload, a quit or a game over
    // never rewinds the clock (the save file does rewind; this file does not). A NEW game in the slot starts a new run
    // and keeps the slot's personal best and best segments. Display only: nothing in the game reads any of it.
    //
    // Timing follows speedrun.com/mph: the run starts on selecting an empty file and ends when the credits appear
    // (CampaignEnd.Ending). Real time (RTA) is what the leaderboard ranks; the in-game time (IGT) here is load-removed
    // game time: the game's own 60 Hz steps (door loads left out), the ship's screens and movies at their frame rate,
    // nothing while the game is paused (pause menu, Select's map, game over's menu). It counts the same on any device.
    public sealed class SpeedrunSplit
    {
        public string Name { get; set; } = "";
        public double Igt { get; set; }
        public double Rta { get; set; }
    }

    public sealed class SpeedrunRun
    {
        public double Igt { get; set; }
        public double Rta { get; set; }
        public bool Finished { get; set; }
        public string Started { get; set; } = "";
        public List<SpeedrunSplit> Splits { get; set; } = new();
        // splits already taken (a death's reload never splits twice): planets first landed (bit per Planet), boss flags seen
        public int Landed { get; set; }
        public int Bosses { get; set; }

        public SpeedrunRun Copy()
        {
            var copy = (SpeedrunRun)MemberwiseClone();
            copy.Splits = new List<SpeedrunSplit>();
            foreach (SpeedrunSplit s in Splits) copy.Splits.Add(new SpeedrunSplit { Name = s.Name, Igt = s.Igt, Rta = s.Rta });
            return copy;
        }

        public SpeedrunSplit? Find(string name)
        {
            foreach (SpeedrunSplit s in Splits) if (s.Name == name) return s;
            return null;
        }
    }

    // a split just taken: its name, its time, the difference to the personal best's same split (null: no best has it),
    // and whether its segment was the slot's best yet
    public readonly struct SpeedrunMark
    {
        public readonly string Name;
        public readonly double Igt;
        public readonly double? Delta;
        public readonly bool BestSegment;

        public SpeedrunMark(string name, double igt, double? delta, bool bestSegment)
        {
            Name = name;
            Igt = igt;
            Delta = delta;
            BestSegment = bestSegment;
        }
    }

    public sealed class SpeedrunFile
    {
        public int Version { get; set; } = 1;
        public SpeedrunRun? Current { get; set; }
        public SpeedrunRun? Best { get; set; }
        // split name -> the fastest IGT segment (from the split before it) any run in this slot has done
        public Dictionary<string, double> BestSegments { get; set; } = new();

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        public static string PathFor(string savesDir, int slot) => Path.Combine(savesDir, $"speedrun{slot}.json");

        // a missing or unreadable file is a slot with no runs yet
        public static SpeedrunFile Load(string path)
        {
            try
            {
                if (File.Exists(path)) return FromJson(File.ReadAllText(path));
            }
            catch (Exception)
            {
            }
            return new SpeedrunFile();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);
        public static SpeedrunFile FromJson(string json) => JsonSerializer.Deserialize<SpeedrunFile>(json, Options) ?? new SpeedrunFile();

        public SpeedrunRun Start(string startedIso)
        {
            Current = new SpeedrunRun { Started = startedIso };
            return Current;
        }

        public SpeedrunMark Split(string name)
        {
            SpeedrunRun run = Current!;
            double previous = run.Splits.Count > 0 ? run.Splits[^1].Igt : 0;
            run.Splits.Add(new SpeedrunSplit { Name = name, Igt = run.Igt, Rta = run.Rta });
            double segment = run.Igt - previous;
            bool bestSegment = !BestSegments.TryGetValue(name, out double best) || segment < best;
            if (bestSegment) BestSegments[name] = segment;
            double? delta = Best?.Find(name) is SpeedrunSplit pb ? run.Igt - pb.Igt : null;
            return new SpeedrunMark(name, run.Igt, delta, bestSegment);
        }

        // the credits: the last split, the run frozen, and a new personal best when it beat the old one (by IGT)
        public SpeedrunMark Finish()
        {
            SpeedrunMark mark = Split(SpeedrunSplits.CreditsSplit);
            SpeedrunRun run = Current!;
            run.Finished = true;
            if (Best == null || run.Igt < Best.Igt) Best = run.Copy();
            return mark;
        }
    }

    public static class SpeedrunSplits
    {
        public const string CreditsSplit = "CREDITS";
        public const string GoreaSplit = "GOREA";

        public static string Short(Planet planet) => planet switch
        {
            Planet.Alinos => "ALINOS",
            Planet.CelestialArchives => "ARCHIVES",
            Planet.VesperDefenseOutpost => "VDO",
            Planet.Arcterra => "ARCTERRA",
            _ => "OUBLIETTE"
        };

        public static string LandingSplit(Planet planet) => "LAND " + Short(planet);

        // the story's boss flags (2 bits per area, Unit<planet>B<area>: killed, then done once back at the ship;
        // ArtifactEntity sets the kill bit on the octolith's pickup) -> the new splits, each area's once:
        // "<PLANET> <area> OCTOLITH", then GOREA
        public static void BossSplits(SpeedrunRun run, int bossFlags, List<string> into)
        {
            int seen = run.Bosses;
            foreach (PlanetInfo planet in ShipNavigation.Planets)
            {
                for (int i = 0; i < planet.AreaCount; i++)
                {
                    int area = planet.FirstArea + i;
                    if (area > 7) continue; // Oubliette's one area is Gorea's (below)
                    int bits = 3 << (2 * area);
                    if ((bossFlags & bits) != 0 && (seen & bits) == 0)
                    {
                        seen |= bits;
                        into.Add($"{Short(planet.Id)} {i + 1} OCTOLITH");
                    }
                }
            }
            int gorea = (int)BossFlags.Gorea1Kill;
            if ((bossFlags & gorea) != 0 && (seen & gorea) == 0)
            {
                seen |= gorea;
                into.Add(GoreaSplit);
            }
            run.Bosses = seen;
        }

        // a planet's first landing in this run: true once
        public static bool Land(SpeedrunRun run, Planet planet)
        {
            int bit = 1 << (int)planet;
            if ((run.Landed & bit) != 0) return false;
            run.Landed |= bit;
            return true;
        }

        // m:ss.t (h:mm:ss.t from an hour), tenths cut, not rounded (a timer never shows a time it hasn't reached)
        public static string Clock(double seconds)
        {
            long tenths = (long)Math.Floor(Math.Max(0, seconds) * 10 + 1e-6);
            long t = tenths % 10, s = tenths / 10 % 60, m = tenths / 600 % 60, h = tenths / 36000;
            return h > 0
                ? String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}", h, m, s, t)
                : String.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", m, s, t);
        }

        public static string Delta(double seconds) => (seconds < 0 ? "-" : "+") + Clock(Math.Abs(seconds));

        // ROM-free checks (MphRead.Tools -speedruntest): null = all pass, else the first failure
        public static string? SelfTest()
        {
            var file = new SpeedrunFile();
            SpeedrunRun run = file.Start("2026-11-16T00:00:00Z");
            if (!Land(run, Planet.CelestialArchives) || Land(run, Planet.CelestialArchives)) return "landing splits once";
            run.Igt = 90;
            file.Split(LandingSplit(Planet.CelestialArchives));
            var names = new List<string>();
            BossSplits(run, (int)BossFlags.Unit2B1Kill, names);
            if (names.Count != 1 || names[0] != "ARCHIVES 1 OCTOLITH") return "archives octolith: " + String.Join(",", names);
            names.Clear();
            BossSplits(run, (int)BossFlags.Unit2B1Done, names); // killed -> done at the ship, or a reload: no second split
            if (names.Count != 0) return "octolith twice";
            BossSplits(run, (int)(BossFlags.Unit2B1Done | BossFlags.Unit3B2Kill | BossFlags.Gorea1Kill), names);
            if (String.Join(",", names) != "VDO 2 OCTOLITH,GOREA") return "vdo + gorea: " + String.Join(",", names);
            run.Igt = 3723.45;
            file.Finish();
            if (file.Best == null || file.Best.Igt != 3723.45 || !run.Finished) return "first finish is the best";
            if (Clock(3723.45) != "1:02:03.4" || Clock(5.06) != "0:05.0" || Delta(-4.1) != "-0:04.1") return "clock text";
            SpeedrunRun second = file.Start("2026-11-17T00:00:00Z");
            second.Igt = 80;
            SpeedrunMark mark = file.Split(LandingSplit(Planet.CelestialArchives));
            if (mark.Delta != -10 || !mark.BestSegment) return "delta vs best / best segment";
            second.Igt = 4000;
            file.Finish();
            if (file.Best.Igt != 3723.45 || file.Best.Splits.Count != 2) return "a slower finish keeps the best";
            SpeedrunFile back = FromJsonRoundTrip(file);
            if (back.BestSegments["LAND ARCHIVES"] != 80 || back.Best?.Splits.Count != 2 || back.Current?.Finished != true) return "json round trip";
            return null;
        }

        private static SpeedrunFile FromJsonRoundTrip(SpeedrunFile file) => SpeedrunFile.FromJson(file.ToJson());
    }
}
