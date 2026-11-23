using System.Collections.Generic;
using MphRecomp.Config;

namespace MphRecomp.Frontend
{
    // The campaign's speedrun timer rows (owner 2026-11-16; CAMPAIGN > SPEEDRUN; App/Campaign/SpeedrunTimer.cs draws it).
    // Dev builds only for now, with the HUD editor that moves it.
    public sealed partial class RecompMenus
    {
        private static readonly string[] SpeedrunModes = { "off", "igt", "rta", "both" };

        private static string SpeedrunNext(string? mode, int step)
        {
            int i = System.Array.IndexOf(SpeedrunModes, mode ?? "off");
            return SpeedrunModes[((i < 0 ? 0 : i) + step % SpeedrunModes.Length + SpeedrunModes.Length) % SpeedrunModes.Length];
        }

        private List<RowModel> SpeedrunSettingRows()
        {
            if (RecompSettings.PublicBuild) return new List<RowModel>();
            return new List<RowModel>
            {
                new()
                {
                    Label = "speedrun\ntimer",
                    Description = "a run's clock in the campaign, from a new file to the credits. igt: game time, stops for the pause menu, the map and door loads. rta: real time, as speedrun.com ranks. both: igt with rta under it. move it in the hud editor.",
                    Value = () => (_settings.SpeedrunTimer ?? "off").ToUpperInvariant(),
                    Step = d => _settings.SpeedrunTimer = SpeedrunNext(_settings.SpeedrunTimer, d),
                },
                new()
                {
                    Label = "speedrun\nsplits",
                    Description = "on: each planet's first landing, each octolith, gorea and the credits show a split under the timer: green ahead of this file's best run, red behind, gold a best segment.",
                    Value = () => _settings.SpeedrunSplits ? "ON" : "OFF",
                    Step = _ => _settings.SpeedrunSplits = !_settings.SpeedrunSplits
                },
            };
        }
    }
}
