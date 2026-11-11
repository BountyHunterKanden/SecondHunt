using System;
using System.IO;
using System.Linq;
using MphRecomp.Mods;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // MOD_API §10 phase 1: a gameplay mod that doubles damage changes a bot match, the match is recorded with the mod in
    // its header (format 75), and plays back exact with the mod loaded again; without the mod it's refused up front.
    // Run by -modapitest (needs the desktop extraction; skipped without it).
    internal static partial class ReplayTest
    {
        const string DoubleLua = @"
mod.filter('damage', function(t)
  t.damage = t.damage * 2
  mod.save('hits', mod.load('hits', 0) + 1)
end)
";

        internal static void ModsPhase1(Action<string, bool> check)
        {
            if (!Setup())
            {
                Console.WriteLine("  [SKIP] mod replay round trip: no desktop extraction here");
                return;
            }
            string data = Path.Combine(Path.GetTempPath(), "mphrecomp_modreplay_" + Guid.NewGuid().ToString("N"));
            try
            {
                string modDir = Path.Combine(data, "mods", "double");
                Directory.CreateDirectory(modDir);
                File.WriteAllText(Path.Combine(modDir, "mod.json"),
                    "{ \"Id\": \"double\", \"Name\": \"Double damage\", \"Version\": \"1.0.0\", \"Kind\": \"gameplay\", \"Entry\": \"main.lua\" }");
                File.WriteAllText(Path.Combine(modDir, "main.lua"), DoubleLua);
                Options so = Parse(new[] { "mode=Battle", "arena=Combat Hall", "hunter=Samus", "seed=101", "time=60" }, new Options());
                RoomMetadata arena = MatchArenas.Find(so.Arena!)!;
                MatchSettings settings = Settings(so, ParseMode(so.Mode), arena);

                // the same match without the mod, to show the mod changed it
                Menu.PreviousSaveSlot = -1;
                Run plain = Record(settings, so.Seed, View.NoDraw, types: false);

                Menu.PreviousSaveSlot = -1;
                LiveMods.Attach(data, null, match: true);
                var header = LiveMods.StartMatch(null, out ulong seed);
                check("mod replay: the gameplay mod is in the header", header.Count == 1 && header[0].Id == "double"
                    && header[0].Sha.Length > 0);
                Run rec = Record(settings, so.Seed, View.NoDraw, types: false);
                double hits = LiveMods.PlayHost?.Get("double")?.Data.TryGetValue("hits", out var h) == true ? h.GetDouble() : 0;
                LiveMods.Detach();
                check($"mod replay: the damage filter ran ({hits} hits)", rec.Error == null && hits > 0);
                check("mod replay: doubled damage changed the match",
                    plain.File != null && rec.File != null && (plain.Kills != rec.Kills || plain.Deaths != rec.Deaths
                        || plain.File.Steps.Last().Checksum != rec.File.Steps.Last().Checksum));
                if (rec.File == null)
                {
                    return;
                }
                rec.File.Mods.AddRange(header);
                rec.File.ModSeed = seed;
                // through the file format and back
                string path = Path.Combine(data, "double" + ReplayLibrary.Extension);
                rec.File.Save(path);
                ReplayFile file = ReplayFile.Load(path);
                check("mod replay: format 75 keeps the mods", file.Mods.Count == 1 && file.Mods[0].Id == "double"
                    && file.ModSeed == seed && file.Mods[0].Sha == header[0].Sha);

                // no mod on this device: refused before it starts
                Run refused = Replay(file, View.NoDraw, false, null);
                check("mod replay: refused without the mod (\"missing mod double\")", refused.Error?.Contains("missing mod double") == true);

                // the mod back: plays exact
                Menu.PreviousSaveSlot = -1;
                LiveMods.Attach(data, "replay");
                LiveMods.StartMatch(file, out _);
                Run play = Replay(file, View.NoDraw, false, rec);
                LiveMods.Detach();
                check($"mod replay: plays back exact with the mod ({play.Verdict()})", play.Exact);

                // the mod changed after the match: refused
                File.AppendAllText(Path.Combine(modDir, "main.lua"), "\n-- changed\n");
                LiveMods.Attach(data, "replay");
                Run changed = Replay(file, View.NoDraw, false, null);
                LiveMods.Detach();
                check($"mod replay: refused when the mod changed ({changed.Error})", changed.Error?.Contains("changed") == true);
            }
            finally
            {
                LiveMods.Detach();
                try { Directory.Delete(data, recursive: true); } catch { }
            }
        }
    }
}
